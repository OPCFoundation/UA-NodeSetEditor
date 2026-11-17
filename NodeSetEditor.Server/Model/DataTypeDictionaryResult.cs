using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// What regenerating a model's legacy DataTypeDictionaries (the BSD and XSD blobs, their
    /// DataTypeDescriptions and the Default Binary / Default XML encodings) changed.
    /// </summary>
    public class DataTypeDictionaryResult
    {
        /// <summary>False when the model has no dictionaries and none were asked for.</summary>
        [JsonPropertyName("hasDictionaries")]
        public bool HasDictionaries { get; set; }

        [JsonPropertyName("dictionariesCreated")]
        public int DictionariesCreated { get; set; }

        /// <summary>Dictionaries whose blob was rewritten because the DataTypes changed.</summary>
        [JsonPropertyName("dictionariesUpdated")]
        public int DictionariesUpdated { get; set; }

        [JsonPropertyName("descriptionsCreated")]
        public int DescriptionsCreated { get; set; }

        [JsonPropertyName("descriptionsUpdated")]
        public int DescriptionsUpdated { get; set; }

        [JsonPropertyName("descriptionsDeleted")]
        public int DescriptionsDeleted { get; set; }

        [JsonPropertyName("encodingsCreated")]
        public int EncodingsCreated { get; set; }

        [JsonPropertyName("encodingsDeleted")]
        public int EncodingsDeleted { get; set; }

        /// <summary>Problems met while generating, e.g. a field type that could not be resolved.</summary>
        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new();
    }
}
