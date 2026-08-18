import { Pool, QueryResultRow } from 'pg';
import { logger } from '../../utils/logger';

let prodPool: Pool | null = null;

const getProdPool = (): Pool | null => {
  const url = process.env.PRODUCTION_DATABASE_URL;
  if (!url || url.includes('<YOUR_ENCODED_PASSWORD>')) return null;

  if (!prodPool) {
    prodPool = new Pool({
      connectionString: url,
      max: 5,
      idleTimeoutMillis: 30000,
      connectionTimeoutMillis: 5000,
      ssl: { rejectUnauthorized: false },
    });
    prodPool.on('error', (err) => {
      logger.error('Production DB pool error', { error: err.message });
    });
  }
  return prodPool;
};

export const prodQuery = async <T extends QueryResultRow = Record<string, unknown>>(
  text: string,
  params?: unknown[]
): Promise<T[] | null> => {
  const pool = getProdPool();
  if (!pool) return null;

  let client;
  try {
    client = await pool.connect();
    const result = await client.query<T>(text, params);
    return result.rows;
  } catch (err) {
    logger.warn('Production DB query failed', { error: (err as Error).message, sql: text.slice(0, 60) });
    return null;
  } finally {
    client?.release();
  }
};
