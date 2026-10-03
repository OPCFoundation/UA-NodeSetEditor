using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Publishing is the act that makes a model the shared answer for its namespace URI to every
/// other user, so a URI someone else has already published is not available to publish over.
/// A NEW version does not make it acceptable either — a later version of another user's
/// namespace is still a takeover of it — which is why this is checked independently of the
/// (Uri, VersionNorm) collision rule that merely keeps versions distinct.
///
/// Editing in place is deliberately NOT restricted: a private model is the workspace's own and
/// anything goes there. Only the hand-off to other users is policed. See also
/// GuardCloudLibraryNamespaceAsync, which closes the same hole against the UA Cloud Library.
/// </summary>
[Collection("Api")]
public class PublishOwnershipTests : UaRestTestBase
{
    public PublishOwnershipTests(ApiFixture fixture) : base(fixture) { }

    private const string FirstId = "pub-owner-001", FirstEmail = "pub-owner@test.net";
    private const string SecondId = "pub-other-001", SecondEmail = "pub-other@test.net";
    private const string FirstWsName = "PublishOwnerWorkspace";
    private const string SecondWsName = "PublishOtherWorkspace";
    private const string ContestedUri = "http://test.example.org/UA/PublishOwnership/";

    [Fact]
    public async Task PublishingAUriAnotherUserPublished_IsRejected()
    {
        string? firstUrn = null, secondUrn = null;
        try
        {
            await FindAndDeleteWorkspace(FirstId, FirstEmail, FirstWsName);
            await FindAndDeleteWorkspace(SecondId, SecondEmail, SecondWsName);
            await PurgeModels(ContestedUri);

            // The first user authors the namespace and publishes it: it is now theirs.
            firstUrn = await CreateWorkspace(FirstId, FirstEmail, FirstWsName);
            var firstModelId = await CreateModel(FirstId, FirstEmail, firstUrn, ContestedUri, "Owned", "1.0.0");
            var firstPublish = await Publish(FirstId, FirstEmail, firstUrn, firstModelId);
            Assert.True(firstPublish.IsSuccessStatusCode,
                $"The first publish should succeed, got {firstPublish.StatusCode}: "
                + await firstPublish.Content.ReadAsStringAsync());

            // A second user authors the same URI privately. That is allowed — the restriction is
            // on publishing, not on holding a private copy. A distinct version keeps the
            // (Uri, VersionNorm) dedup index out of the way so the owner rule is what's measured.
            secondUrn = await CreateWorkspace(SecondId, SecondEmail, SecondWsName);
            var secondModelId = await CreateModel(SecondId, SecondEmail, secondUrn, ContestedUri, "Contested", "2.0.0");

            var secondPublish = await Publish(SecondId, SecondEmail, secondUrn, secondModelId);
            Assert.Equal(HttpStatusCode.BadRequest, secondPublish.StatusCode);
            Assert.Contains("another user", await secondPublish.Content.ReadAsStringAsync());

            // Positive control: the rule is about OTHER users, so the owner may still publish
            // their own namespace again. Without this, a blanket "already published" block
            // would pass the assertion above and go unnoticed.
            await Checkout(FirstId, FirstEmail, firstUrn, firstModelId);
            var republish = await Publish(FirstId, FirstEmail, firstUrn, firstModelId);
            Assert.True(republish.IsSuccessStatusCode,
                $"The owner should be able to republish their own URI, got {republish.StatusCode}: "
                + await republish.Content.ReadAsStringAsync());
        }
        finally
        {
            if (secondUrn != null) await DeleteWorkspace(SecondId, SecondEmail, secondUrn);
            if (firstUrn != null) await DeleteWorkspace(FirstId, FirstEmail, firstUrn);
            // Published rows survive workspace deletion and the shared test DB is never wiped,
            // so clear this test's model rows directly to stay idempotent across runs.
            await PurgeModels(ContestedUri);
        }
    }

    private async Task<string> CreateWorkspace(string userId, string email, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
        req.Content = JsonContent.Create(new { applicationName = name, description = "publish-ownership test" });
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

    private async Task<HttpResponseMessage> Publish(string userId, string email, string wsUrn, string modelId)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { action = "publish", description = "Published for publish-ownership test" });
        return await Client.SendAsync(req);
    }

    private async Task Checkout(string userId, string email, string wsUrn, string modelId)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkout", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"Checkout failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
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
}
