import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Button from '@mui/material/Button';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import Chip from '@mui/material/Chip';
import VisibilityIcon from '@mui/icons-material/Visibility';

import FolderOutlinedIcon from '@mui/icons-material/FolderOutlined';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import MyLocationIcon from '@mui/icons-material/MyLocation';
import FilterCenterFocusIcon from '@mui/icons-material/FilterCenterFocus';

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
   const {
      selectedWorkspaceId,
      setSelectedWorkspaceId,
      selectedModelUri,
      setSelectedModelUri,
      setSelectedType,
   } = React.useContext(WorkspaceContext);

   const [wsAnchor, setWsAnchor] = React.useState<HTMLElement | null>(null);

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
      currentWorkspace?.applicationName?.text ?? t('sidebarToolbar.noWorkspace', 'OPC UA Server');

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
			flexDirection: 'column',
			gap: 8,
			px: 12,
			py: 12,
			borderBottom: 1,
			borderColor: 'divider',
		 }}
	  >
		 {/* Server picker — labelled so it's obvious what is being switched. */}
		 <Box sx={{ display: 'flex', alignItems: 'center', gap: 6 }}>
			<Tooltip title={t('sidebarToolbar.workspaceTooltip', 'Active OPC UA server')}>
			   <Button
				  onClick={(e) => setWsAnchor(e.currentTarget)}
				  startIcon={<FolderOutlinedIcon />}
				  endIcon={<ExpandMoreIcon />}
				  sx={{
					 flexGrow: 1,
					 minWidth: 0,
					 justifyContent: 'flex-start',
					 borderRadius: 10,
					 py: 6,
					 px: 10,
					 color: 'text.primary',
					 bgcolor: 'background.paper',
					 border: 1,
					 borderColor: 'divider',
					 '& .MuiButton-endIcon': { ml: 'auto' },
				  }}
			   >
				  <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', minWidth: 0 }}>
					 <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.2 }}>
						{t('sidebarToolbar.projectLabel', 'OPC UA Server')}
					 </Typography>
					 <Typography variant="body1" noWrap sx={{ fontWeight: 600, lineHeight: 1.3, maxWidth: '100%' }}>
						{currentWorkspaceLabel}
					 </Typography>
				  </Box>
			   </Button>
			</Tooltip>
			{currentWorkspace && currentWorkspace.canWrite === false && (
			   <Tooltip title={t('sidebarToolbar.readOnlyTooltip', 'This OPC UA server is shared with you and is read-only. Only the owner can make changes.')}>
				  <Chip
					 size="small"
					 variant="outlined"
					 icon={<VisibilityIcon fontSize="small" />}
					 label={t('sidebarToolbar.readOnly', 'Read-only')}
					 sx={{ flexShrink: 0 }}
				  />
			   </Tooltip>
			)}
		 </Box>
		 <Menu
			anchorEl={wsAnchor}
			open={Boolean(wsAnchor)}
			onClose={() => setWsAnchor(null)}
		 >
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

		 {/* Model filter */}
		 <Box sx={{ display: 'flex', alignItems: 'center', minWidth: 0 }}>
			<ModelSelect
			   workspaceId={selectedWorkspaceId}
			   value={selectedModelUri}
			   onChange={setSelectedModelUri}
			/>
		 </Box>

		 {/* Tree actions — icon plus a word, so their purpose is clear. */}
		 <Box sx={{ display: 'flex', gap: 6 }}>
			<Tooltip
			   title={focused
				  ? t('sidebarToolbar.focusOff', 'Show complete address space')
				  : t('sidebarToolbar.focusOn', 'Focus on selected node')}
			>
			   <span style={{ flex: 1, display: 'flex' }}>
				  <Button
					 fullWidth
					 size="small"
					 variant={focused ? 'contained' : 'outlined'}
					 startIcon={<FilterCenterFocusIcon />}
					 onClick={onToggleFocused}
					 disabled={focusDisabled}
				  >
					 {t('sidebarToolbar.focusLabel', 'Focus')}
				  </Button>
			   </span>
			</Tooltip>
			<Tooltip title={t('sidebarToolbar.syncTree', 'Sync tree with displayed node')}>
			   <span style={{ flex: 1, display: 'flex' }}>
				  <Button
					 fullWidth
					 size="small"
					 variant="outlined"
					 startIcon={<MyLocationIcon />}
					 onClick={onSyncTree}
					 disabled={syncDisabled}
				  >
					 {t('sidebarToolbar.syncLabel', 'Locate')}
				  </Button>
			   </span>
			</Tooltip>
		 </Box>
	  </Box>
   );
};

export default SidebarToolbar;
