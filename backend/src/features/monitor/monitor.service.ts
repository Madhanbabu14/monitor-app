import { prodQuery } from '../../infrastructure/database/productionConnection';
import { logger } from '../../utils/logger';
import { s3Service } from '../s3/s3.service';

// ─── Types ────────────────────────────────────────────────────────────────────
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

export type MonitorStatus = 'Processed' | 'Failed' | 'In Progress' | 'Not Processed';

export interface FileMonitorRow {
  fileName: string;
  pipelineName: string | null;
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

// ─── Helpers ──────────────────────────────────────────────────────────────────
const mapStatus = (code: number): MonitorStatus => {
  if (code === 1)  return 'Processed';
  if (code === -1) return 'Failed';
  if (code === 0)  return 'In Progress';
  return 'Not Processed';
};

// Pipeline files follow the pattern: <name>_<8+digit id>...
// Files that DON'T match (_SUCCESS, manifest.json, part-*.csv, etc.) are
// not pipeline data files and must never be counted as "Not Processed".
const PIPELINE_FILE_RE = /^([a-zA-Z][a-zA-Z0-9_]*)_\d{8,}/;
const derivePipeline   = (name: string) => name.match(PIPELINE_FILE_RE)?.[1] ?? null;
const isPipelineFile   = (name: string) => PIPELINE_FILE_RE.test(name);

// ─── S3 Not-Processed lookup ──────────────────────────────────────────────────
// Given a list of S3 file names, returns those that have NO record in the DB.
// Uses a lightweight DISTINCT query (no DISTINCT ON, no extra columns).
async function s3FilesNotInDb(
  s3Files: { name: string; lastModified: string }[],
  pipelineFilter?: string
): Promise<{ name: string; lastModified: string; pipelineName: string }[]> {
  // Pre-filter: only pipeline data files; optionally narrow to selected pipeline.
  const eligible = s3Files.filter(f => {
    const derived = derivePipeline(f.name);
    if (derived === null) return false;
    if (pipelineFilter && derived !== pipelineFilter) return false;
    return true;
  });

  if (eligible.length === 0) return [];

  const names = eligible.map(f => f.name);
  const dbRows = await prodQuery<{ file_name: string }>(
    `SELECT DISTINCT file_name FROM rt.pipeline_operational_logs WHERE file_name = ANY($1::text[])`,
    [names]
  );
  // null means the DB is unreachable (VPN down). We cannot determine which
  // files are unprocessed without the DB, so return nothing rather than
  // incorrectly counting every S3 file as "Not Processed".
  if (dbRows === null) return [];
  const dbSet = new Set(dbRows.map(r => r.file_name));

  return eligible
    .filter(f => !dbSet.has(f.name))
    .map(f => ({ name: f.name, lastModified: f.lastModified, pipelineName: derivePipeline(f.name)! }));
}

export class MonitorService {

