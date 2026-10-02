using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The icon contract between server and client.
///
/// A type family's icon is stamped on the TYPE node at import (Nodes."Icon", see
/// NodeSetModel/NodeIcons.cs) and served on <c>Node.icon</c>; the client maps that key to a
/// glyph and falls back to a per-NodeClass default when the key is absent. Two things have
/// gone wrong here before and neither was caught by a test, because there were none:
///
/// 1. The whole threading was dropped in a merge, so every node was served <c>icon: null</c>
///    and the UI silently showed NodeClass defaults everywhere. A green suite said nothing.
/// 2. Most stamped keys describe the family's INSTANCES, not the type node carrying them, so
///    serving a node its own key put a "property" label on PropertyType itself.
///
/// These tests pin both directions: that the pipeline carries a key at all, and that a type
/// node is not dressed as one of its own instances.
/// </summary>
[Collection("Api")]
public class NodeIconContractTests
{
    /// <summary>BaseInterfaceType — the one family whose icon belongs on the ObjectType
    /// itself, since no instances of an InterfaceType exist.</summary>
    private const string BaseInterfaceType = "i=17602";
    private const string PropertyType = "i=68";
    private const string BaseDataVariableType = "i=63";
    /// <summary>The Server object, whose children include PropertyType-typed properties
    /// (ServerArray, NamespaceArray, ...).</summary>
    private const string ServerObject = "i=2253";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public NodeIconContractTests(ApiFixture fixture)
    {
        _client = fixture.Client;
        _workspaceUrn = fixture.WorkspaceUrn!;
    }

    private HttpRequestMessage WithServer(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
        return request;
    }

    private static string Slug(string nodeId) => NodeIdSlugFilter.EncodeBase64Url(nodeId);

    private async Task<List<JsonElement>> GetResultsAsync(string url)
    {
        var response = await _client.SendAsync(WithServer(url));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    /// <summary>The served icon key, or null when the field is absent or JSON null.</summary>
    private static string? Icon(JsonElement node) =>
        node.TryGetProperty("icon", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement ById(List<JsonElement> nodes, string nodeId) =>
        nodes.Single(n => n.GetProperty("nodeId").GetString() == nodeId);

    private Task<List<JsonElement>> SelfAndSubtypesAsync(string category, string nodeId) =>
        GetResultsAsync($"/api/opcua/v1/types/{category}/{Slug(nodeId)}/subtypes"
            + "?includeSelf=true&depth=1&count=10000");

    /// <summary>
    /// The regression guard for the dropped-threading failure: if the server stops attaching
    /// icons entirely, every other assertion here would still pass by reading null, so one
    /// test has to prove a key actually arrives.
    /// </summary>
    [Fact]
    public async Task BaseInterfaceType_IsServedTheInterfaceIcon()
    {
        var results = await SelfAndSubtypesAsync("object-types", BaseInterfaceType);

        Assert.Equal("interfaceType", Icon(ById(results, BaseInterfaceType)));
    }

    [Fact]
    public async Task PropertyType_TheTypeNode_IsNotDressedAsOneOfItsProperties()
    {
        var results = await SelfAndSubtypesAsync("variable-types", PropertyType);

        // Null, not "property": the client then renders the VariableType glyph, which is
        // what a VariableType node should look like.
        Assert.Null(Icon(ById(results, PropertyType)));
    }

    [Fact]
    public async Task BaseDataVariableType_TheTypeNode_IsNotDressedAsOneOfItsVariables()
    {
        var results = await SelfAndSubtypesAsync("variable-types", BaseDataVariableType);

        Assert.Null(Icon(ById(results, BaseDataVariableType)));
    }

    /// <summary>
    /// The other half of the rule: an INSTANCE does take its TypeDefinition's key, which is
    /// where "property" is supposed to show up.
    /// </summary>
    [Fact]
    public async Task AnInstanceTypedByPropertyType_IsServedThePropertyIcon()
    {
        var children = await GetResultsAsync(
            $"/api/opcua/v1/nodes/{Slug(ServerObject)}/children?full=true&count=10000");

        var properties = children
            .Where(c => c.TryGetProperty("typeDefinition", out var t) && t.GetString() == PropertyType)
            .ToList();

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.Equal("property", Icon(p)));
    }
}
