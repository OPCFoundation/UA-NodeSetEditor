import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import IconButton from '@mui/material/IconButton';
import InputAdornment from '@mui/material/InputAdornment';
import TextField from '@mui/material/TextField';
import Tooltip from '@mui/material/Tooltip';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { getNodePlainName } from '../model/Node';
import type { Node as RestNode } from '../model/Node';
import { buildNamespaceMap, formatNodeId } from '../utils/formatNodeId';
import { AccountTreeIcon } from '../icons';

import { NodePickerDialog } from './NodePickerDialog';

interface NodeLocationFieldProps {
   workspaceId: string;
   /** NodeId of the node the new one will sit under. */
   value: string;
   /**
    * Omit to make the field read-only — what cloning does, where the copy has to land
    * beside the original and the location is therefore not the user's to choose.
    */
   onChange?: (nodeId: string) => void;
   label?: string;
}

/**
 * Shows where a node will be placed, and (when it can be changed) lets the user browse
 * the address space for somewhere else. Reads as a name — "Objects (i=85)" — rather than
 * the bare NodeId the user would otherwise have to decode.
 */
export const NodeLocationField: React.FC<NodeLocationFieldProps> = ({
   workspaceId,
   value,
   onChange,
   label,
}) => {
   const { t } = useTranslation();
   const [pickerOpen, setPickerOpen] = React.useState(false);
   const headers = React.useMemo(
      () => ({ 'OpcUa-Server': idToUrn(workspaceId) }),
      [workspaceId],
   );

   const { data: node } = useQuery({
      queryKey: ['nodeLabel', workspaceId, value],
      queryFn: async () => {
         const response = await api.get<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(value)}`, { headers });
         return response.data;
      },
      enabled: !!value,
      staleTime: 60 * 1000,
   });

   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
            '/opcua/v1/namespaces/info', { headers });
         return response.data;
      },
   });

   const text = React.useMemo(() => {
      if (!value) return t('typeDetail.locationNone', 'None');
      const nsMap = buildNamespaceMap(namespacesData?.results ?? []);
      const id = formatNodeId(value, nsMap);
      const name = node ? getNodePlainName(node) : '';
      return name ? `${name} (${id})` : id;
   }, [value, node, namespacesData, t]);

   return (
      <>
         <TextField
            label={label ?? t('typeDetail.location', 'Location')}
            value={text}
            fullWidth
            size="small"
            slotProps={{
               inputLabel: { shrink: true },
               input: {
                  readOnly: true,
                  endAdornment: onChange ? (
                     <InputAdornment position="end">
                        <Tooltip title={t('nodePicker.openTooltip', 'Browse the address space to pick a node')}>
                           <IconButton size="small" onClick={() => setPickerOpen(true)}>
                              <AccountTreeIcon fontSize="small" />
                           </IconButton>
                        </Tooltip>
                     </InputAdornment>
                  ) : undefined,
               },
            }}
         />
         {onChange && (
            <NodePickerDialog
               open={pickerOpen}
               onClose={() => setPickerOpen(false)}
               workspaceId={workspaceId}
               title={t('typeDetail.locationPickerTitle', 'Select the Location')}
               onPick={(picked) => {
                  onChange(picked);
                  setPickerOpen(false);
               }}
            />
         )}
      </>
   );
};
