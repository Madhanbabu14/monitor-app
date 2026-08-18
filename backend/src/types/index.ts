export type PipelineStatus = 'Success' | 'Failed' | 'Running' | 'Cancelled' | 'Warning' | 'Queued';
export type UserRole = 'Admin' | 'Operator' | 'Viewer';
export type AuditAction = 'LOGIN' | 'LOGOUT' | 'PIPELINE_RERUN' | 'VALIDATION_EXECUTED' | 'CONFIG_CHANGED' | 'RECOVERY_TRIGGERED';

export interface Pipeline {
  id: string;
  name: string;
  displayName: string;
  targetTable: string;
  s3Prefix: string;
  isActive: boolean;
  createdAt: Date;
  updatedAt: Date;
}

export interface PipelineRun {
  id: string;
  pipelineId: string;
  pipelineName: string;
  runId: string;
  status: PipelineStatus;
  triggerType: string;
  startTime: Date | null;
  endTime: Date | null;
  durationMs: number | null;
  filesProcessed: number;
  rowsInserted: number;
  rowsUpdated: number;
  errorMessage: string | null;
  createdAt: Date;
}

export interface Activity {
  id: string;
  pipelineRunId: string;
  activityName: string;
  activityType: string;
  status: PipelineStatus;
  startTime: Date | null;
  endTime: Date | null;
  durationMs: number | null;
  errorMessage: string | null;
  input: Record<string, unknown> | null;
  output: Record<string, unknown> | null;
}

export interface PipelineError {
  id: string;
  pipelineRunId: string;
  pipelineName: string;
  failedActivity: string;
  errorMessage: string;
  errorCode: string | null;
  fileName: string | null;
  timestamp: Date;
  runId: string;
}

export interface ValidationResult {
  id: string;
  pipelineId: string;
  pipelineName: string;
  targetTable: string;
  totalRecords: number;
  latestModifiedDate: Date | null;
  recordsUpdatedToday: number;
  validationStatus: 'Healthy' | 'Warning' | 'Error';
  validationMessage: string | null;
  validatedAt: Date;
}

export interface PipelineMapping {
  id: string;
  pipelineName: string;
  targetSchema: string;
  targetTable: string;
  s3Prefix: string;
  isActive: boolean;
  createdAt: Date;
  updatedAt: Date;
}

export interface RecoveryJob {
  id: string;
  pipelineName: string;
  startDate: Date;
  endDate: Date;
  filesFound: number;
  filesProcessed: number;
  filesFailed: number;
  status: 'Pending' | 'Running' | 'Success' | 'Failed' | 'Partial';
  triggeredBy: string;
  startedAt: Date;
  completedAt: Date | null;
  errorMessage: string | null;
}

export interface S3FileInfo {
  key: string;
  size: number;
  lastModified: Date;
  etag: string;
}

export interface MissingFileReport {
  pipelineName: string;
  startDate: Date;
  endDate: Date;
  expectedFiles: string[];
  processedFiles: string[];
  missingFiles: string[];
  totalExpected: number;
  totalProcessed: number;
  totalMissing: number;
}

export interface DailyReport {
  date: Date;
  pipelineName: string;
  filesProcessed: number;
  rowsProcessed: number;
  successRuns: number;
  failedRuns: number;
}

export interface AuditLog {
  id: string;
  userId: string;
  userEmail: string;
  action: AuditAction;
  resource: string | null;
  details: Record<string, unknown> | null;
  ipAddress: string | null;
  userAgent: string | null;
  timestamp: Date;
}

export interface User {
  id: string;
  email: string;
  displayName: string;
  role: UserRole;
  azureOid: string;
  isActive: boolean;
  lastLoginAt: Date | null;
  createdAt: Date;
}

export interface DashboardSummary {
  totalPipelines: number;
  runningPipelines: number;
  failedPipelines: number;
  successRate: number;
  todayFilesProcessed: number;
  todayRowsProcessed: number;
}

export interface PaginationParams {
  page: number;
  limit: number;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
}

export interface PaginatedResult<T> {
  data: T[];
  total: number;
  page: number;
  limit: number;
  totalPages: number;
}

export interface FilterParams {
  search?: string;
  status?: PipelineStatus;
  startDate?: string;
  endDate?: string;
  pipelineName?: string;
}

declare global {
  namespace Express {
    interface Request {
      user?: {
        id: string;
        email: string;
        displayName: string;
        role: UserRole;
        azureOid: string;
      };
    }
  }
}
