import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import Typography from '@mui/material/Typography';
import CircularProgress from '@mui/material/CircularProgress';
import { alpha } from '@mui/material/styles';
import { SimpleTreeView } from '@mui/x-tree-view/SimpleTreeView';
import { TreeItem } from '@mui/x-tree-view/TreeItem';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import { buildTree, getNodePlainName } from '../model/Node';
import type { Node, TreeNode } from '../model/Node';
import { extractNamespaceUri } from '../utils/formatNodeId';
import { HierarchicalTreeItem } from './HierarchicalTreeItem';

// Well-known Objects folder (Part 6 §A) — root of the instance hierarchy.
const OBJECTS_FOLDER_NODE_ID = 'i=85';

// Root type NodeIds.
//
// `treeId` is the TreeItem id used for the category's folder row. It equals
// `nodeId` for every category except ObjectTypes: there we surface
// BaseObjectType (i=58) as a real, selectable node *inside* the folder
// (`showBaseTypeNode`), so the folder needs its own distinct id to avoid
// colliding with that node's TreeItem. We expose BaseObjectType because
// creating a subtype directly off it is valid and common; the other root
// base types (BaseVariableType/BaseDataType/References) can't be subtyped
// directly, so their folders list concrete subtypes instead.
export const ROOT_TYPES = [
   { category: 'object-types', nodeId: 'i=58', treeId: 'object-types', label: 'ObjectTypes', showBaseTypeNode: true },
   { category: 'variable-types', nodeId: 'i=62', treeId: 'i=62', label: 'VariableTypes', showBaseTypeNode: false },
   { category: 'data-types', nodeId: 'i=24', treeId: 'i=24', label: 'DataTypes', showBaseTypeNode: false },
   { category: 'reference-types', nodeId: 'i=31', treeId: 'i=31', label: 'ReferenceTypes', showBaseTypeNode: false },
] as const;

async function fetchSubtypes(workspaceId: string, category: string, nodeId: string, includeSelf = false, modelUri?: string): Promise<Node[]> {
   const params: Record<string, unknown> = { depth: 3, count: 10000, includeSelf };
   // When set, the server returns the full HasSubtype hierarchy pruned to
   // branches containing a type in this namespace (depth/includeSelf ignored).
   if (modelUri) params.modelUri = modelUri;
   const response = await api.get<PaginatedResponse<Node>>(
      `/opcua/v1/types/${category}/${slugifyNodeId(nodeId)}/subtypes`,
      {
         params,
         headers: { 'OpcUa-Server': idToUrn(workspaceId) }
      }
   );
   return response.data.results ?? [];
}

const CORE_NS = 'http://opcfoundation.org/UA/';

interface TypeTreeItemProps {
   node: TreeNode;
   category: string;
   workspaceId: string;
   expandedSet: Set<string>;
   targetNodeId: string | null;
   highlightModelUri: string;
   onNodeSelect: (node: Node) => void;
   /**
    * Namespace-filter mode: the tree is a pre-pruned, fully-materialized subset
    * (see AddressSpaceTreeView). Disables lazy child fetching — `node.children` is
    * the complete child set — and dims scaffolding nodes outside the filtered
    * namespace.
    */
   filterMode?: boolean;
   /** Also show each type's instance declarations beneath it — see AddressSpaceTreeView. */
   showTypeChildren?: boolean;
   /** Namespace URI the tree is pruned to, forwarded to the instance-declaration rows. */
   filterModelUri?: string;
}

