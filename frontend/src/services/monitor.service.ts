import { api } from './api';

export type MonitorStatus = 'Processed' | 'Failed' | 'In Progress' | 'Not Processed';

export interface FileMonitorRow {
  fileName: string;
  pipelineName: string;
  fileReceivedDate: string | null;
  recordsInserted: number;
  recordsUpdated: number;
  totalRecords: number;
  processStatus: MonitorStatus;
  fileProcessed: boolean;
  errorMessage: string | null;
}

export interface StatusBreakdown {
  processed: number; failed: number; inProgress: number; notProcessed: number;
}
export interface FileMonitorResult {
  data: FileMonitorRow[];
  total: number;
  page: number;
  limit: number;
  totalPages: number;
  dbAvailable: boolean;
  statusBreakdown: StatusBreakdown;
}

export interface MonitorParams {
  startDate?: string;
  endDate?: string;
  status?: string;
  pipeline?: string;
  search?: string;
  page?: number;
  limit?: number;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
}

export interface DashboardKpis {
  totalFiles: number; processed: number; failed: number;
  inProgress: number; notProcessed: number;
  rowsInserted: number; rowsUpdated: number;
  successRate: number; failureRate: number;
}
export interface PipelineStatusItem {
  pipelineName: string; totalFiles: number; processedFiles: number;
  failedFiles: number; lastRunTime: string | null;
  status: 'healthy' | 'warning' | 'failed';
}
export interface TrendPoint { period: string; processed: number; failed: number; inProgress: number; }
export interface TopFailureItem { pipelineName: string; fileName: string; failureReason: string | null; failedTime: string; }
export interface ActivityItem { time: string; fileName: string; pipelineName: string; status: string; remark: string | null; }
export interface DashboardData {
  kpis: DashboardKpis; pipelineStatus: PipelineStatusItem[]; trend: TrendPoint[];
  topFailures: TopFailureItem[]; recentActivity: ActivityItem[];
  mostActivePipeline: string | null; highestFailurePipeline: string | null;
  dbAvailable: boolean;
}

export interface NotProcessedCountResult {
  count: number;
}

export const monitorService = {
  getDashboard: (startDate?: string, endDate?: string): Promise<DashboardData> =>
    api.get<{ data: DashboardData }>('/monitor/dashboard', { params: { startDate, endDate } }).then((r) => r.data.data),

  getNotProcessedCount: (startDate?: string, endDate?: string): Promise<NotProcessedCountResult> =>
    api.get<{ data: NotProcessedCountResult }>('/monitor/not-processed-count', { params: { startDate, endDate } }).then((r) => r.data.data),

  getFiles: (params: MonitorParams): Promise<FileMonitorResult> =>
    api.get<{ data: FileMonitorResult }>('/monitor/files', { params }).then((r) => r.data.data),

  getReconciled: (params: MonitorParams): Promise<FileMonitorResult> =>
    api.get<{ data: FileMonitorResult }>('/monitor/reconcile', { params }).then((r) => r.data.data),

  getPipelineNames: (): Promise<string[]> =>
    api.get<{ data: string[] }>('/monitor/pipeline-names').then((r) => r.data.data),

  getDbStatus: (): Promise<{ available: boolean }> =>
    api.get<{ data: { available: boolean } }>('/monitor/db-status').then((r) => r.data.data),
};
