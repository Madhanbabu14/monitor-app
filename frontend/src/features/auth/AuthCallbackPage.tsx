import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Box, CircularProgress, Typography, Alert, Button } from '@mui/material';
import { api } from '../../services/api';
import { useAuthStore } from '../../store/authStore';
import { consumeRedirectResult } from '../../auth/msalConfig';

export default function AuthCallbackPage() {
  const navigate = useNavigate();
  const setUser  = useAuthStore((s) => s.setUser);
  const [errorMsg, setErrorMsg] = useState('');

  // Guard against React StrictMode double-invoke
  const handled = useRef(false);

  useEffect(() => {
    if (handled.current) return;
    handled.current = true;

    // The redirect result was captured by initMsal() in main.tsx (before React rendered).
    // This guarantees handleRedirectPromise() was called exactly once, at startup.
    const result = consumeRedirectResult();

    if (!result) {
      // User navigated here directly — no redirect was in progress.
      navigate('/login', { replace: true });
      return;
    }

    api
      .post('/auth/sso', { azureToken: result.idToken })
      .then((res) => {
        const { user, token } = res.data.data;
        setUser(user, token);
        navigate('/dashboard', { replace: true });
      })
      .catch((err) => {
        const msg =
          (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
        setErrorMsg(msg ?? 'Sign-in failed. Please try again.');
      });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  if (errorMsg) {
    return (
      <Box minHeight="100vh" display="flex" alignItems="center" justifyContent="center" p={3}>
        <Box textAlign="center" maxWidth={440}>
          <Alert severity="error" sx={{ mb: 3 }}>{errorMsg}</Alert>
          <Button variant="outlined" onClick={() => navigate('/login?error=sso_failed')}>
            Back to Login
          </Button>
        </Box>
      </Box>
    );
  }

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
        Completing sign-in…
      </Typography>
    </Box>
  );
}
