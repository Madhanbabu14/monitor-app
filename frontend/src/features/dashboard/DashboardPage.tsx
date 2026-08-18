import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Card, CardContent, CardHeader, Grid, Stack, Chip, Button,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  Skeleton, Alert, Tooltip, LinearProgress, Divider,
} from '@mui/material';
import {
  Refresh, WifiOff, CheckCircle, Cancel, HourglassEmpty, InsertDriveFile,
  TrendingUp, TrendingDown, ArrowUpward, ArrowDownward, AccessTime,
  Storage, BarChart, Assignment,
} from '@mui/icons-material';
import {
  AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip as RechartTooltip,
  Legend, ResponsiveContainer, PieChart, Pie, Cell,
} from 'recharts';
import { format, subDays, startOfMonth, startOfDay, subDays as sub } from 'date-fns';
import { monitorService } from '../../services/monitor.service';

// ─── Period helpers ───────────────────────────────────────────────────────────
type Period = 'yesterday' | 'last7' | 'last30' | 'month' | 'custom';

const PERIODS: { value: Period; label: string }[] = [
  { value: 'yesterday', label: 'Yesterday' },
  { value: 'last7',     label: 'Last 7 Days' },
  { value: 'last30',    label: 'Last 30 Days' },
  { value: 'month',     label: 'This Month' },
  { value: 'custom',    label: 'Custom' },
];

function getDateRange(period: Period, customStart: string, customEnd: string) {
  const today = format(new Date(), 'yyyy-MM-dd');
  switch (period) {
    case 'yesterday': { const y = format(subDays(new Date(), 1), 'yyyy-MM-dd'); return { startDate: y, endDate: y }; }
    case 'last7':     return { startDate: format(subDays(new Date(), 7), 'yyyy-MM-dd'), endDate: today };
    case 'last30':    return { startDate: format(subDays(new Date(), 30), 'yyyy-MM-dd'), endDate: today };
    case 'month':     return { startDate: format(startOfMonth(new Date()), 'yyyy-MM-dd'), endDate: today };
    case 'custom':    return { startDate: customStart || format(subDays(new Date(), 30), 'yyyy-MM-dd'), endDate: customEnd || today };
  }
}

// ─── Chart colours ────────────────────────────────────────────────────────────
const C = { processed: '#22c55e', failed: '#ef4444', inProgress: '#3b82f6', notProcessed: '#f59e0b' };

const PIE_COLOURS = [C.processed, C.failed, C.inProgress, C.notProcessed];

// ─── Status chip ──────────────────────────────────────────────────────────────
function PipelineStatusChip({ status }: { status: 'healthy' | 'warning' | 'failed' }) {
  if (status === 'healthy') return <Chip icon={<CheckCircle />} label="Healthy" color="success" size="small" />;
  if (status === 'failed')  return <Chip icon={<Cancel />} label="Failed" color="error" size="small" />;
  return <Chip icon={<HourglassEmpty />} label="Warning" color="warning" size="small" />;
}

// ─── KPI Card ─────────────────────────────────────────────────────────────────
interface KpiCardProps {
  label: string; value: string | number; icon: React.ReactElement;
  iconBg: string; valueColor?: string; subtitle?: string;
  loading: boolean; onClick?: () => void;
}
function KpiCard({ label, value, icon, iconBg, valueColor, subtitle, loading, onClick }: KpiCardProps) {
  return (
    <Card
      sx={{ height: '100%', cursor: onClick ? 'pointer' : 'default', transition: 'box-shadow .15s', '&:hover': onClick ? { boxShadow: 6 } : {} }}
      onClick={onClick}
    >
      <CardContent>
        <Box display="flex" justifyContent="space-between" alignItems="flex-start">
          <Box flex={1}>
            <Typography variant="body2" color="text.secondary" gutterBottom>{label}</Typography>
            {loading ? <Skeleton width={90} height={44} /> : (
              <Typography variant="h4" fontWeight={700} color={valueColor ?? 'text.primary'} lineHeight={1}>{value}</Typography>
            )}
            {subtitle && !loading && (
              <Typography variant="caption" color="text.secondary" mt={0.5} display="block">{subtitle}</Typography>
            )}
          </Box>
          <Box sx={{ p: 1.5, borderRadius: 2, bgcolor: iconBg, ml: 1, flexShrink: 0, display: 'flex' }}>
            {React.cloneElement(icon, { sx: { fontSize: 22, color: '#fff' } } as object)}
          </Box>
        </Box>
      </CardContent>
    </Card>
  );
}

