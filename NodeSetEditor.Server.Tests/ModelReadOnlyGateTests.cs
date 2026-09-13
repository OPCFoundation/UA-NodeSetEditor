using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the editability gate on node-graph writes: model rows are shared
/// across workspaces, so the API must reject node create/update/delete on any
/// model whose workspace link is not a private, checked-out working copy
/// (IsPrivate &amp;&amp; IsEditable) — the condition the UI shows as IsReadOnly.
/// Regression test for a gap where a direct API call could bypass the
/// checkout lifecycle and edit a locked or shared model in place.
/// </summary>
[Collection("Api")]
public class ModelReadOnlyGateTests : UaRestTestBase
{
    private const string GateModelUri = "http://test.example.org/UA/ReadOnlyGate/";
    private const string ObjectsFolder = "i=85";

    public ModelReadOnlyGateTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NodeWrites_RequireCheckedOutWorkingCopy()
    {
        await DeleteGateModelLinks();
        try
        {
            // A freshly created namespace is an editable working copy: writes pass.
            var modelId = await CreateGateModel();
            var nodeId = await TryCreateChild("EditableNode");
            Assert.True(nodeId != null, "Create on a fresh (editable) model should succeed");

            // Check in with "keep": the link stays private but is no longer editable.
            await Checkin(modelId, "keep");

            // All node-graph writes must now be rejected with 403 BadNotWritable.
            var createResp = await SendCreateChild("BlockedNode");
            Assert.Equal(HttpStatusCode.Forbidden, createResp.StatusCode);
            Assert.Contains("BadNotWritable", await createResp.Content.ReadAsStringAsync());

            var updateReq = WithServer(HttpMethod.Put, $"/api/opcua/v1/nodes/{Slug(nodeId!)}");
            updateReq.Content = JsonContent.Create(new { displayName = "Blocked" });
            var updateResp = await Client.SendAsync(updateReq);
            Assert.Equal(HttpStatusCode.Forbidden, updateResp.StatusCode);

            var deleteResp = await Client.SendAsync(
                WithServer(HttpMethod.Delete, $"/api/opcua/v1/nodes/{Slug(nodeId!)}"));
            Assert.Equal(HttpStatusCode.Forbidden, deleteResp.StatusCode);

            // The rejected update must not linger in the cached address space.
            var node = await GetNode(nodeId!);
            Assert.NotEqual("Blocked", node.GetProperty("displayName").GetProperty("text").GetString());

            // Checkout mints a new editable working copy: writes pass again.
            var checkoutResp = await Client.SendAsync(WithServer(HttpMethod.Post,
                $"/api/opcua/v1/namespaces/info/{modelId}/checkout"));
            checkoutResp.EnsureSuccessStatusCode();

            var afterCheckout = await TryCreateChild("EditableAgainNode");
            Assert.True(afterCheckout != null, "Create after checkout should succeed");
        }
        finally
        {
            await DeleteGateModelLinks();
        }
    }

    private async Task<string> CreateGateModel()
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        req.Content = JsonContent.Create(new
        {
            uri = GateModelUri,
            name = "ReadOnlyGateModel",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private async Task Checkin(string modelId, string action)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin");
        req.Content = JsonContent.Create(new { action });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendCreateChild(string browseName)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/nodes/{Slug(ObjectsFolder)}/children");
        req.Content = JsonContent.Create(new
        {
            modelUri = GateModelUri,
            nodeClass = "Object",
            browseName,
            displayName = browseName,
            referenceTypeId = "i=47",
            typeDefinitionId = "i=58",
        });
        return await Client.SendAsync(req);
    }

    private async Task<string?> TryCreateChild(string browseName)
    {
        var resp = await SendCreateChild(browseName);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("nodeId").GetString();
    }

    /// <summary>
    /// Remove every workspace link for the gate model URI. A checkout leaves two
    /// links (backup + working copy) and the list collapses them per URI, so
    /// delete-and-relist until the URI no longer appears.
    /// </summary>
    private async Task DeleteGateModelLinks()
    {
        for (var i = 0; i < 5; i++)
        {
            var listResp = await Client.SendAsync(
                WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
            listResp.EnsureSuccessStatusCode();
            var body = await listResp.Content.ReadFromJsonAsync<JsonElement>();

            var entry = body.GetProperty("results").EnumerateArray()
                .FirstOrDefault(ns => ns.TryGetProperty("uri", out var uri)
                    && string.Equals(uri.GetString(), GateModelUri, StringComparison.OrdinalIgnoreCase));
            if (entry.ValueKind == JsonValueKind.Undefined) return;

            var id = entry.GetProperty("id").GetString();
            await Client.SendAsync(
                WithServer(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{id}"));
        }
    }
}
