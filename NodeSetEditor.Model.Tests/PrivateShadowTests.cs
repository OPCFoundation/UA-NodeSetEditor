using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// The point of model ownership tiers: a workspace's edited copy of a namespace and the
    /// shared copy everyone else resolves against are DIFFERENT ROWS, at the same URI and the
    /// same version.
    ///
    /// Rows used to be unique on (Uri, VersionNorm) globally, and
    /// <see cref="NodeSetConverter.StoreNodeSetAsync"/> reused whichever row matched — deleting
    /// its nodes and references and refilling them from the incoming file. So importing the
    /// Cloud Library's copy of a namespace a workspace had edited destroyed that workspace's
    /// content in place and re-stamped its provenance. Worse, the import that did it could be
    /// triggered by a different user in a different workspace resolving an unrelated
    /// dependency, so the owner lost their work without doing anything at all.
    ///
    /// See <see cref="StoreNodeSetReimportTests"/> for the other half of the rule: within ONE
    /// scope, a re-import still updates the existing row rather than adding another.
    /// </summary>
    [Collection("Database")]
    public class PrivateShadowTests
    {
        private readonly DatabaseFixture _fixture;

        public PrivateShadowTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        private static Opc.Ua.Export.UANodeSet BuildNodeSet(string uri, string version, string typeName)
        {
            return new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = new[] { uri },
                Models = new[]
                {
                    new Opc.Ua.Export.ModelTableEntry
                    {
                        ModelUri = uri,
                        Version = version,
                        ModelVersion = version,
                        PublicationDate = DateTime.UtcNow,
                        PublicationDateSpecified = true,
                    }
                },
                Items = new Opc.Ua.Export.UANode[]
                {
                    new Opc.Ua.Export.UAObjectType
                    {
                        NodeId = "ns=1;i=1001",
                        BrowseName = $"1:{typeName}",
                        DisplayName = new[] { new Opc.Ua.Export.LocalizedText { Value = typeName } },
                    }
                }
            };
        }

        [Fact]
        public async Task SharedImport_DoesNotOverwriteAWorkspacesPrivateCopy()
        {
            var uri = $"urn:test:shadow:{Guid.NewGuid():N}";
            const string version = "1.0.0";
            var workspaceId = Guid.NewGuid();

            await using var db = _fixture.CreateNewContext();

            // A workspace's own edited copy of the namespace.
            var privateModel = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, version, "MyEditedType"), ModelTier.Private, workspaceId);

            Assert.Equal(ModelTier.Private, privateModel.Tier);
            Assert.Equal(workspaceId, privateModel.OwnerWorkspaceId);

            // Now the shared copy of the SAME URI AND VERSION arrives, which is what a Cloud
            // Library import or a dependency resolution does — on anyone's behalf.
            var sharedModel = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, version, "CatalogType"), ModelTier.Shared);

            // A second row, not a rewrite of the first.
            Assert.NotEqual(privateModel.Id, sharedModel.Id);
            Assert.Equal(ModelTier.Shared, sharedModel.Tier);
            Assert.Null(sharedModel.OwnerWorkspaceId);
            Assert.Equal(2, await db.Models.CountAsync(m => m.Uri == uri));

            // The private copy keeps its own content: the incoming shared nodes went to the
            // shared row, and nothing was deleted from the private one.
            var privateBrowseNames = await db.Nodes
                .Where(n => n.ModelId == privateModel.Id)
                .Select(n => n.BrowseName)
                .ToListAsync();
            var sharedBrowseNames = await db.Nodes
                .Where(n => n.ModelId == sharedModel.Id)
                .Select(n => n.BrowseName)
                .ToListAsync();

            Assert.Contains(privateBrowseNames, b => b != null && b.Contains("MyEditedType"));
            Assert.DoesNotContain(privateBrowseNames, b => b != null && b.Contains("CatalogType"));
            Assert.Contains(sharedBrowseNames, b => b != null && b.Contains("CatalogType"));
        }

        [Fact]
        public async Task TwoWorkspaces_EachGetTheirOwnCopyOfTheSameVersion()
        {
            var uri = $"urn:test:shadow2:{Guid.NewGuid():N}";
            const string version = "2.0.0";
            var workspaceA = Guid.NewGuid();
            var workspaceB = Guid.NewGuid();

            await using var db = _fixture.CreateNewContext();

            var a = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, version, "TypeA"), ModelTier.Private, workspaceA);
            var b = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, version, "TypeB"), ModelTier.Private, workspaceB);

            Assert.NotEqual(a.Id, b.Id);
            Assert.Equal(workspaceA, a.OwnerWorkspaceId);
            Assert.Equal(workspaceB, b.OwnerWorkspaceId);
            Assert.Equal(2, await db.Models.CountAsync(m => m.Uri == uri));

            // Re-importing into the SAME workspace still updates in place rather than adding a
            // third row -- the scope narrows the dedup, it does not remove it.
            var aAgain = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(uri, version, "TypeARevised"), ModelTier.Private, workspaceA);

            Assert.Equal(a.Id, aAgain.Id);
            Assert.Equal(2, await db.Models.CountAsync(m => m.Uri == uri));
        }
    }
}
