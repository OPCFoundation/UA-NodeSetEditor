using System.Net;
using System.Net.Http.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Verifies the email-code auth endpoints are reachable at the correct route (under /api, like
/// every other controller) and enforce anonymous-access + validation as expected. Guards against
/// the route-prefix regression where the controller sat at /auth while the client calls /api/auth.
/// These cases deliberately avoid the happy path (which writes to LoginCodes) so they don't depend
/// on that table existing in the shared test DB.
/// </summary>
[Collection("Api")]
public class AuthEndpointsTests
{
    private readonly ApiFixture _fixture;
    public AuthEndpointsTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Session_Anonymous_Returns401_NotFound()
    {
        using var anon = _fixture.CreateAnonymousClient();
        var res = await anon.GetAsync("/api/auth/session");
        // 401 (Unauthorized) proves the route exists and the auth pipeline ran — a 404 here
        // would mean the controller route is wrong.
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task RequestCode_InvalidEmail_Returns400_NotFound()
    {
        using var anon = _fixture.CreateAnonymousClient();
        var res = await anon.PostAsJsonAsync("/api/auth/request-code", new { email = "not-an-email" });
        // 400 (validation) rather than 404 confirms the endpoint is routed correctly.
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task VerifyCode_MissingCode_Returns400()
    {
        using var anon = _fixture.CreateAnonymousClient();
        var res = await anon.PostAsJsonAsync("/api/auth/verify-code", new { email = "someone@example.org", code = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
