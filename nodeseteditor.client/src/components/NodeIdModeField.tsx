import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Box from '@mui/material/Box';
import InputAdornment from '@mui/material/InputAdornment';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';

import type { RequestedNodeIdState } from '../hooks/useRequestedNodeId';

export interface NodeIdModeFieldProps {
   /** The state returned by useRequestedNodeId. */
   state: RequestedNodeIdState;
}

/**
 * The create-time NodeId choice: let the server allocate a numeric id, or give
 * the node a string identifier. Shown only while creating — a NodeId cannot be
 * changed afterwards, which is why the edit dialogs render it read-only.
 */
export const NodeIdModeField: React.FC<NodeIdModeFieldProps> = ({ state }) => {
   const { t } = useTranslation();
   const { mode, setMode, identifier, setIdentifier, error, isChecking } = state;

   return (
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
         <TextField
            label={t('typeDetail.nodeId', 'NodeId')}
            value={mode}
            onChange={(e) => setMode(e.target.value as typeof mode)}
            select
            fullWidth
            size="small"
            slotProps={{ inputLabel: { shrink: true } }}
            helperText={mode === 'auto'
               ? t('typeDetail.nodeIdAutoHint', 'A numeric NodeId is assigned when the node is created.')
               : undefined}
         >
            <MenuItem value="auto">{t('typeDetail.nodeIdAuto', 'Auto (numeric)')}</MenuItem>
            <MenuItem value="string">{t('typeDetail.nodeIdString', 'String')}</MenuItem>
         </TextField>

         {mode === 'string' && (
            <TextField
               label={t('typeDetail.nodeIdIdentifier', 'String identifier')}
               value={identifier}
               onChange={(e) => setIdentifier(e.target.value)}
               required
               fullWidth
               size="small"
               error={!!error}
               helperText={error
                  ?? (isChecking
                     ? t('typeDetail.nodeIdChecking', 'Checking…')
                     : t('typeDetail.nodeIdStringHint', 'Fixed once the node is created; editing the BrowseName later will not change it.'))}
               slotProps={{
                  inputLabel: { shrink: true },
                  input: {
                     startAdornment: <InputAdornment position="start">s=</InputAdornment>,
                  },
               }}
            />
         )}
      </Box>
   );
};
