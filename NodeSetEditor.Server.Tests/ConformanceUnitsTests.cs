using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Conformance units are the NodeSet XML's &lt;Category&gt; elements. They are stored in the
/// node's Attributes JSON, which is replaced wholesale on every write — so the interesting
/// cases are the ones where an edit could silently drop them, and the re-read after each
/// write (which rebuilds the address space from the DB) is what proves it didn't.
/// </summary>
[Collection("Api")]
public class ConformanceUnitsTests
{
    /// <summary>BaseObjectType — the root every new ObjectType extends.</summary>
    private const string BaseObjectType = "i=58";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public ConformanceUnitsTests(ApiFixture fixture)
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

    private async Task<JsonElement> GetNodeAsync(string nodeId)
    {
        var response = await _client.SendAsync(WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(nodeId)}"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The node's conformance units. An empty list and an absent one both read as empty.</summary>
    private static string[] Units(JsonElement node) =>
        node.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(u => u.GetString()!).ToArray()
            : Array.Empty<string>();

    private static bool HasParent(JsonElement node) =>
        node.TryGetProperty("parentNodeId", out var p) && p.ValueKind == JsonValueKind.String;

    /// <summary>Creates an ObjectType under BaseObjectType and returns its NodeId.</summary>
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

    private async Task<HttpResponseMessage> UpdateNodeAsync(string nodeId, object body)
    {
        var request = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task DeleteNodeAsync(string nodeId) =>
        await _client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId)}"));

    [Fact]
    public async Task Create_KeepsTheUnitsAndTheirOrder()
    {
        var nodeId = await CreateObjectTypeAsync("ConformanceUnitsTestCreate", "Base Info", "Base Info Server");
        try
        {
            // The re-read comes from a rebuilt address space, so it covers the whole
            // round-trip: Attributes JSON → nodeset XML → back into the address space.
            Assert.Equal(new[] { "Base Info", "Base Info Server" }, Units(await GetNodeAsync(nodeId)));
        }
        finally
        {
            await DeleteNodeAsync(nodeId);
        }
    }

    [Fact]
    public async Task Update_ReplacesTheWholeList()
    {
        var nodeId = await CreateObjectTypeAsync("ConformanceUnitsTestReplace", "Original");
        try
        {
            var put = await UpdateNodeAsync(nodeId, new { category = new[] { "Second", "Third" } });
            Assert.True(put.IsSuccessStatusCode,
                $"update failed ({put.StatusCode}): {await put.Content.ReadAsStringAsync()}");

            // Replaced, not merged — "Original" is gone.
            Assert.Equal(new[] { "Second", "Third" }, Units(await GetNodeAsync(nodeId)));
        }
        finally
        {
            await DeleteNodeAsync(nodeId);
        }
    }

    [Fact]
    public async Task Update_WithAnEmptyListClearsThem()
    {
        var nodeId = await CreateObjectTypeAsync("ConformanceUnitsTestClear", "Doomed");
        try
        {
            (await UpdateNodeAsync(nodeId, new { category = Array.Empty<string>() })).EnsureSuccessStatusCode();
            Assert.Empty(Units(await GetNodeAsync(nodeId)));
        }
        finally
        {
            await DeleteNodeAsync(nodeId);
        }
    }

    [Fact]
    public async Task Update_WithoutTheFieldLeavesThemAlone()
    {
        // The Attributes JSON is rewritten from scratch on every node write, so an edit to
        // an unrelated field is exactly where units imported from a nodeset would vanish.
        var nodeId = await CreateObjectTypeAsync("ConformanceUnitsTestUntouched", "Kept");
        try
        {
            (await UpdateNodeAsync(nodeId, new { description = "Edited elsewhere" })).EnsureSuccessStatusCode();

            var node = await GetNodeAsync(nodeId);
            Assert.Equal(new[] { "Kept" }, Units(node));
            Assert.Equal("Edited elsewhere", node.GetProperty("description").GetProperty("text").GetString());
        }
        finally
        {
            await DeleteNodeAsync(nodeId);
        }
    }

    [Fact]
    public async Task BlankUnitsAreDropped()
    {
        // The client edits these as free text, one unit per line, so trailing newlines and
        // stray indentation arrive as entries that are not units.
        var nodeId = await CreateObjectTypeAsync("ConformanceUnitsTestBlanks", "  Padded  ", "", "   ");
        try
        {
            Assert.Equal(new[] { "Padded" }, Units(await GetNodeAsync(nodeId)));

            // Nothing but blanks means no units at all, not a list of empty strings —
            // the XML has no way to express one.
            (await UpdateNodeAsync(nodeId, new { category = new[] { "", " " } })).EnsureSuccessStatusCode();
            Assert.Empty(Units(await GetNodeAsync(nodeId)));
        }
        finally
        {
            await DeleteNodeAsync(nodeId);
        }
    }

    [Fact]
    public async Task TopLevelObject_CarriesUnitsAndReportsNoParent()
    {
        // The editor offers the field on an Object only when it has no structural parent,
        // and it reads that from the node itself — so GET has to report the parent's
        // absence, not just omit the property for every node.
        var request = WithServer(HttpMethod.Post, "/api/opcua/v1/nodes");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "Object",
            browseName = "ConformanceUnitsTestTopLevel",
            displayName = "ConformanceUnitsTestTopLevel",
            typeDefinitionId = BaseObjectType,
            category = new[] { "Top Level Unit" },
        });
        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        var nodeId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        try
        {
            var node = await GetNodeAsync(nodeId);
            Assert.Equal(new[] { "Top Level Unit" }, Units(node));
            Assert.False(HasParent(node));
        }
        finally
        {
            await DeleteNodeAsync(nodeId);
        }
    }

    [Fact]
    public async Task ChildNode_ReportsItsParent()
    {
        // The other half of the rule above: a child is covered by its owner's units, and
        // the editor hides the field for it — which only works if the parent is reported.
        var parentId = await CreateObjectTypeAsync("ConformanceUnitsTestParent");
        try
        {
            var request = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(parentId)}/children");
            request.Content = JsonContent.Create(new
            {
                modelUri = ApiFixture.TestModelUri,
                nodeClass = "Object",
                browseName = "ConformanceUnitsTestChild",
                displayName = "ConformanceUnitsTestChild",
                typeDefinitionId = BaseObjectType,
                referenceTypeId = "i=47", // HasComponent
            });
            var response = await _client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode,
                $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
            var childId = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("nodeId").GetString()!;

            Assert.True(HasParent(await GetNodeAsync(childId)));
        }
        finally
        {
            // Deleting the parent takes the child with it.
            await DeleteNodeAsync(parentId);
        }
    }
}
