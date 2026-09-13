using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class ViewDescription
    {
        [JsonPropertyName("nodeId")]
        public string? NodeId { get; set; }

        [JsonPropertyName("browseName")]
        public string? BrowseName { get; set; }

        [JsonPropertyName("displayName")]
        public LocalizedText? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }

        [JsonPropertyName("containsNoLoops")]
        public bool? ContainsNoLoops { get; set; }

        [JsonPropertyName("eventNotifier")]
        public int? EventNotifier { get; set; }

        [JsonPropertyName("viewVersion")]
        public int? ViewVersion { get; set; }
    }
}
