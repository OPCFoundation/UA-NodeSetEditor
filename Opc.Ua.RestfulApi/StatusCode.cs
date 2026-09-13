using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class StatusCode
    {
        [JsonPropertyName("code")]
        public long Code { get; set; }

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;
    }
}
