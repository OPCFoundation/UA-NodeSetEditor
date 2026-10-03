using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Model metadata on a SHARED row is not the linking workspace's to change.
///
/// A model row is shared across every workspace that links it, so renaming or re-describing
/// one changes it for all of them. Licence, copyright and profile group were gated on that
/// basis from the start; Name, Description and Version were not, and the reserved-namespace
/// rule only covered http://opcfoundation.org/ URIs — so a published or Cloud Library model
/// under any other URI could be renamed by a non-admin through a direct API call, even though
/// the UI renders the dialog read-only. These tests pin the gate for all of them.
///
/// Editing the same fields on the workspace's OWN private copy stays unrestricted: that is the
/// positive control, and without it a blanket block would pass the rejection tests unnoticed.
/// </summary>
[Collection("Api")]
public class SharedModelMetadataGuardTests : UaRestTestBase
{
    public SharedModelMetadataGuardTests(ApiFixture fixture) : base(fixture) { }

    private const string OwnerId = "shared-meta-owner", OwnerEmail = "shared-meta-owner@test.net";
    private const string OtherId = "shared-meta-other", OtherEmail = "shared-meta-other@test.net";
    private const string OwnerWsName = "SharedMetaOwnerWorkspace";
    private const string OtherWsName = "SharedMetaOtherWorkspace";
    private const string SharedUri = "http://test.example.org/UA/SharedMeta/";
    private const string PrivateUri = "http://test.example.org/UA/SharedMetaPrivate/";

    [Fact]
    public async Task ANonAdmin_CannotChangeMetadataOnASharedModel()
    {
        string? ownerUrn = null, otherUrn = null;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            await FindAndDeleteWorkspace(OtherId, OtherEmail, OtherWsName);
            await PurgeModelRowsAsync(SharedUri, PrivateUri);

            // One user publishes a model, making it a shared catalog row.
            ownerUrn = await CreateWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            var sharedId = await CreateModel(OwnerId, OwnerEmail, ownerUrn, SharedUri, "SharedMeta");
            await Publish(OwnerId, OwnerEmail, ownerUrn, sharedId);

            // A second user links it. Linking always produces a SHARED link, so this model is
            // not theirs to edit regardless of what they ask for.
            otherUrn = await CreateWorkspace(OtherId, OtherEmail, OtherWsName);
            var link = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{sharedId}/link", OtherId, OtherEmail);
            link.Headers.Add("OpcUa-Server", otherUrn);
            link.Content = JsonContent.Create(new { isPrivate = false });
            var linkResp = await Client.SendAsync(link);
            Assert.True(linkResp.IsSuccessStatusCode,
                $"Linking a published model should succeed: {await linkResp.Content.ReadAsStringAsync()}");

            // Each field, separately, so a single over-broad guard cannot hide a gap.
            foreach (var body in new object[]
            {
                new { name = "Renamed By Someone Else" },
                new { description = "Re-described by someone else" },
                new { version = "9.9.9" },
                new { license = "MIT", copyrightHolder = "Someone Else" },
            })
            {
                var req = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{sharedId}", OtherId, OtherEmail);
                req.Headers.Add("OpcUa-Server", otherUrn);
                req.Content = JsonContent.Create(body);
                var resp = await Client.SendAsync(req);
                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            }

            // Nothing took: the row still carries the publisher's metadata.
            var name = await NameAsync(SharedUri);
            Assert.Equal("SharedMeta", name);
        }
        finally
        {
            if (otherUrn != null) await DeleteWorkspace(OtherId, OtherEmail, otherUrn);
            if (ownerUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, ownerUrn);
            await PurgeModelRowsAsync(SharedUri, PrivateUri);
        }
    }

    [Fact]
    public async Task TheOwner_CanStillChangeMetadataOnTheirPrivateModel()
    {
        string? ownerUrn = null;
        try
        {
            await FindAndDeleteWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            await PurgeModelRowsAsync(PrivateUri);

            ownerUrn = await CreateWorkspace(OwnerId, OwnerEmail, OwnerWsName);
            var privateId = await CreateModel(OwnerId, OwnerEmail, ownerUrn, PrivateUri, "MinePrivate");

            var req = AsUser(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{privateId}", OwnerId, OwnerEmail);
            req.Headers.Add("OpcUa-Server", ownerUrn);
            req.Content = JsonContent.Create(new { name = "Mine Renamed", description = "Mine, re-described" });
            var resp = await Client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode,
                $"The owner should be able to edit their own private model: {await resp.Content.ReadAsStringAsync()}");

            Assert.Equal("Mine Renamed", await NameAsync(PrivateUri));
        }
        finally
        {
            if (ownerUrn != null) await DeleteWorkspace(OwnerId, OwnerEmail, ownerUrn);
            await PurgeModelRowsAsync(PrivateUri);
        }
    }

    private async Task<string?> NameAsync(string uri)
    {
        using var scope = Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        return await db.Models.Where(m => m.Uri == uri).Select(m => m.Name).FirstOrDefaultAsync();
    }

    private async Task<string> CreateWorkspace(string userId, string email, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/servers", userId, email);
        req.Content = JsonContent.Create(new { applicationName = name, description = "shared-metadata guard test" });
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applicationUri").GetString()!;
    }

    private async Task<string> CreateModel(string userId, string email, string wsUrn, string uri, string name)
    {
        var req = AsUser(HttpMethod.Post, "/api/opcua/v1/namespaces/info", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new
        {
            uri, name,
            version = "1.0.0",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder"
        });
        var resp = await Client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"CreateModel failed ({resp.StatusCode}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    private async Task Publish(string userId, string email, string wsUrn, string modelId)
    {
        var req = AsUser(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin", userId, email);
        req.Headers.Add("OpcUa-Server", wsUrn);
        req.Content = JsonContent.Create(new { action = "publish", description = "Published for guard test" });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"Publish failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task DeleteWorkspace(string userId, string email, string wsUrn)
    {
        var req = AsUser(HttpMethod.Delete, $"/api/opcua/v1/servers/{Uri.EscapeDataString(wsUrn)}", userId, email);
        await Client.SendAsync(req);
    }

    private async Task FindAndDeleteWorkspace(string userId, string email, string wsName)
    {
        var req = AsUser(HttpMethod.Get, "/api/opcua/v1/discovery", userId, email);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");

        var existing = results.EnumerateArray()
            .FirstOrDefault(ws => ws.GetProperty("applicationName").GetProperty("text").GetString() == wsName);
        if (existing.ValueKind == JsonValueKind.Undefined) return;

        await DeleteWorkspace(userId, email, existing.GetProperty("applicationUri").GetString()!);
    }
}
