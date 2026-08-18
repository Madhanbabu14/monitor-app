import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import {
  Box, Typography, Card, CardContent, CardHeader, TextField, InputAdornment,
  Button, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  TablePagination, TableSortLabel, CircularProgress, Stack, Chip,
  FormControl, InputLabel, Select, MenuItem, Alert, Tooltip, Collapse, IconButton,
} from '@mui/material';
import {
  Search, Refresh, FilterList, FileDownload, InsertDriveFile,
  CheckCircle, Cancel, HourglassEmpty, RemoveCircleOutline,
  WifiOff, ExpandMore, ExpandLess,
} from '@mui/icons-material';
import { format, subDays } from 'date-fns';
import { monitorService, MonitorStatus, MonitorParams } from '../../services/monitor.service';

type SortField = 'fileName' | 'pipelineName' | 'fileReceivedDate' | 'recordsInserted' | 'recordsUpdated';

interface FilterState {
  search: string;
  pipeline: string;
  startDate: string;
  endDate: string;
  status: MonitorStatus | '';
}

const STATUS_OPTIONS: { value: MonitorStatus | ''; label: string }[] = [
  { value: '', label: 'All statuses' },
  { value: 'Processed', label: 'Processed' },
  { value: 'Failed', label: 'Failed' },
  { value: 'In Progress', label: 'In Progress' },
  { value: 'Not Processed', label: 'Not Processed' },
];

const STATUS_CONFIG: Record<MonitorStatus, { label: string; color: 'success' | 'error' | 'info' | 'warning'; icon: React.ReactElement }> = {
  Processed:       { label: 'Success',     color: 'success', icon: <CheckCircle fontSize="small" /> },
  Failed:          { label: 'Failed',      color: 'error',   icon: <Cancel fontSize="small" /> },
  'In Progress':   { label: 'In Progress', color: 'info',    icon: <HourglassEmpty fontSize="small" /> },
  'Not Processed': { label: 'Pending',     color: 'warning', icon: <RemoveCircleOutline fontSize="small" /> },
};

