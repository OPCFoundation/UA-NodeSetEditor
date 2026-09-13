import * as React from 'react';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import CircularProgress from '@mui/material/CircularProgress';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { Node } from '../model/Node';
import { HierarchicalTreeItem } from './HierarchicalTreeItem';

interface FocusedTreeBranchProps {
   workspaceId: string;
   rootNodeId: string;
   expandedSet: Set<string>;
   selectedNodeId?: string | null;
   onSelect: (node: Node) => void;
   /** Fired once the server returns the root node, so the parent can
       pin the canonical nodeId into its expanded set — without this, a
       NodeId string mismatch between the caller and the server (e.g.
       ns=N;i=… vs nsu=…;i=…) would leave the root collapsed and the
       children fetch disabled. */
   onRootLoaded?: (node: Node) => void;
}

/**
 * Focused-view subtree: re-roots the tree at the selected node. Children
 * of the root are rendered via HierarchicalTreeItem and lazy-load on
 * expand (same as complete view), so the cost matches that of the regular
 * tree.
 */
export const FocusedTreeBranch: React.FC<FocusedTreeBranchProps> = ({
   workspaceId,
   rootNodeId,
   expandedSet,
   selectedNodeId,
   onSelect,
   onRootLoaded,
}) => {
   const { data: rootNode, isLoading: rootLoading } = useQuery<Node>({
      queryKey: ['focusedRoot', workspaceId, rootNodeId],
      queryFn: async () => {
         const response = await api.get<Node>(
            `/opcua/v1/nodes/${slugifyNodeId(rootNodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         return response.data;
      },
      staleTime: 60 * 1000,
   });

   React.useEffect(() => {
      if (rootNode) onRootLoaded?.(rootNode);
   }, [rootNode, onRootLoaded]);

   if (rootLoading || !rootNode) {
      return (
         <Box sx={{ p: 2 }}>
            <CircularProgress size={20} />
         </Box>
      );
   }

   return (
      <HierarchicalTreeItem
         node={rootNode}
         workspaceId={workspaceId}
         expandedSet={expandedSet}
         selectedNodeId={selectedNodeId}
         onSelect={onSelect}
         // In focused view we want to see the type's own components and
         // properties, not its subtype tree — subtypes live in the
         // complete view's type-category branches.
         includeSubtypes={false}
      />
   );
};

export default FocusedTreeBranch;
