import { Chip } from '@mui/material';
import {
  CheckCircle,
  Cancel,
  HourglassEmpty,
  Warning,
  Block,
  Queue,
} from '@mui/icons-material';
import { PipelineStatus } from '../../types';

const STATUS_CONFIG: Record<
  PipelineStatus,
  { color: 'success' | 'error' | 'warning' | 'default' | 'info'; icon: React.ReactElement; label: string }
> = {
  Success: { color: 'success', icon: <CheckCircle />, label: 'Success' },
  Failed: { color: 'error', icon: <Cancel />, label: 'Failed' },
  Running: { color: 'info', icon: <HourglassEmpty />, label: 'Running' },
  Cancelled: { color: 'default', icon: <Block />, label: 'Cancelled' },
  Warning: { color: 'warning', icon: <Warning />, label: 'Warning' },
  Queued: { color: 'default', icon: <Queue />, label: 'Queued' },
};

interface StatusChipProps {
  status: PipelineStatus;
  size?: 'small' | 'medium';
}

export default function StatusChip({ status, size = 'small' }: StatusChipProps) {
  const config = STATUS_CONFIG[status] ?? STATUS_CONFIG.Warning;
  return (
    <Chip
      icon={config.icon}
      label={config.label}
      color={config.color}
      size={size}
      variant="filled"
    />
  );
}
