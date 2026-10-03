using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Base class for REST API integration tests.
/// Provides shared helpers for making authenticated requests with OpcUa-Server header.
/// </summary>
public abstract class UaRestTestBase
{
    protected readonly ApiFixture Fixture;
    protected readonly HttpClient Client;
    protected readonly string WorkspaceUrn;

    protected static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected UaRestTestBase(ApiFixture fixture)
    {
        Fixture = fixture;
        Client = fixture.Client;
        WorkspaceUrn = fixture.WorkspaceUrn!;
    }

    /// <summary>
    /// Deletes the model rows (and their nodes/references/links) for the given namespace URIs.
    ///
    /// Deleting a workspace is NOT enough to clean up after a test: that cascades the
    /// WorkspaceModels links but never runs the orphan collector, so the Model rows survive
    /// unreachable — and the shared test database is never wiped, so they accumulate run on
    /// run. Those leftovers are indistinguishable from real data to any lookup, which is how
    /// a stale row from an earlier run came to be served in place of the one a test had just
    /// created. Call this from a finally block for every URI the test creates.
    /// </summary>
    protected async Task PurgeModelRowsAsync(params string[] uris)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();

        var ids = await db.Models.Where(m => uris.Contains(m.Uri)).Select(m => m.Id).ToListAsync();
        foreach (var id in ids)
        {
            await db.References.Where(r => r.ModelId == id).ExecuteDeleteAsync();
            await db.Nodes.Where(n => n.ModelId == id).ExecuteDeleteAsync();
            await db.NodeSetTypes.Where(t => t.ModelId == id).ExecuteDeleteAsync();
            await db.WorkspaceModels.Where(wm => wm.ModelId == id).ExecuteDeleteAsync();
        }
        await db.Models.Where(m => uris.Contains(m.Uri)).ExecuteDeleteAsync();
    }

    protected HttpRequestMessage WithServer(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("OpcUa-Server", WorkspaceUrn);
        return request;
    }

    /// <summary>
    /// Encodes a NodeId as a base64url URL slug. Mirrors the frontend's
    /// slugifyNodeId helper so tests exercise the exact same code path the
    /// SPA hits — never call Uri.EscapeDataString on a NodeId.
    /// </summary>
    protected static string Slug(string nodeId) => NodeIdSlugFilter.EncodeBase64Url(nodeId);

    /// <summary>
    /// Creates a request that impersonates a different user via DevAuth headers.
    /// </summary>
    protected HttpRequestMessage AsUser(HttpMethod method, string url, string userId, string email)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Dev-Auth", "true");
        request.Headers.Add("X-Dev-UserId", userId);
        request.Headers.Add("X-Dev-UserEmail", email);
        return request;
    }

    protected async Task<JsonElement> GetNode(string nodeId)
    {
        var req = WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    protected async Task<string> CreateChildNode(string parentNodeId, string nodeClass, string browseName,
        string? typeDefinitionId = null, string referenceTypeId = "i=47", string? modelUri = null)
    {
        var req = WithServer(HttpMethod.Post,
            $"/api/opcua/v1/nodes/{Slug(parentNodeId)}/children");
        req.Content = JsonContent.Create(new
        {
            modelUri = modelUri ?? ApiFixture.TestModelUri,
            nodeClass,
            browseName,
            displayName = browseName,
            referenceTypeId,
            typeDefinitionId,
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateChild failed ({resp.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString()!;
    }
}
