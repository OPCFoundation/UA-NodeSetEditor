using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Node rows must carry enough to render a "[ModelName]:Name" label. Two defects made every
/// type-picker dropdown misleading: the payload had no modelUri (so the client defaulted the
/// namespace to core and labelled companion types "[Core]:"), and a node saved with a blank
/// DisplayName stored an empty LocalizedText that beat the BrowseName fallback, leaving a
/// nameless "[Core]:" row.
/// </summary>
[Collection("Api")]
public class NodeLabelMetadataTests
{
    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public NodeLabelMetadataTests(ApiFixture fixture)
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

    private static string Text(JsonElement n, string prop) =>
        n.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string DisplayText(JsonElement n) =>
        n.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.Object
            ? Text(dn, "text") : "";

    private async Task<string> CreateDataTypeAsync(string browseName, string? displayName)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug("i=22")}/children");
        req.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "DataType",
            browseName,
            displayName,
            referenceTypeId = "i=45", // HasSubtype
        });
        var resp = await _client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"create {browseName} failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nodeId").GetString()!;
    }

    [Fact]
    public async Task QueryTypes_ReturnsModelUri_AndNeverANamelessRow()
    {
        // Two companion DataTypes: one named, one whose DisplayName the user left blank.
        var namedId = await CreateDataTypeAsync("LabelTestNamedType", "LabelTestNamedType");
        var blankId = await CreateDataTypeAsync("LabelTestBlankDisplayType", "");

        var resp = await _client.SendAsync(WithServer(HttpMethod.Get,
            "/api/opcua/v1/query/types?nodeClass=DataType&start=0&count=10000"));
        resp.EnsureSuccessStatusCode();
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("results").EnumerateArray().ToList();

        JsonElement Row(string nodeId) => results.First(n => Text(n, "nodeId") == nodeId);

        // The dropdown's "[ModelName]:" prefix is derived from modelUri; without it the
        // client falls back to the core namespace and every companion type reads "[Core]:".
        Assert.Equal(ApiFixture.TestModelUri, Text(Row(namedId), "modelUri"));
        Assert.Equal("http://opcfoundation.org/UA/", Text(Row("i=22"), "modelUri"));

        // A blank DisplayName is unset, not "": the node reads as its BrowseName.
        Assert.Equal("LabelTestBlankDisplayType", DisplayText(Row(blankId)));

        // Nothing in the list is nameless.
        Assert.DoesNotContain(results, n =>
            string.IsNullOrEmpty(DisplayText(n)) && string.IsNullOrEmpty(Text(n, "browseName")));
    }

    [Fact]
    public async Task UpdateNode_RejectsBlankBrowseName_AndUnsetsBlankDisplayName()
    {
        var nodeId = await CreateDataTypeAsync("LabelTestEditableType", "Editable Label");

        // Blanking the BrowseName used to store "nsu=<uri>;" — a nameless node.
        var blankReq = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        blankReq.Content = JsonContent.Create(new { browseName = "   " });
        var blankResp = await _client.SendAsync(blankReq);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, blankResp.StatusCode);

        // Clearing DisplayName is allowed — it unsets the attribute rather than storing an
        // empty LocalizedText, so the node keeps a name (the persisted one, or its
        // BrowseName) instead of going nameless.
        var clearReq = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        clearReq.Content = JsonContent.Create(new { displayName = "" });
        var clearResp = await _client.SendAsync(clearReq);
        clearResp.EnsureSuccessStatusCode();

        var after = await _client.SendAsync(WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(nodeId)}"));
        after.EnsureSuccessStatusCode();
        var node = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(DisplayText(node)), "the node must never end up nameless");
        Assert.Contains("LabelTestEditableType", Text(node, "browseName"));
        Assert.Equal(ApiFixture.TestModelUri, Text(node, "modelUri"));
    }
}
