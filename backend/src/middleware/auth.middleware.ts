import { Request, Response, NextFunction } from 'express';
import jwt from 'jsonwebtoken';
import jwksClient from 'jwks-rsa';
import { config } from '../config';
import { AppError } from './error.middleware';
import { UserRole } from '../types';

const azureJwksClient = jwksClient({
  jwksUri: `https://login.microsoftonline.com/${config.azure.tenantId}/discovery/v2.0/keys`,
  cache: true,
  rateLimit: true,
});

const getSigningKey = (header: jwt.JwtHeader): Promise<string> =>
  new Promise((resolve, reject) => {
    azureJwksClient.getSigningKey(header.kid!, (err, key) => {
      if (err) return reject(err);
      resolve(key!.getPublicKey());
    });
  });

export const authenticate = async (
  req: Request,
  _res: Response,
  next: NextFunction
): Promise<void> => {
  const authHeader = req.headers.authorization;
  if (!authHeader?.startsWith('Bearer ')) {
    return next(new AppError(401, 'No authentication token provided'));
  }

  const token = authHeader.slice(7);

  try {
    const decoded = jwt.decode(token, { complete: true });
    if (!decoded || typeof decoded === 'string') {
      throw new AppError(401, 'Invalid token format');
    }

    let payload: jwt.JwtPayload;

    if (decoded.header.kid) {
      const signingKey = await getSigningKey(decoded.header);
      payload = jwt.verify(token, signingKey, {
        audience: config.azure.clientId,
        issuer: `https://login.microsoftonline.com/${config.azure.tenantId}/v2.0`,
      }) as jwt.JwtPayload;
    } else {
      payload = jwt.verify(token, config.jwt.secret) as jwt.JwtPayload;
    }

    req.user = {
      id: payload.sub ?? payload.oid ?? '',
      email: payload.email ?? payload.preferred_username ?? '',
      displayName: payload.name ?? '',
      role: (payload.role as UserRole) ?? 'Viewer',
      azureOid: payload.oid ?? '',
    };
    next();
  } catch (err) {
    if (err instanceof AppError) return next(err);
    next(new AppError(401, 'Invalid or expired token'));
  }
};

export const authorize = (...roles: UserRole[]) =>
  (req: Request, _res: Response, next: NextFunction): void => {
    if (!req.user) {
      return next(new AppError(401, 'Not authenticated'));
    }
    if (!roles.includes(req.user.role)) {
      return next(new AppError(403, 'Insufficient permissions'));
    }
    next();
  };