const TypeTreeItem: React.FC<TypeTreeItemProps> = ({ node, category, workspaceId, expandedSet, targetNodeId, highlightModelUri, onNodeSelect, filterMode, showTypeChildren, filterModelUri }) => {
   const targetRef = React.useRef<HTMLDivElement>(null);
   const isTarget = node.nodeId === targetNodeId;
   const nodeNs = extractNamespaceUri(node.nodeId);
   const isHighlighted = !!highlightModelUri && highlightModelUri !== CORE_NS && nodeNs === highlightModelUri;
   // Filter mode: ancestor scaffolding (nodes outside the filtered namespace)
   // is shown dimmed so the in-namespace matches stand out.
   const isDimmed = !!filterMode && !isHighlighted && !isTarget;

   // Boundary node: the response withheld some of this node's subtypes, so they have to be
   // browsed separately when it expands. The server says so outright rather than the client
   // inferring it from "no children in the payload" — which was wrong for a type that has
   // instance declarations AND subtypes: the row was expandable for its children while its
   // subtypes had been pruned away, with nothing to signal they existed.
   //
   // This applies in filter mode too. Pruning to the active namespace is what keeps the dialog
   // fast to open, but the pruned-away types are still legal targets, so expanding has to be
   // able to reach them.
   const isBoundary = node.subtypesTruncated === true;
   const expanded = expandedSet.has(node.nodeId);

   // Fetch subtypes on demand for boundary nodes
   const { data: boundaryTree, isLoading, isFetched } = useQuery({
      queryKey: ['subtypes', workspaceId, category, node.nodeId],
      queryFn: async () => {
         const nodes = await fetchSubtypes(workspaceId, category, node.nodeId);
         return buildTree(nodes, node.nodeId);
      },
      enabled: isBoundary && expanded,
      staleTime: 1000 * 60 * 5,
   });

   // A truncated node prefers the re-browsed list once it arrives: it is the complete set, where
   // node.children holds only whatever survived the prune. Until then the partial list is shown
   // so expanding isn't visibly empty.
   const children = isBoundary ? (boundaryTree ?? node.children) : node.children;
   // Not truncated means the payload already holds every subtype this node has, so an empty
   // children list is definitive. Truncated means we only know once the re-browse lands.
   const isSubtypeLeaf = isBoundary
      ? (isFetched && children.length === 0)
      : node.children.length === 0;

   // A type also owns instance declarations (its Properties/Components/Methods).
   // The subtypes endpoint follows HasSubtype only, so they come from /children
   // — with includeSubtypes=false, which is exactly "hierarchical children minus
   // HasSubtype" and therefore can't duplicate the subtype rows above.
   const canHaveTypeChildren = !!showTypeChildren && node.hasNoChildren !== true;
   const {
      data: typeChildren,
      isLoading: isLoadingTypeChildren,
      isFetched: isTypeChildrenFetched,
   } = useQuery<Node[]>({
      queryKey: ['nodeChildren', workspaceId, node.nodeId, false],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<Node>>(
            `/opcua/v1/nodes/${slugifyNodeId(node.nodeId)}/children`,
            {
               params: { includeSubtypes: false },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            },
         );
         return response.data.results ?? [];
      },
      enabled: canHaveTypeChildren && expanded,
      staleTime: 1000 * 60 * 5,
   });

   const isChildLeaf = !canHaveTypeChildren
      || (isTypeChildrenFetched && (typeChildren?.length ?? 0) === 0);
   const isLeaf = isSubtypeLeaf && isChildLeaf;


   React.useEffect(() => {
      if (isTarget && targetRef.current) {
         const timer = setTimeout(() => {
            targetRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' });
         }, 150);
         return () => clearTimeout(timer);
      }
   }, [isTarget]);

   const handleLabelClick = (e: React.MouseEvent) => {
      e.stopPropagation();
      onNodeSelect(node);
   };

   const label = (
      <Typography
         ref={targetRef}
         variant="body2"
         onClick={handleLabelClick}
         sx={{
            fontWeight: isTarget || isHighlighted ? 900 : 'normal',
            // isTarget = the just-navigated-to row → primary highlight
            //   (OPC blue in light mode, amber in dark mode via the dark
            //   palette override).
            // isHighlighted = belongs to active model → same primary tint
            //   but at lower alpha so the selection still reads as the
            //   stronger of the two states.
            color: (isTarget || isHighlighted) ? 'primary.main' : isDimmed ? 'text.disabled' : 'text.primary',
            backgroundColor: isTarget
               ? (theme) => alpha(theme.palette.primary.main, 0.16)
               : isHighlighted
                  ? (theme) => alpha(theme.palette.primary.main, 0.08)
                  : undefined,
            borderRadius: isTarget || isHighlighted ? '4px' : undefined,
            px: isTarget || isHighlighted ? 0.5 : 0,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
            cursor: 'pointer',
         }}
      >
         {getNodePlainName(node)}
      </Typography>
   );

   if (isLeaf) {
      return <TreeItem itemId={node.nodeId} label={label} />;
   }

   return (
      <TreeItem itemId={node.nodeId} label={label}>
         {/* Nothing is rendered while collapsed — the placeholder keeps the expand arrow.
             The subtype rows used to render even when collapsed, which meant the instance
             declarations (which arrive later, from their own request) were inserted BEFORE
             items already mounted. The tree view registers JSX items in mount order and that
             insert-ahead lost the subtype rows: a type with both kinds of child showed only
             its declarations. Mounting everything in one commit, only while expanded, keeps
             registration append-only. */}
         {!expanded && children.length === 0 && (
            <TreeItem itemId={`${node.nodeId}__placeholder`} label="" />
         )}
         {expanded && (isLoading || isLoadingTypeChildren) && (
            <TreeItem
               itemId={`${node.nodeId}__loading`}
               label={<CircularProgress size={14} sx={{ ml: 1 }} />}
            />
         )}
         {/* Subtypes first, then the type's own instance declarations. The subtype rows continue
             the hierarchy the row above belongs to, so keeping them adjacent to their parent
             makes the nesting readable; a long run of declarations in between is what made
             subtypes look like siblings of the type rather than its children. */}
         {children.map((child) => (
            <TypeTreeItem
               key={child.nodeId}
               node={child}
               category={category}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               targetNodeId={targetNodeId}
               highlightModelUri={highlightModelUri}
               onNodeSelect={onNodeSelect}
               filterMode={filterMode}
               showTypeChildren={showTypeChildren}
               filterModelUri={filterModelUri}
            />
         ))}
         {expanded && typeChildren?.map((child) => (
            <HierarchicalTreeItem
               key={child.nodeId}
               node={child}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               selectedNodeId={targetNodeId}
               onSelect={onNodeSelect}
               includeSubtypes={false}
               filterModelUri={filterModelUri}
            />
         ))}
      </TreeItem>
   );
};

