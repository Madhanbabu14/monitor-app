import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Card, CardContent, CardHeader, Box, TextField, InputAdornment,
  Button, Typography, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TablePagination,
  CircularProgress, Select, MenuItem, FormControl, InputLabel,
} from '@mui/material';
import { Search, FileDownload, Refresh } from '@mui/icons-material';
import { useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { pipelineService } from '../../../services/pipeline.service';
import StatusChip from '../../../components/common/StatusChip';
import { PipelineRun, PipelineStatus } from '../../../types';
import * as XLSX from 'xlsx';
import { saveAs } from 'file-saver';
import { format } from 'date-fns';

function formatDuration(ms: number | null): string {
  if (!ms) return '-';
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.floor(ms / 60000)}m ${Math.floor((ms % 60000) / 1000)}s`;
}

interface PipelineTableProps {
  rows: PipelineRun[];
  total: number;
  loading: boolean;
}

export default function PipelineTable({ rows, total, loading }: PipelineTableProps) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(0);
  const [rowsPerPage, setRowsPerPage] = useState(20);
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState<PipelineStatus | ''>('');

  const { data, isFetching } = useQuery({
    queryKey: ['pipeline-runs', { page: page + 1, limit: rowsPerPage, search, status: statusFilter }],
    queryFn: () =>
      pipelineService.getRuns({
        page: page + 1,
        limit: rowsPerPage,
        search: search || undefined,
        status: statusFilter || undefined,
      }),
    placeholderData: keepPreviousData,
  });

  const tableRows = data?.data ?? rows;
  const tableTotal = data?.total ?? total;

  const exportToExcel = () => {
    const exportData = tableRows.map((r) => ({
      Pipeline: r.pipelineName,
      Status: r.status,
      'Start Time': r.startTime ? format(new Date(r.startTime), 'yyyy-MM-dd HH:mm:ss') : '-',
      'End Time': r.endTime ? format(new Date(r.endTime), 'yyyy-MM-dd HH:mm:ss') : '-',
      Duration: formatDuration(r.durationMs),
      'Files Processed': r.filesProcessed,
      'Rows Inserted': r.rowsInserted,
      'Rows Updated': r.rowsUpdated,
      'Run ID': r.runId,
    }));
    const ws = XLSX.utils.json_to_sheet(exportData);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Pipeline Runs');
    const buf = XLSX.write(wb, { type: 'array', bookType: 'xlsx' });
    saveAs(new Blob([buf]), `pipeline-runs-${format(new Date(), 'yyyyMMdd')}.xlsx`);
  };

  return (
    <Card>
      <CardHeader
        title="Pipeline Runs"
        titleTypographyProps={{ variant: 'h6', fontWeight: 600 }}
        action={
          <Box display="flex" gap={1}>
            <Button
              size="small"
              startIcon={<Refresh />}
              onClick={() => queryClient.invalidateQueries({ queryKey: ['pipeline-runs'] })}
            >
              Refresh
            </Button>
            <Button size="small" variant="outlined" startIcon={<FileDownload />} onClick={exportToExcel}>
              Export
            </Button>
          </Box>
        }
      />
      <CardContent sx={{ pt: 0 }}>
        <Box display="flex" gap={2} mb={2}>
          <TextField
            placeholder="Search pipeline..."
            size="small"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            InputProps={{ startAdornment: <InputAdornment position="start"><Search fontSize="small" /></InputAdornment> }}
            sx={{ flex: 1 }}
          />
          <FormControl size="small" sx={{ minWidth: 140 }}>
            <InputLabel>Status</InputLabel>
            <Select
              value={statusFilter}
              label="Status"
              onChange={(e) => setStatusFilter(e.target.value as PipelineStatus | '')}
            >
              <MenuItem value="">All</MenuItem>
              {(['Success', 'Failed', 'Running', 'Cancelled', 'Warning'] as PipelineStatus[]).map((s) => (
                <MenuItem key={s} value={s}>{s}</MenuItem>
              ))}
            </Select>
          </FormControl>
        </Box>

        {(loading || isFetching) && (
          <Box display="flex" justifyContent="center" py={2}>
            <CircularProgress size={24} />
          </Box>
        )}

        <TableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Pipeline</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Last Run</TableCell>
                <TableCell align="right">Duration</TableCell>
                <TableCell align="right">Files</TableCell>
                <TableCell align="right">Rows In</TableCell>
                <TableCell align="right">Rows Up</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {tableRows.map((row) => (
                <TableRow
                  key={row.id}
                  hover
                  sx={{ cursor: 'pointer' }}
                  onClick={() => navigate(`/pipelines/${row.id}`)}
                >
                  <TableCell>
                    <Typography variant="body2" fontWeight={500}>
                      {row.pipelineName}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <StatusChip status={row.status} />
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2" color="text.secondary">
                      {row.startTime
                        ? format(new Date(row.startTime), 'MMM dd, HH:mm')
                        : '-'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Typography variant="body2">{formatDuration(row.durationMs)}</Typography>
                  </TableCell>
                  <TableCell align="right">{row.filesProcessed.toLocaleString()}</TableCell>
                  <TableCell align="right">{row.rowsInserted.toLocaleString()}</TableCell>
                  <TableCell align="right">{row.rowsUpdated.toLocaleString()}</TableCell>
                </TableRow>
              ))}
              {tableRows.length === 0 && !loading && (
                <TableRow>
                  <TableCell colSpan={7} align="center" sx={{ py: 4, color: 'text.secondary' }}>
                    No pipeline runs found
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>

        <TablePagination
          component="div"
          count={tableTotal}
          page={page}
          rowsPerPage={rowsPerPage}
          onPageChange={(_, p) => setPage(p)}
          onRowsPerPageChange={(e) => { setRowsPerPage(parseInt(e.target.value, 10)); setPage(0); }}
          rowsPerPageOptions={[10, 20, 50, 100]}
        />
      </CardContent>
    </Card>
  );
}
