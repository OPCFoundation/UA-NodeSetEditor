using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    [Collection("Database")]
    public class DirectDatabaseTests
    {
        private readonly DatabaseFixture _fixture;

        public DirectDatabaseTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Database_IsCreated_And_AllTablesExist()
        {
            var tables = new[]
            {
                nameof(NodeSetEditorDbContext.UserPreferences),
                nameof(NodeSetEditorDbContext.Workspaces),
                nameof(NodeSetEditorDbContext.WorkspaceAcls),
                nameof(NodeSetEditorDbContext.Models),
                nameof(NodeSetEditorDbContext.WorkspaceModels),
                nameof(NodeSetEditorDbContext.Nodes),
                nameof(NodeSetEditorDbContext.References),
                nameof(NodeSetEditorDbContext.SubTypeHierarchy),
            };

            foreach (var table in tables)
            {
                var entityType = _fixture.DbContext.Model.FindEntityType($"NodeSetEditor.Model.{table.TrimEnd('s')}")
                    ?? _fixture.DbContext.Model.FindEntityType($"NodeSetEditor.Model.{table}");
                Assert.NotNull(entityType);
            }

            Assert.True(await _fixture.DbContext.Database.CanConnectAsync());
        }

        [Fact]
        public async Task Can_Insert_And_Query_UserPreference()
        {
            await using var db = _fixture.CreateNewContext();

            var pref = new UserPreference
            {
                UserId = "test-oid-001",
                SelectedWorkspaceId = Guid.NewGuid()
            };

            db.UserPreferences.Add(pref);
            await db.SaveChangesAsync();

            var loaded = await db.UserPreferences.FindAsync("test-oid-001");
            Assert.NotNull(loaded);
            Assert.Equal(pref.SelectedWorkspaceId, loaded.SelectedWorkspaceId);
        }

        [Fact]
        public async Task Can_Insert_Workspace_With_Owner()
        {
            await using var db = _fixture.CreateNewContext();

            var workspace = new Workspace
            {
                Name = "Test Workspace",
                Description = "A test workspace",
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                OwnerUserId = "owner-oid-001",
                OwnerEmail = "owner@example.com"
            };

            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            Assert.NotEqual(Guid.Empty, workspace.Id);

            var loaded = await db.Workspaces
                .FirstOrDefaultAsync(w => w.Id == workspace.Id);

            Assert.NotNull(loaded);
            Assert.Equal("Test Workspace", loaded.Name);
            Assert.Equal("owner-oid-001", loaded.OwnerUserId);
            Assert.Equal("owner@example.com", loaded.OwnerEmail);
        }

        [Fact]
        public async Task Can_Insert_WorkspaceAcl()
        {
            await using var db = _fixture.CreateNewContext();

            var workspace = new Workspace
            {
                Name = "ACL Workspace",
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                OwnerUserId = "acl-owner-001"
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            var acl = new WorkspaceAcl
            {
                WorkspaceId = workspace.Id,
                Email = "editor@example.com"
            };
            db.WorkspaceAcls.Add(acl);
            await db.SaveChangesAsync();

            var loaded = await db.WorkspaceAcls
                .Where(a => a.WorkspaceId == workspace.Id)
                .ToListAsync();

            Assert.Single(loaded);
            Assert.Equal("editor@example.com", loaded[0].Email);
        }

        [Fact]
        public async Task Can_Insert_Model_With_Content_Nodes_And_References()
        {
            await using var db = _fixture.CreateNewContext();

            var xmlContent = "<UANodeSet>test</UANodeSet>"u8.ToArray();

            var model = new Model
            {
                Name = "Test Model",
                Description = "A test OPC UA model",
                Uri = "urn:example.com:test-model",
                Version = "1.0.0",
                VersionNorm = Model.NormalizeVersion("1.0.0"),
                PublicationDate = DateTimeOffset.UtcNow.ToString("o"),
                Content = xmlContent
            };
            db.Models.Add(model);
            await db.SaveChangesAsync();

            var node = new Node
            {
                ModelId = model.Id,
                NodeId = $"nsu={model.Uri};i=1001",
                NodeClass = UaNodeClass.ObjectType,
                BrowseName = $"nsu={model.Uri};TestObjectType",
                DisplayName = "TestObjectType",
                SuperTypeId = "i=58",
                Attributes = new JsonObject
                {
                    ["IsAbstract"] = false
                }
            };
            db.Nodes.Add(node);

            var reference = new Reference
            {
                ModelId = model.Id,
                SourceNodeId = $"nsu={model.Uri};i=1001",
                ReferenceTypeId = "i=45",
                IsForward = false,
                TargetNodeId = "i=58"
            };
            db.References.Add(reference);
            await db.SaveChangesAsync();

            var loaded = await db.Models
                .Include(m => m.Nodes)
                .Include(m => m.References)
                .FirstOrDefaultAsync(m => m.Id == model.Id);

            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Content);
            Assert.Equal(xmlContent.Length, loaded.Content!.Length);
            Assert.Single(loaded.Nodes!);
            Assert.Equal("TestObjectType", loaded.Nodes![0].DisplayName);
            Assert.Single(loaded.References!);
        }

        [Fact]
        public async Task Can_Insert_WorkspaceModel_Link()
        {
            await using var db = _fixture.CreateNewContext();

            var workspace = new Workspace
            {
                Name = "WM Workspace",
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                OwnerUserId = "wm-owner-001"
            };
            db.Workspaces.Add(workspace);

            var model = new Model
            {
                Name = "WM Model",
                Uri = "urn:example.com:wm-model",
                Version = "1.0.0",
                VersionNorm = Model.NormalizeVersion("1.0.0")
            };
            db.Models.Add(model);
            await db.SaveChangesAsync();

            var link = new WorkspaceModel
            {
                WorkspaceId = workspace.Id,
                ModelId = model.Id,
                IsPrivate = true
            };
            db.WorkspaceModels.Add(link);
            await db.SaveChangesAsync();

            var loaded = await db.Workspaces
                .Include(w => w.Models!)
                    .ThenInclude(wm => wm.Model)
                .FirstOrDefaultAsync(w => w.Id == workspace.Id);

            Assert.NotNull(loaded);
            Assert.Single(loaded.Models!);
            Assert.True(loaded.Models![0].IsPrivate);
            Assert.Equal("WM Model", loaded.Models![0].Model!.Name);
        }

        [Fact]
        public async Task Can_Insert_SubTypeHierarchy()
        {
            await using var db = _fixture.CreateNewContext();

            var entries = new[]
            {
                new SubTypeHierarchy
                {
                    SubTypeNodeId = "nsu=urn:example.com:test;i=1001",
                    SuperTypeNodeId = "i=58",
                    Depth = 1
                },
                new SubTypeHierarchy
                {
                    SubTypeNodeId = "nsu=urn:example.com:test;i=1001",
                    SuperTypeNodeId = "i=24",
                    Depth = 2
                }
            };

            db.SubTypeHierarchy.AddRange(entries);
            await db.SaveChangesAsync();

            var allSuperTypes = await db.SubTypeHierarchy
                .Where(h => h.SubTypeNodeId == "nsu=urn:example.com:test;i=1001")
                .OrderBy(h => h.Depth)
                .ToListAsync();

            Assert.Equal(2, allSuperTypes.Count);
            Assert.Equal(1, allSuperTypes[0].Depth);
            Assert.Equal(2, allSuperTypes[1].Depth);
        }

        [Fact]
        public async Task Cascade_Delete_Model_Removes_Nodes_And_References()
        {
            await using var db = _fixture.CreateNewContext();

            var model = new Model
            {
                Name = "Cascade Model",
                Uri = "urn:example.com:cascade-model",
                Version = "1.0.0",
                VersionNorm = Model.NormalizeVersion("1.0.0")
            };
            db.Models.Add(model);
            await db.SaveChangesAsync();

            db.Nodes.Add(new Node
            {
                ModelId = model.Id,
                NodeId = $"nsu={model.Uri};i=2001",
                NodeClass = UaNodeClass.Object,
                BrowseName = $"nsu={model.Uri};CascadeNode",
                DisplayName = "CascadeNode"
            });

            db.References.Add(new Reference
            {
                ModelId = model.Id,
                SourceNodeId = $"nsu={model.Uri};i=2001",
                ReferenceTypeId = "i=45",
                IsForward = false,
                TargetNodeId = "i=58"
            });
            await db.SaveChangesAsync();

            db.Models.Remove(model);
            await db.SaveChangesAsync();

            Assert.Empty(await db.Nodes.Where(n => n.ModelId == model.Id).ToListAsync());
            Assert.Empty(await db.References.Where(r => r.ModelId == model.Id).ToListAsync());
        }

        [Fact]
        public async Task Can_Query_Nodes_By_DisplayName()
        {
            await using var db = _fixture.CreateNewContext();

            var model = new Model
            {
                Name = "Query Model",
                Uri = "urn:example.com:query-model",
                Version = "1.0.0",
                VersionNorm = Model.NormalizeVersion("1.0.0")
            };
            db.Models.Add(model);
            await db.SaveChangesAsync();

            db.Nodes.AddRange(
                new Node { ModelId = model.Id, NodeId = "nsu=urn:example.com:query-model;i=1", NodeClass = UaNodeClass.ObjectType, BrowseName = "nsu=urn:example.com:query-model;FooType", DisplayName = "FooType" },
                new Node { ModelId = model.Id, NodeId = "nsu=urn:example.com:query-model;i=2", NodeClass = UaNodeClass.ObjectType, BrowseName = "nsu=urn:example.com:query-model;BarType", DisplayName = "BarType" },
                new Node { ModelId = model.Id, NodeId = "nsu=urn:example.com:query-model;i=3", NodeClass = UaNodeClass.Variable, BrowseName = "nsu=urn:example.com:query-model;FooVar", DisplayName = "FooVar" }
            );
            await db.SaveChangesAsync();

            // Filter by name
            var fooNodes = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.DisplayName != null && n.DisplayName.Contains("Foo"))
                .ToListAsync();
            Assert.Equal(2, fooNodes.Count);

            // Filter by NodeClass
            var objectTypes = await db.Nodes
                .Where(n => n.ModelId == model.Id && n.NodeClass == UaNodeClass.ObjectType)
                .ToListAsync();
            Assert.Equal(2, objectTypes.Count);
        }

        [Fact]
        public async Task SemVer_Sorting_Returns_Latest_Version()
        {
            await using var db = _fixture.CreateNewContext();

            var uri = "urn:example.com:semver-test";

            var v1 = new Model { Name = "SV", Uri = uri, Version = "1.0.0", VersionNorm = Model.NormalizeVersion("1.0.0") };
            var v2 = new Model { Name = "SV", Uri = uri, Version = "1.2.3", VersionNorm = Model.NormalizeVersion("1.2.3") };
            var v3 = new Model { Name = "SV", Uri = uri, Version = "1.10.0", VersionNorm = Model.NormalizeVersion("1.10.0") };
            var v4 = new Model { Name = "SV", Uri = uri, Version = "2.0.0-beta", VersionNorm = Model.NormalizeVersion("2.0.0-beta") };
            var v5 = new Model { Name = "SV", Uri = uri, Version = "2.0.0", VersionNorm = Model.NormalizeVersion("2.0.0") };

            db.Models.AddRange(v1, v2, v3, v4, v5);
            await db.SaveChangesAsync();

            // Single ORDER BY on VersionNorm gives correct SemVer ordering
            var sorted = await db.Models
                .Where(m => m.Uri == uri)
                .OrderByDescending(m => m.VersionNorm)
                .ToListAsync();

            Assert.Equal(5, sorted.Count);
            Assert.Equal("2.0.0", sorted[0].Version);       // 2.0.0~ sorts after 2.0.0-beta
            Assert.Equal("2.0.0-beta", sorted[1].Version);
            Assert.Equal("1.10.0", sorted[2].Version);      // 0001.0010.0000 > 0001.0002.0003
            Assert.Equal("1.2.3", sorted[3].Version);
            Assert.Equal("1.0.0", sorted[4].Version);

            // Get latest release version (VersionNorm ends with ~)
            var latestRelease = await db.Models
                .Where(m => m.Uri == uri && m.VersionNorm != null && m.VersionNorm.EndsWith("~"))
                .OrderByDescending(m => m.VersionNorm)
                .FirstOrDefaultAsync();

            Assert.NotNull(latestRelease);
            Assert.Equal("2.0.0", latestRelease.Version);
        }

        [Fact]
        public async Task Version_Replace_Updates_Existing_Row()
        {
            await using var db = _fixture.CreateNewContext();

            var uri = "urn:example.com:replace-test";

            var original = new Model
            {
                Name = "Replace Model",
                Uri = uri,
                Version = "1.0.0",
                VersionNorm = Model.NormalizeVersion("1.0.0"),
                PublicationDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                Content = "original"u8.ToArray()
            };
            db.Models.Add(original);
            await db.SaveChangesAsync();
            var originalId = original.Id;

            // "Upload" same URI + version → should replace content
            var norm = Model.NormalizeVersion("1.0.0");
            var existing = await db.Models
                .FirstOrDefaultAsync(m => m.Uri == uri && m.VersionNorm == norm);
            Assert.NotNull(existing);

            existing.Content = "updated"u8.ToArray();
            await db.SaveChangesAsync();

            // Verify only one row exists
            var count = await db.Models.CountAsync(m => m.Uri == uri && m.VersionNorm == norm);
            Assert.Equal(1, count);

            // Verify content was updated
            var reloaded = await db.Models.FindAsync(originalId);
            Assert.NotNull(reloaded);
            Assert.Equal("updated", System.Text.Encoding.UTF8.GetString(reloaded.Content!));
        }

        [Fact]
        public void NormalizeVersion_Produces_Sortable_Strings()
        {
            Assert.Equal("000100020003~", Model.NormalizeVersion("1.2.3"));
            Assert.Equal("000200000000-beta.1", Model.NormalizeVersion("2.0.0-beta.1"));
            Assert.Equal("000100040003~", Model.NormalizeVersion("1.04.003"));
            Assert.Null(Model.NormalizeVersion(null));
            Assert.Null(Model.NormalizeVersion(""));

            // Release sorts after prerelease (~ > -)
            Assert.True(string.Compare(
                Model.NormalizeVersion("2.0.0"),
                Model.NormalizeVersion("2.0.0-beta"),
                StringComparison.Ordinal) > 0);

            // Numeric sorting via zero-padding (1.10.0 > 1.2.3)
            Assert.True(string.Compare(
                Model.NormalizeVersion("1.10.0"),
                Model.NormalizeVersion("1.2.3"),
                StringComparison.Ordinal) > 0);
        }
    }
}
