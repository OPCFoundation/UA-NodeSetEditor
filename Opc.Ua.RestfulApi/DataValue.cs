using System;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class DataValue
    {
        [JsonPropertyName("value")]
        public object? Value { get; set; }

        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonPropertyName("statusCode")]
        public StatusCode? StatusCode { get; set; }

        [JsonPropertyName("sourceTimestamp")]
        public DateTime? SourceTimestamp { get; set; }

        [JsonPropertyName("serverTimestamp")]
        public DateTime? ServerTimestamp { get; set; }

        [JsonPropertyName("sourcePicoseconds")]
        public int? SourcePicoseconds { get; set; }

        [JsonPropertyName("serverPicoseconds")]
        public int? ServerPicoseconds { get; set; }
    }
}
