using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

[Collection("Api")]
public class UaRestWorkspaceTests : UaRestTestBase
{
    public UaRestWorkspaceTests(ApiFixture fixture) : base(fixture) { }

    /// <summary>
    /// The deterministic id a Cloud Library model is known by before it has been imported.
    /// /namespaces/info/{id}/link resolves an unknown GUID by hashing every Cloud Library
    /// identifier the same way and matching (see DbNodeSetStorageService.StringToGuid), so a
    /// caller that wants to link a Cloud Library model has to derive it. If that scheme ever
    /// changes, these tests fail — which is the intent.
    /// </summary>
    private static Guid CloudLibraryModelId(string identifier) =>
        new(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"cloudlib:{identifier}")));

    /// <summary>
    /// Looks a namespace up in the Cloud Library and returns the id used to link it into a
    /// workspace. The search endpoint collapses each namespace to its latest version, so an
    /// exact-URI lookup yields the one entry we want.
    /// </summary>
    private async Task<string> FindCloudLibraryModelId(string namespaceUri)
    {
        var response = await Client.GetAsync(
            $"/api/opcua/v1/cloudlibrary/search?namespaceUri={Uri.EscapeDataString(namespaceUri)}&limit=10");
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync();
        var models = JsonSerializer.Deserialize<JsonElement>(raw).EnumerateArray().ToList();
        Assert.True(models.Count > 0,
            $"{namespaceUri} not found in Cloud Library — is the Cloud Library configured? " +
            $"Response: {raw[..Math.Min(500, raw.Length)]}");

        var identifier = models[0].GetProperty("identifier").GetString()!;
        return CloudLibraryModelId(identifier).ToString();
    }

    [Fact]
    public async Task Discovery_CurrentUserWorkspaces()
    {
        // Login as alice â€” a fresh user with no workspaces yet
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/discovery",
            "alice-001", "alice@test.net");
        var response = await Client.SendAsync(req);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = body.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0, "Expected at least one workspace");

        // New user should get an auto-created "Sandbox" workspace
        var defaultWs = results.EnumerateArray()
            .FirstOrDefault(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "Sandbox");
        Assert.NotEqual(default, defaultWs);

        // It should be marked as the default
        Assert.True(defaultWs.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Workspace_CreateUpdateDelete()
    {
        const string userId = "alice-001";
        const string email = "alice@test.net";
        string? wsUrn = null;

        try
        {
            // Clean up leftover workspaces from prior failed runs
            await FindAndDeleteWorkspace(userId, email, "Test");
            await FindAndDeleteWorkspace(userId, email, "TestRenamed");

            // Create workspace
            var createReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createReq.Content = JsonContent.Create(new
            {
                applicationName = "Test",
                description = "Test workspace"
            });
            var createResp = await Client.SendAsync(createReq);
            createResp.EnsureSuccessStatusCode();
            var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = created.GetProperty("applicationUri").GetString()!;
            Assert.Equal("Test", created.GetProperty("applicationName").GetProperty("text").GetString());
            Assert.Equal("Test workspace", created.GetProperty("description").GetProperty("text").GetString());

            // List â€” verify Test exists
            var list = await DiscoverAs(userId, email);
            Assert.Contains(list.EnumerateArray(),
                ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "Test");

            // Update name and description
            var updateReq = AsUser(HttpMethod.Put,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            updateReq.Content = JsonContent.Create(new
            {
                applicationName = "TestRenamed",
                description = "Updated description"
            });
            var updateResp = await Client.SendAsync(updateReq);
            updateResp.EnsureSuccessStatusCode();
            var updated = await updateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("TestRenamed", updated.GetProperty("applicationName").GetProperty("text").GetString());
            Assert.Equal("Updated description", updated.GetProperty("description").GetProperty("text").GetString());

            // List â€” verify updated properties
            var list2 = await DiscoverAs(userId, email);
            var renamed = FindByUrn(list2, wsUrn);
            Assert.Equal("TestRenamed", renamed.GetProperty("applicationName").GetProperty("text").GetString());
            Assert.Equal("Updated description", renamed.GetProperty("description").GetProperty("text").GetString());

            // Delete
            var deleteReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(deleteReq)).StatusCode);
            wsUrn = null;

            // List â€” verify gone
            var list3 = await DiscoverAs(userId, email);
            Assert.DoesNotContain(list3.EnumerateArray(),
                ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "TestRenamed");
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Workspace_Share()
    {
        // Distinct identities (not the bare "alice"/"bob" used by other tests): the owner
        // display name is unique per user, so sharing an email with another test's user makes
        // this test's owner resolve to a suffixed name ("alice2") in an order-dependent way.
        const string aliceId = "ws-share-alice", aliceEmail = "ws-share-alice@test.net";
        const string bobId = "ws-share-bob", bobEmail = "ws-share-bob@test.net";

        string? aliceSharedUrn = null;
        string? bobSharedUrn = null;

        try
        {
            // Clean up leftover workspaces from prior failed runs
            await FindAndDeleteWorkspace(aliceId, aliceEmail, "Shared");
            await FindAndDeleteWorkspace(bobId, bobEmail, "Shared");

            // Create 'Shared' workspace for alice
            var createReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", aliceId, aliceEmail);
            createReq.Content = JsonContent.Create(new { applicationName = "Shared", description = "Alice's shared" });
            var createResp = await Client.SendAsync(createReq);
            createResp.EnsureSuccessStatusCode();
            var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            aliceSharedUrn = created.GetProperty("applicationUri").GetString()!;

            // Add bob to ACL
            var aclReq = AsUser(HttpMethod.Put,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(aliceSharedUrn)}", aliceId, aliceEmail);
            aclReq.Content = JsonContent.Create(new { acl = new[] { bobEmail } });
            var aclResp = await Client.SendAsync(aclReq);
            aclResp.EnsureSuccessStatusCode();

            // List alice's workspaces â€” verify ACL updated
            var aliceList = await DiscoverAs(aliceId, aliceEmail);
            var aliceShared = FindByUrn(aliceList, aliceSharedUrn);
            Assert.Contains(bobEmail, aliceShared.GetProperty("acl").EnumerateArray().Select(a => a.GetString()));

            // List bob's workspaces â€” bob sees alice's Shared via ACL
            // (no auto-created Default because bob already has a visible workspace)
            var bobList = await DiscoverAs(bobId, bobEmail);

            var bobSeesAliceShared = FindByUrn(bobList, aliceSharedUrn);
            Assert.Equal(aliceEmail.Split('@')[0], bobSeesAliceShared.GetProperty("owner").GetString());

            // Bob cannot update alice's workspace name
            var bobUpdateName = AsUser(HttpMethod.Put,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(aliceSharedUrn)}", bobId, bobEmail);
            bobUpdateName.Content = JsonContent.Create(new { applicationName = "Hijacked" });
            var bobUpdateNameResp = await Client.SendAsync(bobUpdateName);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, bobUpdateNameResp.StatusCode);

            // Bob cannot update alice's workspace description
            var bobUpdateDesc = AsUser(HttpMethod.Put,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(aliceSharedUrn)}", bobId, bobEmail);
            bobUpdateDesc.Content = JsonContent.Create(new { description = "Hijacked" });
            var bobUpdateDescResp = await Client.SendAsync(bobUpdateDesc);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, bobUpdateDescResp.StatusCode);

            // List bob's workspaces â€” verify no changes to alice's workspace
            var bobList2 = await DiscoverAs(bobId, bobEmail);
            var unchanged = FindByUrn(bobList2, aliceSharedUrn);
            Assert.Equal("Alice's shared", unchanged.GetProperty("description").GetProperty("text").GetString());

            // Bob creates his own 'Shared'
            var bobCreateReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", bobId, bobEmail);
            bobCreateReq.Content = JsonContent.Create(new { applicationName = "Shared", description = "Bob's shared" });
            var bobCreateResp = await Client.SendAsync(bobCreateReq);
            bobCreateResp.EnsureSuccessStatusCode();
            var bobCreated = await bobCreateResp.Content.ReadFromJsonAsync<JsonElement>();
            bobSharedUrn = bobCreated.GetProperty("applicationUri").GetString()!;

            // Bob sees 2 'Shared' with different owners
            var bobList3 = await DiscoverAs(bobId, bobEmail);
            var sharedWs = bobList3.EnumerateArray()
                .Where(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "Shared")
                .ToList();
            Assert.Equal(2, sharedWs.Count);
            var owners = sharedWs.Select(ws => ws.GetProperty("owner").GetString()).OrderBy(o => o).ToList();
            Assert.Contains(aliceEmail.Split('@')[0], owners);
            Assert.Contains(bobEmail.Split('@')[0], owners);

            // Bob cannot create a second 'Shared' â€” duplicate name
            var bobDupeReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", bobId, bobEmail);
            bobDupeReq.Content = JsonContent.Create(new { applicationName = "Shared" });
            var bobDupeResp = await Client.SendAsync(bobDupeReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bobDupeResp.StatusCode);

            // Add alice to ACL for bob's Shared
            var bobAclReq = AsUser(HttpMethod.Put,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(bobSharedUrn)}", bobId, bobEmail);
            bobAclReq.Content = JsonContent.Create(new { acl = new[] { aliceEmail } });
            var bobAclResp = await Client.SendAsync(bobAclReq);
            bobAclResp.EnsureSuccessStatusCode();

            // Bob sees updated ACL
            var bobList4 = await DiscoverAs(bobId, bobEmail);
            var bobSharedWs = FindByUrn(bobList4, bobSharedUrn);
            Assert.Contains(aliceEmail, bobSharedWs.GetProperty("acl").EnumerateArray().Select(a => a.GetString()));

            // Alice sees 2 'Shared' workspaces with different owners
            var aliceList2 = await DiscoverAs(aliceId, aliceEmail);
            var aliceSharedWs = aliceList2.EnumerateArray()
                .Where(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "Shared")
                .ToList();
            Assert.Equal(2, aliceSharedWs.Count);

            // Delete alice's Shared
            var delAlice = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(aliceSharedUrn)}", aliceId, aliceEmail);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delAlice)).StatusCode);
            aliceSharedUrn = null;

            // Bob no longer sees alice's Shared
            var bobList5 = await DiscoverAs(bobId, bobEmail);
            var bobShared5 = bobList5.EnumerateArray()
                .Where(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "Shared")
                .ToList();
            Assert.Single(bobShared5);
            Assert.Equal(bobSharedUrn, bobShared5[0].GetProperty("applicationUri").GetString());

            // Delete bob's Shared
            var delBob = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(bobSharedUrn)}", bobId, bobEmail);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delBob)).StatusCode);
            bobSharedUrn = null;

            // Bob no longer sees any 'Shared'
            var bobList6 = await DiscoverAs(bobId, bobEmail);
            Assert.DoesNotContain(bobList6.EnumerateArray(),
                ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == "Shared");
        }
        finally
        {
            // Clean up on error
            if (aliceSharedUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(aliceSharedUrn)}", aliceId, aliceEmail);
                await Client.SendAsync(del);
            }
            if (bobSharedUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(bobSharedUrn)}", bobId, bobEmail);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Workspace_SharedUserIsReadOnly()
    {
        // A workspace shared via ACL is read-only for the recipient: the owner
        // (alice) can write, the shared user (bob) can read but every mutating
        // endpoint returns 403.
        const string aliceId = "alice-ro-001", aliceEmail = "alice-ro@test.net";
        const string bobId = "bob-ro-001", bobEmail = "bob-ro@test.net";
        const string wsName = "ReadOnlyShare";
        const string modelUri = "http://test.example.org/UA/ReadOnlyShare/";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(aliceId, aliceEmail, wsName);

            // Alice creates the workspace and adds a private model.
            var createReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", aliceId, aliceEmail);
            createReq.Content = JsonContent.Create(new { applicationName = wsName, description = "Owner writable" });
            var createResp = await Client.SendAsync(createReq);
            createResp.EnsureSuccessStatusCode();
            wsUrn = (await createResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            var addModelReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", aliceId, aliceEmail);
            addModelReq.Headers.Add("OpcUa-Server", wsUrn);
            addModelReq.Content = JsonContent.Create(new { uri = modelUri, name = "RO", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            (await Client.SendAsync(addModelReq)).EnsureSuccessStatusCode();

            // Alice shares it with bob.
            var aclReq = AsUser(HttpMethod.Put,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", aliceId, aliceEmail);
            aclReq.Content = JsonContent.Create(new { acl = new[] { bobEmail } });
            (await Client.SendAsync(aclReq)).EnsureSuccessStatusCode();

            // canWrite is reported per-user: true for the owner, false for the shared user.
            var aliceWs = FindByUrn(await DiscoverAs(aliceId, aliceEmail), wsUrn);
            Assert.True(aliceWs.GetProperty("canWrite").GetBoolean());
            Assert.True(aliceWs.GetProperty("isOwner").GetBoolean());

            var bobWs = FindByUrn(await DiscoverAs(bobId, bobEmail), wsUrn);
            Assert.False(bobWs.GetProperty("canWrite").GetBoolean());
            Assert.False(bobWs.GetProperty("isOwner").GetBoolean());

            // Bob CAN read: listing models succeeds and shows the shared workspace's models.
            var bobModels = await ListModels(bobId, bobEmail, wsUrn);
            Assert.Contains(bobModels.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == modelUri);

            // Bob CAN read: querying types succeeds.
            var bobQueryTypes = AsUser(HttpMethod.Get, "/api/opcua/v1/query/types", bobId, bobEmail);
            bobQueryTypes.Headers.Add("OpcUa-Server", wsUrn);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await Client.SendAsync(bobQueryTypes)).StatusCode);

            // Bob CANNOT write: adding a model is forbidden.
            var bobAddModel = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", bobId, bobEmail);
            bobAddModel.Headers.Add("OpcUa-Server", wsUrn);
            bobAddModel.Content = JsonContent.Create(new
            {
                uri = "http://test.example.org/UA/BobSneaks/",
                name = "BobSneaks",
                version = "1.0.0",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Client.SendAsync(bobAddModel)).StatusCode);

            // Bob CANNOT write: creating a top-level node is forbidden.
            var bobCreateNode = AsUser(HttpMethod.Post, "/api/opcua/v1/nodes", bobId, bobEmail);
            bobCreateNode.Headers.Add("OpcUa-Server", wsUrn);
            bobCreateNode.Content = JsonContent.Create(new
            {
                modelUri,
                nodeClass = "ObjectType",
                browseName = "BobType",
                displayName = "BobType",
                referenceTypeId = "i=45"
            });
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Client.SendAsync(bobCreateNode)).StatusCode);

            // Owner (alice) can still write the same endpoint bob was forbidden from:
            // adding a model succeeds.
            var aliceAddModel = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", aliceId, aliceEmail);
            aliceAddModel.Headers.Add("OpcUa-Server", wsUrn);
            aliceAddModel.Content = JsonContent.Create(new
            {
                uri = "http://test.example.org/UA/AliceWrites/",
                name = "AliceWrites",
                version = "1.0.0",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var aliceAddModelResp = await Client.SendAsync(aliceAddModel);
            Assert.True(aliceAddModelResp.IsSuccessStatusCode,
                $"Owner add-model should succeed, got {aliceAddModelResp.StatusCode}: " +
                await aliceAddModelResp.Content.ReadAsStringAsync());
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", aliceId, aliceEmail);
                await Client.SendAsync(del);
            }
        }
    }

    private async Task<JsonElement> DiscoverAs(string userId, string email)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/discovery", userId, email);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
    }

    private static JsonElement FindByUrn(JsonElement results, string urn)
    {
        var ws = results.EnumerateArray()
            .FirstOrDefault(ws => ws.GetProperty("applicationUri").GetString() == urn);
        Assert.NotEqual(default, ws);
        return ws;
    }

    [Fact]
    public async Task Workspace_ModelAddRemove()
    {
        const string userId = "model-test-001";
        const string email = "modeltest@test.net";
        const string wsName = "ModelTest";
        const string modelUri = "http://test.example.org/UA/ModelTest/";
        string? wsUrn = null;

        try
        {
            // Check if workspace exists from a prior failed run; if so, delete it
            wsUrn = await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Model add/remove test"
            });
            var createResp = await Client.SendAsync(createReq);
            createResp.EnsureSuccessStatusCode();
            var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = created.GetProperty("applicationUri").GetString()!;

            // List models â€” new workspace should have the base UA model auto-added
            var models0 = await ListModels(userId, email, wsUrn);
            Assert.Equal(1, models0.GetProperty("totalCount").GetInt32());
            var coreModel = models0.GetProperty("results").EnumerateArray().First();
            Assert.Equal("http://opcfoundation.org/UA/", coreModel.GetProperty("uri").GetString());
            Assert.False(coreModel.GetProperty("isPrivate").GetBoolean());
            Assert.True(coreModel.GetProperty("isReadOnly").GetBoolean());

            // Add a new private model
            var addReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
            addReq.Headers.Add("OpcUa-Server", wsUrn);
            addReq.Content = JsonContent.Create(new
            {
                uri = modelUri,
                name = "ModelTest",
                version = "1.0.0",
                description = "Test model",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var addResp = await Client.SendAsync(addReq);
            addResp.EnsureSuccessStatusCode();
            var addedModel = await addResp.Content.ReadFromJsonAsync<JsonElement>();
            var modelId = addedModel.GetProperty("id").GetString()!;
            Assert.Equal(modelUri, addedModel.GetProperty("uri").GetString());
            Assert.Equal("ModelTest", addedModel.GetProperty("name").GetString());
            Assert.True(addedModel.GetProperty("isPrivate").GetBoolean());

            // List models â€” verify model appears alongside the core model
            var models1 = await ListModels(userId, email, wsUrn);
            Assert.Equal(2, models1.GetProperty("totalCount").GetInt32());
            var listed = models1.GetProperty("results").EnumerateArray()
                .First(m => m.GetProperty("uri").GetString() == modelUri);
            Assert.Equal(modelUri, listed.GetProperty("uri").GetString());
            Assert.Equal("ModelTest", listed.GetProperty("name").GetString());
            Assert.Equal("1.0.0", listed.GetProperty("version").GetString());
            Assert.True(listed.GetProperty("isPrivate").GetBoolean());
            Assert.False(listed.GetProperty("isReadOnly").GetBoolean());

            // Update model metadata. Name/description are editable while checked out; the version
            // is owned by the check-in lifecycle, so change it after a "keep" check-in.
            var updateReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            updateReq.Headers.Add("OpcUa-Server", wsUrn);
            updateReq.Content = JsonContent.Create(new
            {
                name = "ModelTestRenamed",
                description = "Updated description"
            });
            var updateResp = await Client.SendAsync(updateReq);
            updateResp.EnsureSuccessStatusCode();
            var updated = await updateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ModelTestRenamed", updated.GetProperty("name").GetString());

            var checkinReq = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
            checkinReq.Headers.Add("OpcUa-Server", wsUrn);
            checkinReq.Content = JsonContent.Create(new { action = "keep" });
            (await Client.SendAsync(checkinReq)).EnsureSuccessStatusCode();

            var versionReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            versionReq.Headers.Add("OpcUa-Server", wsUrn);
            versionReq.Content = JsonContent.Create(new { version = "2.0.0" });
            var versionResp = await Client.SendAsync(versionReq);
            versionResp.EnsureSuccessStatusCode();
            Assert.Equal("2.0.0", (await versionResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());

            // List models â€” verify updated properties
            var models2 = await ListModels(userId, email, wsUrn);
            var listedUpdated = models2.GetProperty("results").EnumerateArray()
                .First(m => m.GetProperty("uri").GetString() == modelUri);
            Assert.Equal("ModelTestRenamed", listedUpdated.GetProperty("name").GetString());
            Assert.Equal("2.0.0", listedUpdated.GetProperty("version").GetString());

            // Delete the model
            var delModelReq = AsUser(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            delModelReq.Headers.Add("OpcUa-Server", wsUrn);
            var delModelResp = await Client.SendAsync(delModelReq);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, delModelResp.StatusCode);

            // List models â€” only the core model should remain
            var models3 = await ListModels(userId, email, wsUrn);
            Assert.Equal(1, models3.GetProperty("totalCount").GetInt32());
            Assert.Equal("http://opcfoundation.org/UA/",
                models3.GetProperty("results").EnumerateArray().First().GetProperty("uri").GetString());

            // Delete workspace
            var delReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    /// <summary>
    /// If a workspace with the given name exists for the user, delete it and return null.
    /// Used to clean up state from prior failed test runs.
    /// </summary>
    private async Task<string?> FindAndDeleteWorkspace(string userId, string email, string wsName)
    {
        var list = await DiscoverAs(userId, email);
        var existing = list.EnumerateArray()
            .FirstOrDefault(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == wsName);

        if (existing.ValueKind == JsonValueKind.Undefined)
            return null;

        var urn = existing.GetProperty("applicationUri").GetString()!;
        var del = AsUser(HttpMethod.Delete,
            $"/api/opcua/v1/servers/{Uri.EscapeDataString(urn)}", userId, email);
        var resp = await Client.SendAsync(del);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, resp.StatusCode);
        return null;
    }

    private async Task<JsonElement> ListModels(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Model_CreateEditDelete()
    {
        const string userId = "model-ced-001";
        const string email = "modelced@test.net";
        const string wsName = "ModelCED";
        const string modelUri = "http://test.example.org/UA/ModelCED/";
        const string modelUri2 = "http://test.example.org/UA/ModelCED2/";
        string? wsUrn = null;

        try
        {
            // Check for workspace and delete if present
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create a workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Model create/edit/delete test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            var createdWs = await createWsResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = createdWs.GetProperty("applicationUri").GetString()!;

            // Add new model
            var addReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
            addReq.Headers.Add("OpcUa-Server", wsUrn);
            addReq.Content = JsonContent.Create(new
            {
                uri = modelUri,
                name = "TestModel",
                version = "1.0.0",
                description = "Original description",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var addResp = await Client.SendAsync(addReq);
            addResp.EnsureSuccessStatusCode();
            var addedModel = await addResp.Content.ReadFromJsonAsync<JsonElement>();
            var modelId = addedModel.GetProperty("id").GetString()!;
            Assert.Equal(modelUri, addedModel.GetProperty("uri").GetString());
            Assert.Equal("TestModel", addedModel.GetProperty("name").GetString());

            // List models in workspace â€” verify new model
            var models1 = await ListModels(userId, email, wsUrn);
            Assert.Equal(2, models1.GetProperty("totalCount").GetInt32()); // core + new
            var listed = models1.GetProperty("results").EnumerateArray()
                .First(m => m.GetProperty("uri").GetString() == modelUri);
            Assert.Equal("TestModel", listed.GetProperty("name").GetString());
            Assert.Equal("1.0.0", listed.GetProperty("version").GetString());
            Assert.Equal("Original description", listed.GetProperty("description").GetProperty("text").GetString());

            // Name and description are editable while the model is checked out; the version
            // is owned by the checkout/check-in lifecycle and can't be changed via PUT until
            // the model is checked in.
            var updateReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            updateReq.Headers.Add("OpcUa-Server", wsUrn);
            updateReq.Content = JsonContent.Create(new
            {
                name = "TestModelRenamed",
                description = "Updated description"
            });
            var updateResp = await Client.SendAsync(updateReq);
            updateResp.EnsureSuccessStatusCode();
            var updated = await updateResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("TestModelRenamed", updated.GetProperty("name").GetString());
            Assert.Equal("Updated description", updated.GetProperty("description").GetProperty("text").GetString());
            Assert.Equal("1.0.0", updated.GetProperty("version").GetString()); // unchanged while checked out

            // A version change while checked out is rejected.
            var versionWhileEditableReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            versionWhileEditableReq.Headers.Add("OpcUa-Server", wsUrn);
            versionWhileEditableReq.Content = JsonContent.Create(new { version = "2.0.0" });
            var versionWhileEditableResp = await Client.SendAsync(versionWhileEditableReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, versionWhileEditableResp.StatusCode);

            // Check the model in ("keep" locks the working copy without publishing); the
            // version is then editable.
            var checkinReq = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
            checkinReq.Headers.Add("OpcUa-Server", wsUrn);
            checkinReq.Content = JsonContent.Create(new { action = "keep" });
            (await Client.SendAsync(checkinReq)).EnsureSuccessStatusCode();

            var versionReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            versionReq.Headers.Add("OpcUa-Server", wsUrn);
            versionReq.Content = JsonContent.Create(new { version = "2.0.0" });
            var versionResp = await Client.SendAsync(versionReq);
            versionResp.EnsureSuccessStatusCode();
            var versioned = await versionResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("2.0.0", versioned.GetProperty("version").GetString());

            // List models in workspace â€” verify updated model
            var models2 = await ListModels(userId, email, wsUrn);
            var listedUpdated = models2.GetProperty("results").EnumerateArray()
                .First(m => m.GetProperty("uri").GetString() == modelUri);
            Assert.Equal("TestModelRenamed", listedUpdated.GetProperty("name").GetString());
            Assert.Equal("2.0.0", listedUpdated.GetProperty("version").GetString());
            Assert.Equal("Updated description", listedUpdated.GetProperty("description").GetProperty("text").GetString());

            // Update URI â€” verify change rejected
            var updateUriReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            updateUriReq.Headers.Add("OpcUa-Server", wsUrn);
            updateUriReq.Content = JsonContent.Create(new
            {
                uri = "http://test.example.org/UA/Changed/"
            });
            var updateUriResp = await Client.SendAsync(updateUriReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, updateUriResp.StatusCode);

            // Add new model with same name â€” verify create rejected (unique within workspace)
            var dupeNameReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
            dupeNameReq.Headers.Add("OpcUa-Server", wsUrn);
            dupeNameReq.Content = JsonContent.Create(new
            {
                uri = modelUri2,
                name = "TestModelRenamed",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var dupeNameResp = await Client.SendAsync(dupeNameReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, dupeNameResp.StatusCode);

            // Add new model with same URI â€” verify create rejected (unique among all models)
            var dupeUriReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
            dupeUriReq.Headers.Add("OpcUa-Server", wsUrn);
            dupeUriReq.Content = JsonContent.Create(new
            {
                uri = modelUri,
                name = "DifferentName",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var dupeUriResp = await Client.SendAsync(dupeUriReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, dupeUriResp.StatusCode);

            // Add new model with URI http://opcfoundation.org/UA/DI/ â€” verify create rejected
            var reservedUriReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
            reservedUriReq.Headers.Add("OpcUa-Server", wsUrn);
            reservedUriReq.Content = JsonContent.Create(new
            {
                uri = "http://opcfoundation.org/UA/DI/",
                name = "StolenDI",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var reservedUriResp = await Client.SendAsync(reservedUriReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, reservedUriResp.StatusCode);

            // Delete original model
            var delModelReq = AsUser(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{modelId}", userId, email);
            delModelReq.Headers.Add("OpcUa-Server", wsUrn);
            var delModelResp = await Client.SendAsync(delModelReq);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, delModelResp.StatusCode);

            // List models in workspace â€” verify model gone
            var models3 = await ListModels(userId, email, wsUrn);
            Assert.Equal(1, models3.GetProperty("totalCount").GetInt32());
            Assert.DoesNotContain(models3.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == modelUri);

            // Clean up workspace
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            // Clean up workspace on exit
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Model_UploadUpdateDelete()
    {
        const string userId = "upload-test-001";
        const string email = "uploadtest@test.net";
        const string wsName = "UploadTest";
        const string iaUri = "http://opcfoundation.org/UA/IA/";
        const string diUri = "http://opcfoundation.org/UA/DI/";
        string? wsUrn = null;

        try
        {
            // Check for workspace and delete if present
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Upload/update/delete test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            var createdWs = await createWsResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = createdWs.GetProperty("applicationUri").GetString()!;

            // Upload Opc.Ua.IA.NodeSet2.xml (depends on DI, which depends on core UA)
            var nodeSetPath = Path.Combine(AppContext.BaseDirectory, "NodeSets", "Opc.Ua.IA.NodeSet2.xml");
            Assert.True(File.Exists(nodeSetPath), $"Test NodeSet not found: {nodeSetPath}");

            var uploadReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info/import", userId, email);
            uploadReq.Headers.Add("OpcUa-Server", wsUrn);
            var formContent = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(nodeSetPath));
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
            formContent.Add(fileContent, "file", "Opc.Ua.IA.NodeSet2.xml");
            formContent.Add(new StringContent("Opc.Ua.IA.NodeSet2.xml"), "fileName");
            formContent.Add(new StringContent("0"), "chunkIndex");
            formContent.Add(new StringContent("1"), "totalChunks");
            uploadReq.Content = formContent;

            var uploadResp = await Client.SendAsync(uploadReq);
            var uploadBody = await uploadResp.Content.ReadAsStringAsync();
            Assert.True(uploadResp.IsSuccessStatusCode, $"Upload failed ({uploadResp.StatusCode}): {uploadBody}");

            // List models â€” confirm IA is present (plus core UA; DI may be auto-resolved if available)
            var models1 = await ListModels(userId, email, wsUrn);
            var modelList1 = models1.GetProperty("results").EnumerateArray().ToList();
            Assert.True(modelList1.Count >= 2, $"Expected at least 2 models (UA, IA), got {modelList1.Count}");

            var iaModel = modelList1.FirstOrDefault(m => m.GetProperty("uri").GetString() == iaUri);
            Assert.NotEqual(default, iaModel);
            var iaModelId = iaModel.GetProperty("id").GetString()!;

            // An uploaded NodeSet imports as a private, checked-out working copy so the user
            // can edit it immediately — and the imported version is preserved as-is.
            Assert.True(iaModel.GetProperty("isPrivate").GetBoolean(), "IA should be private (uploaded)");
            Assert.False(iaModel.GetProperty("isReadOnly").GetBoolean(), "IA should be checked out (uploaded)");
            var iaImportedVersion = iaModel.GetProperty("version").GetString();

            // DI may or may not be present (depends on Cloud Library availability in test env)
            var diModel = modelList1.FirstOrDefault(m => m.GetProperty("uri").GetString() == diUri);
            string? diModelId = null;
            if (diModel.ValueKind != JsonValueKind.Undefined)
            {
                diModelId = diModel.GetProperty("id").GetString()!;
                // If DI is present, it should be shared (auto-resolved dependency)
                Assert.False(diModel.GetProperty("isPrivate").GetBoolean(), "DI should be shared (dependency)");
            }

            // IA lives in the reserved OPC Foundation namespace (http://opcfoundation.org/…),
            // so its canonical metadata is read-only even though the upload holds it as a
            // private copy. Both a version edit and a description edit must be rejected.
            var versionReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{iaModelId}", userId, email);
            versionReq.Headers.Add("OpcUa-Server", wsUrn);
            versionReq.Content = JsonContent.Create(new { version = "99.0.0" });
            var versionResp = await Client.SendAsync(versionReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, versionResp.StatusCode);

            var updateReq = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{iaModelId}", userId, email);
            updateReq.Headers.Add("OpcUa-Server", wsUrn);
            updateReq.Content = JsonContent.Create(new
            {
                description = "Updated IA model"
            });
            var updateResp = await Client.SendAsync(updateReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, updateResp.StatusCode);

            // List models â€” confirm the rejected edit left the imported version untouched.
            var models2 = await ListModels(userId, email, wsUrn);
            var iaUpdated = models2.GetProperty("results").EnumerateArray()
                .First(m => m.GetProperty("uri").GetString() == iaUri);
            Assert.Equal(iaImportedVersion, iaUpdated.GetProperty("version").GetString());

            // Delete IA (private model removed â€” shared version should replace it if available)
            var delIaReq = AsUser(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{iaModelId}", userId, email);
            delIaReq.Headers.Add("OpcUa-Server", wsUrn);
            var delIaResp = await Client.SendAsync(delIaReq);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, delIaResp.StatusCode);

            // List models â€” IA should be gone (no shared IA in DB to fall back to)
            var models3 = await ListModels(userId, email, wsUrn);
            var iaAfterDelete = models3.GetProperty("results").EnumerateArray()
                .FirstOrDefault(m => m.GetProperty("uri").GetString() == iaUri);
            Assert.Equal(default, iaAfterDelete);

            // If DI was present, it should still be there (shared, not deleted with IA)
            if (diModelId != null)
            {
                var diAfterDelete = models3.GetProperty("results").EnumerateArray()
                    .FirstOrDefault(m => m.GetProperty("uri").GetString() == diUri);
                Assert.NotEqual(default, diAfterDelete);

                // Delete DI from workspace
                var delDiReq = AsUser(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{diModelId}", userId, email);
                delDiReq.Headers.Add("OpcUa-Server", wsUrn);
                var delDiResp = await Client.SendAsync(delDiReq);
                Assert.Equal(System.Net.HttpStatusCode.NoContent, delDiResp.StatusCode);

                // List models â€” DI should be gone from workspace
                var models4 = await ListModels(userId, email, wsUrn);
                Assert.DoesNotContain(models4.GetProperty("results").EnumerateArray(),
                    m => m.GetProperty("uri").GetString() == diUri);
            }

            // Clean up workspace
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Model_UploadMultiChunk()
    {
        const string userId = "upload-chunk-test-001";
        const string email = "uploadchunktest@test.net";
        const string wsName = "UploadChunkTest";
        const string iaUri = "http://opcfoundation.org/UA/IA/";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Multi-chunk upload test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            var createdWs = await createWsResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = createdWs.GetProperty("applicationUri").GetString()!;

            // Split the NodeSet into several small chunks to exercise the multi-chunk path.
            var nodeSetPath = Path.Combine(AppContext.BaseDirectory, "NodeSets", "Opc.Ua.IA.NodeSet2.xml");
            Assert.True(File.Exists(nodeSetPath), $"Test NodeSet not found: {nodeSetPath}");
            var fileBytes = await File.ReadAllBytesAsync(nodeSetPath);
            const int chunkSize = 4096;
            var totalChunks = (int)Math.Ceiling(fileBytes.Length / (double)chunkSize);
            Assert.True(totalChunks > 1, "Test NodeSet is too small to exercise multi-chunk upload.");

            string? uploadId = null;
            for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                var start = chunkIndex * chunkSize;
                var length = Math.Min(chunkSize, fileBytes.Length - start);

                var uploadReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info/import", userId, email);
                uploadReq.Headers.Add("OpcUa-Server", wsUrn);
                var formContent = new MultipartFormDataContent();
                var chunkContent = new ByteArrayContent(fileBytes, start, length);
                chunkContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
                formContent.Add(chunkContent, "file", "Opc.Ua.IA.NodeSet2.xml");
                formContent.Add(new StringContent("Opc.Ua.IA.NodeSet2.xml"), "fileName");
                formContent.Add(new StringContent(chunkIndex.ToString()), "chunkIndex");
                formContent.Add(new StringContent(totalChunks.ToString()), "totalChunks");
                if (uploadId != null)
                    formContent.Add(new StringContent(uploadId), "uploadId");
                uploadReq.Content = formContent;

                var uploadResp = await Client.SendAsync(uploadReq);
                var uploadBody = await uploadResp.Content.ReadAsStringAsync();
                Assert.True(uploadResp.IsSuccessStatusCode, $"Chunk {chunkIndex} upload failed ({uploadResp.StatusCode}): {uploadBody}");
                var uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadBody);
                uploadId = uploadResult.GetProperty("uploadId").GetString();

                var isComplete = uploadResult.GetProperty("isComplete").GetBoolean();
                Assert.Equal(chunkIndex == totalChunks - 1, isComplete);
            }

            // List models — confirm IA (and its dependencies) landed once all chunks were assembled.
            var models = await ListModels(userId, email, wsUrn);
            var modelList = models.GetProperty("results").EnumerateArray().ToList();
            var iaModel = modelList.FirstOrDefault(m => m.GetProperty("uri").GetString() == iaUri);
            Assert.NotEqual(default, iaModel);
            Assert.True(iaModel.GetProperty("isPrivate").GetBoolean(), "IA should be private (uploaded)");

            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Model_CloudLibraryAddDelete()
    {
        const string userId = "cloudlib-test-001";
        const string email = "cloudlibtest@test.net";
        const string wsName = "CloudLibTest";
        const string roboticsUri = "http://opcfoundation.org/UA/Robotics/";
        string? wsUrn = null;

        try
        {
            // Check for workspace and delete if present
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Cloud Library add/delete test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            var createdWs = await createWsResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = createdWs.GetProperty("applicationUri").GetString()!;

            // Find the Robotics model (latest version) in the Cloud Library
            var roboticsId = await FindCloudLibraryModelId(roboticsUri);

            // Link Robotics into the workspace (triggers dependency resolution from Cloud Library)
            var linkReq = AsUser(HttpMethod.Post,
                $"/api/opcua/v1/namespaces/info/{roboticsId}/link", userId, email);
            linkReq.Headers.Add("OpcUa-Server", wsUrn);
            linkReq.Content = JsonContent.Create(new { isPrivate = false });
            var linkResp = await Client.SendAsync(linkReq);
            var linkBody = await linkResp.Content.ReadAsStringAsync();
            Assert.True(linkResp.IsSuccessStatusCode,
                $"Link failed ({linkResp.StatusCode}): {linkBody}");

            // List models in workspace â€” confirm Robotics and all dependencies
            var models1 = await ListModels(userId, email, wsUrn);
            var modelList1 = models1.GetProperty("results").EnumerateArray().ToList();

            // Robotics depends on DI which depends on UA â€” expect at least 3 models
            var roboticsInWs = modelList1.FirstOrDefault(m =>
                m.GetProperty("uri").GetString() == roboticsUri);
            Assert.NotEqual(default, roboticsInWs);
            var roboticsWsId = roboticsInWs.GetProperty("id").GetString()!;

            var diInWs = modelList1.FirstOrDefault(m =>
                m.GetProperty("uri").GetString() == "http://opcfoundation.org/UA/DI/");
            Assert.NotEqual(default, diInWs);
            var diWsId = diInWs.GetProperty("id").GetString()!;

            Assert.True(modelList1.Count >= 3,
                $"Expected at least 3 models (UA, DI, Robotics), got {modelList1.Count}: " +
                string.Join(", ", modelList1.Select(m => m.GetProperty("uri").GetString())));

            // Delete DI (dependency of Robotics) â€” should fail
            var delDiReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/namespaces/info/{diWsId}", userId, email);
            delDiReq.Headers.Add("OpcUa-Server", wsUrn);
            var delDiResp = await Client.SendAsync(delDiReq);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, delDiResp.StatusCode);

            // Verify DI is still there
            var models2 = await ListModels(userId, email, wsUrn);
            Assert.Contains(models2.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == "http://opcfoundation.org/UA/DI/");

            // Delete Robotics (no other model in workspace depends on it) â€” should succeed
            var delRoboticsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/namespaces/info/{roboticsWsId}", userId, email);
            delRoboticsReq.Headers.Add("OpcUa-Server", wsUrn);
            var delRoboticsResp = await Client.SendAsync(delRoboticsReq);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, delRoboticsResp.StatusCode);

            // List models â€” Robotics should be gone
            var models3 = await ListModels(userId, email, wsUrn);
            Assert.DoesNotContain(models3.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == roboticsUri);

            // Clean up workspace
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Model_AddObjectTypeAndInstantiate()
    {
        const string userId = "instantiate-001";
        const string email = "instantiate@test.net";
        const string wsName = "InstantiateTest";
        const string testModelUri = "http://test.example.org/UA/LegoTest/";
        const string diUri = "http://opcfoundation.org/UA/DI/";
        const string diComponentType = "nsu=http://opcfoundation.org/UA/DI/;i=15063";
        const string diManufacturerBrowseName = "Manufacturer";
        string? wsUrn = null;

        try
        {
            // FindAndDeleteWorkspace
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "ObjectType instantiation test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            var createdWs = await createWsResp.Content.ReadFromJsonAsync<JsonElement>();
            wsUrn = createdWs.GetProperty("applicationUri").GetString()!;

            // Add DI model from CloudLibrary
            var diId = await FindCloudLibraryModelId(diUri);

            var linkDiReq = AsUser(HttpMethod.Post,
                $"/api/opcua/v1/namespaces/info/{diId}/link", userId, email);
            linkDiReq.Headers.Add("OpcUa-Server", wsUrn);
            linkDiReq.Content = JsonContent.Create(new { isPrivate = false });
            var linkDiResp = await Client.SendAsync(linkDiReq);
            Assert.True(linkDiResp.IsSuccessStatusCode,
                $"Link DI failed: {await linkDiResp.Content.ReadAsStringAsync()}");

            // Add Test model
            var addTestReq = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
            addTestReq.Headers.Add("OpcUa-Server", wsUrn);
            addTestReq.Content = JsonContent.Create(new
            {
                uri = testModelUri,
                name = "LegoTest",
                version = "1.0.0",
                license = "MIT",
                copyrightHolder = "Test Copyright Holder"
            });
            var addTestResp = await Client.SendAsync(addTestReq);
            addTestResp.EnsureSuccessStatusCode();
            var testModel = await addTestResp.Content.ReadFromJsonAsync<JsonElement>();
            var testModelId = testModel.GetProperty("id").GetString()!;

            // List models â€” confirm DI and Test are present
            var models = await ListModels(userId, email, wsUrn);
            var modelList = models.GetProperty("results").EnumerateArray().ToList();
            Assert.Contains(modelList, m => m.GetProperty("uri").GetString() == diUri);
            Assert.Contains(modelList, m => m.GetProperty("uri").GetString() == testModelUri);

            // Helper to make requests with OpcUa-Server header
            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            // Create Test:LegoType ObjectType as subtype of DI:ComponentType
            var createTypeReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(diComponentType)}/children");
            createTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "LegoType",
                displayName = "LegoType",
                referenceTypeId = "i=45" // HasSubtype
            });
            var createTypeResp = await Client.SendAsync(createTypeReq);
            var createTypeBody = await createTypeResp.Content.ReadAsStringAsync();
            Assert.True(createTypeResp.IsSuccessStatusCode,
                $"CreateType failed ({createTypeResp.StatusCode}): {createTypeBody}");
            var legoTypeNodeId = JsonSerializer.Deserialize<JsonElement>(createTypeBody)
                .GetProperty("nodeId").GetString()!;

            // Add child DI:Manufacturer property to LegoType
            var addMfgReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(legoTypeNodeId)}/children");
            addMfgReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "Variable",
                browseName = diManufacturerBrowseName,
                browseNameModelUri = diUri,
                displayName = diManufacturerBrowseName,
                referenceTypeId = "i=46", // HasProperty
                typeDefinitionId = "i=68", // PropertyType
                modellingRuleId = "i=78", // Mandatory
                dataType = "i=21" // LocalizedText
            });
            var addMfgResp = await Client.SendAsync(addMfgReq);
            var addMfgBody = await addMfgResp.Content.ReadAsStringAsync();
            Assert.True(addMfgResp.IsSuccessStatusCode,
                $"AddManufacturer failed ({addMfgResp.StatusCode}): {addMfgBody}");
            var mfgNodeId = JsonSerializer.Deserialize<JsonElement>(addMfgBody)
                .GetProperty("nodeId").GetString()!;

            // Set DI:Manufacturer value to "Acme"
            var setValueReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/nodes/{Slug(mfgNodeId)}");
            setValueReq.Content = JsonContent.Create(new
            {
                value = "Acme"
            });
            var setValueResp = await Client.SendAsync(setValueReq);
            Assert.True(setValueResp.IsSuccessStatusCode,
                $"SetValue failed ({setValueResp.StatusCode}): {await setValueResp.Content.ReadAsStringAsync()}");

            // Instantiate Test:Lego from LegoType under LegoType (with Organizes ref added after)
            var instantiateReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/types/object-types/{Slug(legoTypeNodeId)}/instantiate");
            instantiateReq.Content = JsonContent.Create(new
            {
                parentNodeId = legoTypeNodeId,
                modelUri = testModelUri,
                browseName = "Lego",
                displayName = "Lego",
                referenceTypeId = "i=47" // HasComponent
            });
            var instantiateResp = await Client.SendAsync(instantiateReq);
            var instantiateBody = await instantiateResp.Content.ReadAsStringAsync();
            Assert.True(instantiateResp.IsSuccessStatusCode,
                $"Instantiate failed ({instantiateResp.StatusCode}): {instantiateBody}");
            var instantiatedNodes = JsonSerializer.Deserialize<JsonElement>(instantiateBody);
            var legoNode = instantiatedNodes.GetProperty("results").EnumerateArray().First();
            var legoNodeId = legoNode.GetProperty("nodeId").GetString()!;

            // Add inverse Organizes reference from UA:Objects (i=85) to Test:Lego
            var addRefReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(legoNodeId)}/references");
            addRefReq.Content = JsonContent.Create(new
            {
                referenceTypeId = "i=35", // Organizes
                targetNodeId = "i=85",    // Objects folder
                isForward = false         // Inverse: Objects organizes Lego
            });
            var addRefResp = await Client.SendAsync(addRefReq);
            Assert.True(addRefResp.IsSuccessStatusCode,
                $"AddReference failed ({addRefResp.StatusCode}): {await addRefResp.Content.ReadAsStringAsync()}");

            // Download Test NodeSet
            var exportReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/namespaces/info/{testModelId}/export?format=xml");
            var exportResp = await Client.SendAsync(exportReq);
            Assert.True(exportResp.IsSuccessStatusCode,
                $"Export failed ({exportResp.StatusCode}): {await exportResp.Content.ReadAsStringAsync()}");
            var nodeSetXml = await exportResp.Content.ReadAsStringAsync();

            // Parse the NodeSet XML for verification
            var nsDoc = System.Xml.Linq.XDocument.Parse(nodeSetXml);
            var uaNs = System.Xml.Linq.XNamespace.Get("http://opcfoundation.org/UA/2011/03/UANodeSet.xsd");

            // Verify Test:LegoType ObjectType exists with correct SuperType
            var objectTypes = nsDoc.Descendants(uaNs + "UAObjectType").ToList();
            var legoType = objectTypes.FirstOrDefault(ot =>
                (ot.Attribute("BrowseName")?.Value ?? "").Contains("LegoType"));
            Assert.NotNull(legoType);

            // Check HasSubtype reference to ComponentType (may be alias "HasSubtype" or "i=45")
            var legoTypeRefs = legoType.Element(uaNs + "References")?.Elements(uaNs + "Reference").ToList();
            Assert.NotNull(legoTypeRefs);
            var subtypeRef = legoTypeRefs.FirstOrDefault(r =>
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                return (rt == "HasSubtype" || rt == "i=45") &&
                    r.Attribute("IsForward")?.Value == "false";
            });
            Assert.NotNull(subtypeRef);

            // Verify DI:Manufacturer child on LegoType with ModellingRule=Mandatory
            var legoTypeNodeIdVal = legoType.Attribute("NodeId")?.Value;
            var variables = nsDoc.Descendants(uaNs + "UAVariable").ToList();
            var mfgOnType = variables.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("Manufacturer") &&
                v.Attribute("ParentNodeId")?.Value == legoTypeNodeIdVal);
            Assert.True(mfgOnType != null,
                $"Manufacturer not found as child of LegoType (ParentNodeId={legoTypeNodeIdVal}). " +
                $"Variables: {string.Join(", ", variables.Select(v => $"{v.Attribute("BrowseName")?.Value}@{v.Attribute("ParentNodeId")?.Value}"))}");

            // Test:Lego Object exists
            var objects = nsDoc.Descendants(uaNs + "UAObject").ToList();
            var legoObj = objects.FirstOrDefault(o =>
                (o.Attribute("BrowseName")?.Value ?? "").Contains("Lego") &&
                !(o.Attribute("BrowseName")?.Value ?? "").Contains("LegoType"));
            Assert.True(legoObj != null,
                $"Lego Object not found in exported NodeSet. UAObjects: [{string.Join(", ", objects.Select(o => o.Attribute("BrowseName")?.Value))}]. " +
                $"All elements: [{string.Join(", ", nsDoc.Root!.Elements().Select(e => $"{e.Name.LocalName}({e.Attribute("BrowseName")?.Value})"))}]");

            // Test:Lego has HasTypeDefinition reference to LegoType
            var legoObjRefs = legoObj.Element(uaNs + "References")?.Elements(uaNs + "Reference").ToList()
                ?? new List<System.Xml.Linq.XElement>();
            var refDump = string.Join("\n", legoObjRefs.Select(r =>
                $"  RT={r.Attribute("ReferenceType")?.Value} Fwd={r.Attribute("IsForward")?.Value} -> {r.Value}"));
            var typeDefRef = legoObjRefs.FirstOrDefault(r =>
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                return rt == "HasTypeDefinition" || rt == "i=40";
            });
            Assert.True(typeDefRef != null,
                $"HasTypeDefinition not found on Lego. Refs:\n{refDump}\nLegoObj XML:\n{legoObj}");

            // Test:Lego has Organizes reference to Objects folder
            var organizesRef = legoObjRefs.FirstOrDefault(r =>
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                return (rt == "Organizes" || rt == "i=35") &&
                    r.Attribute("IsForward")?.Value == "false";
            });
            Assert.NotNull(organizesRef);

            // Test:Lego Object has DI:Manufacturer Property (instantiated from type)
            var legoObjNodeIdVal = legoObj.Attribute("NodeId")?.Value;
            var mfgOnInstance = variables.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("Manufacturer") &&
                v.Attribute("ParentNodeId")?.Value == legoObjNodeIdVal);
            Assert.True(mfgOnInstance != null,
                $"Manufacturer not found as child of Lego instance (ParentNodeId={legoObjNodeIdVal})");

            // Instance Manufacturer should NOT have a ModellingRule
            var instanceMfgRefs = mfgOnInstance!.Element(uaNs + "References")?
                .Elements(uaNs + "Reference").ToList() ?? new();
            var hasModellingRule = instanceMfgRefs.Any(r =>
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                return rt == "HasModellingRule" || rt == "i=37";
            });
            Assert.False(hasModellingRule,
                "Instance Manufacturer should not have a ModellingRule");

            // Cleanup Workspace
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Theory]
    [InlineData("i=1", -1, null, "true", "false", "Boolean, Scalar")]         // Boolean, Scalar
    [InlineData("i=5", 1, "0", "[10,20,30]", "[40,50,60]", "UInt16, OneDimensional")]  // UInt16, OneDimensional
    [InlineData("i=6", -1, null, "42", "99", "Int32, Scalar")]            // Int32, Scalar
    [InlineData("i=11", 2, "3,3", "[1.1,2.2,3.3,4.4,5.5,6.6,7.7,8.8,9.9]",
                                   "[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9]", "Double, TwoDimensional")] // Double, 2D (flat per OPC UA)
    [InlineData("i=12", -1, null, "Hello World", "Goodbye World", "String, Scalar")]          // String, Scalar
    public async Task Model_AddObjectTypeWithVariableAndInstantiate(
        string dataTypeId, int valueRank, string? arrayDimensions,
        string typeValue, string panelValue, string label)
    {
        var userId = $"vartest-{label.Replace(", ", "-").Replace(" ", "").ToLowerInvariant()}";
        var email = $"{userId}@test.net";
        var wsName = $"VarTest_{label.Replace(", ", "_")}";
        const string testModelUri = "http://test.example.org/UA/VarTest/";
        const string diUri = "http://opcfoundation.org/UA/DI/";
        const string diFunctionalGroupType = "nsu=http://opcfoundation.org/UA/DI/;i=1005";
        const string diUIElementType = "nsu=http://opcfoundation.org/UA/DI/;i=6244";
        string? wsUrn = null;

        try
        {
            // FindAndDeleteWorkspace
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create Workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = $"Variable instantiation test: {label}"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            // Add DI Model from Cloud Library
            var diId = await FindCloudLibraryModelId(diUri);
            var linkDiReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{diId}/link");
            linkDiReq.Content = JsonContent.Create(new { isPrivate = false });
            (await Client.SendAsync(linkDiReq)).EnsureSuccessStatusCode();

            // Add Test model
            var addTestReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addTestReq.Content = JsonContent.Create(new { uri = testModelUri, name = "VarTest", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            var addTestResp = await Client.SendAsync(addTestReq);
            addTestResp.EnsureSuccessStatusCode();
            var testModelId = (await addTestResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            // --- Create types ---

            // Create Test:PanelType subtype of DI:FunctionalGroupType
            var createPanelTypeReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(diFunctionalGroupType)}/children");
            createPanelTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "PanelType",
                displayName = "PanelType",
                referenceTypeId = "i=45"
            });
            var panelTypeNodeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(createPanelTypeReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            // Create Test:DisplayElementType subtype of DI:UIElementType
            var createDispTypeReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(diUIElementType)}/children");
            createDispTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "VariableType",
                browseName = "DisplayElementType",
                displayName = "DisplayElementType",
                referenceTypeId = "i=45",
                dataType = dataTypeId,
                valueRank = valueRank,
                arrayDimensions = arrayDimensions
            });
            var dispTypeNodeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(createDispTypeReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            // Helper: create PUT body with value as proper JSON type.
            // If jsonValue is valid JSON (array, number, bool), use as-is.
            // Otherwise treat as a string and quote it.
            StringContent MakeValueBody(string jsonValue)
            {
                // Try parsing as JSON â€” if it fails, it's a plain string that needs quoting
                try { JsonSerializer.Deserialize<JsonElement>(jsonValue); }
                catch { jsonValue = JsonSerializer.Serialize(jsonValue); }
                return new($"{{\"value\":{jsonValue}}}", System.Text.Encoding.UTF8, "application/json");
            }

            // Set DisplayElementType value
            var setDispValueReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/nodes/{Slug(dispTypeNodeId)}");
            setDispValueReq.Content = MakeValueBody(typeValue);
            (await Client.SendAsync(setDispValueReq)).EnsureSuccessStatusCode();

            // Add UA:Definition property to DisplayElementType
            var addDefOnTypeReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(dispTypeNodeId)}/children");
            addDefOnTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "Variable",
                browseName = "Definition",
                browseNameModelUri = "http://opcfoundation.org/UA/",
                displayName = "Definition",
                referenceTypeId = "i=46",
                typeDefinitionId = "i=68",
                modellingRuleId = "i=78",
                dataType = "i=12",
                valueRank = -1
            });
            var defOnTypeNodeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(addDefOnTypeReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            // Set Definition value on type
            var setDefTypeValueReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/nodes/{Slug(defOnTypeNodeId)}");
            setDefTypeValueReq.Content = MakeValueBody("\"Type-level definition\"");
            (await Client.SendAsync(setDefTypeValueReq)).EnsureSuccessStatusCode();

            // --- Add UIElement to PanelType ---

            // Add DI:UIElement variable to PanelType
            var addUIElemReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(panelTypeNodeId)}/children");
            addUIElemReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "Variable",
                browseName = "UIElement",
                browseNameModelUri = diUri,
                displayName = "UIElement",
                referenceTypeId = "i=47",
                typeDefinitionId = dispTypeNodeId,
                modellingRuleId = "i=78",
                dataType = dataTypeId,
                valueRank = valueRank,
                arrayDimensions = arrayDimensions
            });
            var uiElemNodeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(addUIElemReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            // Set UIElement value on PanelType (different from type value)
            var setUIElemValueReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/nodes/{Slug(uiElemNodeId)}");
            setUIElemValueReq.Content = MakeValueBody(panelValue);
            (await Client.SendAsync(setUIElemValueReq)).EnsureSuccessStatusCode();

            // Add UA:Definition property to UIElement on PanelType
            var addDefOnPanelReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(uiElemNodeId)}/children");
            addDefOnPanelReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "Variable",
                browseName = "Definition",
                browseNameModelUri = "http://opcfoundation.org/UA/",
                displayName = "Definition",
                referenceTypeId = "i=46",
                typeDefinitionId = "i=68",
                modellingRuleId = "i=78",
                dataType = "i=12",
                valueRank = -1
            });
            var defOnPanelNodeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(addDefOnPanelReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            // Set Definition value on PanelType's UIElement (different from type)
            var setDefPanelValueReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/nodes/{Slug(defOnPanelNodeId)}");
            setDefPanelValueReq.Content = MakeValueBody("\"Panel-level definition\"");
            (await Client.SendAsync(setDefPanelValueReq)).EnsureSuccessStatusCode();

            // --- Instantiate Panel ---

            var instantiateReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/types/object-types/{Slug(panelTypeNodeId)}/instantiate");
            instantiateReq.Content = JsonContent.Create(new
            {
                parentNodeId = panelTypeNodeId,
                modelUri = testModelUri,
                browseName = "Panel",
                displayName = "Panel",
                referenceTypeId = "i=47"
            });
            var instantiateResp = await Client.SendAsync(instantiateReq);
            var instantiateBody = await instantiateResp.Content.ReadAsStringAsync();
            Assert.True(instantiateResp.IsSuccessStatusCode,
                $"Instantiate failed ({instantiateResp.StatusCode}): {instantiateBody}");
            var instantiatedNodes = JsonSerializer.Deserialize<JsonElement>(instantiateBody)
                .GetProperty("results").EnumerateArray().ToList();
            var panelNodeId = instantiatedNodes.First().GetProperty("nodeId").GetString()!;

            // Capture instantiated child NodeIds for value verification
            var panelUiElemNodeId = instantiatedNodes
                .FirstOrDefault(n => (n.GetProperty("browseName").GetString() ?? "").Contains("UIElement"))
                .GetProperty("nodeId").GetString();
            var panelDefNodeId = instantiatedNodes
                .FirstOrDefault(n => (n.GetProperty("browseName").GetString() ?? "").Contains("Definition"))
                .GetProperty("nodeId").GetString();

            // Add inverse Organizes reference from UA:Objects to Test:Panel
            var addRefReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(panelNodeId)}/references");
            addRefReq.Content = JsonContent.Create(new
            {
                referenceTypeId = "i=35",
                targetNodeId = "i=85",
                isForward = false
            });
            (await Client.SendAsync(addRefReq)).EnsureSuccessStatusCode();

            // --- Download and verify ---

            var exportReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/namespaces/info/{testModelId}/export?format=xml");
            var exportResp = await Client.SendAsync(exportReq);
            Assert.True(exportResp.IsSuccessStatusCode,
                $"Export failed: {await exportResp.Content.ReadAsStringAsync()}");
            var nodeSetXml = await exportResp.Content.ReadAsStringAsync();
            var nsDoc = System.Xml.Linq.XDocument.Parse(nodeSetXml);
            var uaNs = System.Xml.Linq.XNamespace.Get("http://opcfoundation.org/UA/2011/03/UANodeSet.xsd");

            // Helper to match reference type (alias or numeric)
            bool IsRefType(System.Xml.Linq.XElement r, string alias, string numericId) =>
                (r.Attribute("ReferenceType")?.Value ?? "") is var rt && (rt == alias || rt == numericId);

            var allVars = nsDoc.Descendants(uaNs + "UAVariable").ToList();
            var allObjs = nsDoc.Descendants(uaNs + "UAObject").ToList();
            var allObjTypes = nsDoc.Descendants(uaNs + "UAObjectType").ToList();
            var allVarTypes = nsDoc.Descendants(uaNs + "UAVariableType").ToList();

            // --- Verify PanelType ---
            var panelType = allObjTypes.FirstOrDefault(t =>
                (t.Attribute("BrowseName")?.Value ?? "").Contains("PanelType"));
            Assert.NotNull(panelType);
            var panelTypeNid = panelType.Attribute("NodeId")?.Value;

            // PanelType UIElement component
            var uiElemOnPanelType = allVars.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("UIElement") &&
                v.Attribute("ParentNodeId")?.Value == panelTypeNid);
            Assert.True(uiElemOnPanelType != null,
                $"UIElement not found on PanelType (ParentNodeId={panelTypeNid})");
            var uiElemOnPanelTypeNid = uiElemOnPanelType!.Attribute("NodeId")?.Value;

            // UIElement on PanelType has Mandatory ModellingRule
            var uiElemPTRefs = uiElemOnPanelType.Element(uaNs + "References")?
                .Elements(uaNs + "Reference").ToList() ?? new();
            Assert.True(uiElemPTRefs.Any(r => IsRefType(r, "HasModellingRule", "i=37") && r.Value == "i=78"),
                "UIElement on PanelType should have Mandatory ModellingRule");

            // Verify Value XML encoding in exported NodeSet
            var valueEl = uiElemOnPanelType.Element(uaNs + "Value");
            if (valueEl != null && valueRank >= 2)
            {
                // Multi-dimensional arrays must use <Matrix> encoding
                var matrixEl = valueEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "Matrix");
                Assert.True(matrixEl != null,
                    $"Multi-dimensional value should use <Matrix> encoding. Actual XML: {valueEl}");
                var dimsEl = matrixEl!.Elements().FirstOrDefault(e => e.Name.LocalName == "Dimensions");
                Assert.NotNull(dimsEl);
                var elemsEl = matrixEl.Elements().FirstOrDefault(e => e.Name.LocalName == "Elements");
                Assert.NotNull(elemsEl);
                Assert.True(elemsEl!.Elements().Any(), "Matrix Elements should not be empty");
            }

            // Build alias map from the NodeSet (Aliases section maps names to NodeIds)
            var aliases = nsDoc.Descendants(uaNs + "Alias")
                .ToDictionary(
                    a => a.Value,  // NodeId value like "i=1"
                    a => a.Attribute("Alias")?.Value ?? a.Value); // Alias like "Boolean"
            var expectedDataType = aliases.TryGetValue(dataTypeId, out var alias) ? alias : dataTypeId;

            // UIElement on PanelType has correct DataType
            Assert.Equal(expectedDataType, uiElemOnPanelType.Attribute("DataType")?.Value);

            // Definition property on PanelType's UIElement
            var defOnPanelTypeUIElem = allVars.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("Definition") &&
                v.Attribute("ParentNodeId")?.Value == uiElemOnPanelTypeNid);
            Assert.True(defOnPanelTypeUIElem != null,
                "Definition not found on PanelType's UIElement");

            // Verify PanelType's UIElement value via GET API
            var getUiElemOnPTReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/nodes/{Slug(uiElemNodeId)}");
            var getUiElemOnPTResp = await Client.SendAsync(getUiElemOnPTReq);
            getUiElemOnPTResp.EnsureSuccessStatusCode();
            var uiElemOnPTNode = await getUiElemOnPTResp.Content.ReadFromJsonAsync<JsonElement>();
            {
                var actualStr = uiElemOnPTNode.GetProperty("value").ToString();
                // For scalar strings, compare directly; for JSON values, parse and compare
                try
                {
                    var expected = JsonSerializer.Deserialize<JsonElement>(panelValue);
                    Assert.Equal(expected.ToString(), actualStr, ignoreCase: true);
                }
                catch
                {
                    Assert.Equal(panelValue, actualStr, ignoreCase: true);
                }
            }

            // Verify PanelType's Definition value via GET API
            var getDefOnPTReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/nodes/{Slug(defOnPanelNodeId)}");
            var getDefOnPTResp = await Client.SendAsync(getDefOnPTReq);
            getDefOnPTResp.EnsureSuccessStatusCode();
            var defOnPTNode = await getDefOnPTResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Panel-level definition", defOnPTNode.GetProperty("value").ToString());

            // --- Verify Panel instance ---
            var panelObj = allObjs.FirstOrDefault(o =>
                (o.Attribute("BrowseName")?.Value ?? "").Contains("Panel") &&
                !(o.Attribute("BrowseName")?.Value ?? "").Contains("PanelType"));
            Assert.True(panelObj != null,
                $"Panel Object not found. UAObjects: [{string.Join(", ", allObjs.Select(o => o.Attribute("BrowseName")?.Value))}]");
            var panelObjNid = panelObj!.Attribute("NodeId")?.Value;

            // Panel has HasTypeDefinition to PanelType
            var panelRefs = panelObj.Element(uaNs + "References")?
                .Elements(uaNs + "Reference").ToList() ?? new();
            Assert.True(panelRefs.Any(r => IsRefType(r, "HasTypeDefinition", "i=40")),
                "Panel should have HasTypeDefinition reference");

            // Panel has Organizes reference to Objects
            Assert.True(panelRefs.Any(r => IsRefType(r, "Organizes", "i=35") &&
                r.Attribute("IsForward")?.Value == "false"),
                "Panel should have inverse Organizes reference to Objects");

            // UIElement on Panel instance
            var uiElemOnPanel = allVars.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("UIElement") &&
                v.Attribute("ParentNodeId")?.Value == panelObjNid);
            if (uiElemOnPanel != null)
            {
                var uiElemInstRefs = uiElemOnPanel.Element(uaNs + "References")?
                    .Elements(uaNs + "Reference").ToList() ?? new();

                // No ModellingRule on instance
                Assert.False(uiElemInstRefs.Any(r => IsRefType(r, "HasModellingRule", "i=37")),
                    "UIElement on Panel instance should NOT have ModellingRule");

                // Correct DataType
                Assert.Equal(expectedDataType, uiElemOnPanel.Attribute("DataType")?.Value);

                // Definition on Panel's UIElement
                var uiElemOnPanelNid = uiElemOnPanel.Attribute("NodeId")?.Value;
                var defOnPanelUIElem = allVars.FirstOrDefault(v =>
                    (v.Attribute("BrowseName")?.Value ?? "").Contains("Definition") &&
                    v.Attribute("ParentNodeId")?.Value == uiElemOnPanelNid);
                Assert.True(defOnPanelUIElem != null,
                    "Definition not found on Panel's UIElement instance");
                var defInstRefs = defOnPanelUIElem!.Element(uaNs + "References")?
                    .Elements(uaNs + "Reference").ToList() ?? new();
                Assert.False(defInstRefs.Any(r => IsRefType(r, "HasModellingRule", "i=37")),
                    "Definition on Panel's UIElement should NOT have ModellingRule");

                // Verify Panel's UIElement value â€” should be panelValue (from PanelType override)
                if (panelUiElemNodeId != null)
                {
                    var getUiElemOnPanelReq = WsReq(HttpMethod.Get,
                        $"/api/opcua/v1/nodes/{Slug(panelUiElemNodeId)}");
                    var getUiElemOnPanelResp = await Client.SendAsync(getUiElemOnPanelReq);
                    getUiElemOnPanelResp.EnsureSuccessStatusCode();
                    var uiElemOnPanelNode = await getUiElemOnPanelResp.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal(panelValue, uiElemOnPanelNode.GetProperty("value").ToString(),
                        ignoreCase: true);
                }

                // Verify Panel's Definition value â€” should be "Panel-level definition" (from PanelType override)
                if (panelDefNodeId != null)
                {
                    var getDefOnPanelReq = WsReq(HttpMethod.Get,
                        $"/api/opcua/v1/nodes/{Slug(panelDefNodeId)}");
                    var getDefOnPanelResp = await Client.SendAsync(getDefOnPanelReq);
                    getDefOnPanelResp.EnsureSuccessStatusCode();
                    var defOnPanelNode = await getDefOnPanelResp.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal("Panel-level definition", defOnPanelNode.GetProperty("value").ToString());
                }
            }

            // Cleanup Workspace
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    /// <summary>
    /// Creates an ObjectType with 62 properties covering all OPC UA built-in types
    /// (20 types Ã— 3 ranks = 60, plus Variant 1D and 2D = 62).
    /// Sets default values, instantiates, and verifies all values roundtrip via NodeSet export.
    /// </summary>
    [Fact]
    public async Task Model_AddPropertiesForEveryDataType()
    {
        const string userId = "alldt-001";
        const string email = "alldt@test.net";
        const string wsName = "AllDataTypes";
        const string testModelUri = "http://test.example.org/UA/AllDT/";
        string? wsUrn = null;

        // 20 types (all except XmlElement=16, ExtensionObject=22, DataValue=23, Variant=24)
        // Ã— 3 ranks (Scalar, 1D, 2D) = 60 properties + Variant 1D + Variant 2D = 62
        var typeSpecs = new (string nodeId, int uaType, string name, string scalarVal, string arrayVal)[]
        {
            ("i=1",  1,  "Boolean",        "true",             "[true,false,true]"),
            ("i=2",  2,  "SByte",          "-42",              "[-1,0,1]"),
            ("i=3",  3,  "Byte",           "255",              "[0,128,255]"),
            ("i=4",  4,  "Int16",          "-1000",            "[-100,0,100]"),
            ("i=5",  5,  "UInt16",         "65535",            "[10,20,30]"),
            ("i=6",  6,  "Int32",          "42",               "[1,2,3]"),
            ("i=7",  7,  "UInt32",         "4294967295",       "[100,200,300]"),
            // Part 6 §5.4: Int64 and UInt64 are JSON STRINGS to preserve precision
            // (JSON Number isn't guaranteed to round-trip 64-bit integers losslessly).
            ("i=8",  8,  "Int64",          "\"9223372036854775\"",  "[\"1000\",\"2000\",\"3000\"]"),
            ("i=9",  9,  "UInt64",         "\"18446744073709551\"", "[\"500\",\"600\",\"700\"]"),
            ("i=10", 10, "Float",          "3.14",             "[1.1,2.2,3.3]"),
            ("i=11", 11, "Double",         "2.718281828",      "[0.1,0.2,0.3]"),
            ("i=12", 12, "String",         "Hello",            "[\"A\",\"B\",\"C\"]"),
            // Part 6 §5.4: DateTime is a JSON string in ISO 8601 form, including
            // the time component. Date-only literals ("2026-01-01") are not valid.
            ("i=13", 13, "DateTime",       "\"2026-01-01T00:00:00Z\"",
                                                                "[\"2026-01-01T00:00:00Z\",\"2026-06-15T00:00:00Z\"]"),
            ("i=14", 14, "Guid",           "12345678-1234-1234-1234-123456789abc", "[\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"]"),
            ("i=15", 15, "ByteString",     "AQID",             "[\"AQID\",\"BAUG\"]"),
            ("i=17", 17, "NodeId",         "nsu=http://test.example.org/UA/NID/;i=1234",
                                                                "[\"nsu=http://test.example.org/UA/NID/;i=1234\",\"nsu=http://test.example.org/UA/NID/;i=5678\"]"),
            ("i=18", 18, "ExpandedNodeId", "nsu=http://test.example.org/UA/ENID/;i=9999",
                                                                "[\"nsu=http://test.example.org/UA/ENID/;i=9999\",\"nsu=http://test.example.org/UA/ENID/;s=Tag1\"]"),
            // Part 6: StatusCode is the object {Code, Symbol?}; LocalizedText is
            // {Locale?, Text?}. Bare numbers / strings are not valid Part 6 wire form
            // for these types.
            ("i=19", 19, "StatusCode",     "{\"Code\":0}",
                                                                "[{\"Code\":0},{\"Code\":2147483648}]"),
            ("i=20", 20, "QualifiedName",  "\"nsu=http://test.example.org/UA/QN/;TestName\"",
                                                                "[\"nsu=http://test.example.org/UA/QN/;Alpha\",\"nsu=http://test.example.org/UA/QN/;Beta\"]"),
            ("i=21", 21, "LocalizedText",  "{\"Text\":\"Hello World\"}",
                                                                "[{\"Text\":\"Hello\"},{\"Text\":\"World\"}]"),
        };

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "All data type properties test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            StringContent MakeValueBody(string jsonValue)
            {
                try { JsonSerializer.Deserialize<JsonElement>(jsonValue); }
                catch { jsonValue = JsonSerializer.Serialize(jsonValue); }
                return new($"{{\"value\":{jsonValue}}}", System.Text.Encoding.UTF8, "application/json");
            }

            // Add test model
            var addTestReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addTestReq.Content = JsonContent.Create(new { uri = testModelUri, name = "AllDT", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            var addTestResp = await Client.SendAsync(addTestReq);
            addTestResp.EnsureSuccessStatusCode();
            var testModelId = (await addTestResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            // Create ObjectType
            var createTypeReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=58")}/children");
            createTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "AllPropsType",
                displayName = "AllPropsType",
                referenceTypeId = "i=45"
            });
            var typeNodeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(createTypeReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            // Track created properties: (browseName, expectedJsonValue, nodeId)
            var properties = new List<(string name, string expectedValue, string nodeId)>();

            // Add 60 typed properties (20 types Ã— 3 ranks)
            foreach (var (dtNodeId, uaType, typeName, scalarVal, arrayVal) in typeSpecs)
            {
                // Scalar (ValueRank = -1)
                {
                    var bn = $"{typeName}_Scalar";
                    var req = WsReq(HttpMethod.Post,
                        $"/api/opcua/v1/nodes/{Slug(typeNodeId)}/children");
                    req.Content = JsonContent.Create(new
                    {
                        modelUri = testModelUri,
                        nodeClass = "Variable",
                        browseName = bn,
                        displayName = bn,
                        referenceTypeId = "i=46",
                        typeDefinitionId = "i=68",
                        modellingRuleId = "i=78",
                        dataType = dtNodeId,
                        valueRank = -1
                    });
                    var nid = JsonSerializer.Deserialize<JsonElement>(
                        await (await Client.SendAsync(req)).EnsureSuccessStatusCode()
                            .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

                    var setReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nid)}");
                    setReq.Content = MakeValueBody(scalarVal);
                    (await Client.SendAsync(setReq)).EnsureSuccessStatusCode();

                    properties.Add((bn, scalarVal, nid));
                }

                // OneDimensional (ValueRank = 1)
                {
                    var bn = $"{typeName}_1D";
                    var req = WsReq(HttpMethod.Post,
                        $"/api/opcua/v1/nodes/{Slug(typeNodeId)}/children");
                    req.Content = JsonContent.Create(new
                    {
                        modelUri = testModelUri,
                        nodeClass = "Variable",
                        browseName = bn,
                        displayName = bn,
                        referenceTypeId = "i=46",
                        typeDefinitionId = "i=68",
                        modellingRuleId = "i=78",
                        dataType = dtNodeId,
                        valueRank = 1,
                        arrayDimensions = "0"
                    });
                    var resp = await Client.SendAsync(req);
                    Assert.True(resp.IsSuccessStatusCode,
                        $"CreateChild {bn} (parent={typeNodeId}, dtNodeId={dtNodeId}) failed " +
                        $"({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
                    var nid = JsonSerializer.Deserialize<JsonElement>(
                        await resp.Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

                    var setReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nid)}");
                    setReq.Content = MakeValueBody(arrayVal);
                    (await Client.SendAsync(setReq)).EnsureSuccessStatusCode();

                    properties.Add((bn, arrayVal, nid));
                }

                // TwoDimensional (ValueRank = 2)
                {
                    var bn = $"{typeName}_2D";
                    var req = WsReq(HttpMethod.Post,
                        $"/api/opcua/v1/nodes/{Slug(typeNodeId)}/children");
                    req.Content = JsonContent.Create(new
                    {
                        modelUri = testModelUri,
                        nodeClass = "Variable",
                        browseName = bn,
                        displayName = bn,
                        referenceTypeId = "i=46",
                        typeDefinitionId = "i=68",
                        modellingRuleId = "i=78",
                        dataType = dtNodeId,
                        valueRank = 2,
                        arrayDimensions = "2,2"
                    });
                    var nid = JsonSerializer.Deserialize<JsonElement>(
                        await (await Client.SendAsync(req)).EnsureSuccessStatusCode()
                            .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

                    // 2D value: parse the 1D array as JSON and pad to 4 elements (2x2),
                    // then re-serialize as a flat JSON array. JSON-parse correctly handles
                    // object-shaped items (StatusCode {Code:N}, LocalizedText {Text:..}) whose
                    // text contains commas that a naive Split(',') would mangle.
                    var arrayElements = JsonSerializer.Deserialize<JsonElement>(arrayVal);
                    var items = arrayElements.EnumerateArray().Take(4).ToList();
                    while (items.Count < 4) items.Add(items.LastOrDefault());
                    var flat2dVal = "[" + string.Join(",",
                        items.Select(it => it.GetRawText())) + "]";

                    var setReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nid)}");
                    setReq.Content = MakeValueBody(flat2dVal);
                    (await Client.SendAsync(setReq)).EnsureSuccessStatusCode();

                    properties.Add((bn, flat2dVal, nid));
                }
            }

            // Add Variant 1D (i=24, ValueRank=1)
            {
                var bn = "Variant_1D";
                var req = WsReq(HttpMethod.Post,
                    $"/api/opcua/v1/nodes/{Slug(typeNodeId)}/children");
                req.Content = JsonContent.Create(new
                {
                    modelUri = testModelUri,
                    nodeClass = "Variable",
                    browseName = bn,
                    displayName = bn,
                    referenceTypeId = "i=46",
                    typeDefinitionId = "i=68",
                    modellingRuleId = "i=78",
                    dataType = "i=24",
                    valueRank = 1,
                    arrayDimensions = "0"
                });
                var nid = JsonSerializer.Deserialize<JsonElement>(
                    await (await Client.SendAsync(req)).EnsureSuccessStatusCode()
                        .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

                var setReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nid)}");
                setReq.Content = MakeValueBody("[\"v1\",\"v2\",\"v3\"]");
                (await Client.SendAsync(setReq)).EnsureSuccessStatusCode();

                properties.Add((bn, "[\"v1\",\"v2\",\"v3\"]", nid));
            }

            // Add Variant 2D (i=24, ValueRank=2)
            {
                var bn = "Variant_2D";
                var req = WsReq(HttpMethod.Post,
                    $"/api/opcua/v1/nodes/{Slug(typeNodeId)}/children");
                req.Content = JsonContent.Create(new
                {
                    modelUri = testModelUri,
                    nodeClass = "Variable",
                    browseName = bn,
                    displayName = bn,
                    referenceTypeId = "i=46",
                    typeDefinitionId = "i=68",
                    modellingRuleId = "i=78",
                    dataType = "i=24",
                    valueRank = 2,
                    arrayDimensions = "2,2"
                });
                var nid = JsonSerializer.Deserialize<JsonElement>(
                    await (await Client.SendAsync(req)).EnsureSuccessStatusCode()
                        .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

                var setReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nid)}");
                setReq.Content = MakeValueBody("[\"a\",\"b\",\"c\",\"d\"]");
                (await Client.SendAsync(setReq)).EnsureSuccessStatusCode();

                properties.Add((bn, "[\"a\",\"b\",\"c\",\"d\"]", nid));
            }

            Assert.Equal(62, properties.Count);

            // Instantiate
            var instantiateReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/types/object-types/{Slug(typeNodeId)}/instantiate");
            instantiateReq.Content = JsonContent.Create(new
            {
                parentNodeId = typeNodeId,
                modelUri = testModelUri,
                browseName = "AllProps",
                displayName = "AllProps",
                referenceTypeId = "i=47"
            });
            var instantiateResp = await Client.SendAsync(instantiateReq);
            var instantiateBody = await instantiateResp.Content.ReadAsStringAsync();
            Assert.True(instantiateResp.IsSuccessStatusCode,
                $"Instantiate failed ({instantiateResp.StatusCode}): {instantiateBody}");

            var createdNodes = JsonSerializer.Deserialize<JsonElement>(instantiateBody)
                .GetProperty("results").EnumerateArray().ToList();
            // First node is the instance, rest are children
            Assert.True(createdNodes.Count >= 63,
                $"Expected 63 nodes (1 instance + 62 properties), got {createdNodes.Count}");

            // Verify each property value via GET API
            int verified = 0;
            foreach (var (propName, expectedVal, _) in properties)
            {
                var instanceProp = createdNodes.FirstOrDefault(n =>
                {
                    var bn = n.GetProperty("browseName").GetString() ?? "";
                    // Match exact name after stripping namespace prefix (e.g., "nsu=...;Byte_Scalar" â†’ "Byte_Scalar")
                    var semi = bn.IndexOf(';');
                    var stripped = semi >= 0 ? bn[(semi + 1)..] : bn;
                    return stripped == propName;
                });
                if (instanceProp.ValueKind == JsonValueKind.Undefined) continue;

                var instancePropNodeId = instanceProp.GetProperty("nodeId").GetString()!;
                var getReq = WsReq(HttpMethod.Get,
                    $"/api/opcua/v1/nodes/{Slug(instancePropNodeId)}");
                var getResp = await Client.SendAsync(getReq);
                getResp.EnsureSuccessStatusCode();
                var node = await getResp.Content.ReadFromJsonAsync<JsonElement>();

                if (node.TryGetProperty("value", out var valueEl) &&
                    valueEl.ValueKind != JsonValueKind.Null &&
                    valueEl.ToString() != "")
                {
                    try
                    {
                        var expected = JsonSerializer.Deserialize<JsonElement>(expectedVal);
                        Assert.Equal(expected.ToString(), valueEl.ToString(), ignoreCase: true);
                    }
                    catch (JsonException)
                    {
                        Assert.Equal(expectedVal, valueEl.ToString(), ignoreCase: true);
                    }
                    verified++;
                }
            }

            Assert.True(verified >= 50,
                $"Expected at least 50 verified property values, got {verified}");

            // Also verify via NodeSet export
            var exportReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/namespaces/info/{testModelId}/export?format=xml");
            var exportResp = await Client.SendAsync(exportReq);
            Assert.True(exportResp.IsSuccessStatusCode, "Export failed");
            var nodeSetXml = await exportResp.Content.ReadAsStringAsync();
            var nsDoc = System.Xml.Linq.XDocument.Parse(nodeSetXml);
            var uaNs = System.Xml.Linq.XNamespace.Get("http://opcfoundation.org/UA/2011/03/UANodeSet.xsd");

            // Count UAVariable elements â€” should be 62 on type + 62 on instance = 124
            var allVars = nsDoc.Descendants(uaNs + "UAVariable").ToList();
            Assert.True(allVars.Count >= 124,
                $"Expected at least 124 UAVariables (62 type + 62 instance), got {allVars.Count}");

            // Check that 2D variables use Matrix encoding (not ListOf)
            var vars2D = allVars.Where(v => v.Attribute("ValueRank")?.Value == "2").ToList();
            foreach (var v2d in vars2D)
            {
                var valueNode = v2d.Element(uaNs + "Value");
                if (valueNode != null)
                {
                    var hasMatrix = valueNode.Descendants().Any(e => e.Name.LocalName == "Matrix");
                    var bn = v2d.Attribute("BrowseName")?.Value ?? "";
                    Assert.True(hasMatrix, $"2D variable '{bn}' should use Matrix encoding");
                }
            }

            // Cleanup
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    /// <summary>
    /// Parallel to <see cref="Model_AddPropertiesForEveryDataType"/> but focused on
    /// Structure and Union DataTypes. Creates a plain structure <c>AddressType</c>, a
    /// nested-structure <c>PersonType</c> (whose <c>Address</c> field is an
    /// <c>AddressType</c>), and a <c>ContactType</c> Union with three alternatives,
    /// then adds a scalar variable of each to an ObjectType and verifies the full
    /// PUT â†’ DB â†’ GET + XML-export round trip.
    /// </summary>
    [Fact]
    public async Task Model_AddStructureAndUnionPropertyTypes()
    {
        const string userId = "structprops-001";
        const string email = "structprops@test.net";
        const string wsName = "StructProps";
        const string testModelUri = "http://test.example.org/UA/StructProps/";
        const string uaTypesXmlNs = "http://opcfoundation.org/UA/2008/02/Types.xsd";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Structure/Union property verification"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            // Add test model
            var addTestReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addTestReq.Content = JsonContent.Create(new { uri = testModelUri, name = "StructProps", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            var addTestResp = await Client.SendAsync(addTestReq);
            addTestResp.EnsureSuccessStatusCode();
            var testModelId = (await addTestResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            // ------------------------------------------------------------------
            // Create DataTypes: AddressType, PersonType (nested), ContactType (union)
            // ------------------------------------------------------------------
            async Task<string> CreateDataType(string parentId, string browseName)
            {
                var req = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(parentId)}/children");
                req.Content = JsonContent.Create(new
                {
                    modelUri = testModelUri,
                    nodeClass = "DataType",
                    browseName,
                    displayName = browseName,
                    referenceTypeId = "i=45"
                });
                var resp = await Client.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                Assert.True(resp.IsSuccessStatusCode,
                    $"CreateDataType {browseName} failed ({resp.StatusCode}): {body}");
                return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString()!;
            }

            async Task SetFields(string dataTypeId, object fieldsObj, string label)
            {
                var req = WsReq(HttpMethod.Put,
                    $"/api/opcua/v1/types/data-types/{Slug(dataTypeId)}/definition");
                req.Content = JsonContent.Create(fieldsObj);
                var resp = await Client.SendAsync(req);
                Assert.True(resp.IsSuccessStatusCode,
                    $"{label} failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
            }

            // AddressType â€” plain structure
            var addressTypeId = await CreateDataType("i=22", "AddressType");
            await SetFields(addressTypeId, new
            {
                fields = new object[]
                {
                    new { name = "Street", dataType = "i=12" },  // String
                    new { name = "City",   dataType = "i=12" },  // String
                    new { name = "Zip",    dataType = "i=12" },  // String
                }
            }, "AddressType definition");

            // PersonType â€” structure with a nested AddressType field
            var personTypeId = await CreateDataType("i=22", "PersonType");
            await SetFields(personTypeId, new
            {
                fields = new object[]
                {
                    new { name = "Name",    dataType = "i=12" },     // String
                    new { name = "Age",     dataType = "i=7"  },     // UInt32
                    new { name = "Address", dataType = addressTypeId },
                }
            }, "PersonType definition");

            // ContactType â€” Union, subtype of i=12756 (Union).
            // (The server recognises IsUnion automatically via the supertype chain.)
            var contactTypeId = await CreateDataType("i=12756", "ContactType");
            await SetFields(contactTypeId, new
            {
                fields = new object[]
                {
                    new { name = "Phone", dataType = "i=12" },  // String
                    new { name = "Email", dataType = "i=12" },  // String
                    new { name = "URL",   dataType = "i=12" },  // String
                }
            }, "ContactType definition");

            // ------------------------------------------------------------------
            // Create ObjectType with a scalar variable of each DataType
            // ------------------------------------------------------------------
            var createTypeReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=58")}/children");
            createTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "EmployeeType",
                displayName = "EmployeeType",
                referenceTypeId = "i=45"
            });
            var employeeTypeId = JsonSerializer.Deserialize<JsonElement>(
                await (await Client.SendAsync(createTypeReq)).EnsureSuccessStatusCode()
                    .Content.ReadAsStringAsync()).GetProperty("nodeId").GetString()!;

            async Task<string> AddVariable(string parent, string browseName, string dataTypeId)
            {
                var req = WsReq(HttpMethod.Post,
                    $"/api/opcua/v1/nodes/{Slug(parent)}/children");
                req.Content = JsonContent.Create(new
                {
                    modelUri = testModelUri,
                    nodeClass = "Variable",
                    browseName,
                    displayName = browseName,
                    referenceTypeId = "i=47",
                    typeDefinitionId = "i=63",
                    modellingRuleId = "i=78",
                    dataType = dataTypeId,
                    valueRank = -1
                });
                var resp = await Client.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                Assert.True(resp.IsSuccessStatusCode,
                    $"AddVariable {browseName} failed ({resp.StatusCode}): {body}");
                return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString()!;
            }

            var homeAddressId = await AddVariable(employeeTypeId, "HomeAddress", addressTypeId);
            var profileId     = await AddVariable(employeeTypeId, "Profile",     personTypeId);
            var primaryContactId = await AddVariable(employeeTypeId, "PrimaryContact", contactTypeId);

            // ------------------------------------------------------------------
            // Set structured values via REST PUT
            // ------------------------------------------------------------------
            async Task SetValue(string nodeId, object value, string label)
            {
                var req = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
                req.Content = JsonContent.Create(new { value });
                var resp = await Client.SendAsync(req);
                Assert.True(resp.IsSuccessStatusCode,
                    $"SetValue {label} failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
            }

            // Use Dictionary<string,object?> for struct values so that PascalCase field
            // names pass through JsonContent.Create untouched. Anonymous objects would be
            // camelCased by the default web JSON serializer options, breaking the
            // case-sensitive match against the DataTypeDefinition fields during the
            // AddressSpace's ResolveVariants() canonicalization pass.
            var addressValue = new Dictionary<string, object?>
            {
                ["Street"] = "221B Baker Street",
                ["City"]   = "London",
                ["Zip"]    = "NW1 6XE",
            };
            await SetValue(homeAddressId, addressValue, "HomeAddress");

            var personValue = new Dictionary<string, object?>
            {
                ["Name"] = "Sherlock Holmes",
                ["Age"]  = 60u,
                ["Address"] = new Dictionary<string, object?>
                {
                    ["Street"] = "221B Baker Street",
                    ["City"]   = "London",
                    ["Zip"]    = "NW1 6XE",
                },
            };
            await SetValue(profileId, personValue, "Profile");

            // Union value: SwitchField=2 selects Email
            var contactValue = new Dictionary<string, object?>
            {
                ["SwitchField"] = 2,
                ["Email"]       = "sherlock@bakerstreet.example",
            };
            await SetValue(primaryContactId, contactValue, "PrimaryContact");

            // ------------------------------------------------------------------
            // Verify values via REST GET (end-to-end round trip through DB)
            // ------------------------------------------------------------------
            async Task<JsonElement> GetValue(string nodeId)
            {
                var req = WsReq(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
                var resp = await Client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var node = await resp.Content.ReadFromJsonAsync<JsonElement>();
                return node.GetProperty("value");
            }

            var homeAddressBack = await GetValue(homeAddressId);
            Assert.Equal(JsonValueKind.Object, homeAddressBack.ValueKind);
            Assert.Equal("221B Baker Street", homeAddressBack.GetProperty("Street").GetString());
            Assert.Equal("London",            homeAddressBack.GetProperty("City").GetString());
            Assert.Equal("NW1 6XE",           homeAddressBack.GetProperty("Zip").GetString());

            // Part 6 §5.4: numeric struct fields round-trip as JSON numbers (not strings),
            // so use the JsonElement's text representation rather than GetString() (which
            // throws on a Number kind). Comparing as text keeps the assertion neutral
            // about the underlying CLR type while still verifying the value.
            var profileBack = await GetValue(profileId);
            Assert.Equal("Sherlock Holmes",   profileBack.GetProperty("Name").GetString());
            Assert.Equal("60",                profileBack.GetProperty("Age").ToString());
            var nestedAddress = profileBack.GetProperty("Address");
            Assert.Equal("221B Baker Street", nestedAddress.GetProperty("Street").GetString());

            var contactBack = await GetValue(primaryContactId);
            Assert.Equal("2",                             contactBack.GetProperty("SwitchField").ToString());
            Assert.Equal("sherlock@bakerstreet.example",  contactBack.GetProperty("Email").GetString());

            // ------------------------------------------------------------------
            // Export NodeSet XML and verify ExtensionObject encoding
            // ------------------------------------------------------------------
            var exportReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/namespaces/info/{testModelId}/export?format=xml");
            var exportResp = await Client.SendAsync(exportReq);
            Assert.True(exportResp.IsSuccessStatusCode,
                $"Export failed ({exportResp.StatusCode}): {await exportResp.Content.ReadAsStringAsync()}");
            var nodeSetXml = await exportResp.Content.ReadAsStringAsync();

            var nsDoc = System.Xml.Linq.XDocument.Parse(nodeSetXml);
            var uaNs = System.Xml.Linq.XNamespace.Get("http://opcfoundation.org/UA/2011/03/UANodeSet.xsd");
            var uax = System.Xml.Linq.XNamespace.Get(uaTypesXmlNs);

            // Find the HomeAddress variable on the EmployeeType (not the instance) â€” its
            // Value element should contain a complete <uax:ExtensionObject> with TypeId
            // and Body, and the Body's struct fields should match what we PUT.
            var allVars = nsDoc.Descendants(uaNs + "UAVariable").ToList();

            void AssertExtensionObjectValue(
                System.Xml.Linq.XElement? variable, string expectedStructName,
                Action<System.Xml.Linq.XElement> bodyAssertions, string label)
            {
                Assert.True(variable != null, $"{label}: variable not found in exported XML");
                var valueEl = variable!.Element(uaNs + "Value");
                Assert.True(valueEl != null, $"{label}: <Value> element missing");

                // The <Value> wraps a <uax:ExtensionObject>.
                var eoEl = valueEl!.Element(uax + "ExtensionObject");
                Assert.True(eoEl != null,
                    $"{label}: expected <uax:ExtensionObject> under <Value>, got: {valueEl}");

                // TypeId â†’ Identifier
                var typeIdEl = eoEl!.Element(uax + "TypeId");
                Assert.True(typeIdEl != null, $"{label}: ExtensionObject has no TypeId");
                var identifierEl = typeIdEl!.Element(uax + "Identifier");
                Assert.True(identifierEl != null, $"{label}: TypeId has no Identifier");
                Assert.False(string.IsNullOrEmpty(identifierEl!.Value),
                    $"{label}: Identifier is empty");

                // Body â†’ wrapping struct element (local name matches expected struct)
                var bodyEl = eoEl.Element(uax + "Body");
                Assert.True(bodyEl != null, $"{label}: ExtensionObject has no Body");
                var structEl = bodyEl!.Elements().FirstOrDefault();
                Assert.True(structEl != null,
                    $"{label}: <Body> has no struct wrapper element");
                Assert.Equal(expectedStructName, structEl!.Name.LocalName);

                bodyAssertions(structEl);
            }

            // ---- HomeAddress (scalar AddressType) ----
            var homeAddressTypeVar = allVars.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("HomeAddress"));
            AssertExtensionObjectValue(
                homeAddressTypeVar, "AddressType",
                addressStruct =>
                {
                    var fieldNames = addressStruct.Elements().Select(e => e.Name.LocalName).ToList();
                    Assert.Contains("Street", fieldNames);
                    Assert.Contains("City", fieldNames);
                    Assert.Contains("Zip", fieldNames);
                    Assert.Equal("221B Baker Street",
                        addressStruct.Elements().First(e => e.Name.LocalName == "Street").Value);
                    Assert.Equal("London",
                        addressStruct.Elements().First(e => e.Name.LocalName == "City").Value);
                },
                "HomeAddress");

            // ---- Profile (scalar PersonType, containing nested AddressType) ----
            var profileVar = allVars.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("Profile"));
            AssertExtensionObjectValue(
                profileVar, "PersonType",
                personStruct =>
                {
                    var fieldNames = personStruct.Elements().Select(e => e.Name.LocalName).ToList();
                    Assert.Contains("Name", fieldNames);
                    Assert.Contains("Age", fieldNames);
                    Assert.Contains("Address", fieldNames);
                    Assert.Equal("Sherlock Holmes",
                        personStruct.Elements().First(e => e.Name.LocalName == "Name").Value);
                    Assert.Equal("60",
                        personStruct.Elements().First(e => e.Name.LocalName == "Age").Value);
                },
                "Profile");

            // ---- PrimaryContact (scalar Union ContactType) ----
            var contactVar = allVars.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("PrimaryContact"));
            AssertExtensionObjectValue(
                contactVar, "ContactType",
                contactStruct =>
                {
                    // Union body should carry SwitchField plus the selected arm.
                    var switchEl = contactStruct.Elements().FirstOrDefault(e => e.Name.LocalName == "SwitchField");
                    Assert.True(switchEl != null, "PrimaryContact: SwitchField missing");
                    Assert.Equal("2", switchEl!.Value);
                    var emailEl = contactStruct.Elements().FirstOrDefault(e => e.Name.LocalName == "Email");
                    Assert.True(emailEl != null, "PrimaryContact: Email arm missing");
                    Assert.Equal("sherlock@bakerstreet.example", emailEl!.Value);
                },
                "PrimaryContact");

            // Cleanup
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Model_AddEnumerationDataType()
    {
        const string userId = "enum-001";
        const string email = "enum@test.net";
        const string wsName = "EnumTest";
        const string testModelUri = "http://test.example.org/UA/EnumTest/";
        string? wsUrn = null;

        try
        {
            // FindAndDeleteWorkspace
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Enumeration DataType test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            // Create Test model
            var addTestReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addTestReq.Content = JsonContent.Create(new { uri = testModelUri, name = "EnumTest", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            var addTestResp = await Client.SendAsync(addTestReq);
            addTestResp.EnsureSuccessStatusCode();
            var testModelId = (await addTestResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            // Add Test:TrafficLightType DataType as subtype of Enumeration (i=29)
            var createDtReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=29")}/children");
            createDtReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "DataType",
                browseName = "TrafficLightType",
                displayName = "TrafficLightType",
                referenceTypeId = "i=45" // HasSubtype
            });
            var createDtResp = await Client.SendAsync(createDtReq);
            var createDtBody = await createDtResp.Content.ReadAsStringAsync();
            Assert.True(createDtResp.IsSuccessStatusCode,
                $"CreateDataType failed ({createDtResp.StatusCode}): {createDtBody}");
            var trafficLightNodeId = JsonSerializer.Deserialize<JsonElement>(createDtBody)
                .GetProperty("nodeId").GetString()!;

            // Set DataTypeDefinition fields: Red=0, Yellow=1, Green=2 with descriptions
            var updateDefReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/types/data-types/{Slug(trafficLightNodeId)}/definition");
            updateDefReq.Content = JsonContent.Create(new
            {
                fields = new[]
                {
                    new { name = "Red", value = 0, description = new { text = "Stop signal" } },
                    new { name = "Yellow", value = 1, description = new { text = "Caution signal" } },
                    new { name = "Green", value = 2, description = new { text = "Go signal" } },
                }
            });
            var updateDefResp = await Client.SendAsync(updateDefReq);
            var updateDefBody = await updateDefResp.Content.ReadAsStringAsync();
            Assert.True(updateDefResp.IsSuccessStatusCode,
                $"UpdateDefinition failed ({updateDefResp.StatusCode}): {updateDefBody}");

            // Verify definition returned from update
            var defBody = JsonSerializer.Deserialize<JsonElement>(updateDefBody);
            var fields = defBody.GetProperty("fields").EnumerateArray().ToList();
            Assert.Equal(3, fields.Count);
            Assert.Equal("Red", fields[0].GetProperty("name").GetString());
            Assert.Equal(0, fields[0].GetProperty("value").GetInt32());
            Assert.Equal("Yellow", fields[1].GetProperty("name").GetString());
            Assert.Equal(1, fields[1].GetProperty("value").GetInt32());
            Assert.Equal("Green", fields[2].GetProperty("name").GetString());
            Assert.Equal(2, fields[2].GetProperty("value").GetInt32());

            // Reload the definition from the database (the PUT invalidated the address space, so this
            // GET rebuilds through the Export-XML/JSON round-trip). Guards against the explicit value
            // 0 being collapsed to "unset" — the -1 sentinel, not 0, means unset.
            var reloadDefReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/types/data-types/{Slug(trafficLightNodeId)}/definition");
            var reloadDefResp = await Client.SendAsync(reloadDefReq);
            var reloadDefBody = await reloadDefResp.Content.ReadAsStringAsync();
            Assert.True(reloadDefResp.IsSuccessStatusCode,
                $"ReloadDefinition failed ({reloadDefResp.StatusCode}): {reloadDefBody}");
            var reloadFields = JsonSerializer.Deserialize<JsonElement>(reloadDefBody)
                .GetProperty("fields").EnumerateArray()
                .Where(f => f.TryGetProperty("isInherited", out var inh) ? !inh.GetBoolean() : true)
                .ToList();
            Assert.Equal("Red", reloadFields[0].GetProperty("name").GetString());
            Assert.Equal(0, reloadFields[0].GetProperty("value").GetInt32());
            Assert.Equal(1, reloadFields[1].GetProperty("value").GetInt32());
            Assert.Equal(2, reloadFields[2].GetProperty("value").GetInt32());

            // Add EnumStrings property to TrafficLightType
            // Sequential 0-based enums use EnumStrings (array of LocalizedText)
            var addEnumStringsReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(trafficLightNodeId)}/children");
            addEnumStringsReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "Variable",
                browseName = "EnumStrings",
                browseNameModelUri = "http://opcfoundation.org/UA/",
                displayName = "EnumStrings",
                referenceTypeId = "i=46", // HasProperty
                typeDefinitionId = "i=68", // PropertyType
                dataType = "i=21", // LocalizedText
                valueRank = 1, // OneDimensional
                arrayDimensions = "0"
            });
            var addEnumStringsResp = await Client.SendAsync(addEnumStringsReq);
            var addEnumStringsBody = await addEnumStringsResp.Content.ReadAsStringAsync();
            Assert.True(addEnumStringsResp.IsSuccessStatusCode,
                $"AddEnumStrings failed ({addEnumStringsResp.StatusCode}): {addEnumStringsBody}");
            var enumStringsNodeId = JsonSerializer.Deserialize<JsonElement>(addEnumStringsBody)
                .GetProperty("nodeId").GetString()!;

            // Set EnumStrings value = ["Red", "Yellow", "Green"] with ArrayDimensions=3
            var setEnumStringsReq = WsReq(HttpMethod.Put,
                $"/api/opcua/v1/nodes/{Slug(enumStringsNodeId)}");
            setEnumStringsReq.Content = JsonContent.Create(new
            {
                arrayDimensions = "3",
                value = new[] { "Red", "Yellow", "Green" }
            });
            var setEnumStringsResp = await Client.SendAsync(setEnumStringsReq);
            Assert.True(setEnumStringsResp.IsSuccessStatusCode,
                $"SetEnumStrings failed ({setEnumStringsResp.StatusCode}): {await setEnumStringsResp.Content.ReadAsStringAsync()}");

            // Create Test:IntersectionType ObjectType as subtype of BaseObjectType (i=58)
            var createObjTypeReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=58")}/children");
            createObjTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "IntersectionType",
                displayName = "IntersectionType",
                referenceTypeId = "i=45" // HasSubtype
            });
            var createObjTypeResp = await Client.SendAsync(createObjTypeReq);
            var createObjTypeBody = await createObjTypeResp.Content.ReadAsStringAsync();
            Assert.True(createObjTypeResp.IsSuccessStatusCode,
                $"CreateObjectType failed ({createObjTypeResp.StatusCode}): {createObjTypeBody}");
            var intersectionTypeNodeId = JsonSerializer.Deserialize<JsonElement>(createObjTypeBody)
                .GetProperty("nodeId").GetString()!;

            // Add StopLight_Scalar, StopLight_1D, StopLight_2D components to IntersectionType
            // Client provides ArrayDimensions with actual sizes when setting the value.
            var stopLightSpecs = new (string name, int valueRank, string? arrayDims, string? valueDims, object value)[]
            {
                ("StopLight_Scalar", -1, null,    null,    2),                   // Green
                ("StopLight_1D",      1, null,    "3",     new[] { 0, 1, 2 }),   // Red, Yellow, Green
                ("StopLight_2D",      2, null,    "2,2",   new[] { 0, 1, 2, 0 }), // 2Ã—2 matrix
            };

            foreach (var (name, valueRank, arrayDims, valueDims, value) in stopLightSpecs)
            {
                var addReq = WsReq(HttpMethod.Post,
                    $"/api/opcua/v1/nodes/{Slug(intersectionTypeNodeId)}/children");
                var childProps = new Dictionary<string, object?>
                {
                    ["modelUri"] = testModelUri,
                    ["nodeClass"] = "Variable",
                    ["browseName"] = name,
                    ["displayName"] = name,
                    ["referenceTypeId"] = "i=47",  // HasComponent
                    ["typeDefinitionId"] = "i=63", // BaseDataVariableType
                    ["modellingRuleId"] = "i=78",  // Mandatory
                    ["dataType"] = trafficLightNodeId,
                    ["valueRank"] = valueRank,
                };
                if (arrayDims != null) childProps["arrayDimensions"] = arrayDims;
                addReq.Content = JsonContent.Create(childProps);
                var addResp = await Client.SendAsync(addReq);
                var addBody = await addResp.Content.ReadAsStringAsync();
                Assert.True(addResp.IsSuccessStatusCode,
                    $"Add {name} failed ({addResp.StatusCode}): {addBody}");
                var nid = JsonSerializer.Deserialize<JsonElement>(addBody)
                    .GetProperty("nodeId").GetString()!;

                // Client sets value and provides ArrayDimensions with actual sizes
                var setReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nid)}");
                var updateProps = new Dictionary<string, object?> { ["value"] = value };
                if (valueDims != null) updateProps["arrayDimensions"] = valueDims;
                setReq.Content = JsonContent.Create(updateProps);
                var setResp = await Client.SendAsync(setReq);
                Assert.True(setResp.IsSuccessStatusCode,
                    $"SetValue {name} failed ({setResp.StatusCode}): {await setResp.Content.ReadAsStringAsync()}");
            }

            // Instantiate Test:Intersection from IntersectionType
            var instantiateReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/types/object-types/{Slug(intersectionTypeNodeId)}/instantiate");
            instantiateReq.Content = JsonContent.Create(new
            {
                parentNodeId = intersectionTypeNodeId,
                modelUri = testModelUri,
                browseName = "Intersection",
                displayName = "Intersection",
                referenceTypeId = "i=47" // HasComponent
            });
            var instantiateResp = await Client.SendAsync(instantiateReq);
            var instantiateBody = await instantiateResp.Content.ReadAsStringAsync();
            Assert.True(instantiateResp.IsSuccessStatusCode,
                $"Instantiate failed ({instantiateResp.StatusCode}): {instantiateBody}");
            var instantiatedNodes = JsonSerializer.Deserialize<JsonElement>(instantiateBody);
            var intersectionNode = instantiatedNodes.GetProperty("results").EnumerateArray().First();
            var intersectionNodeId = intersectionNode.GetProperty("nodeId").GetString()!;

            // Add inverse Organizes reference from UA:Objects (i=85) to Intersection
            var addRefReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(intersectionNodeId)}/references");
            addRefReq.Content = JsonContent.Create(new
            {
                referenceTypeId = "i=35", // Organizes
                targetNodeId = "i=85",    // Objects folder
                isForward = false
            });
            var addRefResp = await Client.SendAsync(addRefReq);
            Assert.True(addRefResp.IsSuccessStatusCode,
                $"AddReference failed ({addRefResp.StatusCode}): {await addRefResp.Content.ReadAsStringAsync()}");

            // Download NodeSet XML to verify model
            var exportReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/namespaces/info/{testModelId}/export?format=xml");
            var exportResp = await Client.SendAsync(exportReq);
            Assert.True(exportResp.IsSuccessStatusCode,
                $"Export failed ({exportResp.StatusCode}): {await exportResp.Content.ReadAsStringAsync()}");
            var nodeSetXml = await exportResp.Content.ReadAsStringAsync();

            var nsDoc = System.Xml.Linq.XDocument.Parse(nodeSetXml);
            var uaNs = System.Xml.Linq.XNamespace.Get("http://opcfoundation.org/UA/2011/03/UANodeSet.xsd");

            // Verify Test:TrafficLightType DataType exists
            var dataTypes = nsDoc.Descendants(uaNs + "UADataType").ToList();
            var trafficLightDt = dataTypes.FirstOrDefault(dt =>
                (dt.Attribute("BrowseName")?.Value ?? "").Contains("TrafficLightType"));
            Assert.True(trafficLightDt != null,
                $"TrafficLightType DataType not found. DataTypes: [{string.Join(", ", dataTypes.Select(d => d.Attribute("BrowseName")?.Value))}]");

            // Verify DataTypeDefinition has correct fields
            var definition = trafficLightDt!.Element(uaNs + "Definition");
            Assert.True(definition != null, "DataTypeDefinition not found on TrafficLightType");
            var defFields = definition!.Elements(uaNs + "Field").ToList();
            Assert.Equal(3, defFields.Count);
            Assert.Equal("Red", defFields[0].Attribute("Name")?.Value);
            Assert.Equal("0", defFields[0].Attribute("Value")?.Value);
            Assert.Equal("Yellow", defFields[1].Attribute("Name")?.Value);
            Assert.Equal("1", defFields[1].Attribute("Value")?.Value);
            Assert.Equal("Green", defFields[2].Attribute("Name")?.Value);
            Assert.Equal("2", defFields[2].Attribute("Value")?.Value);

            // Verify EnumStrings property exists on TrafficLightType
            var variables = nsDoc.Descendants(uaNs + "UAVariable").ToList();
            var trafficLightNodeIdVal = trafficLightDt.Attribute("NodeId")?.Value;
            var enumStringsProp = variables.FirstOrDefault(v =>
                (v.Attribute("BrowseName")?.Value ?? "").Contains("EnumStrings") &&
                v.Attribute("ParentNodeId")?.Value == trafficLightNodeIdVal);
            Assert.True(enumStringsProp != null,
                $"EnumStrings not found on TrafficLightType (NodeId={trafficLightNodeIdVal}). " +
                $"Variables with this parent: [{string.Join(", ", variables.Where(v => v.Attribute("ParentNodeId")?.Value == trafficLightNodeIdVal).Select(v => v.Attribute("BrowseName")?.Value))}]");

            // Verify EnumStrings ArrayDimensions matches value count
            var enumStringsArrayDim = enumStringsProp!.Attribute("ArrayDimensions")?.Value;
            var enumStringsVR = enumStringsProp.Attribute("ValueRank")?.Value;
            Assert.True(enumStringsArrayDim == "3",
                $"EnumStrings ArrayDimensions expected '3', got '{enumStringsArrayDim}'. " +
                $"ValueRank='{enumStringsVR}'. " +
                $"EnumStrings XML: {enumStringsProp}");

            // Verify EnumStrings has a Value element with the enum names
            var enumStringsValue = enumStringsProp.Element(uaNs + "Value");
            Assert.True(enumStringsValue != null, "EnumStrings should have a Value element");

            // Verify Test:Intersection Object exists
            var objects = nsDoc.Descendants(uaNs + "UAObject").ToList();
            var intersectionObj = objects.FirstOrDefault(o =>
                (o.Attribute("BrowseName")?.Value ?? "").Contains("Intersection") &&
                !(o.Attribute("BrowseName")?.Value ?? "").Contains("IntersectionType"));
            Assert.True(intersectionObj != null,
                $"Intersection Object not found. UAObjects: [{string.Join(", ", objects.Select(o => o.Attribute("BrowseName")?.Value))}]");

            // Verify HasTypeDefinition reference to IntersectionType
            var intersectionRefs = intersectionObj!.Element(uaNs + "References")?
                .Elements(uaNs + "Reference").ToList() ?? new();
            var typeDefRef = intersectionRefs.FirstOrDefault(r =>
            {
                var rt = r.Attribute("ReferenceType")?.Value ?? "";
                return rt == "HasTypeDefinition" || rt == "i=40";
            });
            Assert.True(typeDefRef != null,
                $"HasTypeDefinition not found on Intersection. Refs: [{string.Join(", ", intersectionRefs.Select(r => $"{r.Attribute("ReferenceType")?.Value}->{r.Value}"))}]");

            // Verify StopLight_Scalar, StopLight_1D, StopLight_2D on Intersection instance
            var intersectionNodeIdVal = intersectionObj.Attribute("NodeId")?.Value;
            var trafficLightNodeIdXml = trafficLightDt.Attribute("NodeId")?.Value;
            var instanceChildren = variables
                .Where(v => v.Attribute("ParentNodeId")?.Value == intersectionNodeIdVal)
                .ToList();

            var expectedStopLights = new (string name, string? valueRank, string? arrayDims)[]
            {
                ("StopLight_Scalar", null, null),   // ValueRank=-1 is default, may be omitted
                ("StopLight_1D",     "1",  "3"),     // 3-element array
                ("StopLight_2D",     "2",  "2,2"),   // 2Ã—2 matrix
            };

            foreach (var (name, expValueRank, expArrayDims) in expectedStopLights)
            {
                var slVar = instanceChildren.FirstOrDefault(v =>
                    (v.Attribute("BrowseName")?.Value ?? "").Contains(name));
                Assert.True(slVar != null,
                    $"{name} not found on Intersection. Children: [{string.Join(", ", instanceChildren.Select(v => v.Attribute("BrowseName")?.Value))}]");

                // DataType references TrafficLightType
                var slDataType = slVar!.Attribute("DataType")?.Value;
                Assert.True(slDataType == trafficLightNodeIdXml || (slDataType?.Contains("TrafficLightType") ?? false),
                    $"{name} DataType should reference TrafficLightType ({trafficLightNodeIdXml}), got: {slDataType}");

                // ValueRank
                var slValueRank = slVar.Attribute("ValueRank")?.Value;
                if (expValueRank != null)
                    Assert.Equal(expValueRank, slValueRank);
                else
                    Assert.True(slValueRank == null || slValueRank == "-1",
                        $"{name} ValueRank should be Scalar, got: {slValueRank}");

                // ArrayDimensions
                var slArrayDims = slVar.Attribute("ArrayDimensions")?.Value;
                if (expArrayDims != null)
                    Assert.Equal(expArrayDims, slArrayDims);
                else
                    Assert.Null(slArrayDims);

                // Has a Value element
                Assert.True(slVar.Element(uaNs + "Value") != null,
                    $"{name} should have a Value element");
            }

            // Cleanup
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task Model_AddStructureDataType()
    {
        const string userId = "struct-001";
        const string email = "struct@test.net";
        const string wsName = "StructTest";
        const string testModelUri = "http://test.example.org/UA/StructTest/";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            // Create workspace
            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new
            {
                applicationName = wsName,
                description = "Structure DataType test"
            });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            // Create Test model
            var addTestReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addTestReq.Content = JsonContent.Create(new { uri = testModelUri, name = "StructTest", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            var addTestResp = await Client.SendAsync(addTestReq);
            addTestResp.EnsureSuccessStatusCode();
            var testModelId = (await addTestResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            // --- Helper to create a child node ---
            async Task<string> CreateChild(string parentNodeId, object props)
            {
                var req = WsReq(HttpMethod.Post,
                    $"/api/opcua/v1/nodes/{Slug(parentNodeId)}/children");
                req.Content = JsonContent.Create(props);
                var resp = await Client.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                Assert.True(resp.IsSuccessStatusCode,
                    $"CreateChild on {parentNodeId} failed ({resp.StatusCode}): {body}");
                return JsonSerializer.Deserialize<JsonElement>(body)
                    .GetProperty("nodeId").GetString()!;
            }

            // --- Helper to update a node ---
            async Task UpdateNode(string nodeId, object props, string label)
            {
                var req = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
                req.Content = JsonContent.Create(props);
                var resp = await Client.SendAsync(req);
                Assert.True(resp.IsSuccessStatusCode,
                    $"{label} failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
            }

            // --- Helper to update DataType definition ---
            async Task UpdateDefinition(string nodeId, object def, string label)
            {
                var req = WsReq(HttpMethod.Put,
                    $"/api/opcua/v1/types/data-types/{Slug(nodeId)}/definition");
                req.Content = JsonContent.Create(def);
                var resp = await Client.SendAsync(req);
                Assert.True(resp.IsSuccessStatusCode,
                    $"{label} failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
            }

            // ============================================================
            // Add Test:PersonType DataType subtype of Structure (i=22)
            // ============================================================
            var personTypeId = await CreateChild("i=22", new
            {
                modelUri = testModelUri,
                nodeClass = "DataType",
                browseName = "PersonType",
                displayName = "PersonType",
                referenceTypeId = "i=45"
            });

            await UpdateDefinition(personTypeId, new
            {
                fields = new object[]
                {
                    new { name = "Name", dataType = "i=12" },  // String
                    new { name = "Age", dataType = "i=7" },    // UInt32
                }
            }, "PersonType definition");


            // ============================================================
            // Add Test:EmployeeType DataType subtype of Test:PersonType
            // ============================================================
            var employeeTypeId = await CreateChild(personTypeId, new
            {
                modelUri = testModelUri,
                nodeClass = "DataType",
                browseName = "EmployeeType",
                displayName = "EmployeeType",
                referenceTypeId = "i=45"
            });

            await UpdateDefinition(employeeTypeId, new
            {
                fields = new object[]
                {
                    new { name = "Title", dataType = "i=12" }, // String
                }
            }, "EmployeeType definition");


            // ============================================================
            // Add Test:WorkOrderType DataType subtype of Structure (i=22)
            // ============================================================
            var workOrderTypeId = await CreateChild("i=22", new
            {
                modelUri = testModelUri,
                nodeClass = "DataType",
                browseName = "WorkOrderType",
                displayName = "WorkOrderType",
                referenceTypeId = "i=45"
            });

            await UpdateDefinition(workOrderTypeId, new
            {
                fields = new object[]
                {
                    new { name = "Id", dataType = "i=14" },           // Guid
                    new { name = "CreatedBy", dataType = personTypeId, allowSubTypes = false },
                    new { name = "AssignedTo", dataType = personTypeId, allowSubTypes = true },
                    new { name = "Description", dataType = "i=21" },  // LocalizedText
                    new { name = "DueDate", dataType = "i=13" },      // DateTime
                    new { name = "Tags", dataType = "i=12", valueRank = 1 }, // String[]
                }
            }, "WorkOrderType definition");


            // ============================================================
            // Create ObjectType Test:ManagerType
            // ============================================================
            var managerTypeId = await CreateChild("i=58", new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "ManagerType",
                displayName = "ManagerType",
                referenceTypeId = "i=45"
            });

            // Add Active_Scalar, Active_1D, Active_2D variables
            var activeSpecs = new (string name, int valueRank, string? arrayDims, string? valueDims)[]
            {
                ("Active_Scalar", -1, null, null),
                ("Active_1D",      1, null, "1"),
                ("Active_2D",      2, null, "1,1"),
            };

            foreach (var (name, valueRank, arrayDims, valueDims) in activeSpecs)
            {
                var childProps = new Dictionary<string, object?>
                {
                    ["modelUri"] = testModelUri,
                    ["nodeClass"] = "Variable",
                    ["browseName"] = name,
                    ["displayName"] = name,
                    ["referenceTypeId"] = "i=47",  // HasComponent
                    ["typeDefinitionId"] = "i=63", // BaseDataVariableType
                    ["modellingRuleId"] = "i=78",  // Mandatory
                    ["dataType"] = workOrderTypeId,
                    ["valueRank"] = valueRank,
                };
                if (arrayDims != null) childProps["arrayDimensions"] = arrayDims;
                var nid = await CreateChild(managerTypeId, childProps);

                // Set a non-trivial value â€” WorkOrderType with EmployeeType for AssignedTo
                // Structure values are ExtensionObjects in OPC UA
                var workOrderValue = new Dictionary<string, object?>
                {
                    ["Id"] = "01234567-89ab-cdef-0123-456789abcdef",
                    ["CreatedBy"] = new { Name = "Alice", Age = 30 },
                    ["AssignedTo"] = new { Name = "Bob", Age = 25, Title = "Engineer" },
                    ["Description"] = "Test work order",
                    ["DueDate"] = "2026-12-31T00:00:00Z",
                    ["Tags"] = new[] { "urgent", "review" },
                };

                object value;
                if (valueRank == -1)
                {
                    value = workOrderValue;
                }
                else
                {
                    // Wrap in array (1D: single element, 2D: single element)
                    value = new[] { workOrderValue };
                }

                var updateProps = new Dictionary<string, object?> { ["value"] = value };
                if (valueDims != null) updateProps["arrayDimensions"] = valueDims;
                await UpdateNode(nid, updateProps, $"SetValue {name}");
            }

            // Diagnostic: verify the stored value for Active_Scalar via GET
            // (This checks the API returns the value correctly after DB round-trip)

            // ============================================================
            // Instantiate Test:Manager from ManagerType
            // ============================================================
            var instantiateReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/types/object-types/{Slug(managerTypeId)}/instantiate");
            instantiateReq.Content = JsonContent.Create(new
            {
                parentNodeId = managerTypeId,
                modelUri = testModelUri,
                browseName = "Manager",
                displayName = "Manager",
                referenceTypeId = "i=47"
            });
            var instantiateResp = await Client.SendAsync(instantiateReq);
            var instantiateBody = await instantiateResp.Content.ReadAsStringAsync();
            Assert.True(instantiateResp.IsSuccessStatusCode,
                $"Instantiate failed ({instantiateResp.StatusCode}): {instantiateBody}");

            // Add inverse Organizes reference from Objects folder
            var managerNode = JsonSerializer.Deserialize<JsonElement>(instantiateBody)
                .GetProperty("results").EnumerateArray().First();
            var managerNodeId = managerNode.GetProperty("nodeId").GetString()!;
            var addRefReq = WsReq(HttpMethod.Post,
                $"/api/opcua/v1/nodes/{Slug(managerNodeId)}/references");
            addRefReq.Content = JsonContent.Create(new
            {
                referenceTypeId = "i=35",
                targetNodeId = "i=85",
                isForward = false
            });
            var addRefResp = await Client.SendAsync(addRefReq);
            Assert.True(addRefResp.IsSuccessStatusCode,
                $"AddReference failed ({addRefResp.StatusCode}): {await addRefResp.Content.ReadAsStringAsync()}");

            // ============================================================
            // Download NodeSet XML and verify
            // ============================================================
            var exportReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/namespaces/info/{testModelId}/export?format=xml");
            var exportResp = await Client.SendAsync(exportReq);
            Assert.True(exportResp.IsSuccessStatusCode,
                $"Export failed ({exportResp.StatusCode}): {await exportResp.Content.ReadAsStringAsync()}");
            var nodeSetXml = await exportResp.Content.ReadAsStringAsync();

            var nsDoc = System.Xml.Linq.XDocument.Parse(nodeSetXml);
            var uaNs = System.Xml.Linq.XNamespace.Get("http://opcfoundation.org/UA/2011/03/UANodeSet.xsd");

            // Verify DataTypes exist
            var dataTypes = nsDoc.Descendants(uaNs + "UADataType").ToList();
            foreach (var dtName in new[] { "PersonType", "EmployeeType", "WorkOrderType" })
            {
                var dt = dataTypes.FirstOrDefault(d =>
                    (d.Attribute("BrowseName")?.Value ?? "").Contains(dtName));
                Assert.True(dt != null,
                    $"{dtName} not found. DataTypes: [{string.Join(", ", dataTypes.Select(d => d.Attribute("BrowseName")?.Value))}]");

                // Verify Definition exists with fields
                var def = dt!.Element(uaNs + "Definition");
                Assert.True(def != null, $"{dtName} has no Definition");
                Assert.True(def!.Elements(uaNs + "Field").Any(), $"{dtName} Definition has no fields");
            }

            // Verify PersonType fields: Name, Age
            var personDt = dataTypes.First(d => (d.Attribute("BrowseName")?.Value ?? "").Contains("PersonType")
                && !(d.Attribute("BrowseName")?.Value ?? "").Contains("Employee"));
            var personFields = personDt.Element(uaNs + "Definition")!.Elements(uaNs + "Field").ToList();
            Assert.Equal(2, personFields.Count);
            Assert.Equal("Name", personFields[0].Attribute("Name")?.Value);
            Assert.Equal("Age", personFields[1].Attribute("Name")?.Value);

            // Verify EmployeeType fields: Title (own) â€” inherited Name/Age not in own Definition
            var employeeDt = dataTypes.First(d => (d.Attribute("BrowseName")?.Value ?? "").Contains("EmployeeType"));
            var employeeFields = employeeDt.Element(uaNs + "Definition")!.Elements(uaNs + "Field").ToList();
            Assert.Single(employeeFields);
            Assert.Equal("Title", employeeFields[0].Attribute("Name")?.Value);

            // Verify WorkOrderType fields including IsOptional and AllowSubTypes flags
            var workOrderDt = dataTypes.First(d => (d.Attribute("BrowseName")?.Value ?? "").Contains("WorkOrderType"));
            var woFields = workOrderDt.Element(uaNs + "Definition")!.Elements(uaNs + "Field").ToList();
            Assert.Equal(6, woFields.Count);
            Assert.Equal("Id", woFields[0].Attribute("Name")?.Value);
            Assert.Equal("CreatedBy", woFields[1].Attribute("Name")?.Value);
            // CreatedBy: AllowSubTypes=false (default, should be absent)
            Assert.Null(woFields[1].Attribute("AllowSubTypes")?.Value);
            Assert.Equal("AssignedTo", woFields[2].Attribute("Name")?.Value);
            // AssignedTo: AllowSubTypes=true
            Assert.Equal("true", woFields[2].Attribute("AllowSubTypes")?.Value);
            Assert.Equal("Tags", woFields[5].Attribute("Name")?.Value);
            // Tags: ValueRank=1
            Assert.Equal("1", woFields[5].Attribute("ValueRank")?.Value);

            // Verify DataType encoding nodes (Default Binary, Default XML) â€” auto-created by server
            var allObjects = nsDoc.Descendants(uaNs + "UAObject").ToList();
            foreach (var dtName in new[] { "PersonType", "EmployeeType", "WorkOrderType" })
            {
                var dt = dataTypes.First(d => (d.Attribute("BrowseName")?.Value ?? "").Contains(dtName)
                    && (dtName != "PersonType" || !(d.Attribute("BrowseName")?.Value ?? "").Contains("Employee")));
                var dtNodeIdXml = dt.Attribute("NodeId")?.Value;

                // Find encoding objects that have HasEncoding inverse reference to this DataType
                var encodings = allObjects.Where(o =>
                {
                    var bn = o.Attribute("BrowseName")?.Value ?? "";
                    if (!bn.Contains("Default")) return false;
                    var refs = o.Element(uaNs + "References")?.Elements(uaNs + "Reference") ?? Enumerable.Empty<System.Xml.Linq.XElement>();
                    return refs.Any(r =>
                        (r.Attribute("ReferenceType")?.Value == "HasEncoding" || r.Attribute("ReferenceType")?.Value == "i=38") &&
                        r.Attribute("IsForward")?.Value == "false" &&
                        r.Value == dtNodeIdXml);
                }).ToList();

                Assert.True(encodings.Count >= 2,
                    $"{dtName} should have at least 2 encoding nodes (Binary, XML), found {encodings.Count}: " +
                    $"[{string.Join(", ", encodings.Select(e => e.Attribute("BrowseName")?.Value))}]");
            }

            // Verify variables on Manager instance
            var variables = nsDoc.Descendants(uaNs + "UAVariable").ToList();
            var managerObj = allObjects.FirstOrDefault(o =>
                (o.Attribute("BrowseName")?.Value ?? "").Contains("Manager") &&
                !(o.Attribute("BrowseName")?.Value ?? "").Contains("ManagerType"));
            Assert.True(managerObj != null,
                $"Manager Object not found. UAObjects: [{string.Join(", ", allObjects.Select(o => o.Attribute("BrowseName")?.Value))}]");

            var managerNodeIdXml = managerObj!.Attribute("NodeId")?.Value;
            var managerChildren = variables
                .Where(v => v.Attribute("ParentNodeId")?.Value == managerNodeIdXml)
                .ToList();

            var workOrderNodeIdXml = workOrderDt.Attribute("NodeId")?.Value;
            var expectedVars = new (string name, string? expValueRank, string? expArrayDims)[]
            {
                ("Active_Scalar", null, null),
                ("Active_1D",     "1",  "1"),
                ("Active_2D",     "2",  "1,1"),
            };

            foreach (var (name, expValueRank, expArrayDims) in expectedVars)
            {
                var v = managerChildren.FirstOrDefault(c =>
                    (c.Attribute("BrowseName")?.Value ?? "").Contains(name));
                Assert.True(v != null,
                    $"{name} not found on Manager. Children: [{string.Join(", ", managerChildren.Select(c => c.Attribute("BrowseName")?.Value))}]");

                // DataType references WorkOrderType
                var vdt = v!.Attribute("DataType")?.Value;
                Assert.True(vdt == workOrderNodeIdXml || (vdt?.Contains("WorkOrderType") ?? false),
                    $"{name} DataType should reference WorkOrderType ({workOrderNodeIdXml}), got: {vdt}");

                // ValueRank
                var vvr = v.Attribute("ValueRank")?.Value;
                if (expValueRank != null)
                    Assert.Equal(expValueRank, vvr);
                else
                    Assert.True(vvr == null || vvr == "-1", $"{name} ValueRank should be Scalar, got: {vvr}");

                // ArrayDimensions
                var vad = v.Attribute("ArrayDimensions")?.Value;
                if (expArrayDims != null)
                    Assert.Equal(expArrayDims, vad);
                else
                    Assert.Null(vad);

                // Has a Value element
                // TODO: verify ExtensionObject XML structure once SDK-based serialization is implemented
                var valueEl = v.Element(uaNs + "Value");
                Assert.True(valueEl != null, $"{name} should have a Value element");
            }

            // Cleanup
            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    [Fact]
    public async Task GetSubtypes_NamespaceFilter_PrunesToBranchesWithMatches()
    {
        const string userId = "nsfilter-001";
        const string email = "nsfilter@test.net";
        const string wsName = "NsFilterTest";
        const string testModelUri = "http://test.example.org/UA/NsFilter/";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new { applicationName = wsName, description = "Namespace filter test" });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            var addModelReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addModelReq.Content = JsonContent.Create(new { uri = testModelUri, name = "NsFilter", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            (await Client.SendAsync(addModelReq)).EnsureSuccessStatusCode();

            // Companion ObjectType nested under a core type (FolderType i=61 → BaseObjectType i=58),
            // so the pruned tree must include those two core ancestors as scaffolding.
            var widgetReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=61")}/children");
            widgetReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "WidgetType",
                displayName = "WidgetType",
                referenceTypeId = "i=45",
            });
            var widgetResp = await Client.SendAsync(widgetReq);
            Assert.True(widgetResp.IsSuccessStatusCode,
                $"create WidgetType failed ({widgetResp.StatusCode}): {await widgetResp.Content.ReadAsStringAsync()}");
            var widgetTypeId = (await widgetResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("nodeId").GetString()!;

            // Filter object-types by the companion namespace.
            var filterReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/types/object-types/{Slug("i=58")}/subtypes?modelUri={Uri.EscapeDataString(testModelUri)}");
            var filterResp = await Client.SendAsync(filterReq);
            filterResp.EnsureSuccessStatusCode();
            var body = await filterResp.Content.ReadFromJsonAsync<JsonElement>();

            var nodes = body.GetProperty("results").EnumerateArray().ToList();
            var ids = nodes.Select(n => n.GetProperty("nodeId").GetString()).ToHashSet();

            Assert.Contains(widgetTypeId, ids);                 // the match
            Assert.Contains("i=61", ids);                       // FolderType scaffolding
            Assert.Contains("i=58", ids);                       // BaseObjectType scaffolding (root)
            Assert.DoesNotContain("i=2004", ids);               // ServerType — core branch with no companion descendant

            var widget = nodes.First(n => n.GetProperty("nodeId").GetString() == widgetTypeId);
            Assert.Equal("i=61", widget.GetProperty("superTypeId").GetString());
            Assert.True(widget.GetProperty("hasNoSubtypes").GetBoolean(), "WidgetType is a pruned leaf");

            // A category with no companion types is empty.
            var dtReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/types/data-types/{Slug("i=24")}/subtypes?modelUri={Uri.EscapeDataString(testModelUri)}");
            var dtResp = await Client.SendAsync(dtReq);
            dtResp.EnsureSuccessStatusCode();
            var dtBody = await dtResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, dtBody.GetProperty("results").GetArrayLength());

            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    /// <summary>
    /// Type rows carry hasNoChildren so a tree that lists a type's instance declarations
    /// next to its subtypes knows whether the row expands without fetching. The flag counts
    /// hierarchical children *other than* subtypes — the same set GetChildren returns with
    /// includeSubtypes=false — and is null (not false) when the node does have some.
    /// </summary>
    [Fact]
    public async Task GetSubtypes_ReportsHasNoChildren_ForTypeInstanceDeclarations()
    {
        const string userId = "typechildren-001";
        const string email = "typechildren@test.net";
        const string wsName = "TypeChildrenTest";
        const string testModelUri = "http://test.example.org/UA/TypeChildren/";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new { applicationName = wsName, description = "Type children flag test" });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            var addModelReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addModelReq.Content = JsonContent.Create(new { uri = testModelUri, name = "TypeChildren", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            (await Client.SendAsync(addModelReq)).EnsureSuccessStatusCode();

            async Task<string> CreateObjectTypeAsync(string browseName)
            {
                var req = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=58")}/children");
                req.Content = JsonContent.Create(new
                {
                    modelUri = testModelUri,
                    nodeClass = "ObjectType",
                    browseName,
                    displayName = browseName,
                    referenceTypeId = "i=45",
                });
                var resp = await Client.SendAsync(req);
                Assert.True(resp.IsSuccessStatusCode,
                    $"create {browseName} failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
                return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodeId").GetString()!;
            }

            var parentTypeId = await CreateObjectTypeAsync("HasAPropertyType");
            var barrenTypeId = await CreateObjectTypeAsync("HasNothingType");

            // A subtype makes the parent non-leaf on the HasSubtype axis; hasNoChildren must
            // ignore it, since the tree lists subtypes from this same response.
            var subTypeReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(parentTypeId)}/children");
            subTypeReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "ObjectType",
                browseName = "DerivedType",
                displayName = "DerivedType",
                referenceTypeId = "i=45",
            });
            var subTypeId = (await (await Client.SendAsync(subTypeReq)).EnsureSuccessStatusCode()
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodeId").GetString()!;

            var propReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(parentTypeId)}/children");
            propReq.Content = JsonContent.Create(new
            {
                modelUri = testModelUri,
                nodeClass = "Variable",
                browseName = "Serial",
                displayName = "Serial",
                referenceTypeId = "i=46", // HasProperty
                typeDefinitionId = "i=68",
                modellingRuleId = "i=78",
                dataType = "i=12",
                valueRank = -1,
            });
            (await Client.SendAsync(propReq)).EnsureSuccessStatusCode();

            var subtypesReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/types/object-types/{Slug("i=58")}/subtypes?depth=3&count=10000&includeSelf=true");
            var subtypesResp = await Client.SendAsync(subtypesReq);
            subtypesResp.EnsureSuccessStatusCode();
            var nodes = (await subtypesResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("results").EnumerateArray().ToList();

            JsonElement Row(string nodeId) =>
                nodes.First(n => n.GetProperty("nodeId").GetString() == nodeId);

            // The type owning the property: expandable, so the flag is null and drops out
            // of the payload — even though its only other hierarchical reference is
            // HasSubtype → DerivedType.
            Assert.False(Row(parentTypeId).TryGetProperty("hasNoChildren", out var parentFlag)
                && parentFlag.ValueKind != JsonValueKind.Null,
                "HasAPropertyType owns an instance declaration, so hasNoChildren must not be set");
            // Subtype-only / childless types are leaves on the children axis.
            Assert.True(Row(subTypeId).GetProperty("hasNoChildren").GetBoolean(),
                "DerivedType has no instance declarations");
            Assert.True(Row(barrenTypeId).GetProperty("hasNoChildren").GetBoolean(),
                "HasNothingType has no instance declarations");
            // includeSelf row (BaseObjectType) carries the flag too.
            Assert.True(Row("i=58").GetProperty("hasNoChildren").GetBoolean(),
                "BaseObjectType has no instance declarations");

            // The flag agrees with what the tree fetches on expand.
            var childrenReq = WsReq(HttpMethod.Get,
                $"/api/opcua/v1/nodes/{Slug(parentTypeId)}/children?includeSubtypes=false");
            var childrenResp = await Client.SendAsync(childrenReq);
            childrenResp.EnsureSuccessStatusCode();
            var children = (await childrenResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("results").EnumerateArray().ToList();
            Assert.Single(children);
            Assert.Contains("Serial", children[0].GetProperty("browseName").GetString() ?? "");

            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }

    /// <summary>
    /// Regression: editing a node's DisplayName/Description must survive the address-space
    /// rebuild that follows the save. The edit writes the dedicated DB columns, but the XML
    /// regeneration path (NodeSetConverter) reads DisplayName/Description from the node's
    /// Attributes JSON. Previously the edit dropped them from Attributes, so the change
    /// silently reverted on the next read (which rebuilds from the DB after the PUT invalidates
    /// the cache).
    /// </summary>
    [Fact]
    public async Task Node_EditDisplayNameAndDescription_SurvivesRebuild()
    {
        const string userId = "node-edit-001";
        const string email = "nodeedit@test.net";
        const string wsName = "NodeEditTest";
        const string modelUri = "http://test.example.org/UA/NodeEditTest/";
        string? wsUrn = null;

        try
        {
            await FindAndDeleteWorkspace(userId, email, wsName);

            var createWsReq = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
            createWsReq.Content = JsonContent.Create(new { applicationName = wsName, description = "Node edit test" });
            var createWsResp = await Client.SendAsync(createWsReq);
            createWsResp.EnsureSuccessStatusCode();
            wsUrn = (await createWsResp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("applicationUri").GetString()!;

            HttpRequestMessage WsReq(HttpMethod method, string url)
            {
                var r = AsUser(method, url, userId, email);
                r.Headers.Add("OpcUa-Server", wsUrn);
                return r;
            }

            // Private, editable model + a fresh ObjectType (subtype of BaseObjectType i=58).
            var addModelReq = WsReq(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
            addModelReq.Content = JsonContent.Create(new { uri = modelUri, name = "NodeEditModel", version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
            (await Client.SendAsync(addModelReq)).EnsureSuccessStatusCode();

            var createTypeReq = WsReq(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=58")}/children");
            createTypeReq.Content = JsonContent.Create(new
            {
                modelUri,
                nodeClass = "ObjectType",
                browseName = "WidgetType",
                displayName = "WidgetType",
                referenceTypeId = "i=45",
            });
            var createTypeResp = await Client.SendAsync(createTypeReq);
            var createBody = await createTypeResp.Content.ReadAsStringAsync();
            Assert.True(createTypeResp.IsSuccessStatusCode,
                $"create WidgetType failed ({createTypeResp.StatusCode}): {createBody}");
            var nodeId = JsonSerializer.Deserialize<JsonElement>(createBody).GetProperty("nodeId").GetString()!;

            // Edit DisplayName + Description.
            var editReq = WsReq(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
            editReq.Content = JsonContent.Create(new
            {
                browseName = "WidgetType",
                displayName = "Edited Widget",
                description = "An edited description",
            });
            var editResp = await Client.SendAsync(editReq);
            Assert.True(editResp.IsSuccessStatusCode,
                $"edit failed ({editResp.StatusCode}): {await editResp.Content.ReadAsStringAsync()}");

            // GET forces a rebuild from the DB (the PUT invalidated the cache), regenerating
            // the nodeset from stored rows. The edit must be reflected after that round trip.
            var getReq = WsReq(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
            var getResp = await Client.SendAsync(getReq);
            getResp.EnsureSuccessStatusCode();
            var node = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Edited Widget", node.GetProperty("displayName").GetProperty("text").GetString());
            Assert.Equal("An edited description", node.GetProperty("description").GetProperty("text").GetString());

            var delWsReq = AsUser(HttpMethod.Delete,
                $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await Client.SendAsync(delWsReq)).StatusCode);
            wsUrn = null;
        }
        finally
        {
            if (wsUrn != null)
            {
                var del = AsUser(HttpMethod.Delete,
                    $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
                await Client.SendAsync(del);
            }
        }
    }
}