using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class Namespace
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;
    }
}
