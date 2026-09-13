using System.Net.Http.Json;
using System.Text.Json;
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
