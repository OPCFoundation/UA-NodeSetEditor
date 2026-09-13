import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import Divider from '@mui/material/Divider';
import Chip from '@mui/material/Chip';
import VisibilityIcon from '@mui/icons-material/Visibility';

import WorkspacesIcon from '@mui/icons-material/Workspaces';
import FilterCenterFocusIcon from '@mui/icons-material/FilterCenterFocus';
import RotateRightIcon from '@mui/icons-material/RotateRight';
import VerticalAlignBottomIcon from '@mui/icons-material/VerticalAlignBottom';
import VerticalAlignTopIcon from '@mui/icons-material/VerticalAlignTop';

import api from '../api/axios.api';
import { WorkspaceContext } from '../WorkspaceContext';
import { idToUrn, urnToId } from '../model/WorkspaceDescription';
import type { WorkspaceDescription, PaginatedResponse } from '../model/WorkspaceDescription';
import { ModelSelect } from './ModelSelect';

interface SidebarToolbarProps {
   focused: boolean;
   onToggleFocused: () => void;
   /** Disable the focus toggle when there's no selectable target. */
   focusDisabled?: boolean;
   /** Align the tree (focused or complete) with the currently displayed node. */
   onSyncTree?: () => void;
   /** Disable the sync button when there's nothing displayed to sync to. */
   syncDisabled?: boolean;
}

/**
 * Top-of-sidebar context bar: Model selector | Workspace selector | Focus toggle.
 * Both selectors are Button+Menu (compact) rather than Select fields so they fit
 * the 300px sidebar without label chrome. Workspace lives here because, while
 * switching is rare, having it next to the model keeps the "what context am I
 * in" answer in one place.
 */
