using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Guards preference-row provisioning against the first-login burst: the SPA
/// fires several requests in parallel for a brand-new user, all of which try
/// to insert the UserPreferences row. The losers of that insert race used to
/// surface as 500s (23505 duplicate key on PK_UserPreferences); they must now
/// recover and return the winner's row.
/// </summary>
[Collection("Api")]
public class UserPreferenceRaceTests : UaRestTestBase
{
    public UserPreferenceRaceTests(ApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ConcurrentFirstLogin_ProvisionsPreferenceRow_WithoutConflicts()
    {
        // A fresh user id AND email local-part each run, so both the row-insert
        // race and the name-generation race are genuinely exercised.
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var localPart = $"prefrace{suffix}";
        var email = $"{localPart}@test.net";

        // Identity is keyed on the LOWERCASED EMAIL (AuthenticatedUser.FromClaimsPrincipal), so
        // the two sign-in paths converge on one user; the object id is only the fallback for a
        // token with no email claim. The preference row is therefore keyed on the email, not on
        // the X-Dev-UserId this request also carries.
        var devUserId = $"pref-race-{suffix}";
        var userId = email.ToLowerInvariant();

        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Client.SendAsync(AsUser(HttpMethod.Get, "/api/opcua/v1/user/preferences", devUserId, email))));

            foreach (var response in responses)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode,
                    $"Concurrent preference provisioning failed ({(int)response.StatusCode}): {body}");
            }

            // The slower racers must never see the faster one's freshly committed
            // name as "taken" and overwrite it with a numbered variant
            // (randy → randy2). The persisted name is the plain local part.
            using var scope = Fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
            var row = await db.UserPreferences.SingleAsync(p => p.UserId == userId);
            Assert.Equal(localPart, row.Name);
        }
        finally
        {
            using var scope = Fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
            await db.UserPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        }
    }
}
