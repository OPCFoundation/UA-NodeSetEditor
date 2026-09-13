using Xunit;
using DbModel = NodeSetEditor.Model.Model;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Unit tests for the checkout/check-in version helpers on <see cref="Model"/>.
/// Pure functions — no database or server fixture required.
/// </summary>
public class ModelVersionHelpersTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1-alpha")]   // release → patch+1, -alpha
    [InlineData("2.3", "2.3.1-alpha")]     // missing patch defaults to 0
    [InlineData("1.0.1-beta", "1.0.2-alpha")] // beta is bumped like a release
    [InlineData("1.0.1-alpha", "1.0.2-alpha")] // an -alpha is ALSO bumped (keeps prior as backup)
    [InlineData("2.0.0-alpha", "2.0.1-alpha")]
    [InlineData("", "0.0.1-alpha")]        // empty → 0.0.0 → 0.0.1-alpha
    [InlineData(null, "0.0.1-alpha")]
    public void BumpForCheckout_AlwaysBumpsPatchAndAppendsAlpha(string? input, string expected)
    {
        Assert.Equal(expected, DbModel.BumpForCheckout(input));
    }

    [Theory]
    [InlineData("1.0.1-alpha", "1.0.1-beta")]
    [InlineData("1.0.1", "1.0.1-beta")]
    [InlineData("2.3", "2.3.0-beta")]
    public void PublishVersion_ReplacesPrereleaseWithBeta(string input, string expected)
    {
        Assert.Equal(expected, DbModel.PublishVersion(input));
    }

    [Theory]
    [InlineData("1.0.1-alpha", "1.0.1")]      // the working-copy marker comes off
    [InlineData("1.0.1-beta", "1.0.1")]
    [InlineData("1.0.1-beta.2", "1.0.1")]     // ...counter and all
    [InlineData("1.0.1-ALPHA", "1.0.1")]      // label match is case-insensitive
    [InlineData("1.0.1", "1.0.1")]            // already settled — unchanged
    [InlineData("2.3", "2.3.0")]              // missing patch defaults to 0
    [InlineData("1.0.1-rc1", "1.0.1-rc1")]    // someone else's label is left alone
    [InlineData("", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void StripWorkingSuffix_DropsOnlyAlphaAndBeta(string? input, string expected)
    {
        Assert.Equal(expected, DbModel.StripWorkingSuffix(input));
    }

    [Fact]
    public void Checkout_Then_Keep_SettlesOnTheBumpedPatch()
    {
        // Keeping ends the edit without publishing: the patch bumped at checkout stays,
        // the -alpha marker does not. A second checkout then bumps again, so the kept
        // version is never overwritten by the next working copy.
        var working = DbModel.BumpForCheckout("1.0.0"); // 1.0.1-alpha
        var kept = DbModel.StripWorkingSuffix(working);
        Assert.Equal("1.0.1", kept);
        Assert.Equal("1.0.2-alpha", DbModel.BumpForCheckout(kept));
    }

    [Fact]
    public void Checkout_Then_Publish_ProducesBetaOfNewPatch()
    {
        var working = DbModel.BumpForCheckout("1.0.0"); // 1.0.1-alpha
        Assert.Equal("1.0.1-alpha", working);
        var published = DbModel.PublishVersion(working); // 1.0.1-beta
        Assert.Equal("1.0.1-beta", published);
    }

    [Theory]
    [InlineData("1.0.1-alpha", "000100000001-alpha")]
    [InlineData("1.0.1-beta", "000100000001-beta")]
    [InlineData("1.0.1", "000100000001~")]
    public void NormalizeVersion_SortsAlphaBeforeBetaBeforeRelease(string version, string expectedNorm)
    {
        Assert.Equal(expectedNorm, DbModel.NormalizeVersion(version));
    }

    [Fact]
    public void NormalizeVersion_OrderingIsAlphaThenBetaThenRelease()
    {
        var alpha = DbModel.NormalizeVersion("1.0.1-alpha")!;
        var beta = DbModel.NormalizeVersion("1.0.1-beta")!;
        var release = DbModel.NormalizeVersion("1.0.1")!;
        Assert.True(string.CompareOrdinal(alpha, beta) < 0);
        Assert.True(string.CompareOrdinal(beta, release) < 0);
    }
}
