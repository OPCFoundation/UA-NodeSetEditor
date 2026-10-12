import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import { getNodePlainName } from '../model/Node';
import type { Node as RestNode, ReferenceDescription } from '../model/Node';
import { extractBrowseNameNamespace } from './formatNodeId';
import { stripNamespace } from './instantiateUtils';

/** HasProperty / HasComponent / Organizes. */
const HAS_PROPERTY = 'i=46';
const HAS_COMPONENT = 'i=47';
export const ORGANIZES = 'i=35';

/**
 * How a node hangs off the one above it in the tree.
 *
 * - `parent`: ParentNodeId plus the matching reference. Ownership — deleting the parent
 *   takes the child with it — and necessarily WITHIN one model: the reference is stored
 *   on the child, so a cross-model ParentId would make the model's NodeSet depend on a
 *   node it cannot see.
 * - `reference`: an inverse hierarchical reference only, with no ParentId. The node is
 *   top-level in its own right and merely organized under the target. This is the only
 *   shape containment can take ACROSS models — e.g. an object in a private model sitting
 *   under the Core Objects folder.
 * - `none`: standalone top-level node, nothing above it.
 */
export type ParentLinkKind = 'parent' | 'reference' | 'none';

export interface ParentLink {
   kind: ParentLinkKind;
   /** The node above this one. Absent for `none`. */
   targetNodeId?: string;
   /** The hierarchical reference type linking the two. */
   referenceTypeId: string;
}

/**
 * Works out how a node is attached to the tree.
 *
 * The reference list is what makes this reliable: the server flags the structural parent
 * link with `isCanonicalParent`, computed from its own GetStructuralParent — which prefers
 * the inverse reference authored on the node and falls back to the reference index. So a
 * cross-model link, which no ParentNodeId can express, still shows up here.
 */
export async function resolveParentLink(
   workspaceId: string,
   nodeId: string,
): Promise<ParentLink> {
   const headers = { 'OpcUa-Server': idToUrn(workspaceId) };
   const [nodeResponse, refsResponse] = await Promise.all([
      api.get<RestNode>(`/opcua/v1/nodes/${slugifyNodeId(nodeId)}`, { headers }),
      api.get<PaginatedResponse<ReferenceDescription>>(
         `/opcua/v1/nodes/${slugifyNodeId(nodeId)}/references`, { headers }),
   ]);
   const node = nodeResponse.data;
   const refs = refsResponse.data.results ?? [];
   // Only the INVERSE direction: the same isCanonicalParent flag is set on the FORWARD
   // references that tie the node to its own children.
   const parentRef = refs.find(r => !r.isForward && r.isCanonicalParent);

   if (node.parentNodeId) {
      return {
         kind: 'parent',
         targetNodeId: node.parentNodeId,
         referenceTypeId: parentRef?.referenceTypeId
            ?? (node.isProperty ? HAS_PROPERTY : HAS_COMPONENT),
      };
   }
   if (parentRef) {
      return {
         kind: 'reference',
         targetNodeId: parentRef.targetNodeId,
         referenceTypeId: parentRef.referenceTypeId,
      };
   }
   return { kind: 'none', referenceTypeId: ORGANIZES };
}

export interface ClonedNodeInfo {
   nodeId: string;
   displayName: string;
   nodeClass: number;
}

export interface CloneNodeOptions {
   workspaceId: string;
   /** The node being copied. Its model may be read-only — only the target has to be writable. */
   sourceNodeId: string;
   /** 1 = Object, 2 = Variable, 4 = Method. */
   nodeClass: number;
   /** The writable model the clone (and its copied subtree) is created in. */
   modelUri: string;
   /**
    * How and where the clone attaches — normally the same relationship the source has,
    * so the copy lands as its sibling. Anything other than `parent` produces a top-level
    * node, which the server only allows for an Object or a Variable: a Method must be a
    * child of something.
    */
   link: ParentLink;
   browseName: string;
   browseNameModelUri?: string;
   displayName?: string;
   description?: string;
   typeDefinitionId?: string;
   modellingRuleId?: string;
   dataType?: string;
   valueRank?: number;
   arrayDimensions?: string;
   designToolOnly?: boolean;
   /** Conformance units — only meaningful on a top-level Object. */
   category?: string[];
   /** Copy the source's own descendants under the clone as well. */
   includeChildren: boolean;
}

const NODE_CLASS_NAMES: Record<number, string> = { 1: 'Object', 2: 'Variable', 4: 'Method' };

/**
 * Copies an instance Object / Variable / Method, with the attributes the caller passes
 * (which the clone dialog lets the user revise first). The copy is created in a writable
 * model regardless of which model the source lives in, so a Core node can be cloned into
 * a private one.
 *
 * What travels with the copy: the attributes given here, the source's Value, and — when
 * `includeChildren` is set — its whole containment subtree (each descendant's attributes,
 * TypeDefinition, ModellingRule and Value). What does not: references outside that
 * containment hierarchy, e.g. HasInterface or an Organizes link added by hand. Those name
 * targets that may themselves be inside the copied subtree, and re-pointing them is not
 * something this can decide for the user — add them to the clone afterwards.
 */
