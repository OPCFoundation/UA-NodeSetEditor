using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Unit tests for the HMAC-signed email session cookie. Pure (no DB / no web host):
/// round-trip, tamper detection, and signing-secret isolation.
/// </summary>
public class EmailAuthCookieTests
{
    private const string Secret = "unit-test-cookie-secret-0123456789";

    [Fact]
    public void RoundTrips_Email()
    {
        var cookie = new EmailAuthCookie(Secret);
        var value = cookie.Create("user@example.com");

        Assert.Equal("user@example.com", cookie.Validate(value));
    }

    [Fact]
    public void Rejects_TamperedPayload()
    {
        var cookie = new EmailAuthCookie(Secret);
        var value = cookie.Create("user@example.com");

        // Flip the last character of the base64 blob — the recomputed HMAC won't match.
        var tampered = value.Substring(0, value.Length - 1) + (value[^1] == 'A' ? 'B' : 'A');

        Assert.Null(cookie.Validate(tampered));
    }

    [Fact]
    public void Rejects_CookieSignedWithDifferentSecret()
    {
        var issued = new EmailAuthCookie(Secret).Create("user@example.com");
        var other = new EmailAuthCookie("a-totally-different-secret-value-1");

        Assert.Null(other.Validate(issued));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("dGhpcy1pcy1ub3QtdGhlLXJpZ2h0LXNoYXBl")] // valid base64, wrong structure
    public void Rejects_MalformedInput(string? input)
    {
        var cookie = new EmailAuthCookie(Secret);
        Assert.Null(cookie.Validate(input));
    }

    [Fact]
    public void Constructor_Rejects_ShortSecret()
    {
        Assert.Throws<ArgumentException>(() => new EmailAuthCookie("too-short"));
    }
}
