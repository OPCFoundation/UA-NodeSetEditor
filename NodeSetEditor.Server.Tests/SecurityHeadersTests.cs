namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the security response headers added in Program.cs: anti-sniffing,
/// anti-clickjacking (enforced frame-ancestors + legacy X-Frame-Options),
/// referrer suppression, and the report-only CSP that must soak before being
/// promoted to enforced.
/// </summary>
[Collection("Api")]
public class SecurityHeadersTests
{
    private readonly ApiFixture _fixture;

    public SecurityHeadersTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Responses_CarrySecurityHeaders()
    {
        using var client = _fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/opcua/v1/cloudlibrary/search?keyword=x&limit=1");

        Assert.Equal("nosniff", GetHeader(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", GetHeader(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", GetHeader(response, "Referrer-Policy"));
        Assert.Equal("frame-ancestors 'none'", GetHeader(response, "Content-Security-Policy"));
        Assert.Contains("default-src 'self'", GetHeader(response, "Content-Security-Policy-Report-Only"));
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
