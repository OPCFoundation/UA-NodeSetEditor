namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Naming helpers for OPC UA namespaces. Centralized so every path that derives a display
    /// name from a namespace URI (dependency import, Cloud Library browse, Cloud Library import)
    /// stays consistent — the Cloud Library can publish one namespace under several submission
    /// titles, so a URI-derived name is the reliable fallback when titles conflict.
    ///
    /// Only ever used to SEED a name: callers leave a curated one alone (see the
    /// "Name == Uri" checks in DbNodeSetStorageService), so changing the rule here renames
    /// nothing that already exists — it only affects models imported from now on.
    /// </summary>
    public static class NamespaceNaming
    {
        /// <summary>The marker OPC UA namespaces nest their models under.</summary>
        private const string UaSegment = "/UA/";

        /// <summary>
        /// Display name from a namespace URI: everything after "/UA/", with the remaining path
        /// separators read as spaces (e.g. "http://opcfoundation.org/UA/PADIM/" → "PADIM",
        /// "http://opcfoundation.org/UA/Dictionary/IRDI" → "Dictionary IRDI").
        ///
        /// The whole tail is kept rather than just the last segment because the leading segments
        /// are what distinguish a model from another spec's identically named leaf — two
        /// namespaces ending in "/Jobs/" are both just "Jobs" otherwise.
        ///
        /// URIs with no "/UA/" (a vendor's own namespace, say) keep the older last-segment
        /// behaviour, as does "http://opcfoundation.org/UA/" itself, which has nothing after the
        /// marker and would otherwise derive an empty name.
        /// </summary>
        public static string DeriveFromUri(string uri)
        {
            var ua = uri.IndexOf(UaSegment, System.StringComparison.Ordinal);
            if (ua >= 0)
            {
                // Splitting on the separator (rather than replacing it) drops the empty entries a
                // trailing or doubled slash would otherwise leave as stray spaces.
                var tail = string.Join(' ', uri[(ua + UaSegment.Length)..]
                    .Split('/', System.StringSplitOptions.RemoveEmptyEntries
                        | System.StringSplitOptions.TrimEntries));
                if (tail.Length > 0) return tail;
            }

            var trimmed = uri.TrimEnd('/');
            var sep = System.Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf(':'));
            return sep >= 0 ? trimmed[(sep + 1)..] : trimmed;
        }
    }
}
