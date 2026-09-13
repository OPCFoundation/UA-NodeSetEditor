import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import type { Node } from '../model/Node';
import { parseNodeId } from '../utils/nodeIdValidation';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import FormControlLabel from '@mui/material/FormControlLabel';
import Checkbox from '@mui/material/Checkbox';
import IconButton from '@mui/material/IconButton';
import InputAdornment from '@mui/material/InputAdornment';
import Tooltip from '@mui/material/Tooltip';
import AccountTreeIcon from '@mui/icons-material/AccountTree';

import { ModelDialog } from './ModelDialog';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { NodePickerDialog } from './NodePickerDialog';

interface BrowseNodeResult {
   nodeId?: string;
   displayName?: string;
   nodeClass: number;
   modelUri?: string;
}

export interface AddReferenceData {
   referenceTypeId: string;
   targetNodeId: string;
   isForward: boolean;
}

export interface AddReferenceDialogProps {
   open: boolean;
   onClose: () => void;
   onSave: (data: AddReferenceData) => void;
   isSaving: boolean;
   saveError: string | null;
   workspaceId: string;
}

export const AddReferenceDialog: React.FC<AddReferenceDialogProps> = ({
   open,
   onClose,
   onSave,
   isSaving,
   saveError,
   workspaceId,
}) => {
   const { t } = useTranslation();
   const [referenceTypeId, setReferenceTypeId] = React.useState('');
   const [targetNodeId, setTargetNodeId] = React.useState('');
   const [isForward, setIsForward] = React.useState(true);
   const [pickerOpen, setPickerOpen] = React.useState(false);

   // Reset form when dialog opens
   React.useEffect(() => {
      if (open) {
         setReferenceTypeId('');
         setTargetNodeId('');
         setIsForward(true);
         setPickerOpen(false);
      }
   }, [open]);

   // Fetch all reference types
   const { data: refTypes } = useQuery({
      queryKey: ['allReferenceTypes', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<Node>>(
            `/opcua/v1/types/reference-types/${slugifyNodeId('i=31')}/subtypes`,
            {
               params: { depth: 10, count: 10000 },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId,
            displayName: n.browseName ?? n.displayName?.text,
            nodeClass: 32,
         })) as BrowseNodeResult[];
      },
      enabled: open,
   });

   const refTypeOptions = React.useMemo((): NodeIdOption[] => {
      return (refTypes ?? []).map((rt): NodeIdOption => ({
         nodeId: rt.nodeId ?? '',
         displayName: rt.displayName ?? '',
         modelUri: rt.modelUri,
      }));
   }, [refTypes]);

   // Namespaces resolve the "[ModelName]:" / "ns=<index>;" prefixes a user may
   // type. Same query key as NodeIdAutocomplete, so this shares its cache entry.
   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
            '/opcua/v1/namespaces/info',
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         return response.data;
      },
      enabled: open,
   });
   const namespaces = React.useMemo(
      () => namespacesData?.results ?? [], [namespacesData]);

   // Manually-typed targets are parsed, not taken on trust: the API stores the
   // reference verbatim, so an unparseable (or misspelled) NodeId would become
   // a dangling reference that only surfaces at export time.
   const parsed = React.useMemo(
      () => (targetNodeId.trim() ? parseNodeId(targetNodeId, namespaces) : null),
      [targetNodeId, namespaces]);
   const parseError = parsed && !parsed.ok ? parsed.error : null;
   const canonicalTargetId = parsed?.ok ? parsed.value.nodeId : null;

   // Debounce so we don't fire an existence check on every keystroke that
   // happens to parse (e.g. "i=1" on the way to "i=1234").
   const [checkedTargetId, setCheckedTargetId] = React.useState<string | null>(null);
   React.useEffect(() => {
      const timer = setTimeout(() => setCheckedTargetId(canonicalTargetId), 300);
      return () => clearTimeout(timer);
   }, [canonicalTargetId]);

   // null = couldn't tell (request failed for some other reason) — never blocks.
   const { data: targetExists, isFetching: isCheckingTarget } = useQuery<boolean | null>({
      queryKey: ['nodeExists', workspaceId, checkedTargetId],
      queryFn: async () => {
         try {
            await api.get(`/opcua/v1/nodes/${slugifyNodeId(checkedTargetId!)}`, {
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            });
            return true;
         } catch (e) {
            if (axios.isAxiosError(e) && e.response?.status === 404) return false;
            return null;
         }
      },
      enabled: open && !!checkedTargetId,
      staleTime: 60 * 1000,
   });

   const targetPending = !!canonicalTargetId
      && (checkedTargetId !== canonicalTargetId || isCheckingTarget);
   const targetError = parseError
      ?? (targetExists === false && !targetPending
         ? t('typeDetail.targetNotFound', 'No node with this NodeId exists in this workspace.')
         : null);

   // Show what a "[Model]:" / "ns=<index>;" input was rewritten to, so the user
   // can see the NodeId that will actually be stored.
   const canonicalHint = canonicalTargetId && canonicalTargetId !== targetNodeId.trim()
      ? `→ ${canonicalTargetId}`
      : null;

   const canSave = referenceTypeId !== '' && !!canonicalTargetId
      && !targetError && !targetPending;

   const handleSave = () => {
      if (!canSave || !canonicalTargetId) return;
      onSave({
         referenceTypeId,
         targetNodeId: canonicalTargetId,
         isForward,
      });
   };

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('typeDetail.addReference')}
         isLoading={isSaving}
         isError={!!saveError}
         error={saveError ? new Error(saveError) : null}
         actions={[
            {
               label: t('common.ok'),
               onClick: handleSave,
               disabled: !canSave || isSaving,
            },
         ]}
      >
         <Box sx={{ pt: 10, px: 6, pb: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
            <NodeIdAutocomplete
               options={refTypeOptions}
               value={referenceTypeId}
               onChange={setReferenceTypeId}
               workspaceId={workspaceId}
               label={t('typeDetail.referenceType')}
            />

            <FormControlLabel
               control={
                  <Checkbox
                     checked={isForward}
                     onChange={(e) => setIsForward(e.target.checked)}
                  />
               }
               label={t('typeDetail.forward')}
            />

            <TextField
               label={t('typeDetail.target')}
               value={targetNodeId}
               onChange={(e) => setTargetNodeId(e.target.value)}
               required
               fullWidth
               size="small"
               placeholder="nsu=http://...;i=123"
               error={!!targetError}
               helperText={targetError ?? canonicalHint ?? ' '}
               slotProps={{
                  inputLabel: { shrink: true },
                  input: {
                     endAdornment: (
                        <InputAdornment position="end">
                           <Tooltip title={t('nodePicker.openTooltip', 'Browse the address space to pick a node')}>
                              <IconButton
                                 size="small"
                                 onClick={() => setPickerOpen(true)}
                                 edge="end"
                              >
                                 <AccountTreeIcon fontSize="small" />
                              </IconButton>
                           </Tooltip>
                        </InputAdornment>
                     ),
                  },
               }}
            />
         </Box>
         <NodePickerDialog
            open={pickerOpen}
            onClose={() => setPickerOpen(false)}
            workspaceId={workspaceId}
            onPick={(nodeId) => {
               setTargetNodeId(nodeId);
               setPickerOpen(false);
            }}
         />
      </ModelDialog>
   );
};
