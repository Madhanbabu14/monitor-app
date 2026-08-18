import { S3Client, ListObjectsV2Command, GetObjectCommand } from '@aws-sdk/client-s3';
import { Readable } from 'stream';
import { Response } from 'express';
import { randomUUID } from 'crypto';
import { config } from '../../config';
import { AppError } from '../../middleware/error.middleware';
import { logger } from '../../utils/logger';
import { query, queryOne } from '../../infrastructure/database/connection';

const s3Client = new S3Client({
  region: config.aws.region,
  credentials: {
    accessKeyId: config.aws.accessKeyId,
    secretAccessKey: config.aws.secretAccessKey,
  },
});

export interface S3FileItem {
  key: string;
  name: string;
  prefix: string;
  size: number;
  lastModified: string;
  etag: string;
  pipelineName: string | null;
}

export interface ListFilesParams {
  prefix?: string;
  search?: string;
  startDate?: string;
  endDate?: string;
  page?: number;
  limit?: number;
  sortBy?: 'name' | 'size' | 'lastModified';
  sortOrder?: 'asc' | 'desc';
}

export interface ListFilesResult {
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

export interface RetriggerResult {
  jobId: string;
  pipelineName: string;
  status: 'queued';
}

const extractSuggestions = (items: S3FileItem[]): string[] => {
  const seen = new Set<string>();
  const pattern = /^(.+?)_\d{8,}/;
  for (const item of items) {
    const match = item.name.match(pattern);
    const suggestion = match ? match[1] : item.name.replace(/\.[^.]+$/, '');
    if (suggestion) seen.add(suggestion);
  }
  return Array.from(seen).sort();
};

// Derive pipeline name from filename: enrolment_000000009718_... → enrolment
const derivePipelineName = (fileName: string): string | null => {
  const match = fileName.match(/^(.+?)_\d{8,}/);
  return match ? match[1] : null;
};

// Extract date from filename: enrolment_000000009718_20260629010224.csv → 2026-06-29
// Must match a realistic year (20xx), month (01-12), day (01-31) to skip record IDs like 000000009718
const extractDateFromFilename = (fileName: string): string | null => {
  const match = fileName.match(/(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{4,}/);
  if (!match) return null;
  return `${match[1]}-${match[2]}-${match[3]}`;
};

const toAppError = (err: unknown): AppError => {
  const name = (err as { name?: string })?.name ?? '';
  const message = (err as { message?: string })?.message ?? 'Unknown S3 error';
  const httpStatus = (err as { $metadata?: { httpStatusCode?: number } })?.$metadata?.httpStatusCode;

  logger.error('S3 error', { name, message, httpStatus });

  if (name === 'NoSuchBucket') {
    return new AppError(404, `S3 bucket '${config.aws.s3Bucket}' not found. Verify AWS_S3_BUCKET and AWS_REGION in .env`);
  }
  if (name === 'NoSuchKey') {
    return new AppError(404, 'File not found in S3');
  }
  if (
    name === 'AccessDenied' ||
    name === 'InvalidAccessKeyId' ||
    name === 'SignatureDoesNotMatch' ||
    name === 'InvalidClientTokenId' ||
    httpStatus === 403
  ) {
    return new AppError(
      403,
      'S3 access denied. Check: AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_REGION, and that the IAM user has s3:ListBucket + s3:GetObject on this bucket.'
    );
  }
  if (name === 'RequestExpired' || name === 'TokenExpiredError') {
    return new AppError(403, 'AWS credentials have expired. Refresh your access keys.');
  }
  if (
    message.toLowerCase().includes('econnrefused') ||
    message.toLowerCase().includes('etimedout') ||
    message.toLowerCase().includes('enotfound')
  ) {
    return new AppError(503, 'Cannot reach AWS S3. Check network / VPC settings.');
  }
  return new AppError(
    httpStatus && httpStatus >= 400 && httpStatus < 600 ? httpStatus : 500,
    `S3 error (${name || httpStatus || 'unknown'}): ${message}`
  );
};

// ── S3 raw-listing cache ───────────────────────────────────────────────────────
// Caches the full bucket listing for 60 s so the dashboard and file monitor
// never trigger more than one ListObjectsV2 round-trip per minute.
// Only getFileNamesForDateRange (used by reconciliation) reads from this cache.
// listFiles (the S3 Files page) always calls fetchAllObjects directly so the
// page always shows up-to-date content.
interface RawS3Cache { items: S3FileItem[]; expiresAt: number; }
let rawS3Cache: RawS3Cache | null = null;
let rawS3Inflight: Promise<S3FileItem[]> | null = null; // deduplicates concurrent cold-cache requests
const S3_CACHE_TTL_MS = 60_000; // configurable: change here to adjust TTL

const fetchAllObjectsCached = async (prefix: string): Promise<S3FileItem[]> => {
  if (rawS3Cache && rawS3Cache.expiresAt > Date.now()) {
    logger.debug('S3 cache hit', { remaining: Math.round((rawS3Cache.expiresAt - Date.now()) / 1000) + 's' });
    return rawS3Cache.items;
  }
  // If another request is already fetching, wait for it instead of firing a
  // second ListObjectsV2 round-trip (handles concurrent cold-cache requests).
  if (rawS3Inflight) {
    logger.debug('S3 dedup — awaiting inflight fetch');
    return rawS3Inflight;
  }
  rawS3Inflight = fetchAllObjects(prefix).then((items) => {
    rawS3Cache = { items, expiresAt: Date.now() + S3_CACHE_TTL_MS };
    rawS3Inflight = null;
    logger.info('S3 cache refreshed', { count: items.length, ttlSeconds: S3_CACHE_TTL_MS / 1000 });
    return items;
  }).catch((err) => {
    rawS3Inflight = null;
    throw err;
  });
  return rawS3Inflight;
};

const fetchAllObjects = async (prefix: string): Promise<S3FileItem[]> => {
  const items: S3FileItem[] = [];
  let continuationToken: string | undefined;

  do {
    const command = new ListObjectsV2Command({
      Bucket: config.aws.s3Bucket,
      Prefix: prefix,
      MaxKeys: 1000,
      ContinuationToken: continuationToken,
    });

    let output;
    try {
      output = await s3Client.send(command);
    } catch (err) {
      throw toAppError(err);
    }

    for (const obj of output.Contents ?? []) {
      if (!obj.Key) continue;
      const key = obj.Key;
      if (key.endsWith('/')) continue;

      const slashIdx = key.lastIndexOf('/');
      const name = slashIdx >= 0 ? key.slice(slashIdx + 1) : key;
      const objPrefix = slashIdx >= 0 ? key.slice(0, slashIdx + 1) : '';

      items.push({
        key,
        name,
        prefix: objPrefix,
        size: obj.Size ?? 0,
        lastModified: obj.LastModified?.toISOString() ?? new Date(0).toISOString(),
        etag: (obj.ETag ?? '').replace(/"/g, ''),
        pipelineName: derivePipelineName(name),
      });
    }

    continuationToken = output.IsTruncated ? output.NextContinuationToken : undefined;
  } while (continuationToken);

  return items;
};

const extractFolders = (items: S3FileItem[], rootPrefix: string): string[] => {
  const folderSet = new Set<string>();
  for (const item of items) {
    if (item.prefix && item.prefix !== rootPrefix) {
      const relative = item.prefix.startsWith(rootPrefix)
        ? item.prefix.slice(rootPrefix.length)
        : item.prefix;
      const parts = relative.split('/').filter(Boolean);
      let accumulated = rootPrefix;
      for (const part of parts) {
        accumulated += part + '/';
        folderSet.add(accumulated);
      }
    }
  }
  return Array.from(folderSet).sort();
};

export class S3Service {
  async listFiles(params: ListFilesParams): Promise<ListFilesResult> {
    const {
      prefix,
      search,
      startDate,
      endDate,
      page = 1,
      limit = 20,
      sortBy = 'lastModified',
      sortOrder = 'desc',
    } = params;

    const rootPrefix = config.aws.s3Prefix ?? '';
    const fetchPrefix = prefix && prefix !== rootPrefix ? prefix : rootPrefix;

    logger.info('S3 list files', { fetchPrefix, search, startDate, endDate });

    let items = await fetchAllObjects(fetchPrefix);

    const folders = extractFolders(items, rootPrefix);
    const suggestions = extractSuggestions(items);

    if (prefix && prefix !== rootPrefix) {
      items = items.filter((f) => f.key.startsWith(prefix));
    }

    if (search?.trim()) {
      const q = search.trim().toLowerCase();
      items = items.filter((f) => f.name.toLowerCase().includes(q));
    }

    if (startDate) {
      const from = new Date(startDate).getTime();
      items = items.filter((f) => new Date(f.lastModified).getTime() >= from);
    }
    if (endDate) {
      const to = new Date(endDate);
      to.setHours(23, 59, 59, 999);
      items = items.filter((f) => new Date(f.lastModified).getTime() <= to.getTime());
    }

    items.sort((a, b) => {
      let cmp = 0;
      if (sortBy === 'name') cmp = a.name.localeCompare(b.name);
      else if (sortBy === 'size') cmp = a.size - b.size;
      else cmp = new Date(a.lastModified).getTime() - new Date(b.lastModified).getTime();
      return sortOrder === 'asc' ? cmp : -cmp;
    });

    const total = items.length;
    const totalSize = items.reduce((sum, f) => sum + f.size, 0);
    const totalPages = Math.ceil(total / limit);
    const offset = (page - 1) * limit;
    const paged = items.slice(offset, offset + limit);

    return {
      files: paged,
      suggestions,
      total,
      totalSize,
      page,
      limit,
      totalPages,
      folders,
      bucket: config.aws.s3Bucket,
      rootPrefix,
    };
  }

  async streamFile(key: string, res: Response): Promise<void> {
    const command = new GetObjectCommand({
      Bucket: config.aws.s3Bucket,
      Key: key,
    });

    let output;
    try {
      output = await s3Client.send(command);
    } catch (err) {
      throw toAppError(err);
    }

    if (!output.Body) throw new AppError(404, 'File body is empty');

    const filename = key.split('/').pop() ?? 'download';
    res.setHeader('Content-Disposition', `attachment; filename="${encodeURIComponent(filename)}"`);
    res.setHeader('Content-Type', output.ContentType ?? 'application/octet-stream');
    if (output.ContentLength) res.setHeader('Content-Length', output.ContentLength);

    (output.Body as Readable).pipe(res);
  }

  async getPipelineNames(): Promise<string[]> {
    const seen = new Set<string>();
    const pattern = /^(.+?)_\d{8,}/;
    let continuationToken: string | undefined;

    // Scan the entire bucket with no size cap — only pipeline names are kept in memory
    do {
      const command = new ListObjectsV2Command({
        Bucket: config.aws.s3Bucket,
        Prefix: config.aws.s3Prefix ?? '',
        MaxKeys: 1000,
        ContinuationToken: continuationToken,
      });

      let output;
      try {
        output = await s3Client.send(command);
      } catch (err) {
        throw toAppError(err);
      }

      for (const obj of output.Contents ?? []) {
        if (!obj.Key || obj.Key.endsWith('/')) continue;
        const slashIdx = obj.Key.lastIndexOf('/');
        const name = slashIdx >= 0 ? obj.Key.slice(slashIdx + 1) : obj.Key;
        const match = name.match(pattern);
        if (match) seen.add(match[1]);
      }

      continuationToken = output.IsTruncated ? output.NextContinuationToken : undefined;
    } while (continuationToken);

    return Array.from(seen).sort();
  }

  async retriggerFile(
    fileName: string,
    s3Key: string,
    pipelineNameOverride?: string
  ): Promise<RetriggerResult> {
    const pipelineName = pipelineNameOverride ?? derivePipelineName(fileName);
    if (!pipelineName) throw new AppError(400, `Cannot derive pipeline name from file: ${fileName}`);

    const fileDate = extractDateFromFilename(fileName);
    const dateStr = fileDate ?? new Date().toISOString().slice(0, 10);

    const jobId = randomUUID();

    await query(
      `INSERT INTO recovery_jobs
         (id, pipeline_name, start_date, end_date, files_found,
          files_processed, files_failed, status, triggered_by)
       VALUES ($1, $2, $3, $4, 1, 0, 0, 'Pending', 'S3 Files Page')`,
      [jobId, pipelineName, dateStr, dateStr]
    );

    // Auto-create pipeline definition if it doesn't exist
    await queryOne(
      `INSERT INTO pipelines (id, name, display_name, is_active)
       VALUES ($1, $2, $3, true)
       ON CONFLICT (name) DO NOTHING`,
      [
        randomUUID(),
        pipelineName,
        pipelineName.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase()),
      ]
    );

    logger.info('Retrigger queued from S3 Files page', { jobId, pipelineName, fileName, s3Key });

    return { jobId, pipelineName, status: 'queued' };
  }

  async getFileNamesForDateRange(startDate?: string, endDate?: string): Promise<{ name: string; lastModified: string }[]> {
    // Uses the 60-second cache — safe to call from dashboard + file monitor in the
    // same session without triggering duplicate S3 API round-trips.
    let items = await fetchAllObjectsCached(config.aws.s3Prefix ?? '');
    if (startDate) {
      const from = new Date(startDate).getTime();
      items = items.filter((f) => new Date(f.lastModified).getTime() >= from);
    }
    if (endDate) {
      const to = new Date(endDate);
      to.setHours(23, 59, 59, 999);
      items = items.filter((f) => new Date(f.lastModified).getTime() <= to.getTime());
    }
    return items.map((f) => ({ name: f.name, lastModified: f.lastModified }));
  }
}

export const s3Service = new S3Service();
