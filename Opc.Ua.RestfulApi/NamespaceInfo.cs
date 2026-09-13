using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum IdType
    {
        Numeric,
        String,
        Guid,
        Opaque
    }

    public class NamespaceInfo : Namespace
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("publicationDate")]
        public DateTime? PublicationDate { get; set; }

        [JsonPropertyName("description")]
        public LocalizedText? Description { get; set; }

        [JsonPropertyName("isNamespaceSubset")]
        public bool? IsNamespaceSubset { get; set; }

        [JsonPropertyName("staticNodeIdTypes")]
        public List<IdType>? StaticNodeIdTypes { get; set; }

        [JsonPropertyName("staticNumericNodeIdRange")]
        public List<string>? StaticNumericNodeIdRange { get; set; }

        [JsonPropertyName("staticStringNodeIdPattern")]
        public string? StaticStringNodeIdPattern { get; set; }
    }
}
