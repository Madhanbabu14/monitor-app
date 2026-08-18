import { useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { Box, Toolbar } from '@mui/material';
import Sidebar, { DRAWER_WIDTH } from './Sidebar';
import TopBar from './TopBar';
import { ErrorBoundary } from '../common/ErrorBoundary';

const PAGE_TITLES: Record<string, string> = {
  '/dashboard': 'Dashboard',
  '/pipelines': 'Pipeline Details',
  '/errors': 'Error Analysis',
  '/recovery': 'Data Recovery',
  '/validation': 'Database Validation',
  '/reports': 'Reports',
  '/s3-files': 'AWS S3 Files',
  '/configuration': 'Configuration',
  '/audit': 'Audit Logs',
};

export default function AppLayout() {
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();

  const title =
    Object.entries(PAGE_TITLES).find(([path]) =>
      location.pathname.startsWith(path)
    )?.[1] ?? 'Pipeline Control Center';

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <Sidebar
        open={true}
        variant="permanent"
        onClose={() => {}}
      />
      <Sidebar
        open={mobileOpen}
        variant="temporary"
        onClose={() => setMobileOpen(false)}
      />

      <TopBar onMenuClick={() => setMobileOpen(true)} title={title} />

      <Box
        component="main"
        sx={{
          flexGrow: 1,
          minWidth: 0,
          minHeight: '100vh',
          bgcolor: 'background.default',
        }}
      >
        <Toolbar />
        <Box sx={{ p: { xs: 2, md: 3 } }}>
          <ErrorBoundary>
            <Outlet />
          </ErrorBoundary>
        </Box>
      </Box>
    </Box>
  );
}
