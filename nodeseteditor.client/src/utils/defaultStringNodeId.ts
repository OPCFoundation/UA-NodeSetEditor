import { stripNamespace } from './instantiateUtils';

/**
 * How string NodeIds compose down a hierarchy — the convention the published
 * nodesets use, e.g. "NamespaceMetadata.ModelVersion" for the ModelVersion
 * property of a NamespaceMetadata object.
 */
export const NODE_ID_PATH_SEPARATOR = '.';

export interface NodeIdIdentifier {
   /** 'i' | 's' | 'g' | 'b' */
   idType: string;
   identifier: string;
}

/**
 * Split a stored NodeId into its id type and identifier:
 * "nsu=http://x/;s=Foo.Bar" → { idType: 's', identifier: 'Foo.Bar' }.
 * A core-namespace node is stored without the "nsu=" prefix ("i=85"), which
 * parses the same way.
 */
export function parseNodeIdIdentifier(nodeId?: string): NodeIdIdentifier | null {
   if (!nodeId) return null;
   let rest = nodeId;
   if (rest.startsWith('nsu=')) {
      const semi = rest.indexOf(';');
      if (semi < 0) return null;
      rest = rest.substring(semi + 1);
   }
   const match = /^([isgb])=([\s\S]*)$/.exec(rest);
   return match ? { idType: match[1], identifier: match[2] } : null;
}

/** True when the node carries a string NodeId, i.e. children should extend it. */
export function hasStringNodeId(nodeId?: string): boolean {
   return parseNodeIdIdentifier(nodeId)?.idType === 's';
}

/**
 * The string identifier to offer for a new node.
 *
 * A parent that already has a string id sets the convention, so the child just
 * extends it — that is what keeps a subtree's ids consistent and is how the
 * published nodesets read. When the parent's id is numeric there is nothing to
 * extend, so the identifier is built from the BrowseName path instead:
 * `ancestorBrowseNames` runs root-most first and is expected to include the
 * parent itself (which is what GET /nodes/{id}/path returns for the parent).
 *
 * Returns '' when there is no BrowseName yet — the caller leaves the field empty
 * rather than offering a trailing separator.
 */
export function buildDefaultStringIdentifier(
   browseName: string,
   parentNodeId?: string,
   ancestorBrowseNames: string[] = [],
): string {
   const leaf = stripNamespace(browseName).trim();
   if (!leaf) return '';

   const parentId = parseNodeIdIdentifier(parentNodeId);
   if (parentId?.idType === 's' && parentId.identifier) {
      return `${parentId.identifier}${NODE_ID_PATH_SEPARATOR}${leaf}`;
   }

   const segments = ancestorBrowseNames
      .map(name => stripNamespace(name).trim())
      .filter(name => name.length > 0);

   return [...segments, leaf].join(NODE_ID_PATH_SEPARATOR);
}
