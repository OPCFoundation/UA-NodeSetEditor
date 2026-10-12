import * as React from 'react';
import { useQuery } from '@tanstack/react-query';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { Node as RestNode } from '../model/Node';
import type { NodeIdOption } from '../components/NodeIdAutocomplete';

/** HierarchicalReferences — the root of every reference type that nests one node in another. */
const HIERARCHICAL_REFERENCES = 'i=33';

/**
 * The reference types that can tie a node to the one above it, as picker options.
 *
 * Restricted to subtypes of HierarchicalReferences: a non-hierarchical reference
 * (HasTypeDefinition, HasEncoding, a plain cross-link) says nothing about where a node
 * sits, so offering one would let the user build a containment that no tree can show.
 *
 * The query key is shared with the other call sites, so the list is fetched once per
 * workspace however many dialogs ask for it.
 */
export function useHierarchicalReferenceTypes(
   workspaceId: string,
   enabled = true,
): NodeIdOption[] {
   const { data } = useQuery({
      queryKey: ['hierarchicalRefTypes', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/reference-types/${slugifyNodeId(HIERARCHICAL_REFERENCES)}/subtypes`,
            {
               params: { depth: 10, count: 10000 },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            },
         );
         return response.data.results ?? [];
      },
      enabled: enabled && !!workspaceId,
      staleTime: 1000 * 60 * 5,
   });

   return React.useMemo(() => (data ?? []).map((rt): NodeIdOption => ({
      nodeId: rt.nodeId ?? '',
      displayName: rt.browseName ?? rt.displayName?.text ?? rt.nodeId ?? '',
      modelUri: rt.modelUri,
   })), [data]);
}
