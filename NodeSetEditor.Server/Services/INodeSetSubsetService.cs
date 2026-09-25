namespace NodeSetEditor.Server.Services
{
    /// <summary>Who exported a subset and what it was trimmed for; recorded in its Extensions.</summary>
    public sealed record SubsetProvenance(
        string? UserName,
        string? UserEmail,
        string TargetModelUri,
        DateTime ExportedUtc);

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
        /// <item>Exception for <b>top-level nodes</b> (no ParentNodeId): the far end of an inverse
        /// hierarchical reference IS included when it belongs to another model. Such a node is
        /// anchored into the address space only by that reference — a vendor object organised under
        /// Core's Objects folder, for instance — so dropping the anchor would leave it nowhere.</item>
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
