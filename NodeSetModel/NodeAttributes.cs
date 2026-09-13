using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NodeSetEditor.Model
{
    public class LocalizedTextEntry
    {
        public string? Locale { get; set; }
        public string? Value { get; set; }
    }

    public class FieldDefinitionEntry
    {
        public string? Name { get; set; }
        public string? DataType { get; set; }
        public int? ValueRank { get; set; }
        public string? ArrayDimensions { get; set; }
        public int? Value { get; set; }
        public bool? IsOptional { get; set; }
        public bool? AllowSubTypes { get; set; }
        public string? SymbolicName { get; set; }
        public List<LocalizedTextEntry>? Description { get; set; }
    }

    public class DataTypeDefinitionEntry
    {
        public string? Name { get; set; }
        public string? SymbolicName { get; set; }
        public bool? IsUnion { get; set; }
        public bool? IsOptionSet { get; set; }
        public string? BaseType { get; set; }
        public List<FieldDefinitionEntry>? Fields { get; set; }
    }

    public class RolePermissionEntry
    {
        public string? Value { get; set; }
        public uint? Permissions { get; set; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "NodeType")]
    [JsonDerivedType(typeof(ObjectTypeAttributes), "ObjectType")]
    [JsonDerivedType(typeof(VariableTypeAttributes), "VariableType")]
    [JsonDerivedType(typeof(ReferenceTypeAttributes), "ReferenceType")]
    [JsonDerivedType(typeof(DataTypeAttributes), "DataType")]
    [JsonDerivedType(typeof(ObjectAttributes), "Object")]
    [JsonDerivedType(typeof(VariableAttributes), "Variable")]
    [JsonDerivedType(typeof(MethodAttributes), "Method")]
    [JsonDerivedType(typeof(ViewAttributes), "View")]
    public class NodeAttributesBase
    {
        public List<LocalizedTextEntry>? DisplayName { get; set; }
        public List<LocalizedTextEntry>? Description { get; set; }
        public uint? WriteMask { get; set; }
        public uint? UserWriteMask { get; set; }
        public ushort? AccessRestrictions { get; set; }
        public string? SymbolicName { get; set; }
        public string? ReleaseStatus { get; set; }
        public string[]? Category { get; set; }
        public string? Documentation { get; set; }
        public List<RolePermissionEntry>? RolePermissions { get; set; }
    }

    public class ObjectTypeAttributes : NodeAttributesBase
    {
        public bool? IsAbstract { get; set; }
    }

    public class VariableTypeAttributes : NodeAttributesBase
    {
        public bool? IsAbstract { get; set; }
        public JsonNode? Value { get; set; }
        public string? DataType { get; set; }
        public int? ValueRank { get; set; }
        public string? ArrayDimensions { get; set; }
    }

    public class ReferenceTypeAttributes : NodeAttributesBase
    {
        public bool? IsAbstract { get; set; }
        public List<LocalizedTextEntry>? InverseName { get; set; }
        public bool? Symmetric { get; set; }
    }

    public class DataTypeAttributes : NodeAttributesBase
    {
        public bool? IsAbstract { get; set; }
        public DataTypeDefinitionEntry? Definition { get; set; }
        public string? Purpose { get; set; }
    }

    public class ObjectAttributes : NodeAttributesBase
    {
        public byte? EventNotifier { get; set; }

        /// <summary>
        /// Marks a top-level Object that exists only in the design tool (no parent,
        /// no children, no references — invisible in the address space). Settable
        /// only at creation; skips instantiation rules. See UAInstance.DesignToolOnly.
        /// </summary>
        public bool? DesignToolOnly { get; set; }
    }

    public class VariableAttributes : NodeAttributesBase
    {
        public JsonNode? Value { get; set; }
        public string? DataType { get; set; }
        public int? ValueRank { get; set; }
        public string? ArrayDimensions { get; set; }
        public uint? AccessLevel { get; set; }
        public uint? UserAccessLevel { get; set; }
        public double? MinimumSamplingInterval { get; set; }
        public bool? Historizing { get; set; }

        /// <summary>
        /// Marks a top-level Variable that exists only in the design tool. Top-level
        /// Variables are always design-tool-only. See UAInstance.DesignToolOnly.
        /// </summary>
        public bool? DesignToolOnly { get; set; }
    }

    public class MethodAttributes : NodeAttributesBase
    {
        public bool? Executable { get; set; }
        public bool? UserExecutable { get; set; }
        public string? MethodDeclarationId { get; set; }
    }

    public class ViewAttributes : NodeAttributesBase
    {
        public bool? ContainsNoLoops { get; set; }
        public byte? EventNotifier { get; set; }
    }
}
