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
        /// find what hierarchically contains a node, for the top-level anchor rule below.
        /// </summary>
        private const string HierarchicalReferencesId = "i=33";

        private const string NamespaceMetadataTypeId = "i=11616";

        /// <summary>Part 6 type namespace, for the Boolean value written into IsNamespaceSubset.</summary>
        private const string UaTypesNamespace = "http://opcfoundation.org/UA/2008/02/Types.xsd";

        /// <summary>Namespace of the Extensions element this app writes onto a subset.</summary>
        private const string SubsetExtensionNamespace = "https://opcua.rocks/UA/NodeSetEditor/Subset";

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
            // What hierarchically contains each node, i.e. the far end of its INVERSE hierarchical
            // references. Only consulted for top-level nodes (see the anchor rule below).
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
                if (nonHierarchical.Contains(r.ReferenceTypeId)) Add(outgoing, from, to);

                if (hasChild.Contains(r.ReferenceTypeId))
                {
                    Add(outgoing, from, to);
                    Add(outgoing, to, from);
                }

                if (hasSubtype.Contains(r.ReferenceTypeId)) Add(outgoing, to, from);

                // `to` holds this reference inversely, so `from` is what contains it.
                if (hierarchical.Contains(r.ReferenceTypeId)) Add(containers, to, from);
            }

            foreach (var node in nodes)
            {
                if (node.NodeId == null || node.ParentNodeId == null) continue;
                Add(outgoing, node.NodeId, node.ParentNodeId);
                Add(outgoing, node.ParentNodeId, node.NodeId);
            }

            // ---- Seed: every node of the model being downloaded ----
            // It is kept whole; what the trim removes is everything in the DEPENDENCIES that
            // nothing here reaches.
            var included = new HashSet<string>(StringComparer.Ordinal);
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

                // A top-level node — no ParentNodeId — is anchored into the address space only by
                // an inverse hierarchical reference, and for a node that sits under another
                // model's structure (Core's Objects folder, say, via Organizes) that anchor is in
                // that other model. Keep it, or the trimmed dependency drops the very node this
                // one hangs off. Same-model anchors need no special case: they are either already
                // reached or genuinely unused.
                if (node.ParentNodeId == null && containers.TryGetValue(nodeId, out var anchors))
                {
                    foreach (var anchor in anchors)
                    {
                        if (byNodeId.TryGetValue(anchor, out var anchorNode)
                            && anchorNode.ModelId != node.ModelId)
                        {
                            Enqueue(anchor);
                        }
                    }
                }

                // The DataType attribute, StructureDefinition field DataTypes and the DataTypes of
                // Method Input/OutputArguments are all spelled "DataType" in the attributes JSON,
                // so one recursive sweep catches them.
                foreach (var dataTypeId in CollectDataTypeIds(node.Attributes)) Enqueue(dataTypeId);
            }

            return included;

            void Enqueue(string? candidate)
            {
                if (candidate == null || !byNodeId.ContainsKey(candidate)) return;
                if (included.Add(candidate)) queue.Enqueue(candidate);
            }
        }

        public async Task<byte[]?> ExportTrimmedAsync(
            Guid modelId, IReadOnlySet<string> includeNodeIds, SubsetProvenance provenance)
        {
            var model = await _db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId);
            if (model?.Uri == null) return null;

            var nodeSet = await NodeSetConverter.CreateNodeSetAsync(_db, model.Uri, model.Version, includeNodeIds);

            MarkAsNamespaceSubset(nodeSet);
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

            var metadataObjectIds = items
                .Where(i => i.References?.Any(r =>
                    r.ReferenceType == HasTypeDefinitionId && r.IsForward
                    && r.Value == NamespaceMetadataTypeId) == true)
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

        /// <summary>
        /// Records who produced the subset and what it was trimmed for, in the document's
        /// Extensions. This is how a reader tells a trimmed dependency from the real NodeSet.
        /// </summary>
        private static void AddSubsetExtension(Opc.Ua.Export.UANodeSet nodeSet, SubsetProvenance provenance)
        {
            var document = new XmlDocument();
            var element = document.CreateElement("NodeSetSubset", SubsetExtensionNamespace);
            element.SetAttribute("TargetModelUri", provenance.TargetModelUri);
            element.SetAttribute("ExportedUtc", provenance.ExportedUtc.ToUniversalTime().ToString("o"));
            if (!string.IsNullOrWhiteSpace(provenance.UserName))
                element.SetAttribute("ExportedByName", provenance.UserName);
            if (!string.IsNullOrWhiteSpace(provenance.UserEmail))
                element.SetAttribute("ExportedByEmail", provenance.UserEmail);

            nodeSet.Extensions = [.. nodeSet.Extensions ?? [], element];
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
