import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Card, CardContent, CardHeader, TextField, InputAdornment,
  Button, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  TablePagination, TableSortLabel, CircularProgress, Alert, Stack, Chip,
  Tooltip, IconButton, FormControl, InputLabel, Select, MenuItem,
} from '@mui/material';
import {
  Search, Refresh, Download, InsertDriveFile, FilterList,
  CloudQueue, Lock, WifiOff, ErrorOutline,
} from '@mui/icons-material';
import { format, subDays } from 'date-fns';
import { s3Service, S3ListParams } from '../../services/s3.service';
import { S3FileItem } from '../../types';

type SortField = 'name' | 'size' | 'lastModified';
type SortOrder = 'asc' | 'desc';

interface FilterState {
  search: string;
  prefix: string;
  startDate: string;
  endDate: string;
}

const DEFAULT_FILTERS: FilterState = {
  search: '',
  prefix: '',
  startDate: format(subDays(new Date(), 30), 'yyyy-MM-dd'),
  endDate: format(new Date(), 'yyyy-MM-dd'),
};

const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`;
};

const getErrorDetails = (err: unknown): { icon: React.ReactNode; title: string; body: string } => {
  const msg = String((err as { message?: string })?.message ?? '').toLowerCase();
  if (msg.includes('403') || msg.includes('access denied') || msg.includes('credentials'))
    return { icon: <Lock color="error" sx={{ fontSize: 40 }} />, title: 'Access Denied', body: 'AWS credentials or bucket permissions are invalid. Contact your administrator.' };
  if (msg.includes('404') || msg.includes('not found') || msg.includes('bucket'))
    return { icon: <CloudQueue color="warning" sx={{ fontSize: 40 }} />, title: 'Bucket Not Found', body: 'The configured S3 bucket does not exist or is in a different region.' };
  if (msg.includes('network') || msg.includes('timeout') || msg.includes('econnrefused'))
    return { icon: <WifiOff color="error" sx={{ fontSize: 40 }} />, title: 'Network Error', body: 'Unable to reach the backend. Ensure the server is running.' };
  return { icon: <ErrorOutline color="error" sx={{ fontSize: 40 }} />, title: 'S3 Error', body: String((err as { message?: string })?.message ?? 'An unexpected error occurred') };
};

export default function S3FilesPage() {
  const [draft, setDraft]     = useState<FilterState>(DEFAULT_FILTERS);
  const [applied, setApplied] = useState<FilterState>(DEFAULT_FILTERS);
  const [page, setPage]               = useState(0);
  const [rowsPerPage, setRowsPerPage] = useState(20);
  const [sortBy, setSortBy]           = useState<SortField>('lastModified');
  const [sortOrder, setSortOrder]     = useState<SortOrder>('desc');
  const [downloadingKey, setDownloadingKey] = useState<string | null>(null);

  const queryParams: S3ListParams = {
    prefix:    applied.prefix || undefined,
    search:    applied.search || undefined,
    startDate: applied.startDate,
    endDate:   applied.endDate,
    page:      page + 1,
    limit:     rowsPerPage,
    sortBy,
    sortOrder,
  };

  const { data, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey: ['s3-files', queryParams],
    queryFn:  () => s3Service.listFiles(queryParams),
    staleTime: 60_000,
    retry: 1,
  });

  const { data: pipelineNames = [] } = useQuery({
    queryKey: ['s3-pipeline-names'],
    queryFn:  () => s3Service.getPipelineNames(),
    staleTime: 5 * 60_000,
  });

  const handleSearch = () => { setPage(0); setApplied({ ...draft }); };
  const handleKeyDown = (e: React.KeyboardEvent) => { if (e.key === 'Enter') handleSearch(); };

  const handleSort = (field: SortField) => {
    if (sortBy === field) setSortOrder((o) => (o === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(field); setSortOrder('asc'); }
    setPage(0);
  };

  const handleDownload = async (file: S3FileItem) => {
    setDownloadingKey(file.key);
    try { await s3Service.downloadFile(file.key, file.name); }
    finally { setDownloadingKey(null); }
  };

  const errorDetails = isError ? getErrorDetails(error) : null;

  return (
    <Box>
      {/* Header */}
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" mb={3}>
        <Box>
          <Typography variant="h4" fontWeight={700}>AWS S3 Files</Typography>
          <Typography variant="body2" color="text.secondary">
            Files ingested into the data pipeline from S3
          </Typography>
        </Box>
        <Button
          variant="outlined"
          startIcon={isFetching ? <CircularProgress size={14} /> : <Refresh />}
          onClick={() => refetch()}
          disabled={isFetching}
        >
          Refresh
        </Button>
      </Box>

      {/* Stats bar */}
      {data && (
        <Stack direction="row" spacing={2} mb={3} flexWrap="wrap" useFlexGap>
          <Chip icon={<InsertDriveFile fontSize="small" />} label={`${data.total.toLocaleString()} file${data.total !== 1 ? 's' : ''}`} color="primary" size="small" />
          <Chip label={`Total size: ${formatBytes(data.totalSize)}`} size="small" variant="outlined" />
        </Stack>
      )}

      {/* Filters */}
      <Card sx={{ mb: 3 }}>
        <CardContent sx={{ pb: '16px !important' }}>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="flex-end" flexWrap="wrap" useFlexGap>
            <TextField
              label="Search" placeholder="Search by file name..." size="small"
              value={draft.search} onChange={(e) => setDraft(d => ({ ...d, search: e.target.value }))}
              onKeyDown={handleKeyDown}
              InputProps={{ startAdornment: <InputAdornment position="start"><Search fontSize="small" /></InputAdornment> }}
              InputLabelProps={{ shrink: true }} sx={{ flex: 1, minWidth: 220 }}
            />
            <FormControl size="small" sx={{ minWidth: 180 }}>
              <InputLabel shrink>Pipeline</InputLabel>
              <Select value={draft.prefix} label="Pipeline" displayEmpty notched
                onChange={(e) => setDraft(d => ({ ...d, prefix: e.target.value }))}
                startAdornment={<InputAdornment position="start"><FilterList fontSize="small" /></InputAdornment>}
                MenuProps={{ PaperProps: { style: { maxHeight: 480 } } }}>
                <MenuItem value="">All pipelines</MenuItem>
                {pipelineNames.map(s => <MenuItem key={s} value={s} sx={{ fontFamily: 'monospace', fontSize: 13 }}>{s}</MenuItem>)}
              </Select>
            </FormControl>
            <TextField label="From" type="date" size="small" value={draft.startDate}
              onChange={(e) => setDraft(d => ({ ...d, startDate: e.target.value }))}
              onKeyDown={handleKeyDown} InputLabelProps={{ shrink: true }} sx={{ width: 155 }} />
            <TextField label="To" type="date" size="small" value={draft.endDate}
              onChange={(e) => setDraft(d => ({ ...d, endDate: e.target.value }))}
              onKeyDown={handleKeyDown} InputLabelProps={{ shrink: true }} sx={{ width: 155 }} />
            <Button variant="contained" startIcon={<Search fontSize="small" />}
              onClick={handleSearch} disabled={isFetching} sx={{ height: 40, px: 3 }}>
              Search
            </Button>
          </Stack>
        </CardContent>
      </Card>

      {/* Error state */}
      {isError && errorDetails && (
        <Card sx={{ mb: 3 }}>
          <CardContent>
            <Box display="flex" flexDirection="column" alignItems="center" py={4} gap={2}>
              {errorDetails.icon}
              <Typography variant="h6" fontWeight={600}>{errorDetails.title}</Typography>
              <Typography variant="body2" color="text.secondary" textAlign="center" maxWidth={480}>{errorDetails.body}</Typography>
              <Button variant="outlined" startIcon={<Refresh />} onClick={() => refetch()}>Retry</Button>
            </Box>
          </CardContent>
        </Card>
      )}

      {/* Loading */}
      {isLoading && (
        <Box display="flex" justifyContent="center" alignItems="center" py={8}>
          <CircularProgress />
          <Typography variant="body2" color="text.secondary" ml={2}>Fetching files from S3…</Typography>
        </Box>
      )}

      {/* File table */}
      {!isLoading && !isError && data && (
        <Card>
          <CardHeader
            title={
              <Box display="flex" alignItems="center" gap={1}>
                <Typography variant="h6" fontWeight={600}>Files</Typography>
                {isFetching && <CircularProgress size={16} />}
              </Box>
            }
            titleTypographyProps={{ component: 'div' } as object}
          />
          <CardContent sx={{ p: 0 }}>
            <TableContainer>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>
                      <TableSortLabel active={sortBy === 'name'} direction={sortBy === 'name' ? sortOrder : 'asc'} onClick={() => handleSort('name')}>
                        File Name
                      </TableSortLabel>
                    </TableCell>
                    <TableCell align="right">
                      <TableSortLabel active={sortBy === 'size'} direction={sortBy === 'size' ? sortOrder : 'asc'} onClick={() => handleSort('size')}>
                        Size
                      </TableSortLabel>
                    </TableCell>
                    <TableCell>
                      <TableSortLabel active={sortBy === 'lastModified'} direction={sortBy === 'lastModified' ? sortOrder : 'asc'} onClick={() => handleSort('lastModified')}>
                        Last Modified
                      </TableSortLabel>
                    </TableCell>
                    <TableCell>Pipeline</TableCell>
                    <TableCell align="center">Actions</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {data.files.length === 0 ? (
                    <TableRow>
                      <TableCell colSpan={5} align="center" sx={{ py: 6 }}>
                        <Box display="flex" flexDirection="column" alignItems="center" gap={1}>
                          <InsertDriveFile sx={{ fontSize: 40, opacity: 0.3 }} />
                          <Typography color="text.secondary">No files found matching the current filters</Typography>
                        </Box>
                      </TableCell>
                    </TableRow>
                  ) : data.files.map((file) => (
                    <TableRow key={file.key} hover>
                      <TableCell>
                        <Box display="flex" alignItems="center" gap={1}>
                          <InsertDriveFile fontSize="small" sx={{ opacity: 0.5, flexShrink: 0 }} />
                          <Tooltip title={file.key} placement="top">
                            <Typography variant="body2" fontWeight={500} sx={{ maxWidth: 340, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: 'monospace', fontSize: 13 }}>
                              {file.name}
                            </Typography>
                          </Tooltip>
                        </Box>
                      </TableCell>
                      <TableCell align="right">
                        <Typography variant="body2" color="text.secondary">{formatBytes(file.size)}</Typography>
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2" color="text.secondary">
                          {format(new Date(file.lastModified), 'MMM dd, yyyy HH:mm')}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2" sx={{ fontFamily: 'monospace', fontSize: 12 }}>
                          {file.pipelineName ?? '—'}
                        </Typography>
                      </TableCell>
                      <TableCell align="center">
                        <Tooltip title={`Download ${file.name}`}>
                          <span>
                            <IconButton size="small" color="primary" onClick={() => handleDownload(file)} disabled={downloadingKey === file.key}>
                              {downloadingKey === file.key ? <CircularProgress size={16} /> : <Download fontSize="small" />}
                            </IconButton>
                          </span>
                        </Tooltip>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
            <TablePagination
              component="div" count={data.total} page={page} rowsPerPage={rowsPerPage}
              onPageChange={(_, p) => setPage(p)}
              onRowsPerPageChange={(e) => { setRowsPerPage(parseInt(e.target.value, 10)); setPage(0); }}
              rowsPerPageOptions={[10, 20, 50, 100]}
            />
          </CardContent>
        </Card>
      )}

      {downloadingKey && (
        <Alert severity="info" sx={{ mt: 2 }}>
          Downloading <strong>{downloadingKey.split('/').pop()}</strong> from S3…
        </Alert>
      )}
    </Box>
  );
}
