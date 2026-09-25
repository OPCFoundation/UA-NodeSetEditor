namespace NodeSetEditor.Model
{
    /// <summary>
    /// The NodeSet-level convention for carrying a namespace's profile group.
    ///
    /// <para>A conformance unit (a NodeSet XML <c>&lt;Category&gt;</c> element) of the form
    /// <c>ProfileGroup:&lt;Name&gt;</c> on the namespace's <b>NamespaceMetadata</b> object declares
    /// the profile group for that namespace. On <b>any other node</b> the same spelling carries no
    /// special meaning and is kept as a literal conformance-unit name — the convention is scoped
    /// to the one node that describes the namespace as a whole, so a model is free to use the
    /// string elsewhere.</para>
    ///
    /// <para>The value is <b>not</b> retained in the NamespaceMetadata node's own conformance-unit
    /// list. It is lifted out on import into <see cref="Model.GetProfileGroupName"/> (model
    /// metadata) and written back on export, so there is exactly one place it is edited and
    /// exactly one place it is stored. Everything in between — the editor UI, the address space,
    /// the conformance-unit view — sees the model metadata, never the encoded unit.</para>
    ///
    /// <para>This is a forward-looking convention: NodeSets the editor exports always carry it,
    /// and NodeSets from the Cloud Library are expected to adopt it over time.</para>
    /// </summary>
    public static class ProfileGroupConvention
    {
        /// <summary>The reserved conformance-unit prefix. Written exactly like this.</summary>
        public const string Prefix = "ProfileGroup:";

        /// <summary>
        /// Splits a NamespaceMetadata node's conformance units into the profile group the
        /// convention encodes and the units that remain. Reading is case-insensitive on the
        /// prefix (a hand-written NodeSet may not match the canonical casing) while
        /// <see cref="Merge"/> always writes the canonical form.
        ///
        /// <para>A prefixed entry with a blank name declares nothing and is still removed — it is
        /// a reserved spelling on this node either way. If several are present the first wins and
        /// the rest are dropped, so a re-export is unambiguous.</para>
        /// </summary>
        /// <returns>
        /// The profile group name (null when the convention is absent), and the remaining units
        /// (null when nothing is left, matching how "no units" is expressed in the NodeSet XML).
        /// </returns>
        public static (string? ProfileGroupName, string[]? Units) Split(string[]? units)
        {
            if (units == null || units.Length == 0) return (null, units);

            string? profileGroupName = null;
            var remaining = new List<string>(units.Length);

            foreach (var unit in units)
            {
                var value = unit ?? string.Empty;
                if (!value.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    remaining.Add(value);
                    continue;
                }

                var name = value.TrimStart()[Prefix.Length..].Trim();
                if (profileGroupName == null && name.Length > 0) profileGroupName = name;
            }

            return (profileGroupName, remaining.Count > 0 ? remaining.ToArray() : null);
        }

        /// <summary>
        /// Re-inserts the profile group as the leading conformance unit for export. Any prefixed
        /// entry already in <paramref name="units"/> is replaced, so exporting a model that was
        /// imported from a NodeSet using the convention round-trips to one entry, not two.
        /// A null/blank <paramref name="profileGroupName"/> just strips the convention.
        /// </summary>
        public static string[]? Merge(string[]? units, string? profileGroupName)
        {
            var (_, remaining) = Split(units);
            var name = profileGroupName?.Trim();

            if (string.IsNullOrEmpty(name)) return remaining;

            var merged = new List<string>(1 + (remaining?.Length ?? 0)) { Prefix + name };
            if (remaining != null) merged.AddRange(remaining);
            return merged.ToArray();
        }
    }
}
