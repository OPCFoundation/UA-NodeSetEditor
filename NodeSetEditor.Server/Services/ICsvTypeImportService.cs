extern alias JsonNodeSet;

using JsonNodeSet::Opc.Ua.NodeSetSerializer;
using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Encodes a JSON value as a <c>Variant</c> for a variable of the given DataType.
    /// Supplied by the controller so the import shares the one encoder the node API
    /// already uses (<c>UaRestApiController.JsonElementToVariant</c>) instead of
    /// growing a second, drifting copy.
    /// </summary>
    public delegate JsonNodeSet::Opc.Ua.NodeSetSerializer.Model.Variant CsvVariantEncoder(
        System.Text.Json.JsonElement value, string? dataTypeNodeId);

    /// <summary>The nodes an import would create, before anything is written.</summary>
    public sealed class CsvBuildResult
    {
        /// <summary>
        /// Every node to create, in dependency order: the folder, the type and its
        /// declarations, then the Object and its children. A caller can walk this once,
        /// adding each node to the address space, because a node's parent always precedes it.
        /// </summary>
        public List<JsonNodeSet::Opc.Ua.NodeSetSerializer.Model.UANode> Nodes { get; } = new();

        /// <summary>The Object the rows hang under. Always set on success.</summary>
        public JsonNodeSet::Opc.Ua.NodeSetSerializer.Model.UANode? InstanceNode { get; set; }

        /// <summary>The new ObjectType, when the request asked for one.</summary>
        public JsonNodeSet::Opc.Ua.NodeSetSerializer.Model.UANode? TypeNode { get; set; }

        /// <summary>The new folder, when the request asked for one.</summary>
        public JsonNodeSet::Opc.Ua.NodeSetSerializer.Model.UANode? FolderNode { get; set; }

        public int VariablesCreated { get; set; }
        public int PropertiesCreated { get; set; }
        public int RowsSkipped { get; set; }
        public List<string> Warnings { get; } = new();
    }

    public interface ICsvTypeImportService
    {
        /// <summary>Parses a CSV stream into headers and rows.</summary>
        CsvTable Parse(Stream csv);

        /// <summary>
        /// Proposes a column mapping and a type name for an uploaded CSV. Read-only: it
        /// creates nothing, and the workspace's address space is consulted only to resolve
        /// DataType names.
        /// </summary>
        CsvAnalysisResult Analyze(CsvTable table, string? fileName, AddressSpace addressSpace);

        /// <summary>
        /// Builds the node graph for a confirmed mapping. Throws
        /// <see cref="InvalidOperationException"/> for a mapping that cannot produce a
        /// valid type (no BrowseName column, a supertype that isn't an ObjectType, …);
        /// anything recoverable becomes a warning instead.
        /// </summary>
        CsvBuildResult Build(
            CsvTable table,
            CsvCreateTypeRequest request,
            AddressSpace addressSpace,
            Func<string, string> allocateNodeId,
            CsvVariantEncoder encodeValue);
    }
}
