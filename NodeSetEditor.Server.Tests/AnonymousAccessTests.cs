using System.Net;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards the authorization posture introduced by the global fallback policy:
/// protected API endpoints must fail closed for anonymous callers, while the
/// SPA shell and its static assets must remain anonymously reachable so an
/// unauthenticated user can load the app and sign in. The latter regressed once
/// when the fallback policy started 401-ing index.html / static assets.
/// </summary>
[Collection("Api")]
public class AnonymousAccessTests
{
    private readonly ApiFixture _fixture;

    public AnonymousAccessTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProtectedApi_Anonymous_Returns401()
    {
        using var client = _fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/opcua/v1/discovery");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SpaShell_Anonymous_IsNotBlockedByAuthorization()
    {
        using var client = _fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/");

        // The page must not be gated by the fallback auth policy. It may be 200
        // (index.html present) or 404 (not built into the test wwwroot), but it
        // must never be 401 — that would mean anonymous users can't reach login.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
