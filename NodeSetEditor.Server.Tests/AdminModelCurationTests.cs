using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The admin allow-list (<c>AdminEmails</c>): who may curate a SHARED model — one whose row is
/// used by every workspace linked to it — rather than only their own private copy. The case that
/// matters is the UA Core nodeset, which is shared into every workspace and lives in the
/// otherwise read-only <c>http://opcfoundation.org/</c> namespace.
/// </summary>
[Collection("Api")]
public class AdminModelCurationTests
{
    private const string UaCoreNamespace = "http://opcfoundation.org/UA/";

    private readonly ApiFixture _fixture;
    private readonly string _workspaceUrn;

    public AdminModelCurationTests(ApiFixture fixture)
    {
        _fixture = fixture;
        _workspaceUrn = fixture.WorkspaceUrn!;
    }

    private HttpRequestMessage WithServer(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("OpcUa-Server", _workspaceUrn);
        return request;
    }

    private async Task<JsonElement> GetNamespaceAsync(HttpClient client, string namespaceUri)
    {
        var response = await client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var ns in body.GetProperty("results").EnumerateArray())
        {
            if (ns.TryGetProperty("uri", out var uri) && uri.GetString() == namespaceUri) return ns;
        }
        throw new InvalidOperationException($"Model '{namespaceUri}' not found in the workspace.");
    }

    private static string? ProfileGroupOf(JsonElement ns) =>
        ns.TryGetProperty("profileGroupName", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private async Task<HttpResponseMessage> PutProfileGroupAsync(
        HttpClient client, string modelId, string? profileGroupName)
    {
        var request = WithServer(HttpMethod.Put, $"/api/opcua/v1/namespaces/info/{modelId}");
        request.Content = JsonContent.Create(new { profileGroupName = profileGroupName ?? string.Empty });
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Admin_CanSetTheProfileGroupOnTheSharedCoreModel()
    {
        var admin = _fixture.CreateClientWithAdmins(ApiFixture.TestUserEmail);
        var coreModelId = (await GetNamespaceAsync(admin, UaCoreNamespace)).GetProperty("id").GetString()!;

        try
        {
            var response = await PutProfileGroupAsync(admin, coreModelId, "UACore 1.05");
            Assert.True(response.IsSuccessStatusCode,
                $"admin edit failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");

            // And every other user sees it — the point of the role.
            Assert.Equal("UACore 1.05", ProfileGroupOf(await GetNamespaceAsync(_fixture.Client, UaCoreNamespace)));
        }
        finally
        {
            await PutProfileGroupAsync(admin, coreModelId, null);
        }
    }

    [Fact]
    public async Task NonAdmin_CannotSetTheProfileGroupOnTheSharedCoreModel()
    {
        // Empty allow-list: nobody is an admin, which is also the default for a deployment that
        // never sets AdminEmails.
        var user = _fixture.CreateClientWithAdmins(null);
        var coreModelId = (await GetNamespaceAsync(user, UaCoreNamespace)).GetProperty("id").GetString()!;

        var response = await PutProfileGroupAsync(user, coreModelId, "UACore 1.05");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(ProfileGroupOf(await GetNamespaceAsync(user, UaCoreNamespace)));
    }

    [Fact]
    public async Task AdminList_MatchesByDomainToo()
    {
        var domain = ApiFixture.TestUserEmail[(ApiFixture.TestUserEmail.IndexOf('@') + 1)..];
        var admin = _fixture.CreateClientWithAdmins($"@{domain}");
        var coreModelId = (await GetNamespaceAsync(admin, UaCoreNamespace)).GetProperty("id").GetString()!;

        try
        {
            var response = await PutProfileGroupAsync(admin, coreModelId, "Machinery 1.03");
            Assert.True(response.IsSuccessStatusCode,
                $"admin edit failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        }
        finally
        {
            await PutProfileGroupAsync(admin, coreModelId, null);
        }
    }

    [Fact]
    public async Task AdminList_DoesNotAdmitAnotherAddress()
    {
        var user = _fixture.CreateClientWithAdmins("someone.else@example.com");
        var coreModelId = (await GetNamespaceAsync(user, UaCoreNamespace)).GetProperty("id").GetString()!;

        var response = await PutProfileGroupAsync(user, coreModelId, "UACore 1.05");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The exported NodeSet XML for a model, as the download button produces it.</summary>
    private async Task<string> ExportXmlAsync(HttpClient client, string modelId)
    {
        var response = await client.SendAsync(WithServer(
            HttpMethod.Get, $"/api/opcua/v1/namespaces/info/{modelId}/export?format=xml"));
        Assert.True(response.IsSuccessStatusCode,
            $"export failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task ProfileGroup_ReachesTheExportedXml_ForAPrivateModel()
    {
        // The whole point of the convention: what an admin (or owner) sets must travel in the
        // NodeSet. This goes through the real export endpoint, which serializes from the cached
        // workspace address space rather than straight from the DB.
        var client = _fixture.Client;
        var modelId = (await GetNamespaceAsync(client, ApiFixture.TestModelUri)).GetProperty("id").GetString()!;

        try
        {
            (await PutProfileGroupAsync(client, modelId, "UACore 1.05")).EnsureSuccessStatusCode();

            var xml = await ExportXmlAsync(client, modelId);

            Assert.Contains("<Category>ProfileGroup:UACore 1.05</Category>", xml);
        }
        finally
        {
            await PutProfileGroupAsync(client, modelId, null);
        }
    }

    [Fact]
    public async Task ProfileGroup_IsDroppedFromTheExportedXml_WhenCleared()
    {
        var client = _fixture.Client;
        var modelId = (await GetNamespaceAsync(client, ApiFixture.TestModelUri)).GetProperty("id").GetString()!;

        (await PutProfileGroupAsync(client, modelId, "UACore 1.05")).EnsureSuccessStatusCode();
        Assert.Contains("ProfileGroup:UACore 1.05", await ExportXmlAsync(client, modelId));

        (await PutProfileGroupAsync(client, modelId, null)).EnsureSuccessStatusCode();

        Assert.DoesNotContain("ProfileGroup:", await ExportXmlAsync(client, modelId));
    }

    [Fact]
    public async Task ProfileGroup_ReachesTheExportedXml_ForTheSharedCoreModel()
    {
        // Core is the model the admin role exists for, and it is imported straight through
        // StoreNodeSetAsync — so it is the one most likely to lack a NamespaceMetadata object
        // for the convention to attach to.
        var admin = _fixture.CreateClientWithAdmins(ApiFixture.TestUserEmail);
        var coreModelId = (await GetNamespaceAsync(admin, UaCoreNamespace)).GetProperty("id").GetString()!;

        try
        {
            (await PutProfileGroupAsync(admin, coreModelId, "UACore 1.05")).EnsureSuccessStatusCode();

            var xml = await ExportXmlAsync(admin, coreModelId);

            Assert.Contains("<Category>ProfileGroup:UACore 1.05</Category>", xml);
        }
        finally
        {
            await PutProfileGroupAsync(admin, coreModelId, null);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("someone.else@example.com", false)]
    [InlineData(ApiFixture.TestUserEmail, true)]
    public async Task UserPreferences_ReportWhetherTheAccountIsAnAdmin(string? adminEmails, bool expected)
    {
        var client = _fixture.CreateClientWithAdmins(adminEmails);

        var response = await client.GetAsync("/api/opcua/v1/user/preferences");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var isAdmin = body.TryGetProperty("admin", out var a) && a.ValueKind == JsonValueKind.True;
        Assert.Equal(expected, isAdmin);
    }
}