interface CategoryItemProps {
   category: string;
   rootNodeId: string;
   folderId: string;
   showBaseTypeNode: boolean;
   categoryLabel: string;
   workspaceId: string;
   expandedSet: Set<string>;
   targetNodeId: string | null;
   highlightModelUri: string;
   onNodeSelect: (node: Node) => void;
   /** When set, prune the category to branches containing a type in this namespace. */
   filterModelUri?: string;
   /** Also show each type's instance declarations beneath it. */
   showTypeChildren?: boolean;
}

const CategoryItem: React.FC<CategoryItemProps> = ({ category, rootNodeId, folderId, showBaseTypeNode, categoryLabel, workspaceId, expandedSet, targetNodeId, highlightModelUri, onNodeSelect, filterModelUri, showTypeChildren }) => {
   const expanded = expandedSet.has(folderId);
   const filterMode = !!filterModelUri;

   const { data: tree, isLoading } = useQuery({
      queryKey: ['subtypes', workspaceId, category, rootNodeId, showBaseTypeNode, filterModelUri ?? null],
      queryFn: async () => {
         const nodes = await fetchSubtypes(workspaceId, category, rootNodeId, showBaseTypeNode, filterModelUri);
         // The root base type is filtered out before buildTree in both branches:
         // in filter mode the server emits it as scaffolding, and buildTree would
         // otherwise self-reference it (parentId === rootId).
         const childNodes = nodes.filter((n) => n.nodeId !== rootNodeId);
         if (showBaseTypeNode) {
            // Surface the root base type itself as the single top-level node,
            // with its subtypes nested beneath, so it can be selected.
            const baseNode = nodes.find((n) => n.nodeId === rootNodeId) ?? nodes[0];
            const childTree = buildTree(childNodes, rootNodeId);
            return baseNode ? [{ ...baseNode, children: childTree } as TreeNode] : childTree;
         }
         return buildTree(childNodes, rootNodeId);
      },
      // In filter mode load eagerly (even while collapsed) so empty categories
      // can be hidden; otherwise load lazily on expand.
      enabled: filterMode || expanded,
      staleTime: 1000 * 60 * 5,
   });

   // Hide categories with no matches in the selected namespace.
   if (filterMode && (!tree || tree.length === 0)) return null;

   return (
      <TreeItem itemId={folderId} label={<Typography variant="body2" sx={{ fontWeight: 'bold' }}>{categoryLabel}</Typography>}>
         {!expanded && <TreeItem itemId={`${folderId}__placeholder`} label="" />}
         {expanded && isLoading && (
            <TreeItem
               itemId={`${folderId}__loading`}
               label={<CircularProgress size={14} sx={{ ml: 1 }} />}
            />
         )}
         {expanded && tree?.map((child) => (
            <TypeTreeItem
               key={child.nodeId}
               node={child}
               category={category}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               targetNodeId={targetNodeId}
               highlightModelUri={highlightModelUri}
               onNodeSelect={onNodeSelect}
               filterMode={filterMode}
               showTypeChildren={showTypeChildren}
               filterModelUri={filterModelUri}
            />
         ))}
      </TreeItem>
   );
};

