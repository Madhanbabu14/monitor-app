import { api, extractData } from './api';
import { S3FilesResult } from '../types';

export interface S3ListParams {
  prefix?: string;
  search?: string;
  startDate?: string;
  endDate?: string;
  page?: number;
  limit?: number;
  sortBy?: 'name' | 'size' | 'lastModified';
  sortOrder?: 'asc' | 'desc';
}

export interface RetriggerResult {
  jobId: string;
  pipelineName: string;
  status: 'queued';
}

export const s3Service = {
  listFiles: (params: S3ListParams) =>
    api
      .get<{ data: S3FilesResult }>('/s3/files', { params })
      .then(extractData),

  getPipelineNames: (): Promise<string[]> =>
    api
      .get<{ data: string[] }>('/s3/pipeline-names')
      .then((r) => r.data.data),

  retriggerFile: (
    fileName: string,
    s3Key: string,
    pipelineName?: string
  ): Promise<RetriggerResult> =>
    api
      .post<{ data: RetriggerResult }>('/s3/retrigger', { fileName, s3Key, pipelineName })
      .then((r) => r.data.data),

  downloadFile: async (key: string, filename: string): Promise<void> => {
    const response = await api.get('/s3/download', {
      params: { key },
      responseType: 'blob',
      timeout: 300_000,
    });
    const blob = new Blob([response.data as BlobPart]);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  },
};