  // ── File Monitor — DB-only, legacy /monitor/files endpoint ────────────────
  async getFileMonitor(params: MonitorParams): Promise<FileMonitorResult> {
    const {
      startDate, endDate, status, pipeline, search,
      page = 1, limit = 20, sortBy = 'fileReceivedDate', sortOrder = 'desc',
    } = params;

    const orderCol = sortBy === 'fileName'       ? 'file_name'
      : sortBy === 'pipelineName'                ? 'pipeline_name'
      : sortBy === 'recordsInserted'             ? 'rows_inserted'
      : sortBy === 'recordsUpdated'              ? 'rows_updated'
      : 'process_date_time';
    const orderDir = sortOrder === 'asc' ? 'ASC' : 'DESC';

    const sql = `
      WITH latest AS (
        SELECT DISTINCT ON (file_name)
          file_name, pipeline_name, process_date_time, process_status, process_remark,
          GREATEST(COALESCE(rows_inserted, 0), 0) AS rows_inserted,
          GREATEST(COALESCE(rows_updated,  0), 0) AS rows_updated
        FROM rt.pipeline_operational_logs
        WHERE
          ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1::date)
          AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2::date)
          AND ($3::text IS NULL OR pipeline_name = $3)
          AND ($5::text IS NULL OR file_name ILIKE '%' || $5 || '%')
        ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
      ),
      filtered AS (
        SELECT * FROM latest
        WHERE (
          $4::text IS NULL
          OR ($4 = 'Processed'   AND process_status = 1)
          OR ($4 = 'Failed'      AND process_status = -1)
          OR ($4 = 'In Progress' AND process_status = 0)
        )
      )
      SELECT *, COUNT(*) OVER() AS total_count
      FROM filtered
      ORDER BY ${orderCol} ${orderDir} NULLS LAST
      LIMIT $6 OFFSET $7
    `;

    const offset = (page - 1) * limit;
    type RawRow = {
      file_name: string; pipeline_name: string; process_date_time: string | null;
      process_status: number; process_remark: string | null;
      rows_inserted: number; rows_updated: number; total_count: string;
    };
    const rows = await prodQuery<RawRow>(sql, [
      startDate || null, endDate || null, pipeline || null,
      status || null, search || null, limit, offset,
    ]);
    const safeRows = rows ?? [];
    const total = safeRows.length > 0 ? parseInt(safeRows[0].total_count, 10) : 0;

    return {
      data: safeRows.map((r) => ({
        fileName: r.file_name, pipelineName: r.pipeline_name,
        fileReceivedDate: r.process_date_time,
        recordsInserted: r.rows_inserted, recordsUpdated: r.rows_updated,
        totalRecords: r.rows_inserted + r.rows_updated,
        processStatus: mapStatus(r.process_status),
        fileProcessed: r.process_status === 1,
        errorMessage: r.process_remark,
      })),
      total, page, limit, totalPages: Math.ceil(total / limit),
      dbAvailable: rows !== null,
      statusBreakdown: { processed: 0, failed: 0, inProgress: 0, notProcessed: 0 },
    };
  }

  // ── Pipeline names ─────────────────────────────────────────────────────────
  async getPipelineNames(): Promise<string[]> {
    const rows = await prodQuery<{ pipeline_name: string }>(
      `SELECT DISTINCT pipeline_name FROM rt.pipeline_operational_logs ORDER BY pipeline_name`
    );
    return (rows ?? []).map((r) => r.pipeline_name);
  }

  // ── DB health check ────────────────────────────────────────────────────────
  async isDbAvailable(): Promise<boolean> {
    const rows = await prodQuery<{ ok: number }>('SELECT 1 AS ok');
    return (rows?.length ?? 0) > 0;
  }

