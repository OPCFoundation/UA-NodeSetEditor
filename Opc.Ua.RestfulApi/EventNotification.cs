using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class EventNotification
    {
        [JsonPropertyName("monitoredItemId")]
        public long? MonitoredItemId { get; set; }

        [JsonPropertyName("nodeId")]
        public string? NodeId { get; set; }

        [JsonPropertyName("fields")]
        public Dictionary<string, object?>? Fields { get; set; }

        [JsonPropertyName("sequenceNumber")]
        public int? SequenceNumber { get; set; }
    }
}
