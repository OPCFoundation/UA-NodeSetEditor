using System;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ServerState
    {
        Running,
        Failed,
        NoConfiguration,
        Suspended,
        Shutdown,
        Test,
        CommunicationFault,
        Unknown
    }

    public class ServerStatus
    {
        [JsonPropertyName("serverState")]
        public ServerState? ServerState { get; set; }

        [JsonPropertyName("productUri")]
        public string? ProductUri { get; set; }

        [JsonPropertyName("manufacturerName")]
        public string? ManufacturerName { get; set; }

        [JsonPropertyName("productName")]
        public string? ProductName { get; set; }

        [JsonPropertyName("softwareVersion")]
        public string? SoftwareVersion { get; set; }

        [JsonPropertyName("buildNumber")]
        public string? BuildNumber { get; set; }

        [JsonPropertyName("buildDate")]
        public DateTime? BuildDate { get; set; }

        [JsonPropertyName("startTime")]
        public DateTime? StartTime { get; set; }

        [JsonPropertyName("currentTime")]
        public DateTime? CurrentTime { get; set; }

        [JsonPropertyName("serviceLevel")]
        public int? ServiceLevel { get; set; }
    }
}
