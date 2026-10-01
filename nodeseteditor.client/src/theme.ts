import { createTheme, alpha, type ThemeOptions, type Theme } from '@mui/material/styles';

export const ThemeModes = {
   Light: 'light',
   Dark: 'dark'
} as const;

export type ThemeMode = typeof ThemeModes[keyof typeof ThemeModes];

declare module '@mui/material/styles' {
   interface TypographyVariants {
      bold: React.CSSProperties;
      error: React.CSSProperties;
      italic: React.CSSProperties;
      superscript: React.CSSProperties;
      subscript: React.CSSProperties;
      list: React.CSSProperties;
      table: React.CSSProperties;
   }

   // allow configuration using `createTheme`
   interface TypographyVariantsOptions {
      bold?: React.CSSProperties;
      error?: React.CSSProperties;
      italic?: React.CSSProperties;
      superscript?: React.CSSProperties;
      subscript?: React.CSSProperties;
      list?: React.CSSProperties;
      table?: React.CSSProperties;
   }
}

// Update the Typography's variant prop options
declare module '@mui/material/Typography' {
   interface TypographyPropsVariantOverrides {
      bold: true;
      error: true;
      italic: true;
      superscript: true;
      subscript: true;
      list: true;
      table: true;
   }
}

// Apple-inspired design tokens. Colours follow the macOS/iOS system palette so the UI
// reads as calm and familiar: one accent colour, soft grey surfaces, hairline separators.
const fontStack = [
   '-apple-system',
   'BlinkMacSystemFont',
   '"SF Pro Text"',
   '"SF Pro Display"',
   '"Helvetica Neue"',
   '"Segoe UI"',
   'Roboto',
   'Arial',
   'sans-serif'
].join(',');

const light = {
   accent: '#007AFF',
   accentDark: '#0060DF',
   accentLight: '#E5F1FF',
   background: '#FFFFFF',
   surface: '#F5F5F7',
   sidebar: '#F2F2F7',
   separator: 'rgba(60, 60, 67, 0.16)',
   textPrimary: '#1D1D1F',
   textSecondary: '#6E6E73',
   red: '#FF3B30',
   green: '#34C759',
   orange: '#FF9500',
};

const dark = {
   accent: '#0A84FF',
   accentDark: '#0071E3',
   accentLight: '#1C3A5E',
   background: '#1C1C1E',
   surface: '#2C2C2E',
   sidebar: '#232325',
   separator: 'rgba(235, 235, 245, 0.16)',
   textPrimary: '#F5F5F7',
   textSecondary: '#A1A1A6',
   red: '#FF453A',
   green: '#30D158',
   orange: '#FF9F0A',
};

const bodyText = {
   fontSize: '1rem',
   lineHeight: 1.5,
   letterSpacing: '-0.01em',
   textRendering: 'optimizeLegibility'
} as const;

const typography: ThemeOptions['typography'] = {
   fontFamily: fontStack,
   h1: { fontWeight: 700, letterSpacing: '-0.025em' },
   h2: { fontWeight: 700, letterSpacing: '-0.025em' },
   h3: { fontWeight: 700, letterSpacing: '-0.02em' },
   h4: { fontWeight: 700, letterSpacing: '-0.02em' },
   h5: { fontWeight: 600, letterSpacing: '-0.015em' },
   h6: { fontWeight: 600, letterSpacing: '-0.01em' },
   subtitle1: { fontWeight: 500 },
   subtitle2: { fontWeight: 600 },
   body1: { ...bodyText },
   body2: { fontSize: '0.9375rem', lineHeight: 1.45, letterSpacing: '-0.005em' },
   caption: { fontSize: '0.8125rem', letterSpacing: 0 },
   button: { textTransform: 'none', fontWeight: 500, fontSize: '0.9375rem', letterSpacing: '-0.005em' },
   bold: { ...bodyText, fontWeight: 600 },
   error: { ...bodyText, fontWeight: 600 },
   italic: { ...bodyText, fontWeight: 600, fontStyle: 'italic' },
   superscript: { ...bodyText, verticalAlign: 'super', fontSize: '0.675rem' },
   subscript: { ...bodyText, verticalAlign: 'sub', fontSize: '0.675rem' },
   list: { marginBottom: '0' },
   table: { marginBottom: '0', paddingLeft: '0.5em', paddingRight: '0.5em' }
};

