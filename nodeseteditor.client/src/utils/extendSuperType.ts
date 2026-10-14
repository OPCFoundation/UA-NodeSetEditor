import { OPC_UA_CORE_URI } from './formatNodeId';

/**
 * Extend starts a new type whose base type is the node the author had selected. For the
 * roots of the ReferenceTypes, VariableTypes and DataTypes categories that is almost never
 * what they mean: References, BaseVariableType and BaseDataType are abstract roots whose own
 * direct subtypes are structural (HierarchicalReferences, PropertyType, Enumeration, ...),
 * so deriving straight off one of them yields a type no client can use as intended. Extending
 * one of those rows therefore pre-selects the concrete type new types normally derive from.
 *
 * BaseObjectType (i=58) is deliberately absent: it is the ordinary base for a new ObjectType,
 * so there is nothing to substitute.
 */
const SUPER_TYPE_SUBSTITUTIONS = new Map<string, string>([
   ['i=31', 'i=32'], // References       → NonHierarchicalReferences
   ['i=62', 'i=63'], // BaseVariableType → BaseDataVariableType
   ['i=24', 'i=22'], // BaseDataType     → Structure
]);

const CORE_NSU_PREFIX = `nsu=${OPC_UA_CORE_URI};`;

/**
 * Map an abstract category root to the base type Extend should pre-select. Any other
 * NodeId — including every type in a non-core model — passes through unchanged.
 */
export function resolveExtendSuperTypeId(nodeId: string): string {
   if (!nodeId) return nodeId;
   // Core NodeIds are stored bare ("i=31"); tolerate an explicit core nsu= prefix as well.
   const bare = nodeId.startsWith(CORE_NSU_PREFIX) ? nodeId.slice(CORE_NSU_PREFIX.length) : nodeId;
   return SUPER_TYPE_SUBSTITUTIONS.get(bare) ?? nodeId;
}
