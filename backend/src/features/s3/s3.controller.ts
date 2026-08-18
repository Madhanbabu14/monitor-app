import { Request, Response } from 'express';
import { s3Service } from './s3.service';
import { AppError } from '../../middleware/error.middleware';

export class S3Controller {
  async listFiles(req: Request, res: Response): Promise<void> {
    const { prefix, search, startDate, endDate, page, limit, sortBy, sortOrder } = req.query;

    const result = await s3Service.listFiles({
      prefix: prefix as string | undefined,
      search: search as string | undefined,
      startDate: startDate as string | undefined,
      endDate: endDate as string | undefined,
      page: page ? parseInt(page as string, 10) : 1,
      limit: limit ? Math.min(parseInt(limit as string, 10), 100) : 20,
      sortBy: (sortBy as 'name' | 'size' | 'lastModified') || 'lastModified',
      sortOrder: (sortOrder as 'asc' | 'desc') || 'desc',
    });

    res.json({ status: 'success', data: result });
  }

  async downloadFile(req: Request, res: Response): Promise<void> {
    const { key } = req.query;
    if (!key || typeof key !== 'string') {
      throw new AppError(400, 'File key is required');
    }
    await s3Service.streamFile(key, res);
  }

  async pipelineNames(_req: Request, res: Response): Promise<void> {
    const names = await s3Service.getPipelineNames();
    res.json({ status: 'success', data: names });
  }

  async retriggerFile(req: Request, res: Response): Promise<void> {
    const { fileName, s3Key, pipelineName } = req.body as {
      fileName?: string;
      s3Key?: string;
      pipelineName?: string;
    };
    if (!fileName) throw new AppError(400, 'fileName is required');
    if (!s3Key) throw new AppError(400, 's3Key is required');

    const result = await s3Service.retriggerFile(fileName, s3Key, pipelineName);
    res.status(201).json({ status: 'success', data: result });
  }
}

export const s3Controller = new S3Controller();
