import * as React from 'react';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { NodeIdLink } from './NodeIdLink';
import { formatValueRankSuffix } from '../model/NodeFormatting';

const UA_DATA_TYPE_NODE_CLASS = 64;

interface DataTypeCellProps {
   dataTypeId?: string | null;
   dataTypeName?: string | null;
   valueRank?: number | null;
}

/**
 * Render a DataType reference together with a ValueRank decoration
 * (e.g. `[]`, `[*]`, `[0..*]`). Used in the Children and DataType-fields
 * tables so the same DataType + suffix presentation lives in one place.
 */
export const DataTypeCell: React.FC<DataTypeCellProps> = ({
   dataTypeId,
   dataTypeName,
   valueRank,
}) => {
   const suffix = formatValueRankSuffix(valueRank);
   const name = dataTypeName ?? '';

   if (!dataTypeId) {
      return <>{suffix ? `${name} ${suffix}` : name}</>;
   }

   return (
      <Box component="span" sx={{ display: 'inline-flex', alignItems: 'baseline', gap: 4 }}>
         <NodeIdLink
            nodeId={dataTypeId}
            displayName={name}
            nodeClass={UA_DATA_TYPE_NODE_CLASS}
         />
         {suffix && (
            <Typography component="span" variant="body2">
               {suffix}
            </Typography>
         )}
      </Box>
   );
};
