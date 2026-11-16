using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// <see cref="NodeSetConverter.SyncNamespaceMetadataAsync"/> creates any of the four standard
    /// NamespaceMetadata value properties its source NodeSet was missing. It used to give them a
    /// synthesized string NodeId ("nsu={uri};s=NamespaceMetadata.ModelVersion") — a deterministic
    /// id needs no allocator, but it left a string NodeId sitting among numeric siblings in the
    /// exported NodeSet. It showed up in the wild on models that predate ModelVersion being
    /// mandatory: every other property was already in the file and kept its numeric id, so
    /// ModelVersion alone came out as "ns=1;s=NamespaceMetadata.ModelVersion".
    /// </summary>
    [Collection("Database")]
    public class NamespaceMetadataPropertyIdTests
    {
        /// <summary>NamespaceMetadataType — the type definition that marks the namespace object.</summary>
        private const string NamespaceMetadataTypeId = "i=11616";
        private const string PropertyTypeId = "i=68";
        private const string HasTypeDefinition = "i=40";
        private const string HasProperty = "i=46";

        /// <summary>The NamespaceMetadata object's own id, as authored in the source NodeSet.</summary>
        private const string MetadataObjectId = "ns=1;i=5000";

        private static readonly string[] AllPropertyNames =
            ["NamespaceUri", "NamespaceVersion", "NamespacePublicationDate", "ModelVersion"];

        private readonly DatabaseFixture _fixture;

        public NamespaceMetadataPropertyIdTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// A nodeset with a NamespaceMetadata object that already carries the named properties —
        /// so passing none reproduces a model whose metadata properties all have to be created.
        /// </summary>
        private static Opc.Ua.Export.UANodeSet BuildNodeSet(string uri, params string[] presentProperties)
        {
            var items = new List<Opc.Ua.Export.UANode>
            {
                new Opc.Ua.Export.UAObject
                {
                    NodeId = MetadataObjectId,
                    BrowseName = "1:NamespaceMetadata",
                    DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "NamespaceMetadata" }],
                    References =
                    [
                        new Opc.Ua.Export.Reference
                        {
                            ReferenceType = HasTypeDefinition, IsForward = true, Value = NamespaceMetadataTypeId,
                        },
                    ],
                },
            };

            // Numeric ids, contiguous with the object's — the shape a published NodeSet has.
            var nextId = 5001;
            foreach (var propName in presentProperties)
            {
                items.Add(new Opc.Ua.Export.UAVariable
                {
                    NodeId = $"ns=1;i={nextId++}",
                    BrowseName = propName,
                    DisplayName = [new Opc.Ua.Export.LocalizedText { Value = propName }],
                    ParentNodeId = MetadataObjectId,
                    DataType = "i=12",
                    References =
                    [
                        new Opc.Ua.Export.Reference
                        {
                            ReferenceType = HasTypeDefinition, IsForward = true, Value = PropertyTypeId,
                        },
                        new Opc.Ua.Export.Reference
                        {
                            ReferenceType = HasProperty, IsForward = false, Value = MetadataObjectId,
                        },
                    ],
                });
            }

            return new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = [uri],
                Models =
                [
                    new Opc.Ua.Export.ModelTableEntry
                    {
                        ModelUri = uri,
                        Version = "1.0.0",
                        ModelVersion = "1.0.0",
                        PublicationDate = DateTime.UtcNow,
                        PublicationDateSpecified = true,
                    },
                ],
                Items = [.. items],
            };
        }

        private static async Task<List<Node>> MetadataPropertiesAsync(
            NodeSetEditorDbContext db, global::NodeSetEditor.Model.Model model)
        {
            var objNodeId = $"nsu={model.Uri};i=5000";
            return await db.Nodes.AsNoTracking()
                .Where(n => n.ModelId == model.Id && n.ParentNodeId == objNodeId)
                .ToListAsync();
        }

        private static string LocalName(string? browseName)
        {
            if (string.IsNullOrEmpty(browseName)) return string.Empty;
            var semi = browseName.IndexOf(';');
            return semi >= 0 ? browseName[(semi + 1)..] : browseName;
        }

        [Fact]
        public async Task CreatedProperties_GetNumericNodeIds()
        {
            var uri = $"urn:test:nsmeta:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            // Nothing but the object itself, so all four properties are created.
            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            var props = await MetadataPropertiesAsync(db, model);
            Assert.Equal(
                AllPropertyNames.OrderBy(n => n),
                props.Select(p => LocalName(p.BrowseName)).OrderBy(n => n));

            var numericPrefix = $"nsu={uri};i=";
            foreach (var prop in props)
            {
                Assert.StartsWith(numericPrefix, prop.NodeId);
                Assert.True(
                    long.TryParse(prop.NodeId!.AsSpan(numericPrefix.Length), out _),
                    $"{LocalName(prop.BrowseName)} got a non-numeric identifier: {prop.NodeId}");
            }
        }

        [Fact]
        public async Task CreatedProperties_GetDistinctIdsThatDoNotCollide()
        {
            var uri = $"urn:test:nsmeta:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            var props = await MetadataPropertiesAsync(db, model);

            // All four are created in one pass, before anything is written, so the allocator has
            // to count up in process rather than re-read max+1 from the DB each time.
            var ids = props.Select(p => p.NodeId!).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());

            // And none may land on the object itself (i=5000), which already exists.
            Assert.DoesNotContain($"nsu={uri};i=5000", ids);
        }

        [Fact]
        public async Task ExistingProperties_KeepTheirOwnNodeIds()
        {
            var uri = $"urn:test:nsmeta:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            // The shape the two nodesets in the wild had: everything but ModelVersion present.
            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(
                uri, "NamespaceUri", "NamespaceVersion", "NamespacePublicationDate"));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            var props = await MetadataPropertiesAsync(db, model);
            Assert.Equal(4, props.Count);

            // The three authored ones are refreshed in place, not recreated.
            foreach (var name in new[] { "NamespaceUri", "NamespaceVersion", "NamespacePublicationDate" })
            {
                var prop = props.Single(p => LocalName(p.BrowseName) == name);
                Assert.Contains(prop.NodeId, new[] { $"nsu={uri};i=5001", $"nsu={uri};i=5002", $"nsu={uri};i=5003" });
            }

            // ModelVersion is the one created here — numeric, and clear of the authored ids.
            var modelVersion = props.Single(p => LocalName(p.BrowseName) == "ModelVersion");
            Assert.StartsWith($"nsu={uri};i=", modelVersion.NodeId);
            Assert.True(long.TryParse(modelVersion.NodeId!.AsSpan($"nsu={uri};i=".Length), out var numeric));
            Assert.True(numeric > 5003, $"Expected an id past the authored ones, got {numeric}.");
        }

        [Fact]
        public async Task SyncingTwice_DoesNotDuplicateProperties()
        {
            var uri = $"urn:test:nsmeta:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri));

            // Idempotency never came from the deterministic id — the lookup matches on the local
            // BrowseName, and the ParentNodeId written on creation is what the next pass finds.
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);
            var firstPassIds = (await MetadataPropertiesAsync(db, model))
                .ToDictionary(p => LocalName(p.BrowseName), p => p.NodeId);

            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);
            var secondPass = await MetadataPropertiesAsync(db, model);

            Assert.Equal(4, secondPass.Count);
            foreach (var prop in secondPass)
            {
                Assert.Equal(firstPassIds[LocalName(prop.BrowseName)], prop.NodeId);
            }
        }

        [Fact]
        public async Task ExportedNodeSet_HasNoStringIdentifiers()
        {
            var uri = $"urn:test:nsmeta:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(
                uri, "NamespaceUri", "NamespaceVersion", "NamespacePublicationDate"));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            var exported = await NodeSetConverter.CreateNodeSetAsync(db, uri);

            // The symptom as it appeared in the file: <UAVariable NodeId="ns=1;s=NamespaceMetadata.ModelVersion" …>
            var modelVersion = Assert.Single(
                exported.Items!, i => LocalName(i.BrowseName) == "ModelVersion");
            Assert.Matches(new Regex(@"^ns=\d+;i=\d+$"), modelVersion.NodeId!);

            foreach (var item in exported.Items!)
            {
                Assert.DoesNotContain(";s=", item.NodeId ?? string.Empty);
            }
        }
    }
}
