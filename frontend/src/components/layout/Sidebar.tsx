import { useLocation, useNavigate } from 'react-router-dom';
import {
  Drawer, List, ListItem, ListItemButton, ListItemIcon, ListItemText,
  Typography, Box, Divider, Chip,
} from '@mui/material';
import {
  Dashboard, AccountTree, PowerSettingsNew, CloudQueue, TableChart,
} from '@mui/icons-material';
import { useAuthStore } from '../../store/authStore';

const DRAWER_WIDTH = 256;

const navItems = [
  { path: '/dashboard',    label: 'Dashboard',    icon: <Dashboard /> },
  { path: '/file-monitor', label: 'File Monitor', icon: <TableChart /> },
  { path: '/s3-files',     label: 'AWS S3 Files', icon: <CloudQueue /> },
];

interface SidebarProps {
  open: boolean;
  variant: 'permanent' | 'temporary';
  onClose: () => void;
}

export default function Sidebar({ open, variant, onClose }: SidebarProps) {
  const location = useLocation();
  const navigate = useNavigate();
  const { user, clearAuth } = useAuthStore();

  const handleNav = (path: string) => {
    navigate(path);
    if (variant === 'temporary') onClose();
  };

  const content = (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <Box sx={{ px: 3, py: 2.5 }}>
        <Box display="flex" alignItems="center" gap={1}>
          <AccountTree sx={{ color: 'primary.main', fontSize: 28 }} />
          <Box>
            <Typography variant="subtitle1" fontWeight={700} sx={{ color: '#F1F5F9', lineHeight: 1.2 }}>
              Pipeline Control
            </Typography>
            <Typography variant="caption" sx={{ color: '#64748B' }}>
              Center
            </Typography>
          </Box>
        </Box>
      </Box>

      <Divider sx={{ borderColor: 'rgba(255,255,255,0.08)', mb: 1 }} />

      <List sx={{ flex: 1, px: 1.5 }}>
        {navItems.map((item) => {
          const active = location.pathname.startsWith(item.path);
          return (
            <ListItem key={item.path} disablePadding sx={{ mb: 0.5 }}>
              <ListItemButton
                onClick={() => handleNav(item.path)}
                sx={{
                  borderRadius: 2,
                  px: 1.5,
                  py: 1,
                  bgcolor: active ? 'rgba(59,130,246,0.15)' : 'transparent',
                  '&:hover': { bgcolor: active ? 'rgba(59,130,246,0.2)' : 'rgba(255,255,255,0.06)' },
                }}
              >
                <ListItemIcon
                  sx={{
                    minWidth: 36,
                    color: active ? 'primary.main' : '#64748B',
                  }}
                >
                  {item.icon}
                </ListItemIcon>
                <ListItemText
                  primary={item.label}
                  primaryTypographyProps={{
                    fontSize: 14,
                    fontWeight: active ? 600 : 400,
                    color: active ? '#F1F5F9' : '#94A3B8',
                  }}
                />
                {active && (
                  <Box
                    sx={{
                      width: 3,
                      height: 20,
                      borderRadius: 1,
                      bgcolor: 'primary.main',
                      ml: 1,
                    }}
                  />
                )}
              </ListItemButton>
            </ListItem>
          );
        })}
      </List>

      <Divider sx={{ borderColor: 'rgba(255,255,255,0.08)' }} />

      <Box sx={{ p: 2 }}>
        <Box display="flex" alignItems="center" gap={1.5} mb={1.5}>
          <Box
            sx={{
              width: 36, height: 36, borderRadius: '50%',
              bgcolor: 'primary.main', display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}
          >
            <Typography variant="subtitle2" sx={{ color: '#fff', fontWeight: 700 }}>
              {user?.displayName?.[0] ?? 'U'}
            </Typography>
          </Box>
          <Box flex={1} overflow="hidden">
            <Typography variant="body2" fontWeight={600} sx={{ color: '#F1F5F9' }} noWrap>
              {user?.displayName ?? 'User'}
            </Typography>
            <Chip
              label={user?.role ?? 'Viewer'}
              size="small"
              sx={{ height: 18, fontSize: 10, bgcolor: 'rgba(59,130,246,0.2)', color: 'primary.light' }}
            />
          </Box>
        </Box>
        <ListItemButton
          onClick={() => { clearAuth(); navigate('/login'); }}
          sx={{ borderRadius: 2, px: 1.5, py: 0.75, '&:hover': { bgcolor: 'rgba(239,68,68,0.15)' } }}
        >
          <ListItemIcon sx={{ minWidth: 32, color: '#EF4444' }}>
            <PowerSettingsNew fontSize="small" />
          </ListItemIcon>
          <ListItemText
            primary="Sign Out"
            primaryTypographyProps={{ fontSize: 13, color: '#94A3B8' }}
          />
        </ListItemButton>
      </Box>
    </Box>
  );

  const responsiveDisplay =
    variant === 'permanent'
      ? { xs: 'none', md: 'block' }
      : { xs: 'block', md: 'none' };

  return (
    <Drawer
      variant={variant}
      open={open}
      onClose={onClose}
      sx={{
        display: responsiveDisplay,
        width: DRAWER_WIDTH,
        flexShrink: 0,
        '& .MuiDrawer-paper': {
          width: DRAWER_WIDTH,
          boxSizing: 'border-box',
          borderRight: 'none',
        },
      }}
    >
      {content}
    </Drawer>
  );
}

export { DRAWER_WIDTH };
