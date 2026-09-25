using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// The ProfileGroup:&lt;Name&gt; convention end to end through
    /// <see cref="NodeSetConverter.StoreNodeSetAsync"/> and
    /// <see cref="NodeSetConverter.CreateNodeSetAsync"/>: it is lifted off the NamespaceMetadata
    /// object into model metadata on import, written back on export, and is a literal
    /// conformance unit anywhere else. <see cref="ProfileGroupConventionTests"/> covers the
    /// encoding itself.
    /// </summary>
    [Collection("Database")]
    public class ProfileGroupRoundTripTests
    {
        /// <summary>NamespaceMetadataType — the type definition that marks the namespace object.</summary>
        private const string NamespaceMetadataTypeId = "i=11616";
        private const string HasTypeDefinition = "i=40";

        private readonly DatabaseFixture _fixture;

        public ProfileGroupRoundTripTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// A nodeset with a NamespaceMetadata object plus one ordinary ObjectType, each carrying
        /// the conformance units supplied.
        /// </summary>
        private static Opc.Ua.Export.UANodeSet BuildNodeSet(
            string uri, string[]? namespaceObjectUnits, string[]? objectTypeUnits = null)
        {
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
                    }
                ],
                Items =
                [
                    new Opc.Ua.Export.UAObject
                    {
                        NodeId = "ns=1;i=5000",
                        BrowseName = "1:NamespaceMetadata",
                        DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "NamespaceMetadata" }],
                        Category = namespaceObjectUnits,
                        References =
                        [
                            new Opc.Ua.Export.Reference
                            {
                                ReferenceType = HasTypeDefinition,
                                IsForward = true,
                                Value = NamespaceMetadataTypeId,
                            }
                        ],
                    },
                    new Opc.Ua.Export.UAObjectType
                    {
                        NodeId = "ns=1;i=1001",
                        BrowseName = "1:OrdinaryType",
                        DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "OrdinaryType" }],
                        Category = objectTypeUnits,
                    },
                ]
            };
        }

        /// <summary>The conformance units the exported nodeset put on a node.</summary>
        private static string[]? UnitsOf(Opc.Ua.Export.UANodeSet nodeSet, string browseNameSuffix) =>
            nodeSet.Items?.FirstOrDefault(i => i.BrowseName?.EndsWith(browseNameSuffix, StringComparison.Ordinal) == true)?.Category;

        [Fact]
        public async Task Import_LiftsTheProfileGroupOffTheNamespaceObject()
        {
            var uri = $"urn:test:profilegroup:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, ["ProfileGroup:UACore 1.05", "Base Info"]));

            // Stored as model metadata...
            Assert.Equal("UACore 1.05", model.GetProfileGroupName());

            // ...and NOT left behind on the node's own unit list.
            var exported = await NodeSetConverter.CreateNodeSetAsync(db, uri);
            var units = UnitsOf(exported, "NamespaceMetadata");
            Assert.Equal(new[] { "ProfileGroup:UACore 1.05", "Base Info" }, units);

            var node = await db.Nodes.AsNoTracking()
                .FirstAsync(n => n.ModelId == model.Id && n.TypeDefinitionId == NamespaceMetadataTypeId);
            var storedUnits = node.Attributes?["Category"]?.AsArray().Select(u => (string)u!).ToArray();
            Assert.Equal(new[] { "Base Info" }, storedUnits);
        }

        [Fact]
        public async Task Export_EmitsTheProfileGroupSetInTheEditor()
        {
            var uri = $"urn:test:profilegroup:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, ["Base Info"]));
            Assert.Null(model.GetProfileGroupName());

            // What the model dialog does.
            model.SetProfileGroupName("Machinery 1.03");
            await db.SaveChangesAsync();

            var exported = await NodeSetConverter.CreateNodeSetAsync(db, uri);

            Assert.Equal(new[] { "ProfileGroup:Machinery 1.03", "Base Info" },
                UnitsOf(exported, "NamespaceMetadata"));
        }

        [Fact]
        public async Task TheConventionIsALiteralUnitOnAnyOtherNode()
        {
            var uri = $"urn:test:profilegroup:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, null, objectTypeUnits: ["ProfileGroup:NotSpecial", "Base Info"]));

            // Only the namespace object carries the convention, so the model has no profile group
            // and the ObjectType keeps the string verbatim.
            Assert.Null(model.GetProfileGroupName());

            var exported = await NodeSetConverter.CreateNodeSetAsync(db, uri);
            Assert.Equal(new[] { "ProfileGroup:NotSpecial", "Base Info" }, UnitsOf(exported, "OrdinaryType"));
        }

        [Fact]
        public async Task RoundTrip_DoesNotAccumulateTheConvention()
        {
            var uri = $"urn:test:profilegroup:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, ["ProfileGroup:UACore 1.05", "Base Info"]));
            var once = await NodeSetConverter.CreateNodeSetAsync(db, uri);

            // Export → re-import → export again: still exactly one ProfileGroup entry.
            var model = await NodeSetConverter.StoreNodeSetAsync(db, once);
            var twice = await NodeSetConverter.CreateNodeSetAsync(db, uri);

            Assert.Equal("UACore 1.05", model.GetProfileGroupName());
            Assert.Equal(UnitsOf(once, "NamespaceMetadata"), UnitsOf(twice, "NamespaceMetadata"));
            Assert.Single(UnitsOf(twice, "NamespaceMetadata")!,
                u => u.StartsWith(ProfileGroupConvention.Prefix, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Reimport_WithoutTheConventionKeepsWhatTheEditorSet()
        {
            var uri = $"urn:test:profilegroup:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, ["Base Info"]));
            model.SetProfileGroupName("Machinery 1.03");
            await db.SaveChangesAsync();

            // A re-import of the same version from a file that predates the convention must not
            // silently clear a value the user curated.
            var reimported = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, ["Base Info"]));

            Assert.Equal("Machinery 1.03", reimported.GetProfileGroupName());
        }

        [Fact]
        public async Task Reimport_WithTheConventionOverridesWhatTheEditorSet()
        {
            var uri = $"urn:test:profilegroup:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, ["Base Info"]));
            model.SetProfileGroupName("Stale 1.00");
            await db.SaveChangesAsync();

            // When the file declares one, the file wins — it is the authority on its own content.
            var reimported = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, ["ProfileGroup:UACore 1.05", "Base Info"]));

            Assert.Equal("UACore 1.05", reimported.GetProfileGroupName());
        }
    }
}
