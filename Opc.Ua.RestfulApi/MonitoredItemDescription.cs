using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MonitoringMode
    {
        Disabled,
        Sampling,
        Reporting
    }

    public class MonitoredItemDescription
    {
        [JsonPropertyName("monitoredItemId")]
        public long MonitoredItemId { get; set; }

        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("attributeId")]
        public int? AttributeId { get; set; }

        [JsonPropertyName("monitoringMode")]
        public MonitoringMode? MonitoringMode { get; set; }

        [JsonPropertyName("samplingInterval")]
        public double? SamplingInterval { get; set; }

        [JsonPropertyName("queueSize")]
        public int? QueueSize { get; set; }

        [JsonPropertyName("discardOldest")]
        public bool? DiscardOldest { get; set; }
    }
}
