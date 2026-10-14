using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The sidebar's model picker is a filter: with a model selected, the address-space tree must
/// show only the paths that lead to a node in that model. The server side of that is the
/// <c>modelUri</c> query parameter on the subtypes endpoint, which returns the HasSubtype
/// hierarchy pruned to those branches — every row is either a node in the model, or an ancestor
/// kept as scaffolding to reach one. Nothing else may appear, or the filter shows types the user
/// filtered out.
/// </summary>
[Collection("Api")]
public class NamespaceFilterTests
{
    private const string BaseObjectType = "i=58";
    /// <summary>BaseEventType — deep enough that its branch needs real scaffolding, and it has
    /// Core subtypes of its own that the prune must drop.</summary>
    private const string BaseEventType = "i=2041";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public NamespaceFilterTests(ApiFixture fixture)
    {
        _client = fixture.Client;
        _workspaceUrn = fixture.WorkspaceUrn!;
    }

    private static string Slug(string nodeId) => NodeIdSlugFilter.EncodeBase64Url(nodeId);

    private HttpRequestMessage WithServer(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
        return request;
    }

    private async Task<List<JsonElement>> SubtypesAsync(string nodeId, string? modelUri)
    {
        var url = $"/api/opcua/v1/types/object-types/{Slug(nodeId)}/subtypes?depth=99&count=10000&includeSelf=true";
        if (modelUri != null) url += $"&modelUri={Uri.EscapeDataString(modelUri)}";
        var response = await _client.SendAsync(WithServer(url));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    private static string NodeId(JsonElement node) => node.GetProperty("nodeId").GetString()!;

    private static string? SuperTypeId(JsonElement node) =>
        node.TryGetProperty("superTypeId", out var s) ? s.GetString() : null;

    private async Task<string> CreateSubtypeAsync(string supertypeNodeId, string browseName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/opcua/v1/nodes/{Slug(supertypeNodeId)}/children");
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
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

    private async Task DeleteNodeAsync(string nodeId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
        await _client.SendAsync(request);
    }

    private static bool IsInModel(string nodeId, string modelUri) =>
        nodeId.StartsWith($"nsu={modelUri};", StringComparison.Ordinal);

    [Fact]
    public async Task EveryPrunedRowIsEitherInTheModelOrScaffoldingForOne()
    {
        var own = await CreateSubtypeAsync(BaseEventType, "NamespaceFilterProbeType");
        try
        {
            var rows = await SubtypesAsync(BaseObjectType, ApiFixture.TestModelUri);
            Assert.NotEmpty(rows);

            // A row earns its place by being in the model, or by being on the supertype chain of
            // a row that is — which, since the chains are emitted whole, means some other row
            // names it as its supertype.
            var isParentOfSomeRow = rows
                .Select(SuperTypeId)
                .Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);

            var strays = rows
                .Select(NodeId)
                .Where(id => !IsInModel(id, ApiFixture.TestModelUri)
                    && !isParentOfSomeRow.Contains(id)
                    && id != BaseObjectType)
                .ToList();

            Assert.True(strays.Count == 0,
                "pruned response contains rows that are neither in the model nor scaffolding: "
                + string.Join(", ", strays.Take(10)));
        }
        finally
        {
            await DeleteNodeAsync(own);
        }
    }

    [Fact]
    public async Task PruningDropsCoreTypesOutsideTheKeptBranches()
    {
        // The prune has to actually prune: the unfiltered tree is the whole Core type universe,
        // the filtered one is a single thin branch down to our own type. Equal sizes would mean
        // modelUri was ignored — the tree would look unfiltered however the client renders it.
        var own = await CreateSubtypeAsync(BaseEventType, "NamespaceFilterProbeSize");
        try
        {
            var all = await SubtypesAsync(BaseObjectType, modelUri: null);
            var pruned = await SubtypesAsync(BaseObjectType, ApiFixture.TestModelUri);

            Assert.True(pruned.Count < all.Count,
                $"prune kept {pruned.Count} of {all.Count} rows — modelUri had no effect");

            // AuditEventType is a Core subtype of BaseEventType with no node of ours beneath it,
            // so it is exactly what the filter is supposed to remove, even though its parent
            // BaseEventType survives as scaffolding.
            var prunedIds = pruned.Select(NodeId).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(BaseEventType, prunedIds);
            Assert.Contains(own, prunedIds);
            Assert.DoesNotContain("i=2052", prunedIds); // AuditEventType
        }
        finally
        {
            await DeleteNodeAsync(own);
        }
    }
}
