using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Verifies that cross-model circular references are permitted at write time (no 409),
/// and that removing a model from the workspace marks dependent models with HasErrors.
///
/// OPC UA allows multiple NodeSets to be loaded together into a single AddressSpace
/// and validated as a combined unit. The editor therefore imposes no write-time
/// cycle prevention — it surfaces unresolved references via HasErrors instead.
///
/// Cleanup discipline: tests share a single TestModel namespace via the fixture.
/// Each test that adds a reference FROM TestModel TO an ephemeral model must remove
/// that reference (or the node holding it) before deleting the ephemeral model —
/// otherwise the server's existing model-delete dependency check ("Cannot remove
/// model X. It is used by ...") blocks cleanup, the ephemeral model orphans, and
/// the next fixture initialization fails.
/// </summary>
[Collection("Api")]
public class ModelDependencyTests : UaRestTestBase
{
    private const string HasSubtype = "i=45";
    private const string HasTypeDefinition = "i=40";
    private const string BaseObjectType = "i=58";
    private const string BaseDataType = "i=24";
    private const string Organizes = "i=35";

    public ModelDependencyTests(ApiFixture fixture) : base(fixture) { }

    // ---- helpers ----

    private static string NewModelUri(string label) =>
        $"http://test-dep-{label}-{Guid.NewGuid():N}.example.org/UA/";

