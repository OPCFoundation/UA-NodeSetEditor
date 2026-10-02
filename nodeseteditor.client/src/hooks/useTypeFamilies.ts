import * as React from 'react';
import { useQueries } from '@tanstack/react-query';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { Node } from '../model/Node';
import { TypeFamilyQueries, type TypeFamilies, type TypeFamilyKey } from '../icons/spec';

/**
 * Loads the subtype closure of each type family the icon rules branch on (FolderType,
 * BaseInterfaceType, PropertyType, BaseDataVariableType).
 *
 * An instance's icon depends on which family its TypeDefinition belongs to, and that
 * is a question about the type hierarchy, not about the node — so each anchor's
 * subtypes are fetched once per workspace and kept as a Set for O(1) membership tests.
 * The closures are per-workspace because a workspace's models can add subtypes of any
 * of these.
 *
 * Returns `undefined` until every family has loaded, which resolveNodeIcon() treats as
 * "fall back to the NodeClass glyph" — so rows render immediately and sharpen once the
 * hierarchy arrives, rather than flashing a placeholder.
 *
 * The 10-minute staleTime matches the tree's own subtype queries: the hierarchy only
 * changes when a model is imported or edited, and both paths already invalidate every
 * workspace-scoped query.
 */
export function useTypeFamilies(workspaceId: string | null | undefined): TypeFamilies | undefined {
   const results = useQueries({
      queries: TypeFamilyQueries.map(({ key, nodeId, category }) => ({
         queryKey: ['typeFamily', workspaceId, key],
         queryFn: async () => {
            const response = await api.get<PaginatedResponse<Node>>(
               `/opcua/v1/types/${category}/${slugifyNodeId(nodeId)}/subtypes`,
               {
                  // depth is bounded in the API; 32 is far deeper than any real
                  // HasSubtype chain, and includeSelf puts the anchor in the Set so an
                  // instance typed directly as e.g. FolderType matches.
                  params: { depth: 32, count: 10000, includeSelf: true },
                  headers: { 'OpcUa-Server': idToUrn(workspaceId!) },
               },
            );
            return (response.data.results ?? []).map(n => n.nodeId);
         },
         enabled: !!workspaceId,
         staleTime: 1000 * 60 * 10,
      })),
   });

   const allLoaded = results.length > 0 && results.every(r => r.data !== undefined);
   // Depend on the data arrays rather than the result objects: useQueries returns fresh
   // wrappers each render, so keying the memo on them would rebuild the Sets constantly.
   const dataKey = results.map(r => r.data?.length ?? -1).join(',');

   return React.useMemo(() => {
      if (!allLoaded) return undefined;
      const families = {} as Record<TypeFamilyKey, ReadonlySet<string>>;
      TypeFamilyQueries.forEach(({ key }, i) => {
         families[key] = new Set(results[i].data ?? []);
      });
      return families as TypeFamilies;
      // eslint-disable-next-line react-hooks/exhaustive-deps
   }, [allLoaded, dataKey]);
}
