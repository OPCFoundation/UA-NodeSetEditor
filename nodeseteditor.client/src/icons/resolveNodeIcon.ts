import {
   NodeClass, NodeIcon, TypeAnchors, nodeClassValue,
   type TypeFamilies,
} from './spec';

export interface NodeIconInput {
   /** Numeric NodeClass, or the string form the API returns. */
   nodeClass: number | string | null | undefined;
   /** The node's own NodeId. Needed for TYPE nodes — see below. */
   nodeId?: string | null;
   /** The node's TypeDefinition NodeId — `Node.typeDefinition` from the API. */
   typeDefinition?: string | null;
}

/**
 * Picks the Iconify name for a node.
 *
 * Which identity a rule tests depends on whether the node is an instance or a type,
 * and getting that backwards is silent — the branch simply never fires:
 *
 *  - INSTANCES (Object, Variable) are keyed off their TypeDefinition: "is this node's
 *    type under FolderType / PropertyType / BaseDataVariableType".
 *  - TYPES (ObjectType) are keyed off their OWN NodeId: a type node has no
 *    TypeDefinition at all — that is an instance attribute — so an interface is an
 *    ObjectType whose nodeId is in BaseInterfaceType's (i=17602) subtype closure,
 *    which includes BaseInterfaceType itself. There is no such thing as an instance
 *    of an interface: HasInterface grafts members onto an object whose TypeDefinition
 *    is still an ordinary ObjectType, so no instance rule mentions interfaces.
 *
 * Pass `families` as `undefined` while the closures load: the result falls back to the
 * NodeClass-level glyph, so a row never renders empty or flashes a placeholder.
 *
 * FolderType is tested before the BaseObjectType default so a FolderType instance is a
 * folder rather than a generic object. PropertyType and BaseDataVariableType are
 * disjoint siblings under BaseVariableType, so those two cannot both match.
 */
export function resolveNodeIcon(node: NodeIconInput, families?: TypeFamilies): string {
   const cls = typeof node.nodeClass === 'number' ? node.nodeClass : nodeClassValue(node.nodeClass);
   const typeDef = node.typeDefinition?.trim() || undefined;
   const self = node.nodeId?.trim() || undefined;
   /** For instances: is the node's TypeDefinition in this family? */
   const typeInFamily = (key: keyof TypeFamilies) =>
      !!typeDef && !!families && families[key].has(typeDef);
   /** For type nodes: is the node itself in this family? */
   const selfInFamily = (key: keyof TypeFamilies) =>
      !!self && !!families && families[key].has(self);

   switch (cls) {
      case NodeClass.Method:
         return NodeIcon.method;
      case NodeClass.View:
         return NodeIcon.view;

      case NodeClass.ObjectType:
         return selfInFamily('baseInterfaceType') ? NodeIcon.interfaceType : NodeIcon.objectType;
      case NodeClass.VariableType:
         return NodeIcon.variableType;
      case NodeClass.DataType:
         return NodeIcon.dataType;
      case NodeClass.ReferenceType:
         return NodeIcon.referenceType;

      case NodeClass.Object:
         if (typeInFamily('folderType')) return NodeIcon.folder;
         // BaseObjectType exactly, and the default for every other TypeDefinition:
         // a MachineType instance is still an Object.
         return NodeIcon.object;

      case NodeClass.Variable:
         if (typeInFamily('propertyType')) return NodeIcon.property;
         // BaseDataVariableType and its subtypes, and the default for anything else.
         return NodeIcon.dataVariable;

      default:
         return NodeIcon.unknown;
   }
}

/** Exposed for tests and for the type-family loader. */
export const iconTypeAnchors = TypeAnchors;