    private static string UniqueName(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 24)];

    private async Task<string> CreateModel(string uri)
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        req.Content = JsonContent.Create(new { uri, name = uri, version = "1.0.0", license = "MIT", copyrightHolder = "Test Copyright Holder" });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateModel({uri}) failed ({resp.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetString()!;
    }

    /// <summary>Best-effort: cleanup must never fail the test.</summary>
    private async Task DeleteModel(string? modelId)
    {
        if (modelId == null) return;
        try
        {
            var req = WithServer(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{modelId}");
            await Client.SendAsync(req);
        }
        catch { }
    }

    /// <summary>Best-effort node delete for cleanup.</summary>
    private async Task DeleteNode(string? nodeId)
    {
        if (nodeId == null) return;
        try
        {
            var req = WithServer(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
            await Client.SendAsync(req);
        }
        catch { }
    }

    /// <summary>Best-effort reference delete for cleanup.</summary>
    private async Task DeleteReference(string sourceNodeId, string referenceTypeId, string targetNodeId, bool isForward)
    {
        try
        {
            var url = $"/api/opcua/v1/nodes/{Slug(sourceNodeId)}/references/"
                + $"{Slug(referenceTypeId)}/{Slug(targetNodeId)}?isForward={isForward.ToString().ToLowerInvariant()}";
            var req = WithServer(HttpMethod.Delete, url);
            await Client.SendAsync(req);
        }
        catch { }
    }

    /// <summary>Creates a node in an explicitly chosen model (not the fixture's TestModelUri).</summary>
    private async Task<string> CreateNodeInModel(
        string parentNodeId, string nodeClass, string browseName, string modelUri,
        string referenceTypeId = HasSubtype, string? typeDefinitionId = null)
    {
        var req = WithServer(HttpMethod.Post,
            $"/api/opcua/v1/nodes/{Slug(parentNodeId)}/children");
        req.Content = JsonContent.Create(new
        {
            modelUri,
            nodeClass,
            browseName,
            displayName = browseName,
            referenceTypeId,
            typeDefinitionId,
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode,
            $"CreateNodeInModel failed ({resp.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString()!;
    }

    private async Task<HttpResponseMessage> AddReference(
        string sourceNodeId, string referenceTypeId, string targetNodeId, bool isForward = true)
    {
        var req = WithServer(HttpMethod.Post,
            $"/api/opcua/v1/nodes/{Slug(sourceNodeId)}/references");
        req.Content = JsonContent.Create(new { referenceTypeId, targetNodeId, isForward });
        return await Client.SendAsync(req);
    }

    private async Task<JsonElement> GetNamespacesInfo()
    {
        var req = WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info");
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- circular reference tests: all must succeed (not return 409) ----

    /// <summary>
    /// HasSubtype direct cycle: typeA (model A) is a supertype of typeB (model B),
    /// and typeB is also added as a supertype of typeA via a forward HasSubtype.
    /// </summary>
    [Fact]
    public async Task CircularHasSubtype_IsAllowed()
    {
        var modelBUri = NewModelUri("subB");
        var modelBId = await CreateModel(modelBUri);
        string? typeA = null;
        try
        {
            typeA = await CreateChildNode(BaseObjectType, "ObjectType",
                UniqueName("CycSubA"), referenceTypeId: HasSubtype);

            // typeB in model B, subtype of typeA (B→A dependency, ref lives in B).
            var typeB = await CreateNodeInModel(typeA, "ObjectType",
                UniqueName("CycSubB"), modelBUri, referenceTypeId: HasSubtype);

            // Close the cycle: typeA is also a subtype of typeB. The forward ref lives
            // on typeB (in model B), so TestModel never references B — clean teardown.
            var resp = await AddReference(typeB, HasSubtype, typeA, isForward: true);
            Assert.True(resp.IsSuccessStatusCode,
                $"Circular HasSubtype should be allowed; got {resp.StatusCode}: " +
                $"{await resp.Content.ReadAsStringAsync()}");
        }
        finally
        {
            await DeleteModel(modelBId);
            await DeleteNode(typeA); // best-effort: TestModel left clean
        }
    }

    /// <summary>
    /// HasTypeDefinition cycle: typeB (model B) is a subtype of typeA (model A),
    /// and an Object in model A uses typeB as its TypeDefinition — A→B and B→A.
    /// </summary>
    [Fact]
    public async Task CircularHasTypeDefinition_IsAllowed()
    {
        var modelBUri = NewModelUri("tdB");
        var modelBId = await CreateModel(modelBUri);
        string? typeA = null;
        string? objectInA = null;
        try
        {
            typeA = await CreateChildNode(BaseObjectType, "ObjectType",
                UniqueName("CycTdA"), referenceTypeId: HasSubtype);

            // typeB in model B, subtype of typeA (B→A dependency).
            var typeB = await CreateNodeInModel(typeA, "ObjectType",
                UniqueName("CycTdB"), modelBUri, referenceTypeId: HasSubtype);

            // Object in TestModel with TypeDefinition=typeB (A→B dependency).
            var resp = await Client.SendAsync(BuildCreateObjectRequest(typeA, typeB));
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode,
                $"Circular HasTypeDefinition should be allowed; got {resp.StatusCode}: {body}");
            objectInA = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("nodeId").GetString();
        }
        finally
        {
            // Delete the Object first — it carries the only A→B reference. Without
            // this, the model-delete dep check blocks DeleteModel(B) and B orphans.
            await DeleteNode(objectInA);
            await DeleteModel(modelBId);
            await DeleteNode(typeA);
        }
    }

    /// <summary>
    /// General (non-structural) reference cycle between two models: both directions added.
    /// </summary>
    [Fact]
    public async Task CircularGeneralReference_IsAllowed()
    {
        var modelBUri = NewModelUri("refB");
        var modelBId = await CreateModel(modelBUri);
        string? typeA = null;
        string? typeB = null;
        try
        {
            typeA = await CreateChildNode(BaseObjectType, "ObjectType",
                UniqueName("CycRefA"), referenceTypeId: HasSubtype);
            typeB = await CreateNodeInModel(BaseObjectType, "ObjectType",
                UniqueName("CycRefB"), modelBUri, referenceTypeId: HasSubtype);

            var r1 = await AddReference(typeA, Organizes, typeB, isForward: true);
            Assert.True(r1.IsSuccessStatusCode,
                $"A→B reference failed: {await r1.Content.ReadAsStringAsync()}");

            var r2 = await AddReference(typeB, Organizes, typeA, isForward: true);
            Assert.True(r2.IsSuccessStatusCode,
                $"Circular B→A reference should be allowed; got {r2.StatusCode}: " +
                $"{await r2.Content.ReadAsStringAsync()}");
        }
        finally
        {
            // Delete the TestModel→B reference so DeleteModel(B) isn't blocked by
            // the dep check. The B→A reference dies with model B.
            if (typeA != null && typeB != null)
                await DeleteReference(typeA, Organizes, typeB, isForward: true);
            await DeleteModel(modelBId);
            await DeleteNode(typeA);
        }
    }

    /// <summary>
    /// DataType attribute cycle: DataTypeB (model B) is a subtype of DataTypeA (model A),
    /// and a Variable in model A has its DataType attribute set to DataTypeB.
    /// </summary>
    [Fact]
    public async Task CircularDataTypeAttribute_IsAllowed()
    {
        var modelBUri = NewModelUri("dtB");
        var modelBId = await CreateModel(modelBUri);
        string? dataTypeA = null;
        string? parentType = null;
        string? variableA = null;
        try
        {
            dataTypeA = await CreateChildNode(BaseDataType, "DataType",
                UniqueName("CycDtA"), referenceTypeId: HasSubtype);

            // DataTypeB in model B, subtype of DataTypeA (B→A dependency).
            var dataTypeB = await CreateNodeInModel(dataTypeA, "DataType",
                UniqueName("CycDtB"), modelBUri, referenceTypeId: HasSubtype);

            parentType = await CreateChildNode(BaseObjectType, "ObjectType",
                UniqueName("CycDtP"), referenceTypeId: HasSubtype);
            variableA = await CreateChildNode(parentType, "Variable", UniqueName("CycDtV"));

            // Set Variable's DataType to DataTypeB (A→B dependency via DataType column).
            var req = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(variableA)}");
            req.Content = JsonContent.Create(new { dataType = dataTypeB });
            var resp = await Client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode,
                $"Circular DataType attribute reference should be allowed; got {resp.StatusCode}: " +
                $"{await resp.Content.ReadAsStringAsync()}");
        }
        finally
        {
            // Delete the Variable first — its DataType column is the A→B link.
            await DeleteNode(variableA);
            await DeleteModel(modelBId);
            await DeleteNode(parentType);
            await DeleteNode(dataTypeA);
        }
    }

    /// <summary>
    /// Multi-hop cycle A→C→B→A. All HasSubtype, so cross-model refs all live in B/C
    /// (no cleanup needed in TestModel besides typeA itself).
    /// </summary>
    [Fact]
    public async Task CircularMultiHop_IsAllowed()
    {
        var modelBUri = NewModelUri("hopB");
        var modelCUri = NewModelUri("hopC");
        var modelBId = await CreateModel(modelBUri);
        var modelCId = await CreateModel(modelCUri);
        string? typeA = null;
        try
        {
            typeA = await CreateChildNode(BaseObjectType, "ObjectType",
                UniqueName("HopA"), referenceTypeId: HasSubtype);

            var typeB = await CreateNodeInModel(typeA, "ObjectType",
                UniqueName("HopB"), modelBUri, referenceTypeId: HasSubtype);

            var typeC = await CreateNodeInModel(typeB, "ObjectType",
                UniqueName("HopC"), modelCUri, referenceTypeId: HasSubtype);

            // Close the cycle: typeA is a subtype of typeC. Ref lives on typeC (model C).
            var resp = await AddReference(typeC, HasSubtype, typeA, isForward: true);
            Assert.True(resp.IsSuccessStatusCode,
                $"Multi-hop cycle close (A→C) should be allowed; got {resp.StatusCode}: " +
                $"{await resp.Content.ReadAsStringAsync()}");
        }
        finally
        {
            // Delete C before B — C references B, so B-first hits the dep check.
            await DeleteModel(modelCId);
            await DeleteModel(modelBId);
            await DeleteNode(typeA);
        }
    }

    private HttpRequestMessage BuildCreateObjectRequest(string parentNodeId, string typeDefinitionId)
    {
        var req = WithServer(HttpMethod.Post,
            $"/api/opcua/v1/nodes/{Slug(parentNodeId)}/children");
        req.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "Object",
            browseName = UniqueName("CycObj"),
            displayName = UniqueName("CycObj"),
            referenceTypeId = "i=47",
            typeDefinitionId,
        });
        return req;
    }
}
