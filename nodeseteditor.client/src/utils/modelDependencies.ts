import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';

/**
 * Builds a map from each model URI to the full set of model URIs it depends on,
 * transitively (i.e. its direct RequiredModels plus all of their dependencies).
 */
export function buildTransitiveDeps(namespaces: WorkspaceNamespaceInfo[]): Map<string, Set<string>> {
   // Direct deps per URI
   const direct = new Map<string, string[]>();
   for (const ns of namespaces) {
      if (ns.uri) direct.set(ns.uri, ns.requiredNamespaceUris ?? []);
   }

   const cache = new Map<string, Set<string>>();

   function resolve(uri: string, visiting = new Set<string>()): Set<string> {
      if (cache.has(uri)) return cache.get(uri)!;
      if (visiting.has(uri)) return new Set(); // cycle guard
      visiting.add(uri);
      const result = new Set<string>();
      for (const dep of direct.get(uri) ?? []) {
         result.add(dep);
         for (const transitive of resolve(dep, visiting)) result.add(transitive);
      }
      cache.set(uri, result);
      return result;
   }

   for (const uri of direct.keys()) resolve(uri);
   return cache;
}

/**
 * Returns true if adding a dependency from `fromUri` on `onUri` would create
 * a cycle — i.e. `onUri` already (transitively) depends on `fromUri`.
 */
export function wouldCreateCycle(
   fromUri: string,
   onUri: string,
   transitiveDeps: Map<string, Set<string>>,
): boolean {
   if (!fromUri || !onUri || fromUri === onUri) return false;
   return transitiveDeps.get(onUri)?.has(fromUri) ?? false;
}
