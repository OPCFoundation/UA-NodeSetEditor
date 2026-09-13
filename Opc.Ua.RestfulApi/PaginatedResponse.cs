using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class PaginatedResponse<T>
    {
        [JsonPropertyName("results")]
        public List<T> Results { get; set; } = new List<T>();

        [JsonPropertyName("totalCount")]
        public int? TotalCount { get; set; }
    }
}
