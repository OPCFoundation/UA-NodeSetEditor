import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    rules: {
      // Icons come from src/icons, which maps every concept to one Material Symbol via
      // icon-map.json (the file the Blazor/ASP.NET apps share). Importing an icon
      // package directly is how the UI drifted into filled-vs-outlined duplicates of
      // the same glyph and two different icons for one NodeClass.
      'no-restricted-imports': ['error', {
        patterns: [{
          group: ['@mui/icons-material', '@mui/icons-material/*', '@iconify/react'],
          message: "Import icons from 'src/icons' instead — add the concept to src/icons/icon-map.json if it is missing.",
        }],
      }],
    },
  },
  {
    // The registry is the one place allowed to touch the icon packages.
    files: ['src/icons/**'],
    rules: { 'no-restricted-imports': 'off' },
  },
])
