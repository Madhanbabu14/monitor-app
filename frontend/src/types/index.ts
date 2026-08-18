export type PipelineStatus = 'Success' | 'Failed' | 'Running' | 'Cancelled' | 'Warning' | 'Queued';
export type UserRole = 'Admin' | 'Operator' | 'Viewer';
export type ThemeMode = 'light' | 'dark';

export interface User {
  id: string;
  email: string;
  displayName: string;
  role: UserRole;
  azureOid: string;
}

export interface Pipeline {
  id: string;
  name: string;
  displayName: string;
  targetTable: string;
  s3Prefix: string;
  isActive: boolean;
}

export interface PipelineRun {
  id: string;
  pipelineId: string;
  pipelineName: string;
  runId: string;
  status: PipelineStatus;
  triggerType: string;
  startTime: string | null;
  endTime: string | null;
  durationMs: number | null;
  filesProcessed: number;
  rowsInserted: number;
  rowsUpdated: number;
  errorMessage: string | null;
  createdAt: string;
  activities?: Activity[];
}

export interface Activity {
  id: string;
  pipelineRunId: string;
  activityName: string;
  activityType: string;
  status: PipelineStatus;
  startTime: string | null;
  endTime: string | null;
  durationMs: number | null;
  errorMessage: string | null;
}

export interface PipelineError {
  id: string;
  pipelineRunId: string;
  pipelineName: string;
  failedActivity: string;
  errorMessage: string;
  errorCode: string | null;
  fileName: string | null;
  timestamp: string;
  runId: string;
}

export interface ValidationResult {
  id: string;
  pipelineId: string;
  pipelineName: string;
  targetTable: string;
  totalRecords: number;
  latestModifiedDate: string | null;
  recordsUpdatedToday: number;
  validationStatus: 'Healthy' | 'Warning' | 'Error';
  validationMessage: string | null;
  validatedAt: string;
}

export interface PipelineMapping {
  id: string;
  pipelineName: string;
  targetSchema: string;
  targetTable: string;
  s3Prefix: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface RecoveryJob {
  id: string;
  pipelineName: string;
  startDate: string;
  endDate: string;
  filesFound: number;
  filesProcessed: number;
  filesFailed: number;
  status: 'Pending' | 'Running' | 'Success' | 'Failed' | 'Partial';
  triggeredBy: string;
  startedAt: string;
  completedAt: string | null;
  errorMessage: string | null;
}

export interface DailyReport {
  date: string;
  pipelineName: string;
  filesProcessed: number;
  rowsProcessed: number;
  successRuns: number;
  failedRuns: number;
}

export interface MissingFileReport {
  pipelineName: string;
  startDate: string;
  endDate: string;
  missingFiles: string[];
  totalExpected: number;
  totalProcessed: number;
  totalMissing: number;
}

export interface AuditLog {
  id: string;
  userId: string;
  userEmail: string;
  action: string;
  resource: string | null;
  details: Record<string, unknown> | null;
  ipAddress: string | null;
  timestamp: string;
}

export interface DashboardSummary {
  totalPipelines: number;
  runningPipelines: number;
  failedPipelines: number;
  successRate: number;
  todayFilesProcessed: number;
  todayRowsProcessed: number;
}

export interface PaginatedResult<T> {
  data: T[];
  total: number;
  page: number;
  limit: number;
  totalPages: number;
}

export interface ApiResponse<T> {
  status: 'success' | 'error';
  data?: T;
  message?: string;
}

export interface S3FileItem {
  key: string;
  name: string;
  prefix: string;
  size: number;
  lastModified: string;
  etag: string;
  pipelineName: string | null;
}

export interface S3FilesResult {
  files: S3FileItem[];
  total: number;
  totalSize: number;
  page: number;
  limit: number;
  totalPages: number;
  folders: string[];
  suggestions: string[];
  bucket: string;
  rootPrefix: string;
}
