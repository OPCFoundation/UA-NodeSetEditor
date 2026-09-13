import { createTheme, type ThemeOptions } from '@mui/material/styles';
import * as Color from '@mui/material/colors';

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

const lightText = '#FFFFFF'
const darkText = '#448FD1'
const mediumBlue = '#196096';
const lightBlue = '#E8EFF4';
const darkBlue = '#084A79';
const lightGrey = '#D7D9DA';

// Dark-mode "interactive" colour set — used for links, toolbar icons,
// import/create buttons, dialog borders/buttons, isPrivate model accents,
// etc. Defined here so changing the accent in dark mode is a one-line edit
// rather than a sweep across components. Stays distinct from the gold
// selection highlight (warning palette) so "this is interactive" and
// "this is selected" remain visually independent.
const darkPrimaryMain = '#ffb74d';   // soft amber
const darkPrimaryDark = '#f57c00';   // deeper amber — hover / active
const darkPrimaryLight = '#ffd95b';  // brighter amber — disabled/light states
const darkPrimaryContrast = '#1a1a1a';

const lightThemeOverrides: ThemeOptions = {
   palette: {
      primary: {
         main: mediumBlue,
         dark: darkBlue,
         light: lightBlue,
         contrastText: lightText
      },
      divider: darkBlue,
      text: {
         primary: '#000000',
         secondary: Color.grey[800]
      }
   },
   typography: {
      body1: {
         fontSize: '1.125rem',
         lineHeight: '1.55556',
         letterSpacing: '0.0125rem',
         textRendering: 'optimizeLegibility'
      },
      bold: {
         fontWeight: 'bold',
         fontSize: '1.125rem',
         lineHeight: '1.55556',
         letterSpacing: '0.0125rem',
         textRendering: 'optimizeLegibility'
      },
      error: {
         color: 'red',
         fontWeight: 'bold',
         fontSize: '1.125rem',
         lineHeight: '1.55556',
         letterSpacing: '0.0125rem',
         textRendering: 'optimizeLegibility'
      },
      italic: {
         fontWeight: 'bold',
         fontStyle: 'italic',
         fontSize: '1.125rem',
         lineHeight: '1.55556',
         letterSpacing: '0.0125rem',
         textRendering: 'optimizeLegibility'
      },
      superscript: {
         verticalAlign: 'super',
         fontSize: '0.675rem',
         lineHeight: '1.55556',
         letterSpacing: '0.0125rem',
         textRendering: 'optimizeLegibility'
      },
      subscript: {
         verticalAlign: 'sub',
         fontSize: '0.675rem',
         lineHeight: '1.55556',
         letterSpacing: '0.0125rem',
         textRendering: 'optimizeLegibility'
      },
      list: {
         marginBottom: "0"
      },
      table: {
         marginBottom: "0",
         paddingLeft: "0.5em",
         paddingRight: "0.5em"
      }
   },
   components: {
      MuiCssBaseline: {
         styleOverrides: `
               a { 
                  text-decoration: none;
               }
               a:link, a:visited, a:hover, a:active  { 
                  color: '${darkText}';
               }
               html {
                  scroll-behavior: smooth;
               }
               span {
                  white-space: pre-wrap;
               }
               div .scrollable {
                 overflow: auto;
                 scrollbar-width: thin; 
                 scrollbar-color: #888 #ddd;
               }
               div .scrollable::-webkit-scrollbar {
                 width: 6px;
               }
               div .scrollable::-webkit-scrollbar-track {
                 background: #ddd;
               }
               div .scrollable::-webkit-scrollbar-thumb {
                 background: #888;
               }
            `
      },
      MuiButton: {
         styleOverrides: {
            root: {
               borderRadius: '0px',
               margin: '0px',
               minHeight: '0px',
               textTransform: 'unset',
               '&:hover': {
                  backgroundColor: Color.lightBlue[100],
                  color: Color.lightBlue[900]
               },
               '& a:hover': {
                  color: Color.lightBlue[900]
               },
               '& a': {
                  color: lightText,
                  textDecoration: 'none'
               },
               '& div': {
                  color: lightText
               },
               '& .MuiSvgIcon-root': {
                  color: lightText
               }
            }
         }
      },
      MuiTableCell: {
         styleOverrides: {
            head: {
               backgroundColor: lightGrey
            }
         }
      }
   }
};

export const LightTheme = createTheme({
   spacing: 1,
   ...lightThemeOverrides
});

export const DarkTheme = createTheme({
   palette: {
      mode: 'dark',
      primary: {
         main: darkPrimaryMain,
         dark: darkPrimaryDark,
         light: darkPrimaryLight,
         contrastText: darkPrimaryContrast,
      },
      grey: {
         // grey[200] is the sidebar + SearchBar surface colour (used in
         // Layout, AddressSpaceTree sidebar wrapper, SearchBar AppBar).
         // The previous remap to grey[600] (#757575) read as "light grey
         // panel" next to dark text, which fought MUI's dark mode light
         // text. grey[800] (#424242) is unambiguously a dark surface
         // while still distinct from background.default (#121212).
         [200]: Color.grey[800],
         [600]: Color.grey[200]
      }
   },
   components: {
      MuiButton: {
         styleOverrides: {
            root: {
               borderRadius: '0px',
               margin: '0px',
               minHeight: '0px',
               textTransform: 'unset',
               // No `& div`/`& .MuiSvgIcon-root` hardcoded colors here —
               // those rules in the light theme are tuned for contained
               // buttons on a primary background; copying them with
               // grey[900] for dark mode painted near-black text/icons
               // inside any button div, which was unreadable against the
               // default dark button surfaces. Let MUI's contrastText
               // resolution handle colour in dark mode.
            }
         }
      },
   },
   spacing: 1
});

export default LightTheme;
