using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the model-link authorization boundary. Model rows are shared across workspaces and
/// are read by id, so the link endpoint (POST namespaces/info/{id}/link) must refuse to pull a
/// model that is not part of the shareable catalog — otherwise any authenticated user could link
/// another user's PRIVATE model into their own workspace by id and read its full contents (IDOR).
///
/// The gate allows: published models, Cloud Library copies, and Cloud Library catalog GUIDs. It
/// must reject a working copy owned by someone else. A rejected id returns 404 so the response
/// can't be used to probe for private ids.
///
/// Link flags deliberately do NOT confer shareability — see ModelOriginTrustTests for why
/// "reachable through a non-private link" was removed from this gate.
/// </summary>
[Collection("Api")]
public class ModelLinkAuthorizationTests : UaRestTestBase
{
    public ModelLinkAuthorizationTests(ApiFixture fixture) : base(fixture) { }

    private const string VictimId = "link-victim-001", VictimEmail = "link-victim@test.net";
    private const string AttackerId = "link-attacker-001", AttackerEmail = "link-attacker@test.net";
    private const string VictimWsName = "LinkVictimWorkspace";
    private const string AttackerWsName = "LinkAttackerWorkspace";
    private const string PrivateUri = "http://test.example.org/UA/LinkVictimPrivate/";
    private const string PublishedUri = "http://test.example.org/UA/LinkVictimPublished/";

    [Fact]
    public async Task Link_RejectsAnotherUsersPrivateModel_AllowsPublished()
    {
        string? victimUrn = null, attackerUrn = null;
        try
        {
            await FindAndDeleteWorkspace(VictimId, VictimEmail, VictimWsName);
            await FindAndDeleteWorkspace(AttackerId, AttackerEmail, AttackerWsName);

            // Victim creates a workspace and a private (unpublished, never-shared) model.
            victimUrn = await CreateWorkspace(VictimId, VictimEmail, VictimWsName);
            var privateModelId = await CreateModel(VictimId, VictimEmail, victimUrn, PrivateUri, "VictimPrivate");

            // Attacker, a different authenticated user, creates their own workspace.
            attackerUrn = await CreateWorkspace(AttackerId, AttackerEmail, AttackerWsName);

            // Attacker attempts to link the victim's private model id by guessing/obtaining it.
            // Both the shared (isPrivate=false) and the private (isPrivate=true) request shapes
            // must be refused with 404 — the private path must not be reachable from link.
            foreach (var isPrivate in new[] { false, true })
            {
                var resp = await Link(AttackerId, AttackerEmail, attackerUrn, privateModelId, isPrivate);
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }

            // The victim's model must not have leaked into the attacker's workspace, and the
            // attacker must not be able to read its nodes.
            var attackerModels = await ListModels(AttackerId, AttackerEmail, attackerUrn);
            Assert.DoesNotContain(attackerModels.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == PrivateUri);

            // Positive control: once the victim PUBLISHES a model, it becomes part of the shareable
            // catalog and another user CAN link it — proving the gate blocks only private models.
            var publishedModelId = await CreateModel(VictimId, VictimEmail, victimUrn, PublishedUri, "VictimPublished");
            await Publish(VictimId, VictimEmail, victimUrn, publishedModelId);

            var pubLink = await Link(AttackerId, AttackerEmail, attackerUrn, publishedModelId, false);
            Assert.True(pubLink.IsSuccessStatusCode,
                $"Linking a published model should succeed, got {pubLink.StatusCode}: {await pubLink.Content.ReadAsStringAsync()}");

            var attackerModels2 = await ListModels(AttackerId, AttackerEmail, attackerUrn);
            Assert.Contains(attackerModels2.GetProperty("results").EnumerateArray(),
                m => m.GetProperty("uri").GetString() == PublishedUri);
        }
        finally
        {
            if (attackerUrn != null) await DeleteWorkspace(AttackerId, AttackerEmail, attackerUrn);
            if (victimUrn != null) await DeleteWorkspace(VictimId, VictimEmail, victimUrn);
            // The shared test DB is never wiped and published model rows survive workspace deletion,
            // so purge this test's model rows directly to stay idempotent across runs.
            await PurgeModels(PrivateUri, PublishedUri);
        }
    }

    /// <summary>Delete model rows (and their nodes/references/links) for the given URIs.</summary>
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

    private async Task<string> CreateWorkspace(string userId, string email, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
        req.Content = JsonContent.Create(new { applicationName = name, description = "link-auth test" });
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
            uri,
            name,
            version = "1.0.0",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateModel failed ({resp.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetString()!;
    }

    private async Task Publish(string userId, string email, string wsUrn, string modelId)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { action = "publish", description = "Published for link-auth test" });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Publish failed ({resp.StatusCode}): {body}");
    }

    private async Task<HttpResponseMessage> Link(string userId, string email, string wsUrn, string modelId, bool isPrivate)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/link", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { isPrivate });
        return await Client.SendAsync(req);
    }

    private async Task<JsonElement> ListModels(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task DeleteWorkspace(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Delete,
            $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
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
