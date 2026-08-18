import { AppBar, Toolbar, IconButton, Typography, Box, Tooltip, useTheme } from '@mui/material';
import { Menu, DarkMode, LightMode, NotificationsNone } from '@mui/icons-material';
import { useAuthStore } from '../../store/authStore';
import { DRAWER_WIDTH } from './Sidebar';

interface TopBarProps {
  onMenuClick: () => void;
  title: string;
}

export default function TopBar({ onMenuClick, title }: TopBarProps) {
  const { themeMode, toggleTheme } = useAuthStore();
  const theme = useTheme();
  return (
    <AppBar
      position="fixed"
      elevation={0}
      sx={{
        width: { md: `calc(100% - ${DRAWER_WIDTH}px)` },
        ml: { md: `${DRAWER_WIDTH}px` },
        zIndex: theme.zIndex.drawer - 1,
      }}
    >
      <Toolbar sx={{ gap: 1 }}>
        <IconButton edge="start" onClick={onMenuClick} sx={{ display: { md: 'none' } }}>
          <Menu />
        </IconButton>

        <Typography variant="h6" fontWeight={600} sx={{ flex: 1 }}>
          {title}
        </Typography>

        <Box display="flex" alignItems="center" gap={0.5}>
          <Tooltip title={`Switch to ${themeMode === 'dark' ? 'light' : 'dark'} mode`}>
            <IconButton onClick={toggleTheme} size="small">
              {themeMode === 'dark' ? <LightMode fontSize="small" /> : <DarkMode fontSize="small" />}
            </IconButton>
          </Tooltip>
          <Tooltip title="Notifications">
            <IconButton size="small">
              <NotificationsNone fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      </Toolbar>
    </AppBar>
  );
}
