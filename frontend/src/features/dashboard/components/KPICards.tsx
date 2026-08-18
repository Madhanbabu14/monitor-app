import { Grid, Card, CardContent, Box, Typography, Chip } from '@mui/material';
import {
  AccountTree, PlayCircle, Cancel, TrendingUp, FolderOpen, TableRows,
} from '@mui/icons-material';
import { DashboardSummary } from '../../../types';

interface KPICardProps {
  title: string;
  value: string | number;
  icon: React.ReactNode;
  color: string;
  badge?: string;
  badgeColor?: 'success' | 'error' | 'warning' | 'default';
}

function KPICard({ title, value, icon, color, badge, badgeColor }: KPICardProps) {
  return (
    <Card sx={{ height: '100%' }}>
      <CardContent sx={{ p: 2.5 }}>
        <Box display="flex" alignItems="flex-start" justifyContent="space-between">
          <Box>
            <Typography variant="caption" color="text.secondary" fontWeight={500} textTransform="uppercase" letterSpacing={0.5}>
              {title}
            </Typography>
            <Typography variant="h3" fontWeight={700} mt={0.5} sx={{ lineHeight: 1.2 }}>
              {value}
            </Typography>
            {badge && (
              <Chip label={badge} color={badgeColor} size="small" sx={{ mt: 1, height: 20, fontSize: 11 }} />
            )}
          </Box>
          <Box
            sx={{
              width: 48, height: 48, borderRadius: 2,
              bgcolor: `${color}20`, display: 'flex',
              alignItems: 'center', justifyContent: 'center',
            }}
          >
            <Box sx={{ color }}>{icon}</Box>
          </Box>
        </Box>
      </CardContent>
    </Card>
  );
}

interface KPICardsProps {
  summary: DashboardSummary;
}

export default function KPICards({ summary }: KPICardsProps) {
  const cards: KPICardProps[] = [
    {
      title: 'Total Pipelines',
      value: summary.totalPipelines,
      icon: <AccountTree />,
      color: '#3B82F6',
    },
    {
      title: 'Running Now',
      value: summary.runningPipelines,
      icon: <PlayCircle />,
      color: '#10B981',
      badge: summary.runningPipelines > 0 ? 'Active' : 'Idle',
      badgeColor: summary.runningPipelines > 0 ? 'success' : 'default',
    },
    {
      title: "Today's Failures",
      value: summary.failedPipelines,
      icon: <Cancel />,
      color: summary.failedPipelines > 0 ? '#EF4444' : '#10B981',
      badge: summary.failedPipelines > 0 ? 'Needs Attention' : 'All Clear',
      badgeColor: summary.failedPipelines > 0 ? 'error' : 'success',
    },
    {
      title: '7-Day Success Rate',
      value: `${summary.successRate}%`,
      icon: <TrendingUp />,
      color: summary.successRate >= 90 ? '#10B981' : summary.successRate >= 70 ? '#F59E0B' : '#EF4444',
    },
    {
      title: "Today's Files",
      value: summary.todayFilesProcessed.toLocaleString(),
      icon: <FolderOpen />,
      color: '#8B5CF6',
    },
    {
      title: "Today's Rows",
      value: summary.todayRowsProcessed.toLocaleString(),
      icon: <TableRows />,
      color: '#F59E0B',
    },
  ];

  return (
    <Grid container spacing={2.5}>
      {cards.map((card) => (
        <Grid item xs={12} sm={6} lg={4} key={card.title}>
          <KPICard {...card} />
        </Grid>
      ))}
    </Grid>
  );
}
