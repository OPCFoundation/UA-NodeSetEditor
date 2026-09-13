import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';

import Box from '@mui/material/Box';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import { getNodePlainName } from '../model/Node';
import type { Node } from '../model/Node';

import { ModelDialog } from './ModelDialog';
import { NodeAttributesPanel } from './NodeAttributesPanel';

interface ViewNodeDialogProps {
   workspaceId: string;
   /** The NodeId to look up. If null/empty, the dialog stays closed. */
   nodeId: string | null;
   onClose: () => void;
}

/**
 * Read-only popup that shows a node's core attributes. Used when the user
 * clicks a NodeId-typed field inside the JSON value editor: if the target
 * resolves in the workspace AddressSpace we surface its details; if it 404s
 * (an opaque NodeId from a foreign namespace, a typo, etc.) we silently close
 * — the user's click had no observable effect, which is the desired UX.
 */
export const ViewNodeDialog: React.FC<ViewNodeDialogProps> = ({
   workspaceId,
   nodeId,
   onClose,
}) => {
   const { t } = useTranslation();

   const { data, isLoading, isError, error } = useQuery({
      queryKey: ['viewNodeDialog', workspaceId, nodeId],
      queryFn: async () => {
         const response = await api.get<Node>(
            `/opcua/v1/nodes/${slugifyNodeId(nodeId!)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         return response.data;
      },
      enabled: !!nodeId,
      retry: false,
      staleTime: 60 * 1000,
   });

   // Silent dismiss when the lookup 404s — the user clicked an unknown NodeId
   // (foreign namespace, opaque identifier, stale reference). Anything else
   // (network error, 5xx) we surface inside the dialog so it isn't lost.
   // The axios response interceptor in axios.api.ts forwards errors raw, so
   // the failure is an AxiosError; the HTTP status lives on error.response.
   const isNotFound = React.useMemo(() => {
      if (!isError || !error) return false;
      return axios.isAxiosError(error) && error.response?.status === 404;
   }, [isError, error]);

   React.useEffect(() => {
      if (isNotFound) onClose();
   }, [isNotFound, onClose]);

   if (!nodeId) return null;
   if (isNotFound) return null;

   const title = data
      ? getNodePlainName(data) || nodeId
      : t('viewNode.loadingTitle', 'Loading…');

   return (
      <ModelDialog
         open
         onClose={onClose}
         title={title}
         isLoading={isLoading}
         isError={isError && !isNotFound}
         error={error instanceof Error ? error : null}
         maxWidth="md"
      >
         {data && (
            <Box sx={{ p: 3 }}>
               <NodeAttributesPanel node={data} />
            </Box>
         )}
      </ModelDialog>
   );
};