  // ── Dashboard ──────────────────────────────────────────────────────────────
  async getDashboard(startDate?: string, endDate?: string): Promise<DashboardData> {
    const df = [startDate || null, endDate || null];

    // ── WHY DB IS AUTHORITATIVE FOR KPIs ─────────────────────────────────────
    // The pipeline's process_date_time determines "when something was processed".
    // S3 lastModified is "when the file arrived in S3" — a different dimension.
    // A file uploaded to S3 three days ago can be processed by the pipeline today.
    // So Total/Processed/Failed/Rows are always DB-based (matches what user saw before).
    // Only "Not Processed" is derived from S3 (files in S3 with no DB record at all).
    const kpiSql = `
      WITH latest AS (
        SELECT DISTINCT ON (file_name)
          file_name, process_status,
          GREATEST(COALESCE(rows_inserted, 0), 0) AS rows_inserted,
          GREATEST(COALESCE(rows_updated,  0), 0) AS rows_updated
        FROM rt.pipeline_operational_logs
        WHERE ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1)
          AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2)
        ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
      )
      SELECT
        COUNT(*)::int                                         AS total_files,
        COUNT(CASE WHEN process_status = 1  THEN 1 END)::int AS processed,
        COUNT(CASE WHEN process_status = -1 THEN 1 END)::int AS failed,
        COUNT(CASE WHEN process_status = 0  THEN 1 END)::int AS in_progress,
        COALESCE(SUM(rows_inserted), 0)::bigint              AS rows_inserted,
        COALESCE(SUM(rows_updated),  0)::bigint              AS rows_updated
      FROM latest`;

    const pipelineSql = `
      WITH latest AS (
        SELECT DISTINCT ON (pipeline_name, file_name)
          pipeline_name, file_name, process_status, process_date_time
        FROM rt.pipeline_operational_logs
        WHERE ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1)
          AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2)
        ORDER BY pipeline_name, file_name, process_date_time::timestamptz DESC NULLS LAST
      )
      SELECT
        pipeline_name,
        COUNT(*)::int                                          AS total_files,
        COUNT(CASE WHEN process_status = 1  THEN 1 END)::int  AS processed,
        COUNT(CASE WHEN process_status = -1 THEN 1 END)::int  AS failed,
        MAX(process_date_time::timestamptz)                    AS last_run_time
      FROM latest
      GROUP BY pipeline_name
      ORDER BY total_files DESC`;

    const trendSql = `
      WITH latest_per_day AS (
        SELECT DISTINCT ON ((process_date_time::timestamptz)::date, file_name)
          (process_date_time::timestamptz)::date AS period, process_status
        FROM rt.pipeline_operational_logs
        WHERE ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1)
          AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2)
        ORDER BY (process_date_time::timestamptz)::date,
                 file_name, process_date_time::timestamptz DESC NULLS LAST
      )
      SELECT period::text,
        COUNT(CASE WHEN process_status = 1  THEN 1 END)::int AS processed,
        COUNT(CASE WHEN process_status = -1 THEN 1 END)::int AS failed,
        COUNT(CASE WHEN process_status = 0  THEN 1 END)::int AS in_progress
      FROM latest_per_day GROUP BY period ORDER BY period ASC`;

    const failureSql = `
      WITH latest AS (
        SELECT DISTINCT ON (file_name)
          pipeline_name, file_name, process_status, process_remark, process_date_time
        FROM rt.pipeline_operational_logs
        WHERE ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1)
          AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2)
        ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
      )
      SELECT pipeline_name, file_name,
             process_remark AS failure_reason, process_date_time AS failed_time
      FROM latest WHERE process_status = -1
      ORDER BY process_date_time::timestamptz DESC LIMIT 10`;

    const activitySql = `
      SELECT DISTINCT ON (file_name)
        process_date_time AS time, file_name, pipeline_name, process_status, process_remark AS remark
      FROM rt.pipeline_operational_logs
      WHERE ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1)
        AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2)
      ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
      LIMIT 15`;

    type KpiRaw      = { total_files: number; processed: number; failed: number; in_progress: number; rows_inserted: string; rows_updated: string; };
    type PipelineRaw = { pipeline_name: string; total_files: number; processed: number; failed: number; last_run_time: string | null; };
    type TrendRaw    = { period: string; processed: number; failed: number; in_progress: number; };
    type FailureRaw  = { pipeline_name: string; file_name: string; failure_reason: string | null; failed_time: string; };
    type ActivityRaw = { time: string; file_name: string; pipeline_name: string; process_status: number; remark: string | null; };

    // All DB queries run in parallel. S3 is NOT called here — the frontend
    // fetches Not Processed count via /monitor/not-processed-count separately
    // so DB metrics render immediately without waiting for AWS.
    const [kpiRows, pipelineRows, trendRows, failureRows, activityRows] = await Promise.all([
      prodQuery<KpiRaw>(kpiSql, df),
      prodQuery<PipelineRaw>(pipelineSql, df),
      prodQuery<TrendRaw>(trendSql, df),
      prodQuery<FailureRaw>(failureSql, df),
      prodQuery<ActivityRaw>(activitySql, df),
    ]);

    // DB-based KPIs — identical to pre-S3 implementation (Total = 1100+ restored).
    const kpi     = kpiRows?.[0];
    const total   = kpi?.total_files  ?? 0;
    const proc    = kpi?.processed    ?? 0;
    const fail    = kpi?.failed       ?? 0;
    const inProg  = kpi?.in_progress  ?? 0;
    const rowsIns = parseInt(String(kpi?.rows_inserted ?? '0'), 10);
    const rowsUpd = parseInt(String(kpi?.rows_updated  ?? '0'), 10);

    const pipelineStatus: PipelineStatusItem[] = (pipelineRows ?? []).map(r => ({
      pipelineName: r.pipeline_name, totalFiles: r.total_files,
      processedFiles: r.processed, failedFiles: r.failed, lastRunTime: r.last_run_time,
      status: r.failed === 0 ? 'healthy' : r.failed >= r.total_files * 0.5 ? 'failed' : 'warning',
    }));

    const sorted = [...pipelineStatus].sort(
      (a, b) => (b.failedFiles / (b.totalFiles || 1)) - (a.failedFiles / (a.totalFiles || 1))
    );

    logger.info('Dashboard DB metrics', { startDate, endDate, total, proc, fail, inProg });

    return {
      kpis: {
        totalFiles: total, processed: proc, failed: fail,
        inProgress: inProg, notProcessed: 0,  // fetched async via /not-processed-count
        rowsInserted: rowsIns, rowsUpdated: rowsUpd,
        successRate: total > 0 ? Math.round((proc / total) * 1000) / 10 : 0,
        failureRate: total > 0 ? Math.round((fail / total) * 1000) / 10 : 0,
      },
      pipelineStatus,
      trend: (trendRows ?? []).map(r => ({ period: r.period, processed: r.processed, failed: r.failed, inProgress: r.in_progress })),
      topFailures: (failureRows ?? []).map(r => ({ pipelineName: r.pipeline_name, fileName: r.file_name, failureReason: r.failure_reason, failedTime: r.failed_time })),
      recentActivity: (activityRows ?? []).map(r => ({ time: r.time, fileName: r.file_name, pipelineName: r.pipeline_name, status: mapStatus(r.process_status), remark: r.remark })),
      mostActivePipeline: pipelineStatus[0]?.pipelineName ?? null,
      highestFailurePipeline: sorted[0]?.failedFiles > 0 ? sorted[0].pipelineName : null,
      dbAvailable: kpiRows !== null,
    };
  }

