using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// A ReferenceType carries two attributes no other node class has: Symmetric and
/// InverseName (Part 3, 5.3.3). Both have to survive the round trip through the DB
/// and the address-space rebuild, and the spec rule that a symmetric type has no
/// inverse name has to hold on both write paths.
/// </summary>
[Collection("Api")]
public class ReferenceTypeAttributeTests
{
    /// <summary>NonHierarchicalReferences — the usual root for a user-defined ReferenceType.</summary>
    private const string NonHierarchicalReferences = "i=32";

    /// <summary>HierarchicalReferences — directional, so never symmetric and always inverse-named.</summary>
    private const string HierarchicalReferences = "i=33";

    /// <summary>HasComponent: asymmetric, InverseName "ComponentOf".</summary>
    private const string HasComponent = "i=47";

    /// <summary>AssociatedWith: one of the core symmetric ReferenceTypes.</summary>
    private const string AssociatedWith = "i=24137";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public ReferenceTypeAttributeTests(ApiFixture fixture)
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

    private static bool Symmetric(JsonElement node) =>
        node.TryGetProperty("symmetric", out var s) && s.ValueKind == JsonValueKind.True;

    private static string? InverseName(JsonElement node) =>
        node.TryGetProperty("inverseName", out var inv) && inv.ValueKind == JsonValueKind.Object
            ? inv.GetProperty("text").GetString()
            : null;

