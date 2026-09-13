using System.Text.Json.Serialization;

namespace NodeSetEditor.Server.Model
{
    public class TypeDefinition
    {
        public string? NodeId { get; set; }

        public NodeClass? NodeClass { get; set; }

        public string? BrowseName { get; set; }

        public string? DisplayName { get; set; }

        public string? Description { get; set; }

        public string? SuperTypeId { get; set; }

        public List<ChildDefinition>? Children { get; set; }

        public DataTypeDefinition? DataTypeDefinition { get; set; }
    }

    public class FieldDefinition
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? DataTypeId { get; set; }

        public int? Value { get; set; }

        public int? ValueRank { get; set; }

        public string? ArrayDimensions { get; set; }
    }

    public class DataTypeDefinition
    {
        public string? Name { get; set; }

        public List<FieldDefinition>? Fields { get; set; }
    }

    public class ChildDefinition
    {
        public string? ReferenceTypeId { get; set; }

        public string? NodeId { get; set; }

        public NodeClass? NodeClass { get; set; }

        public string? BrowseName { get; set; }

        public string? DisplayName { get; set; }

        public string? Description { get; set; }

        public string? TypeDefinitionId { get; set; }

        public string? ModellingRule { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum NodeClass
    {
        Unknown = 0,
        ObjectType = 1,
        VariableType = 2,
        ReferenceType = 3,
        DataType = 4,
        Object = 5,
        Variable = 6,
        Method = 7,
        View = 8
    }
}
