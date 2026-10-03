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

declare module '@mui/material/styles' {
   // The header/footer bars carry their own colour pair so what sits inside them
   // (TopMenu's segmented nav, the footer links) can tint against the bar instead
   // of guessing from the palette mode. In light mode the bar is the brand blue,
   // in dark mode it matches the page — so neither `primary` nor `background`
   // describes it on its own.
   interface Palette {
      appBar: { main: string; contrastText: string };
      /** Avatar backgrounds for a model's lifecycle state in the model library. */
      modelState: { unlocked: string; locked: string; shared: string; cloud: string };
   }
   interface PaletteOptions {
      appBar?: { main: string; contrastText: string };
      modelState?: { unlocked: string; locked: string; shared: string; cloud: string };
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

// Apple-inspired structure — soft grey surfaces, hairline separators, one accent colour
// — carrying the OPC Foundation brand colours rather than the macOS system palette.
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
   // OPC Foundation brand blue, as before the restyle: the accent for every
   // interactive element and the fill of the header/footer bars.
   accent: '#196096',
   accentDark: '#084A79',
   accentLight: '#E8EFF4',
   onAccent: '#FFFFFF',
   appBar: '#196096',
   onAppBar: '#FFFFFF',
   background: '#FFFFFF',
   surface: '#F5F5F7',
   sidebar: '#F2F2F7',
   separator: 'rgba(60, 60, 67, 0.16)',
   textPrimary: '#1D1D1F',
   textSecondary: '#6E6E73',
   red: '#FF3B30',
   green: '#34C759',
   orange: '#FF9500',
   // Distinct from the brand accent so an info alert never reads as a warning
   // (dark mode's accent is amber) or as a plain interactive element.
   info: '#0288D1',
   // Model lifecycle states — see palette.modelState.
   //
   // Chosen in OKLCH (perceptual) rather than picked as hex, at chroma 0.10 — just under the
   // brand's 0.110 — so a status chip never out-shouts the primary action. The earlier set
   // used iOS system colours at chroma 0.194, nearly twice the brand's, which is what made
   // them clash. Blues sit at hue 230° rather than the brand's 247°: close enough to read as
   // blue, far enough not to look like a brighter copy of the accent.
   //   unlocked L.92  locked L.52  shared L.82 H150  cloud L.82 C.01
   modelUnlocked: '#C5EBFF',
   modelLocked: '#0E7397',
   modelShared: '#95D7A2',
   modelCloud: '#BEC5C9',
};

const dark = {
   // Amber, not the brand blue: against near-black surfaces a saturated blue is too
   // low-contrast to read as interactive. Amber is bright enough to carry links,
   // toolbar icons and selected rows — but it needs DARK text on top of it, which is
   // what onAccent is for (every `primary` background in the app honours it).
   accent: '#FFB74D',
   accentDark: '#F57C00',
   accentLight: '#FFD95B',
   onAccent: '#1A1A1A',
   // The bars match the page in dark mode, as they did originally — the hairline
   // border, not a fill, is what separates chrome from content.
   appBar: '#1C1C1E',
   onAppBar: '#F5F5F7',
   background: '#1C1C1E',
   surface: '#2C2C2E',
   sidebar: '#232325',
   separator: 'rgba(235, 235, 245, 0.16)',
   textPrimary: '#F5F5F7',
   textSecondary: '#A1A1A6',
   red: '#FF453A',
   green: '#30D158',
   orange: '#FF9F0A',
   info: '#64D2FF',
   // Same hues and chroma as light mode — a status colour has to mean the same thing in both,
   // and the brand cannot anchor them here anyway because it is amber in dark mode. Only
   // lightness differs: the light-mode values are tuned against white and would glare here.
   //   unlocked L.78  locked L.52  shared L.72  cloud L.68
   modelUnlocked: '#6EC3EB',
   modelLocked: '#0E7397',
   modelShared: '#75B683',
   modelCloud: '#929A9D',
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
   /* Colour only hand-written anchors. An 'a:link' selector is (0,1,1) specificity, which
      beats the (0,1,0) class an sx or a color prop generates — so applying it to every
      anchor silently overrode MUI Link's own colour app-wide, and footer links came out
      accent blue on the accent-blue bar. MUI Link defaults to color="primary" anyway, so
      component-rendered links keep the colour they had. */
   a:not([class]):link, a:not([class]):visited { color: ${t.accent}; }
   a:not([class]):hover, a:not([class]):active { color: ${t.accentDark}; text-decoration: underline; }
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
      // Opaque, not the translucent blurred bar: that was tuned for a near-white
      // surface and turns muddy over a saturated fill. `root` and `colorDefault`
      // carry the same values so the result does not depend on which slot MUI
      // applies last.
      defaultProps: { elevation: 0, color: 'default' },
      styleOverrides: {
         root: {
            backgroundColor: t.appBar,
            color: t.onAppBar,
            borderBottom: `1px solid ${t.separator}`,
         },
         colorDefault: {
            backgroundColor: t.appBar,
            color: t.onAppBar,
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
         // Tertiary/row actions (View, Types, Edit Workspace...): tinted pill; red tint for destructive ones.
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
         // 20px matches the gutter ModelDialog gives its content, so the buttons line up
         // with the fields above them rather than sitting inset from (or past) them.
         root: {
            padding: '12px 20px 16px',
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
      // MUI's `standard` variant is a 90%-lightened wash of the severity colour — on
      // these tokens that lands on #FFEFEE / #FFF7EB / #EFFBF2, which read as white.
      // Tint from the severity colour itself instead, so red/amber/green is legible at a
      // glance. alpha() composites over whatever surface is behind it, so one value works
      // in both modes; MUI's own text colour (dark in light mode, pale in dark) still sits
      // on it with plenty of contrast. No single-side accent border: a border on one edge
      // of a 12px-radius box gets mitred around the corners into a tapered sliver.
      styleOverrides: {
         root: { borderRadius: 12, alignItems: 'center' },
         standardError: { backgroundColor: alpha(t.red, 0.12) },
         standardWarning: { backgroundColor: alpha(t.orange, 0.12) },
         standardSuccess: { backgroundColor: alpha(t.green, 0.12) },
         standardInfo: { backgroundColor: alpha(t.info, 0.12) },
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
      // Dark mode's amber accent needs dark text, light mode's blue needs white.
      contrastText: t.onAccent
   },
   appBar: { main: t.appBar, contrastText: t.onAppBar },
   // Its own green, not the `green` success token: alerts should stay vivid, while a status
   // chip is deliberately muted to the brand's chroma.
   modelState: {
      unlocked: t.modelUnlocked,
      locked: t.modelLocked,
      shared: t.modelShared,
      cloud: t.modelCloud
   },
   error: { main: t.red },
   success: { main: t.green },
   warning: { main: t.orange },
   info: { main: t.info },
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