// ─── Skeleton section ─────────────────────────────────────────────────────────
function SectionSkeleton({ height = 200 }: { height?: number }) {
  return <Skeleton variant="rectangular" height={height} sx={{ borderRadius: 2 }} />;
}

// ─── Main Page ────────────────────────────────────────────────────────────────
export default function DashboardPage() {
  const navigate   = useNavigate();
  const [period, setPeriod]       = useState<Period>('yesterday');
  const [customStart, setCustomStart] = useState('');
  const [customEnd,   setCustomEnd]   = useState('');

  const { startDate, endDate } = getDateRange(period, customStart, customEnd);

  // Fast query — DB metrics only (no S3 wait)
  const { data, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey: ['dashboard', startDate, endDate],
    queryFn: () => monitorService.getDashboard(startDate, endDate),
    staleTime: 60_000,
    retry: 1,
  });

  // Background query — S3 Not Processed count (independent, may take longer)
  const { data: notProcData, isLoading: notProcLoading, refetch: refetchNotProc, isFetching: isFetchingNotProc } = useQuery({
    queryKey: ['not-processed-count', startDate, endDate],
    queryFn: () => monitorService.getNotProcessedCount(startDate, endDate),
    staleTime: 60_000,
    retry: 1,
  });

  const goMonitor = (params: Record<string, string>) =>
    navigate(`/file-monitor?${new URLSearchParams({ startDate, endDate, ...params }).toString()}`);

  const kpi = data?.kpis;

  const kpiCards: KpiCardProps[] = [
    { label: 'Total Files',       value: kpi?.totalFiles.toLocaleString()    ?? 0,  icon: <InsertDriveFile />, iconBg: '#3b82f6', loading: isLoading, onClick: () => goMonitor({}) },
    { label: 'Processed',         value: kpi?.processed.toLocaleString()     ?? 0,  icon: <CheckCircle />,     iconBg: '#22c55e', valueColor: 'success.main', loading: isLoading, onClick: () => goMonitor({ status: 'Processed' }) },
    { label: 'Failed',            value: kpi?.failed.toLocaleString()        ?? 0,  icon: <Cancel />,          iconBg: '#ef4444', valueColor: 'error.main',   loading: isLoading, onClick: () => goMonitor({ status: 'Failed' }) },
    { label: 'Not Processed',     value: (notProcData?.count ?? 0).toLocaleString(), icon: <HourglassEmpty />,  iconBg: '#f59e0b', valueColor: 'warning.main', loading: notProcLoading, onClick: () => goMonitor({ status: 'Not Processed' }) },
    { label: 'Rows Inserted',     value: kpi?.rowsInserted.toLocaleString()  ?? 0,  icon: <ArrowUpward />,     iconBg: '#8b5cf6', loading: isLoading },
    { label: 'Rows Updated',      value: kpi?.rowsUpdated.toLocaleString()   ?? 0,  icon: <ArrowDownward />,   iconBg: '#6366f1', loading: isLoading },
    { label: 'Success Rate',      value: kpi ? `${kpi.successRate}%` : '—',         icon: <TrendingUp />,      iconBg: '#10b981', valueColor: 'success.main', loading: isLoading },
    { label: 'Failure Rate',      value: kpi ? `${kpi.failureRate}%` : '—',         icon: <TrendingDown />,    iconBg: '#f43f5e', valueColor: kpi && kpi.failureRate > 10 ? 'error.main' : 'text.primary', loading: isLoading },
  ];

  const pieData = kpi ? [
    { name: 'Processed',     value: kpi.processed },
    { name: 'Failed',        value: kpi.failed },
    { name: 'In Progress',   value: kpi.inProgress },
    { name: 'Not Processed', value: notProcData?.count ?? 0 },
  ].filter(d => d.value > 0) : [];

  const trendData = (data?.trend ?? []).map(t => ({
    ...t,
    period: (() => {
      try { return format(new Date(t.period), 'MMM dd'); } catch { return t.period; }
    })(),
  }));

  return (
    <Box>
      {/* ── Header ── */}
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" mb={3}>
        <Box>
          <Typography variant="h4" fontWeight={700}>Pipeline Operations Dashboard</Typography>
          <Typography variant="body2" color="text.secondary">
            Real-time health overview of all data pipelines
          </Typography>
        </Box>
        <Button
          variant="outlined" size="small"
          startIcon={(isFetching || isFetchingNotProc) ? undefined : <Refresh />}
          onClick={() => { refetch(); refetchNotProc(); }}
          disabled={isFetching || isFetchingNotProc}
        >
          {(isFetching || isFetchingNotProc) ? 'Refreshing…' : 'Refresh'}
        </Button>
      </Box>

      {/* ── Timeline filter ── */}
      <Card sx={{ mb: 3 }}>
        <CardContent sx={{ pb: '16px !important' }}>
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap alignItems="center">
            <Typography variant="body2" color="text.secondary" sx={{ mr: 1 }}>Period:</Typography>
            {PERIODS.map(p => (
              <Button
                key={p.value}
                size="small"
                variant={period === p.value ? 'contained' : 'outlined'}
                onClick={() => setPeriod(p.value)}
                sx={{ minWidth: 90 }}
              >
                {p.label}
              </Button>
            ))}
            {period === 'custom' && (
              <>
                <Box sx={{ width: 1, display: { xs: 'block', sm: 'none' } }} />
                <input type="date" value={customStart} onChange={e => setCustomStart(e.target.value)}
                  style={{ padding: '6px 10px', borderRadius: 6, border: '1px solid #ccc', fontSize: 13 }} />
                <Typography variant="body2" color="text.secondary">to</Typography>
                <input type="date" value={customEnd} onChange={e => setCustomEnd(e.target.value)}
                  style={{ padding: '6px 10px', borderRadius: 6, border: '1px solid #ccc', fontSize: 13 }} />
              </>
            )}
          </Stack>
        </CardContent>
      </Card>

      {/* ── DB / network alerts ── */}
      {data && !data.dbAvailable && (
        <Alert severity="warning" icon={<WifiOff />} sx={{ mb: 3 }}
          action={<Button color="inherit" size="small" onClick={() => refetch()}>Retry</Button>}>
          <strong>Production database unreachable.</strong> Check VPN connection and database credentials, then click Retry.
        </Alert>
      )}
      {isError && (
        <Alert severity="error" sx={{ mb: 3 }}
          action={<Button color="inherit" size="small" onClick={() => refetch()}>Retry</Button>}>
          {String((error as { message?: string })?.message ?? 'Failed to load dashboard data.')}
        </Alert>
      )}

      {/* ── KPI cards (2 rows × 4) ── */}
      <Grid container spacing={2} sx={{ mb: 3 }}>
        {kpiCards.map(card => (
          <Grid item xs={12} sm={6} md={3} key={card.label}>
            <KpiCard {...card} />
          </Grid>
        ))}
      </Grid>

      {/* ── Charts row ── */}
      <Grid container spacing={2} sx={{ mb: 3 }}>
        {/* Trend chart */}
        <Grid item xs={12} md={8}>
          <Card sx={{ height: '100%' }}>
            <CardHeader
              title={<Typography variant="h6" fontWeight={600}>Processing Trend</Typography>}
              subheader={<Typography variant="caption" color="text.secondary">Files processed vs failed over time</Typography>}
              action={<BarChart sx={{ opacity: 0.3, mt: 1, mr: 1 }} />}
            />
            <CardContent>
              {isLoading ? <SectionSkeleton height={220} /> : trendData.length === 0 ? (
                <Box display="flex" justifyContent="center" alignItems="center" height={220}>
                  <Typography color="text.secondary" variant="body2">No trend data for selected period</Typography>
                </Box>
              ) : (
                <ResponsiveContainer width="100%" height={220}>
                  <AreaChart data={trendData} margin={{ top: 4, right: 16, left: -20, bottom: 0 }}>
                    <defs>
                      <linearGradient id="gradProc" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="5%" stopColor={C.processed} stopOpacity={0.3} />
                        <stop offset="95%" stopColor={C.processed} stopOpacity={0} />
                      </linearGradient>
                      <linearGradient id="gradFail" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="5%" stopColor={C.failed} stopOpacity={0.3} />
                        <stop offset="95%" stopColor={C.failed} stopOpacity={0} />
                      </linearGradient>
                    </defs>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(128,128,128,0.15)" />
                    <XAxis dataKey="period" tick={{ fontSize: 11 }} tickLine={false} />
                    <YAxis tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
                    <RechartTooltip contentStyle={{ borderRadius: 8, fontSize: 13 }} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                    <Area type="monotone" dataKey="processed" name="Processed" stroke={C.processed} fill="url(#gradProc)" strokeWidth={2} dot={false} />
                    <Area type="monotone" dataKey="failed"    name="Failed"    stroke={C.failed}    fill="url(#gradFail)" strokeWidth={2} dot={false} />
                  </AreaChart>
                </ResponsiveContainer>
              )}
            </CardContent>
          </Card>
        </Grid>

        {/* Health pie */}
        <Grid item xs={12} md={4}>
          <Card sx={{ height: '100%' }}>
            <CardHeader
              title={<Typography variant="h6" fontWeight={600}>Pipeline Health</Typography>}
              subheader={<Typography variant="caption" color="text.secondary">Overall file status breakdown</Typography>}
            />
            <CardContent>
              {isLoading ? <SectionSkeleton height={220} /> : pieData.length === 0 ? (
                <Box display="flex" justifyContent="center" alignItems="center" height={220}>
                  <Typography color="text.secondary" variant="body2">No data</Typography>
                </Box>
              ) : (
                <ResponsiveContainer width="100%" height={220}>
                  <PieChart>
                    <Pie data={pieData} cx="50%" cy="50%" innerRadius={55} outerRadius={85} paddingAngle={3} dataKey="value">
                      {pieData.map((_, i) => <Cell key={i} fill={PIE_COLOURS[i % PIE_COLOURS.length]} />)}
                    </Pie>
                    <RechartTooltip contentStyle={{ borderRadius: 8, fontSize: 13 }} />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                  </PieChart>
                </ResponsiveContainer>
              )}
            </CardContent>
          </Card>
        </Grid>
      </Grid>

      {/* ── Pipeline Status Overview ── */}
      <Card sx={{ mb: 3 }}>
        <CardHeader
          title={<Typography variant="h6" fontWeight={600}>Pipeline Status Overview</Typography>}
          subheader={<Typography variant="caption" color="text.secondary">Health summary per pipeline for selected period</Typography>}
          action={<Storage sx={{ opacity: 0.3, mt: 1, mr: 1 }} />}
        />
        <CardContent sx={{ p: 0 }}>
          {isLoading ? (
            <Box px={3} pb={2}><SectionSkeleton height={150} /></Box>
          ) : !data?.pipelineStatus.length ? (
            <Box display="flex" justifyContent="center" py={4}>
              <Typography color="text.secondary" variant="body2">No pipeline data available</Typography>
            </Box>
          ) : (
            <TableContainer>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Pipeline</TableCell>
                    <TableCell>Status</TableCell>
                    <TableCell>Last Run</TableCell>
                    <TableCell align="right">Total Files</TableCell>
                    <TableCell align="right">Processed</TableCell>
                    <TableCell align="right">Failed</TableCell>
                    <TableCell sx={{ minWidth: 140 }}>Success Rate</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {data.pipelineStatus.map(p => {
                    const rate = p.totalFiles > 0 ? Math.round((p.processedFiles / p.totalFiles) * 100) : 0;
                    return (
                      <TableRow
                        key={p.pipelineName} hover sx={{ cursor: 'pointer' }}
                        onClick={() => goMonitor({ pipeline: p.pipelineName })}
                      >
                        <TableCell>
                          <Typography variant="body2" fontWeight={500} sx={{ fontFamily: 'monospace', fontSize: 13 }}>
                            {p.pipelineName}
                          </Typography>
                        </TableCell>
                        <TableCell><PipelineStatusChip status={p.status} /></TableCell>
                        <TableCell>
                          <Typography variant="body2" color="text.secondary">
                            {p.lastRunTime ? format(new Date(p.lastRunTime), 'MMM dd, HH:mm') : '—'}
                          </Typography>
                        </TableCell>
                        <TableCell align="right"><Typography variant="body2">{p.totalFiles.toLocaleString()}</Typography></TableCell>
                        <TableCell align="right"><Typography variant="body2" color="success.main" fontWeight={500}>{p.processedFiles.toLocaleString()}</Typography></TableCell>
                        <TableCell align="right"><Typography variant="body2" color={p.failedFiles > 0 ? 'error.main' : 'text.secondary'} fontWeight={p.failedFiles > 0 ? 600 : 400}>{p.failedFiles.toLocaleString()}</Typography></TableCell>
                        <TableCell>
                          <Box display="flex" alignItems="center" gap={1}>
                            <LinearProgress variant="determinate" value={rate} sx={{ flex: 1, height: 6, borderRadius: 3, bgcolor: 'rgba(128,128,128,0.2)', '& .MuiLinearProgress-bar': { bgcolor: rate >= 80 ? C.processed : rate >= 50 ? C.notProcessed : C.failed } }} />
                            <Typography variant="caption" sx={{ minWidth: 34, textAlign: 'right' }}>{rate}%</Typography>
                          </Box>
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            </TableContainer>
          )}
        </CardContent>
      </Card>

      {/* ── Bottom row: Top Failures + Recent Activity ── */}
      <Grid container spacing={2} sx={{ mb: 3 }}>
        {/* Top Failures */}
        <Grid item xs={12} md={7}>
          <Card sx={{ height: '100%' }}>
            <CardHeader
              title={<Typography variant="h6" fontWeight={600}>Top Failures</Typography>}
              subheader={<Typography variant="caption" color="text.secondary">Latest failed files sorted by time</Typography>}
              action={
                <Button size="small" onClick={() => goMonitor({ status: 'Failed' })} sx={{ mt: 0.5 }}>
                  View All
                </Button>
              }
            />
            <CardContent sx={{ p: 0 }}>
              {isLoading ? (
                <Box px={3} pb={2}><SectionSkeleton height={200} /></Box>
              ) : !data?.topFailures.length ? (
                <Box display="flex" justifyContent="center" alignItems="center" py={5}>
                  <Box textAlign="center">
                    <CheckCircle sx={{ fontSize: 40, color: 'success.main', opacity: 0.6, mb: 1 }} />
                    <Typography color="text.secondary" variant="body2">No failures in this period</Typography>
                  </Box>
                </Box>
              ) : (
                <TableContainer>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>Pipeline</TableCell>
                        <TableCell>File</TableCell>
                        <TableCell>Failure Reason</TableCell>
                        <TableCell>Time</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {data.topFailures.map((f, i) => (
                        <TableRow key={i} hover>
                          <TableCell><Chip label={f.pipelineName} size="small" variant="outlined" sx={{ fontFamily: 'monospace', fontSize: 11 }} /></TableCell>
                          <TableCell>
                            <Tooltip title={f.fileName}>
                              <Typography variant="body2" sx={{ maxWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: 'monospace', fontSize: 11 }}>
                                {f.fileName}
                              </Typography>
                            </Tooltip>
                          </TableCell>
                          <TableCell>
                            <Tooltip title={f.failureReason ?? ''}>
                              <Typography variant="body2" color="error" sx={{ maxWidth: 180, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: 12 }}>
                                {f.failureReason ?? '—'}
                              </Typography>
                            </Tooltip>
                          </TableCell>
                          <TableCell>
                            <Typography variant="caption" color="text.secondary">
                              {(() => { try { return format(new Date(f.failedTime), 'MMM dd, HH:mm'); } catch { return '—'; } })()}
                            </Typography>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </TableContainer>
              )}
            </CardContent>
          </Card>
        </Grid>

        {/* Recent Activity */}
        <Grid item xs={12} md={5}>
          <Card sx={{ height: '100%' }}>
            <CardHeader
              title={<Typography variant="h6" fontWeight={600}>Recent Activity</Typography>}
              subheader={<Typography variant="caption" color="text.secondary">Latest pipeline processing events</Typography>}
              action={<AccessTime sx={{ opacity: 0.3, mt: 1, mr: 1 }} />}
            />
            <CardContent sx={{ p: 0, maxHeight: 360, overflowY: 'auto' }}>
              {isLoading ? (
                <Box px={3} pb={2}><SectionSkeleton height={280} /></Box>
              ) : !data?.recentActivity.length ? (
                <Box display="flex" justifyContent="center" py={5}>
                  <Typography color="text.secondary" variant="body2">No recent activity</Typography>
                </Box>
              ) : data.recentActivity.map((a, i) => (
                <React.Fragment key={i}>
                  <Box px={2} py={1.5} display="flex" gap={2} alignItems="flex-start">
                    <Box sx={{ mt: 0.3, flexShrink: 0 }}>
                      {a.status === 'Processed' ? <CheckCircle sx={{ fontSize: 16, color: C.processed }} />
                        : a.status === 'Failed' ? <Cancel sx={{ fontSize: 16, color: C.failed }} />
                        : <HourglassEmpty sx={{ fontSize: 16, color: C.inProgress }} />}
                    </Box>
                    <Box flex={1} overflow="hidden">
                      <Box display="flex" justifyContent="space-between" alignItems="center">
                        <Typography variant="body2" fontWeight={500} sx={{ fontFamily: 'monospace', fontSize: 11, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 200 }}>
                          {a.fileName}
                        </Typography>
                        <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0, ml: 1 }}>
                          {(() => { try { return format(new Date(a.time), 'HH:mm'); } catch { return ''; } })()}
                        </Typography>
                      </Box>
                      <Typography variant="caption" color="text.secondary">
                        {a.pipelineName}
                        {a.status === 'Failed' && a.remark && (
                          <Tooltip title={a.remark}>
                            <Box component="span" sx={{ color: 'error.main', ml: 0.5, cursor: 'help' }}>· {a.remark.slice(0, 30)}{a.remark.length > 30 ? '…' : ''}</Box>
                          </Tooltip>
                        )}
                      </Typography>
                    </Box>
                    <Chip
                      label={a.status} size="small"
                      color={a.status === 'Processed' ? 'success' : a.status === 'Failed' ? 'error' : 'info'}
                      sx={{ flexShrink: 0, fontSize: 10 }}
                    />
                  </Box>
                  {i < (data?.recentActivity.length ?? 0) - 1 && <Divider />}
                </React.Fragment>
              ))}
            </CardContent>
          </Card>
        </Grid>
      </Grid>

      {/* ── Quick Stats ── */}
      <Card>
        <CardHeader
          title={<Typography variant="h6" fontWeight={600}>Quick Statistics</Typography>}
          action={<Assignment sx={{ opacity: 0.3, mt: 1, mr: 1 }} />}
        />
        <CardContent>
          <Grid container spacing={2}>
            {[
              { label: 'Most Active Pipeline',        value: data?.mostActivePipeline ?? '—',      loading: isLoading },
              { label: 'Highest Failure Rate',        value: data?.highestFailurePipeline ?? (data?.kpis.failed === 0 ? 'None' : '—'), loading: isLoading },
              { label: 'Total Records Inserted',      value: kpi?.rowsInserted.toLocaleString() ?? '—', loading: isLoading },
              { label: 'Total Records Updated',       value: kpi?.rowsUpdated.toLocaleString()  ?? '—', loading: isLoading },
            ].map(s => (
              <Grid item xs={12} sm={6} md={3} key={s.label}>
                <Box>
                  <Typography variant="caption" color="text.secondary" display="block">{s.label}</Typography>
                  {s.loading ? <Skeleton width={120} height={28} /> : (
                    <Typography variant="h6" fontWeight={600} sx={{ fontFamily: 'monospace', fontSize: 15 }}>{s.value}</Typography>
                  )}
                </Box>
              </Grid>
            ))}
          </Grid>
        </CardContent>
      </Card>
    </Box>
  );
}
