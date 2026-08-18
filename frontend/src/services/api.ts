import axios, { AxiosInstance, AxiosError } from 'axios';
import { useAuthStore } from '../store/authStore';

const createApiClient = (): AxiosInstance => {
  const client = axios.create({
    baseURL: import.meta.env.VITE_API_URL ?? '/api',
    timeout: 120000,
    headers: { 'Content-Type': 'application/json' },
  });

  client.interceptors.request.use((config) => {
    const token = useAuthStore.getState().token;
    if (token) config.headers.Authorization = `Bearer ${token}`;
    return config;
  });

  client.interceptors.response.use(
    (response) => response,
    (error: AxiosError) => {
      // Do NOT intercept 401s from the auth endpoints themselves — those are
      // pre-authentication flows (SSO, login) that must propagate to their own
      // .catch() handlers so they can show a proper error message.
      // Only redirect to /login when a *protected* route returns 401 (session expired).
      const url = error.config?.url ?? '';
      const isAuthEndpoint = url.includes('/auth/sso') || url.includes('/auth/login');
      if (error.response?.status === 401 && !isAuthEndpoint) {
        useAuthStore.getState().clearAuth();
        window.location.href = '/login';
      }
      return Promise.reject(error);
    }
  );

  return client;
};

export const api = createApiClient();

export const extractData = <T>(response: { data: { data: T } }): T =>
  response.data.data;
