import { app } from './app';
import { config } from './config';
import { testConnection, closePool } from './infrastructure/database/connection';
import { logger } from './utils/logger';

const startServer = async (): Promise<void> => {
  await testConnection();

  const server = app.listen(config.port, () => {
    logger.info(`Pipeline Control Center API running on port ${config.port}`, {
      env: config.env,
      port: config.port,
    });
  });

  const shutdown = async (signal: string): Promise<void> => {
    logger.info(`Received ${signal}, shutting down gracefully`);
    server.close(async () => {
      await closePool();
      logger.info('Server closed');
      process.exit(0);
    });
    setTimeout(() => {
      logger.error('Forced shutdown after timeout');
      process.exit(1);
    }, 10000);
  };

  process.on('SIGTERM', () => shutdown('SIGTERM'));
  process.on('SIGINT', () => shutdown('SIGINT'));
  process.on('uncaughtException', (err) => {
    logger.error('Uncaught exception', { error: err.message, stack: err.stack });
    process.exit(1);
  });
  process.on('unhandledRejection', (reason) => {
    logger.error('Unhandled rejection', { reason });
    process.exit(1);
  });
};

startServer().catch((err) => {
  logger.error('Failed to start server', { error: (err as Error).message });
  process.exit(1);
});