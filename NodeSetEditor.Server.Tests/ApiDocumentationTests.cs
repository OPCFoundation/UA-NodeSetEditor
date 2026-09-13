using System.Net;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the API documentation endpoints. Two properties matter here: the docs are login-only
/// (they describe every request body and internal endpoint, and the global fallback policy is the
/// only thing gating them — a stray AllowAnonymous would publish the whole API surface), and the
/// spec served is the checked-in, build-generated OpenApi\v1.json rather than a runtime document.
/// </summary>
[Collection("Api")]
public class ApiDocumentationTests
{
    private readonly ApiFixture _fixture;

    public ApiDocumentationTests(ApiFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/swagger-ui.css")]
    [InlineData("/openapi/v1.json")]
    public async Task Documentation_Anonymous_Returns401(string url)
    {
        using var client = _fixture.CreateAnonymousClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_Authenticated_ServesUiPage()
    {
        var response = await _fixture.Client.GetAsync("/swagger");
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();

        // The page must not fall through to the SPA shell — MapFallbackToFile would otherwise
        // serve index.html for an extensionless route and the docs would silently disappear.
        Assert.Contains("swagger-ui", html);
        Assert.Contains("/swagger/swagger-ui-bundle.js", html);
    }

    [Fact]
    public async Task OpenApiDocument_Authenticated_IsVersion30()
    {
        var response = await _fixture.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // 3.0.x, not 3.1 — the OpenAPI generators used against this spec handle 3.0 far better.
        Assert.StartsWith("3.0.", root.GetProperty("openapi").GetString());
        Assert.NotEmpty(root.GetProperty("paths").EnumerateObject());
    }

    [Fact]
    public async Task OpenApiDocument_DescribesTheAuthenticationSchemes()
    {
        var response = await _fixture.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemes = doc.RootElement.GetProperty("components").GetProperty("securitySchemes");

        Assert.True(schemes.TryGetProperty("AzureAd", out _));
        Assert.True(schemes.TryGetProperty("EmailCookie", out _));

        // The worker's X-Api-Key scheme must not appear: its endpoints are hidden from this
        // document, so the scheme would describe nothing.
        Assert.False(schemes.TryGetProperty("WorkerApiKey", out _));

        // An anonymous endpoint carries an explicit empty requirement; without it a reader would
        // inherit the default and think sign-in is required to read the licence list.
        var licenses = doc.RootElement
            .GetProperty("paths").GetProperty("/api/opcua/v1/licenses")
            .GetProperty("get").GetProperty("security");
        Assert.Equal(0, licenses.GetArrayLength());
    }

    [Fact]
    public async Task OpenApiDocument_ExcludesInternalControllers()
    {
        var response = await _fixture.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        // Account, Auth, CloudLibrary and the validation worker are deliberately hidden.
        // Hiding is documentation-only — authorization is what actually protects them.
        Assert.DoesNotContain(paths, p => p.StartsWith("/account", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("cloudlibrary", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("/validation/worker", StringComparison.OrdinalIgnoreCase));

        // The editor endpoints are grouped under "Editor", not the controller's class name.
        var tags = doc.RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject())
            .SelectMany(op => op.Value.GetProperty("tags").EnumerateArray().Select(t => t.GetString()))
            .Distinct()
            .ToList();
        Assert.Contains("Editor", tags);
        Assert.DoesNotContain("UaRestApi", tags);
    }

    [Fact]
    public async Task Documentation_UnknownFile_Returns404()
    {
        var missingDoc = await _fixture.Client.GetAsync("/openapi/does-not-exist.json");
        Assert.Equal(HttpStatusCode.NotFound, missingDoc.StatusCode);

        // The route values are reduced with Path.GetFileName before they reach the filesystem,
        // so a traversal attempt resolves to a name that isn't there rather than escaping.
        var traversal = await _fixture.Client.GetAsync("/openapi/..%2f..%2fappsettings.json");
        Assert.NotEqual(HttpStatusCode.OK, traversal.StatusCode);
    }
}
