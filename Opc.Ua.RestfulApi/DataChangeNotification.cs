using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class DataChangeNotification
    {
        [JsonPropertyName("monitoredItemId")]
        public long? MonitoredItemId { get; set; }

        [JsonPropertyName("nodeId")]
        public string? NodeId { get; set; }

        [JsonPropertyName("value")]
        public DataValue? Value { get; set; }

        [JsonPropertyName("sequenceNumber")]
        public int? SequenceNumber { get; set; }
    }
}
