using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// A NodeId is normally allocated by the server as "nsu={model};i={max+1}", but published
/// nodesets routinely carry hand-authored string ids — Machinery's
/// "ns=1;s=NamespaceMetadata.ModelVersion", say. So a create request may name the id it
/// wants, and these cover the three things that has to get right: the id is used verbatim,
/// a clash is refused rather than silently overwriting the node that holds it (nothing
/// below the controller enforces uniqueness), and a malformed identifier is rejected.
///
/// The id is settable only at creation. There is deliberately no rename: the NodeId is
/// stored by value as the join key in node rows, reference rows, JSON attribute blobs and
/// the exported NodeSet alike, so the last test here pins that a BrowseName edit leaves it
/// alone.
/// </summary>
[Collection("Api")]
public class RequestedNodeIdTests : UaRestTestBase
{
    /// <summary>BaseObjectType — the usual root for a user-defined ObjectType.</summary>
    private const string BaseObjectType = "i=58";

    /// <summary>HasSubtype, which is what makes the new node a type rather than a child.</summary>
    private const string HasSubtype = "i=45";

    /// <summary>PropertyType — the TypeDefinition every Property carries.</summary>
    private const string PropertyType = "i=68";

    /// <summary>HasProperty.</summary>
    private const string HasProperty = "i=46";

    public RequestedNodeIdTests(ApiFixture fixture) : base(fixture) { }

    private static string Qualified(string identifier) =>
        $"nsu={ApiFixture.TestModelUri};{identifier}";

    private async Task<HttpResponseMessage> PostChildAsync(
        string parentNodeId, string browseName, string? requestedNodeId,
        string nodeClass = "Object", string referenceTypeId = "i=47", string? typeDefinitionId = null)
    {
        var request = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(parentNodeId)}/children");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            requestedNodeId,
            nodeClass,
            browseName,
            displayName = browseName,
            referenceTypeId,
            typeDefinitionId,
        });
        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task CreateChild_WithStringNodeId_UsesItVerbatim()
    {
        var parentId = await CreateChildNode(BaseObjectType, "ObjectType", "StringIdParentType",
            referenceTypeId: HasSubtype);

        var identifier = "s=NamespaceMetadata.ModelVersion";
        var response = await PostChildAsync(parentId, "ModelVersion", identifier,
            nodeClass: "Variable", referenceTypeId: HasProperty, typeDefinitionId: PropertyType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Create failed ({response.StatusCode}): {body}");

        var created = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(Qualified(identifier), created.GetProperty("nodeId").GetString());

        // And it survives the address-space rebuild, which is what the GET goes through.
        var reread = await GetNode(Qualified(identifier));
        Assert.Equal(Qualified(identifier), reread.GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task CreateChild_WithNumericNodeId_UsesItVerbatim()
    {
        var parentId = await CreateChildNode(BaseObjectType, "ObjectType", "NumericIdParentType",
            referenceTypeId: HasSubtype);

        // Well above anything the allocator would hand out, so it cannot collide by accident.
        var identifier = "i=4000123";
        var response = await PostChildAsync(parentId, "PinnedNumericChild", identifier);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Create failed ({response.StatusCode}): {body}");
        Assert.Equal(
            Qualified(identifier),
            JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task CreateChild_WithoutRequestedNodeId_StillAllocatesNumeric()
    {
        var parentId = await CreateChildNode(BaseObjectType, "ObjectType", "AutoIdParentType",
            referenceTypeId: HasSubtype);

        var response = await PostChildAsync(parentId, "AutoIdChild", requestedNodeId: null);
        response.EnsureSuccessStatusCode();

        var nodeId = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync()).GetProperty("nodeId").GetString();
        Assert.StartsWith($"nsu={ApiFixture.TestModelUri};i=", nodeId);
    }

    [Fact]
    public async Task CreateChild_WithDuplicateNodeId_IsRefused()
    {
        var parentId = await CreateChildNode(BaseObjectType, "ObjectType", "DuplicateIdParentType",
            referenceTypeId: HasSubtype);

        var identifier = "s=Duplicate.Target";
        var first = await PostChildAsync(parentId, "FirstChild", identifier);
        first.EnsureSuccessStatusCode();

        var second = await PostChildAsync(parentId, "SecondChild", identifier);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The node that already held the id is untouched — a clash must never overwrite,
        // which is exactly what AddressSpace's dictionary assignment would have done.
        var survivor = await GetNode(Qualified(identifier));
        Assert.Contains("FirstChild", survivor.GetProperty("browseName").GetString());
    }

    [Theory]
    [InlineData("x=1", "an unknown id type")]
    [InlineData("i=-1", "a negative numeric identifier")]
    [InlineData("i=4294967296", "a numeric identifier past UInt32")]
    [InlineData("i=notanumber", "a non-numeric numeric identifier")]
    [InlineData("s=", "an empty string identifier")]
    [InlineData("s=   ", "a blank string identifier")]
    [InlineData("s=Has;Semicolon", "a string identifier containing the namespace separator")]
    [InlineData("g=not-a-guid", "a malformed GUID identifier")]
    [InlineData("nsu=http://other.example.org/;s=Foo", "a fully qualified id naming another namespace")]
    public async Task CreateChild_WithMalformedNodeId_IsRejected(string identifier, string why)
    {
        // Rejected before anything is created, so this needs no parent of its own —
        // a new ObjectType under BaseObjectType is a well-formed request apart from the id.
        var response = await PostChildAsync(BaseObjectType, "MalformedIdType", identifier,
            nodeClass: "ObjectType", referenceTypeId: HasSubtype);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.IsSuccessStatusCode, $"Expected {why} to be rejected, got: {body}");
    }

    [Fact]
    public async Task CreateTopLevelNode_WithStringNodeId_UsesItVerbatim()
    {
        var identifier = "s=TopLevelStringIdObject";
        var request = WithServer(HttpMethod.Post, "/api/opcua/v1/nodes");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            requestedNodeId = identifier,
            nodeClass = "Object",
            browseName = "TopLevelStringIdObject",
            displayName = "TopLevelStringIdObject",
            typeDefinitionId = BaseObjectType,
        });

        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Create failed ({response.StatusCode}): {body}");
        Assert.Equal(
            Qualified(identifier),
            JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task UpdateBrowseName_LeavesStringNodeIdAlone()
    {
        var parentId = await CreateChildNode(BaseObjectType, "ObjectType", "RenameParentType",
            referenceTypeId: HasSubtype);

        var identifier = "s=Rename.Original";
        var createResponse = await PostChildAsync(parentId, "OriginalName", identifier);
        createResponse.EnsureSuccessStatusCode();

        var update = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(Qualified(identifier))}");
        update.Content = JsonContent.Create(new { browseName = "RenamedAfterwards" });
        var updateResponse = await Client.SendAsync(update);
        updateResponse.EnsureSuccessStatusCode();

        // The BrowseName moved; the id the author chose did not follow it. Renaming a NodeId
        // is not supported — an author who needs a different one exports, replaces and reimports.
        var reread = await GetNode(Qualified(identifier));
        Assert.Contains("RenamedAfterwards", reread.GetProperty("browseName").GetString());
        Assert.Equal(Qualified(identifier), reread.GetProperty("nodeId").GetString());
    }
}
