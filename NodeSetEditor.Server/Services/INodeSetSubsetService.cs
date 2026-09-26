namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Who exported a subset, from where, and what it was trimmed for; recorded in its Extensions.
    /// </summary>
    /// <param name="ExportedBy">Account name of the exporting user, not their email address.</param>
    /// <param name="Workspace">Name of the workspace the export was taken from.</param>
    public sealed record SubsetProvenance(
        string? ExportedBy,
        string? Workspace,
        string TargetModelUri,
        DateTime ExportTime);

    /// <summary>
    /// Produces trimmed dependency NodeSets for the "Remove unused nodes" download option: each
    /// dependency keeps only the nodes the model being downloaded actually reaches.
    /// </summary>
    public interface INodeSetSubsetService
    {
        /// <summary>
        /// Node ids reachable from every node of <paramref name="primaryModelId"/>, walking into
        /// the given dependency models. The result spans all of them; intersect it with one
        /// model's nodes to get that model's subset.
        ///
        /// <para>References are followed by kind rather than by a fixed list of types:</para>
        /// <list type="bullet">
        /// <item><b>Non-hierarchical</b> (everything under NonHierarchicalReferences — HasTypeDefinition,
        /// HasModellingRule, HasInterface, GeneratesEvent, FromState/ToState/HasCause/HasEffect, and any
        /// a model defines itself): followed FORWARD only. An inverse one is never walked; it survives
        /// in the output only if its target is already in the set, and is dropped otherwise.</item>
        /// <item><b>HasChild</b> and its subtypes: BOTH directions — a type is incomplete without its
        /// instance declarations, and a node whose parent is absent would be an orphan.</item>
        /// <item><b>HasSubtype</b>: from a subtype up to its supertype only, so selecting a type never
        /// drags in everything derived from it.</item>
        /// <item>Other hierarchical references (Organizes and the like) are not followed; they are kept
        /// when both ends are in the set and dropped when they are not.</item>
        /// <item><b>Cross-model mounting points</b>: when a node is hierarchically contained by a
        /// node in ANOTHER model — a vendor object under Core's Objects folder, a device under DI's
        /// DeviceSet, a NamespaceMetadata object under Server's Namespaces folder — that container
        /// is included as a <b>placeholder</b>: the node itself and nothing below it. Dropping it
        /// would leave the reference naming it dangling; walking it would pull in a whole
        /// well-known structure the model never asked for. A placeholder later reached on its own
        /// merit is promoted to a full walk.</item>
        /// </list>
        /// <para>Every DataType a node names is also included — the DataType attribute,
        /// StructureDefinition fields, and Method InputArguments/OutputArguments — as is the
        /// ReferenceType of every reference an included node carries.</para>
        /// </summary>
        Task<IReadOnlySet<string>> ComputeDependencyClosureAsync(
            Guid primaryModelId, IReadOnlyCollection<Guid> dependencyModelIds);

        /// <summary>
        /// Renders one dependency as a NodeSet holding only <paramref name="includeNodeIds"/>,
        /// with its NamespaceMetadata object marked <c>IsNamespaceSubset = true</c> and
        /// <paramref name="provenance"/> recorded in the document's Extensions. Null when the
        /// model row does not exist.
        /// </summary>
        Task<byte[]?> ExportTrimmedAsync(
            Guid modelId, IReadOnlySet<string> includeNodeIds, SubsetProvenance provenance);
    }
}
