import type { QueryClient } from '@tanstack/react-query';

/**
 * Invalidates everything the address-space tree and the node views draw from, after a
 * node is created, deleted, renamed or re-parented.
 *
 * Why the whole families rather than the keys of the node that changed: the tree takes a
 * row's LABEL — and its has-children flag — from its PARENT's listing, and with a model
 * filter on, the entire Objects branch is a single `nodeChildren` query. Invalidating only
 * the edited node's own keys left the sidebar showing the old name, or a node with no
 * expand arrow to reach the child just added to it.
 *
 * It is cheaper than it looks: invalidation refetches only MOUNTED queries, which is the
 * handful of expanded tree rows plus the open view. Collapsed branches are merely marked
 * stale and refetch if the user expands them.
 */
export function refreshAddressSpace(queryClient: QueryClient): void {
   // Instance hierarchy (tree rows and the Children views).
   queryClient.invalidateQueries({ queryKey: ['nodeChildren'] });
   queryClient.invalidateQueries({ queryKey: ['nodeReferences'] });
   // Type hierarchy (the tree's type categories).
   queryClient.invalidateQueries({ queryKey: ['subtypes'] });
   queryClient.invalidateQueries({ queryKey: ['workspaceTypes'] });
   // Flat type/instance lists (Type Library, pickers).
   queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
   // The focused tree's root row renders from its own node query.
   queryClient.invalidateQueries({ queryKey: ['focusedRoot'] });
   // The next free NodeId moves whenever a node is created.
   queryClient.invalidateQueries({ queryKey: ['nextNodeId'] });
}
