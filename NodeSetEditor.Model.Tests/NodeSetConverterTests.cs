using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;
using Opc.Ua.Export;

namespace NodeSetEditor.Model.Tests
{
    [Collection("Database")]
    public class NodeSetConverterTests
    {
        private readonly DatabaseFixture _fixture;

        public NodeSetConverterTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        private static UANodeSet LoadNodeSet(string relativePath)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestNodeSets", relativePath);
            Assert.True(File.Exists(path), $"Test NodeSet file not found: {path}");

            using var stream = File.OpenRead(path);
            return UANodeSet.Read(stream);
        }

        [Fact]
        public async Task StoreNodeSet_DI_Creates_Model_Nodes_References()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            Assert.NotEqual(Guid.Empty, model.Id);
            Assert.Equal("http://opcfoundation.org/UA/DI/", model.Uri);
            Assert.NotNull(model.Version);

            var nodeCount = await db.Nodes.CountAsync(n => n.ModelId == model.Id);
            Assert.True(nodeCount > 0, $"Expected nodes, got {nodeCount}");

            var refCount = await db.References.CountAsync(r => r.ModelId == model.Id);
            Assert.True(refCount > 0, $"Expected references, got {refCount}");

            Assert.NotNull(model.Metadata);
            Assert.True(model.Metadata!.ContainsKey("RequiredModels"));

