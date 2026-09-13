using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class LocalizedText
    {
        [JsonPropertyName("locale")]
        public string? Locale { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
