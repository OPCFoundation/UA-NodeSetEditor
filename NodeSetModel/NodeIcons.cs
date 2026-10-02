using System;
using System.Collections.Generic;

namespace NodeSetEditor.Model
{
    /// <summary>
    /// The icon rules, in one place.
    ///
    /// Keys mirror the client's <c>src/icons/icon-map.json</c>, which is the shared
    /// contract: that file maps a key to a Material Symbols glyph, and the Blazor/ASP.NET
    /// applications read the same keys. Nothing here names a glyph — only concepts — so
    /// changing how an icon looks never touches the server or the database.
    ///
    /// A key is stamped onto TYPE nodes (<see cref="Node.Icon"/>) at import and at
    /// creation. Instances store nothing: they are served their TypeDefinition's key.
    /// Null means "no rule applies" and the client falls back to a NodeClass default, so
    /// an unstamped node is never broken — just generic.
    /// </summary>
    public static class NodeIcons
    {
        public const string Object = "object";
        public const string Folder = "folder";
        public const string Property = "property";
        public const string DataVariable = "dataVariable";
        public const string InterfaceType = "interfaceType";

        /// <summary>
        /// Anchor type NodeId -> icon key. Namespace-0 NodeIds are stored bare (see
        /// FormatColumnNodeId), hence "i=58" rather than a namespace-qualified form.
        ///
        /// To add a rule: add an anchor here and re-run <c>backfill-icons</c>, or simply
        /// patch the rows (UPDATE "Nodes" SET "Icon" = ...). Both work because the column
        /// is the source of truth at read time.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Anchors =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["i=58"] = Object,           // BaseObjectType
                ["i=61"] = Folder,           // FolderType
                ["i=17602"] = InterfaceType, // BaseInterfaceType
                ["i=68"] = Property,         // PropertyType
                ["i=63"] = DataVariable,     // BaseDataVariableType
            };

        /// <summary>
        /// The keys that describe the TYPE node itself, rather than its instances.
        ///
        /// Everything else in <see cref="Anchors"/> answers "what does an instance of this
        /// family look like": a Variable typed by PropertyType is a label, one typed by
        /// BaseDataVariableType is a ticket. Those glyphs must not land on the type node —
        /// PropertyType *is* a VariableType and belongs with the other VariableTypes.
        ///
        /// InterfaceType is the one exception, and deliberately so: no instances of an
        /// InterfaceType exist, so the only node that can carry the glyph is the ObjectType
        /// itself.
        /// </summary>
        public static readonly IReadOnlySet<string> TypeLevel =
            new HashSet<string>(StringComparer.Ordinal) { InterfaceType };

        /// <summary>
        /// True when <paramref name="icon"/> is a key a TYPE node may wear. A type node
        /// stamped with any other key falls back to its NodeClass glyph (ObjectType,
        /// VariableType, ...).
        /// </summary>
        public static bool IsTypeLevel(string? icon) =>
            !string.IsNullOrEmpty(icon) && TypeLevel.Contains(icon!);

        /// <summary>
        /// The icon key for a type node: the icon of the NEAREST anchor at or above it in
        /// the supertype chain, or null if it sits under none.
        ///
        /// Nearest-wins is what keeps the rules from colliding. FolderType and
        /// BaseInterfaceType are both subtypes of BaseObjectType, so a "first anchor
        /// found walking up" rule gives a FolderType subtype "folder" and an interface
        /// "interfaceType", while an ordinary MachineType falls through to "object".
        /// </summary>
        /// <param name="nodeId">The type node's own NodeId.</param>
        /// <param name="superTypeOf">
        /// Resolves a NodeId to its supertype's NodeId, or null at the top. May be asked
        /// about types in other models — a companion spec's types derive from core ones.
        /// </param>
        public static string? Resolve(string? nodeId, Func<string, string?> superTypeOf)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;

            var current = nodeId;
            // A HasSubtype cycle would be a malformed NodeSet, but it must not hang the
            // importer, so the walk is bounded and revisits are treated as the top.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(current) && seen.Add(current))
            {
                if (Anchors.TryGetValue(current, out var icon)) return icon;
                current = superTypeOf(current);
            }
            return null;
        }
    }
}