interface RootTypeBranchProps {
   category: string;
   rootNodeId: string;
   workspaceId: string;
   expandedSet: Set<string>;
   selectedNodeId: string | null;
   onNodeSelect: (node: Node) => void;
   /** Also show each type's instance declarations beneath it. */
   showTypeChildren?: boolean;
}

/**
 * Single type hierarchy rooted at `rootNodeId`, following HasSubtype. Unlike
 * CategoryItem there is no folder wrapper: the root type itself is the top
 * row (selectable), with its subtypes nested beneath. Deeper subtypes load
 * lazily as boundary nodes via TypeTreeItem. Used by the NodePickerDialog
 * rootType mode (e.g. picking an InterfaceType).
 */
const RootTypeBranch: React.FC<RootTypeBranchProps> = ({ category, rootNodeId, workspaceId, expandedSet, selectedNodeId, onNodeSelect, showTypeChildren }) => {
   const { data: tree, isLoading } = useQuery({
      // Same cache key shape as CategoryItem's showBaseTypeNode branch so the
      // two share the root fetch.
      queryKey: ['subtypes', workspaceId, category, rootNodeId, true, null],
      queryFn: async () => {
         const nodes = await fetchSubtypes(workspaceId, category, rootNodeId, true);
         const childNodes = nodes.filter((n) => n.nodeId !== rootNodeId);
         const baseNode = nodes.find((n) => n.nodeId === rootNodeId) ?? nodes[0];
         const childTree = buildTree(childNodes, rootNodeId);
         return baseNode ? [{ ...baseNode, children: childTree } as TreeNode] : childTree;
      },
      staleTime: 1000 * 60 * 5,
   });

   if (isLoading) {
      return <TreeItem itemId={`${rootNodeId}__loading`} label={<CircularProgress size={14} sx={{ ml: 1 }} />} />;
   }

   return (
      <>
         {tree?.map((child) => (
            <TypeTreeItem
               key={child.nodeId}
               node={child}
               category={category}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               targetNodeId={selectedNodeId}
               highlightModelUri=""
               onNodeSelect={onNodeSelect}
               showTypeChildren={showTypeChildren}
            />
         ))}
      </>
   );
};

