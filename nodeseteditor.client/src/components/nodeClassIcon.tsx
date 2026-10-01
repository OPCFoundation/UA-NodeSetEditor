import * as React from 'react';
import Box from '@mui/material/Box';
import type { SvgIconProps } from '@mui/material/SvgIcon';

/** Numeric NodeClass values per OPC UA Part 3. */
export const NodeClass = {
   Object: 1,
   Variable: 2,
   Method: 4,
   ObjectType: 8,
   VariableType: 16,
   ReferenceType: 32,
   DataType: 64,
   View: 128,
} as const;

const monoFont = '"SF Mono", ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace';

const badges: Record<number, { text: string; title: string; rgb?: string }> = {
   [NodeClass.Object]: { text: 'O', title: 'Object', rgb: '52, 199, 89' },
   [NodeClass.Variable]: { text: 'V', title: 'Variable', rgb: '0, 113, 227' },
   [NodeClass.Method]: { text: 'M', title: 'Method', rgb: '255, 159, 10' },
   [NodeClass.ObjectType]: { text: 'OT', title: 'ObjectType' },
   [NodeClass.VariableType]: { text: 'VT', title: 'VariableType' },
   [NodeClass.ReferenceType]: { text: 'RT', title: 'ReferenceType' },
   [NodeClass.DataType]: { text: 'DT', title: 'DataType' },
   [NodeClass.View]: { text: 'VW', title: 'View' },
};

/**
 * NodeClass badge, matching the UA Edge Translator address-space tree: a small
 * monospace abbreviation (O, V, M, OT, VT, RT, DT, VW) in a rounded chip. Objects,
 * Variables and Methods are tinted green, blue and orange; type nodes stay neutral.
 * The props parameter is kept for call-site compatibility; only its sx is applied.
 */
export function getNodeClassIcon(nodeClass: number, props?: SvgIconProps): React.ReactElement {
   const badge = badges[nodeClass] ?? { text: '?', title: 'Unknown' };
   const tint = badge.rgb
	  ? { color: `rgb(${badge.rgb})`, backgroundColor: `rgba(${badge.rgb}, 0.12)`, borderColor: `rgba(${badge.rgb}, 0.25)` }
	  : { color: 'text.secondary', backgroundColor: 'action.hover', borderColor: 'divider' };
   return (
	  <Box
		 component="span"
		 title={badge.title}
		 sx={{
			display: 'inline-flex',
			alignItems: 'center',
			justifyContent: 'center',
			minWidth: 22,
			height: 18,
			px: 5,
			fontFamily: monoFont,
			fontSize: 10,
			fontWeight: 600,
			lineHeight: 1,
			borderRadius: '5px',
			border: 1,
			flex: '0 0 auto',
			...tint,
			...((props?.sx as object) ?? {}),
		 }}
	  >
		 {badge.text}
	  </Box>
   );
}
