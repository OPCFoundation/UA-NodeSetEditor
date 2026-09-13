import SvgIcon, { type SvgIconProps } from '@mui/material/SvgIcon';

// A branded variant of MUI's HandymanTwoTone glyph: the tool sits on a
// rounded-square tile with a light-blue gradient fill and border, drawn
// from the toolbar/primary palette in theme.ts. The TwoTone "highlight"
// layer (the semi-transparent tone path in the stock icon) is recoloured
// red so it reads as an accent against the dark tool outline.
const TILE_GRADIENT_FROM = '#E8EFF4'; // theme lightBlue (top-left)
const TILE_GRADIENT_TO = '#A3C4DE';   // light tint of theme mediumBlue (bottom-right)
const TILE_BORDER = '#448FD1';        // theme darkText — blue border
const TOOL_OUTLINE = '#084A79';       // theme darkBlue — solid tool body
const TOOL_HIGHLIGHT = '#d32f2f';     // red — the TwoTone highlight/accent

// Shared gradient id. Multiple rendered instances all reference the same
// (identical) definition, so a stable id is safe.
const GRADIENT_ID = 'handyman-badge-blue';

/**
 * Handyman tool rendered as a rounded-square badge with a light-teal
 * gradient background. Accepts any `SvgIconProps` (fontSize, sx, …); the
 * tile scales with the icon, so set `sx={{ fontSize: 40 }}` (or similar)
 * where a larger emblem is wanted.
 */
export const HandymanBadgeIcon = (props: SvgIconProps) => (
   <SvgIcon viewBox='0 0 24 24' {...props}>
      <defs>
         <linearGradient id={GRADIENT_ID} x1='0' y1='0' x2='1' y2='1'>
            <stop offset='0%' stopColor={TILE_GRADIENT_FROM} />
            <stop offset='100%' stopColor={TILE_GRADIENT_TO} />
         </linearGradient>
      </defs>

      {/* Rounded-square tile: gradient fill + teal border. */}
      <rect
         x='0.5'
         y='0.5'
         width='23'
         height='23'
         rx='5'
         ry='5'
         fill={`url(#${GRADIENT_ID})`}
         stroke={TILE_BORDER}
         strokeWidth='1'
      />

      {/* Tool glyph, inset and centred within the tile (24 * 0.7 = 16.8, so
          (24 - 16.8) / 2 = 3.6 padding on each side). */}
      <g transform='translate(3.6 3.6) scale(0.7)'>
         {/* Highlight/tone layer — red accent. */}
         <path
            d='m8.66 14.64-4.25 4.24.71.71 4.24-4.25zm5.9356.7054.7071-.7072 4.2426 4.2427-.707.7071z'
            fill={TOOL_HIGHLIGHT}
         />
         {/* Solid tool outline. */}
         <path
            d='m21.67 18.17-5.3-5.3h-.99l-2.54 2.54v.99l5.3 5.3c.39.39 1.02.39 1.41 0l2.12-2.12c.39-.38.39-1.02 0-1.41m-2.83 1.42-4.24-4.24.71-.71 4.24 4.24z'
            fill={TOOL_OUTLINE}
         />
         <path
            d='m17.34 10.19 1.41-1.41 2.12 2.12c1.17-1.17 1.17-3.07 0-4.24l-3.54-3.54-1.41 1.41V1.71l-.7-.71-3.54 3.54.71.71h2.83l-1.41 1.41 1.06 1.06-2.89 2.89-4.13-4.13V5.06L4.83 2.04 2 4.87 5.03 7.9h1.41l4.13 4.13-.85.85H7.6l-5.3 5.3c-.39.39-.39 1.02 0 1.41l2.12 2.12c.39.39 1.02.39 1.41 0l5.3-5.3v-2.12l5.15-5.15zm-7.98 5.15-4.24 4.24-.71-.71 4.24-4.24z'
            fill={TOOL_OUTLINE}
         />
      </g>
   </SvgIcon>
);
