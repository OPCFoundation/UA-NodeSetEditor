using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// Guards the re-import semantics of <see cref="NodeSetConverter.StoreNodeSetAsync"/>:
    /// storing a nodeset whose (Uri, VersionNorm) already exists must UPDATE the
    /// existing row in place — keeping its Id (which anchors WorkspaceModels links
    /// in every workspace) and its curated metadata (Name, Description, Published,
    /// Creator). The old delete-and-replace behavior reset UA Core's name to its
    /// URI and cascade-deleted every workspace's link whenever a Cloud Library
    /// dependency import re-stored a version already in the DB.
    /// </summary>
    [Collection("Database")]
    public class StoreNodeSetReimportTests
    {
        private readonly DatabaseFixture _fixture;

        public StoreNodeSetReimportTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        private static Opc.Ua.Export.UANodeSet BuildNodeSet(string uri, string version)
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
                        BrowseName = "1:ReimportType",
                        DisplayName = new[] { new Opc.Ua.Export.LocalizedText { Value = "ReimportType" } },
                    }
                }
            };
        }

        [Fact]
        public async Task Reimport_SameVersion_KeepsRowIdCuratedMetadataAndWorkspaceLinks()
        {
            var uri = $"urn:test:reimport:{Guid.NewGuid():N}";

            await using var db = _fixture.CreateNewContext();

            // First import, then curate the row the way initialize_db / a user would.
            var first = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, "1.0.0"));
            first.Name = "Curated Name";
            first.Description = "Curated description";
            first.Published = true;
            first.Creator = "randy";
            await db.SaveChangesAsync();

            // Link it into a workspace (the way every workspace links UA Core).
            var ws = new Workspace
            {
                Name = "Reimport WS",
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                OwnerUserId = "reimport-owner-001"
            };
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            db.WorkspaceModels.Add(new WorkspaceModel { WorkspaceId = ws.Id, ModelId = first.Id });
            await db.SaveChangesAsync();

            // Re-import the SAME (Uri, VersionNorm) — e.g. a dependency import
            // pulling a version that is already in the DB.
            await using var db2 = _fixture.CreateNewContext();
            var second = await NodeSetConverter.StoreNodeSetAsync(db2, BuildNodeSet(uri, "1.0.0"));

            Assert.Equal(first.Id, second.Id);

            await using var verify = _fixture.CreateNewContext();
            var row = await verify.Models.SingleAsync(m => m.Uri == uri);
            Assert.Equal(first.Id, row.Id);
            Assert.Equal("Curated Name", row.Name);
            Assert.Equal("Curated description", row.Description);
            Assert.True(row.Published);
            Assert.Equal("randy", row.Creator);

            // The workspace link must survive the re-import.
            Assert.True(await verify.WorkspaceModels.AnyAsync(
                wm => wm.WorkspaceId == ws.Id && wm.ModelId == first.Id));

            // The content was still replaced (rebuilt from the incoming nodeset).
            Assert.Equal(1, await verify.Nodes.CountAsync(n => n.ModelId == first.Id));
        }
    }
}
