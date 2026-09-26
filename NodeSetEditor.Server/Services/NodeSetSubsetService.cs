using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>EF Core-backed implementation of <see cref="INodeSetSubsetService"/>.</summary>
    public class NodeSetSubsetService : INodeSetSubsetService
    {
        // Well-known reference types the closure walks. The concrete types stored on a node are
        // subtypes of these (HasComponent/HasProperty under HasChild, and a model may define its
        // own), so the subtype sets come from SubTypeHierarchy rather than being hardcoded.
        private const string HasChildId = "i=34";
        private const string HasSubtypeId = "i=45";
        private const string HasTypeDefinitionId = "i=40";

        /// <summary>
        /// Every non-hierarchical reference type derives from this one, so following the family
        /// covers HasTypeDefinition, HasModellingRule, HasInterface, HasEncoding, GeneratesEvent,
        /// the state-machine references (FromState / ToState / HasCause / HasEffect) and anything
        /// a model defines itself — no list to keep in step with the spec.
        /// </summary>
        private const string NonHierarchicalReferencesId = "i=32";

        /// <summary>
        /// Root of the hierarchical reference types (HasChild, HasSubtype, Organizes, …). Used to
        /// find what hierarchically contains a node, for the cross-model placeholder rule below.
        /// </summary>
        private const string HierarchicalReferencesId = "i=33";

        private const string NamespaceMetadataTypeId = "i=11616";

        /// <summary>
        /// The Part 3 type-dictionary machinery. A DataType points at its DataTypeEncoding objects
        /// (Default Binary / Default XML), each of which points by HasDescription at a
        /// DataTypeDescription variable living inside a DataTypeDictionary — and that dictionary
        /// owns a description for every type in the namespace, plus the whole schema as one base64
        /// Value. Following HasDescription therefore turns two DataTypes into ~390 nodes and most
        /// of a megabyte. The dictionaries are deprecated; the encodings are what a reader needs,
        /// so they are all that is kept.
        /// </summary>
        private const string HasDescriptionId = "i=39";
        private const string DataTypeEncodingTypeId = "i=76";
        private const string DataTypeDictionaryTypeId = "i=72";
        private const string DataTypeDescriptionTypeId = "i=69";

        /// <summary>
        /// The JSON encoding carries no information the DataTypeDefinition does not already give,
        /// and nothing here emits JSON-encoded values, so it never travels with a subset.
        /// </summary>
        private const string DefaultJsonBrowseName = "Default JSON";

        /// <summary>Part 6 type namespace, for the Boolean value written into IsNamespaceSubset.</summary>
        private const string UaTypesNamespace = "http://opcfoundation.org/UA/2008/02/Types.xsd";

        /// <summary>Namespace of the Extensions element this app writes onto a subset.</summary>
        private const string SubsetExtensionNamespace = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd/extensions";

        private readonly NodeSetEditorDbContext _db;

        public NodeSetSubsetService(NodeSetEditorDbContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlySet<string>> ComputeDependencyClosureAsync(
            Guid primaryModelId, IReadOnlyCollection<Guid> dependencyModelIds)
        {
            var allModelIds = new List<Guid>(dependencyModelIds) { primaryModelId };

            var nodes = await _db.Nodes.AsNoTracking()
                .Where(n => allModelIds.Contains(n.ModelId))
                .ToListAsync();
            var references = await _db.References.AsNoTracking()
                .Where(r => allModelIds.Contains(r.ModelId))
                .ToListAsync();

            // Node ids are namespace-qualified ("nsu=uri;i=5", or bare "i=5" for Core), so one
            // dictionary spans every model in the bundle without collisions.
            var byNodeId = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var node in nodes)
                if (node.NodeId != null) byNodeId[node.NodeId] = node;

            var families = await ReferenceTypeFamiliesAsync(
                [HasChildId, HasSubtypeId, NonHierarchicalReferencesId, HierarchicalReferencesId]);

            var hasChild = families[HasChildId];
            var hasSubtype = families[HasSubtypeId];
            // HasSubtype IS a subtype of HasChild, but the two are walked differently — a subtype
            // reaches its supertype and stops, while HasChild is walked in both directions. Take
            // the HasSubtype family back out so the HasChild rule can't drag subtypes in.
            hasChild.ExceptWith(hasSubtype);

            var nonHierarchical = families[NonHierarchicalReferencesId];
            var hierarchical = families[HierarchicalReferencesId];

            var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var referenceTypesBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // What hierarchically contains each node: the far end of its INVERSE hierarchical
            // references, plus its ParentNodeId. Drives the cross-model placeholder rule below.
            var containers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var r in references)
            {
                if (r.SourceNodeId == null || r.TargetNodeId == null || r.ReferenceTypeId == null) continue;

                // A reference that gets emitted needs its ReferenceType defined.
                Add(referenceTypesBySource, r.SourceNodeId, r.ReferenceTypeId);

                // A reference row is stored on one side with IsForward saying which way it runs.
                var (from, to) = r.IsForward ? (r.SourceNodeId, r.TargetNodeId) : (r.TargetNodeId, r.SourceNodeId);

                // Non-hierarchical references are followed FORWARD only — the source names
                // something it needs. The inverse direction is not walked: an inverse reference
                // simply survives in the output if its target is already in the set, and is
                // dropped if it is not (the export applies that to every reference kind).
                //
                // HasDescription is the exception: its target is a type-dictionary entry, which
                // the subset drops outright (see HasDescriptionId). The reference goes with it,
                // in StripTypeDictionaryArtifacts.
                if (nonHierarchical.Contains(r.ReferenceTypeId) && r.ReferenceTypeId != HasDescriptionId)
                    Add(outgoing, from, to);

                // Downward only. Climbing to a container is handled by the placeholder rule in the
                // walk below — never as an ordinary edge, or reaching one node inside a container
                // pulls in every other node the container owns.
                if (hasChild.Contains(r.ReferenceTypeId)) Add(outgoing, from, to);

                if (hasSubtype.Contains(r.ReferenceTypeId)) Add(outgoing, to, from);

                // `to` holds this reference inversely, so `from` is what contains it.
                if (hierarchical.Contains(r.ReferenceTypeId)) Add(containers, to, from);
            }

            // Containment is spelled two ways — an inverse HasChild reference, a ParentNodeId, or
            // both — and the rules have to agree whichever way a given NodeSet wrote it.
            foreach (var node in nodes)
            {
                if (node.NodeId == null || node.ParentNodeId == null) continue;
                Add(containers, node.NodeId, node.ParentNodeId);
                Add(outgoing, node.ParentNodeId, node.NodeId);
            }

            // ---- Seed: every node of the model being downloaded ----
            // It is kept whole; what the trim removes is everything in the DEPENDENCIES that
            // nothing here reaches.
            var included = new HashSet<string>(StringComparer.Ordinal);
            // Included but not walked: cross-model mounting points. Kept apart from `included` so
            // one can be promoted to a full walk if something reaches it for a real reason.
            var placeholders = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var node in nodes)
            {
                if (node.ModelId != primaryModelId || node.NodeId == null) continue;
                if (included.Add(node.NodeId)) queue.Enqueue(node.NodeId);
            }

            // Each dependency's own NamespaceMetadata object travels with it — the subset has to
            // carry the namespace's identity and its IsNamespaceSubset flag.
            foreach (var node in nodes)
            {
                if (node.NodeId != null && node.TypeDefinitionId == NamespaceMetadataTypeId
                    && included.Add(node.NodeId))
                {
                    queue.Enqueue(node.NodeId);
                }
            }

            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();

                if (outgoing.TryGetValue(nodeId, out var targets))
                    foreach (var target in targets) Enqueue(target);

                if (referenceTypesBySource.TryGetValue(nodeId, out var referenceTypes))
                    foreach (var referenceType in referenceTypes) Enqueue(referenceType);

                if (!byNodeId.TryGetValue(nodeId, out var node)) continue;

                Enqueue(node.SuperTypeId);
                Enqueue(node.TypeDefinitionId);
                Enqueue(node.ModellingRule);

                // Whatever contains this node has to exist, or the reference naming it dangles —
                // but it comes in as a PLACEHOLDER: the container itself and nothing below it.
                //
                // Expanding a container is what made a trimmed Core useless. These are well-known
                // objects near the root of big structures — Core's Objects folder, DI's DeviceSet,
                // Server's Namespaces folder — and descending into one brings everything it owns.
                // Core's own NamespaceMetadata object sits under Server's Namespaces folder, so
                // climbing-and-expanding from it reached Server, then ServerType, then every
                // diagnostics DataType behind that: thousands of nodes for a model that named
                // none of them. The container is where this node attaches, not a statement that
                // anything wants its contents.
                //
                // This holds WITHIN a model as much as across one. The dictionary case was the
                // same shape: one description entry reached its dictionary and the dictionary
                // brought back every other entry in the namespace.
                if (containers.TryGetValue(nodeId, out var anchors))
                {
                    foreach (var anchor in anchors) IncludeAsPlaceholder(anchor);
                }

                // The DataType attribute, StructureDefinition field DataTypes and the DataTypes of
                // Method Input/OutputArguments are all spelled "DataType" in the attributes JSON,
                // so one recursive sweep catches them.
                foreach (var dataTypeId in CollectDataTypeIds(node.Attributes)) Enqueue(dataTypeId);
            }

            return included;

            void Enqueue(string? candidate)
            {
                if (!Admissible(candidate)) return;

                // A node first met as a placeholder and later reached on its own merit is promoted:
                // it is in the set already, so Add says nothing, but it still has to be walked.
                if (placeholders.Remove(candidate!))
                {
                    queue.Enqueue(candidate!);
                    return;
                }

                if (included.Add(candidate!)) queue.Enqueue(candidate!);
            }

            // In the set, but never walked — see the container rule above. The candidate's own
            // container chain comes with it, also as placeholders, so the structure it hangs from
            // is complete all the way up and nothing names a node the file does not define.
            void IncludeAsPlaceholder(string? candidate)
            {
                var pending = new Queue<string>();
                if (Admissible(candidate)) pending.Enqueue(candidate!);

                while (pending.Count > 0)
                {
                    var id = pending.Dequeue();

                    // Already walked, or already a placeholder whose chain was added with it.
                    if (!included.Add(id)) continue;
                    placeholders.Add(id);

                    // A stub still has to be a legal node. An Object or Variable is the source of
                    // exactly one HasTypeDefinition (OPC 10000-3, 7.13), a type needs its
                    // supertype, an instance declaration its modelling rule. Each of those comes
                    // in as a placeholder as well — which is the whole point: ServerType arrives
                    // as ONE node, not as the tree that hangs off it.
                    if (byNodeId.TryGetValue(id, out var stub))
                    {
                        if (Admissible(stub.TypeDefinitionId)) pending.Enqueue(stub.TypeDefinitionId!);
                        if (Admissible(stub.SuperTypeId)) pending.Enqueue(stub.SuperTypeId!);
                        if (Admissible(stub.ModellingRule)) pending.Enqueue(stub.ModellingRule!);
                    }

                    if (!containers.TryGetValue(id, out var up)) continue;
                    foreach (var container in up)
                        if (Admissible(container)) pending.Enqueue(container);
                }
            }

            // In the bundle, and not one of the node kinds a subset never carries.
            bool Admissible(string? candidate)
            {
                if (candidate == null || !byNodeId.TryGetValue(candidate, out var node)) return false;
                return !IsTypeDictionaryArtifact(node) && !IsDefaultJsonEncoding(node);
            }

        }

        public async Task<byte[]?> ExportTrimmedAsync(
            Guid modelId, IReadOnlySet<string> includeNodeIds, SubsetProvenance provenance)
        {
            var model = await _db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId);
            if (model?.Uri == null) return null;

            var nodeSet = await NodeSetConverter.CreateNodeSetAsync(_db, model.Uri, model.Version, includeNodeIds);

            MarkAsNamespaceSubset(nodeSet);
            StripTypeDictionaryArtifacts(nodeSet);
            DropDanglingSelfReferences(nodeSet);
            AddSubsetExtension(nodeSet, provenance);

            using var raw = new MemoryStream();
            nodeSet.Write(raw);
            return SpdxHeaders.InjectIntoXml(
                raw.ToArray(), model.CopyrightHolder, model.License, model.LicenseUrl);
        }

        /// <summary>
        /// Sets IsNamespaceSubset = true on the NamespaceMetadata object's property of that name.
        /// The flag is what tells a consumer the file is not the whole namespace, so a subset
        /// that omitted it would misrepresent itself.
        /// </summary>
        private static void MarkAsNamespaceSubset(Opc.Ua.Export.UANodeSet nodeSet)
        {
            var items = nodeSet.Items ?? [];

            var metadataObjectIds = NamespaceMetadataObjects(nodeSet)
                .Select(i => i.NodeId)
                .Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);

            if (metadataObjectIds.Count == 0) return;

            foreach (var item in items.OfType<Opc.Ua.Export.UAVariable>())
            {
                if (item.ParentNodeId == null || !metadataObjectIds.Contains(item.ParentNodeId)) continue;
                if (LocalName(item.BrowseName) != "IsNamespaceSubset") continue;

                var document = new XmlDocument();
                var value = document.CreateElement("Boolean", UaTypesNamespace);
                value.InnerText = "true";
                item.Value = value;
            }
        }

        /// <summary>The objects whose HasTypeDefinition names NamespaceMetadataType.</summary>
        private static IEnumerable<Opc.Ua.Export.UANode> NamespaceMetadataObjects(
            Opc.Ua.Export.UANodeSet nodeSet)
        {
            return (nodeSet.Items ?? []).Where(i => HasTypeDefinition(i, NamespaceMetadataTypeId));
        }

        private static bool HasTypeDefinition(Opc.Ua.Export.UANode node, string typeId) =>
            node.References?.Any(r =>
                r.ReferenceType == HasTypeDefinitionId && r.IsForward && r.Value == typeId) == true;

        /// <summary>
        /// Drops the type-dictionary machinery from a subset: the DataTypeDictionary variables and
        /// their DataTypeDescription entries, the Default JSON encodings, and the HasDescription
        /// references that named the entries. What survives is the Default Binary / Default XML
        /// encoding objects, which is what a reader actually resolves an ExtensionObject TypeId
        /// against.
        ///
        /// <para>The closure already refuses to walk into any of this, so in practice the only
        /// thing left to do here is strip the HasDescription references — the export keeps a
        /// reference whose target was dropped, which is right for a cross-model link and wrong for
        /// a link into a dictionary that is deliberately gone.</para>
        /// </summary>
        private static void StripTypeDictionaryArtifacts(Opc.Ua.Export.UANodeSet nodeSet)
        {
            var items = nodeSet.Items ?? [];

            var dropped = items
                .Where(i => HasTypeDefinition(i, DataTypeDictionaryTypeId)
                         || HasTypeDefinition(i, DataTypeDescriptionTypeId)
                         || (HasTypeDefinition(i, DataTypeEncodingTypeId)
                             && LocalName(i.BrowseName) == DefaultJsonBrowseName))
                .Select(i => i.NodeId)
                .Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);

            var kept = items.Where(i => !dropped.Contains(i.NodeId)).ToArray();

            foreach (var item in kept)
            {
                if (item.References == null) continue;

                item.References = item.References
                    .Where(r => r.ReferenceType != HasDescriptionId && !dropped.Contains(r.Value))
                    .ToArray();
            }

            nodeSet.Items = kept;
        }

        /// <summary>
        /// Removes references — and ParentNodeIds — that name a node in THIS model's own
        /// namespace which the subset does not contain.
        ///
        /// <para>A reference whose target was trimmed is normally kept, because a NodeId pointing
        /// outside the file is how a NodeSet expresses a cross-model link. That reasoning stops at
        /// the model's own namespace: a file that names its own missing node is simply broken.
        /// Container placeholders make this concrete — a folder kept only so the thing hanging off
        /// it resolves still carries a forward reference to every child it owns, and those children
        /// are exactly what the trim removed.</para>
        /// </summary>
        private static void DropDanglingSelfReferences(Opc.Ua.Export.UANodeSet nodeSet)
        {
            var items = nodeSet.Items ?? [];
            var present = items.Select(i => i.NodeId).Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);

            // The model's own nodes are "ns=1;…" — the exporter always writes its URI first. Core
            // is the exception: it lists no NamespaceUris of its own, so its nodes are bare.
            var ownPrefix = nodeSet.NamespaceUris is { Length: > 0 } ? "ns=1;" : null;

            bool IsOwnAndMissing(string? nodeId)
            {
                if (nodeId == null || present.Contains(nodeId)) return false;
                return ownPrefix != null
                    ? nodeId.StartsWith(ownPrefix, StringComparison.Ordinal)
                    : !nodeId.StartsWith("ns=", StringComparison.Ordinal);
            }

            foreach (var item in items)
            {
                if (item is Opc.Ua.Export.UAInstance instance && IsOwnAndMissing(instance.ParentNodeId))
                    instance.ParentNodeId = null;

                if (item.References == null) continue;
                item.References = item.References.Where(r => !IsOwnAndMissing(r.Value)).ToArray();
            }
        }

        /// <summary>
        /// Records who produced the subset and what it was trimmed for, in the document's
        /// Extensions. This is how a reader tells a trimmed dependency from the real NodeSet.
        /// </summary>
        private static void AddSubsetExtension(Opc.Ua.Export.UANodeSet nodeSet, SubsetProvenance provenance)
        {
            var document = new XmlDocument();
            var element = document.CreateElement("NodeSetSubset", SubsetExtensionNamespace);
            element.SetAttribute("TargetModelUri", provenance.TargetModelUri);
            element.SetAttribute("ExportTime", provenance.ExportTime.ToUniversalTime().ToString("o"));
            if (!string.IsNullOrWhiteSpace(provenance.ExportedBy))
                element.SetAttribute("ExportedBy", provenance.ExportedBy);
            if (!string.IsNullOrWhiteSpace(provenance.Workspace))
                element.SetAttribute("Workspace", provenance.Workspace);

            nodeSet.Extensions = [.. nodeSet.Extensions ?? [], element];
        }

        /// <summary>A DataTypeDictionary variable, or one of its DataTypeDescription entries.</summary>
        private static bool IsTypeDictionaryArtifact(Node node) =>
            node.TypeDefinitionId is DataTypeDictionaryTypeId or DataTypeDescriptionTypeId;

        /// <summary>The "Default JSON" DataTypeEncoding object.</summary>
        private static bool IsDefaultJsonEncoding(Node node) =>
            node.TypeDefinitionId == DataTypeEncodingTypeId
            && DbLocalName(node.BrowseName) == DefaultJsonBrowseName;

        /// <summary>
        /// Stored BrowseNames are "nsu=uri;Name" for a non-zero namespace and bare for ns=0.
        /// </summary>
        private static string DbLocalName(string? browseName)
        {
            if (string.IsNullOrEmpty(browseName)) return string.Empty;
            var semi = browseName.LastIndexOf(';');
            return semi >= 0 ? browseName[(semi + 1)..] : browseName;
        }

        /// <summary>"1:IsNamespaceSubset" → "IsNamespaceSubset".</summary>
        private static string LocalName(string? browseName)
        {
            if (string.IsNullOrEmpty(browseName)) return string.Empty;
            var colon = browseName.IndexOf(':');
            return colon >= 0 ? browseName[(colon + 1)..] : browseName;
        }

        private static void Add(Dictionary<string, List<string>> index, string key, string value)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<string>();
            list.Add(value);
        }

        /// <summary>Every "DataType" string anywhere in the node's attributes JSON.</summary>
        private static IEnumerable<string> CollectDataTypeIds(JsonNode? attributes)
        {
            var found = new List<string>();
            Walk(attributes);
            return found;

            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonObject obj:
                        foreach (var (key, value) in obj)
                        {
                            if (key == "DataType" && value is JsonValue
                                && value.GetValueKind() == System.Text.Json.JsonValueKind.String)
                            {
                                found.Add(value.GetValue<string>());
                            }
                            else Walk(value);
                        }
                        break;
                    case JsonArray arr:
                        foreach (var item in arr) Walk(item);
                        break;
                }
            }
        }

        /// <summary>
        /// Each given reference type together with everything derived from it — a model may define
        /// its own subtypes of HasChild, HasCause and so on, and those have to be walked the same
        /// way as the standard ones. One query for all of them.
        /// </summary>
        private async Task<Dictionary<string, HashSet<string>>> ReferenceTypeFamiliesAsync(string[] roots)
        {
            var families = roots.ToDictionary(
                root => root,
                root => new HashSet<string>(StringComparer.Ordinal) { root },
                StringComparer.Ordinal);

            var subtypes = await _db.SubTypeHierarchy.AsNoTracking()
                .Where(h => roots.Contains(h.SuperTypeNodeId))
                .Select(h => new { h.SuperTypeNodeId, h.SubTypeNodeId })
                .ToListAsync();

            foreach (var subtype in subtypes)
                families[subtype.SuperTypeNodeId].Add(subtype.SubTypeNodeId);

            return families;
        }
    }
}
