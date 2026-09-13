import * as React from 'react';
import { useQuery } from '@tanstack/react-query';

import api from '../api/axios.api';
import { WorkspaceContext } from '../WorkspaceContext';
import { urnToId } from '../model/WorkspaceDescription';
import type { WorkspaceDescription, PaginatedResponse } from '../model/WorkspaceDescription';

/**
 * Resolves the currently-selected workspace from the shared ['discovery']
 * query cache (the same query SidebarToolbar / WorkspaceSelector populate).
 * The queryFn matches those consumers so this hook reuses the cached result
 * rather than issuing a second request.
 */
export function useCurrentWorkspace(): WorkspaceDescription | undefined {
   const { selectedWorkspaceId } = React.useContext(WorkspaceContext);

   const { data } = useQuery({
      queryKey: ['discovery'],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceDescription>>(
            '/opcua/v1/discovery',
            { params: { start: 0, count: 100 } },
         );
         return response.data;
      },
   });

   return React.useMemo(() => {
      if (!selectedWorkspaceId || !data?.results) return undefined;
      return data.results.find((ws) => urnToId(ws.applicationUri) === selectedWorkspaceId);
   }, [data, selectedWorkspaceId]);
}

/**
 * Whether the current user may edit the selected workspace. Defaults to false
 * until discovery resolves, so edit affordances stay disabled rather than
 * flashing enabled for read-only (shared) workspaces.
 */
export function useCanWriteWorkspace(): boolean {
   const workspace = useCurrentWorkspace();
   return workspace?.canWrite ?? false;
}
