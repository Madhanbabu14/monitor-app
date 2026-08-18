import jwt from 'jsonwebtoken';
import jwksClient from 'jwks-rsa';
import { config } from '../config';
import { AppError } from '../middleware/error.middleware';
import { logger } from './logger';

// Shared JWKS client used by both auth.middleware and auth.service.
// cache:true ensures keys are only fetched once per key ID; rateLimit:true
// prevents hammering the Microsoft endpoint if many concurrent requests arrive.
const client = jwksClient({
  jwksUri: `https://login.microsoftonline.com/${config.azure.tenantId}/discovery/v2.0/keys`,
  cache: true,
  rateLimit: true,
});

const getSigningKey = (header: jwt.JwtHeader): Promise<string> =>
  new Promise((resolve, reject) => {
    client.getSigningKey(header.kid!, (err, key) => {
      if (err) return reject(err);
      resolve(key!.getPublicKey());
    });
  });

export interface AzureClaims {
  oid: string;
  email: string;
  name: string;
}

/**
 * Validates an Azure AD access token:
 *   - signature via JWKS
 *   - issuer must match the configured tenant
 *   - audience must match the configured client ID
 *   - expiry is checked by jsonwebtoken
 *
 * Throws AppError(401) on any validation failure so callers don't need
 * to handle multiple error types.
 */
export const validateAzureToken = async (token: string): Promise<AzureClaims> => {
  const decoded = jwt.decode(token, { complete: true });

  if (!decoded || typeof decoded === 'string' || !decoded.header.kid) {
    logger.error('[SSO] Token decode failed — missing or malformed header', {
      hasDecoded: !!decoded,
      isString: typeof decoded === 'string',
      kid: typeof decoded === 'object' && decoded !== null ? decoded.header?.kid : undefined,
    });
    throw new AppError(401, 'Invalid Azure AD token: missing key ID');
  }

  // Log raw claims before signature verification so we can see audience/issuer mismatches.
  const rawPayload = decoded.payload as jwt.JwtPayload;
  logger.info('[SSO] Token received — pre-validation claims', {
    aud:      rawPayload.aud,
    iss:      rawPayload.iss,
    tid:      rawPayload.tid,
    oid:      rawPayload.oid  ? '(present)' : '(missing)',
    email:    rawPayload.email ?? rawPayload.preferred_username ?? '(missing)',
    exp:      rawPayload.exp ? new Date(rawPayload.exp * 1000).toISOString() : '(missing)',
    now:      new Date().toISOString(),
    expired:  rawPayload.exp ? Date.now() / 1000 > rawPayload.exp : 'unknown',
  });
  logger.info('[SSO] Expected values', {
    expectedAudience: config.azure.clientId,
    expectedIssuer:   `https://login.microsoftonline.com/${config.azure.tenantId}/v2.0`,
  });

  let payload: jwt.JwtPayload;
  try {
    const signingKey = await getSigningKey(decoded.header);
    payload = jwt.verify(token, signingKey, {
      audience: config.azure.clientId,
      issuer: `https://login.microsoftonline.com/${config.azure.tenantId}/v2.0`,
    }) as jwt.JwtPayload;
    logger.info('[SSO] Signature + claims verification passed');
  } catch (err) {
    logger.error('[SSO] Token verification failed', {
      error:     (err as Error).message,
      errorType: (err as Error).name,
      tokenAud:  rawPayload.aud,
      tokenIss:  rawPayload.iss,
      configAud: config.azure.clientId,
      configIss: `https://login.microsoftonline.com/${config.azure.tenantId}/v2.0`,
    });
    throw new AppError(401, `Azure AD token validation failed: ${(err as Error).message}`);
  }

  const oid   = payload.oid   ?? payload.sub ?? '';
  const email = payload.email ?? payload.preferred_username ?? '';
  const name  = payload.name  ?? email;

  if (!oid || !email) {
    logger.error('[SSO] Required claims missing after verification', { oid: !!oid, email: !!email });
    throw new AppError(401, 'Azure AD token is missing required claims (oid, email)');
  }

  logger.info('[SSO] Token validated successfully', { email, oid: oid.slice(0, 8) + '…' });
  return { oid, email, name };
};
