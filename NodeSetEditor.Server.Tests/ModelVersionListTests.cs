using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The versions dialog lists every stored version of a namespace URI the workspace can see and
/// lets the user clean up private ones. The delete rule is the interesting part: the version the
/// workspace is using, published/shared versions, and anything another workspace references all
/// have to stay.
/// </summary>
[Collection("Api")]
public class ModelVersionListTests : UaRestTestBase
{
    private const string VersionsModelUri = "http://test.example.org/UA/VersionList/";

    public ModelVersionListTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Versions_ListTheVersionInUseAndTheRetainedBackup()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();
            await CheckinAsync(modelId, "keep");

            // One version so far: the one the workspace is using.
            var single = await GetVersionsAsync(modelId);
            var only = Assert.Single(single);
            Assert.True(only.GetProperty("isCurrent").GetBoolean());
            Assert.False(only.GetProperty("canDelete").GetBoolean());
            Assert.False(string.IsNullOrEmpty(only.GetProperty("origin").GetString()));

            // Checkout mints a working copy and retains the previous version as the backup
            // a Discard would restore — so now there are two.
            modelId = await CheckoutAsync(modelId);
            var versions = await GetVersionsAsync(modelId);
            Assert.Equal(2, versions.Count);

            var working = versions.Single(v => v.GetProperty("isCurrent").GetBoolean());
            Assert.Equal("1.0.1-alpha", working.GetProperty("version").GetString());
            Assert.True(working.GetProperty("isEditable").GetBoolean());
            // A checkout clone is editor content whatever the source was.
            Assert.Equal("Authored", working.GetProperty("origin").GetString());
            Assert.False(working.GetProperty("canDelete").GetBoolean());

            var backup = versions.Single(v => !v.GetProperty("isCurrent").GetBoolean());
            Assert.Equal("1.0.0", backup.GetProperty("version").GetString());
            Assert.True(backup.GetProperty("isPrivate").GetBoolean());
            Assert.False(backup.GetProperty("isPublished").GetBoolean());
            Assert.Equal(0, backup.GetProperty("otherWorkspaceCount").GetInt32());
            // Private, unreferenced, not in use — the one case that may go.
            Assert.True(backup.GetProperty("canDelete").GetBoolean());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task DeleteVersion_RemovesARetainedBackup()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();
            await CheckinAsync(modelId, "keep");
            modelId = await CheckoutAsync(modelId);

            var backupId = (await GetVersionsAsync(modelId))
                .Single(v => !v.GetProperty("isCurrent").GetBoolean())
                .GetProperty("id").GetString()!;

            var resp = await Client.SendAsync(WithServer(HttpMethod.Delete,
                $"/api/opcua/v1/namespaces/info/{modelId}/versions/{backupId}"));
            Assert.True(resp.IsSuccessStatusCode,
                $"delete failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");

            // The endpoint returns what is left, and a fresh read agrees.
            var returned = (await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
            Assert.Single(returned);
            var remaining = Assert.Single(await GetVersionsAsync(modelId));
            Assert.Equal("1.0.1-alpha", remaining.GetProperty("version").GetString());

            // The working copy itself is untouched and still usable.
            Assert.True(remaining.GetProperty("isEditable").GetBoolean());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task DeleteVersion_RejectsTheVersionTheWorkspaceIsUsing()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();
            await CheckinAsync(modelId, "keep");
            modelId = await CheckoutAsync(modelId);

            var currentId = (await GetVersionsAsync(modelId))
                .Single(v => v.GetProperty("isCurrent").GetBoolean())
                .GetProperty("id").GetString()!;

            var resp = await Client.SendAsync(WithServer(HttpMethod.Delete,
                $"/api/opcua/v1/namespaces/info/{modelId}/versions/{currentId}"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            // Nothing was removed.
            Assert.Equal(2, (await GetVersionsAsync(modelId)).Count);
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task DeleteVersion_RejectsAVersionThatIsNotThere()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();

            var resp = await Client.SendAsync(WithServer(HttpMethod.Delete,
                $"/api/opcua/v1/namespaces/info/{modelId}/versions/{Guid.NewGuid()}"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task Versions_RejectAModelThatIsNotInTheWorkspace()
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get,
            $"/api/opcua/v1/namespaces/info/{Guid.NewGuid()}/versions"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<List<JsonElement>> GetVersionsAsync(string modelId)
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get,
            $"/api/opcua/v1/namespaces/info/{modelId}/versions"));
        Assert.True(resp.IsSuccessStatusCode,
            $"versions failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<string> CreateModel()
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        req.Content = JsonContent.Create(new
        {
            uri = VersionsModelUri,
            name = "VersionListModel",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder",
        });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"create failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CheckoutAsync(string modelId)
    {
        var resp = await Client.SendAsync(
            WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkout"));
        Assert.True(resp.IsSuccessStatusCode,
            $"checkout failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return await GetActiveModelIdAsync();
    }

    private async Task CheckinAsync(string modelId, string action)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin");
        req.Content = JsonContent.Create(new { action });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"check-in failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<string> GetActiveModelIdAsync()
    {
        var entry = await FindEntryAsync();
        Assert.NotEqual(JsonValueKind.Undefined, entry.ValueKind);
        return entry.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> FindEntryAsync()
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray()
            .FirstOrDefault(ns => ns.TryGetProperty("uri", out var uri)
                && string.Equals(uri.GetString(), VersionsModelUri, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Remove every workspace link for the test URI. A checkout leaves two links and the list
    /// collapses them per URI, so delete-and-relist until the URI no longer appears.
    /// </summary>
    private async Task DeleteModelLinks()
    {
        for (var i = 0; i < 5; i++)
        {
            var entry = await FindEntryAsync();
            if (entry.ValueKind == JsonValueKind.Undefined) return;

            var id = entry.GetProperty("id").GetString();
            await Client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{id}"));
        }
    }
}
