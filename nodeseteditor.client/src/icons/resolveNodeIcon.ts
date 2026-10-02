import { NodeClass, NodeIcon, nodeClassValue } from './spec';

export interface NodeIconInput {
   /** Numeric NodeClass, or the string form the API returns. */
   nodeClass: number | string | null | undefined;
   /**
    * `Node.icon` from the API — the icon concept key the server stamped on this node's
    * type (or, for an instance, on its TypeDefinition). Absent when no rule applies.
    */
   icon?: string | null;
}

/** NodeClass -> icon, used whenever the server sent no key. */
const byNodeClass: Record<number, string> = {
   [NodeClass.Object]: NodeIcon.object,
   [NodeClass.Variable]: NodeIcon.dataVariable,
   [NodeClass.Method]: NodeIcon.method,
   [NodeClass.View]: NodeIcon.view,
   [NodeClass.ObjectType]: NodeIcon.objectType,
   [NodeClass.VariableType]: NodeIcon.variableType,
   [NodeClass.DataType]: NodeIcon.dataType,
   [NodeClass.ReferenceType]: NodeIcon.referenceType,
};

/**
 * Picks the Iconify name for a node.
 *
 * The rules themselves live server-side: which type family a node belongs to is decided
 * once, when the type is imported or created, and stored on the node (see
 * NodeSetModel/NodeIcons.cs). This function only maps the key the server sent onto a
 * glyph, so there is no hierarchy to fetch and nothing to walk at render time.
 *
 * A missing key is normal, not an error — it means no rule matched that node — so it
 * falls back to the NodeClass default. That is also what makes the server change
 * backwards-compatible: a database that has not been backfilled yet simply renders
 * generic icons rather than breaking.
 */
export function resolveNodeIcon(node: NodeIconInput): string {
   const key = node.icon?.trim();
   if (key) {
      // An unrecognised key falls through rather than rendering nothing: the server may
      // be ahead of this client (a new rule rolled out by patching rows), and a generic
      // icon beats a blank one.
      const named = (NodeIcon as Record<string, string | string[]>)[key];
      if (typeof named === 'string') return named;
   }

   const cls = typeof node.nodeClass === 'number' ? node.nodeClass : nodeClassValue(node.nodeClass);
   return byNodeClass[cls] ?? NodeIcon.unknown;
}
