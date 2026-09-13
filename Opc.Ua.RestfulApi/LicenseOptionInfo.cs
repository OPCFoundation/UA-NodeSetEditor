using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    /// <summary>
    /// A selectable license, served from the catalog to drive the data-driven
    /// license selector in the client.
    /// </summary>
    public class LicenseOptionInfo
    {
        /// <summary>SPDX identifier, e.g. "MIT" or "LicenseRef-Proprietary".</summary>
        [JsonPropertyName("spdxId")]
        public string SpdxId { get; set; } = null!;

        /// <summary>Human-readable license name shown in the selector.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        /// <summary>Canonical reference URL for the license text. Null for the custom entry.</summary>
        [JsonPropertyName("referenceUrl")]
        public string? ReferenceUrl { get; set; }

        /// <summary>
        /// True for the "Other / Proprietary" entry. When selected the client must
        /// collect a custom identifier and a valid reference URL.
        /// </summary>
        [JsonPropertyName("isCustom")]
        public bool IsCustom { get; set; }

        /// <summary>Display order in the selector (ascending).</summary>
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }
    }
}
