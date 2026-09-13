import * as React from 'react';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import CircularProgress from '@mui/material/CircularProgress';
import { TreeItem } from '@mui/x-tree-view/TreeItem';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import { alpha } from '@mui/material/styles';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import { getNodePlainName } from '../model/Node';
import type { Node, TreeNode } from '../model/Node';
import { nodeClassToNum } from '../model/NodeFormatting';
import { extractNamespaceUri } from '../utils/formatNodeId';
import { getNodeClassIcon } from './nodeClassIcon';

export interface HierarchicalTreeItemProps {
   node: Node;
   workspaceId: string;
   expandedSet: Set<string>;
   selectedNodeId?: string | null;
   onSelect: (node: Node) => void;
   /**
    * Forward includeSubtypes=true to /children so the request also follows
    * subtypes of HierarchicalReferences (HasChild, Organizes, HasComponent,
    * HasProperty, HasSubtype, ...). Default true — without it the server
    * filters strictly to refs typed exactly as HierarchicalReferences and
    * the tree dead-ends after the root.
    */
   includeSubtypes?: boolean;
   /**
    * Namespace-filter mode: `node` is a fully-materialized TreeNode from a
    * pre-pruned subtree. Disables lazy child fetching (children come from
    * `node.children`), dims scaffolding outside `filterModelUri`, and
    * highlights in-namespace matches.
    */
   filterMode?: boolean;
   filterModelUri?: string;
}

// (HIGHLIGHT_BG is now resolved per-theme via sx — see below — so we can
// pick up OPC blue in light mode and amber in dark mode without a static
// rgba locking us into one hue.)

export const HierarchicalTreeItem: React.FC<HierarchicalTreeItemProps> = ({
   node,
   workspaceId,
   expandedSet,
   selectedNodeId,
   onSelect,
   includeSubtypes = true,
   filterMode,
   filterModelUri,
}) => {
   const expanded = expandedSet.has(node.nodeId);
   const isSelected = node.nodeId === selectedNodeId;

   const nodeNs = extractNamespaceUri(node.nodeId);
   const isInNamespace = !!filterModelUri && nodeNs === filterModelUri;
   const highlight = isSelected || isInNamespace;
   // Scaffolding (outside the filtered namespace) is dimmed so matches stand out.
   const isDimmed = !!filterMode && !isInNamespace && !isSelected;

   const { data: fetchedChildren, isLoading, isFetched } = useQuery<Node[]>({
      queryKey: ['nodeChildren', workspaceId, node.nodeId, includeSubtypes],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<Node>>(
            `/opcua/v1/nodes/${slugifyNodeId(node.nodeId)}/children`,
            {
               params: { includeSubtypes },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            },
         );
         return response.data.results ?? [];
      },
      enabled: expanded && !filterMode,
      staleTime: 1000 * 60 * 5,
   });

   // In filter mode the pruned children are already present on the TreeNode.
   const children = filterMode ? (node as TreeNode).children ?? [] : fetchedChildren;
   const isLeaf = filterMode
      ? (children?.length ?? 0) === 0
      : (node.hasNoChildren || (isFetched && (children?.length ?? 0) === 0));

   const icon = getNodeClassIcon(nodeClassToNum(node.nodeClass), {
      sx: {
         fontSize: 16,
         // Highlight uses primary.main — OPC blue in light mode, amber in
         // dark mode (per the dark-theme palette override). One key,
         // theme-aware.
         color: highlight ? 'primary.main' : isDimmed ? 'text.disabled' : 'text.secondary',
      },
   });

   const label = (
      <Box
         onClick={(e) => { e.stopPropagation(); onSelect(node); }}
         sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 0.5,
            cursor: 'pointer',
            backgroundColor: isSelected
               ? (theme) => alpha(theme.palette.primary.main, 0.16)
               : isInNamespace
                  ? (theme) => alpha(theme.palette.primary.main, 0.08)
                  : undefined,
            borderRadius: highlight ? '4px' : undefined,
            px: highlight ? 0.5 : 0,
            minWidth: 0,
         }}
      >
         {icon}
         <Typography
            variant="body2"
            sx={{
               fontWeight: highlight ? 900 : 'normal',
               color: highlight ? 'primary.main' : isDimmed ? 'text.disabled' : 'text.primary',
               whiteSpace: 'nowrap',
               overflow: 'hidden',
               textOverflow: 'ellipsis',
            }}
         >
            {getNodePlainName(node)}
         </Typography>
      </Box>
   );

   if (isLeaf) {
      return <TreeItem itemId={node.nodeId} label={label} />;
   }

   return (
      <TreeItem itemId={node.nodeId} label={label}>
         {!expanded && <TreeItem itemId={`${node.nodeId}__placeholder`} label="" />}
         {expanded && isLoading && (
            <TreeItem
               itemId={`${node.nodeId}__loading`}
               label={<CircularProgress size={14} sx={{ ml: 1 }} />}
            />
         )}
         {expanded && children?.map((c) => (
            <HierarchicalTreeItem
               key={c.nodeId}
               node={c}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               selectedNodeId={selectedNodeId}
               onSelect={onSelect}
               includeSubtypes={includeSubtypes}
               filterMode={filterMode}
               filterModelUri={filterModelUri}
            />
         ))}
      </TreeItem>
   );
};
