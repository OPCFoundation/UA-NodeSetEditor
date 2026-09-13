import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { Node } from '../model/Node';

export function stripNamespace(value: string): string {
   const semi = value.indexOf(';');
   return semi >= 0 ? value.substring(semi + 1) : value;
}

export function isMandatory(modellingRuleId?: string): boolean {
   return modellingRuleId === 'i=78' || modellingRuleId === 'i=11510';
}

/**
 * After creating a new Object or Variable child, populate it with the mandatory
 * children declared by its TypeDefinition. Calls the server-side
 * /nodes/{nodeId}/instantiate-children endpoint, which adds the children directly
 * under the supplied node — no wrapper instance is created.
 */
export async function autoInstantiateMandatoryChildren(
   workspaceId: string,
   parentNodeId: string,
   typeNodeId: string,
   modelUri: string,
): Promise<Node[]> {
   const response = await api.post<PaginatedResponse<Node>>(
      `/opcua/v1/nodes/${slugifyNodeId(parentNodeId)}/instantiate-children`,
      {
         typeNodeId,
         modelUri,
      },
      { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
   );

   return response.data.results ?? [];
}

/**
 * Populate a node created from an instance declaration with every mandatory
 * descendant of that declaration, recursively.
 *
 * Prefer this over {@link autoInstantiateMandatoryChildren} whenever the source
 * declaration is known: the TypeDefinition does not carry the children the owning
 * type authored under the declaration, and those are precisely what a subtype
 * inherits when it overrides an inherited child.
 */
export async function autoInstantiateMandatoryDescendants(
   workspaceId: string,
   parentNodeId: string,
   sourceNodeId: string,
   modelUri: string,
): Promise<Node[]> {
   const response = await api.post<PaginatedResponse<Node>>(
      `/opcua/v1/nodes/${slugifyNodeId(parentNodeId)}/instantiate-children`,
      {
         sourceNodeId,
         modelUri,
      },
      { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
   );

   return response.data.results ?? [];
}

// Re-export TemplateChildDto as Node for backward compat
export type TemplateChildDto = Node;
