import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';

export const OPC_UA_CORE_URI = 'http://opcfoundation.org/UA/';

/** Build a namespace URI → model-name map from namespace info. */
export function buildNamespaceMap(namespaces: WorkspaceNamespaceInfo[]): Map<string, string> {
   const map = new Map<string, string>();
   for (const ns of namespaces) {
      if (ns.uri && ns.name) map.set(ns.uri, ns.name);
   }
   return map;
}

/** Extract the namespace URI from a NodeId, defaulting to the core URI. */
export function extractNamespaceUri(nodeId?: string): string {
   if (!nodeId) return OPC_UA_CORE_URI;
   if (nodeId.startsWith('nsu=')) {
      const semi = nodeId.indexOf(';');
      if (semi > 4) return nodeId.substring(4, semi);
   }
   return OPC_UA_CORE_URI;
}

/**
 * Extract the namespace URI from a BrowseName ("nsu=uri;Name" → uri).
 *
 * A BrowseName's namespace is independent of the node's own NodeId namespace:
 * an overridden inherited property (e.g. Definition on a subtype of
 * AnalogItemType) keeps its declaring namespace (ns0) even though the override
 * node itself lives in the current model. A bare BrowseName (no "nsu=" prefix)
 * denotes the core namespace (ns=0) — that is how ns0 BrowseNames are stored —
 * so it must NOT be defaulted to the node's own/current-model namespace, which
 * would silently switch the BrowseName's namespace on save.
 */
export function extractBrowseNameNamespace(browseName?: string): string {
   if (browseName?.startsWith('nsu=')) {
      const semi = browseName.indexOf(';');
      if (semi > 4) return browseName.substring(4, semi);
   }
   return OPC_UA_CORE_URI;
}

/**
 * The single source of truth for URI → "[ModelName]" prefix.
 * Returns "" when the URI is not in the map.
 */
export function modelPrefix(modelUri: string | undefined, nsMap?: Map<string, string>): string {
   if (!nsMap) return '';
   const uri = modelUri ?? OPC_UA_CORE_URI;
   const name = nsMap.get(uri);
   return name ? `[${name}]` : '';
}

/**
 * Apply the [ModelName]: prefix to a display name. A leading "nsu=URI;" is
 * stripped first so qualified BrowseNames flow through the same path as
 * plain names; the embedded URI is used as a fallback when no modelUri is
 * supplied.
 */
export function formatWithModelPrefix(
   displayName: string,
   modelUri: string | undefined,
   nsMap?: Map<string, string>,
): string {
   // Be defensive: callers map option lists from varied API shapes, and a
   // non-string (e.g. an undefined or a LocalizedText object that slipped
   // through) would otherwise throw and take down the whole component tree.
   const rawName: unknown = displayName;
   let name = typeof rawName === 'string'
      ? rawName
      : (rawName && typeof rawName === 'object'
         && typeof (rawName as { text?: unknown }).text === 'string')
         ? (rawName as { text: string }).text
         : String(rawName ?? '');
   let uri = modelUri;

   if (name.startsWith('nsu=')) {
      const semi = name.indexOf(';');
      if (semi > 4) {
         uri = uri ?? name.substring(4, semi);
         name = name.substring(semi + 1);
      }
   }

   // Already prefixed (e.g. "[Core]:DeviceType" or "DI:DeviceType") — leave alone.
   if (name.includes(':')) return name;

   const prefix = modelPrefix(uri, nsMap);
   return prefix ? `${prefix}:${name}` : name;
}

/** Format a NodeId for display: nsu=http://...;i=1234 → [ModelName]:i=1234 */
export function formatNodeId(nodeId: string | undefined, nsMap?: Map<string, string>): string {
   if (!nodeId) return '';
   return formatWithModelPrefix(nodeId, undefined, nsMap);
}

/** Format a BrowseName for display: nsu=http://...;TypeName → [ModelName]:TypeName */
export function formatBrowseName(browseName: string | undefined, nsMap?: Map<string, string>): string {
   if (!browseName) return '';
   return formatWithModelPrefix(browseName, undefined, nsMap);
}