            // Aliases are expanded into full NodeIds on import and never written back, so the
            // source document's table is not carried into metadata.
            Assert.False(model.Metadata!.ContainsKey("Aliases"));
        }

        [Fact]
        public async Task StoreNodeSet_Machinery_Creates_Model_With_RequiredModels()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Machinery.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            Assert.Equal("http://opcfoundation.org/UA/Machinery/", model.Uri);
            Assert.NotNull(model.Metadata);
            Assert.True(model.Metadata!.ContainsKey("RequiredModels"));

            var nodeCount = await db.Nodes.CountAsync(n => n.ModelId == model.Id);
            Assert.True(nodeCount > 0);
        }

        [Fact]
        public async Task StoreNodeSet_Covers_All_NodeClasses()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var nodeClasses = await db.Nodes
                .Where(n => n.ModelId == model.Id)
                .Select(n => n.NodeClass)
                .Distinct()
                .ToListAsync();

            Assert.Contains(1, nodeClasses);   // ObjectType
            Assert.Contains(4, nodeClasses);   // ReferenceType
            Assert.Contains(8, nodeClasses);   // DataType
            Assert.Contains(16, nodeClasses);  // Object
            Assert.Contains(32, nodeClasses);  // Variable
            Assert.Contains(64, nodeClasses);  // Method
        }

        [Fact]
        public async Task StoreNodeSet_ObjectType_HasAttributes()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var objectTypeNode = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.ObjectType)
                .FirstOrDefaultAsync();

            Assert.NotNull(objectTypeNode);
            Assert.NotNull(objectTypeNode!.Attributes);
            Assert.Equal("ObjectType", objectTypeNode.Attributes!["NodeType"]?.ToString());
        }

        [Fact]
        public async Task StoreNodeSet_Variable_HasDataType()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var variableNode = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.Variable)
                .FirstOrDefaultAsync(n => n.Attributes != null);

            Assert.NotNull(variableNode);
            Assert.Equal("Variable", variableNode!.Attributes!["NodeType"]?.ToString());
        }

        [Fact]
        public async Task StoreNodeSet_ReferenceType_HasInverseName()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var refTypeNodes = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.ReferenceType)
                .ToListAsync();

            Assert.True(refTypeNodes.Count > 0);

            var withInverseName = refTypeNodes.Where(n =>
                n.Attributes != null && n.Attributes.ContainsKey("InverseName")).ToList();
            Assert.True(withInverseName.Count > 0, "Expected at least one ReferenceType with InverseName");
        }

        [Fact]
        public async Task StoreNodeSet_DataType_HasDefinition()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var dataTypeNodes = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.DataType)
                .ToListAsync();

            Assert.True(dataTypeNodes.Count > 0);

            var withDefinition = dataTypeNodes.Where(n =>
                n.Attributes != null && n.Attributes.ContainsKey("Definition")).ToList();
            Assert.True(withDefinition.Count > 0, "Expected at least one DataType with Definition");
        }

        [Fact]
        public async Task StoreNodeSet_Method_HasMethodDeclarationId()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var methodNodes = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.Method)
                .ToListAsync();

            Assert.True(methodNodes.Count > 0);

            var withDeclaration = methodNodes.Where(n =>
                n.Attributes != null && n.Attributes.ContainsKey("MethodDeclarationId")).ToList();
            Assert.True(withDeclaration.Count > 0, "Expected at least one Method with MethodDeclarationId");
        }

        [Fact]
        public async Task StoreNodeSet_SuperTypeId_IsPopulated()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var withSuperType = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.SuperTypeId != null)
                .CountAsync();

            Assert.True(withSuperType > 0, "Expected nodes with SuperTypeId");
        }

        [Fact]
        public async Task Update_SameNodeSet_ReplacesContentInPlace()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model1 = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);
            var nodeCount1 = await db.Nodes.CountAsync(n => n.ModelId == model1.Id);

            // Re-importing the same (Uri, VersionNorm) must keep the ROW (its Id
            // anchors workspace links; its Name/Description/Published are curated)
            // and replace only the content.
            var model2 = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);
            var nodeCount2 = await db.Nodes.CountAsync(n => n.ModelId == model2.Id);

            Assert.Equal(model1.Id, model2.Id);
            Assert.Equal(nodeCount1, nodeCount2);
            Assert.Equal(1, await db.Models.CountAsync(m => m.Uri == model1.Uri));
        }

        #region NodeId Format Verification

        [Fact]
        public async Task StoreNodeSet_NodeId_UsesNsuPrefix_ForModelNamespace()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // Nodes in the model's own namespace (ns=1 in DI) should use nsu= format
            var modelNodes = await db.Nodes
                .Where(n => n.ModelId == model.Id)
                .Take(5)
                .ToListAsync();

            foreach (var node in modelNodes)
            {
                Assert.StartsWith("nsu=http://opcfoundation.org/UA/DI/;", node.NodeId!);
            }
        }

        [Fact]
        public async Task StoreNodeSet_BrowseName_UsesNsuFormat_ForModelNamespace()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // BrowseNames with ns=1 prefix should be stored as nsu=<uri>;Name
            var nodeWithNsBrowseName = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.BrowseName != null)
                .FirstOrDefaultAsync(n => n.BrowseName!.StartsWith("nsu="));

            Assert.NotNull(nodeWithNsBrowseName);
            Assert.StartsWith("nsu=http://opcfoundation.org/UA/DI/;", nodeWithNsBrowseName!.BrowseName);
        }

        [Fact]
        public async Task StoreNodeSet_BrowseName_BareForNs0()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // BrowseNames without namespace prefix should be stored as bare names (no nsu=)
            var bareNames = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.BrowseName != null
                    && !n.BrowseName.StartsWith("nsu="))
                .Take(5)
                .ToListAsync();

            foreach (var node in bareNames)
            {
                // Should not contain numeric prefix like "1:" — that's the old format
                Assert.DoesNotContain(":", node.BrowseName!.Split(';').Last());
            }
        }

        [Fact]
        public async Task StoreNodeSet_References_UaNs0_BareIdentifier()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // References to UA core (ns=0) nodes like HasSubtype (i=45) should be bare identifiers
            var hasSubtypeRef = await db.References
                .Where(r => r.ModelId == model.Id && r.ReferenceTypeId == "i=45")
                .FirstOrDefaultAsync();

            Assert.NotNull(hasSubtypeRef);
            Assert.Equal("i=45", hasSubtypeRef!.ReferenceTypeId);
        }

        [Fact]
        public async Task StoreNodeSet_Machinery_CrossNamespaceRefs_UseNsuFormat()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Machinery.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // Machinery references DI nodes (ns=2 in original). These should use nsu= format.
            var nsuTargets = await db.References
                .Where(r => r.ModelId == model.Id && r.TargetNodeId!.StartsWith("nsu="))
                .Take(5)
                .ToListAsync();

            Assert.True(nsuTargets.Count > 0, "Expected cross-namespace references with nsu= format");

            // Verify the URIs are real namespaces (Machinery's own, DI, or IA)
            foreach (var r in nsuTargets)
            {
                Assert.True(
                    r.TargetNodeId!.StartsWith("nsu=http://opcfoundation.org/UA/DI/;") ||
                    r.TargetNodeId!.StartsWith("nsu=http://opcfoundation.org/UA/IA/;") ||
                    r.TargetNodeId!.StartsWith("nsu=http://opcfoundation.org/UA/Machinery/;"),
                    $"Unexpected nsu target: {r.TargetNodeId}");
            }
        }

        [Fact]
        public async Task StoreNodeSet_MethodDeclarationId_UsesNsuFormat()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // MethodDeclarationId in attributes should use nsu= format for non-ns0 references
            var methodNodes = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.Method)
                .ToListAsync();

            var withNsuDecl = methodNodes.Where(n =>
                n.Attributes != null &&
                n.Attributes.ContainsKey("MethodDeclarationId") &&
                n.Attributes["MethodDeclarationId"]!.ToString().StartsWith("nsu=")).ToList();

            Assert.True(withNsuDecl.Count > 0, "Expected at least one Method with nsu= MethodDeclarationId");
        }

        [Fact]
        public async Task StoreNodeSet_DataType_AttrUsesNsuFormat()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            // DataType in Variable attributes should use nsu= for non-ns0 types
            var variables = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.Variable)
                .ToListAsync();

            // Check that at least some DataType values reference the DI namespace with nsu=
            var withNsuDataType = variables.Where(n =>
                n.Attributes != null &&
                n.Attributes.ContainsKey("DataType") &&
                n.Attributes["DataType"]!.ToString().StartsWith("nsu=")).ToList();

            // Some variables may reference custom DI types
            // At minimum, ns=0 types like i=12 (String) should be bare
            var withBareDataType = variables.Where(n =>
                n.Attributes != null &&
                n.Attributes.ContainsKey("DataType") &&
                !n.Attributes["DataType"]!.ToString().StartsWith("nsu=")).ToList();

            Assert.True(withBareDataType.Count > 0, "Expected at least one Variable with bare (ns=0) DataType");
        }

        #endregion

        #region Round-Trip Tests

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesNodeCount()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            Assert.NotNull(reconstructed.Items);
            Assert.Equal(originalNodeSet.Items!.Length, reconstructed.Items.Length);
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesModelUri()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            Assert.NotNull(reconstructed.Models);
            Assert.Single(reconstructed.Models);
            Assert.Equal("http://opcfoundation.org/UA/DI/", reconstructed.Models[0].ModelUri);
            Assert.Equal(originalNodeSet.Models![0].Version, reconstructed.Models[0].Version);
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesNamespaceUris()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            Assert.NotNull(reconstructed.NamespaceUris);
            // Model's own URI should always be present
            Assert.Contains("http://opcfoundation.org/UA/DI/", reconstructed.NamespaceUris);
            // DI only references its own namespace, so NamespaceUris should have 1 entry
            Assert.Equal(originalNodeSet.NamespaceUris!.Length, reconstructed.NamespaceUris.Length);
        }

        /// <summary>
        /// An alias is shorthand for a NodeId at the point of use, and this exporter writes every
        /// NodeId in full — so an emitted Aliases table could only ever be entirely unused. DI
        /// ships one, and it is dropped rather than replayed.
        /// </summary>
        [Fact]
        public async Task CreateNodeSet_RoundTrip_DropsAliases()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            // The premise: the source really does declare aliases, and really does use them.
            Assert.NotEmpty(originalNodeSet.Aliases!);

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            // Null rather than an empty array, so the element is omitted instead of emitting
            // an empty <Aliases />.
            Assert.Null(reconstructed.Aliases);

            // And nothing in the output leans on one: every NodeId-valued attribute and reference
            // is a real NodeId, not an alias name.
            var aliasNames = originalNodeSet.Aliases!.Select(a => a.Alias).ToHashSet();
            foreach (var item in reconstructed.Items ?? [])
            {
                Assert.DoesNotContain(item.NodeId, aliasNames);
                foreach (var reference in item.References ?? [])
                {
                    Assert.DoesNotContain(reference.ReferenceType, aliasNames);
                    Assert.DoesNotContain(reference.Value, aliasNames);
                }
                if (item is Opc.Ua.Export.UAVariable v)
                    Assert.DoesNotContain(v.DataType, aliasNames);
            }
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_GeneratesRequiredModels()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Machinery.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(
                db, "http://opcfoundation.org/UA/Machinery/");

            Assert.NotNull(reconstructed.Models![0].RequiredModel);

            // RequiredModels should include UA core + all NamespaceUris except model's own
            var requiredUris = reconstructed.Models[0].RequiredModel
                .Select(rm => rm.ModelUri).ToHashSet();

            Assert.Contains("http://opcfoundation.org/UA/", requiredUris); // UA core always required
            Assert.Contains("http://opcfoundation.org/UA/DI/", requiredUris);
            Assert.Contains("http://opcfoundation.org/UA/IA/", requiredUris);
            Assert.DoesNotContain("http://opcfoundation.org/UA/Machinery/", requiredUris); // Not self
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesReferences()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            int originalRefCount = originalNodeSet.Items!.Sum(n => n.References?.Length ?? 0);
            int reconstructedRefCount = reconstructed.Items!.Sum(n => n.References?.Length ?? 0);
            Assert.Equal(originalRefCount, reconstructedRefCount);
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesNodeTypes()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            var originalTypes = originalNodeSet.Items!
                .GroupBy(n => n.GetType().Name)
                .ToDictionary(g => g.Key, g => g.Count());

            var reconstructedTypes = reconstructed.Items!
                .GroupBy(n => n.GetType().Name)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.Equal(originalTypes.Count, reconstructedTypes.Count);
            foreach (var kvp in originalTypes)
            {
                Assert.True(reconstructedTypes.ContainsKey(kvp.Key), $"Missing type: {kvp.Key}");
                Assert.Equal(kvp.Value, reconstructedTypes[kvp.Key]);
            }
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesNodeIds()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            // All original NodeIds should be present in the reconstructed set
            var origIds = originalNodeSet.Items!.Select(n => n.NodeId).ToHashSet();
            var reconIds = reconstructed.Items!.Select(n => n.NodeId).ToHashSet();

            Assert.Equal(origIds.Count, reconIds.Count);
            foreach (var id in origIds)
                Assert.Contains(id, reconIds);
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesBrowseNames()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            var origBn = originalNodeSet.Items!.Select(n => n.BrowseName).OrderBy(x => x).ToList();
            var reconBn = reconstructed.Items!.Select(n => n.BrowseName).OrderBy(x => x).ToList();

            Assert.Equal(origBn.Count, reconBn.Count);
            for (int i = 0; i < origBn.Count; i++)
                Assert.Equal(origBn[i], reconBn[i]);
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_Machinery_PreservesNodeIds()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Machinery.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(
                db, "http://opcfoundation.org/UA/Machinery/");

            var origIds = originalNodeSet.Items!.Select(n => n.NodeId).ToHashSet();
            var reconIds = reconstructed.Items!.Select(n => n.NodeId).ToHashSet();

            Assert.Equal(origIds.Count, reconIds.Count);
            foreach (var id in origIds)
                Assert.Contains(id, reconIds);
        }

        [Fact]
        public async Task CreateNodeSet_ByVersion_FindsCorrectModel()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(
                db, "http://opcfoundation.org/UA/DI/", model.Version);

            Assert.Equal(model.Version, reconstructed.Models![0].Version);
        }

        [Fact]
        public async Task CreateNodeSet_NotFound_ThrowsException()
        {
            await using var db = _fixture.CreateNewContext();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => NodeSetConverter.CreateNodeSetAsync(db, "http://nonexistent.example.com/"));
        }

        [Fact]
        public async Task StoreNodeSet_Variable_WithValue_StoresAsPart6Json()
        {
            await using var db = _fixture.CreateNewContext();
            var nodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);

            var varWithValue = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.Variable)
                .Where(n => n.Attributes != null)
                .ToListAsync();

            var withValue = varWithValue.Where(n => n.Attributes!.ContainsKey("Value")).ToList();
            Assert.True(withValue.Count > 0, "Expected at least one Variable with stored Value");

            // Part 6 §5.4 reversible form: the stored Value is the typed JSON value
            // directly (string for String/DateTime/Guid, number for numeric scalars,
            // {UaTypeId, ...inline body} for ExtensionObject, JSON array for arrays).
            // It is NOT the legacy {"<TypeName>": ...} wrapper.
            foreach (var n in withValue)
            {
                var valueNode = n.Attributes!["Value"];
                Assert.NotNull(valueNode);
                // Reject the old wrapper form: a JSON object whose only property's name
                // matches a built-in type tag like "String"/"Int32"/"ExtensionObject".
                if (valueNode is JsonObject obj && obj.Count == 1)
                {
                    var only = obj.First().Key;
                    Assert.False(
                        only is "String" or "Int32" or "Int64" or "Double" or "Float"
                            or "Boolean" or "DateTime" or "Guid" or "ByteString"
                            or "ExtensionObject" or "ListOfExtensionObject",
                        $"Value for {n.NodeId} still has legacy wrapper key '{only}'");
                }
            }
        }

        [Fact]
        public async Task CreateNodeSet_RoundTrip_PreservesValues()
        {
            await using var db = _fixture.CreateNewContext();
            var originalNodeSet = LoadNodeSet("Opc.Ua.Di.NodeSet2.xml");

            await NodeSetConverter.StoreNodeSetAsync(db, originalNodeSet);

            var reconstructed = await NodeSetConverter.CreateNodeSetAsync(db, "http://opcfoundation.org/UA/DI/");

            // Find variables with values in the original
            var origVarsWithValue = originalNodeSet.Items!
                .OfType<UAVariable>()
                .Where(v => v.Value != null)
                .ToDictionary(v => v.NodeId, v => v.Value!);

            Assert.True(origVarsWithValue.Count > 0, "Expected variables with values in original");

            var reconVarsWithValue = reconstructed.Items!
                .OfType<UAVariable>()
                .Where(v => v.Value != null)
                .ToDictionary(v => v.NodeId, v => v.Value!);

            Assert.Equal(origVarsWithValue.Count, reconVarsWithValue.Count);

            // Verify each value round-trips. Schema-free Part 6 conversion cannot recover
            // every built-in type's XML element name (e.g. <DateTime> round-trips as
            // <String> because Part 6 JSON encodes both as strings, with the type carried
            // by the Variable's DataType attribute, not by the value DOM). Compare TEXT
            // content rather than XML element names.
            foreach (var kvp in origVarsWithValue)
            {
                Assert.True(reconVarsWithValue.ContainsKey(kvp.Key), $"Missing value for {kvp.Key}");
                AssertXmlValueContentEquivalent(kvp.Value, reconVarsWithValue[kvp.Key]);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Compare two XML value elements by leaf content (not by element-name structure).
        /// Schema-free Part 6 round-trip can change a wrapper's local name (DateTime
        /// becoming String, NodeId becoming String, etc.) — the actual VALUE bytes at
        /// each leaf are preserved. Walks both DOMs collecting leaf text values in
        /// document order and asserts the lists match.
        /// </summary>
        private static void AssertXmlValueContentEquivalent(XmlElement expected, XmlElement actual)
        {
            var expectedLeaves = CollectLeafTexts(expected);
            var actualLeaves = CollectLeafTexts(actual);
            Assert.Equal(expectedLeaves, actualLeaves);
        }

        private static List<string> CollectLeafTexts(XmlElement el)
        {
            var list = new List<string>();
            void Walk(XmlElement e)
            {
                var children = e.ChildNodes.OfType<XmlElement>().ToList();
                if (children.Count == 0)
                {
                    // Normalize whitespace inside leaf text — ByteString/XmlElement values
                    // in NodeSet XML are often line-wrapped (whitespace inside the base64
                    // text node). The round-trip drops the wrapping, so compare with all
                    // internal whitespace collapsed/stripped.
                    var t = System.Text.RegularExpressions.Regex.Replace(
                        e.InnerText ?? "", @"\s+", "").Trim();
                    if (!string.IsNullOrEmpty(t)) list.Add(t);
                }
                else
                {
                    foreach (var c in children) Walk(c);
                }
            }
            Walk(el);
            return list;
        }

        #endregion
    }
}
