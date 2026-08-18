import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { User, ThemeMode } from '../types';

interface AuthState {
  user: User | null;
  token: string | null;
  themeMode: ThemeMode;
  isAuthenticated: boolean;
  setUser: (user: User, token: string) => void;
  clearAuth: () => void;
  setThemeMode: (mode: ThemeMode) => void;
  toggleTheme: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      user: null,
      token: null,
      themeMode: 'dark',
      isAuthenticated: false,

      setUser: (user, token) =>
        set({ user, token, isAuthenticated: true }),

      clearAuth: () =>
        set({ user: null, token: null, isAuthenticated: false }),

      setThemeMode: (themeMode) => set({ themeMode }),

      toggleTheme: () =>
        set((state) => ({ themeMode: state.themeMode === 'dark' ? 'light' : 'dark' })),
    }),
    {
      name: 'pcc-auth',
      partialize: (state) => ({
        token: state.token,
        user: state.user,
        themeMode: state.themeMode,
        isAuthenticated: state.isAuthenticated,
      }),
    }
  )
);
