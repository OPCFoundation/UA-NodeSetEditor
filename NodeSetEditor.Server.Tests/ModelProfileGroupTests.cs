using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The NodeSet's profile group: model metadata edited through the model-update endpoint that the
/// Edit Model dialog uses. Stored in the model's Metadata JSON rather than its own column (see
/// <see cref="NodeSetEditor.Model.Model.ProfileGroupNameKey"/>), so the cases that matter are the
/// ones where a write elsewhere could silently drop it.
/// </summary>
[Collection("Api")]
public class ModelProfileGroupTests
{
    private const string UaCoreNamespace = "http://opcfoundation.org/UA/";
    /// <summary>BaseObjectType — the root every new ObjectType extends.</summary>
    private const string BaseObjectType = "i=58";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public ModelProfileGroupTests(ApiFixture fixture)
    {
        _client = fixture.Client;
        _workspaceUrn = fixture.WorkspaceUrn!;
    }

    private HttpRequestMessage WithServer(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
        return request;
    }

    private static string Slug(string nodeId) => NodeIdSlugFilter.EncodeBase64Url(nodeId);

    private Task<string> GetTestModelIdAsync() => GetModelIdAsync(ApiFixture.TestModelUri);

    /// <summary>A workspace model's id, resolved from the workspace's namespace list.</summary>
    private async Task<string> GetModelIdAsync(string namespaceUri)
    {
        var response = await _client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var ns in body.GetProperty("results").EnumerateArray())
        {
            if (ns.TryGetProperty("uri", out var uri) && uri.GetString() == namespaceUri)
                return ns.GetProperty("id").GetString()!;
        }
        throw new InvalidOperationException($"Model '{namespaceUri}' not found in the workspace.");
    }

    /// <summary>
    /// The model's profile group, read off the workspace's namespace list — it is model metadata,
    /// so it rides along with the rest of the model info rather than having an endpoint of its own.
    /// </summary>
    private async Task<string?> GetProfileGroupAsync(string modelId)
    {
        var response = await _client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var ns in body.GetProperty("results").EnumerateArray())
        {
            if (!ns.TryGetProperty("id", out var id) || id.GetString() != modelId) continue;
            return ns.TryGetProperty("profileGroupName", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
        }
        throw new InvalidOperationException($"Model '{modelId}' not found in the workspace.");
    }

    /// <summary>
    /// Sets the profile group through the model-update endpoint the edit dialog uses. Pass an
    /// empty string to clear it — null means "leave unchanged", so it cannot restore "none".
    /// </summary>
    private async Task<HttpResponseMessage> PutProfileGroupAsync(string modelId, string? profileGroupName)
    {
        var request = WithServer(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}");
        request.Content = JsonContent.Create(new { profileGroupName = profileGroupName ?? string.Empty });
        return await _client.SendAsync(request);
    }

    /// <summary>Creates an ObjectType under BaseObjectType and returns its NodeId.</summary>
    private async Task<string> CreateObjectTypeAsync(string browseName)
    {
        var request = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(BaseObjectType)}/children");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "ObjectType",
            browseName,
            displayName = browseName,
            referenceTypeId = "i=45", // HasSubtype
        });
        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodeId").GetString()!;
    }

    private async Task DeleteNodeAsync(string nodeId) =>
        await _client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId)}"));

    [Fact]
    public async Task ProfileGroup_RoundTripsAndClears()
    {
        var modelId = await GetTestModelIdAsync();
        var original = await GetProfileGroupAsync(modelId);
        try
        {
            (await PutProfileGroupAsync(modelId, "UACore 1.05")).EnsureSuccessStatusCode();
            Assert.Equal("UACore 1.05", await GetProfileGroupAsync(modelId));

            // Blank means "none", not an empty-string profile group.
            (await PutProfileGroupAsync(modelId, "  ")).EnsureSuccessStatusCode();
            Assert.Null(await GetProfileGroupAsync(modelId));
        }
        finally
        {
            await PutProfileGroupAsync(modelId, original);
        }
    }

    [Fact]
    public async Task ProfileGroup_CannotBeSetOnASharedModel()
    {
        // Model rows are shared across workspaces, so setting this through a shared link would
        // change it for everyone holding that link — same rule as license/copyright. Admins are
        // the exception; that path is covered by AdminModelCurationTests.
        var coreModelId = await GetModelIdAsync(UaCoreNamespace);

        var response = await PutProfileGroupAsync(coreModelId, "UACore 1.05");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await GetProfileGroupAsync(coreModelId));
    }

    [Fact]
    public async Task ProfileGroup_SurvivesUnrelatedModelEdits()
    {
        // The profile group lives in the model's Metadata JSON, which is rebuilt wholesale on
        // import — so a node write (which rebuilds the address space) is exactly where it could
        // silently vanish.
        var modelId = await GetTestModelIdAsync();
        var original = await GetProfileGroupAsync(modelId);
        string? nodeId = null;
        try
        {
            (await PutProfileGroupAsync(modelId, "UACore 1.05")).EnsureSuccessStatusCode();

            nodeId = await CreateObjectTypeAsync("ProfileGroupUntouchedProbe");

            Assert.Equal("UACore 1.05", await GetProfileGroupAsync(modelId));
        }
        finally
        {
            if (nodeId != null) await DeleteNodeAsync(nodeId);
            await PutProfileGroupAsync(modelId, original);
        }
    }
}
