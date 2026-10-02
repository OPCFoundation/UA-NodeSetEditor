using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The node picker prefetches a shallow slice of the type hierarchy so opening the dialog does
/// not browse a very deep tree. That makes the response ambiguous unless it says so: a row with
/// no subtypes in the payload is either genuinely subtype-free, or was cut off at the requested
/// depth. <c>subtypesTruncated</c> is what distinguishes them, and the picker re-browses from a
/// truncated node when the user expands it.
/// </summary>
[Collection("Api")]
public class TypeTreePrefetchTests
{
    private const string BaseObjectType = "i=58";
    /// <summary>BaseEventType — a direct subtype of BaseObjectType that has BOTH instance
    /// declarations (EventId, EventType, SourceNode, …) and subtypes (AuditEventType, …).</summary>
    private const string BaseEventType = "i=2041";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public TypeTreePrefetchTests(ApiFixture fixture)
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

    private async Task<List<JsonElement>> SubtypesAsync(string nodeId, int depth)
    {
        var response = await _client.SendAsync(WithServer(
            $"/api/opcua/v1/types/object-types/{Slug(nodeId)}/subtypes?depth={depth}&count=10000"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    private static bool Flag(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string NodeId(JsonElement node) => node.GetProperty("nodeId").GetString()!;

    [Fact]
    public async Task ATypeWithBothChildrenAndSubtypesIsMarkedTruncatedAtTheLimit()
    {
        // The reported bug: for a type that has instance declarations AND subtypes, the picker
        // showed the children and silently omitted the subtypes. The row is expandable either
        // way, so without an explicit flag the client cannot tell it still owes a browse.
        var children = await _client.SendAsync(WithServer(
            $"/api/opcua/v1/nodes/{Slug(BaseEventType)}/children?includeSubtypes=false"));
        children.EnsureSuccessStatusCode();
        var childBody = await children.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(childBody.GetProperty("results").EnumerateArray());

        var directSubtypes = await SubtypesAsync(BaseObjectType, depth: 1);
        var baseEvent = directSubtypes.FirstOrDefault(n => NodeId(n) == BaseEventType);
        Assert.True(baseEvent.ValueKind == JsonValueKind.Object,
            "BaseEventType should be a direct subtype of BaseObjectType");

        Assert.True(Flag(baseEvent, "subtypesTruncated"),
            "a type cut off at the depth limit must say its subtypes are missing from the payload");
        Assert.False(Flag(baseEvent, "hasNoSubtypes"));
    }

    [Fact]
    public async Task TheTwoLeafFlagsAreMutuallyExclusiveAndCoverEveryRowAtTheLimit()
    {
        // At the limit every row is one or the other: no subtypes at all, or subtypes we didn't
        // fetch. Both present, or neither, would leave the client guessing again.
        var rows = await SubtypesAsync(BaseObjectType, depth: 1);
        Assert.NotEmpty(rows);

        var truncated = rows.Where(n => Flag(n, "subtypesTruncated")).ToList();
        var leaves = rows.Where(n => Flag(n, "hasNoSubtypes")).ToList();

        Assert.NotEmpty(truncated); // otherwise the test proves nothing
        Assert.NotEmpty(leaves);
        Assert.DoesNotContain(truncated, n => Flag(n, "hasNoSubtypes"));
        Assert.Equal(rows.Count, truncated.Count + leaves.Count);
    }

    /// <summary>
    /// The request the node picker actually makes: pruned to the active model's namespace, which
    /// is what keeps the dialog fast to open on a deep type tree.
    /// </summary>
    private async Task<List<JsonElement>> PrunedSubtypesAsync(string nodeId, string modelUri)
    {
        var response = await _client.SendAsync(WithServer(
            $"/api/opcua/v1/types/object-types/{Slug(nodeId)}/subtypes"
            + $"?depth=3&count=10000&includeSelf=true&modelUri={Uri.EscapeDataString(modelUri)}"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    /// <summary>
    /// An ObjectType in the test model derived from <paramref name="supertypeNodeId"/>, so the
    /// pruned tree has a branch to keep. Returns its NodeId.
    /// </summary>
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

    [Fact]
    public async Task PruningNeverClaimsATypeWithSubtypesIsTerminal()
    {
        // The reported bug. The picker prunes the subtype tree to the active model's namespace,
        // which drops Core subtypes that are still legal reference targets. Those rows used to be
        // reported as HasNoSubtypes, so the picker showed a type's instance declarations with its
        // subtypes silently gone and no way to reach them.
        var own = await CreateSubtypeAsync(BaseEventType, "PrefetchProbeEventType");
        try
        {
            var rows = await PrunedSubtypesAsync(BaseObjectType, ApiFixture.TestModelUri);
            Assert.NotEmpty(rows);

            var liars = new List<string>();
            foreach (var row in rows.Where(n => Flag(n, "hasNoSubtypes")))
            {
                // Cross-check each claimed leaf against the unpruned view of the same node.
                var real = await SubtypesAsync(NodeId(row), depth: 1);
                if (real.Count > 0) liars.Add(NodeId(row));
            }

            Assert.True(liars.Count == 0,
                "pruned rows claim to have no subtypes but do: " + string.Join(", ", liars.Take(5)));
        }
        finally
        {
            await DeleteNodeAsync(own);
        }
    }

    [Fact]
    public async Task PrunedRowsWithWithheldSubtypesAreMarkedTruncated()
    {
        // BaseEventType is the exact case from the report: it has instance declarations AND
        // several Core subtypes, of which the prune keeps only the branch leading to our own type.
        var own = await CreateSubtypeAsync(BaseEventType, "PrefetchProbeTruncated");
        try
        {
            var rows = await PrunedSubtypesAsync(BaseObjectType, ApiFixture.TestModelUri);

            var baseEvent = rows.FirstOrDefault(n => NodeId(n) == BaseEventType);
            Assert.True(baseEvent.ValueKind == JsonValueKind.Object,
                "BaseEventType should appear as scaffolding for our own subtype");

            Assert.True(Flag(baseEvent, "subtypesTruncated"),
                "a pruned row keeping fewer subtypes than it has must say so");
            Assert.False(Flag(baseEvent, "hasNoSubtypes"));

            // Our own type is the kept branch and really is terminal.
            var mine = rows.FirstOrDefault(n => NodeId(n) == own);
            Assert.True(mine.ValueKind == JsonValueKind.Object);
            Assert.True(Flag(mine, "hasNoSubtypes"));
            Assert.False(Flag(mine, "subtypesTruncated"));
        }
        finally
        {
            await DeleteNodeAsync(own);
        }
    }

    [Fact]
    public async Task RowsWhoseSubtypesAreInThePayloadAreNotMarkedTruncated()
    {
        // Above the limit the subtypes are present, so the flag must be absent — otherwise the
        // picker would re-browse nodes it already has and the optimisation buys nothing.
        var rows = await SubtypesAsync(BaseObjectType, depth: 3);
        Assert.NotEmpty(rows);

        var hasSubtypeRowsInPayload = rows
            .Select(n => n.TryGetProperty("superTypeId", out var s) ? s.GetString() : null)
            .Where(id => id != null)
            .ToHashSet(StringComparer.Ordinal);

        var wronglyTruncated = rows
            .Where(n => hasSubtypeRowsInPayload.Contains(NodeId(n)) && Flag(n, "subtypesTruncated"))
            .Select(NodeId)
            .ToList();

        Assert.True(wronglyTruncated.Count == 0,
            "rows whose subtypes are already in the response must not be marked truncated: "
            + string.Join(", ", wronglyTruncated.Take(5)));
    }
}
