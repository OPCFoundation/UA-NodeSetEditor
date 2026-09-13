using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodeSetEditor.Server.Controllers;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// An OptionSet is a DataType derived from UInteger whose fields are bit positions rather
/// than values. Unlike Structure/Union/Enumeration, nothing about the node itself says so —
/// the flag lives on the DataTypeDefinition — so the definition has to exist from creation,
/// survive the rebuild, and keep the flag when the fields are edited or all removed.
/// </summary>
[Collection("Api")]
public class OptionSetDataTypeTests
{
    /// <summary>UInt32 — a UInteger subtype, so a legal base for an OptionSet.</summary>
    private const string UInt32Type = "i=7";

    /// <summary>Int32 — a Number but not a UInteger, so it has no bits to index.</summary>
    private const string Int32Type = "i=6";

    private readonly HttpClient _client;
    private readonly string _workspaceUrn;

    public OptionSetDataTypeTests(ApiFixture fixture)
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

    /// <summary>The node's computed DataTypeForm, or null when it has none (omitted from the JSON).</summary>
    private static string? Form(JsonElement node) =>
        node.TryGetProperty("dataTypeForm", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;

    private async Task<JsonElement> GetDefinitionAsync(string nodeId)
    {
        var response = await _client.SendAsync(
            WithServer(HttpMethod.Get, $"/api/opcua/v1/types/data-types/{Slug(nodeId)}/definition"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Own (non-inherited) fields as name → bit position.</summary>
    private static Dictionary<string, int?> OwnFields(JsonElement definition)
    {
        var fields = new Dictionary<string, int?>();
        if (!definition.TryGetProperty("fields", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return fields;

        foreach (var f in arr.EnumerateArray())
        {
            if (f.TryGetProperty("isInherited", out var inh) && inh.ValueKind == JsonValueKind.True)
                continue;
            var value = f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : (int?)null;
            fields[f.GetProperty("name").GetString()!] = value;
        }
        return fields;
    }

    private async Task<HttpResponseMessage> CreateDataTypeAsync(
        string browseName, bool? isOptionSet = null, string superType = UInt32Type)
    {
        var request = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(superType)}/children");
        request.Content = JsonContent.Create(new
        {
            modelUri = ApiFixture.TestModelUri,
            nodeClass = "DataType",
            browseName,
            displayName = browseName,
            referenceTypeId = "i=45", // HasSubtype
            isOptionSet,
        });
        return await _client.SendAsync(request);
    }

    /// <summary>Creates an OptionSet and returns its NodeId, failing the test if the create is rejected.</summary>
    private async Task<string> CreateOptionSetAsync(string browseName)
    {
        var response = await CreateDataTypeAsync(browseName, isOptionSet: true);
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;
    }

    /// <summary>Replaces the own fields with the given (name, bit) pairs.</summary>
    private async Task<HttpResponseMessage> PutFieldsAsync(string nodeId, params (string Name, int Bit)[] bits)
    {
        var request = WithServer(HttpMethod.Put, $"/api/opcua/v1/types/data-types/{Slug(nodeId)}/definition");
        request.Content = JsonContent.Create(new
        {
            fields = bits.Select(b => new { name = b.Name, value = b.Bit }).ToArray(),
        });
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UpdateNodeAsync(string nodeId, object body)
    {
        var request = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId)}");
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task CreateOptionSet_ReportsOptionSetForm_BeforeAnyFieldExists()
    {
        // The whole point: the form is what gates the editor's Fields tab, so a brand-new
        // OptionSet has to report one before it has a single bit, or no field can be added.
        var nodeId = await CreateOptionSetAsync("OptionSetTestEmpty");

        var created = await GetNodeAsync(nodeId);
        Assert.Equal("OptionSet", Form(created));

        var definition = await GetDefinitionAsync(nodeId);
        Assert.True(definition.GetProperty("isOptionSet").GetBoolean());
        Assert.Empty(OwnFields(definition));
    }

    [Fact]
    public async Task CreateDataType_WithoutTheFlag_HasNoForm()
    {
        // A plain UInt32 subtype is just a number: no bits, no Fields tab.
        var response = await CreateDataTypeAsync("OptionSetTestPlainUInt");
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        var nodeId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        Assert.Null(Form(await GetNodeAsync(nodeId)));
    }

    [Fact]
    public async Task CreateOptionSet_RejectsNonUIntegerSuperType()
    {
        // Int32 is a Number, but the bits of an OptionSet index an *unsigned* integer.
        var response = await CreateDataTypeAsync(
            "OptionSetTestSignedBase", isOptionSet: true, superType: Int32Type);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OptionSet_AcceptsBitFields_AndSurvivesRebuild()
    {
        var nodeId = await CreateOptionSetAsync("OptionSetTestBits");

        var put = await PutFieldsAsync(nodeId, ("Read", 0), ("Write", 1), ("Delete", 7));
        Assert.True(put.IsSuccessStatusCode,
            $"field save failed ({put.StatusCode}): {await put.Content.ReadAsStringAsync()}");

        // Re-read: the address space is rebuilt from the DB rows, so this is the step
        // that catches a definition the changeset failed to mirror.
        var reread = await GetDefinitionAsync(nodeId);
        var fields = OwnFields(reread);
        Assert.Equal(3, fields.Count);
        Assert.Equal(0, fields["Read"]);
        Assert.Equal(1, fields["Write"]);
        Assert.Equal(7, fields["Delete"]);

        Assert.Equal("OptionSet", Form(await GetNodeAsync(nodeId)));
    }

    [Fact]
    public async Task OptionSet_RejectsBitPositionsOutsideTheWidestUInteger()
    {
        var nodeId = await CreateOptionSetAsync("OptionSetTestBadBits");
        await PutFieldsAsync(nodeId, ("Valid", 3));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutFieldsAsync(nodeId, ("Valid", 3), ("Negative", -1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutFieldsAsync(nodeId, ("Valid", 3), ("TooWide", 64))).StatusCode);

        // The rejected saves left the existing fields alone.
        var fields = OwnFields(await GetDefinitionAsync(nodeId));
        Assert.Single(fields);
        Assert.Equal(3, fields["Valid"]);
    }

    [Fact]
    public async Task OptionSet_StaysAnOptionSet_WhenEveryFieldIsRemoved()
    {
        // Emptying the field list must not silently demote the type back to a plain
        // UInteger subtype — the flag lives on the definition, which is kept.
        var nodeId = await CreateOptionSetAsync("OptionSetTestEmptied");
        await PutFieldsAsync(nodeId, ("OnlyBit", 0));
        Assert.Single(OwnFields(await GetDefinitionAsync(nodeId)));

        var cleared = await PutFieldsAsync(nodeId);
        Assert.True(cleared.IsSuccessStatusCode,
            $"clearing fields failed ({cleared.StatusCode}): {await cleared.Content.ReadAsStringAsync()}");

        Assert.Empty(OwnFields(await GetDefinitionAsync(nodeId)));
        Assert.Equal("OptionSet", Form(await GetNodeAsync(nodeId)));
    }

    [Fact]
    public async Task UpdateNode_RejectsTurningIsOptionSetOff()
    {
        var nodeId = await CreateOptionSetAsync("OptionSetTestImmutableOff");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await UpdateNodeAsync(nodeId, new { isOptionSet = false })).StatusCode);

        // Echoing the value it already has is accepted, and an unrelated edit leaves it be.
        (await UpdateNodeAsync(nodeId, new { isOptionSet = true })).EnsureSuccessStatusCode();
        (await UpdateNodeAsync(nodeId, new { description = "Edited elsewhere" })).EnsureSuccessStatusCode();

        Assert.Equal("OptionSet", Form(await GetNodeAsync(nodeId)));
    }

    [Fact]
    public async Task UpdateNode_RejectsTurningIsOptionSetOn()
    {
        var response = await CreateDataTypeAsync("OptionSetTestImmutableOn");
        Assert.True(response.IsSuccessStatusCode,
            $"create failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        var nodeId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nodeId").GetString()!;

        Assert.Equal(HttpStatusCode.BadRequest,
            (await UpdateNodeAsync(nodeId, new { isOptionSet = true })).StatusCode);
        Assert.Null(Form(await GetNodeAsync(nodeId)));
    }
}