interface ObjectsCategoryProps {
   workspaceId: string;
   expandedSet: Set<string>;
   selectedNodeId: string | null;
   onNodeSelect: (node: Node) => void;
   categoryLabel: string;
   /** When set, prune the instance tree to branches containing a node in this namespace. */
   filterModelUri?: string;
   /** When true, the Objects folder row itself is selectable (clicking its label picks i=85). */
   selectableFolder?: boolean;
}

/**
 * Top-level "Objects" branch rooted at i=85. Mirrors the CategoryItem
 * shape but follows hierarchical references (HasComponent/HasProperty/
 * Organizes/...) instead of HasSubtype, so the user can drill through
 * the instance tree alongside the type categories.
 */
const ObjectsCategory: React.FC<ObjectsCategoryProps> = ({
   workspaceId,
   expandedSet,
   selectedNodeId,
   onNodeSelect,
   categoryLabel,
   filterModelUri,
   selectableFolder = false,
}) => {
   const expanded = expandedSet.has(OBJECTS_FOLDER_NODE_ID);
   const filterMode = !!filterModelUri;
   const isSelected = selectedNodeId === OBJECTS_FOLDER_NODE_ID;

   // The Objects folder is a real node (i=85); when selection is allowed, clicking its
   // label picks it (stopPropagation so the row still expands via the toggle icon).
   const folderLabel = (
      <Typography
         variant="body2"
         onClick={selectableFolder ? (e) => {
            e.stopPropagation();
            onNodeSelect({
               nodeId: OBJECTS_FOLDER_NODE_ID,
               nodeClass: 'Object',
               browseName: 'Objects',
               displayName: { text: categoryLabel },
            } as Node);
         } : undefined}
         sx={{
            fontWeight: 'bold',
            cursor: selectableFolder ? 'pointer' : undefined,
            color: isSelected ? 'primary.main' : undefined,
            backgroundColor: isSelected
               ? (theme) => alpha(theme.palette.primary.main, 0.16) : undefined,
            borderRadius: isSelected ? '4px' : undefined,
            px: isSelected ? 0.5 : 0,
         }}
      >
         {categoryLabel}
      </Typography>
   );

   // Non-filter: lazy depth-1 children (Node[]); each row fetches its own
   // children. Filter: the server returns the whole pruned instance subtree as
   // a flat list, built here into a fully-materialized tree (TreeNode[]).
   const { data: children, isLoading } = useQuery<Node[]>({
      queryKey: ['nodeChildren', workspaceId, OBJECTS_FOLDER_NODE_ID, true, filterModelUri ?? null],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<Node>>(
            `/opcua/v1/nodes/${slugifyNodeId(OBJECTS_FOLDER_NODE_ID)}/children`,
            {
               params: filterMode ? { modelUri: filterModelUri } : { includeSubtypes: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            },
         );
         const results = response.data.results ?? [];
         return filterMode ? buildTree(results, OBJECTS_FOLDER_NODE_ID) : results;
      },
      enabled: filterMode || expanded,
      staleTime: 1000 * 60 * 5,
   });

   // Hide the Objects branch when nothing in the namespace lives under it.
   if (filterMode && (!children || children.length === 0)) return null;

   return (
      <TreeItem
         itemId={OBJECTS_FOLDER_NODE_ID}
         label={folderLabel}
      >
         {!expanded && <TreeItem itemId={`${OBJECTS_FOLDER_NODE_ID}__placeholder`} label="" />}
         {expanded && isLoading && (
            <TreeItem
               itemId={`${OBJECTS_FOLDER_NODE_ID}__loading`}
               label={<CircularProgress size={14} sx={{ ml: 1 }} />}
            />
         )}
         {expanded && children?.map((child) => (
            <HierarchicalTreeItem
               key={child.nodeId}
               node={child}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               selectedNodeId={selectedNodeId}
               onSelect={onNodeSelect}
               filterMode={filterMode}
               filterModelUri={filterModelUri}
            />
         ))}
      </TreeItem>
   );
};

