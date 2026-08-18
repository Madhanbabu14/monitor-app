import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Box, CircularProgress, Typography } from '@mui/material';
import { api } from '../../services/api';
import { useAuthStore } from '../../store/authStore';

interface Props { children: React.ReactNode; }

// Intercepts ?azure_token= query parameters injected by the Admin Portal when it
// redirects the user to this app with a pre-acquired Azure token.  Nothing in
// here runs if the parameter is absent — zero cost for normal navigation.
export default function SSOHandler({ children }: Props) {
  const navigate    = useNavigate();
  const { setUser, isAuthenticated } = useAuthStore();

  const azureToken = new URLSearchParams(window.location.search).get('azure_token');
  const isCallback = window.location.pathname === '/auth/callback';

  const [resolving, setResolving] = useState(
    !!azureToken && !isAuthenticated && !isCallback
  );

  useEffect(() => {
    if (!azureToken || isCallback) return;

    if (isAuthenticated) {
      const params = new URLSearchParams(window.location.search);
      params.delete('azure_token');
      const search = params.toString() ? `?${params}` : '';
      navigate(window.location.pathname + search, { replace: true });
      return;
    }

    api
      .post('/auth/sso', { azureToken })
      .then((res) => {
        const { user, token } = res.data.data;
        setUser(user, token);
        navigate('/dashboard', { replace: true });
      })
      .catch((err) => {
        const message =
          (err as { response?: { data?: { message?: string } } })?.response?.data?.message
          ?? 'Unknown error';
        console.error('[SSO] Admin Portal token rejected:', message);
        navigate('/login?error=sso_failed', { replace: true });
      })
      .finally(() => setResolving(false));
  }, []); // intentional: run only once on mount

  if (resolving) {
    return (
      <Box
        minHeight="100vh"
        display="flex"
        flexDirection="column"
        alignItems="center"
        justifyContent="center"
        gap={2}
      >
        <CircularProgress />
        <Typography variant="body2" color="text.secondary">
          Signing you in with Microsoft…
        </Typography>
      </Box>
    );
  }

  return <>{children}</>;
}
