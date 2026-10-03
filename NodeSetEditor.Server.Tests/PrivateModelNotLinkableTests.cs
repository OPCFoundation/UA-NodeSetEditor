using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// A PRIVATE model is one workspace's own copy and can never be used by another workspace —
/// whatever else it looks like.
///
/// Checking a model in only locks it (IsEditable = false); it does not make it shared. And the
/// shareability gate used to read provenance rather than ownership (`Published || Origin ==
/// CloudLibrary`), which a private row can satisfy: a private copy of a catalog namespace
/// carries Origin = CloudLibrary, as does every row the tier migration classified from legacy
/// link flags. So another workspace could link a user's edited copy as though it were the
/// catalog's own. The gate is now ownership: Tier == Private is never linkable.
///
/// See <see cref="ModelLinkAuthorizationTests"/> for the unpublished-and-never-checked-in case.
/// </summary>
[Collection("Api")]
public class PrivateModelNotLinkableTests : UaRestTestBase
{
    public PrivateModelNotLinkableTests(ApiFixture fixture) : base(fixture) { }

    private const string OwnerId = "notlink-owner", OwnerEmail = "notlink-owner@test.net";
    private const string OtherId = "notlink-other", OtherEmail = "notlink-other@test.net";
    private const string OwnerWsName = "NotLinkableOwnerWorkspace";
    private const string OtherWsName = "NotLinkableOtherWorkspace";
    private const string PrivateUri = "http://test.example.org/UA/NotLinkable/";

    [Fact]
    public async Task ACheckedInPrivateModel_IsStillNotLinkableByAnotherWorkspace()
    {
        string? ownerUrn = null, otherUrn = null;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            await FindAndDeleteWorkspace(OtherId, OtherEmail, OtherWsName);
            await PurgeModelRowsAsync(PrivateUri);

            ownerUrn = await CreateWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            var modelId = await CreateModel(OwnerId, OwnerEmail, ownerUrn, PrivateUri, "NotLinkable");

            // Check in with "keep": the row is locked but remains the owner's private copy.
            await Checkin(OwnerId, OwnerEmail, ownerUrn, modelId, "keep");
            Assert.Equal(ModelTier.Private, await TierAsync(PrivateUri));

            otherUrn = await CreateWorkspace(OtherId, OtherEmail, OtherWsName);

            // Both request shapes must be refused, and the private path especially.
            foreach (var isPrivate in new[] { false, true })
            {
                var resp = await Link(OtherId, OtherEmail, otherUrn, modelId, isPrivate);
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }

            // Now the specific hole: a private row whose provenance says Cloud Library. The old
            // gate read provenance, so this was linkable; ownership says it is not.
            await SetOriginAsync(PrivateUri, ModelOrigin.CloudLibrary);
            var cloudResp = await Link(OtherId, OtherEmail, otherUrn, modelId, isPrivate: false);
            Assert.Equal(HttpStatusCode.NotFound, cloudResp.StatusCode);

            // Nothing leaked into the other workspace.
            var otherModels = await ListModels(OtherId, OtherEmail, otherUrn);
            Assert.DoesNotContain(otherModels.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == PrivateUri);
        }
        finally
        {
            if (otherUrn != null) await DeleteWorkspace(OtherId, OtherEmail, otherUrn);
            if (ownerUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, ownerUrn);
            await PurgeModelRowsAsync(PrivateUri);
        }
    }

    private async Task<ModelTier> TierAsync(string uri)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        return await db.Models.Where(m => m.Uri == uri).Select(m => m.Tier).FirstAsync();
    }

    private async Task SetOriginAsync(string uri, ModelOrigin origin)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        await db.Models.Where(m => m.Uri == uri)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Origin, origin));
    }

    private async Task<HttpResponseMessage> Link(
        string userId, string email, string wsUrn, string modelId, bool isPrivate)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/link", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { isPrivate });
        return await Client.SendAsync(req);
    }

    private async Task Checkin(string userId, string email, string wsUrn, string modelId, string action)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { action, description = "not-linkable test" });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"Checkin '{action}' failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<string> CreateWorkspace(string userId, string email, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
        req.Content = JsonContent.Create(new { applicationName = name, description = "not-linkable test" });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applicationUri").GetString()!;
    }

    private async Task<string> CreateModel(string userId, string email, string wsUrn, string uri, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new
        {
            uri, name,
            version = "1.0.0",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateModel failed ({resp.StatusCode}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
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
}
