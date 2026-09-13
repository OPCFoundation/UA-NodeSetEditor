import * as React from 'react';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import Select from '@mui/material/Select';
import { NODE_ID_PREFIXES, validateNodeIdValue, type NodeIdPrefix } from '../utils/nodeIdValidation';

export interface NodeIdEditorProps {
   prefix: NodeIdPrefix;
   value: string;
   onPrefixChange: (prefix: NodeIdPrefix) => void;
   onValueChange: (value: string) => void;
}

export const NodeIdEditor: React.FC<NodeIdEditorProps> = ({
   prefix,
   value,
   onPrefixChange,
   onValueChange,
}) => {
   const error = value ? validateNodeIdValue(prefix, value) : null;

   return (
      <Box sx={{ display: 'flex', gap: 4, alignItems: 'flex-start' }}>
         <Select
            value={prefix}
            onChange={(e) => onPrefixChange(e.target.value as NodeIdPrefix)}
            size="small"
            sx={{ width: 80 }}
         >
            {NODE_ID_PREFIXES.map((p) => (
               <MenuItem key={p} value={p}>{p}=</MenuItem>
            ))}
         </Select>
         <TextField
            label="NodeId"
            value={value}
            onChange={(e) => onValueChange(e.target.value)}
            fullWidth
            size="small"
            slotProps={{ inputLabel: { shrink: true } }}
            error={!!error}
            helperText={error}
         />
      </Box>
   );
};
