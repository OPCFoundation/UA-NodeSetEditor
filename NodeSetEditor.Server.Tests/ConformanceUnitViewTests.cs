using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The NodeSet-wide view of conformance units (/conformance-units) and the profile settings
/// that go with it. Per-node conformance-unit persistence is covered by
/// <see cref="ConformanceUnitsTests"/>; what matters here is the aggregation across nodes and
/// that the profile group survives a round trip through the model's Metadata JSON.
/// </summary>
[Collection("Api")]
public class ConformanceUnitViewTests
{
    /// <summary>BaseObjectType — the root every new ObjectType extends.</summary>
    private const string BaseObjectType = "i=58";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public ConformanceUnitViewTests(ApiFixture fixture)
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

    /// <summary>The test model's id, resolved from the workspace's namespace list.</summary>
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

    private async Task<string> CreateObjectTypeAsync(string browseName, params string[] units)
    {
        var request = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(BaseObjectType)}/children");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "ObjectType",
            browseName,
            displayName = browseName,
            referenceTypeId = "i=45", // HasSubtype
            category = units,
        });
        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;
    }

    private async Task DeleteNodeAsync(string nodeId) =>
        await _client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId)}"));

    private async Task<Dictionary<string, int>> ListUnitsAsync(string modelId)
    {
        var response = await _client.SendAsync(WithServer(
            HttpMethod.Get, $"/api/opcua/v1/conformance-units?modelId={modelId}"));
        Assert.True(response.IsSuccessStatusCode,
            $"list failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.EnumerateArray().ToDictionary(
            u => u.GetProperty("name").GetString()!,
            u => u.GetProperty("nodeCount").GetInt32());
    }

    /// <summary>
    /// The model's profile group, read off the workspace's namespace list — it is model
    /// metadata, so it rides along with the rest of the model info rather than having an
    /// endpoint of its own.
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

    [Fact]
    public async Task List_CountsTheNodesDeclaringEachUnit()
    {
        var modelId = await GetTestModelIdAsync();

        // Two nodes share "CuViewShared"; only one declares "CuViewSolo".
        var first = await CreateObjectTypeAsync("CuViewCountA", "CuViewShared", "CuViewSolo");
        var second = await CreateObjectTypeAsync("CuViewCountB", "CuViewShared");
        try
        {
            var units = await ListUnitsAsync(modelId);

            Assert.Equal(2, units["CuViewShared"]);
            Assert.Equal(1, units["CuViewSolo"]);
        }
        finally
        {
            await DeleteNodeAsync(first);
            await DeleteNodeAsync(second);
        }
    }

    [Fact]
    public async Task List_DropsAUnitOnceNoNodeDeclaresIt()
    {
        var modelId = await GetTestModelIdAsync();

        var nodeId = await CreateObjectTypeAsync("CuViewTransient", "CuViewTransientUnit");
        Assert.Contains("CuViewTransientUnit", (await ListUnitsAsync(modelId)).Keys);

        await DeleteNodeAsync(nodeId);
        Assert.DoesNotContain("CuViewTransientUnit", (await ListUnitsAsync(modelId)).Keys);
    }

    [Fact]
    public async Task List_ShowsOnlyTheSelectedModelsUnits()
    {
        // The UA Core nodeset is linked into every workspace and declares hundreds of its own
        // conformance units. Listing the test model must not pick up any of them.
        var testModelId = await GetTestModelIdAsync();
        var coreModelId = await GetModelIdAsync("http://opcfoundation.org/UA/");

        var coreUnits = await ListUnitsAsync(coreModelId);
        Assert.NotEmpty(coreUnits); // otherwise this test proves nothing

        var testUnits = await ListUnitsAsync(testModelId);

        var leaked = testUnits.Keys.Intersect(coreUnits.Keys).ToList();
        Assert.True(leaked.Count == 0,
            $"Units from the Core nodeset leaked into the test model's list: {string.Join(", ", leaked.Take(10))}");
    }

    [Fact]
    public async Task List_IsSortedByName()
    {
        var modelId = await GetTestModelIdAsync();

        var response = await _client.SendAsync(WithServer(
            HttpMethod.Get, $"/api/opcua/v1/conformance-units?modelId={modelId}"));
        response.EnsureSuccessStatusCode();

        var names = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(u => u.GetProperty("name").GetString()!).ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task List_RejectsAModelThatIsNotInTheWorkspace()
    {
        var response = await _client.SendAsync(WithServer(
            HttpMethod.Get, $"/api/opcua/v1/conformance-units?modelId={Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

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
        // change it for everyone holding that link — same rule as license/copyright.
        var coreModelId = await GetModelIdAsync("http://opcfoundation.org/UA/");

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

            nodeId = await CreateObjectTypeAsync("CuViewProfileGroupUntouched", "CuViewUnrelated");

            Assert.Equal("UACore 1.05", await GetProfileGroupAsync(modelId));
        }
        finally
        {
            if (nodeId != null) await DeleteNodeAsync(nodeId);
            await PutProfileGroupAsync(modelId, original);
        }
    }
}
