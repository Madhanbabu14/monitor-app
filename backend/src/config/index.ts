import dotenv from 'dotenv';

dotenv.config();

const required = (key: string): string => {
  const value = process.env[key];
  if (!value) throw new Error(`Missing required environment variable: ${key}`);
  return value;
};

const optional = (key: string, defaultValue: string): string =>
  process.env[key] ?? defaultValue;

export const config = {
  env: optional('NODE_ENV', 'development'),
  port: parseInt(optional('PORT', '4000'), 10),
  isDev: optional('NODE_ENV', 'development') === 'development',

  database: {
    url: required('DATABASE_URL'),
    maxConnections: parseInt(optional('DB_MAX_CONNECTIONS', '20'), 10),
    idleTimeoutMs: parseInt(optional('DB_IDLE_TIMEOUT_MS', '30000'), 10),
    connectionTimeoutMs: parseInt(optional('DB_CONNECTION_TIMEOUT_MS', '5000'), 10),
  },

  azure: {
    tenantId: required('AZURE_TENANT_ID'),
    clientId: required('AZURE_CLIENT_ID'),
  },

  aws: {
    accessKeyId: required('AWS_ACCESS_KEY_ID'),
    secretAccessKey: required('AWS_SECRET_ACCESS_KEY'),
    region: optional('AWS_REGION', 'us-east-1'),
    s3Bucket: required('AWS_S3_BUCKET'),
    s3Prefix: optional('AWS_S3_PREFIX', 'data/'),
  },

  jwt: {
    secret: required('JWT_SECRET'),
    expiresIn: optional('JWT_EXPIRES_IN', '8h'),
  },

  cors: {
    origin: optional('CORS_ORIGIN', 'http://localhost:3000'),
  },

  rateLimit: {
    windowMs: parseInt(optional('RATE_LIMIT_WINDOW_MS', '900000'), 10),
    maxRequests: parseInt(optional('RATE_LIMIT_MAX_REQUESTS', '100'), 10),
  },

  webhook: {
    secret: required('WEBHOOK_SECRET'),
  },
};
