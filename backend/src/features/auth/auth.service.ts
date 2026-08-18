import jwt from 'jsonwebtoken';
import bcrypt from 'bcrypt';
import { queryOne } from '../../infrastructure/database/connection';
import { AppError } from '../../middleware/error.middleware';
import { config } from '../../config';
import { User } from '../../types';
import { validateAzureToken } from '../../utils/azureAuth';

type UserRow = User & { isActive: boolean; passwordHash: string | null };

export class AuthService {
  async login(email: string, password: string): Promise<{ user: User; token: string }> {
    const row = await queryOne<UserRow>(
      `SELECT id, email, display_name AS "displayName", role,
              azure_oid AS "azureOid", is_active AS "isActive",
              password_hash AS "passwordHash"
       FROM users WHERE email = $1`,
      [email]
    );

    if (!row) throw new AppError(401, 'Invalid email or password');
    if (!row.isActive) throw new AppError(403, 'Account is inactive. Contact your administrator.');
    if (!row.passwordHash) throw new AppError(401, 'Password login not configured for this account');

    const valid = await bcrypt.compare(password, row.passwordHash);
    if (!valid) throw new AppError(401, 'Invalid email or password');

    await queryOne(`UPDATE users SET last_login_at = NOW() WHERE id = $1`, [row.id]);

    const token = jwt.sign(
      { sub: row.id, email: row.email, name: row.displayName, role: row.role },
      config.jwt.secret,
      { expiresIn: config.jwt.expiresIn as jwt.SignOptions['expiresIn'] }
    );

    const { isActive: _, passwordHash: __, ...safeUser } = row;
    return { user: safeUser as User, token };
  }

  // ── Azure AD SSO ─────────────────────────────────────────────────────────────
  // Called by POST /auth/sso from both the Admin Portal redirect and the
  // existing "Sign in with Azure" button on the login page.
  //
  // The backend validates the Azure token (signature, issuer, audience, expiry)
  // before touching the database — the frontend is never trusted.
  //
  // User resolution order:
  //   1. Find by azure_oid (handles email changes in Azure AD)
  //   2. Upsert by email (links existing account on first SSO login, or creates new)
  async ssoLogin(azureToken: string): Promise<{ user: User; token: string }> {
    const claims = await validateAzureToken(azureToken);

    // Try to find an existing user by Azure OID first — this handles the case
    // where a user's email changes in Azure AD without losing their local record.
    let row = await queryOne<UserRow>(
      `SELECT id, email, display_name AS "displayName", role,
              azure_oid AS "azureOid", is_active AS "isActive"
       FROM users WHERE azure_oid = $1`,
      [claims.oid]
    );

    if (!row) {
      // Upsert by email: links an existing password-login account to Azure on
      // first SSO use, or creates a new Viewer account for first-time Azure users.
      row = await queryOne<UserRow>(
        `INSERT INTO users (id, email, display_name, role, azure_oid, is_active, last_login_at)
         VALUES (gen_random_uuid(), $1, $2, 'Viewer', $3, true, NOW())
         ON CONFLICT (email) DO UPDATE SET
           azure_oid    = EXCLUDED.azure_oid,
           display_name = EXCLUDED.display_name,
           last_login_at = NOW()
         RETURNING id, email, display_name AS "displayName", role,
                   azure_oid AS "azureOid", is_active AS "isActive"`,
        [claims.email, claims.name, claims.oid]
      );
    }

    if (!row) throw new AppError(500, 'Failed to process Azure AD user');
    if (!row.isActive) throw new AppError(403, 'Account is inactive. Contact your administrator.');

    await queryOne(
      `UPDATE users SET last_login_at = NOW(), azure_oid = $2 WHERE id = $1`,
      [row.id, claims.oid]
    );

    const token = jwt.sign(
      { sub: row.id, email: row.email, name: row.displayName, role: row.role, oid: claims.oid },
      config.jwt.secret,
      { expiresIn: config.jwt.expiresIn as jwt.SignOptions['expiresIn'] }
    );

    const { isActive: _, ...safeUser } = row;
    return { user: safeUser as User, token };
  }
}

export const authService = new AuthService();
