using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// A NodeSet carries two version strings that are not interchangeable: Version is the human
    /// form and keeps its leading zeros, ModelVersion is SemVer and must not have any. Machinery
    /// ships exactly that pair — Version="1.04.2" alongside ModelVersion="1.4.2".
    ///
    /// <see cref="NodeSetConverter.SyncNamespaceMetadataAsync"/> writes both onto the
    /// NamespaceMetadata object as properties, and used to put Version through verbatim as the
    /// ModelVersion whenever the source NodeSet declared none — which is how a non-compliant
    /// "1.04.2" ended up in the ModelVersion property of exported files.
    /// </summary>
    [Collection("Database")]
    public class NamespaceMetadataModelVersionTests
    {
        private const string NamespaceMetadataTypeId = "i=11616";
        private const string HasTypeDefinition = "i=40";
        private const string MetadataObjectId = "ns=1;i=5000";

        private readonly DatabaseFixture _fixture;

        public NamespaceMetadataModelVersionTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// A nodeset whose model table carries the given Version and, optionally, a ModelVersion.
        /// The NamespaceMetadata object has no value properties, so the sync creates all four.
        /// </summary>
        private static Opc.Ua.Export.UANodeSet BuildNodeSet(
            string uri, string version, string? modelVersion)
        {
            return new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = [uri],
                Models =
                [
                    new Opc.Ua.Export.ModelTableEntry
                    {
                        ModelUri = uri,
                        Version = version,
                        ModelVersion = modelVersion,
                        PublicationDate = DateTime.UtcNow,
                        PublicationDateSpecified = true,
                    },
                ],
                Items =
                [
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
                ],
            };
        }

        private static string LocalName(string? browseName)
        {
            if (string.IsNullOrEmpty(browseName)) return string.Empty;
            var semi = browseName.IndexOf(';');
            return semi >= 0 ? browseName[(semi + 1)..] : browseName;
        }

        /// <summary>
        /// The stored Value text of one NamespaceMetadata property. Both version properties are
        /// Strings, and the Part 6 §5.4 reversible form stores those as the JSON string directly
        /// — no type wrapper (see NodeSetConverterTests).
        /// </summary>
        private static async Task<string?> PropertyValueAsync(
            NodeSetEditorDbContext db, global::NodeSetEditor.Model.Model model, string propName)
        {
            var objNodeId = $"nsu={model.Uri};i=5000";
            var props = await db.Nodes.AsNoTracking()
                .Where(n => n.ModelId == model.Id && n.ParentNodeId == objNodeId)
                .ToListAsync();

            var prop = props.Single(p => LocalName(p.BrowseName) == propName);
            return prop.Attributes?["Value"]?.GetValue<string>();
        }

        [Theory]
        // The pair Machinery ships: the human Version is padded, the ModelVersion is not.
        [InlineData("1.04.2", "1.4.2")]
        [InlineData("1.05.07", "1.5.7")]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData("2.10.0", "2.10.0")]
        // A pre-release label is the author's and survives normalization.
        [InlineData("1.05.07-alpha", "1.5.7-alpha")]
        // Short forms fill out to three components.
        [InlineData("1.4", "1.4.0")]
        public async Task ModelVersionProperty_IsSemVer_WhenDerivedFromVersion(
            string version, string expectedModelVersion)
        {
            var uri = $"urn:test:nsmetaver:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            // No ModelVersion declared, so the property is derived from Version.
            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, version, modelVersion: null));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            Assert.Equal(expectedModelVersion, await PropertyValueAsync(db, model, "ModelVersion"));

            // NamespaceVersion is the human form and keeps its padding — the whole distinction.
            Assert.Equal(version, await PropertyValueAsync(db, model, "NamespaceVersion"));
        }

        [Fact]
        public async Task ModelVersionProperty_UsesTheDeclaredModelVersion()
        {
            var uri = $"urn:test:nsmetaver:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            // Machinery's real pair.
            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, "1.04.2", modelVersion: "1.4.2"));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            Assert.Equal("1.4.2", await PropertyValueAsync(db, model, "ModelVersion"));
            Assert.Equal("1.04.2", await PropertyValueAsync(db, model, "NamespaceVersion"));
        }

        [Fact]
        public async Task ModelVersionProperty_NormalizesANonCompliantDeclaredValue()
        {
            var uri = $"urn:test:nsmetaver:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            // Some files declare a padded ModelVersion. The property is SemVer by definition,
            // so it is normalized rather than copied.
            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, "1.04.2", modelVersion: "1.04.2"));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            Assert.Equal("1.4.2", await PropertyValueAsync(db, model, "ModelVersion"));
        }

        private const string SemanticVersionString = "i=24263";
        private const string StringDataType = "i=12";

        private static async Task<Node> PropertyNodeAsync(
            NodeSetEditorDbContext db, global::NodeSetEditor.Model.Model model, string propName)
        {
            var objNodeId = $"nsu={model.Uri};i=5000";
            var props = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.ParentNodeId == objNodeId)
                .ToListAsync();
            return props.Single(p => LocalName(p.BrowseName) == propName);
        }

        [Fact]
        public async Task ModelVersionProperty_IsCreatedAsSemanticVersionString()
        {
            var uri = $"urn:test:nsmetaver:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, "1.0.0", modelVersion: "1.0.0"));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            // NamespaceMetadataType declares ModelVersion as SemanticVersionString, not String.
            var prop = await PropertyNodeAsync(db, model, "ModelVersion");
            Assert.Equal(SemanticVersionString, prop.Attributes?["DataType"]?.GetValue<string>());
            Assert.Equal("1.0.0", prop.Attributes?["Value"]?.GetValue<string>());

            // The other version property really is a String.
            var nsVersion = await PropertyNodeAsync(db, model, "NamespaceVersion");
            Assert.Equal(StringDataType, nsVersion.Attributes?["DataType"]?.GetValue<string>());
        }

        [Fact]
        public async Task ModelVersionProperty_StoredAsString_IsRepairedOnNextSync()
        {
            var uri = $"urn:test:nsmetaver:{Guid.NewGuid():N}";
            await using var db = _fixture.CreateNewContext();

            var model = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, "1.0.0", modelVersion: "1.0.0"));
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            // Simulate a model created before the fix: the property carries DataType String.
            var prop = await PropertyNodeAsync(db, model, "ModelVersion");
            var attrs = prop.Attributes!.DeepClone().AsObject();
            attrs["DataType"] = StringDataType;
            prop.Attributes = attrs;
            await db.SaveChangesAsync();

            // A version change runs the sync again, which must put the DataType right.
            model.Version = "1.1.0";
            model.Metadata?.Remove("ModelVersion");
            await NodeSetConverter.SyncNamespaceMetadataAsync(db, model);

            await using var verify = _fixture.CreateNewContext();
            var repaired = await PropertyNodeAsync(verify, model, "ModelVersion");
            Assert.Equal(SemanticVersionString, repaired.Attributes?["DataType"]?.GetValue<string>());
            Assert.Equal("1.1.0", repaired.Attributes?["Value"]?.GetValue<string>());
        }

        [Theory]
        [InlineData("1.04.2", "1.4.2")]
        [InlineData("1.05.07", "1.5.7")]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData("1.04.003", "1.4.3")]
        [InlineData("2.0.0-beta.1", "2.0.0-beta.1")]
        [InlineData("1.4", "1.4.0")]
        [InlineData("3", "3.0.0")]
        public void ToSemVer_StripsPaddingAndKeepsPrerelease(string version, string expected)
        {
            Assert.Equal(expected, global::NodeSetEditor.Model.Model.ToSemVer(version));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ToSemVer_IsNullForNothing(string? version)
        {
            Assert.Null(global::NodeSetEditor.Model.Model.ToSemVer(version));
        }
    }
}
