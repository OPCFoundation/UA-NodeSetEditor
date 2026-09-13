using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class MethodDescription
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("browseName")]
        public string? BrowseName { get; set; }

        [JsonPropertyName("displayName")]
        public LocalizedText DisplayName { get; set; } = new LocalizedText();

        [JsonPropertyName("inputArguments")]
        public List<MethodArgument>? InputArguments { get; set; }

        [JsonPropertyName("outputArguments")]
        public List<MethodArgument>? OutputArguments { get; set; }

        [JsonPropertyName("executable")]
        public bool? Executable { get; set; }

        [JsonPropertyName("userExecutable")]
        public bool? UserExecutable { get; set; }
    }
}
