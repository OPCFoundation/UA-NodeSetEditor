using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class ServerConfiguration
    {
        [JsonPropertyName("endpointUrl")]
        public string? EndpointUrl { get; set; }

        [JsonPropertyName("transportProfileUri")]
        public string? TransportProfileUri { get; set; }

        [JsonPropertyName("securityMode")]
        public string? SecurityMode { get; set; }

        [JsonPropertyName("securityPolicyUri")]
        public string? SecurityPolicyUri { get; set; }
    }
}
