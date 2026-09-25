import type { LocalizedText } from './WorkspaceDescription';

/** Get the plain display name without namespace prefix */
export function getNodePlainName(node: { displayName?: LocalizedText; browseName?: string; nodeId?: string }): string {
   const name = node.displayName?.text ?? '';
   if (name) return name;
   const browseName = node.browseName ?? '';
   // Extract plain name from "nsu=URI;Name" format
   const semiIdx = browseName.indexOf(';');
   if (semiIdx >= 0) return browseName.substring(semiIdx + 1);
   // Fallback for "ModelName:Name" format
   const colonIdx = browseName.indexOf(':');
   if (colonIdx > 0) return browseName.substring(colonIdx + 1);
   return browseName || node.nodeId || '';
}

export interface Node {
   nodeId: string;
   nodeClass: string;
   browseName: string;
   displayName: LocalizedText;
   description?: LocalizedText;
   isAbstract?: boolean;
   // ReferenceType only. A symmetric reference reads the same in both
   // directions and therefore carries no inverseName.
   symmetric?: boolean;
   inverseName?: LocalizedText;
   // Top-level Object/Variable only: node exists only in the design tool
   // (no children/references, instantiation skipped). Set at creation only.
   designToolOnly?: boolean;
   // Variable whose TypeDefinition is PropertyType (i=68) or a subtype. A
   // Property may not be the source of hierarchical references (no children).
   isProperty?: boolean;
   superTypeId?: string;
   superTypeIds?: string[];
   parentNodeId?: string;
   hasNoSubtypes?: boolean;
   hasNoChildren?: boolean;
   /**
    * The subtype walk hit the requested depth here, so this node's subtypes are NOT in the
    * response even though it has some — re-browse from this node when it is expanded. Absent
    * means the response already carries whatever subtypes it has.
    */
   subtypesTruncated?: boolean;
   modelUri?: string;
   referenceType?: string;
   referenceTypeId?: string;
   typeDefinition?: string;
   typeDefinitionName?: string;
   modellingRule?: string;
   dataType?: string;
   dataTypeName?: string;
   valueRank?: number;
   arrayDimensions?: number[];
   isInherited?: boolean;
   sourceTypeNodeId?: string;
   isOverride?: boolean;
   dataTypeForm?: string;
   value?: unknown;
   documentation?: string;
   /**
    * Conformance units the node belongs to, one per entry — the <Category>
    * elements of the NodeSet XML.
    */
   category?: string[];
}

export interface ReferenceDescription {
   referenceTypeId: string;
   referenceTypeName?: string;
   isForward: boolean;
   targetNodeId: string;
   targetBrowseName?: string;
   targetDisplayName?: LocalizedText;
   targetNodeClass?: string;
   /**
    * True when this reference is the structural parent link for one of its
    * endpoints (Node table's ParentNodeId+ReferenceTypeId, or SuperTypeId
    * via HasSubtype for type nodes). The references view hides the delete
    * affordance for these — re-parenting/deleting the child is the only
    * way to remove them.
    */
   isCanonicalParent?: boolean;
}

export interface TreeNode extends Node {
   children: TreeNode[];
}

/** Build a tree from a flat list of nodes using superTypeId or parentNodeId. */
export function buildTree(nodes: Node[], rootId: string): TreeNode[] {
   const childrenMap = new Map<string, TreeNode[]>();

   for (const node of nodes) {
      const parentId = node.superTypeId ?? node.parentNodeId ?? rootId;
      const treeNode: TreeNode = { ...node, children: [] };

      if (!childrenMap.has(parentId)) {
         childrenMap.set(parentId, []);
      }
      childrenMap.get(parentId)!.push(treeNode);
   }

   function attachChildren(treeNode: TreeNode): void {
      const kids = childrenMap.get(treeNode.nodeId);
      if (kids) {
         treeNode.children = kids;
         for (const kid of kids) attachChildren(kid);
      }
   }

   const roots = childrenMap.get(rootId) ?? [];
   for (const root of roots) attachChildren(root);
   return roots;
}
