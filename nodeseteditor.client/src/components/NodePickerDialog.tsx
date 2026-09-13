import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Stack from '@mui/material/Stack';
import Divider from '@mui/material/Divider';
import Typography from '@mui/material/Typography';
import Breadcrumbs from '@mui/material/Breadcrumbs';
import Link from '@mui/material/Link';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import { getNodePlainName } from '../model/Node';
import type { Node } from '../model/Node';

import { ModelDialog } from './ModelDialog';
import type { ModelDialogAction } from './ModelDialog';
import { ModelSelect } from './ModelSelect';
import { AddressSpaceTreeView } from './AddressSpaceTreeView';
import { NodeAttributesPanel } from './NodeAttributesPanel';

interface NodePickerDialogProps {
   open: boolean;
   onClose: () => void;
   /** Called with the chosen NodeId when the user confirms the selection. */
   onPick: (nodeId: string) => void;
   workspaceId: string;
   /** Defaults to a translated "Select a Node". */
   title?: string;
   /**
    * When set, the tree shows a single type hierarchy rooted at this type
    * (following HasSubtype) instead of the full address space, and the
    * model-filter dropdown is hidden — e.g. picking an InterfaceType.
    */
   rootType?: { category: string; nodeId: string };
   /** Confirm-button label. Defaults to a translated "Select". */
   confirmLabel?: string;
   /**
    * When the caller runs an async action in onPick (rather than just closing),
    * it can keep the dialog open and surface progress/errors via these.
    */
   isBusy?: boolean;
   pickError?: string | null;
}

/**
 * Stacked modal that lets the user pick a NodeId. Layout: the same
 * address-space tree as the left pane (Objects + type categories, with a
 * model-filter dropdown) on the left, node detail (parent-chain breadcrumbs +
 * attributes) on the right. Unlike the left pane, each type also lists the
 * instance declarations it owns, so any node in the address space is reachable. Confirms via the dialog action; on confirm we
 * fire onPick(nodeId) and the caller is responsible for closing.
 */
