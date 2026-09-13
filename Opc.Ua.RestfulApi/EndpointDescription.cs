using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MessageSecurityMode
    {
        None,
        Sign,
        SignAndEncrypt
    }

    public class EndpointDescription
    {
        [JsonPropertyName("endpointUrl")]
        public string? EndpointUrl { get; set; }

        [JsonPropertyName("transportProfileUri")]
        public string? TransportProfileUri { get; set; }

        [JsonPropertyName("endpointProfileUris")]
        public List<string>? EndpointProfileUris { get; set; }

        [JsonPropertyName("securityMode")]
        public MessageSecurityMode? SecurityMode { get; set; }

        [JsonPropertyName("securityPolicyUri")]
        public string? SecurityPolicyUri { get; set; }

        [JsonPropertyName("securityLevel")]
        public int? SecurityLevel { get; set; }
    }
}
