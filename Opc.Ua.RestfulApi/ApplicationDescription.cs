using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class ApplicationDescription
    {
        [JsonPropertyName("applicationUri")]
        public string? ApplicationUri { get; set; }

        [JsonPropertyName("productUri")]
        public string? ProductUri { get; set; }

        [JsonPropertyName("applicationName")]
        public LocalizedText? ApplicationName { get; set; }

        [JsonPropertyName("discoveryUrls")]
        public List<string>? DiscoveryUrls { get; set; }

        [JsonPropertyName("isDefault")]
        public bool? IsDefault { get; set; }
    }
}
