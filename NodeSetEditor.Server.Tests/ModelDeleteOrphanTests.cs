using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Deleting a private model takes its stored versions with it.
///
/// A workspace holds one link per stored version of a URI — the version in use plus the backup
/// each checkout retains — so removing "the model" has to clear all of them. It used to remove
/// only the first link it matched on the URI, leaving the rest linked: they never looked
/// orphaned, so the collector never took them, and the rows stayed in the database holding
/// their slots in the unique (Uri, VersionNorm) index.
/// </summary>
[Collection("Api")]
public class ModelDeleteOrphanTests : UaRestTestBase
{
    public ModelDeleteOrphanTests(ApiFixture fixture) : base(fixture) { }

    private const string OwnerId = "del-orphan-001", OwnerEmail = "del-orphan@test.net";
    private const string WsName = "DeleteOrphanWorkspace";
    private const string ModelUri = "http://test.example.org/UA/DeleteOrphan/";

    [Fact]
    public async Task DeletingAPrivateModel_AlsoDeletesItsRetainedVersions()
    {
        string? wsUrn = null;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, WsName);
            await PurgeModels(ModelUri);

            wsUrn = await CreateWorkspace(OwnerId, OwnerEmail, WsName);
            var modelId = await CreateModel(OwnerId, OwnerEmail, wsUrn, ModelUri, "DeleteOrphan", "1.0.0");

            // Each checkout mints a working copy and retains the previous row as a backup, so
            // this is what leaves more than one stored version behind for the URI.
            await Checkout(OwnerId, OwnerEmail, wsUrn, modelId);
            var afterCheckout = await CurrentModelIdAsync(wsUrn);
            await Checkin(OwnerId, OwnerEmail, wsUrn, afterCheckout, "keep");
            await Checkout(OwnerId, OwnerEmail, wsUrn, afterCheckout);

            // Guard the premise: without several rows for the URI this test proves nothing.
            Assert.True(await RowCountAsync(ModelUri) > 1,
                "Expected checkout to retain at least one backup version for the URI.");

