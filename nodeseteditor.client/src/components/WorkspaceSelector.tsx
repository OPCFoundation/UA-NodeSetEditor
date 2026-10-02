import * as React from 'react';
import { useQuery } from '@tanstack/react-query';

import FormControl from '@mui/material/FormControl';
import InputLabel from '@mui/material/InputLabel';
import Select from '@mui/material/Select';
import MenuItem from '@mui/material/MenuItem';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { SelectChangeEvent } from '@mui/material/Select';

import api from '../api/axios.api';
import { WorkspaceContext } from '../WorkspaceContext';
import { urnToId, idToUrn } from '../model/WorkspaceDescription';
import type { WorkspaceDescription, PaginatedResponse } from '../model/WorkspaceDescription';

/**
 * Page-level workspace dropdown used in ModelLibraryPage's header. Mirrors the
 * Sidebar toolbar's selection state via WorkspaceContext, so changes here
 * propagate everywhere. The auto-init-on-first-load logic lives in
 * SidebarToolbar (which is mounted whenever the address-space tree is on
 * screen), so this component is a pure controlled selector.
 */
export const WorkspaceSelector: React.FC = () => {
   const { selectedWorkspaceId, setSelectedWorkspaceId } = React.useContext(WorkspaceContext);

   const { data: discoveryData, isLoading } = useQuery({
      queryKey: ['discovery'],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceDescription>>('/opcua/v1/discovery', {
            params: { start: 0, count: 100 }
         });
         return response.data;
      },
      retry: 2
   });

   const handleChange = (event: SelectChangeEvent<string>) => {
      const id = event.target.value;
      setSelectedWorkspaceId(id);
      api.put('/opcua/v1/user/preferences', { selectedServer: idToUrn(id) }).catch(() => { /* best-effort */ });
   };

   if (isLoading) {
      return (
         <Box sx={{ display: 'flex', alignItems: 'center', minWidth: 200, gap: 8 }}>
            <CircularProgress size={20} />
            <Typography variant="body2">Loading...</Typography>
         </Box>
      );
   }

   const servers = discoveryData?.results ?? [];
   if (servers.length === 0) {
      return (
         <Box sx={{ display: 'flex', alignItems: 'center', minWidth: 200 }}>
            <Typography variant="body2" color="error">No workspaces defined.</Typography>
         </Box>
      );
   }

   return (
      <FormControl size="small" sx={{ minWidth: 200 }}>
         <InputLabel id="workspace-select-label">Workspace</InputLabel>
         <Select
            labelId="workspace-select-label"
            id="workspace-select"
            value={selectedWorkspaceId}
            label="Workspace"
            onChange={handleChange}
         >
            {servers.map((ws: WorkspaceDescription) => {
               const name = ws.applicationName?.text ?? 'Unnamed';
               const label = ws.owner ? `${name} (${ws.owner})` : name;
               return (
                  <MenuItem key={ws.applicationUri} value={urnToId(ws.applicationUri)}>
                     {label}
                  </MenuItem>
               );
            })}
         </Select>
      </FormControl>
   );
};

export default WorkspaceSelector;