export const SidebarToolbar: React.FC<SidebarToolbarProps> = ({
   focused,
   onToggleFocused,
   focusDisabled,
   onSyncTree,
   syncDisabled,
}) => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   const location = useLocation();
   const {
      selectedWorkspaceId,
      setSelectedWorkspaceId,
      selectedModelUri,
      setSelectedModelUri,
      setSelectedType,
   } = React.useContext(WorkspaceContext);

   const [wsAnchor, setWsAnchor] = React.useState<HTMLElement | null>(null);

   // Workspace view = the model-library page. Editor view = type_library.
   // Toggle bounces between them so the user can manage the workspace's
   // models without leaving the sidebar surface.
   const isWorkspaceView = location.pathname === '/model_library';
   const handleToggleWorkspaceView = () => {
      navigate(isWorkspaceView ? '/type_library' : '/model_library');
   };

   // queryFn shape must match the other ['discovery'] consumers
   // (ModelLibraryPage, WorkspaceSelector) — they all return the full
   // PaginatedResponse. Returning a bare array here was the original
   // cache-shape collision that broke the WorkspaceSelector.
   const { data: discoveryData } = useQuery({
      queryKey: ['discovery'],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceDescription>>(
            '/opcua/v1/discovery',
            { params: { start: 0, count: 100 } },
         );
         return response.data;
      },
   });
   const workspaces = discoveryData?.results ?? [];

   // Auto-init: when discovery resolves and either nothing is selected or the
   // stored selection is no longer valid, fall back to the server-marked
   // default (or the first workspace). This used to live in the WorkspaceSelector
   // mounted on ModelLibraryPage; consolidated here so it fires the moment the
   // sidebar appears on any route.
   React.useEffect(() => {
      if (workspaces.length === 0) return;
      const stillValid = selectedWorkspaceId
         && workspaces.some((w) => urnToId(w.applicationUri) === selectedWorkspaceId);
      if (stillValid) return;
      const fallback = workspaces.find((w) => w.isDefault) ?? workspaces[0];
      setSelectedWorkspaceId(urnToId(fallback.applicationUri));
   }, [workspaces, selectedWorkspaceId, setSelectedWorkspaceId]);

   const currentWorkspace = workspaces?.find(
      (w) => urnToId(w.applicationUri) === selectedWorkspaceId,
   );
   const currentWorkspaceLabel =
      currentWorkspace?.applicationName?.text ?? t('sidebarToolbar.noWorkspace', 'Workspace');

   const handleSelectWorkspace = (id: string) => {
      setSelectedWorkspaceId(id);
      // The previous workspace's model selection rarely makes sense in
      // the new one — at best it's a stale namespace URI that doesn't
      // belong, at worst it silently filters the new workspace's tree by
      // a model it doesn't contain. Drop back to "All Models" so the
      // user gets a clean view of the new workspace.
      setSelectedModelUri('');
      // Clear any selected type so a workspace switch while in the type
      // detail subview drops back to the top-level list — the displayed
      // NodeId belongs to the previous workspace and won't resolve in
      // the new one. (TypeLibraryPage's selectedType → URL sync clears
      // the ?type= search params from here.)
      setSelectedType(null);
      // Best-effort persist; same pattern used by the legacy WorkspaceSelector.
      api.put('/opcua/v1/user/preferences', { selectedServer: idToUrn(id) }).catch(() => { });
      setWsAnchor(null);
   };

   return (
      <Box
         sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 0.5,
            px: 1,
            py: 0.5,
            borderBottom: 1,
            borderColor: 'divider',
            minHeight: 40,
         }}
      >
         <ModelSelect
            workspaceId={selectedWorkspaceId}
            value={selectedModelUri}
            onChange={setSelectedModelUri}
         />

         <Tooltip title={t('sidebarToolbar.workspaceTooltip', 'Active workspace')}>
            <IconButton
               size="small"
               color="primary"
               onClick={(e) => setWsAnchor(e.currentTarget)}
               sx={{ flexShrink: 0 }}
            >
               <WorkspacesIcon fontSize="small" />
            </IconButton>
         </Tooltip>
         <Menu
            anchorEl={wsAnchor}
            open={Boolean(wsAnchor)}
            onClose={() => setWsAnchor(null)}
         >
            <MenuItem disabled sx={{ opacity: '1 !important' }}>
               <Typography variant="caption" color="text.secondary">
                  {t('sidebarToolbar.workspaceHeader', 'Workspace: {{name}}', { name: currentWorkspaceLabel })}
               </Typography>
            </MenuItem>
            <Divider />
            {(workspaces ?? []).map((ws) => {
               const id = urnToId(ws.applicationUri);
               return (
                  <MenuItem
                     key={ws.applicationUri}
                     selected={id === selectedWorkspaceId}
                     onClick={() => handleSelectWorkspace(id)}
                  >
                     {ws.applicationName?.text ?? ws.applicationUri}
                  </MenuItem>
               );
            })}
         </Menu>

         {currentWorkspace && currentWorkspace.canWrite === false && (
            <Tooltip title={t('sidebarToolbar.readOnlyTooltip', 'This workspace is shared with you and is read-only. Only the owner can make changes.')}>
               <Chip
                  size="small"
                  variant="outlined"
                  color="default"
                  icon={<VisibilityIcon fontSize="small" />}
                  label={t('sidebarToolbar.readOnly', 'Read-only')}
                  sx={{ flexShrink: 0, height: 22 }}
               />
            </Tooltip>
         )}

         <Tooltip
            title={isWorkspaceView
               ? t('sidebarToolbar.editorView', 'Edit models in workspace')
               : t('sidebarToolbar.workspaceView', 'Manage models in this workspace')}
         >
            <IconButton
               size="small"
               color="primary"
               onClick={handleToggleWorkspaceView}
               sx={{ flexShrink: 0 }}
            >
               {/* "Up" icon when the click takes you to the workspace
                   (a level above any single model); "down" icon when the
                   click drops back into the model editor. */}
               {isWorkspaceView
                  ? <VerticalAlignBottomIcon fontSize="small" />
                  : <VerticalAlignTopIcon fontSize="small" />}
            </IconButton>
         </Tooltip>

         <Tooltip
            title={focused
               ? t('sidebarToolbar.focusOff', 'Show complete address space')
               : t('sidebarToolbar.focusOn', 'Focus on selected node')}
         >
            <span>
               <IconButton
                  size="small"
                  onClick={onToggleFocused}
                  disabled={focusDisabled}
                  color="primary"
                  sx={{
                     flexShrink: 0,
                     // Pressed state shown via tinted background rather than a
                     // colour swap, since the icon itself stays the same.
                     backgroundColor: focused ? 'action.selected' : undefined,
                  }}
               >
                  <FilterCenterFocusIcon fontSize="small" />
               </IconButton>
            </span>
         </Tooltip>

         <Tooltip
            title={t('sidebarToolbar.syncTree', 'Sync tree with displayed node')}
         >
            <span>
               <IconButton
                  size="small"
                  onClick={onSyncTree}
                  disabled={syncDisabled}
                  color="primary"
                  sx={{ flexShrink: 0 }}
               >
                  <RotateRightIcon fontSize="small" />
               </IconButton>
            </span>
         </Tooltip>
      </Box>
   );
};

export default SidebarToolbar;
