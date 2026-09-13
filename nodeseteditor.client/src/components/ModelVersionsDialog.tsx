import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Chip from '@mui/material/Chip';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import DeleteIcon from '@mui/icons-material/Delete';

import api, { ApiError } from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import { ModelDialog } from './ModelDialog';
import { StripedTable, type StripedTableColumn } from './StripedTable';

export interface ModelVersionInfo {
   id: string;
   version?: string | null;
   publicationDate?: string | null;
   /** "CloudLibrary" | "Upload" | "Authored" | "Unknown" */
   origin?: string | null;
   isPrivate: boolean;
   isPublished: boolean;
   isCurrent: boolean;
   isEditable: boolean;
   otherWorkspaceCount: number;
   canDelete: boolean;
   /** Why canDelete is false — shown on the disabled delete button. */
   blockedReason?: string | null;
}

interface ModelVersionsDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /** Id of the model whose namespace URI's versions are being listed. */
   modelId: string;
   modelUri?: string | null;
   /** Deleting is only offered when the user can write to the workspace. */
   canWrite: boolean;
   /** Called after a version is deleted so the caller can refresh its own lists. */
   onDeleted?: () => void;
}

function formatDate(value: string | null | undefined): string {
   if (!value) return '';
   const date = new Date(value);
   return isNaN(date.getTime()) ? value : date.toISOString().split('T')[0];
}

export const ModelVersionsDialog: React.FC<ModelVersionsDialogProps> = ({
   open,
   onClose,
   workspaceId,
   modelId,
   modelUri,
   canWrite,
   onDeleted,
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const [deleteError, setDeleteError] = React.useState<string | null>(null);
   const [deletingId, setDeletingId] = React.useState<string | null>(null);
   // Two-step delete: the first click arms the row, the second confirms. Cheaper than a
   // nested confirmation dialog, and this one is destructive enough to want the pause.
   const [confirmingId, setConfirmingId] = React.useState<string | null>(null);

   const { data, isLoading, isError, error } = useQuery({
      queryKey: ['modelVersions', workspaceId, modelId],
      queryFn: async () => {
         const response = await api.get<ModelVersionInfo[]>(
            `/opcua/v1/namespaces/info/${modelId}/versions`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         return response.data;
      },
      enabled: open && !!modelId && !!workspaceId,
   });

   const handleDelete = async (versionId: string) => {
      setDeletingId(versionId);
      setDeleteError(null);
      try {
         await api.delete(
            `/opcua/v1/namespaces/info/${modelId}/versions/${versionId}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         setConfirmingId(null);
         await queryClient.invalidateQueries({ queryKey: ['modelVersions', workspaceId, modelId] });
         onDeleted?.();
      } catch (e) {
         setDeleteError(e instanceof ApiError || e instanceof Error ? e.message : 'Failed to delete version');
      } finally {
         setDeletingId(null);
      }
   };

   const columns: StripedTableColumn[] = [
      { key: 'version', label: t('modelVersions.version', 'Version'), width: '28%' },
      { key: 'origin', label: t('modelVersions.origin', 'Origin'), width: '20%' },
      { key: 'published', label: t('modelVersions.publicationDate', 'Published'), width: '20%' },
      { key: 'status', label: t('modelVersions.status', 'Status'), width: '22%' },
      { key: 'actions', label: '', width: '10%' },
   ];

   const originLabel = (origin: string | null | undefined) => {
      switch (origin) {
         case 'CloudLibrary': return t('modelVersions.originCloudLibrary', 'Cloud Library');
         case 'Upload': return t('modelVersions.originUpload', 'Uploaded');
         case 'Authored': return t('modelVersions.originAuthored', 'Authored here');
         default: return t('modelVersions.originUnknown', 'Unknown');
      }
   };

   const versions = data ?? [];

   const rows = versions.map((v) => {
      // Status is the one thing that decides whether the row can be deleted, so say the
      // most restrictive fact first rather than listing every flag.
      const status = v.isEditable
         ? t('modelVersions.statusCheckedOut', 'Checked out')
         : v.isCurrent
            ? t('modelVersions.statusCurrent', 'In use')
            : v.isPublished
               ? t('modelVersions.statusPublished', 'Published')
               : v.otherWorkspaceCount > 0
                  ? t('modelVersions.statusShared', 'Used elsewhere')
                  : t('modelVersions.statusBackup', 'Kept copy');

      const deleteDisabled = !canWrite || !v.canDelete || deletingId != null;
      const armed = confirmingId === v.id;
      const tooltip = !canWrite
         ? t('modelVersions.readOnlyWorkspace', 'You do not have write access to this workspace.')
         : v.canDelete
            ? (armed
               ? t('modelVersions.confirmDelete', 'Click again to delete this version permanently.')
               : t('modelVersions.deleteVersion', 'Delete this version'))
            : v.blockedReason ?? t('modelVersions.cannotDelete', 'This version cannot be deleted.');

      return {
         version: (
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 4 }}>
               <span>{v.version ?? ''}</span>
               {v.isCurrent && (
                  <Chip size="small" color="primary" variant="outlined"
                     label={t('modelVersions.currentChip', 'Current')} />
               )}
            </Box>
         ),
         origin: originLabel(v.origin),
         published: formatDate(v.publicationDate),
         status,
         actions: (
            <Tooltip title={tooltip}>
               {/* A disabled IconButton fires no events, so the tooltip needs a live wrapper. */}
               <span>
                  <IconButton
                     size="small"
                     color={armed ? 'error' : 'default'}
                     disabled={deleteDisabled}
                     onClick={() => (armed ? handleDelete(v.id) : setConfirmingId(v.id))}
                     aria-label={t('modelVersions.deleteVersion', 'Delete this version')}
                  >
                     <DeleteIcon fontSize="small" />
                  </IconButton>
               </span>
            </Tooltip>
         ),
      };
   });

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('modelVersions.dialogTitle', 'Model Versions')}
         maxWidth="md"
         isLoading={isLoading || deletingId != null}
         isError={isError}
         error={error instanceof Error ? error : null}
      >
         <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Typography variant="body2" color="text.secondary">
               {modelUri}
            </Typography>
            {deleteError && (
               <Typography variant="body2" color="error">{deleteError}</Typography>
            )}
            {versions.length === 0 ? (
               <Typography variant="body2" color="text.secondary">
                  {t('modelVersions.noVersions', 'No stored versions found.')}
               </Typography>
            ) : (
               <StripedTable columns={columns} rows={rows} />
            )}
         </Box>
      </ModelDialog>
   );
};
