using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    /// <summary>
    /// License/copyright auto-detected from an uploaded NodeSet file (embedded SPDX headers, or
    /// OPC Foundation defaults for foundation namespaces). Returned by the detect endpoint so the
    /// import dialog can pre-fill the values for the user to confirm or override. Any field may be
    /// null when nothing could be detected.
    /// </summary>
    public class LicenseDetectionResult
    {
        /// <summary>Detected SPDX license id (or custom "LicenseRef-…"), or null.</summary>
        [JsonPropertyName("license")]
        public string? License { get; set; }

        /// <summary>Detected/resolved license reference URL, or null.</summary>
        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; set; }

        /// <summary>Detected copyright holder, or null.</summary>
        [JsonPropertyName("copyrightHolder")]
        public string? CopyrightHolder { get; set; }
    }
}
