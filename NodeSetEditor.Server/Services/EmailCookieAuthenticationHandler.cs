using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Authenticates a browser session established through the email-code sign-in path by
    /// validating the HMAC-signed <c>opc-email-auth</c> cookie (see <see cref="EmailAuthCookie"/>).
    /// A valid cookie yields a principal carrying the email as <c>preferred_username</c>/email/name
    /// claims — the same shape <see cref="AuthenticatedUser.FromClaimsPrincipal"/> reads for the
    /// Azure AD bearer path, so both sign-in methods resolve to one email-keyed identity.
    ///
    /// A missing or tampered/expired cookie yields <c>NoResult</c> (treated as anonymous), never a
    /// hard failure — so this composes cleanly with the bearer scheme under the policy selector.
    /// </summary>
    public sealed class EmailCookieAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "EmailCookie";

        private readonly EmailAuthCookie _cookie;
        private readonly TestModeOptions _testMode;

        public EmailCookieAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            EmailAuthCookie cookie,
            TestModeOptions testMode)
            : base(options, logger, encoder)
        {
            _cookie = cookie;
            _testMode = testMode;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Cookies.TryGetValue(EmailAuthCookie.CookieName, out var raw) || string.IsNullOrEmpty(raw))
                return Task.FromResult(AuthenticateResult.NoResult());

            var email = _cookie.Validate(raw);
            if (string.IsNullOrEmpty(email))
                return Task.FromResult(AuthenticateResult.NoResult());

            // Normally the address doubles as the display name. The shared test-mode account
            // instead shows "Test Mode" everywhere the UI names the signed-in user, so an
            // evaluation instance is never mistaken for a real one.
            var displayName = _testMode.IsTestModeUser(email) ? TestModeOptions.DisplayName : email;

            var claims = new[]
            {
                new Claim("preferred_username", email),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, displayName),
                new Claim("name", displayName),
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
