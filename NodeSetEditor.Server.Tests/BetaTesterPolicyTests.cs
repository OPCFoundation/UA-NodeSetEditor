using Microsoft.Extensions.Configuration;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Unit tests for the beta-feature allow-list. Pure (no DB / no web host). The policy decides who
/// may download the non-XML formats, so the cases that matter most are the ones that must NOT
/// admit: an empty list, a near-miss domain, and a domain appearing anywhere but after the '@'.
/// </summary>
public class BetaTesterPolicyTests
{
    private static BetaTesterPolicy Policy(string? configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BetaTesterPolicy.ConfigurationKey] = configured
            })
            .Build();

        // Test mode disabled: these cases are about the allow-list itself. The shared test-mode
        // account bypasses it, and TestModeBypassesTheAllowList below covers that.
        return new BetaTesterPolicy(configuration, new TestModeOptions());
    }

    [Fact]
    public void TestModeBypassesTheAllowList()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BetaTesterPolicy.ConfigurationKey] = ""   // admits nobody
            })
            .Build();

        var enabled = new BetaTesterPolicy(configuration, new TestModeOptions { Enabled = true });
        var disabled = new BetaTesterPolicy(configuration, new TestModeOptions { Enabled = false });

        Assert.True(enabled.IsBetaTester(TestModeOptions.Email));
        Assert.False(disabled.IsBetaTester(TestModeOptions.Email));
        Assert.False(enabled.IsBetaTester("someone@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , , ")]
    public void AdmitsNobody_WhenNothingIsConfigured(string? configured)
    {
        var policy = Policy(configured);

        Assert.False(policy.IsBetaTester("randy@opcfoundation.org"));
        Assert.False(policy.IsBetaTester("anyone@example.com"));
    }

    [Theory]
    [InlineData("randy@opcfoundation.org")]
    [InlineData("someone.else@opcfoundation.org")]
    [InlineData("RANDY@OPCFOUNDATION.ORG")]
    public void AdmitsEveryAddressInAListedDomain(string email)
    {
        Assert.True(Policy("opcfoundation.org").IsBetaTester(email));
    }

    [Fact]
    public void AdmitsADomainWrittenWithALeadingAt()
    {
        Assert.True(Policy("@opcfoundation.org").IsBetaTester("randy@opcfoundation.org"));
    }

    [Fact]
    public void AdmitsASingleAddressWithoutAdmittingItsDomain()
    {
        var policy = Policy("randy@sparhawksoftware.com");

        Assert.True(policy.IsBetaTester("randy@sparhawksoftware.com"));
        Assert.False(policy.IsBetaTester("someone.else@sparhawksoftware.com"));
    }

    [Fact]
    public void SplitsOnCommasAndIgnoresSurroundingWhitespace()
    {
        var policy = Policy(" opcfoundation.org , randy@sparhawksoftware.com ,example.org ");

        Assert.True(policy.IsBetaTester("a@opcfoundation.org"));
        Assert.True(policy.IsBetaTester("randy@sparhawksoftware.com"));
        Assert.True(policy.IsBetaTester("b@example.org"));
        Assert.False(policy.IsBetaTester("c@nope.org"));
    }

    // A listed domain must match the whole domain, not a suffix or a substring — otherwise
    // "opcfoundation.org" would also admit "evil-opcfoundation.org" and "opcfoundation.org.evil.com".
    [Theory]
    [InlineData("user@evil-opcfoundation.org")]
    [InlineData("user@opcfoundation.org.evil.com")]
    [InlineData("user@sub.opcfoundation.org")]
    [InlineData("user@opcfoundation.or")]
    [InlineData("opcfoundation.org@evil.com")]
    public void RejectsNearMissDomains(string email)
    {
        Assert.False(Policy("opcfoundation.org").IsBetaTester(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("trailing@")]
    public void RejectsMissingOrMalformedAddresses(string? email)
    {
        Assert.False(Policy("opcfoundation.org").IsBetaTester(email));
    }

    [Fact]
    public void MatchesTheDomainAfterTheLastAt()
    {
        var policy = Policy("opcfoundation.org");

        Assert.True(policy.IsBetaTester("odd\"@\"local@opcfoundation.org"));
        Assert.False(policy.IsBetaTester("user@opcfoundation.org@evil.com"));
    }
}
