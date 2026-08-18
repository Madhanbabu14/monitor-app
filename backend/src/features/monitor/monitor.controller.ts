import { Request, Response } from 'express';
import { monitorService } from './monitor.service';

export class MonitorController {
  async getFiles(req: Request, res: Response): Promise<void> {
    const { startDate, endDate, status, pipeline, search, page, limit, sortBy, sortOrder } = req.query;
    const result = await monitorService.getFileMonitor({
      startDate: startDate as string,
      endDate: endDate as string,
      status: status as string,
      pipeline: pipeline as string,
      search: search as string,
      page: page ? parseInt(page as string, 10) : 1,
      limit: limit ? Math.min(parseInt(limit as string, 10), 100) : 20,
      sortBy: sortBy as string,
      sortOrder: (sortOrder as 'asc' | 'desc') ?? 'desc',
    });
    res.json({ status: 'success', data: result });
  }

  async getPipelineNames(_req: Request, res: Response): Promise<void> {
    const names = await monitorService.getPipelineNames();
    res.json({ status: 'success', data: names });
  }

  async getDbStatus(_req: Request, res: Response): Promise<void> {
    const available = await monitorService.isDbAvailable();
    res.json({ status: 'success', data: { available } });
  }

  async getDashboard(req: Request, res: Response): Promise<void> {
    const { startDate, endDate } = req.query;
    const result = await monitorService.getDashboard(startDate as string, endDate as string);
    res.json({ status: 'success', data: result });
  }

  async getNotProcessedCount(req: Request, res: Response): Promise<void> {
    const { startDate, endDate } = req.query;
    const result = await monitorService.getNotProcessedCount(startDate as string, endDate as string);
    res.json({ status: 'success', data: result });
  }

  async getReconciled(req: Request, res: Response): Promise<void> {
    const { startDate, endDate, status, pipeline, search, page, limit, sortBy, sortOrder } = req.query;
    const result = await monitorService.getReconciled({
      startDate: startDate as string,
      endDate:   endDate   as string,
      status:    status    as string,
      pipeline:  pipeline  as string,
      search:    search    as string,
      page:  page  ? parseInt(page  as string, 10) : 1,
      limit: limit ? Math.min(parseInt(limit as string, 10), 200) : 20,
      sortBy:    sortBy    as string,
      sortOrder: (sortOrder as 'asc' | 'desc') ?? 'desc',
    });
    res.json({ status: 'success', data: result });
  }
}

export const monitorController = new MonitorController();
