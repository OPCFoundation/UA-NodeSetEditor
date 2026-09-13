using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class UserPreferences
    {
        [JsonPropertyName("selectedServer")]
        public string? SelectedServer { get; set; }

        /// <summary>
        /// "light" or "dark". Optional on PUT — only updates the stored
        /// theme when present.
        /// </summary>
        [JsonPropertyName("themeMode")]
        public string? ThemeMode { get; set; }

        /// <summary>
        /// The user's globally-unique display name (shown to other users).
        /// Optional on PUT — only updates the stored name when present; the
        /// server rejects a value already taken by another user.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Default domain used to seed newly created namespace URIs.
        /// Optional on PUT — only updates the stored domain when present.
        /// </summary>
        [JsonPropertyName("defaultDomain")]
        public string? DefaultDomain { get; set; }

        /// <summary>
        /// Default license identifier (SPDX id, or a custom id when "Other / Proprietary").
        /// Prefilled into the create-model dialog. Optional on PUT — only updates when present.
        /// </summary>
        [JsonPropertyName("defaultLicense")]
        public string? DefaultLicense { get; set; }

        /// <summary>
        /// Reference URL for <see cref="DefaultLicense"/> when it is a custom license.
        /// Optional on PUT — only updates when present.
        /// </summary>
        [JsonPropertyName("defaultLicenseUrl")]
        public string? DefaultLicenseUrl { get; set; }

        /// <summary>
        /// Default copyright holder prefilled into the create-model dialog.
        /// Optional on PUT — only updates when present.
        /// </summary>
        [JsonPropertyName("defaultCopyrightHolder")]
        public string? DefaultCopyrightHolder { get; set; }

        /// <summary>True when the user has accepted the Terms of Use. Read-only on PUT.</summary>
        [JsonPropertyName("termsAccepted")]
        public bool? TermsAccepted { get; set; }

        /// <summary>
        /// True when the user may use beta features — currently the JSON, JSON-LD and archive
        /// download formats. Derived from the server's allow-list, so read-only on PUT.
        /// </summary>
        [JsonPropertyName("betaTester")]
        public bool? BetaTester { get; set; }
    }
}
