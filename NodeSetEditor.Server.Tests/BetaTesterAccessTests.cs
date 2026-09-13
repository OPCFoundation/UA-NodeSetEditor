using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// End-to-end cover for the beta-format gate on the export endpoint. XML is open to everyone;
/// JSON, JSON-LD and the archive are only served to accounts the <c>BetaTesterDomains</c>
/// allow-list admits. The list is enforced server-side, so a client that ignores the flag and
/// asks for a beta format anyway still gets turned away.
/// </summary>
[Collection("Api")]
public class BetaTesterAccessTests : UaRestTestBase
{
    // Admits the fixture user (test@example.com); the domain-only entry is there to prove one
    // list can carry both forms.
    private const string AdmitsTestUser = "betatester.example.org, test@example.com";

    // A well-formed list that does not admit the fixture user.
    private const string ExcludesTestUser = "betatester.example.org, someone.else@example.com";

    public BetaTesterAccessTests(ApiFixture fixture) : base(fixture) { }

    private async Task<string> TestModelIdAsync()
    {
        var request = WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info");
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var model in body.GetProperty("results").EnumerateArray())
        {
            if (model.TryGetProperty("uri", out var uri) && uri.GetString() == ApiFixture.TestModelUri)
            {
                return model.GetProperty("id").GetString()!;
            }
        }

        throw new InvalidOperationException($"The fixture model '{ApiFixture.TestModelUri}' was not found.");
    }

    private async Task<HttpResponseMessage> ExportAsync(HttpClient client, string modelId, string format, bool includeDependencies = false)
    {
        var url = $"/api/opcua/v1/namespaces/info/{modelId}/export?format={format}"
                  + (includeDependencies ? "&includeDependencies=true" : string.Empty);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("OpcUa-Server", WorkspaceUrn);

        return await client.SendAsync(request);
    }

    // "jsonld" is deliberately absent from both beta-format theories: RDF/JSON-LD ships in its own
    // assembly, which this build does not reference, so the server has no route that serves it.
    [Theory]
    [InlineData("json")]
    [InlineData("compressed")]
    public async Task Export_RefusesBetaFormats_ForAnAccountNotOnTheList(string format)
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(ExcludesTestUser);

        var response = await ExportAsync(client, modelId, format);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Export_RefusesBetaFormats_WhenNobodyIsConfigured(string? configured)
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(configured);

        var response = await ExportAsync(client, modelId, "json");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The gate must not shut the open format, whatever the list says.
    [Fact]
    public async Task Export_StillServesXml_ForAnAccountNotOnTheList()
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(ExcludesTestUser);

        var response = await ExportAsync(client, modelId, "xml");

        Assert.True(response.IsSuccessStatusCode,
            $"XML export failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("json", "application/json")]
    [InlineData("compressed", "application/gzip")]
    public async Task Export_ServesBetaFormats_ForAnAccountOnTheList(string format, string expectedContentType)
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(AdmitsTestUser);

        var response = await ExportAsync(client, modelId, format);

        Assert.True(response.IsSuccessStatusCode,
            $"{format} export failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Export_NamesTheArchiveWithTheUaNodeSetExtension()
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(AdmitsTestUser);

        var response = await ExportAsync(client, modelId, "compressed");
        response.EnsureSuccessStatusCode();

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName
                       ?? string.Empty;

        Assert.EndsWith(".uanodeset", fileName.Trim('"'));
        Assert.DoesNotContain(".tar.gz", fileName);
    }

    // The bundle path serializes every dependency in the requested format, so it has to be gated
    // on the same rule rather than slipping through as a ZIP.
    [Fact]
    public async Task Export_RefusesABetaFormatBundle_ForAnAccountNotOnTheList()
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(ExcludesTestUser);

        var response = await ExportAsync(client, modelId, "json", includeDependencies: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_AllowsAnXmlBundle_ForAnAccountNotOnTheList()
    {
        var modelId = await TestModelIdAsync();
        using var client = Fixture.CreateClientWithBetaTesters(ExcludesTestUser);

        var response = await ExportAsync(client, modelId, "xml", includeDependencies: true);

        Assert.True(response.IsSuccessStatusCode,
            $"XML bundle failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
    }

    // What the SPA reads to decide whether to offer the beta formats at all.
    [Theory]
    [InlineData(AdmitsTestUser, true)]
    [InlineData(ExcludesTestUser, false)]
    [InlineData("", false)]
    public async Task UserPreferences_ReportWhetherTheAccountIsABetaTester(string configured, bool expected)
    {
        using var client = Fixture.CreateClientWithBetaTesters(configured);

        var response = await client.GetAsync("/api/opcua/v1/user/preferences");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(expected, body.GetProperty("betaTester").GetBoolean());
    }
}