  // ── Not Processed count — S3-only, called asynchronously by the dashboard ──
  async getNotProcessedCount(startDate?: string, endDate?: string): Promise<{ count: number }> {
    const s3Files = await s3Service.getFileNamesForDateRange(startDate, endDate);
    const notProcFiles = await s3FilesNotInDb(s3Files);
    logger.info('Not Processed count', { startDate, endDate, s3Total: s3Files.length, count: notProcFiles.length });
    return { count: notProcFiles.length };
  }

  // ── Reconciled File Monitor — DB rows + S3 "Not Processed" rows ───────────
  //
  // Design:
  //   DB  (date filter = process_date_time) → Processed / Failed / In Progress rows
  //   S3  (date filter = lastModified)      → "Not Processed" rows (no DB record)
  //   Merge → apply search/status filter → sort → paginate in memory
  //
  // Why separate date axes:
  //   The pipeline runs daily and processes files that arrived in S3 over many days.
  //   "Yesterday" in DB means "processed yesterday" (could be files from any S3 date).
  //   "Yesterday" in S3 means "arrived in S3 yesterday" (may not be processed yet).
  async getReconciled(params: MonitorParams): Promise<FileMonitorResult> {
    const {
      startDate, endDate, status, pipeline, search,
      page = 1, limit = 20, sortBy = 'fileReceivedDate', sortOrder = 'desc',
    } = params;

    const needsDbFiles  = !status || status !== 'Not Processed';
    const needsS3Check  = !status || status === 'Not Processed';

    // DB query: ALL files for date range (no SQL pagination — merging in memory).
    // No status filter in SQL; we filter in memory after merge so the total is correct.
    const dbSql = `
      SELECT DISTINCT ON (file_name)
        file_name, pipeline_name, process_date_time, process_status, process_remark,
        GREATEST(COALESCE(rows_inserted, 0), 0) AS rows_inserted,
        GREATEST(COALESCE(rows_updated,  0), 0) AS rows_updated
      FROM rt.pipeline_operational_logs
      WHERE
        ($1::date IS NULL OR (process_date_time::timestamptz)::date >= $1::date)
        AND ($2::date IS NULL OR (process_date_time::timestamptz)::date <= $2::date)
        AND ($3::text IS NULL OR pipeline_name = $3)
        AND ($4::text IS NULL OR file_name ILIKE '%' || $4 || '%')
      ORDER BY file_name, process_date_time::timestamptz DESC NULLS LAST
    `;

    type DbRow = {
      file_name: string; pipeline_name: string; process_date_time: string | null;
      process_status: number; process_remark: string | null;
      rows_inserted: number; rows_updated: number;
    };

    // DB and S3 run in parallel where both are needed.
    const [rawDbRows, s3Files] = await Promise.all([
      needsDbFiles
        ? prodQuery<DbRow>(dbSql, [startDate || null, endDate || null, pipeline || null, search || null])
        : Promise.resolve([] as DbRow[]),
      needsS3Check
        ? s3Service.getFileNamesForDateRange(startDate, endDate)
        : Promise.resolve([] as { name: string; lastModified: string }[]),
    ]);

    const safeDbRows = rawDbRows ?? [];

    // Convert DB rows → FileMonitorRow.
    const dbRows: FileMonitorRow[] = safeDbRows.map(r => ({
      fileName: r.file_name, pipelineName: r.pipeline_name,
      fileReceivedDate: r.process_date_time,
      recordsInserted: r.rows_inserted, recordsUpdated: r.rows_updated,
      totalRecords: r.rows_inserted + r.rows_updated,
      processStatus: mapStatus(r.process_status),
      fileProcessed: r.process_status === 1,
      errorMessage: r.process_remark,
    }));

    // S3 Not Processed rows — only if status allows it.
    let notProcRows: FileMonitorRow[] = [];
    if (needsS3Check && s3Files.length > 0) {
      const notProcFiles = await s3FilesNotInDb(s3Files, pipeline);
      notProcRows = notProcFiles.map(f => ({
        fileName: f.name, pipelineName: f.pipelineName,
        fileReceivedDate: f.lastModified,
        recordsInserted: 0, recordsUpdated: 0, totalRecords: 0,
        processStatus: 'Not Processed' as MonitorStatus,
        fileProcessed: false, errorMessage: null,
      }));
    }

    // Merge. DB rows already have pipeline+search applied in SQL.
    // Not Processed rows have pipeline applied in s3FilesNotInDb().
    let merged = [...dbRows, ...notProcRows];

    // Apply search to Not Processed rows (DB search was already in SQL).
    // Re-apply to merged to cover both (cheaper than maintaining two filters).
    if (search?.trim()) {
      const q = search.trim().toLowerCase();
      merged = merged.filter(r => r.fileName.toLowerCase().includes(q));
    }

    // Status filter — 'Not Processed' only matches S3-only rows.
    // A Failed file has a DB record so it is NEVER "Not Processed".
    if (status) {
      merged = merged.filter(r => r.processStatus === status);
    }

    // Sort in memory — O(N log N).
    merged.sort((a, b) => {
      let cmp = 0;
      if (sortBy === 'fileName')          cmp = a.fileName.localeCompare(b.fileName);
      else if (sortBy === 'pipelineName') cmp = (a.pipelineName ?? '').localeCompare(b.pipelineName ?? '');
      else {
        const aT = a.fileReceivedDate ? new Date(a.fileReceivedDate).getTime() : 0;
        const bT = b.fileReceivedDate ? new Date(b.fileReceivedDate).getTime() : 0;
        cmp = aT - bT;
      }
      return sortOrder === 'asc' ? cmp : -cmp;
    });

    const total  = merged.length;
    const offset = (page - 1) * limit;
    const data   = merged.slice(offset, offset + limit);

    // Single-pass breakdown over the full post-filter, pre-pagination result set.
    const statusBreakdown: StatusBreakdown = { processed: 0, failed: 0, inProgress: 0, notProcessed: 0 };
    for (const r of merged) {
      if (r.processStatus === 'Processed')      statusBreakdown.processed++;
      else if (r.processStatus === 'Failed')     statusBreakdown.failed++;
      else if (r.processStatus === 'In Progress') statusBreakdown.inProgress++;
      else if (r.processStatus === 'Not Processed') statusBreakdown.notProcessed++;
    }

    logger.info('Reconcile S3+DB', {
      startDate, endDate,
      pipelineFilter: pipeline ?? '(all)',
      statusFilter:   status   ?? '(all)',
      ...statusBreakdown,
      total,
    });

    return {
      data, total, page, limit,
      totalPages: Math.ceil(total / limit),
      dbAvailable: rawDbRows !== null,
      statusBreakdown,
    };
  }
}

export const monitorService = new MonitorService();
