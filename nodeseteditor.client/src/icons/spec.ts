import iconMap from './icon-map.json';

/**
 * The icon specification: sizes, the NodeClass vocabulary, and the well-known type
 * NodeIds the icon rules key off. Kept separate from the components in ./index.tsx so
 * each file exports one kind of thing (react-refresh/only-export-components).
 *
 * SET — Material Symbols (Apache-2.0), outline + rounded, resolved by Iconify name.
 * ./icon-map.json holds every concept -> name pair and is the artifact shared with the
 * Blazor/ASP.NET applications. Add a concept there before using an icon for it.
 *
 * COLOUR & CONTAINER — a glyph is EITHER bare, OR inside a tinted chip, OR inside a
 * solid avatar. Never nest those: a tinted chip inside a grey avatar is what put the
 * Variable glyph at 1.01:1 against its own background. Colour carries STATE (error,
 * read-only, dimmed), not the node's kind — the glyph identifies that.
 */

export const IconSize = {
   /** Tree rows — matches body2's line box. */
   tree: 16,
   /** Inline with text, list rows, table cells. */
   inline: 20,
   /** Section and dialog headers. */
   header: 24,
} as const;

/** Iconify names, keyed by concept. See icon-map.json. */
export const NodeIcon = iconMap.nodeIcon;

/** Numeric NodeClass values per OPC UA Part 3. */
export const NodeClass = {
   Object: 1,
   Variable: 2,
   Method: 4,
   ObjectType: 8,
   VariableType: 16,
   ReferenceType: 32,
   DataType: 64,
   View: 128,
} as const;

export const nodeClassNames: Record<number, string> = {
   [NodeClass.Object]: 'Object',
   [NodeClass.Variable]: 'Variable',
   [NodeClass.Method]: 'Method',
   [NodeClass.ObjectType]: 'ObjectType',
   [NodeClass.VariableType]: 'VariableType',
   [NodeClass.ReferenceType]: 'ReferenceType',
   [NodeClass.DataType]: 'DataType',
   [NodeClass.View]: 'View',
};

/** Maps the string NodeClass the REST API returns onto its numeric value. */
export function nodeClassValue(nodeClass?: string | null): number {
   if (!nodeClass) return 0;
   const wanted = nodeClass.trim().toLowerCase();
   const entry = Object.entries(nodeClassNames).find(([, name]) => name.toLowerCase() === wanted);
   return entry ? Number(entry[0]) : 0;
}

/**
 * Well-known type NodeIds the icon rules branch on (OPC UA Part 6 Annex A). The
 * icon for an instance depends on which of these families its TypeDefinition sits
 * in, so each one needs its subtype closure — see useTypeFamilies().
 */
export const TypeAnchors = {
   /** Exact match only: an Object typed directly as BaseObjectType. */
   baseObjectType: 'i=58',
   /** Objects under FolderType get the folder glyph. */
   folderType: 'i=61',
   /** ObjectTypes under BaseInterfaceType are interfaces. */
   baseInterfaceType: 'i=17602',
   /** Variables under PropertyType get the label glyph. */
   propertyType: 'i=68',
   /** Variables under BaseDataVariableType (and anything else) get the ticket glyph. */
   baseDataVariableType: 'i=63',
} as const;

/** The anchors whose subtype closures the client needs, with their API category. */
export const TypeFamilyQueries = [
   { key: 'folderType', nodeId: TypeAnchors.folderType, category: 'object-types' },
   { key: 'baseInterfaceType', nodeId: TypeAnchors.baseInterfaceType, category: 'object-types' },
   { key: 'propertyType', nodeId: TypeAnchors.propertyType, category: 'variable-types' },
   { key: 'dataVariableType', nodeId: TypeAnchors.baseDataVariableType, category: 'variable-types' },
] as const;

export type TypeFamilyKey = typeof TypeFamilyQueries[number]['key'];

/** Subtype closures, keyed by family. Each Set includes the anchor type itself. */
export type TypeFamilies = Record<TypeFamilyKey, ReadonlySet<string>>;
