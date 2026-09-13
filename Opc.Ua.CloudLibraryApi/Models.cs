using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opc.Ua.CloudLibraryApi
{
    public class UANameSpace
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("license")]
        public string? License { get; set; }

        [JsonPropertyName("copyrightText")]
        public string? CopyrightText { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("documentationUrl")]
        public string? DocumentationUrl { get; set; }

        [JsonPropertyName("iconUrl")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; set; }

        [JsonPropertyName("purchasingInformationUrl")]
        public string? PurchasingInformationUrl { get; set; }

        [JsonPropertyName("releaseNotesUrl")]
        public string? ReleaseNotesUrl { get; set; }

        [JsonPropertyName("testSpecificationUrl")]
        public string? TestSpecificationUrl { get; set; }

        [JsonPropertyName("keywords")]
        public string[]? Keywords { get; set; }

        [JsonPropertyName("supportedLocales")]
        public string[]? SupportedLocales { get; set; }

        [JsonPropertyName("nodeset")]
        public Nodeset? Nodeset { get; set; }

        [JsonPropertyName("creationTime")]
        public DateTime? CreationTime { get; set; }

        [JsonPropertyName("numberOfDownloads")]
        public int? NumberOfDownloads { get; set; }
    }

    public class UANodesetResult : UANameSpace
    {
        [JsonPropertyName("nodesetId")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? NodesetId { get; set; }

        [JsonPropertyName("nodesetTitle")]
        public string? NodesetTitle { get; set; }

        [JsonPropertyName("orgName")]
        public string? OrgName { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("publicationDate")]
        public DateTime? PublicationDate { get; set; }

        [JsonPropertyName("nodesetNamespaceUri")]
        public string? NodesetNamespaceUri { get; set; }

        [JsonPropertyName("requiredNodesets")]
        public RequiredModelInfo[]? RequiredNodesets { get; set; }
    }

    public class Nodeset
    {
        [JsonPropertyName("nodesetXml")]
        public string? NodesetXml { get; set; }

        [JsonPropertyName("identifier")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? Identifier { get; set; }

        [JsonPropertyName("namespaceUri")]
        public string? NamespaceUri { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("publicationDate")]
        public DateTime? PublicationDate { get; set; }

        [JsonPropertyName("lastModifiedDate")]
        public DateTime? LastModifiedDate { get; set; }

        [JsonPropertyName("requiredModels")]
        public RequiredModelInfo[]? RequiredModels { get; set; }
    }

    public class RequiredModelInfo
    {
        [JsonPropertyName("namespaceUri")]
        public string? NamespaceUri { get; set; }

        [JsonPropertyName("publicationDate")]
        public DateTime? PublicationDate { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("availableModel")]
        public Nodeset? AvailableModel { get; set; }
    }

    /// <summary>
    /// Reads a JSON value as a string regardless of whether the token is a string, number, or boolean.
    /// </summary>
    public class FlexibleStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String: return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var l)) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // InvariantCulture: this string goes back out as JSON/NodeSet data, and the
                    // current culture would render 1.5 as "1,5" on most of Europe.
                    if (reader.TryGetDouble(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return null;
                case JsonTokenType.True: return "true";
                case JsonTokenType.False: return "false";
                case JsonTokenType.Null: return null;
                default: return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }
}
