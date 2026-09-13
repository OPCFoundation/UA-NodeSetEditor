namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Naming helpers for OPC UA namespaces. Centralized so every path that derives a display
    /// name from a namespace URI (dependency import, Cloud Library browse, Cloud Library import)
    /// stays consistent — the Cloud Library can publish one namespace under several submission
    /// titles, so a URI-derived name is the reliable fallback when titles conflict.
    /// </summary>
    public static class NamespaceNaming
    {
        /// <summary>
        /// Display name from a namespace URI's last path/colon segment
        /// (e.g. "http://opcfoundation.org/UA/PADIM/" → "PADIM",
        /// "http://opcfoundation.org/UA/Dictionary/IRDI" → "IRDI").
        /// </summary>
        public static string DeriveFromUri(string uri)
        {
            var trimmed = uri.TrimEnd('/');
            var sep = System.Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf(':'));
            return sep >= 0 ? trimmed[(sep + 1)..] : trimmed;
        }
    }
}