    private async Task<HttpResponseMessage> CreateReferenceTypeAsync(
        string browseName, bool? symmetric = null, string? inverseName = null,
        string superType = NonHierarchicalReferences)
    {
        var request = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(superType)}/children");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "ReferenceType",
            browseName,
            displayName = browseName,
            referenceTypeId = "i=45", // HasSubtype
            symmetric,
            inverseName,
        });
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UpdateAsync(string nodeId, object body)
    {
        var request = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task GetNode_ReportsSymmetricAndInverseName_ForCoreReferenceTypes()
    {
        // Read the two attributes off types whose values the spec fixes, so this
        // also covers the import path rather than just what we wrote ourselves.
        var hasComponent = await GetNodeAsync(HasComponent);
        Assert.False(Symmetric(hasComponent));
        Assert.Equal("ComponentOf", InverseName(hasComponent));

        var associatedWith = await GetNodeAsync(AssociatedWith);
        Assert.True(Symmetric(associatedWith));
        Assert.Null(InverseName(associatedWith));
    }

    [Fact]
    public async Task CreateReferenceType_PersistsInverseName_AndSurvivesRebuild()
    {
        var response = await CreateReferenceTypeAsync(
            "RefTypeTestAsymmetric", symmetric: false, inverseName: "IsTestedBy");
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var nodeId = created.GetProperty("nodeId").GetString()!;

        Assert.False(Symmetric(created));
        Assert.Equal("IsTestedBy", InverseName(created));

        // Re-read: the address space is rebuilt from the DB rows, so this is the
        // step that catches an attribute the changeset forgot to mirror.
        var reread = await GetNodeAsync(nodeId);
        Assert.False(Symmetric(reread));
        Assert.Equal("IsTestedBy", InverseName(reread));
    }

    [Fact]
    public async Task CreateSymmetricReferenceType_RoundTrips()
    {
        var response = await CreateReferenceTypeAsync("RefTypeTestSymmetric", symmetric: true);
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        var nodeId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        var reread = await GetNodeAsync(nodeId);
        Assert.True(Symmetric(reread));
        Assert.Null(InverseName(reread));
    }

    [Fact]
    public async Task CreateReferenceType_DefaultsToAsymmetric()
    {
        // Symmetric omitted means false — the XSD default. It used to come back
        // as true, because the in-memory model read a missing value as symmetric.
        var response = await CreateReferenceTypeAsync("RefTypeTestDefault");
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        var nodeId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        Assert.False(Symmetric(await GetNodeAsync(nodeId)));
    }

    [Fact]
    public async Task CreateReferenceType_RejectsInverseNameOnSymmetric()
    {
        var response = await CreateReferenceTypeAsync(
            "RefTypeTestRejected", symmetric: true, inverseName: "NotAllowed");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReferenceType_EditsBothAttributes()
    {
        var create = await CreateReferenceTypeAsync(
            "RefTypeTestEditable", symmetric: false, inverseName: "OriginalInverse");
        Assert.True(create.IsSuccessStatusCode,
            $"create failed ({create.StatusCode}): {await create.Content.ReadAsStringAsync()}");
        var nodeId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        // Rename the inverse only.
        (await UpdateAsync(nodeId, new { inverseName = "RenamedInverse" })).EnsureSuccessStatusCode();
        Assert.Equal("RenamedInverse", InverseName(await GetNodeAsync(nodeId)));

        // Turning Symmetric on while an InverseName is still set is the state the
        // spec forbids; the client sends both in one call, so accept that pairing.
        var conflicting = await UpdateAsync(nodeId, new { symmetric = true });
        Assert.Equal(HttpStatusCode.BadRequest, conflicting.StatusCode);

        (await UpdateAsync(nodeId, new { symmetric = true, inverseName = "" })).EnsureSuccessStatusCode();
        var symmetricNode = await GetNodeAsync(nodeId);
        Assert.True(Symmetric(symmetricNode));
        Assert.Null(InverseName(symmetricNode));

        // ...and back again.
        (await UpdateAsync(nodeId, new { symmetric = false, inverseName = "BackToAsymmetric" })).EnsureSuccessStatusCode();
        var asymmetricNode = await GetNodeAsync(nodeId);
        Assert.False(Symmetric(asymmetricNode));
        Assert.Equal("BackToAsymmetric", InverseName(asymmetricNode));
    }

    [Fact]
    public async Task CreateHierarchicalReferenceType_RequiresInverseName()
    {
        var missing = await CreateReferenceTypeAsync(
            "RefTypeTestHierNoInverse", superType: HierarchicalReferences);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var blank = await CreateReferenceTypeAsync(
            "RefTypeTestHierBlankInverse", inverseName: "   ", superType: HierarchicalReferences);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var ok = await CreateReferenceTypeAsync(
            "RefTypeTestHierarchical", inverseName: "IsPartOfTest", superType: HierarchicalReferences);
        Assert.True(ok.IsSuccessStatusCode,
            $"create failed ({ok.StatusCode}): {await ok.Content.ReadAsStringAsync()}");
        var nodeId = (await ok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        var reread = await GetNodeAsync(nodeId);
        Assert.False(Symmetric(reread));
        Assert.Equal("IsPartOfTest", InverseName(reread));
    }

    [Fact]
    public async Task CreateHierarchicalReferenceType_RejectsSymmetric()
    {
        var response = await CreateReferenceTypeAsync(
            "RefTypeTestHierSymmetric", symmetric: true, superType: HierarchicalReferences);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateHierarchicalReferenceType_KeepsInverseNameAndAsymmetry()
    {
        var create = await CreateReferenceTypeAsync(
            "RefTypeTestHierEditable", inverseName: "OriginalHierInverse", superType: HierarchicalReferences);
        Assert.True(create.IsSuccessStatusCode,
            $"create failed ({create.StatusCode}): {await create.Content.ReadAsStringAsync()}");
        var nodeId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        // Neither attribute can be edited into an invalid state...
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UpdateAsync(nodeId, new { inverseName = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UpdateAsync(nodeId, new { symmetric = true, inverseName = "" })).StatusCode);

        // ...and the failed attempts left the node as it was.
        var unchanged = await GetNodeAsync(nodeId);
        Assert.False(Symmetric(unchanged));
        Assert.Equal("OriginalHierInverse", InverseName(unchanged));

        // Renaming the inverse is still allowed.
        (await UpdateAsync(nodeId, new { inverseName = "RenamedHierInverse" })).EnsureSuccessStatusCode();
        Assert.Equal("RenamedHierInverse", InverseName(await GetNodeAsync(nodeId)));
    }

    [Fact]
    public async Task UpdateReferenceType_UnrelatedEditKeepsSymmetric()
    {
        var create = await CreateReferenceTypeAsync("RefTypeTestUntouched", symmetric: true);
        Assert.True(create.IsSuccessStatusCode,
            $"create failed ({create.StatusCode}): {await create.Content.ReadAsStringAsync()}");
        var nodeId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        // An edit that says nothing about Symmetric must not silently clear it.
        (await UpdateAsync(nodeId, new { description = "Edited elsewhere" })).EnsureSuccessStatusCode();
        Assert.True(Symmetric(await GetNodeAsync(nodeId)));
    }
}
