extern alias JsonNodeSet;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Opc.Ua;
using Part6Variant = JsonNodeSet::Opc.Ua.NodeSetSerializer.Part6Variant;
using JsonAddressSpace = JsonNodeSet::Opc.Ua.NodeSetSerializer.AddressSpace;
using JsonCore = JsonNodeSet::Opc.Ua.NodeSetSerializer.CoreNodeSetLoader;
using JsonModel = JsonNodeSet::Opc.Ua.NodeSetSerializer.Model;

namespace NodeSetEditor.Model
{
    public static class NodeSetConverter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null,
            // PostgreSQL jsonb sorts keys alphabetically, so the polymorphic discriminator
            // (NodeType) may not be the first property. Allow out-of-order metadata.
            AllowOutOfOrderMetadataProperties = true,
        };

        private const string UaCoreNamespace = "http://opcfoundation.org/UA/";

        // OPC UA NodeClass numeric values — aliases for readability
        private const int NodeClassObject = UaNodeClass.Object;
        private const int NodeClassVariable = UaNodeClass.Variable;
        private const int NodeClassMethod = UaNodeClass.Method;
        private const int NodeClassObjectType = UaNodeClass.ObjectType;
        private const int NodeClassVariableType = UaNodeClass.VariableType;
        private const int NodeClassReferenceType = UaNodeClass.ReferenceType;
        private const int NodeClassDataType = UaNodeClass.DataType;
        private const int NodeClassView = UaNodeClass.View;

        // Well-known reference type NodeIds (after alias resolution, ns=0 form)
        private const string HasSubtypeId = "i=45";
        private const string HasTypeDefinitionId = "i=40";
        private const string HasEncodingId = "i=38";
        private const string DataTypeEncodingTypeId = "i=76";

        #region Store

        /// <summary>
        /// Stores a NodeSet (Model row + Nodes + References + SubTypeHierarchy) atomically.
        /// The insert is several separate SaveChanges calls (model row, then nodes/references,
        /// then the hierarchy); without a transaction, a failure partway through — e.g. a
        /// namespace-index or other malformed-XML exception while building nodes from
        /// nodeSet.Items — left the Model row committed with zero Nodes: a permanent "empty"
        /// model, since later imports of the same Uri+Version see it as already present and
        /// skip re-importing. Wrapped in the execution strategy because the DbContext has
        /// EnableRetryOnFailure configured, which disallows ad-hoc transactions otherwise.
        /// </summary>
        public static async Task<Model> StoreNodeSetAsync(NodeSetEditorDbContext db, Opc.Ua.Export.UANodeSet nodeSet)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var model = await StoreNodeSetCoreAsync(db, nodeSet);
                await tx.CommitAsync();
                return model;
            });
        }

        private static async Task<Model> StoreNodeSetCoreAsync(NodeSetEditorDbContext db, Opc.Ua.Export.UANodeSet nodeSet)
        {
            if (nodeSet.Models == null || nodeSet.Models.Length == 0)
                throw new ArgumentException("NodeSet must have at least one Model entry.", nameof(nodeSet));

            var modelTable = nodeSet.Models[0];
            var modelUri = modelTable.ModelUri!;
            var version = modelTable.Version; // legacy freeform text, preserved for round-trip
            var publicationDate = modelTable.PublicationDate != DateTime.MinValue
                ? modelTable.PublicationDate.ToUniversalTime().ToString("o")
                : null;
            var modelVersion = modelTable.ModelVersion; // SemVer, drives logic

            // Build full namespace table: index 0 = UA core, index 1+ = NamespaceUris entries
            // In NodeSet XML, NamespaceUris[0] maps to ns=1, NamespaceUris[1] to ns=2, etc.
            var nsTable = new[] { UaCoreNamespace };
            if (nodeSet.NamespaceUris != null)
                nsTable = nsTable.Concat(nodeSet.NamespaceUris).ToArray();

            // Build alias lookup (alias name → resolved NodeId string)
            var aliases = new Dictionary<string, string>();
            if (nodeSet.Aliases != null)
                foreach (var alias in nodeSet.Aliases)
                    aliases[alias.Alias] = alias.Value;

            // Parse SemVer from ModelVersion for dedup and storage
            var model = new Model
            {
                Name = modelUri,
                Uri = modelUri,
                Version = version,
                PublicationDate = publicationDate,
            };
            model.SetVersionNorm(modelVersion ?? version);

            // Re-importing a version that already exists (same Uri + normalized
            // SemVer) replaces the model's CONTENT but must keep the ROW itself:
            // the Id anchors WorkspaceModels links in every workspace (deleting
            // the row would cascade them all away), and curated metadata — Name,
            // Description, Published, Creator — set by users or catalog imports
            // is never overridden by automatic code.
            var existing = await db.Models
                .FirstOrDefaultAsync(m => m.Uri == modelUri && m.VersionNorm == model.VersionNorm);

            if (existing != null)
            {
                await db.Nodes.Where(n => n.ModelId == existing.Id).ExecuteDeleteAsync();
                await db.References.Where(r => r.ModelId == existing.Id).ExecuteDeleteAsync();
                await db.NodeSetTypes.Where(t => t.ModelId == existing.Id).ExecuteDeleteAsync();

                existing.Version = version;
                existing.PublicationDate = publicationDate;
                model = existing;
            }

            // Build metadata (Aliases stored in URI form for round-trip)
            var metadata = BuildMetadata(nodeSet, modelTable, nsTable, aliases);
            model.Metadata = metadata.Count > 0 ? metadata : null;

            if (existing == null)
                db.Models.Add(model);
            await db.SaveChangesAsync();

            var nodes = new List<Node>();
            var references = new List<Reference>();

            var nodeOrdinal = 0;
            if (nodeSet.Items != null)
            {
                foreach (var item in nodeSet.Items)
                {
                    var rawNodeId = ResolveAlias(item.NodeId, aliases);
                    int nodeClass = GetNodeClass(item);
                    var attrs = BuildAttributes(item, aliases, nsTable);

                    string? parentNodeId = null;
                    if (item is Opc.Ua.Export.UAInstance instance && instance.ParentNodeId != null)
                    {
                        parentNodeId = FormatColumnNodeId(
                            ResolveAlias(instance.ParentNodeId, aliases), nsTable);
                    }

                    // Find SuperTypeId, TypeDefinitionId, and (for unparented
                    // DataTypeEncoding objects) the inverse HasEncoding target
                    // we'll use as a fallback parent below.
                    string? superTypeId = null;
                    string? typeDefinitionId = null;
                    string? inverseHasEncodingTarget = null;
                    if (item.References != null)
                    {
                        foreach (var r in item.References)
                        {
                            var refTypeId = ResolveAlias(r.ReferenceType, aliases);
                            var targetId = ResolveAlias(r.Value, aliases);

                            if (refTypeId == HasSubtypeId && !r.IsForward)
                                superTypeId = FormatColumnNodeId(targetId, nsTable);
                            else if (refTypeId == HasTypeDefinitionId && r.IsForward)
                                typeDefinitionId = FormatColumnNodeId(targetId, nsTable);
                            else if (refTypeId == HasEncodingId && !r.IsForward)
                                inverseHasEncodingTarget = FormatColumnNodeId(targetId, nsTable);
                        }
                    }

                    // DataTypeEncoding objects ("Default Binary", "Default
                    // XML", "Default JSON") sit under their DataType in the
                    // address space but the canonical NodeSet XML omits a
                    // ParentNodeId on them — the membership is implied by the
                    // inverse HasEncoding reference. Materialise that as a
                    // ParentNodeId so the address-space tree shows them in
                    // the right place.
                    if (parentNodeId == null
                        && typeDefinitionId == DataTypeEncodingTypeId
                        && inverseHasEncodingTarget != null)
                    {
                        parentNodeId = inverseHasEncodingTarget;
                    }

                    // Extract display name and description from item
                    string? displayName = null;
                    if (item.DisplayName != null && item.DisplayName.Length > 0)
                        displayName = item.DisplayName[0].Value;
                    string? description = null;
                    if (item.Description != null && item.Description.Length > 0)
                        description = item.Description[0].Value;

                    // Extract modelling rule from references
                    string? modellingRuleId = null;
                    if (item.References != null)
                    {
                        foreach (var r in item.References)
                        {
                            var rtId = ResolveAlias(r.ReferenceType, aliases);
                            if (rtId == "i=37" && r.IsForward) // HasModellingRule
                            {
                                modellingRuleId = FormatColumnNodeId(
                                    ResolveAlias(r.Value, aliases), nsTable);
                                break;
                            }
                        }
                    }

                    nodeOrdinal += 100;
                    var node = new Node
                    {
                        ModelId = model.Id,
                        NodeId = FormatColumnNodeId(rawNodeId, nsTable),
                        ParentNodeId = parentNodeId,
                        NodeClass = nodeClass,
                        BrowseName = FormatDbBrowseName(item.BrowseName, nsTable),
                        DisplayName = displayName,
                        Description = description,
                        SuperTypeId = superTypeId,
                        TypeDefinitionId = typeDefinitionId,
                        ModellingRule = modellingRuleId,
                        Attributes = SerializeAttributes(attrs),
                        Ordinal = nodeOrdinal,
                    };
                    nodes.Add(node);

                    // Extract references
                    var refOrdinal = 0;
                    if (item.References != null)
                    {
                        foreach (var r in item.References)
                        {
                            var refTypeId = ResolveAlias(r.ReferenceType, aliases);
                            var targetId = ResolveAlias(r.Value, aliases);
                            refOrdinal += 100;

                            references.Add(new Reference
                            {
                                ModelId = model.Id,
                                SourceNodeId = FormatColumnNodeId(rawNodeId, nsTable),
                                ReferenceTypeId = FormatColumnNodeId(refTypeId, nsTable),
                                IsForward = r.IsForward,
                                TargetNodeId = FormatColumnNodeId(targetId, nsTable),
                                Ordinal = refOrdinal,
                            });
                        }
                    }
                }
            }

            db.Nodes.AddRange(nodes);
            db.References.AddRange(references);
            await db.SaveChangesAsync();

            // Build SubTypeHierarchy from HasSubtype references
            await BuildSubTypeHierarchyAsync(db, model.Id, nodes, references);

            return model;
        }

        /// <summary>
        /// Builds the SubTypeHierarchy materialized path table from HasSubtype references.
        /// For each type node, walks the supertype chain and inserts entries at each depth.
        /// </summary>
        private static async Task BuildSubTypeHierarchyAsync(
            NodeSetEditorDbContext db, Guid modelId,
            List<Node> nodes, List<Reference> references)
        {
            // Build supertype lookup: child → parent
            var superTypeMap = new Dictionary<string, string>();
            foreach (var r in references)
            {
                if (r.ReferenceTypeId == HasSubtypeId || r.ReferenceTypeId == "i=45")
                {
                    if (!r.IsForward && r.SourceNodeId != null && r.TargetNodeId != null)
                        superTypeMap[r.SourceNodeId] = r.TargetNodeId;
                    else if (r.IsForward && r.SourceNodeId != null && r.TargetNodeId != null)
                        superTypeMap[r.TargetNodeId] = r.SourceNodeId;
                }
            }

            // Also use Node.SuperTypeId
            foreach (var node in nodes)
            {
                if (node.SuperTypeId != null && node.NodeId != null)
                    superTypeMap.TryAdd(node.NodeId, node.SuperTypeId);
            }

            // Remove existing hierarchy entries for nodes in this model
            var nodeIds = nodes.Where(n => n.NodeId != null).Select(n => n.NodeId!).ToHashSet();
            var existingEntries = await db.SubTypeHierarchy
                .Where(h => nodeIds.Contains(h.SubTypeNodeId))
                .ToListAsync();
            if (existingEntries.Count > 0)
                db.SubTypeHierarchy.RemoveRange(existingEntries);

            // Walk supertype chains
            var entries = new List<SubTypeHierarchy>();
            foreach (var nodeId in nodeIds)
            {
                if (!superTypeMap.ContainsKey(nodeId)) continue;

                var current = nodeId;
                var depth = 0;
                var visited = new HashSet<string>();
                while (superTypeMap.TryGetValue(current, out var parentId) && visited.Add(current))
                {
                    depth++;
                    entries.Add(new SubTypeHierarchy
                    {
                        SubTypeNodeId = nodeId,
                        SuperTypeNodeId = parentId,
                        Depth = depth
                    });
                    current = parentId;
                }
            }

            if (entries.Count > 0)
            {
                db.SubTypeHierarchy.AddRange(entries);
                await db.SaveChangesAsync();
            }
        }

        #endregion

        #region Load

        public static async Task<Opc.Ua.Export.UANodeSet> CreateNodeSetAsync(
            NodeSetEditorDbContext db, string modelUri, string? version = null)
        {
            // Find the model — version param is the freeform Version text for exact match
            Model? model;
            if (version != null)
            {
                var norm = Model.NormalizeVersion(version);
                model = await db.Models.FirstOrDefaultAsync(m => m.Uri == modelUri && m.VersionNorm == norm)
                    ?? await db.Models.FirstOrDefaultAsync(m => m.Uri == modelUri && m.Version == version);
            }
            else
            {
                model = await db.Models.Where(m => m.Uri == modelUri)
                    .OrderByDescending(m => m.VersionNorm)
                    .FirstOrDefaultAsync();
            }

            if (model == null)
                throw new InvalidOperationException($"Model not found: {modelUri} version={version ?? "latest"}");

            // Load nodes and references, ordered for deterministic export
            var dbNodes = await db.Nodes.Where(n => n.ModelId == model.Id)
                .OrderBy(n => n.Ordinal).ToListAsync();
            var dbRefs = await db.References.Where(r => r.ModelId == model.Id)
                .OrderBy(r => r.Ordinal).ToListAsync();

            // Collect namespace URIs in two buckets:
            //   * dependencyUris — namespaces whose definitions this model actually
            //     references via node-graph links (DataTypes, supertypes, type
            //     definitions, references, definition fields). These become BOTH
            //     NamespaceUris entries AND RequiredModels.
            //   * valueOnlyUris — namespaces appearing only inside Variable values
            //     (NodeId / ExpandedNodeId / QualifiedName / ExtensionObject UaTypeId).
            //     These are opaque identifiers per Part 6 — they go into NamespaceUris
            //     so the ns=N round-trips, but NOT into RequiredModels because the
            //     model doesn't actually depend on those NodeSets being loadable.
            var dependencyUris = new HashSet<string>();
            var valueOnlyUris = new HashSet<string>();
            foreach (var n in dbNodes)
            {
                CollectUriFromColumnId(n.NodeId, dependencyUris);
                CollectUriFromColumnId(n.ParentNodeId, dependencyUris);
                CollectUriFromColumnId(n.SuperTypeId, dependencyUris);
                CollectUriFromColumnId(n.TypeDefinitionId, dependencyUris);
                CollectUriFromNsuId(n.BrowseName, dependencyUris);
                // Definition / DataType / MethodDeclarationId references are dependencies;
                // values are not. Splitting the two is what CollectUrisFromAttributes does.
                CollectDependencyUrisFromAttributes(n.Attributes, dependencyUris);
                CollectValueOnlyUrisFromAttributes(n.Attributes, valueOnlyUris);
            }
            foreach (var r in dbRefs)
            {
                CollectUriFromColumnId(r.SourceNodeId, dependencyUris);
                CollectUriFromColumnId(r.ReferenceTypeId, dependencyUris);
                CollectUriFromColumnId(r.TargetNodeId, dependencyUris);
            }
            dependencyUris.Remove(UaCoreNamespace); // ns=0 is implicit, not in NamespaceUris
            valueOnlyUris.Remove(UaCoreNamespace);
            // A URI present as both a dependency and a value-only stays a dependency.
            valueOnlyUris.ExceptWith(dependencyUris);

            // The combined set drives NamespaceUris.
            var collectedUris = new HashSet<string>(dependencyUris);
            collectedUris.UnionWith(valueOnlyUris);

            // Build NamespaceUris: model's own URI first (ns=1), then others sorted
            var namespaceUris = new List<string> { model.Uri! };
            foreach (var uri in collectedUris.Where(u => u != model.Uri).OrderBy(u => u))
                namespaceUris.Add(uri);

            // Build URI → namespace index mapping
            var uriToIdx = new Dictionary<string, int> { [UaCoreNamespace] = 0 };
            for (int i = 0; i < namespaceUris.Count; i++)
                uriToIdx[namespaceUris[i]] = i + 1;

            // Build reference lookup by source
            var refsBySource = dbRefs.GroupBy(r => r.SourceNodeId)
                .ToDictionary(g => g.Key!, g => g.ToList());

            // Reconstruct UANodeSet
            var nodeSet = new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = namespaceUris.ToArray(),
            };

            // Restore non-namespace metadata (Aliases with restored indices, Extensions, etc.)
            RestoreMetadata(nodeSet, model.Metadata, uriToIdx);

            // Build model entry
            DateTime modelPd = DateTime.MinValue;
            var hasPubDate = !string.IsNullOrEmpty(model.PublicationDate)
                && DateTime.TryParse(model.PublicationDate, out modelPd);
            if (hasPubDate) modelPd = modelPd.ToUniversalTime();
            var modelEntry = new Opc.Ua.Export.ModelTableEntry
            {
                ModelUri = model.Uri,
                Version = model.Version,
                PublicationDate = modelPd,
                PublicationDateSpecified = hasPubDate,
            };
            if (model.Metadata != null)
            {
                if (model.Metadata.TryGetPropertyValue("ModelVersion", out var mvNode))
                    modelEntry.ModelVersion = mvNode?.ToString();
                if (model.Metadata.TryGetPropertyValue("XmlSchemaUri", out var xsNode))
                    modelEntry.XmlSchemaUri = xsNode?.ToString();
                if (model.Metadata.TryGetPropertyValue("AccessRestrictions", out var arNode))
                    modelEntry.AccessRestrictions = (ushort)(arNode?.GetValue<int>() ?? 0);
                if (model.Metadata.TryGetPropertyValue("RolePermissions", out var rpNode)
                    && rpNode is JsonArray rpArr)
                {
                    var rpList = new List<Opc.Ua.Export.RolePermission>();
                    foreach (var item in rpArr)
                    {
                        if (item is not JsonObject rpObj) continue;
                        var rp = new Opc.Ua.Export.RolePermission
                        {
                            Value = RestoreAttrNodeId(rpObj["Value"]?.ToString(), uriToIdx),
                        };
                        if (rpObj.TryGetPropertyValue("Permissions", out var permNode))
                            rp.Permissions = (uint)(permNode?.GetValue<long>() ?? 0);
                        rpList.Add(rp);
                    }
                    if (rpList.Count > 0)
                        modelEntry.RolePermissions = rpList.ToArray();
                }
            }

            // Generate RequiredModels from dependencyUris (NOT the full NamespaceUris):
            // value-only namespaces are opaque identifiers and must not be added as
            // RequiredModels — the model doesn't depend on them being loaded. See
            // CollectValueOnlyUrisFromAttributes for why.
            var dependencyList = namespaceUris
                .Where(u => u == model.Uri || dependencyUris.Contains(u))
                .ToList();
            modelEntry.RequiredModel = await GenerateRequiredModels(
                db, dependencyList, model.Uri!, model.Metadata);
            nodeSet.Models = [modelEntry];

            // Two-pass build:
            //
            //   Pass 1 — populate a NodeSetSerializer.AddressSpace with this model's DataTypes
            //            (plus the embedded Core fallback) so the value-emitting writer
            //            in pass 2 can resolve struct wrapper names, recognise built-in
            //            shapes (NodeId/QualifiedName/DateTime), etc. The AddressSpace is
            //            transient — it does not escape this method.
            //
            //   Pass 2 — emit the Opc.Ua.Export.UANodeSet, calling JsonToXmlElement(value,
            //            addressSpace) so Variable values render byte-stable Part 6 XML.
            var addressSpace = BuildPartialAddressSpaceForValueWriting(
                model.Uri!, dbNodes, dbRefs, uriToIdx);

            // Reconstruct nodes (pass 2)
            var items = new List<Opc.Ua.Export.UANode>();
            foreach (var dbNode in dbNodes)
            {
                var restoredNodeId = RestoreColumnNodeId(dbNode.NodeId!, uriToIdx);
                var attrs = DeserializeAttributes(dbNode.Attributes);

                var uaNode = CreateUANode(dbNode.NodeClass);
                uaNode.NodeId = restoredNodeId;
                uaNode.BrowseName = RestoreDbBrowseName(dbNode.BrowseName, uriToIdx);

                PopulateCommonAttributes(uaNode, attrs, uriToIdx);

                // Every exported node carries a DisplayName. Part 6 permits omitting one that
                // merely repeats the BrowseName, and many source NodeSets do — but consumers that
                // read the file without an address space shouldn't have to reconstruct it, so fall
                // back to the DisplayName column and then to the BrowseName's local part.
                if (uaNode.DisplayName == null || uaNode.DisplayName.Length == 0)
                {
                    var fallback = !string.IsNullOrEmpty(dbNode.DisplayName)
                        ? dbNode.DisplayName!
                        : ParseBrowseNamePrefix(uaNode.BrowseName ?? string.Empty).name;
                    if (!string.IsNullOrEmpty(fallback))
                        uaNode.DisplayName = [new Opc.Ua.Export.LocalizedText { Value = fallback }];
                }

                if (uaNode is Opc.Ua.Export.UAInstance instance && dbNode.ParentNodeId != null)
                    instance.ParentNodeId = RestoreColumnNodeId(dbNode.ParentNodeId, uriToIdx);

                PopulateSubtypeAttributes(uaNode, dbNode.NodeClass, attrs, uriToIdx, addressSpace, namespaceUris);

                if (refsBySource.TryGetValue(dbNode.NodeId!, out var nodeRefs))
                {
                    uaNode.References = nodeRefs.Select(r => new Opc.Ua.Export.Reference
                    {
                        ReferenceType = RestoreColumnNodeId(r.ReferenceTypeId!, uriToIdx),
                        IsForward = r.IsForward,
                        Value = RestoreColumnNodeId(r.TargetNodeId!, uriToIdx),
                    }).ToArray();
                }

                items.Add(uaNode);
            }

            nodeSet.Items = items.ToArray();
            return nodeSet;
        }

        /// <summary>
        /// Pass 1 of the two-pass build: assembles a NodeSetSerializer.AddressSpace from the
        /// in-progress reconstitution. The AddressSpace contains:
        ///   * The embedded Core (Services) NodeSet — gives every spec-compliant NodeSet
        ///     access to standard DataTypes (Argument, EUInformation, BuildInfo, etc.).
        ///   * A ModelDefinition for the current model.
        ///   * Every UADataType this model defines, with its DataTypeDefinition, supertype
        ///     reference (inverse HasSubtype), and HasEncoding references to encoding nodes.
        ///   * The encoding nodes (Default Binary / Default XML / Default JSON) themselves,
        ///     so encoding TypeId → DataType normalization works for ExtensionObject values.
        ///
        /// What's intentionally NOT in here: Variable nodes (we're trying to render their
        /// values), Object instances, Methods, Views. The writer only needs DataType
        /// resolution to do its job.
        /// </summary>
        private static JsonAddressSpace BuildPartialAddressSpaceForValueWriting(
            string modelUri,
            List<Node> dbNodes,
            List<Reference> dbRefs,
            Dictionary<string, int> uriToIdx)
        {
            var space = new JsonAddressSpace();

            // Embedded Core fallback — always loaded first so Core types are visible
            // before this model's types layer on top.
            JsonCore.EnsureCoreLoaded(space);

            // Skip if this IS Core — Core was just loaded by the line above and adding
            // the model definition again would be a duplicate registration.
            if (string.Equals(modelUri, "http://opcfoundation.org/UA/", StringComparison.Ordinal))
                return space;

            // Register the current model so namespace-aware lookups (XmlSchemaUri etc.) work.
            space.AddModel(new JsonModel.ModelDefinition
            {
                ModelUri = modelUri,
            });

            // References indexed by source for fast HasSubtype/HasEncoding lookup
            var refsBySource = dbRefs.GroupBy(r => r.SourceNodeId)
                .ToDictionary(g => g.Key!, g => g.ToList());
            const string HasSubtype = "i=45";
            const string HasEncoding = "i=38";

            // First pass: add encoding objects (DataType nodes reference them via i=38).
            foreach (var dbNode in dbNodes)
            {
                if (dbNode.NodeClass != UaNodeClass.Object) continue;
                var bn = RestoreDbBrowseName(dbNode.BrowseName, uriToIdx) ?? "";
                if (bn != "Default Binary" && bn != "Default XML" && bn != "Default JSON") continue;

                var nodeId = RestoreColumnNodeId(dbNode.NodeId!, uriToIdx);
                space.AddNode(new JsonModel.UAObject
                {
                    NodeId = nodeId,
                    NodeClass = JsonModel.NodeClass.UAObject,
                    BrowseName = bn,
                });
            }

            // Second pass: add DataTypes with full Definition + supertype + encoding refs.
            foreach (var dbNode in dbNodes)
            {
                if (dbNode.NodeClass != UaNodeClass.DataType) continue;

                var attrs = DeserializeAttributes(dbNode.Attributes) as DataTypeAttributes;
                var nodeId = RestoreColumnNodeId(dbNode.NodeId!, uriToIdx);
                var browseName = RestoreDbBrowseName(dbNode.BrowseName, uriToIdx);

                var dt = new JsonModel.UADataType
                {
                    NodeId = nodeId,
                    NodeClass = JsonModel.NodeClass.UADataType,
                    BrowseName = browseName,
                    IsAbstract = attrs?.IsAbstract,
                };

                // DataTypeDefinition — convert from internal entry shape to NodeSetSerializer shape.
                if (attrs?.Definition != null)
                {
                    dt.Definition = ConvertDefinitionToJsonNodeSet(attrs.Definition);
                }

                // Supertype: inverse HasSubtype reference (encoded as a DB reference
                // with IsForward=true on the supertype, OR stored as SuperTypeId on this node).
                var refs = new List<JsonModel.Reference>();
                if (!string.IsNullOrEmpty(dbNode.SuperTypeId))
                {
                    var superId = RestoreColumnNodeId(dbNode.SuperTypeId, uriToIdx);
                    refs.Add(new JsonModel.Reference
                    {
                        ReferenceTypeId = HasSubtype,
                        TargetId = superId,
                        IsForward = false,
                    });
                }

                // Forward HasEncoding references to "Default Binary"/"Default XML"/"Default JSON"
                // — needed so ExtensionObject TypeId encoding-NodeId normalization works.
                if (refsBySource.TryGetValue(dbNode.NodeId!, out var nodeRefs))
                {
                    foreach (var r in nodeRefs)
                    {
                        if (r.ReferenceTypeId != HasEncoding || !r.IsForward) continue;
                        var encNodeId = RestoreColumnNodeId(r.TargetNodeId!, uriToIdx);
                        refs.Add(new JsonModel.Reference
                        {
                            ReferenceTypeId = HasEncoding,
                            TargetId = encNodeId,
                            IsForward = true,
                        });
                    }
                }

                if (refs.Count > 0) dt.References = refs;

                space.AddNode(dt);
            }

            // Trigger the AddressSpace's variant indexes so encoding lookups are warm.
            space.BuildVariantIndexes();

            return space;
        }

        /// <summary>
        /// Bridges from the internal <see cref="DataTypeDefinitionEntry"/> shape (used in
        /// DB-stored attributes) to the NodeSetSerializer model's
        /// <see cref="JsonNodeSet::Opc.Ua.NodeSetSerializer.Model.DataTypeDefinition"/> so the
        /// NodeSetSerializer AddressSpace can drive the schema-aware Variant writer.
        /// </summary>
        private static JsonModel.DataTypeDefinition ConvertDefinitionToJsonNodeSet(
            DataTypeDefinitionEntry src)
        {
            // No Name: the NodeSetSerializer model takes it from the containing DataType's BrowseName.
            var def = new JsonModel.DataTypeDefinition
            {
                SymbolicName = src.SymbolicName,
                IsUnion = src.IsUnion,
                IsOptionSet = src.IsOptionSet,
            };
            if (src.Fields != null)
            {
                def.Fields = src.Fields.Select(f => new JsonModel.DataTypeField
                {
                    Name = f.Name,
                    SymbolicName = f.SymbolicName,
                    DataType = f.DataType,
                    ValueRank = f.ValueRank,
                    ArrayDimensions = f.ArrayDimensions,
                    Value = f.Value,
                    IsOptional = f.IsOptional,
                    AllowSubTypes = f.AllowSubTypes,
                }).ToList();
            }
            return def;
        }

        #endregion

        #region Namespace Resolution — Store Direction

        /// <summary>
        /// Parse "ns=N;rest" → (N, "rest"). Returns (0, original) if no ns= prefix.
        /// </summary>
        private static (int nsIndex, string rest) ParseNsPrefix(string nodeId)
        {
            if (nodeId.StartsWith("ns="))
            {
                var semi = nodeId.IndexOf(';');
                if (semi > 3 && int.TryParse(nodeId.AsSpan(3, semi - 3), out int ns))
                    return (ns, nodeId[(semi + 1)..]);
            }
            return (0, nodeId);
        }

        /// <summary>
        /// Parse "N:name" → (N, "name"). Returns (0, original) if no numeric prefix.
        /// </summary>
        private static (int nsIndex, string name) ParseBrowseNamePrefix(string browseName)
        {
            var colon = browseName.IndexOf(':');
            if (colon > 0 && int.TryParse(browseName.AsSpan(0, colon), out int ns))
                return (ns, browseName[(colon + 1)..]);
            return (0, browseName);
        }

        /// <summary>
        /// Format a NodeId for DB column storage (NodeId, ParentNodeId, SuperTypeId, etc.).
        /// Non-zero ns → nsu=uri;identifier, ns=0 → bare identifier.
        /// </summary>
        private static string FormatColumnNodeId(string rawNodeId, string[] nsTable)
        {
            var (ns, identifier) = ParseNsPrefix(rawNodeId);
            if (ns == 0) return identifier;

            if (ns >= nsTable.Length)
                throw new InvalidOperationException($"Namespace index {ns} out of range in NodeId '{rawNodeId}'");

            return $"nsu={nsTable[ns]};{identifier}";
        }

        /// <summary>
        /// Format a BrowseName for DB storage. "N:Name" → "nsu=uri;Name" for non-zero ns, bare "Name" for ns=0.
        /// </summary>
        private static string? FormatDbBrowseName(string? browseName, string[] nsTable)
        {
            if (string.IsNullOrEmpty(browseName)) return browseName;
            var (ns, name) = ParseBrowseNamePrefix(browseName);
            if (ns == 0) return name;

            if (ns >= nsTable.Length)
                throw new InvalidOperationException($"Namespace index {ns} out of range in BrowseName '{browseName}'");

            return $"nsu={nsTable[ns]};{name}";
        }

        /// <summary>
        /// Format a NodeId for JSON attribute storage (DataType, MethodDeclarationId, etc.).
        /// Uses nsu=uri;identifier for all non-zero ns (no model GUID — GUID may change on reimport).
        /// </summary>
        private static string FormatAttrNodeId(string rawNodeId, string[] nsTable)
        {
            var (ns, identifier) = ParseNsPrefix(rawNodeId);
            if (ns == 0) return identifier;

            if (ns >= nsTable.Length)
                throw new InvalidOperationException($"Namespace index {ns} out of range in attribute NodeId '{rawNodeId}'");

            return $"nsu={nsTable[ns]};{identifier}";
        }

        /// <summary>
        /// Format a QualifiedName (Definition.Name) for JSON attribute storage.
        /// Same logic as FormatDbBrowseName.
        /// </summary>
        private static string? FormatAttrQualifiedName(string? qname, string[] nsTable)
        {
            return FormatDbBrowseName(qname, nsTable);
        }

        #endregion

        #region Namespace Resolution — Load Direction

        /// <summary>
        /// Restore a NodeId from DB column format to "ns=N;identifier" form.
        /// </summary>
        private static string RestoreColumnNodeId(string dbId, Dictionary<string, int> uriToIdx)
        {
            // "nsu=uri;identifier" → "ns=N;identifier"
            if (dbId.StartsWith("nsu="))
            {
                var semi = dbId.IndexOf(';');
                if (semi > 4)
                {
                    var uri = dbId[4..semi];
                    var rest = dbId[(semi + 1)..];
                    if (uriToIdx.TryGetValue(uri, out int idx))
                        return idx == 0 ? rest : $"ns={idx};{rest}";
                }
            }

            // ns=0 (UA core) — bare identifier, return as-is
            return dbId;
        }

        /// <summary>
        /// Restore a BrowseName from DB format to "N:name" form.
        /// </summary>
        private static string? RestoreDbBrowseName(string? dbBn, Dictionary<string, int> uriToIdx)
        {
            if (string.IsNullOrEmpty(dbBn)) return dbBn;

            if (dbBn.StartsWith("nsu="))
            {
                var semi = dbBn.IndexOf(';');
                if (semi > 4)
                {
                    var uri = dbBn[4..semi];
                    var name = dbBn[(semi + 1)..];
                    if (uriToIdx.TryGetValue(uri, out int idx))
                        return idx == 0 ? name : $"{idx}:{name}";
                }
            }

            // ns=0 — bare name
            return dbBn;
        }

        /// <summary>
        /// Restore a NodeId from JSON attribute format to "ns=N;identifier" form.
        /// </summary>
        private static string? RestoreAttrNodeId(string? dbId, Dictionary<string, int> uriToIdx)
        {
            if (string.IsNullOrEmpty(dbId)) return dbId;

            if (dbId.StartsWith("nsu="))
            {
                var semi = dbId.IndexOf(';');
                if (semi > 4)
                {
                    var uri = dbId[4..semi];
                    var rest = dbId[(semi + 1)..];
                    if (uriToIdx.TryGetValue(uri, out int idx))
                        return idx == 0 ? rest : $"ns={idx};{rest}";
                }
            }

            return dbId;
        }

        /// <summary>
        /// Restore a QualifiedName from JSON attribute format.
        /// </summary>
        private static string? RestoreAttrQualifiedName(string? dbQn, Dictionary<string, int> uriToIdx)
        {
            return RestoreDbBrowseName(dbQn, uriToIdx);
        }

        #endregion

        #region URI Collection — Load Direction

        private static void CollectUriFromColumnId(string? dbId, HashSet<string> uris)
        {
            CollectUriFromNsuId(dbId, uris);
        }

        private static void CollectUriFromNsuId(string? dbId, HashSet<string> uris)
        {
            if (string.IsNullOrEmpty(dbId) || !dbId.StartsWith("nsu=")) return;
            var semi = dbId.IndexOf(';');
            if (semi > 4)
                uris.Add(dbId[4..semi]);
        }

        /// <summary>
        /// Collects URIs from attribute fields that represent node-graph dependencies
        /// (DataType, MethodDeclarationId, DataTypeDefinition fields). These belong in
        /// both NamespaceUris and RequiredModels.
        /// </summary>
        private static void CollectDependencyUrisFromAttributes(JsonObject? attrs, HashSet<string> uris)
        {
            if (attrs == null) return;

            if (attrs.TryGetPropertyValue("DataType", out var dtNode))
                CollectUriFromNsuId(dtNode?.ToString(), uris);

            if (attrs.TryGetPropertyValue("MethodDeclarationId", out var mdNode))
                CollectUriFromNsuId(mdNode?.ToString(), uris);

            if (attrs.TryGetPropertyValue("Definition", out var defNode) && defNode is JsonObject defObj)
            {
                if (defObj.TryGetPropertyValue("Name", out var nameNode))
                    CollectUriFromNsuId(nameNode?.ToString(), uris);
                if (defObj.TryGetPropertyValue("BaseType", out var btNode))
                    CollectUriFromNsuId(btNode?.ToString(), uris);
                if (defObj.TryGetPropertyValue("Fields", out var fieldsNode) && fieldsNode is JsonArray fieldsArr)
                {
                    foreach (var field in fieldsArr)
                    {
                        if (field is JsonObject fieldObj && fieldObj.TryGetPropertyValue("DataType", out var fdt))
                            CollectUriFromNsuId(fdt?.ToString(), uris);
                    }
                }
            }
        }

        /// <summary>
        /// Collects URIs that appear ONLY inside Variable values (NodeId / ExpandedNodeId
        /// / QualifiedName values, plus ExtensionObject UaTypeIds). Per Part 6, these are
        /// opaque identifiers — the namespace must appear in NamespaceUris so the ns=N
        /// index round-trips, but NOT in RequiredModels because the model doesn't depend
        /// on those NodeSets being available at load time. (Common case: well-known
        /// identifiers like IRDIs that link back to external repositories.)
        /// </summary>
        private static void CollectValueOnlyUrisFromAttributes(JsonObject? attrs, HashSet<string> uris)
        {
            if (attrs == null) return;
            if (attrs.TryGetPropertyValue("Value", out var valueNode))
                CollectUrisFromValue(valueNode, uris);
        }

        /// <summary>
        /// Recursively walks a stored Part 6 JSON value, extracting any embedded nsu=URI
        /// references so they get registered in the model's NamespaceUris table.
        /// Per Part 6, NodeId / ExpandedNodeId / QualifiedName values (and the UaTypeId
        /// inside ExtensionObject wrappers) carry opaque identifiers — the namespace
        /// doesn't need to resolve to a node in this NodeSet, but the NamespaceUris
        /// table must contain the URI so the ns=N XML index round-trips.
        /// </summary>
        private static void CollectUrisFromValue(JsonNode? node, HashSet<string> uris)
        {
            if (node == null) return;
            switch (node)
            {
                case JsonValue jv when jv.GetValueKind() == JsonValueKind.String:
                {
                    // Unbox to the underlying string. JsonValue.ToString() returns the
                    // raw value for strings (not the quoted JSON form), but using
                    // TryGetValue<string> is unambiguous.
                    if (jv.TryGetValue<string>(out var s)) CollectUriFromNsuId(s, uris);
                    break;
                }
                case JsonObject obj:
                    foreach (var prop in obj)
                    {
                        if (prop.Key is "UaTypeId" && prop.Value is JsonValue idVal
                            && idVal.TryGetValue<string>(out var typeIdStr))
                            CollectUriFromNsuId(typeIdStr, uris);
                        else
                            CollectUrisFromValue(prop.Value, uris);
                    }
                    break;
                case JsonArray arr:
                    foreach (var item in arr) CollectUrisFromValue(item, uris);
                    break;
            }
        }

        #endregion

        #region Metadata

        private static JsonObject BuildMetadata(
            Opc.Ua.Export.UANodeSet nodeSet, Opc.Ua.Export.ModelTableEntry modelTable,
            string[] nsTable, Dictionary<string, string> aliases)
        {
            var metadata = new JsonObject();

            // Store Aliases in URI-resolved form so round-trip works regardless of namespace ordering
            if (nodeSet.Aliases != null && nodeSet.Aliases.Length > 0)
            {
                var aliasObj = new JsonObject();
                foreach (var alias in nodeSet.Aliases)
                {
                    // Resolve the alias value's namespace to URI form
                    aliasObj[alias.Alias] = FormatAttrNodeId(alias.Value, nsTable);
                }
                metadata["Aliases"] = aliasObj;
            }

            // Store RequiredModels as fallback for version info
            if (modelTable.RequiredModel != null && modelTable.RequiredModel.Length > 0)
            {
                var reqArr = new JsonArray();
                foreach (var rm in modelTable.RequiredModel)
                {
                    var reqObj = new JsonObject { ["ModelUri"] = rm.ModelUri };
                    if (rm.Version != null) reqObj["Version"] = rm.Version;
                    if (rm.PublicationDate != DateTime.MinValue)
                        reqObj["PublicationDate"] = rm.PublicationDate.ToUniversalTime().ToString("o");
                    if (rm.ModelVersion != null) reqObj["ModelVersion"] = rm.ModelVersion;
                    if (rm.XmlSchemaUri != null) reqObj["XmlSchemaUri"] = rm.XmlSchemaUri;
                    reqArr.Add(reqObj);
                }
                metadata["RequiredModels"] = reqArr;
            }

            // Store model-level RolePermissions
            if (modelTable.RolePermissions != null && modelTable.RolePermissions.Length > 0)
            {
                var rpArr = new JsonArray();
                foreach (var rp in modelTable.RolePermissions)
                {
                    var rpObj = new JsonObject();
                    rpObj["Value"] = FormatAttrNodeId(rp.Value, nsTable);
                    if (rp.Permissions != 0) rpObj["Permissions"] = rp.Permissions;
                    rpArr.Add(rpObj);
                }
                metadata["RolePermissions"] = rpArr;
            }

            // Store model-level AccessRestrictions
            if (modelTable.AccessRestrictions != 0)
                metadata["AccessRestrictions"] = modelTable.AccessRestrictions;

            if (modelTable.ModelVersion != null)
                metadata["ModelVersion"] = modelTable.ModelVersion;
            if (modelTable.XmlSchemaUri != null)
                metadata["XmlSchemaUri"] = modelTable.XmlSchemaUri;
            if (nodeSet.ServerUris != null && nodeSet.ServerUris.Length > 0)
                metadata["ServerUris"] = JsonSerializer.SerializeToNode(nodeSet.ServerUris);
            if (nodeSet.LastModified != DateTime.MinValue)
                metadata["LastModified"] = nodeSet.LastModified.ToString("o");
            if (nodeSet.Extensions != null && nodeSet.Extensions.Length > 0)
            {
                var extArr = new JsonArray();
                foreach (var ext in nodeSet.Extensions)
                    extArr.Add(ext.OuterXml);
                if (extArr.Count > 0)
                    metadata["Extensions"] = extArr;
            }

            return metadata;
        }

        private static void RestoreMetadata(
            Opc.Ua.Export.UANodeSet nodeSet, JsonObject? metadata, Dictionary<string, int> uriToIdx)
        {
            if (metadata == null) return;

            // Restore Aliases with namespace indices updated to match reconstructed NamespaceUris
            if (metadata.TryGetPropertyValue("Aliases", out var aliasNode) && aliasNode is JsonObject aliasObj)
            {
                var aliasList = new List<Opc.Ua.Export.NodeIdAlias>();
                foreach (var kvp in aliasObj)
                {
                    var restoredValue = RestoreAttrNodeId(kvp.Value?.ToString(), uriToIdx);
                    aliasList.Add(new Opc.Ua.Export.NodeIdAlias
                    {
                        Alias = kvp.Key,
                        Value = restoredValue ?? ""
                    });
                }
                nodeSet.Aliases = aliasList.ToArray();
            }

            if (metadata.TryGetPropertyValue("ServerUris", out var suNode))
                nodeSet.ServerUris = suNode.Deserialize<string[]>(JsonOptions);

            if (metadata.TryGetPropertyValue("LastModified", out var lmNode) &&
                DateTime.TryParse(lmNode?.ToString(), out var lm))
                nodeSet.LastModified = lm;

            if (metadata.TryGetPropertyValue("Extensions", out var extNode) && extNode is JsonArray extArr)
            {
                var extensions = new List<XmlElement>();
                foreach (var extItem in extArr)
                {
                    var xmlStr = extItem?.ToString();
                    if (!string.IsNullOrEmpty(xmlStr))
                    {
                        // XmlResolver = null disables external entity/DTD resolution
                        // (defense-in-depth against XXE). The content is already
                        // DTD-prohibited upstream in UANodeSet.Read, but this parse
                        // path should not rely on that invariant holding forever.
                        var doc = new XmlDocument { XmlResolver = null };
                        doc.LoadXml(xmlStr);
                        if (doc.DocumentElement != null)
                            extensions.Add(doc.DocumentElement);
                    }
                }
                if (extensions.Count > 0)
                    nodeSet.Extensions = extensions.ToArray();
            }
        }

        /// <summary>
        /// Generate RequiredModels by looking up each namespace URI in the DB.
        /// UA core is always required. Other URIs come from NamespaceUris (excluding model's own).
        /// Falls back to stored metadata for version info when model is not in DB.
        /// </summary>
        private static async Task<Opc.Ua.Export.ModelTableEntry[]> GenerateRequiredModels(
            NodeSetEditorDbContext db, List<string> namespaceUris, string modelUri, JsonObject? metadata)
        {
            // Build fallback lookup from stored RequiredModels metadata
            var metadataFallback = new Dictionary<string, JsonObject>();
            if (metadata?.TryGetPropertyValue("RequiredModels", out var rmNode) == true && rmNode is JsonArray rmArr)
            {
                foreach (var item in rmArr)
                {
                    if (item is JsonObject rmObj)
                    {
                        var uri = rmObj["ModelUri"]?.ToString();
                        if (uri != null)
                            metadataFallback[uri] = rmObj;
                    }
                }
            }

            var required = new List<Opc.Ua.Export.ModelTableEntry>();

            // UA core is required unless this IS the core model
            var urisToResolve = new List<string>();
            if (modelUri != UaCoreNamespace)
                urisToResolve.Add(UaCoreNamespace);
            // Add all NamespaceUris except model's own
            urisToResolve.AddRange(namespaceUris.Where(u => u != modelUri && u != UaCoreNamespace));

            foreach (var uri in urisToResolve)
            {
                var entry = new Opc.Ua.Export.ModelTableEntry { ModelUri = uri };

                // Try DB lookup first — latest by normalized SemVer
                var dbModel = await db.Models
                    .Where(m => m.Uri == uri)
                    .OrderByDescending(m => m.VersionNorm)
                    .FirstOrDefaultAsync();

                if (dbModel != null)
                {
                    entry.Version = dbModel.Version;
                    if (!string.IsNullOrEmpty(dbModel.PublicationDate)
                        && DateTime.TryParse(dbModel.PublicationDate, out var dbPd))
                    {
                        entry.PublicationDate = dbPd.ToUniversalTime();
                        entry.PublicationDateSpecified = true;
                    }
                }
                else if (metadataFallback.TryGetValue(uri, out var fallback))
                {
                    // Fallback to stored metadata
                    if (fallback.TryGetPropertyValue("Version", out var vNode))
                        entry.Version = vNode?.ToString();
                    if (fallback.TryGetPropertyValue("PublicationDate", out var pdNode) &&
                        DateTime.TryParse(pdNode?.ToString(), out var pd2))
                    {
                        entry.PublicationDate = pd2.ToUniversalTime();
                        entry.PublicationDateSpecified = true;
                    }
                    if (fallback.TryGetPropertyValue("ModelVersion", out var mv2Node))
                        entry.ModelVersion = mv2Node?.ToString();
                    if (fallback.TryGetPropertyValue("XmlSchemaUri", out var xs2Node))
                        entry.XmlSchemaUri = xs2Node?.ToString();
                }

                required.Add(entry);
            }

            return required.ToArray();
        }

        #endregion

        #region Store Helpers

        private static int GetNodeClass(Opc.Ua.Export.UANode node) => node switch
        {
            Opc.Ua.Export.UAObjectType => NodeClassObjectType,
            Opc.Ua.Export.UAVariableType => NodeClassVariableType,
            Opc.Ua.Export.UAReferenceType => NodeClassReferenceType,
            Opc.Ua.Export.UADataType => NodeClassDataType,
            Opc.Ua.Export.UAObject => NodeClassObject,
            Opc.Ua.Export.UAVariable => NodeClassVariable,
            Opc.Ua.Export.UAMethod => NodeClassMethod,
            Opc.Ua.Export.UAView => NodeClassView,
            _ => throw new NotSupportedException($"Unknown UANode type: {node.GetType().Name}")
        };

        private static NodeAttributesBase BuildAttributes(
            Opc.Ua.Export.UANode node, Dictionary<string, string> aliases, string[] nsTable)
        {
            NodeAttributesBase attrs = node switch
            {
                Opc.Ua.Export.UAObjectType ot => new ObjectTypeAttributes
                {
                    IsAbstract = ot.IsAbstract ? true : null,
                },
                Opc.Ua.Export.UAVariableType vt => new VariableTypeAttributes
                {
                    IsAbstract = vt.IsAbstract ? true : null,
                    Value = XmlElementToJson(vt.Value),
                    DataType = vt.DataType != null ? FormatAttrNodeId(ResolveAlias(vt.DataType, aliases), nsTable) : null,
                    ValueRank = vt.ValueRank != -1 ? vt.ValueRank : null,
                    ArrayDimensions = vt.ArrayDimensions,
                },
                Opc.Ua.Export.UAReferenceType rt => new ReferenceTypeAttributes
                {
                    IsAbstract = rt.IsAbstract ? true : null,
                    InverseName = rt.InverseName != null
                        ? rt.InverseName.Select(ln => new LocalizedTextEntry { Locale = ln.Locale, Value = ln.Value }).ToList()
                        : null,
                    Symmetric = rt.Symmetric ? true : null,
                },
                Opc.Ua.Export.UADataType dt => new DataTypeAttributes
                {
                    IsAbstract = dt.IsAbstract ? true : null,
                    Definition = dt.Definition != null ? ConvertDefinition(dt.Definition, aliases, nsTable) : null,
                    Purpose = dt.Purpose != Opc.Ua.Export.DataTypePurpose.Normal
                        ? dt.Purpose.ToString()
                        : null,
                },
                Opc.Ua.Export.UAObject obj => new ObjectAttributes
                {
                    EventNotifier = obj.EventNotifier != 0 ? obj.EventNotifier : null,
                    DesignToolOnly = obj.DesignToolOnly ? true : null,
                },
                Opc.Ua.Export.UAVariable v => new VariableAttributes
                {
                    Value = XmlElementToJson(v.Value),
                    DataType = v.DataType != null ? FormatAttrNodeId(ResolveAlias(v.DataType, aliases), nsTable) : null,
                    ValueRank = v.ValueRank != -1 ? v.ValueRank : null,
                    ArrayDimensions = v.ArrayDimensions,
                    AccessLevel = v.AccessLevel != 1 ? (uint?)v.AccessLevel : null,
                    UserAccessLevel = v.UserAccessLevel != 1 ? (uint?)v.UserAccessLevel : null,
                    MinimumSamplingInterval = v.MinimumSamplingInterval != 0 ? v.MinimumSamplingInterval : null,
                    Historizing = v.Historizing ? true : null,
                    DesignToolOnly = v.DesignToolOnly ? true : null,
                },
                Opc.Ua.Export.UAMethod m => new MethodAttributes
                {
                    Executable = !m.Executable ? false : null,
                    UserExecutable = !m.UserExecutable ? false : null,
                    MethodDeclarationId = m.MethodDeclarationId != null
                        ? FormatAttrNodeId(m.MethodDeclarationId, nsTable)
                        : null,
                },
                Opc.Ua.Export.UAView view => new ViewAttributes
                {
                    ContainsNoLoops = view.ContainsNoLoops ? true : null,
                    EventNotifier = view.EventNotifier != 0 ? view.EventNotifier : null,
                },
                _ => throw new NotSupportedException($"Unknown UANode type: {node.GetType().Name}")
            };

            // Common attributes
            if (node.DisplayName != null && node.DisplayName.Length > 0)
                attrs.DisplayName = node.DisplayName.Select(ln => new LocalizedTextEntry { Locale = ln.Locale, Value = ln.Value }).ToList();

            if (node.Description != null && node.Description.Length > 0)
                attrs.Description = node.Description.Select(ln => new LocalizedTextEntry { Locale = ln.Locale, Value = ln.Value }).ToList();

            if (node.WriteMask != 0) attrs.WriteMask = node.WriteMask;
            if (node.UserWriteMask != 0) attrs.UserWriteMask = node.UserWriteMask;
            if (node.AccessRestrictionsSpecified) attrs.AccessRestrictions = node.AccessRestrictions;
            if (!string.IsNullOrEmpty(node.SymbolicName)) attrs.SymbolicName = node.SymbolicName;
            if (node.ReleaseStatus != Opc.Ua.Export.ReleaseStatus.Released)
                attrs.ReleaseStatus = node.ReleaseStatus.ToString();
            if (node.Category != null && node.Category.Length > 0) attrs.Category = node.Category;
            if (node.Documentation != null) attrs.Documentation = node.Documentation;
            if (node.RolePermissions != null && node.RolePermissions.Length > 0)
                attrs.RolePermissions = node.RolePermissions.Select(rp => new RolePermissionEntry
                {
                    Value = FormatAttrNodeId(rp.Value, nsTable),
                    Permissions = rp.Permissions != 0 ? rp.Permissions : null,
                }).ToList();

            return attrs;
        }

        private static DataTypeDefinitionEntry ConvertDefinition(
            Opc.Ua.Export.DataTypeDefinition def, Dictionary<string, string> aliases, string[] nsTable)
        {
            var entry = new DataTypeDefinitionEntry
            {
                Name = FormatAttrQualifiedName(def.Name, nsTable),
                SymbolicName = def.SymbolicName,
                IsUnion = def.IsUnion ? true : null,
                IsOptionSet = def.IsOptionSet ? true : null,
                BaseType = def.BaseType != null ? FormatAttrNodeId(ResolveAlias(def.BaseType, aliases), nsTable) : null,
            };

            if (def.Field != null && def.Field.Length > 0)
            {
                entry.Fields = def.Field.Select(f => new FieldDefinitionEntry
                {
                    Name = f.Name,
                    DataType = f.DataType != null ? FormatAttrNodeId(ResolveAlias(f.DataType, aliases), nsTable) : null,
                    ValueRank = f.ValueRank != -1 ? f.ValueRank : null,
                    ArrayDimensions = f.ArrayDimensions,
                    Value = f.Value != -1 ? f.Value : null,
                    IsOptional = f.IsOptional ? true : null,
                    AllowSubTypes = f.AllowSubTypes ? true : null,
                    SymbolicName = f.SymbolicName,
                    Description = f.Description != null && f.Description.Length > 0
                        ? f.Description.Select(ln => new LocalizedTextEntry { Locale = ln.Locale, Value = ln.Value }).ToList()
                        : null,
                }).ToList();
            }

            return entry;
        }

        private static string ResolveAlias(string value, Dictionary<string, string> aliases)
        {
            return aliases.TryGetValue(value, out var resolved) ? resolved : value;
        }

        #region Value XML ↔ JSON Conversion (Part 6)

        private const string UaTypesNamespace = "http://opcfoundation.org/UA/2008/02/Types.xsd";

        /// <summary>
        /// Convert an OPC UA Value XmlElement to its Part 6 JSON representation
        /// (Part 6 §5.4 reversible form). The result is the typed JSON value:
        /// primitives as native JSON, structures as <c>{UaTypeId, ...inline body fields}</c>,
        /// arrays as JSON arrays, etc. Routed through <c>Opc.Ua.NodeSetSerializer.Part6Variant</c>
        /// so the same converter implementation drives both the standalone NodeSetTool
        /// path and the DB-persisted path.
        /// </summary>
        private static JsonNode? XmlElementToJson(XmlElement? element)
        {
            if (element == null) return null;
            var jsonText = Part6Variant.ReadXmlValueAsJsonText(element);
            if (string.IsNullOrEmpty(jsonText)) return null;
            try { return JsonNode.Parse(jsonText); }
            catch { return null; }
        }

        /// <summary>
        /// Convert a Part 6 JSON Value (as produced by <see cref="XmlElementToJson"/>)
        /// back to an OPC UA Value XmlElement. When <paramref name="addressSpace"/> is
        /// provided (pass 1 of the two-pass build), the writer can resolve struct
        /// wrapper element names, distinguish QualifiedName/NodeId/DateTime from plain
        /// strings, and emit byte-stable XML. Without it, the schema-free fallback
        /// inlines body fields without the inner type-name wrapper and renders
        /// string-shaped built-ins as plain text.
        /// </summary>
        private static XmlElement? JsonToXmlElement(
            JsonNode? json, JsonAddressSpace? addressSpace = null,
            string? dataTypeNodeId = null, IReadOnlyList<string>? namespaceUris = null)
        {
            if (json == null) return null;
            var jsonText = json.ToJsonString(JsonOptions);
            return Part6Variant.WriteJsonTextValue(jsonText, addressSpace, dataTypeNodeId, namespaceUris);
        }

        #endregion

        private static JsonObject? SerializeAttributes(NodeAttributesBase attrs)
        {
            var json = JsonSerializer.Serialize<NodeAttributesBase>(attrs, JsonOptions);
            return JsonNode.Parse(json)?.AsObject();
        }

        #endregion

        #region NamespaceMetadata

        // Well-known OPC UA Core (ns=0) NodeIds used to wire up the NamespaceMetadata properties.
        private const string NamespaceMetadataTypeId = "i=11616";   // NamespaceMetadataType
        private const string PropertyTypeId = "i=68";
        private const string HasPropertyId = "i=46";
        private const string StringDataTypeId = "i=12";
        private const string DateTimeDataTypeId = "i=13";
        private const string UaTypesXsd = "http://opcfoundation.org/UA/2008/02/Types.xsd";
        private const int MetadataOrdinalBase = 100_000_000;

        /// <summary>
        /// Refresh the value properties (NamespaceUri / NamespaceVersion / NamespacePublicationDate /
        /// ModelVersion) of the model's standard OPC UA NamespaceMetadataType object so they track the
        /// model's URI / version / publication date.
        ///
        /// This method does NOT create the object: structural creation — the NamespaceMetadata object
        /// plus the full set of NamespaceMetadataType's mandatory children — is done by the
        /// instantiation engine (see UaRestApiController.EnsureNamespaceMetadataObjectAsync), the same
        /// code path used when a user instantiates any other type. A hand-built subset would omit
        /// mandatory children (IsNamespaceSubset, StaticNodeIdTypes, …). When no object exists yet,
        /// this is a no-op; callers after a version change / clone / import (whose NodeSet already
        /// carried a NamespaceMetadata object) get the value refresh.
        /// </summary>
        public static async Task SyncNamespaceMetadataAsync(NodeSetEditorDbContext db, Model model)
        {
            var uri = model.Uri;
            if (string.IsNullOrEmpty(uri)) return;

            var version = model.Version ?? string.Empty;
            var pubDate = !string.IsNullOrEmpty(model.PublicationDate)
                ? model.PublicationDate!
                : DateTime.UtcNow.ToString("o");
            var modelVersion = version;
            if (model.Metadata != null
                && model.Metadata.TryGetPropertyValue("ModelVersion", out var mv) && mv != null)
            {
                modelVersion = mv.ToString();
            }

            // Structural creation lives in the instantiation engine; here we only refresh values on
            // an object that already exists. If none exists yet, leave it to the engine path.
            var obj = await db.Nodes.FirstOrDefaultAsync(n =>
                n.ModelId == model.Id && n.TypeDefinitionId == NamespaceMetadataTypeId);
            if (obj == null) return;

            var objNodeId = obj.NodeId!;

            await UpsertMetadataPropertyAsync(db, model, objNodeId, "NamespaceUri", StringDataTypeId, "String", uri);
            await UpsertMetadataPropertyAsync(db, model, objNodeId, "NamespaceVersion", StringDataTypeId, "String", version);
            await UpsertMetadataPropertyAsync(db, model, objNodeId, "NamespacePublicationDate", DateTimeDataTypeId, "DateTime", pubDate);
            await UpsertMetadataPropertyAsync(db, model, objNodeId, "ModelVersion", StringDataTypeId, "String", modelVersion);

            await db.SaveChangesAsync();
        }

        private static async Task UpsertMetadataPropertyAsync(
            NodeSetEditorDbContext db, Model model, string objNodeId,
            string propName, string dataTypeId, string xsdType, string valueText)
        {
            var attrs = new VariableAttributes
            {
                DisplayName = new List<LocalizedTextEntry> { new() { Locale = string.Empty, Value = propName } },
                Value = ScalarValueJson(xsdType, valueText),
                DataType = dataTypeId,
            };
            var attrsJson = SerializeAttributes(attrs);

            // Locate the existing property by its LOCAL BrowseName. Engine-instantiated children
            // carry the fully-qualified ns=0 BrowseName ("nsu=http://opcfoundation.org/UA/;NamespaceUri"),
            // whereas a directly-authored one uses the bare local name — strip the prefix and compare
            // so either form matches and we never create a duplicate property. Candidates come from
            // ParentNodeId first, then the object's HasProperty references (imported NodeSets don't
            // always populate ParentNodeId).
            var candidates = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.ParentNodeId == objNodeId)
                .ToListAsync();
            if (candidates.Count == 0)
            {
                var propIds = await db.References
                    .Where(r => r.ModelId == model.Id && r.SourceNodeId == objNodeId
                        && r.ReferenceTypeId == HasPropertyId && r.IsForward)
                    .Select(r => r.TargetNodeId)
                    .ToListAsync();
                if (propIds.Count > 0)
                    candidates = await db.Nodes
                        .Where(n => n.ModelId == model.Id && propIds.Contains(n.NodeId))
                        .ToListAsync();
            }
            var existing = candidates.FirstOrDefault(n => LocalBrowseName(n.BrowseName) == propName);

            if (existing != null)
            {
                existing.Attributes = attrsJson; // reassign so EF flags the JSON column as modified
                if (string.IsNullOrEmpty(existing.TypeDefinitionId))
                    existing.TypeDefinitionId = PropertyTypeId;
                return;
            }

            var propNodeId = $"nsu={model.Uri};s=NamespaceMetadata.{propName}";
            db.Nodes.Add(new Node
            {
                ModelId = model.Id,
                NodeId = propNodeId,
                ParentNodeId = objNodeId,
                NodeClass = 2, // Variable
                BrowseName = propName, // ns=0 standard property BrowseName
                DisplayName = propName,
                TypeDefinitionId = PropertyTypeId,
                Attributes = attrsJson,
                Ordinal = MetadataOrdinalBase + propName.Length,
            });
            db.References.AddRange(
                new Reference { ModelId = model.Id, SourceNodeId = objNodeId, ReferenceTypeId = HasPropertyId, IsForward = true, TargetNodeId = propNodeId, Ordinal = MetadataOrdinalBase + 10 },
                new Reference { ModelId = model.Id, SourceNodeId = propNodeId, ReferenceTypeId = HasTypeDefinitionId, IsForward = true, TargetNodeId = PropertyTypeId, Ordinal = MetadataOrdinalBase + 11 });
        }

        /// <summary>Return a BrowseName's local part, stripping any "nsu=...;" namespace prefix.</summary>
        private static string LocalBrowseName(string? browseName)
        {
            if (string.IsNullOrEmpty(browseName)) return string.Empty;
            var semi = browseName.IndexOf(';');
            return semi >= 0 ? browseName[(semi + 1)..] : browseName;
        }

        /// <summary>Build a Part 6 JSON scalar Value from a uax-typed element (e.g. String, DateTime).</summary>
        private static JsonNode? ScalarValueJson(string xsdLocalName, string text)
        {
            var doc = new System.Xml.XmlDocument();
            var el = doc.CreateElement("uax", xsdLocalName, UaTypesXsd);
            el.InnerText = text ?? string.Empty;
            return XmlElementToJson(el);
        }

        #endregion

        #region Load Helpers

        private static NodeAttributesBase? DeserializeAttributes(JsonObject? json)
        {
            if (json == null) return null;
            var rawJson = json.ToJsonString(JsonOptions);
            return JsonSerializer.Deserialize<NodeAttributesBase>(rawJson, JsonOptions);
        }

        private static Opc.Ua.Export.UANode CreateUANode(int nodeClass) => nodeClass switch
        {
            NodeClassObjectType => new Opc.Ua.Export.UAObjectType(),
            NodeClassVariableType => new Opc.Ua.Export.UAVariableType(),
            NodeClassReferenceType => new Opc.Ua.Export.UAReferenceType(),
            NodeClassDataType => new Opc.Ua.Export.UADataType(),
            NodeClassObject => new Opc.Ua.Export.UAObject(),
            NodeClassVariable => new Opc.Ua.Export.UAVariable(),
            NodeClassMethod => new Opc.Ua.Export.UAMethod(),
            NodeClassView => new Opc.Ua.Export.UAView(),
            _ => throw new NotSupportedException($"Unknown NodeClass: {nodeClass}")
        };

        private static void PopulateCommonAttributes(
            Opc.Ua.Export.UANode uaNode, NodeAttributesBase? attrs, Dictionary<string, int> uriToIdx)
        {
            if (attrs == null) return;

            if (attrs.DisplayName != null)
                uaNode.DisplayName = attrs.DisplayName.Select(ln => new Opc.Ua.Export.LocalizedText { Locale = ln.Locale, Value = ln.Value }).ToArray();

            if (attrs.Description != null)
                uaNode.Description = attrs.Description.Select(ln => new Opc.Ua.Export.LocalizedText { Locale = ln.Locale, Value = ln.Value }).ToArray();

            if (attrs.WriteMask.HasValue) uaNode.WriteMask = attrs.WriteMask.Value;
            if (attrs.UserWriteMask.HasValue) uaNode.UserWriteMask = attrs.UserWriteMask.Value;
            if (attrs.AccessRestrictions.HasValue)
            {
                uaNode.AccessRestrictions = attrs.AccessRestrictions.Value;
                uaNode.AccessRestrictionsSpecified = true;
            }
            if (attrs.SymbolicName != null) uaNode.SymbolicName = attrs.SymbolicName;
            if (attrs.ReleaseStatus != null && Enum.TryParse<Opc.Ua.Export.ReleaseStatus>(attrs.ReleaseStatus, out var rs))
                uaNode.ReleaseStatus = rs;
            if (attrs.Category != null) uaNode.Category = attrs.Category;
            if (attrs.Documentation != null) uaNode.Documentation = attrs.Documentation;
            if (attrs.RolePermissions != null)
                uaNode.RolePermissions = attrs.RolePermissions.Select(rp => new Opc.Ua.Export.RolePermission
                {
                    Value = RestoreAttrNodeId(rp.Value, uriToIdx),
                    Permissions = rp.Permissions ?? 0,
                }).ToArray();
        }

        private static void PopulateSubtypeAttributes(
            Opc.Ua.Export.UANode uaNode, int nodeClass, NodeAttributesBase? attrs,
            Dictionary<string, int> uriToIdx,
            JsonAddressSpace? addressSpace = null,
            IReadOnlyList<string>? namespaceUris = null)
        {
            if (attrs == null) return;

            switch (uaNode)
            {
                case Opc.Ua.Export.UAObjectType ot when attrs is ObjectTypeAttributes otAttrs:
                    ot.IsAbstract = otAttrs.IsAbstract ?? false;
                    break;

                case Opc.Ua.Export.UAVariableType vt when attrs is VariableTypeAttributes vtAttrs:
                    vt.IsAbstract = vtAttrs.IsAbstract ?? false;
                    vt.DataType = RestoreAttrNodeId(vtAttrs.DataType, uriToIdx);
                    vt.Value = JsonToXmlElement(vtAttrs.Value, addressSpace, vtAttrs.DataType, namespaceUris);
                    vt.ValueRank = vtAttrs.ValueRank ?? -1;
                    vt.ArrayDimensions = vtAttrs.ArrayDimensions;
                    break;

                case Opc.Ua.Export.UAReferenceType rt when attrs is ReferenceTypeAttributes rtAttrs:
                    rt.IsAbstract = rtAttrs.IsAbstract ?? false;
                    if (rtAttrs.InverseName != null)
                        rt.InverseName = rtAttrs.InverseName.Select(ln => new Opc.Ua.Export.LocalizedText { Locale = ln.Locale, Value = ln.Value }).ToArray();
                    rt.Symmetric = rtAttrs.Symmetric ?? false;
                    break;

                case Opc.Ua.Export.UADataType dt when attrs is DataTypeAttributes dtAttrs:
                    dt.IsAbstract = dtAttrs.IsAbstract ?? false;
                    if (dtAttrs.Definition != null)
                        dt.Definition = ReconstructDefinition(dtAttrs.Definition, uriToIdx);
                    if (dtAttrs.Purpose != null && Enum.TryParse<Opc.Ua.Export.DataTypePurpose>(dtAttrs.Purpose, out var purpose))
                        dt.Purpose = purpose;
                    break;

                case Opc.Ua.Export.UAObject obj when attrs is ObjectAttributes objAttrs:
                    obj.EventNotifier = objAttrs.EventNotifier ?? 0;
                    obj.DesignToolOnly = objAttrs.DesignToolOnly ?? false;
                    break;

                case Opc.Ua.Export.UAVariable v when attrs is VariableAttributes vAttrs:
                    v.DataType = RestoreAttrNodeId(vAttrs.DataType, uriToIdx);
                    v.Value = JsonToXmlElement(vAttrs.Value, addressSpace, vAttrs.DataType, namespaceUris);
                    v.ValueRank = vAttrs.ValueRank ?? -1;
                    v.ArrayDimensions = vAttrs.ArrayDimensions;
                    v.AccessLevel = (uint)(vAttrs.AccessLevel ?? 1);
                    v.UserAccessLevel = (uint)(vAttrs.UserAccessLevel ?? 1);
                    v.MinimumSamplingInterval = vAttrs.MinimumSamplingInterval ?? 0;
                    v.Historizing = vAttrs.Historizing ?? false;
                    v.DesignToolOnly = vAttrs.DesignToolOnly ?? false;
                    break;

                case Opc.Ua.Export.UAMethod m when attrs is MethodAttributes mAttrs:
                    m.Executable = mAttrs.Executable ?? true;
                    m.UserExecutable = mAttrs.UserExecutable ?? true;
                    m.MethodDeclarationId = RestoreAttrNodeId(mAttrs.MethodDeclarationId, uriToIdx);
                    break;

                case Opc.Ua.Export.UAView view when attrs is ViewAttributes viewAttrs:
                    view.ContainsNoLoops = viewAttrs.ContainsNoLoops ?? false;
                    view.EventNotifier = viewAttrs.EventNotifier ?? 0;
                    break;
            }
        }

        private static Opc.Ua.Export.DataTypeDefinition ReconstructDefinition(
            DataTypeDefinitionEntry entry, Dictionary<string, int> uriToIdx)
        {
            var def = new Opc.Ua.Export.DataTypeDefinition
            {
                Name = RestoreAttrQualifiedName(entry.Name, uriToIdx),
                SymbolicName = entry.SymbolicName,
                IsUnion = entry.IsUnion ?? false,
                IsOptionSet = entry.IsOptionSet ?? false,
                BaseType = RestoreAttrNodeId(entry.BaseType, uriToIdx),
            };

            if (entry.Fields != null)
            {
                def.Field = entry.Fields.Select(f =>
                {
                    var field = new Opc.Ua.Export.DataTypeField
                    {
                        Name = f.Name,
                        DataType = RestoreAttrNodeId(f.DataType, uriToIdx),
                        ValueRank = f.ValueRank ?? -1,
                        ArrayDimensions = f.ArrayDimensions,
                        Value = f.Value ?? -1,
                        IsOptional = f.IsOptional ?? false,
                        AllowSubTypes = f.AllowSubTypes ?? false,
                        SymbolicName = f.SymbolicName,
                    };
                    if (f.Description != null)
                        field.Description = f.Description.Select(ln => new Opc.Ua.Export.LocalizedText { Locale = ln.Locale, Value = ln.Value }).ToArray();
                    return field;
                }).ToArray();
            }

            return def;
        }

        #endregion
    }
}
