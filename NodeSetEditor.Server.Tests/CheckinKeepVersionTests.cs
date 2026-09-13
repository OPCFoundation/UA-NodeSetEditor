using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// "Keep changes" ends an editing session without publishing. The <c>-alpha</c> suffix means
/// "checked out for editing", so a kept model must not keep it, and the caller may name the
/// version it settles on instead.
/// </summary>
[Collection("Api")]
public class CheckinKeepVersionTests : UaRestTestBase
{
    private const string KeepModelUri = "http://test.example.org/UA/CheckinKeep/";

    public CheckinKeepVersionTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Keep_DropsTheWorkingCopySuffix()
    {
        await DeleteModelLinks();
        try
        {
            // A new namespace starts life as an editable 1.0.0-alpha working copy.
            var modelId = await CreateModel();
            Assert.Equal("1.0.0-alpha", await GetVersion());

            await CheckinAsync(modelId, "keep");
            Assert.Equal("1.0.0", await GetVersion());

            // Checking out again bumps the patch, and keeping settles that one too.
            modelId = await CheckoutAsync(modelId);
            Assert.Equal("1.0.1-alpha", await GetVersion());

            await CheckinAsync(modelId, "keep");
            Assert.Equal("1.0.1", await GetVersion());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task Keep_AcceptsACallerChosenVersion()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();

            // The dialog offers the stripped version but lets the user change it.
            await CheckinAsync(modelId, "keep", version: "2.5.0");
            Assert.Equal("2.5.0", await GetVersion());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task Keep_StripsTheSuffixFromACallerChosenVersion()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();

            // A kept model is not being edited, so it may not carry the marker even
            // when an API caller supplies one explicitly.
            await CheckinAsync(modelId, "keep", version: "3.1.0-alpha");
            Assert.Equal("3.1.0", await GetVersion());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    [Fact]
    public async Task Keep_RejectsAVersionAlreadyUsedByAnotherRow()
    {
        await DeleteModelLinks();
        try
        {
            var modelId = await CreateModel();
            await CheckinAsync(modelId, "keep");
            Assert.Equal("1.0.0", await GetVersion());

            // Checkout retains 1.0.0 as the backup Discard restores, so keeping the new
            // working copy at 1.0.0 would collide with it.
            modelId = await CheckoutAsync(modelId);
            var conflict = await SendCheckinAsync(modelId, "keep", version: "1.0.0");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

            // The rejected check-in changed nothing: still the checked-out working copy.
            Assert.Equal("1.0.1-alpha", await GetVersion());
            Assert.True(await GetIsEditable());
        }
        finally
        {
            await DeleteModelLinks();
        }
    }

    private async Task<string> CreateModel()
    {
        var req = WithServer(HttpMethod.Post, "/api/opcua/v1/namespaces/info");
        req.Content = JsonContent.Create(new
        {
            uri = KeepModelUri,
            name = "CheckinKeepModel",
            license = "MIT",
            copyrightHolder = "Test Copyright Holder",
        });
        var resp = await Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"create failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    /// <summary>Checks the model out and returns the new working copy's id.</summary>
    private async Task<string> CheckoutAsync(string modelId)
    {
        var resp = await Client.SendAsync(
            WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkout"));
        Assert.True(resp.IsSuccessStatusCode,
            $"checkout failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        return await GetModelIdAsync();
    }

    private async Task<HttpResponseMessage> SendCheckinAsync(string modelId, string action, string? version = null)
    {
        var req = WithServer(HttpMethod.Post, $"/api/opcua/v1/namespaces/info/{modelId}/checkin");
        req.Content = JsonContent.Create(new { action, version });
        return await Client.SendAsync(req);
    }

    private async Task CheckinAsync(string modelId, string action, string? version = null)
    {
        var resp = await SendCheckinAsync(modelId, action, version);
        Assert.True(resp.IsSuccessStatusCode,
            $"check-in failed ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// The list entry for the test URI. A checkout leaves two links; the list collapses
    /// them per URI and shows the active one, which is what the dialog acts on.
    /// </summary>
    private async Task<JsonElement> GetEntryAsync()
    {
        var resp = await Client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var entry = body.GetProperty("results").EnumerateArray()
            .FirstOrDefault(ns => ns.TryGetProperty("uri", out var uri)
                && string.Equals(uri.GetString(), KeepModelUri, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(JsonValueKind.Undefined, entry.ValueKind);
        return entry;
    }

    private async Task<string> GetModelIdAsync() => (await GetEntryAsync()).GetProperty("id").GetString()!;

    private async Task<string?> GetVersion() =>
        (await GetEntryAsync()).TryGetProperty("version", out var v) ? v.GetString() : null;

    private async Task<bool> GetIsEditable() =>
        (await GetEntryAsync()).TryGetProperty("isEditable", out var e) && e.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Remove every workspace link for the test URI. A checkout leaves two links (backup +
    /// working copy) and the list collapses them per URI, so delete-and-relist until gone.
    /// </summary>
    private async Task DeleteModelLinks()
    {
        for (var i = 0; i < 5; i++)
        {
            var resp = await Client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

            var entry = body.GetProperty("results").EnumerateArray()
                .FirstOrDefault(ns => ns.TryGetProperty("uri", out var uri)
                    && string.Equals(uri.GetString(), KeepModelUri, StringComparison.OrdinalIgnoreCase));
            if (entry.ValueKind == JsonValueKind.Undefined) return;

            var id = entry.GetProperty("id").GetString();
            await Client.SendAsync(WithServer(HttpMethod.Delete, $"/api/opcua/v1/namespaces/info/{id}"));
        }
    }
}
