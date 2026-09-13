import * as React from 'react';
import type { SvgIconProps } from '@mui/material/SvgIcon';

import FolderIcon from '@mui/icons-material/Folder';
import DataObjectIcon from '@mui/icons-material/DataObject';
import FunctionsIcon from '@mui/icons-material/Functions';
import CategoryIcon from '@mui/icons-material/Category';
import TuneIcon from '@mui/icons-material/Tune';
import SchemaIcon from '@mui/icons-material/Schema';
import LinkIcon from '@mui/icons-material/Link';
import WidgetsIcon from '@mui/icons-material/Widgets';

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

/**
 * Glyph for a NodeClass. Pass any SvgIconProps (fontSize, sx, color…) to
 * tweak size — e.g. `{ sx: { fontSize: 16 } }` for tree-row use where the
 * default `fontSize="small"` (~20px) is too tall for body2 text.
 */
export function getNodeClassIcon(nodeClass: number, props?: SvgIconProps): React.ReactElement {
   const iconProps: SvgIconProps = { fontSize: 'small', ...props };
   switch (nodeClass) {
      case NodeClass.Object:
         return <FolderIcon {...iconProps} />;
      case NodeClass.Variable:
         return <DataObjectIcon {...iconProps} />;
      case NodeClass.Method:
         return <FunctionsIcon {...iconProps} />;
      case NodeClass.ObjectType:
         return <CategoryIcon {...iconProps} />;
      case NodeClass.VariableType:
         return <TuneIcon {...iconProps} />;
      case NodeClass.DataType:
         return <SchemaIcon {...iconProps} />;
      case NodeClass.ReferenceType:
         return <LinkIcon {...iconProps} />;
      default:
         return <WidgetsIcon {...iconProps} />;
   }
}
