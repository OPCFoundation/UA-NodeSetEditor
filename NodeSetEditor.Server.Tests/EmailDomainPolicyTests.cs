using Microsoft.Extensions.Configuration;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Unit tests for the sign-in allow-list. Pure (no DB / no web host). Two behaviours matter:
/// an unset list must admit everyone (the hosted deployment relies on that, where the Azure AD
/// tenant is the perimeter), and a configured list must not admit a near-miss domain.
/// </summary>
public class EmailDomainPolicyTests
{
    private static EmailDomainPolicy Policy(string? configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EmailDomainPolicy.ConfigurationKey] = configured
            })
            .Build();

        return new EmailDomainPolicy(configuration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void AnUnsetListAdmitsEveryone(string? configured)
    {
        var policy = Policy(configured);

        Assert.True(policy.IsUnrestricted);
        Assert.True(policy.IsAllowed("anyone@example.com"));
    }

    [Theory]
    [InlineData("example.com", "user@example.com", true)]
    [InlineData("@example.com", "user@example.com", true)]
    [InlineData("example.com", "user@EXAMPLE.COM", true)]
    [InlineData("example.com", "user@notexample.com", false)]
    [InlineData("example.com", "user@example.com.evil.net", false)]
    [InlineData("example.com", "example.com@evil.net", false)]
    [InlineData("user@example.com", "user@example.com", true)]
    [InlineData("user@example.com", "other@example.com", false)]
    [InlineData("a.org, b.org", "someone@b.org", true)]
    public void AConfiguredListAdmitsOnlyWhatItNames(string configured, string email, bool expected)
    {
        Assert.Equal(expected, Policy(configured).IsAllowed(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARestrictedListRejectsAMissingAddress(string? email)
    {
        Assert.False(Policy("example.com").IsAllowed(email));
    }
}