type Tokens = typeof light;

const baseline = (t: Tokens) => `
   a { text-decoration: none; }
   a:link, a:visited { color: ${t.accent}; }
   a:hover, a:active { color: ${t.accentDark}; text-decoration: underline; }
   html { scroll-behavior: smooth; -webkit-font-smoothing: antialiased; -moz-osx-font-smoothing: grayscale; }
   body { font-family: ${fontStack}; }
   span { white-space: pre-wrap; }
   ::selection { background: ${alpha(t.accent, 0.25)}; }
   * { scrollbar-width: thin; scrollbar-color: ${alpha(t.textSecondary, 0.5)} transparent; }
   *::-webkit-scrollbar { width: 8px; height: 8px; }
   *::-webkit-scrollbar-track { background: transparent; }
   *::-webkit-scrollbar-thumb { background: ${alpha(t.textSecondary, 0.4)}; border-radius: 8px; }
   div .scrollable { overflow: auto; }
`;

const components = (t: Tokens): ThemeOptions['components'] => ({
   MuiCssBaseline: { styleOverrides: baseline(t) },
   MuiAppBar: {
      defaultProps: { elevation: 0, color: 'default' },
      styleOverrides: {
         root: {
            backgroundColor: alpha(t.background, 0.8),
            backdropFilter: 'saturate(180%) blur(20px)',
            WebkitBackdropFilter: 'saturate(180%) blur(20px)',
            color: t.textPrimary,
            borderBottom: `1px solid ${t.separator}`,
         }
      }
   },
   MuiPaper: {
      defaultProps: { elevation: 0 },
      styleOverrides: {
         root: { backgroundImage: 'none' },
         rounded: { borderRadius: 12 },
         outlined: { borderColor: t.separator },
      }
   },
   MuiCard: {
      defaultProps: { elevation: 0 },
      styleOverrides: {
         root: {
            borderRadius: 16,
            border: `1px solid ${t.separator}`,
            backgroundColor: t.background,
            boxShadow: '0 1px 2px rgba(0,0,0,0.04), 0 4px 16px rgba(0,0,0,0.04)',
            transition: 'box-shadow 0.2s ease, transform 0.2s ease',
         }
      }
   },
   MuiButton: {
      defaultProps: { disableElevation: true },
      styleOverrides: {
         root: {
            borderRadius: 980,
            textTransform: 'none',
            fontWeight: 500,
            minHeight: 38,
            margin: 0,
            paddingLeft: 18,
            paddingRight: 18,
         },
         sizeSmall: { minHeight: 32, paddingLeft: 12, paddingRight: 12, fontSize: '0.875rem' },
         sizeLarge: { minHeight: 46, paddingLeft: 24, paddingRight: 24, fontSize: '1.0625rem' },
         // Secondary buttons (Cancel, Reset, Close...): neutral pill, like macOS push buttons.
         outlinedPrimary: {
            color: t.textPrimary,
            borderColor: t.separator,
            backgroundColor: t.background,
            '&:hover': { backgroundColor: t.surface, borderColor: t.separator },
         },
         outlined: { borderColor: t.separator },
         // Tertiary/row actions (View, Types, Edit Server...): tinted pill; red tint for destructive ones.
         textPrimary: {
            backgroundColor: alpha(t.accent, 0.1),
            '&:hover': { backgroundColor: alpha(t.accent, 0.18) },
         },
         textError: {
            backgroundColor: alpha(t.red, 0.1),
            '&:hover': { backgroundColor: alpha(t.red, 0.18) },
         },
         text: {
            '&.Mui-disabled': { backgroundColor: alpha(t.textSecondary, 0.08) },
         },
      }
   },
   MuiDialogActions: {
      styleOverrides: {
         // Apple dialog layout: actions right-aligned, secondary left of the primary action.
         root: {
            padding: '12px 24px 20px',
            gap: 8,
            justifyContent: 'flex-end',
            '& > :not(style) ~ :not(style)': { marginLeft: 0 },
         }
      }
   },
   MuiIconButton: {
      styleOverrides: {
         root: { borderRadius: 8, padding: 8 },
         sizeSmall: { padding: 6 }
      }
   },
   MuiToggleButton: {
      styleOverrides: {
         root: { textTransform: 'none', borderColor: t.separator, paddingLeft: 14, paddingRight: 14, paddingTop: 5, paddingBottom: 5, whiteSpace: 'nowrap' }
      }
   },
   MuiTab: {
      styleOverrides: {
         root: { textTransform: 'none', fontWeight: 500, minHeight: 40 }
      }
   },
   MuiTabs: {
      styleOverrides: {
         indicator: { height: 2, borderRadius: 2 }
      }
   },
   MuiOutlinedInput: {
      styleOverrides: {
         root: {
            borderRadius: 8,
            '& .MuiOutlinedInput-notchedOutline': { borderColor: t.separator },
         }
      }
   },
   MuiDialog: {
      styleOverrides: {
         paper: {
            borderRadius: 14,
            boxShadow: '0 20px 60px rgba(0,0,0,0.25)',
         }
      }
   },
   MuiDialogTitle: {
      styleOverrides: {
         root: { fontWeight: 600, fontSize: '1.125rem' }
      }
   },
   MuiPopover: {
      styleOverrides: {
         paper: {
            borderRadius: 12,
            border: `1px solid ${t.separator}`,
            boxShadow: '0 10px 30px rgba(0,0,0,0.15)',
         }
      }
   },
   MuiMenu: {
      styleOverrides: {
         paper: { borderRadius: 10 },
         list: { padding: 4 },
      }
   },
   MuiMenuItem: {
      styleOverrides: {
         root: { borderRadius: 6, margin: '1px 0' }
      }
   },
   MuiTooltip: {
      styleOverrides: {
         tooltip: {
            borderRadius: 8,
            fontSize: '0.8125rem',
            backgroundColor: alpha(t === dark ? '#3A3A3C' : '#1D1D1F', 0.92),
            backdropFilter: 'blur(10px)',
            padding: '6px 10px',
         }
      }
   },
   MuiAlert: {
      styleOverrides: {
         root: { borderRadius: 12, alignItems: 'center' }
      }
   },
   MuiChip: {
      styleOverrides: {
         root: { borderRadius: 8, fontWeight: 500 }
      }
   },
   MuiDivider: {
      styleOverrides: {
         root: { borderColor: t.separator }
      }
   },
   MuiTableCell: {
      styleOverrides: {
         root: { borderBottomColor: t.separator },
         head: {
            backgroundColor: t.surface,
            fontWeight: 600,
            color: t.textSecondary,
         }
      }
   },
   MuiListItemButton: {
      styleOverrides: {
         root: {
            borderRadius: 6,
            '&.Mui-selected': { backgroundColor: alpha(t.accent, 0.12) },
            '&.Mui-selected:hover': { backgroundColor: alpha(t.accent, 0.18) },
         }
      }
   },
});

const palette = (t: Tokens, mode: 'light' | 'dark'): ThemeOptions['palette'] => ({
   mode,
   primary: {
      main: t.accent,
      dark: t.accentDark,
      light: t.accentLight,
      contrastText: '#FFFFFF'
   },
   error: { main: t.red },
   success: { main: t.green },
   warning: { main: t.orange },
   divider: t.separator,
   background: {
      default: t.background,
      paper: t.background
   },
   text: {
      primary: t.textPrimary,
      secondary: t.textSecondary
   },
   // grey[200] is the sidebar / search-bar surface used by Layout and AddressSpaceTree.
   grey: mode === 'light'
      ? { [200]: t.sidebar }
      : { [200]: t.sidebar, [600]: '#D1D1D6' }
});

const buildTheme = (t: Tokens, mode: 'light' | 'dark'): Theme => createTheme({
   spacing: 1,
   shape: { borderRadius: 10 },
   palette: palette(t, mode),
   typography: {
      ...typography,
      error: { ...(typography as { error: React.CSSProperties }).error, color: t.red }
   },
   components: components(t)
});

export const LightTheme = buildTheme(light, 'light');

export const DarkTheme = buildTheme(dark, 'dark');

export default LightTheme;