export const NodePickerDialog: React.FC<NodePickerDialogProps> = ({
   open,
   onClose,
   onPick,
   workspaceId,
   title,
   rootType,
   confirmLabel,
   isBusy = false,
   pickError = null,
}) => {
   const { t } = useTranslation();
   const [selectedNodeId, setSelectedNodeId] = React.useState<string | null>(null);
   // Local model-filter + expansion state, kept independent of the sidebar's
   // global selection so opening the picker doesn't disturb the left pane.
   const [modelUri, setModelUri] = React.useState<string>('');
   const [expandedItems, setExpandedItems] = React.useState<string[]>([]);

   // Reset state each time the dialog opens, so reopening doesn't surface a
   // stale pick / filter / expansion from a previous session.
   React.useEffect(() => {
      if (open) {
         setSelectedNodeId(null);
         setModelUri('');
         // In rootType mode, expand the root type so its subtypes show at once.
         setExpandedItems(rootType ? [rootType.nodeId] : []);
      }
   }, [open, rootType]);

   // Full attributes for the currently-selected node. The tree's child rows
   // already carry most fields, but going through the node-detail endpoint
   // gives us the canonical shape (and is cached, so re-selecting is cheap).
   const { data: nodeDetail } = useQuery<Node>({
      queryKey: ['nodePickerDetail', workspaceId, selectedNodeId],
      queryFn: async () => {
         const response = await api.get<Node>(
            `/opcua/v1/nodes/${slugifyNodeId(selectedNodeId!)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         return response.data;
      },
      enabled: open && !!selectedNodeId,
      staleTime: 60 * 1000,
   });

   const chain = useParentChain(workspaceId, nodeDetail ?? null);

   const actions: ModelDialogAction[] = [
      {
         label: confirmLabel ?? t('common.select', 'Select'),
         onClick: () => {
            if (selectedNodeId) onPick(selectedNodeId);
         },
         disabled: !selectedNodeId || isBusy,
         variant: 'contained',
      },
   ];

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={title ?? t('nodePicker.title', 'Select a Node')}
         maxWidth="lg"
         actions={actions}
         isLoading={isBusy}
         isError={!!pickError}
         error={pickError ? new Error(pickError) : null}
      >
         <Box sx={{ display: 'flex', height: '70vh', minHeight: 480 }}>
            <Box sx={{
               width: '40%',
               minWidth: 240,
               display: 'flex',
               flexDirection: 'column',
               borderRight: 1,
               borderColor: 'divider',
            }}>
               {!rootType && (
                  <Box sx={{
                     display: 'flex',
                     p: 0.5,
                     borderBottom: 1,
                     borderColor: 'divider',
                  }}>
                     <ModelSelect
                        workspaceId={workspaceId}
                        value={modelUri}
                        onChange={setModelUri}
                     />
                  </Box>
               )}
               <Box sx={{ flex: 1, overflow: 'auto', p: 1, minHeight: 0 }}>
                  {open && (
                     <AddressSpaceTreeView
                        workspaceId={workspaceId}
                        rootType={rootType}
                        filterModelUri={rootType ? undefined : (modelUri || undefined)}
                        highlightNodeId={selectedNodeId}
                        onNodeSelect={(n) => setSelectedNodeId(n.nodeId)}
                        expandedItems={expandedItems}
                        onExpandedItemsChange={(_e, ids) => setExpandedItems(ids)}
                        selectableObjectsFolder
                        // Types own instance declarations, and those are legal
                        // reference targets — so the tree has to reach them.
                        // Not in rootType mode: there the caller wants one
                        // specific type (e.g. an InterfaceType), and a child of
                        // one would be an invalid pick.
                        showTypeChildren={!rootType}
                     />
                  )}
               </Box>
            </Box>
            <Box sx={{ flex: 1, minWidth: 0, overflow: 'auto', p: 2 }}>
               {!selectedNodeId ? (
                  <Typography variant="body2" color="text.secondary">
                     {t(
                        'nodePicker.empty',
                        'Select a node from the tree to see its details.',
                     )}
                  </Typography>
               ) : (
                  <Stack spacing={2}>
                     <ParentChainBreadcrumbs
                        chain={chain}
                        onPick={setSelectedNodeId}
                     />
                     <Divider />
                     {nodeDetail && <NodeAttributesPanel node={nodeDetail} />}
                  </Stack>
               )}
            </Box>
         </Box>
      </ModelDialog>
   );
};

interface ChainEntry {
   nodeId: string;
   label: string;
}

/**
 * Walks parentNodeId from the leaf up, returning a top-down chain
 * (root-most ancestor first, leaf last). Each hop is a /nodes/{id} request,
 * and each request is cached by react-query so revisits are free. Bounded
 * by a visited-set to break cycles in malformed data.
 */
function useParentChain(workspaceId: string, leaf: Node | null): ChainEntry[] {
   const [chain, setChain] = React.useState<ChainEntry[]>([]);

   React.useEffect(() => {
      let cancelled = false;
      if (!leaf) {
         setChain([]);
         return;
      }

      (async () => {
         const out: ChainEntry[] = [
            { nodeId: leaf.nodeId, label: getNodePlainName(leaf) },
         ];
         const visited = new Set<string>([leaf.nodeId]);
         let parentId = leaf.parentNodeId;
         while (parentId && !visited.has(parentId)) {
            visited.add(parentId);
            try {
               const response = await api.get<Node>(
                  `/opcua/v1/nodes/${slugifyNodeId(parentId)}`,
                  { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
               );
               if (cancelled) return;
               out.unshift({
                  nodeId: response.data.nodeId,
                  label: getNodePlainName(response.data),
               });
               parentId = response.data.parentNodeId;
            } catch {
               break;
            }
         }
         if (!cancelled) setChain(out);
      })();

      return () => { cancelled = true; };
   }, [workspaceId, leaf]);

   return chain;
}

const ParentChainBreadcrumbs: React.FC<{
   chain: ChainEntry[];
   onPick: (nodeId: string) => void;
}> = ({ chain, onPick }) => {
   if (chain.length === 0) return null;
   return (
      <Breadcrumbs separator=">" sx={{ flexWrap: 'wrap' }}>
         {chain.map((entry, idx) => {
            const isLast = idx === chain.length - 1;
            if (isLast) {
               return (
                  <Typography
                     key={entry.nodeId}
                     variant="body2"
                     sx={{ fontWeight: 'bold' }}
                  >
                     {entry.label}
                  </Typography>
               );
            }
            return (
               <Link
                  key={entry.nodeId}
                  component="button"
                  variant="body2"
                  underline="hover"
                  onClick={() => onPick(entry.nodeId)}
               >
                  {entry.label}
               </Link>
            );
         })}
      </Breadcrumbs>
   );
};