export async function cloneNode(options: CloneNodeOptions): Promise<ClonedNodeInfo> {
   const {
      workspaceId, sourceNodeId, nodeClass, modelUri, link,
      browseName, browseNameModelUri, displayName, description, typeDefinitionId,
      modellingRuleId, dataType, valueRank, arrayDimensions, designToolOnly, category,
      includeChildren,
   } = options;
   const headers = { 'OpcUa-Server': idToUrn(workspaceId) };
   // Only a `parent` link is created as a child; the other two kinds are top-level nodes,
   // one of which then gets its inverse reference added below.
   const asChildOf = link.kind === 'parent' ? link.targetNodeId : undefined;

   const body = {
      modelUri,
      nodeClass: NODE_CLASS_NAMES[nodeClass] ?? 'Object',
      browseName,
      browseNameModelUri,
      displayName: displayName || browseName,
      description,
      typeDefinitionId,
      dataType,
      valueRank,
      arrayDimensions,
      category,
      // Copies the source's Value onto the new node.
      sourceNodeId,
   };

   const created = asChildOf
      ? await api.post<RestNode>(
         `/opcua/v1/nodes/${slugifyNodeId(asChildOf)}/children`,
         { ...body, referenceTypeId: link.referenceTypeId, modellingRuleId },
         { headers },
      )
      : await api.post<RestNode>(
         '/opcua/v1/nodes',
         {
            ...body,
            // A design-tool-only node may carry no references at all, so it can never be
            // the organized kind.
            designToolOnly: (link.kind === 'none' && designToolOnly) || undefined,
         },
         { headers },
      );

   const nodeId = created.data?.nodeId;
   if (!nodeId) throw new Error('The server did not return a NodeId for the clone.');

   // Organized, not owned: the clone stays top-level (no ParentId) and the containment is
   // the inverse reference instead — the shape that survives crossing a model boundary.
   if (link.kind === 'reference' && link.targetNodeId) {
      await api.post(
         `/opcua/v1/nodes/${slugifyNodeId(nodeId)}/references`,
         {
            referenceTypeId: link.referenceTypeId,
            targetNodeId: link.targetNodeId,
            isForward: false,
         },
         { headers },
      );
   }

   // The top-level create ignores sourceNodeId (only a child takes its default Value from
   // a declaration), so a top-level Variable's Value has to be carried over by hand.
   if (!asChildOf && nodeClass === 2) {
      const source = await api.get<RestNode>(
         `/opcua/v1/nodes/${slugifyNodeId(sourceNodeId)}`, { headers });
      if (source.data.value !== undefined && source.data.value !== null) {
         await api.put(
            `/opcua/v1/nodes/${slugifyNodeId(nodeId)}`,
            { value: source.data.value },
            { headers },
         );
      }
   }

   if (includeChildren) {
      await copySubtree(workspaceId, sourceNodeId, nodeId, modelUri);
   }

   return { nodeId, displayName: displayName || browseName, nodeClass };
}

/**
 * Recreates every own child of `srcId` under `dstId`, depth first. Inherited rows are
 * declarations the TypeDefinition contributes rather than nodes of their own — the clone
 * inherits them again from the same type, so re-creating them here would turn each one
 * into an override.
 */
async function copySubtree(
   workspaceId: string,
   srcId: string,
   dstId: string,
   modelUri: string,
): Promise<void> {
   const headers = { 'OpcUa-Server': idToUrn(workspaceId) };
   const response = await api.get<PaginatedResponse<RestNode>>(
      `/opcua/v1/nodes/${slugifyNodeId(srcId)}/children`,
      { params: { full: true }, headers },
   );
   for (const child of response.data.results ?? []) {
      if (child.isInherited || !child.nodeId) continue;
      const created = await api.post<RestNode>(
         `/opcua/v1/nodes/${slugifyNodeId(dstId)}/children`,
         {
            modelUri,
            nodeClass: child.nodeClass,
            browseName: stripNamespace(child.browseName ?? ''),
            // A child's BrowseName namespace is its own: a declaration inherited from a
            // Core type keeps ns0 even when the copy is created in a private model, and
            // that is what makes the copy match the same declaration.
            browseNameModelUri: extractBrowseNameNamespace(child.browseName),
            displayName: getNodePlainName(child),
            description: child.description?.text,
            referenceTypeId: child.referenceTypeId,
            typeDefinitionId: child.typeDefinition,
            modellingRuleId: child.modellingRule,
            dataType: child.dataType,
            valueRank: child.valueRank,
            arrayDimensions: child.arrayDimensions?.join(','),
            // Carries the child's Value over to the copy.
            sourceNodeId: child.nodeId,
         },
         { headers },
      );
      const createdNodeId = created.data?.nodeId;
      if (createdNodeId) await copySubtree(workspaceId, child.nodeId, createdNodeId, modelUri);
   }
}