interface AddressSpaceTreeViewProps {
   workspaceId: string;
   /** When set, prune the tree to branches containing a node in this namespace
       and highlight the in-namespace matches. */
   filterModelUri?: string;
   /** Node to highlight (and scroll to) in the tree. */
   highlightNodeId?: string | null;
   onNodeSelect: (node: Node) => void;
   expandedItems: string[];
   onExpandedItemsChange: (event: React.SyntheticEvent | null, itemIds: string[]) => void;
   /**
    * When set, replace the full address space with a single type hierarchy
    * rooted at this type (following HasSubtype) — e.g. picking an
    * InterfaceType. `filterModelUri` is ignored in this mode.
    */
   rootType?: { category: string; nodeId: string };
   /**
    * When true, the top-level "Objects" folder (i=85) is itself selectable
    * (it's a real node). Used by the node picker so a reference can target it;
    * the sidebar leaves it as a non-selectable header.
    */
   selectableObjectsFolder?: boolean;
   /**
    * When true, each type row also lists the instance declarations it owns
    * (Properties/Components/Methods) below its subtypes, so the tree can reach
    * a child of a type — a legal reference target. The sidebar leaves this off
    * and offers focus mode for drilling into a single type instead.
    */
   showTypeChildren?: boolean;
}

/**
 * The complete address-space tree body: an "Objects" instance branch plus the
 * four type categories (ObjectTypes/VariableTypes/DataTypes/ReferenceTypes),
 * with optional namespace filtering. Shared between the left-pane
 * AddressSpaceTree and the NodePickerDialog so both render an identical tree;
 * the surrounding chrome (toolbar, focus mode, selection wiring) lives with
 * each caller.
 */
export const AddressSpaceTreeView: React.FC<AddressSpaceTreeViewProps> = ({
   workspaceId,
   filterModelUri,
   highlightNodeId = null,
   onNodeSelect,
   expandedItems,
   onExpandedItemsChange,
   rootType,
   selectableObjectsFolder = false,
   showTypeChildren = false,
}) => {
   const { t } = useTranslation();
   const expandedSet = React.useMemo(() => new Set(expandedItems), [expandedItems]);
   // The active model both prunes (filterModelUri) and highlights
   // (highlightModelUri) — they are always the same value.
   const highlightModelUri = filterModelUri ?? '';

   if (rootType) {
      return (
         <SimpleTreeView
            expandedItems={expandedItems}
            onExpandedItemsChange={onExpandedItemsChange}
         >
            <RootTypeBranch
               category={rootType.category}
               rootNodeId={rootType.nodeId}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               selectedNodeId={highlightNodeId}
               onNodeSelect={onNodeSelect}
               showTypeChildren={showTypeChildren}
            />
         </SimpleTreeView>
      );
   }

   return (
      <SimpleTreeView
         expandedItems={expandedItems}
         onExpandedItemsChange={onExpandedItemsChange}
      >
         <ObjectsCategory
            workspaceId={workspaceId}
            expandedSet={expandedSet}
            selectedNodeId={highlightNodeId}
            onNodeSelect={onNodeSelect}
            categoryLabel={t('addressSpaceTree.objects', 'Objects')}
            filterModelUri={filterModelUri}
            selectableFolder={selectableObjectsFolder}
         />
         {ROOT_TYPES.map(({ category, nodeId, treeId, label, showBaseTypeNode }) => (
            <CategoryItem
               key={nodeId}
               category={category}
               rootNodeId={nodeId}
               folderId={treeId}
               showBaseTypeNode={showBaseTypeNode}
               categoryLabel={label}
               workspaceId={workspaceId}
               expandedSet={expandedSet}
               targetNodeId={highlightNodeId}
               highlightModelUri={highlightModelUri}
               onNodeSelect={onNodeSelect}
               filterModelUri={filterModelUri}
               showTypeChildren={showTypeChildren}
            />
         ))}
      </SimpleTreeView>
   );
};

export default AddressSpaceTreeView;
