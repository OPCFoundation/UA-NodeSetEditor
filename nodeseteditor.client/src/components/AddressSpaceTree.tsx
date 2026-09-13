import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useLocation } from 'react-router-dom';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { SimpleTreeView } from '@mui/x-tree-view/SimpleTreeView';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import { WorkspaceContext } from '../WorkspaceContext';
import type { SelectedType } from '../WorkspaceContext';
import { HelpContext, HELP_SEARCH_URL } from '../HelpContext';
import { getNodePlainName } from '../model/Node';
import type { Node } from '../model/Node';
import { nodeClassToNum } from '../model/NodeFormatting';
import { extractNamespaceUri } from '../utils/formatNodeId';
import { SidebarToolbar } from './SidebarToolbar';
import { FocusedTreeBranch } from './FocusedTreeBranch';
import { AddressSpaceTreeView, ROOT_TYPES } from './AddressSpaceTreeView';

export const AddressSpaceTree: React.FC = () => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   const location = useLocation();
   const { selectedWorkspaceId, selectedModelUri, setSelectedModelUri, navigateToNode, setNavigateToNode, selectedType, setSelectedType } = React.useContext(WorkspaceContext);
   const { isHelpOpen, navigateHelp } = React.useContext(HelpContext);
   const [expandedItems, setExpandedItems] = React.useState<string[]>([]);
   // Focus mode + the anchor it's rooted at. The anchor is snapshotted when
   // the user enables focus and stays fixed while they drill down through
   // children — clicks update selectedType (right-panel detail) without
   // re-rooting the tree. Toggling focus off and back on re-snapshots from
   // the current selection.
   // superTypeIds is retained so toggling focus off can re-navigate the
   // complete tree to the anchor (path expansion needs the supertype chain).
   const [focused, setFocused] = React.useState<boolean>(false);
   const [focusRoot, setFocusRoot] = React.useState<(SelectedType & { superTypeIds: string[] }) | null>(null);

   // Switching workspaces invalidates any focus anchor (NodeIds belong to
   // the previous workspace's address space) and a stale focused tree
   // would just render an empty / errored branch. Drop back to the full
   // view on every workspace change.
   React.useEffect(() => {
      setFocused(false);
      setFocusRoot(null);
      setExpandedItems([]);
   }, [selectedWorkspaceId]);

   const handleNodeSelect = React.useCallback(async (node: Node) => {
      const displayName = getNodePlainName(node);
      const nodeClass = nodeClassToNum(node.nodeClass);
      setSelectedType({ nodeId: node.nodeId, displayName, nodeClass, documentation: node.documentation });
      if (location.pathname === '/model_library') {
         const params = new URLSearchParams({
            type: node.nodeId,
            name: displayName,
            nc: String(nodeClass),
         });
         if (selectedModelUri) params.set('ns', selectedModelUri);
         navigate(`/type_library?${params.toString()}`);
      }
      if (isHelpOpen) {
         if (node.parentNodeId) {
            // Child node: look up the root type/object and navigate help there.
            try {
               const response = await api.get<PaginatedResponse<Node>>(
                  `/opcua/v1/nodes/${slugifyNodeId(node.nodeId)}/path`,
                  { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } },
               );
               const rootNode = response.data.results?.[0];
               if (rootNode) {
                  navigateHelp(rootNode.documentation ?? HELP_SEARCH_URL(getNodePlainName(rootNode)));
               } else {
                  navigateHelp(node.documentation ?? HELP_SEARCH_URL(displayName));
               }
            } catch {
               navigateHelp(node.documentation ?? HELP_SEARCH_URL(displayName));
            }
         } else {
            navigateHelp(node.documentation ?? HELP_SEARCH_URL(displayName));
         }
      }
   }, [setSelectedType, location.pathname, selectedModelUri, navigate, isHelpOpen, navigateHelp, selectedWorkspaceId]);

   React.useEffect(() => {
      if (!navigateToNode) return;

      const path: string[] = [];
      const rootType = ROOT_TYPES.find(r => {
         switch (navigateToNode.nodeClass) {
            case 8: return r.category === 'object-types';
            case 16: return r.category === 'variable-types';
            case 64: return r.category === 'data-types';
            case 32: return r.category === 'reference-types';
            default: return false;
         }
      });
      if (rootType) {
         path.push(rootType.treeId);
         // When the folder wraps a selectable base-type node (ObjectTypes),
         // its tree id differs from the node id — expand both so the chain
         // from the folder down to the target stays open.
         if (rootType.treeId !== rootType.nodeId) path.push(rootType.nodeId);
      }
      path.push(...navigateToNode.superTypeIds);

      setExpandedItems(prev => {
         const set = new Set(prev);
         for (const id of path) set.add(id);
         return Array.from(set);
      });
   }, [navigateToNode]);

   React.useEffect(() => {
      if (navigateToNode) {
         const timer = setTimeout(() => setNavigateToNode(null), 5000);
         return () => clearTimeout(timer);
      }
   }, [navigateToNode, setNavigateToNode]);

   // Mirror navigateToNode → focusRoot so a single click in the list view
   // re-roots the focused tree (in focus mode) and primes the focus anchor
   // for the next toggle (in complete mode). If the navigation request also
   // asks to enter focused mode (e.g. just-created node), flip the toggle
   // and pre-expand the root row.
   React.useEffect(() => {
      if (!navigateToNode?.displayName) return;
      const root = {
         nodeId: navigateToNode.nodeId,
         displayName: navigateToNode.displayName,
         nodeClass: navigateToNode.nodeClass,
         superTypeIds: navigateToNode.superTypeIds ?? [],
      };
      setFocusRoot(root);
      if (navigateToNode.enterFocusMode) {
         setFocused(true);
         setExpandedItems((items) => {
            const set = new Set(items);
            set.add(root.nodeId);
            return Array.from(set);
         });
      }
   }, [navigateToNode]);

   // While in complete view, keep focusRoot in sync with the currently-
   // selected node — so toggling focus picks the latest interaction even
   // when the user got there via the detail view (double-click / edit action).
   // Suppressed while focused so drill-downs don't re-root the tree.
   React.useEffect(() => {
      if (focused) return;
      if (selectedType) {
         // Preserve a previously-captured supertype chain when the same
         // node is still in focus; otherwise we'd lose path expansion on
         // a later toggle-off because selectedType doesn't carry it.
         setFocusRoot((prev) => ({
            ...selectedType,
            superTypeIds: prev?.nodeId === selectedType.nodeId ? prev.superTypeIds : [],
         }));
      }
   }, [selectedType, focused]);

   const expandedSet = React.useMemo(() => new Set(expandedItems), [expandedItems]);
   const targetNodeId = navigateToNode?.nodeId ?? null;

   const handleExpandedItemsChange = (_event: React.SyntheticEvent | null, itemIds: string[]) => {
      setExpandedItems(itemIds);
   };

   const handleToggleFocused = React.useCallback(() => {
      setFocused((prev) => {
         const next = !prev;
         // Focus toggle only changes the mode. Syncing the tree to the
         // currently displayed node is the separate Sync button's job —
         // keep these concerns apart so the user controls each
         // independently. Pre-expand the focus root's row on entry so
         // the focused tree shows content immediately.
         if (next && focusRoot) {
            setExpandedItems((items) => {
               const set = new Set(items);
               set.add(focusRoot.nodeId);
               return Array.from(set);
            });
         }
         return next;
      });
   }, [focusRoot]);

   // Sync button: align the tree with the currently displayed node. Detail-
   // view link clicks (NodeIdLink → setSelectedType) deliberately leave the
   // tree alone, and the mirror effect above is suppressed in focused mode —
   // so this is the user's explicit "catch the tree up to where I'm looking"
   // action.
   //
   // The displayed node may sit several instance-declaration levels below its
   // owning type (AmberType → Pink → EngineeringUnits). We ask the server for
   // the structural path from that top-level type/object/variable down to the
   // node, then re-root the focused tree at the top of the path and expand
   // every level so the displayed node ends up visible and selected. We also
   // update the complete-view navigation target so toggling modes after a
   // sync keeps the synced context in both trees.
   const handleSyncTree = React.useCallback(async () => {
      if (!selectedType) return;

      // Fetch the path top-level-ancestor → … → displayed node. On failure,
      // fall back to treating the displayed node as its own root so the
      // button still does something useful.
      let path: Node[] = [];
      try {
         const response = await api.get<PaginatedResponse<Node>>(
            `/opcua/v1/nodes/${slugifyNodeId(selectedType.nodeId)}/path`,
            { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } },
         );
         path = response.data.results ?? [];
      } catch {
         path = [];
      }

      const rootNode = path[0];
      const newRoot = rootNode
         ? {
            nodeId: rootNode.nodeId,
            displayName: getNodePlainName(rootNode),
            nodeClass: nodeClassToNum(rootNode.nodeClass),
            superTypeIds: rootNode.superTypeIds ?? [],
         }
         : {
            nodeId: selectedType.nodeId,
            displayName: selectedType.displayName,
            nodeClass: selectedType.nodeClass,
            superTypeIds: focusRoot?.nodeId === selectedType.nodeId ? focusRoot.superTypeIds : [],
         };

      setFocusRoot(newRoot);
      // The complete view prunes the tree to the active model (filterModelUri =
      // selectedModelUri). If the root lives in a different namespace it would
      // be filtered out, so switch the active model to the root's namespace to
      // guarantee it's visible there once the tree navigates to it.
      setSelectedModelUri(extractNamespaceUri(newRoot.nodeId));
      if (focused) {
         // Expand every node along the path so the focused tree opens all the
         // way down to the displayed node (which stays the selected node).
         setExpandedItems((items) => {
            const set = new Set(items);
            set.add(newRoot.nodeId);
            for (const n of path) set.add(n.nodeId);
            return Array.from(set);
         });
      }
      setNavigateToNode({
         nodeId: newRoot.nodeId,
         displayName: newRoot.displayName,
         nodeClass: newRoot.nodeClass,
         superTypeIds: newRoot.superTypeIds,
      });
   }, [selectedType, focused, focusRoot, selectedWorkspaceId, setNavigateToNode, setSelectedModelUri]);

   if (!selectedWorkspaceId) {
      return (
         <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
            <SidebarToolbar
               focused={focused}
               onToggleFocused={handleToggleFocused}
               focusDisabled
               onSyncTree={handleSyncTree}
               syncDisabled
            />
            <Box sx={{ p: 2 }}>
               <Typography variant="body2" color="text.secondary">
                  {t('addressSpaceTree.selectWorkspace')}
               </Typography>
            </Box>
         </Box>
      );
   }

   return (
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
         <SidebarToolbar
            focused={focused}
            onToggleFocused={handleToggleFocused}
            focusDisabled={!focusRoot}
            onSyncTree={handleSyncTree}
            syncDisabled={!selectedType}
         />
         {focused && focusRoot && (
            <Typography variant="caption" sx={{ px: 2, pt: 1, color: 'text.secondary' }}>
               {t('addressSpaceTree.focusedOn', 'Focused on: {{name}}', { name: focusRoot.displayName })}
            </Typography>
         )}
         <Box sx={{ p: 1, flex: 1, overflowY: 'auto', minHeight: 0 }}>
            {focused && focusRoot ? (
               <SimpleTreeView
                  expandedItems={expandedItems}
                  onExpandedItemsChange={handleExpandedItemsChange}
               >
                  <FocusedTreeBranch
                     workspaceId={selectedWorkspaceId}
                     rootNodeId={focusRoot.nodeId}
                     expandedSet={expandedSet}
                     selectedNodeId={selectedType?.nodeId ?? null}
                     onSelect={handleNodeSelect}
                     onRootLoaded={(node) => {
                        // Pin the server's canonical nodeId into the expanded
                        // set so the root's children query fires even when the
                        // nodeId we held differed in serialization form.
                        setExpandedItems((items) => {
                           if (items.includes(node.nodeId)) return items;
                           return [...items, node.nodeId];
                        });
                     }}
                  />
               </SimpleTreeView>
            ) : (
               <AddressSpaceTreeView
                  workspaceId={selectedWorkspaceId}
                  filterModelUri={selectedModelUri || undefined}
                  highlightNodeId={targetNodeId}
                  onNodeSelect={handleNodeSelect}
                  expandedItems={expandedItems}
                  onExpandedItemsChange={handleExpandedItemsChange}
               />
            )}
         </Box>
      </Box>
   );
};

export default AddressSpaceTree;
