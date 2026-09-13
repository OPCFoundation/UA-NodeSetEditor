using System;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class ErrorResponse
    {
        [JsonPropertyName("statusCode")]
        public StatusCode StatusCode { get; set; } = new StatusCode();

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTime? Timestamp { get; set; }
    }
}
