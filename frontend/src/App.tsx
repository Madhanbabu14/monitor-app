import { useMemo } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ThemeProvider, CssBaseline } from '@mui/material';
import { useAuthStore } from './store/authStore';
import { lightTheme, darkTheme } from './theme';
import AppLayout from './components/layout/AppLayout';
import DashboardPage from './features/dashboard/DashboardPage';
import FileMonitorPage from './features/file-monitor/FileMonitorPage';
import LoginPage from './features/auth/LoginPage';
import S3FilesPage from './features/s3/S3FilesPage';
import SSOHandler from './features/auth/SSOHandler';
import AuthCallbackPage from './features/auth/AuthCallbackPage';

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  if (isAuthenticated) return <>{children}</>;
  // Carry login_hint into /login so MSAL's ssoSilent can use it
  const loginHint = new URLSearchParams(window.location.search).get('login_hint');
  const to = loginHint ? `/login?login_hint=${encodeURIComponent(loginHint)}` : '/login';
  return <Navigate to={to} replace />;
}

export default function App() {
  const themeMode = useAuthStore((s) => s.themeMode);
  const theme = useMemo(() => (themeMode === 'dark' ? darkTheme : lightTheme), [themeMode]);

  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <BrowserRouter>
        {/* SSOHandler intercepts ?azure_token= before any route is rendered.
            It posts the token to the backend for validation, stores the
            resulting session, then navigates to /dashboard — or falls back
            to /login on failure.  Nothing inside it runs if there is no token. */}
        <SSOHandler>
          <Routes>
            <Route path="/login"         element={<LoginPage />} />
            <Route path="/auth/callback" element={<AuthCallbackPage />} />
            <Route
              path="/"
              element={
                <ProtectedRoute>
                  <AppLayout />
                </ProtectedRoute>
              }
            >
              <Route index element={<Navigate to="/dashboard" replace />} />
              <Route path="dashboard"    element={<DashboardPage />} />
              <Route path="file-monitor" element={<FileMonitorPage />} />
              <Route path="s3-files"     element={<S3FilesPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/dashboard" replace />} />
          </Routes>
        </SSOHandler>
      </BrowserRouter>
    </ThemeProvider>
  );
}