            var current = await CurrentModelIdAsync(wsUrn);
            var del = AsUser(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{current}", OwnerId, OwnerEmail);
            del.Headers.Add("OpcUa-Server", wsUrn);
            var delResp = await Client.SendAsync(del);
            Assert.True(delResp.IsSuccessStatusCode,
                $"Delete failed ({delResp.StatusCode}): {await delResp.Content.ReadAsStringAsync()}");

            // Nothing unpublished may survive: no rows, and no links pointing at them.
            Assert.Equal(0, await RowCountAsync(ModelUri));
            Assert.Equal(0, await LinkCountAsync(ModelUri));

            // ...and the model is gone from the workspace listing.
            var models = await ListModels(OwnerId, OwnerEmail, wsUrn);
            Assert.DoesNotContain(models.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == ModelUri);
        }
        finally
        {
            if (wsUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, wsUrn);
            await PurgeModels(ModelUri);
        }
    }

    /// <summary>
    /// A Cloud Library copy is a cache of a public catalog entry, not something the workspace
    /// owns: it is the row every other workspace resolves this namespace against, so dropping
    /// a link to it must not take the row with it — the next dependency resolution would only
    /// download it again.
    /// </summary>
    [Fact]
    public async Task DeletingACloudLibraryModel_KeepsTheRow()
    {
        string? wsUrn = null;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, WsName);
            await PurgeModels(ModelUri);

            wsUrn = await CreateWorkspace(OwnerId, OwnerEmail, WsName);
            var modelId = await CreateModel(OwnerId, OwnerEmail, wsUrn, ModelUri, "FromCloudLib", "1.0.0");

            // Stand in for a Cloud Library import, which produces an UNOWNED shared row: that
            // is the shape the retention rule reads, so the tier and the owner both have to be
            // set, not just the provenance. Done directly to keep the test off the network --
            // the Cloud Library is not configured in this environment at all.
            await MakeSharedCloudLibraryAsync(modelId);

            var del = AsUser(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{modelId}", OwnerId, OwnerEmail);
            del.Headers.Add("OpcUa-Server", wsUrn);
            var delResp = await Client.SendAsync(del);
            Assert.True(delResp.IsSuccessStatusCode,
                $"Delete failed ({delResp.StatusCode}): {await delResp.Content.ReadAsStringAsync()}");

            // The link goes; the row stays.
            Assert.Equal(0, await LinkCountAsync(ModelUri));
            Assert.Equal(1, await RowCountAsync(ModelUri));

            // ...and it is out of the workspace listing, so the user still sees it as removed.
            var models = await ListModels(OwnerId, OwnerEmail, wsUrn);
            Assert.DoesNotContain(models.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == ModelUri);
        }
        finally
        {
            if (wsUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, wsUrn);
            await PurgeModels(ModelUri);
        }
    }

    /// <summary>
    /// Turns a row into what a Cloud Library import produces: the unowned shared copy for its
    /// URI and version. Clearing the owner alongside the tier is required, not tidiness --
    /// CK_Models_Tier_Owner rejects a non-private row that still carries one.
    /// </summary>
    private async Task MakeSharedCloudLibraryAsync(string modelId)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        var id = Guid.Parse(modelId);
        await db.Models.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Origin, ModelOrigin.CloudLibrary)
            .SetProperty(m => m.Tier, ModelTier.Shared)
            .SetProperty(m => m.OwnerWorkspaceId, (Guid?)null));
    }

    /// <summary>The id the workspace currently serves for the URI.</summary>
    private async Task<string> CurrentModelIdAsync(string wsUrn)
    {
        var models = await ListModels(OwnerId, OwnerEmail, wsUrn);
        var row = models.GetProperty("results").EnumerateArray()
            .First(m => m.GetProperty("uri").GetString() == ModelUri);
        return row.GetProperty("id").GetString()!;
    }

    private async Task<int> RowCountAsync(string uri)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        return await db.Models.CountAsync(m => m.Uri == uri);
    }

    private async Task<int> LinkCountAsync(string uri)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        return await db.WorkspaceModels.CountAsync(wm => wm.Model!.Uri == uri);
    }

    private async Task<string> CreateWorkspace(string userId, string email, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
        req.Content = JsonContent.Create(new { applicationName = name, description = "delete-orphan test" });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applicationUri").GetString()!;
    }

    private async Task<string> CreateModel(
        string userId, string email, string wsUrn, string uri, string name, string version)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new
        {
            uri,
            name,
            version,
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateModel failed ({resp.StatusCode}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    private async Task Checkout(string userId, string email, string wsUrn, string modelId)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkout", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"Checkout failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task Checkin(string userId, string email, string wsUrn, string modelId, string action)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { action, description = "delete-orphan test" });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"Checkin '{action}' failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<JsonElement> ListModels(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/namespaces/info?start=0&count=1000", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task DeleteWorkspace(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Delete, $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
        await Client.SendAsync(req);
    }

    private async Task FindAndDeleteWorkspace(string userId, string email, string wsName)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/discovery", userId, email);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");

        var existing = results.EnumerateArray()
            .FirstOrDefault(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == wsName);
        if (existing.ValueKind == JsonValueKind.Undefined) return;

        await DeleteWorkspace(userId, email, existing.GetProperty("applicationUri").GetString()!);
    }

    private async Task PurgeModels(params string[] uris)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        var ids = await db.Models.Where(m => uris.Contains(m.Uri)).Select(m => m.Id).ToListAsync();
        foreach (var id in ids)
        {
            await db.References.Where(r => r.ModelId == id).ExecuteDeleteAsync();
            await db.Nodes.Where(n => n.ModelId == id).ExecuteDeleteAsync();
            await db.WorkspaceModels.Where(wm => wm.ModelId == id).ExecuteDeleteAsync();
        }
        await db.Models.Where(m => uris.Contains(m.Uri)).ExecuteDeleteAsync();
    }
}
