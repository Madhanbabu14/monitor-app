import { createTheme, ThemeOptions } from '@mui/material/styles';

const baseTypography = {
  fontFamily: '"Inter", "Roboto", "Helvetica", "Arial", sans-serif',
  h1: { fontWeight: 700 },
  h2: { fontWeight: 700 },
  h3: { fontWeight: 600 },
  h4: { fontWeight: 600 },
  h5: { fontWeight: 600 },
  h6: { fontWeight: 600 },
};

// Only truly theme-agnostic overrides go here (shapes, transforms, etc.)
const baseComponents: ThemeOptions['components'] = {
  MuiTextField: {
    defaultProps: { variant: 'outlined' },
  },
  MuiButton: {
    styleOverrides: {
      root: { textTransform: 'none', borderRadius: 8, fontWeight: 500 },
    },
    defaultProps: { disableElevation: true },
  },
  MuiCard: {
    styleOverrides: {
      root: { borderRadius: 12, boxShadow: '0 1px 3px 0 rgb(0 0 0 / 0.1), 0 1px 2px -1px rgb(0 0 0 / 0.1)' },
    },
  },
  MuiChip: {
    styleOverrides: { root: { borderRadius: 6, fontWeight: 500 } },
  },
  MuiTableCell: {
    styleOverrides: { root: { borderBottom: '1px solid rgba(0,0,0,0.06)' } },
  },
};

export const lightTheme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#2563EB', light: '#3B82F6', dark: '#1D4ED8' },
    secondary: { main: '#7C3AED', light: '#8B5CF6', dark: '#6D28D9' },
    success: { main: '#059669', light: '#10B981', dark: '#047857' },
    warning: { main: '#D97706', light: '#F59E0B', dark: '#B45309' },
    error: { main: '#DC2626', light: '#EF4444', dark: '#B91C1C' },
    background: { default: '#F8FAFC', paper: '#FFFFFF' },
    text: { primary: '#0F172A', secondary: '#475569' },
    divider: 'rgba(0,0,0,0.08)',
  },
  typography: baseTypography,
  components: {
    ...baseComponents,
    MuiOutlinedInput: {
      styleOverrides: {
        input: {
          color: '#0F172A',
          '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus': {
            WebkitBoxShadow: '0 0 0 1000px #FFFFFF inset',
            WebkitTextFillColor: '#0F172A',
            transition: 'background-color 5000s ease-in-out 0s',
          },
        },
      },
    },
    MuiInputLabel: {
      styleOverrides: {
        root: {
          color: '#475569',
          '&.Mui-focused': { color: '#2563EB' },
        },
      },
    },
    MuiAppBar: {
      styleOverrides: {
        root: {
          backgroundColor: '#FFFFFF',
          color: '#0F172A',
          boxShadow: '0 1px 0 0 rgba(0,0,0,0.08)',
        },
      },
    },
    MuiDrawer: {
      styleOverrides: {
        paper: { backgroundColor: '#0F172A', color: '#CBD5E1' },
      },
    },
  },
});

export const darkTheme = createTheme({
  palette: {
    mode: 'dark',
    primary: { main: '#3B82F6', light: '#60A5FA', dark: '#2563EB' },
    secondary: { main: '#8B5CF6', light: '#A78BFA', dark: '#7C3AED' },
    success: { main: '#10B981', light: '#34D399', dark: '#059669' },
    warning: { main: '#F59E0B', light: '#FBBF24', dark: '#D97706' },
    error: { main: '#EF4444', light: '#F87171', dark: '#DC2626' },
    background: { default: '#0B1120', paper: '#111827' },
    text: { primary: '#F1F5F9', secondary: '#94A3B8' },
    divider: 'rgba(255,255,255,0.08)',
  },
  typography: baseTypography,
  components: {
    ...baseComponents,
    // MuiInputBase catches all input types (TextField, Select native input, etc.)
    MuiInputBase: {
      styleOverrides: {
        root: { color: '#F1F5F9' },
        input: {
          color: '#F1F5F9',
          '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus': {
            WebkitBoxShadow: '0 0 0 1000px #111827 inset',
            WebkitTextFillColor: '#F1F5F9',
            transition: 'background-color 5000s ease-in-out 0s',
          },
        },
      },
    },
    MuiOutlinedInput: {
      styleOverrides: {
        root: {
          color: '#F1F5F9',
          '& .MuiOutlinedInput-notchedOutline': {
            borderColor: 'rgba(255,255,255,0.23)',
          },
          '&:hover .MuiOutlinedInput-notchedOutline': {
            borderColor: 'rgba(255,255,255,0.5)',
          },
          '&.Mui-focused .MuiOutlinedInput-notchedOutline': {
            borderColor: '#3B82F6',
          },
        },
        input: {
          color: '#F1F5F9',
          '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus': {
            WebkitBoxShadow: '0 0 0 1000px #111827 inset',
            WebkitTextFillColor: '#F1F5F9',
            transition: 'background-color 5000s ease-in-out 0s',
          },
        },
      },
    },
    MuiInputLabel: {
      styleOverrides: {
        root: {
          color: '#94A3B8',
          '&.Mui-focused': { color: '#3B82F6' },
        },
      },
    },
    MuiSelect: {
      styleOverrides: {
        icon: { color: '#94A3B8' },
      },
    },
    // Paper is the base for Select dropdowns, Menus, Tooltips, Dialogs
    MuiPaper: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          backgroundColor: '#111827',
        },
      },
    },
    MuiCard: {
      styleOverrides: {
        root: {
          borderRadius: 12,
          boxShadow: 'none',
          border: '1px solid rgba(255,255,255,0.08)',
          backgroundColor: '#111827',
          backgroundImage: 'none',
        },
      },
    },
    MuiAppBar: {
      styleOverrides: {
        root: {
          backgroundColor: '#111827',
          backgroundImage: 'none',
          boxShadow: '0 1px 0 0 rgba(255,255,255,0.08)',
        },
      },
    },
    MuiDrawer: {
      styleOverrides: {
        paper: {
          backgroundColor: '#0B1120',
          backgroundImage: 'none',
          color: '#CBD5E1',
          borderRight: '1px solid rgba(255,255,255,0.06)',
        },
      },
    },
    MuiTableCell: {
      styleOverrides: {
        root: { borderBottom: '1px solid rgba(255,255,255,0.06)', color: '#F1F5F9' },
        head: { color: '#94A3B8', fontWeight: 600 },
      },
    },
    MuiMenuItem: {
      styleOverrides: {
        root: {
          color: '#F1F5F9',
          '&:hover': { backgroundColor: 'rgba(255,255,255,0.08)' },
          '&.Mui-selected': { backgroundColor: 'rgba(59,130,246,0.15)' },
          '&.Mui-selected:hover': { backgroundColor: 'rgba(59,130,246,0.25)' },
        },
      },
    },
    MuiTab: {
      styleOverrides: {
        root: {
          color: '#94A3B8',
          '&.Mui-selected': { color: '#3B82F6' },
          textTransform: 'none',
        },
      },
    },
    MuiDivider: {
      styleOverrides: { root: { borderColor: 'rgba(255,255,255,0.08)' } },
    },
    MuiTooltip: {
      styleOverrides: {
        tooltip: { backgroundColor: '#1E293B', color: '#F1F5F9' },
        arrow: { color: '#1E293B' },
      },
    },
    MuiTablePagination: {
      styleOverrides: {
        root: { color: '#94A3B8' },
        selectLabel: { color: '#94A3B8' },
        displayedRows: { color: '#94A3B8' },
        select: { color: '#F1F5F9' },
      },
    },
  },
});