export default function FileMonitorPage() {
  const [searchParams] = useSearchParams();

  const getInitialFilters = (): FilterState => ({
    search:    searchParams.get('search')    || '',
    pipeline:  searchParams.get('pipeline')  || '',
    startDate: searchParams.get('startDate') || format(subDays(new Date(), 30), 'yyyy-MM-dd'),
    endDate:   searchParams.get('endDate')   || format(new Date(), 'yyyy-MM-dd'),
    status:    (searchParams.get('status')   || '') as MonitorStatus | '',
  });

  const [draft, setDraft]   = useState<FilterState>(() => getInitialFilters());
  const [applied, setApplied] = useState<FilterState>(() => getInitialFilters());
  const [page, setPage]           = useState(0);
  const [rowsPerPage, setRowsPerPage] = useState(20);
  const [sortBy, setSortBy]       = useState<SortField>('fileReceivedDate');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('desc');
  const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set());

  const queryParams: MonitorParams = {
    startDate: applied.startDate, endDate: applied.endDate,
    status: applied.status || undefined, pipeline: applied.pipeline || undefined,
    search: applied.search || undefined, page: page + 1,
    limit: rowsPerPage, sortBy, sortOrder,
  };

  const { data, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey: ['monitor-reconcile', queryParams],
    queryFn: () => monitorService.getReconciled(queryParams),
    staleTime: 30_000, retry: 1,
  });

  const { data: pipelineNames = [] } = useQuery({
    queryKey: ['monitor-pipeline-names'],
    queryFn: () => monitorService.getPipelineNames(),
    staleTime: 5 * 60_000,
  });

  const handleSearch = () => { setPage(0); setApplied({ ...draft }); };
  const handleKeyDown = (e: React.KeyboardEvent) => { if (e.key === 'Enter') handleSearch(); };

  const handleSort = (field: SortField) => {
    if (sortBy === field) setSortOrder((o) => (o === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(field); setSortOrder('desc'); }
    setPage(0);
  };

  const toggleExpand = (key: string) => {
    setExpandedRows((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  };

  const exportToCSV = () => {
    if (!data?.data.length) return;
    const headers = ['File Name','Pipeline','File Received Date','Records Inserted','Records Updated','Total Records','File Processed','Pipeline Status','Error Message'];
    const csvRows = data.data.map((r) => [
      r.fileName, r.pipelineName,
      r.fileReceivedDate ? format(new Date(r.fileReceivedDate), 'yyyy-MM-dd HH:mm:ss') : '',
      r.recordsInserted, r.recordsUpdated, r.totalRecords,
      r.fileProcessed ? 'Yes' : 'No', r.processStatus, r.errorMessage ?? '',
    ]);
    const escape = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`;
    const csv = [headers, ...csvRows].map((row) => row.map(escape).join(',')).join('\r\n');
    const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `file-monitor-${format(new Date(), 'yyyyMMdd-HHmm')}.csv`;
    document.body.appendChild(a); a.click(); document.body.removeChild(a);
    URL.revokeObjectURL(url);
  };

  return (
    <Box>
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" mb={3}>
        <Box>
          <Typography variant="h4" fontWeight={700}>Pipeline File Monitor</Typography>
          <Typography variant="body2" color="text.secondary">
            File-level processing status from the production pipeline database
          </Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          <Tooltip title="Exports the current page only. Narrow filters before exporting to get the rows you need.">
            <span>
              <Button variant="outlined" startIcon={<FileDownload />} onClick={exportToCSV} disabled={!data?.data.length} size="small">
                Export Page (CSV)
              </Button>
            </span>
          </Tooltip>
          <Button variant="outlined" startIcon={isFetching ? <CircularProgress size={14} /> : <Refresh />} onClick={() => refetch()} disabled={isFetching} size="small">
            Refresh
          </Button>
        </Stack>
      </Box>

      {data && data.total > 0 && (
        <Stack direction="row" spacing={2} mb={3} flexWrap="wrap" useFlexGap>
          <Chip icon={<InsertDriveFile fontSize="small" />} label={`${data.total.toLocaleString()} files`} color="primary" size="small" />
          <Chip label={`${data.statusBreakdown.processed.toLocaleString()} processed`} color="success" size="small" variant="outlined" />
          <Chip label={`${data.statusBreakdown.failed.toLocaleString()} failed`} color="error" size="small" variant="outlined" />
        </Stack>
      )}

      <Card sx={{ mb: 3 }}>
        <CardContent sx={{ pb: '16px !important' }}>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="flex-end" flexWrap="wrap" useFlexGap>
            <TextField
              label="File Name" placeholder="Search by file name..." size="small"
              value={draft.search} onChange={(e) => setDraft(d => ({ ...d, search: e.target.value }))}
              onKeyDown={handleKeyDown}
              InputProps={{ startAdornment: <InputAdornment position="start"><Search fontSize="small" /></InputAdornment> }}
              InputLabelProps={{ shrink: true }} sx={{ flex: 1, minWidth: 220 }}
            />
            <FormControl size="small" sx={{ minWidth: 180 }}>
              <InputLabel shrink>Pipeline</InputLabel>
              <Select value={draft.pipeline} label="Pipeline" displayEmpty notched
                onChange={(e) => setDraft(d => ({ ...d, pipeline: e.target.value }))}
                startAdornment={<InputAdornment position="start"><FilterList fontSize="small" /></InputAdornment>}
                MenuProps={{ PaperProps: { style: { maxHeight: 480 } } }}>
                <MenuItem value="">All pipelines</MenuItem>
                {pipelineNames.map(n => <MenuItem key={n} value={n} sx={{ fontFamily: 'monospace', fontSize: 13 }}>{n}</MenuItem>)}
              </Select>
            </FormControl>
            <TextField label="From" type="date" size="small" value={draft.startDate}
              onChange={(e) => setDraft(d => ({ ...d, startDate: e.target.value }))}
              onKeyDown={handleKeyDown} InputLabelProps={{ shrink: true }} sx={{ width: 155 }} />
            <TextField label="To" type="date" size="small" value={draft.endDate}
              onChange={(e) => setDraft(d => ({ ...d, endDate: e.target.value }))}
              onKeyDown={handleKeyDown} InputLabelProps={{ shrink: true }} sx={{ width: 155 }} />
            <FormControl size="small" sx={{ minWidth: 170 }}>
              <InputLabel shrink>Pipeline Status</InputLabel>
              <Select value={draft.status} label="Pipeline Status" displayEmpty notched
                onChange={(e) => setDraft(d => ({ ...d, status: e.target.value as MonitorStatus | '' }))}>
                {STATUS_OPTIONS.map(o => <MenuItem key={o.value} value={o.value}>{o.label}</MenuItem>)}
              </Select>
            </FormControl>
            <Button variant="contained" startIcon={<Search fontSize="small" />}
              onClick={handleSearch} disabled={isFetching} sx={{ height: 40, px: 3 }}>
              Search
            </Button>
          </Stack>
        </CardContent>
      </Card>

      {data && !data.dbAvailable && (
        <Alert severity="warning" icon={<WifiOff />} sx={{ mb: 3 }}
          action={<Button color="inherit" size="small" onClick={() => refetch()}>Retry</Button>}>
          <strong>Production database unreachable.</strong> Check VPN connection and database credentials, then click Retry.
        </Alert>
      )}
      {isError && (
        <Alert severity="error" sx={{ mb: 3 }}
          action={<Button color="inherit" size="small" onClick={() => refetch()}>Retry</Button>}>
          {String((error as { message?: string })?.message ?? 'Failed to load data.')}
        </Alert>
      )}

      {isLoading && (
        <Box display="flex" justifyContent="center" alignItems="center" py={8}>
          <CircularProgress />
          <Typography variant="body2" color="text.secondary" ml={2}>Loading file processing data…</Typography>
        </Box>
      )}

      {!isLoading && !isError && data && (
        <Card>
          <CardHeader
            title={
              <Box display="flex" alignItems="center" gap={1}>
                <Typography variant="h6" fontWeight={600}>File Processing Records</Typography>
                {isFetching && <CircularProgress size={16} />}
              </Box>
            }
            titleTypographyProps={{ component: 'div' } as object}
          />
          <CardContent sx={{ p: 0 }}>
            <TableContainer sx={{ overflowX: 'auto' }}>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ minWidth: 280 }}>
                      <TableSortLabel active={sortBy === 'fileName'} direction={sortBy === 'fileName' ? sortOrder : 'asc'} onClick={() => handleSort('fileName')}>File Name</TableSortLabel>
                    </TableCell>
                    <TableCell sx={{ minWidth: 130 }}>
                      <TableSortLabel active={sortBy === 'pipelineName'} direction={sortBy === 'pipelineName' ? sortOrder : 'asc'} onClick={() => handleSort('pipelineName')}>Pipeline</TableSortLabel>
                    </TableCell>
                    <TableCell sx={{ minWidth: 160 }}>
                      <TableSortLabel active={sortBy === 'fileReceivedDate'} direction={sortBy === 'fileReceivedDate' ? sortOrder : 'asc'} onClick={() => handleSort('fileReceivedDate')}>File Received Date</TableSortLabel>
                    </TableCell>
                    <TableCell align="center" sx={{ minWidth: 110 }}>File Processed</TableCell>
                    <TableCell align="right" sx={{ minWidth: 110 }}>
                      <TableSortLabel active={sortBy === 'recordsInserted'} direction={sortBy === 'recordsInserted' ? sortOrder : 'asc'} onClick={() => handleSort('recordsInserted')}>Records Inserted</TableSortLabel>
                    </TableCell>
                    <TableCell align="right" sx={{ minWidth: 110 }}>
                      <TableSortLabel active={sortBy === 'recordsUpdated'} direction={sortBy === 'recordsUpdated' ? sortOrder : 'asc'} onClick={() => handleSort('recordsUpdated')}>Records Updated</TableSortLabel>
                    </TableCell>
                    <TableCell align="right" sx={{ minWidth: 100 }}>Total Records</TableCell>
                    <TableCell sx={{ minWidth: 130 }}>Pipeline Status</TableCell>
                    <TableCell sx={{ width: 48 }} />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {data.data.length === 0 ? (
                    <TableRow>
                      <TableCell colSpan={9} align="center" sx={{ py: 8 }}>
                        <Box display="flex" flexDirection="column" alignItems="center" gap={1}>
                          <InsertDriveFile sx={{ fontSize: 48, opacity: 0.2 }} />
                          <Typography color="text.secondary">No records found for the selected filters</Typography>
                        </Box>
                      </TableCell>
                    </TableRow>
                  ) : data.data.map((row, idx) => {
                    const cfg = STATUS_CONFIG[row.processStatus];
                    const key = row.fileName + idx;
                    const isExpanded = expandedRows.has(key);
                    return (
                      <React.Fragment key={key}>
                        <TableRow hover sx={{ '& td': { borderBottom: isExpanded ? 'none' : undefined } }}>
                          <TableCell>
                            <Box display="flex" alignItems="center" gap={1}>
                              <InsertDriveFile fontSize="small" sx={{ opacity: 0.4, flexShrink: 0 }} />
                              <Tooltip title={row.fileName} placement="top">
                                <Typography variant="body2" fontWeight={500} sx={{ maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: 'monospace', fontSize: 12 }}>
                                  {row.fileName}
                                </Typography>
                              </Tooltip>
                            </Box>
                          </TableCell>
                          <TableCell><Typography variant="body2" sx={{ fontFamily: 'monospace', fontSize: 12 }}>{row.pipelineName}</Typography></TableCell>
                          <TableCell><Typography variant="body2" color="text.secondary">{row.fileReceivedDate ? format(new Date(row.fileReceivedDate), 'MMM dd, yyyy HH:mm') : '—'}</Typography></TableCell>
                          <TableCell align="center">
                            <Chip label={row.fileProcessed ? 'Yes' : 'No'} color={row.fileProcessed ? 'success' : 'default'} size="small" variant={row.fileProcessed ? 'filled' : 'outlined'} />
                          </TableCell>
                          <TableCell align="right"><Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{row.recordsInserted > 0 ? row.recordsInserted.toLocaleString() : '—'}</Typography></TableCell>
                          <TableCell align="right"><Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{row.recordsUpdated > 0 ? row.recordsUpdated.toLocaleString() : '—'}</Typography></TableCell>
                          <TableCell align="right"><Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{row.totalRecords > 0 ? row.totalRecords.toLocaleString() : '—'}</Typography></TableCell>
                          <TableCell>
                            <Chip icon={cfg.icon} label={cfg.label} color={cfg.color} size="small" variant={row.processStatus === 'Not Processed' ? 'outlined' : 'filled'} />
                          </TableCell>
                          <TableCell>
                            {row.processStatus === 'Failed' && row.errorMessage && (
                              <Tooltip title={isExpanded ? 'Hide error' : 'Show error'}>
                                <IconButton size="small" onClick={() => toggleExpand(key)}>
                                  {isExpanded ? <ExpandLess fontSize="small" /> : <ExpandMore fontSize="small" />}
                                </IconButton>
                              </Tooltip>
                            )}
                          </TableCell>
                        </TableRow>
                        {row.processStatus === 'Failed' && row.errorMessage && (
                          <TableRow>
                            <TableCell colSpan={9} sx={{ py: 0, px: 0 }}>
                              <Collapse in={isExpanded} timeout="auto" unmountOnExit>
                                <Box sx={{ px: 3, py: 1.5, bgcolor: 'error.main', opacity: 0.92 }}>
                                  <Typography variant="caption" sx={{ color: 'error.contrastText', opacity: 0.7, display: 'block', mb: 0.5 }}>Failure Reason</Typography>
                                  <Typography variant="body2" sx={{ color: 'error.contrastText', fontFamily: 'monospace', fontSize: 12, wordBreak: 'break-word' }}>{row.errorMessage}</Typography>
                                </Box>
                              </Collapse>
                            </TableCell>
                          </TableRow>
                        )}
                      </React.Fragment>
                    );
                  })}
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
    </Box>
  );
}
