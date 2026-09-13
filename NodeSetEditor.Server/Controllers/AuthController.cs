using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Controllers
{
    /// <summary>
    /// Passwordless "email code" sign-in endpoints. A user requests a one-time code by email,
    /// submits it, and — on success — receives an HttpOnly, HMAC-signed session cookie
    /// (<see cref="EmailAuthCookie"/>). This runs alongside, not instead of, the Azure AD bearer
    /// path; both resolve to the same email-keyed identity. Endpoints are anonymous by necessity
    /// (the caller has no session yet) and rate-limited to blunt enumeration / email-bombing.
    /// </summary>
    [ApiController]
    [ApiExplorerSettings(IgnoreApi = true)] // Not part of the published API surface.
    [AllowAnonymous]
    [Route("api/auth")]
    [EnableRateLimiting(Program.AuthRateLimitPolicy)]
    public sealed class AuthController : ControllerBase
    {
        private readonly EmailVerificationService _verification;
        private readonly EmailAuthCookie _cookie;
        private readonly IWebHostEnvironment _env;
        private readonly TestModeOptions _testMode;
        private readonly EmailDomainPolicy _domains;
        private readonly bool _requireSecureCookie;

        public AuthController(
            EmailVerificationService verification,
            EmailAuthCookie cookie,
            IWebHostEnvironment env,
            TestModeOptions testMode,
            EmailDomainPolicy domains,
            IConfiguration configuration)
        {
            _verification = verification;
            _cookie = cookie;
            _env = env;
            _testMode = testMode;
            _domains = domains;
            _requireSecureCookie = configuration.GetValue("Auth:RequireSecureCookie", true);
        }

        public sealed record RequestCodeBody(string? Email);
        public sealed record VerifyCodeBody(string? Email, string? Code);

        /// <summary>Emails a one-time sign-in code. Always returns a generic success (no enumeration).</summary>
        [HttpPost("request-code")]
        public async Task<IActionResult> RequestCode([FromBody] RequestCodeBody body, CancellationToken ct)
        {
            var email = body?.Email?.Trim() ?? string.Empty;
            if (!IsValidEmail(email))
                return BadRequest(new { message = "A valid email address is required." });

            // An address outside the allow-list gets the same generic response as any other, so
            // the endpoint still reveals nothing — it simply never receives a code.
            if (_domains.IsAllowed(email))
                await _verification.RequestCodeAsync(email, ct);

            // Deliberately generic — the response is identical whether the address is new,
            // known, throttled, or undeliverable, so this endpoint reveals nothing.
            return Ok(new { message = "If that address is valid, a sign-in code has been sent." });
        }

        /// <summary>Verifies a submitted code and, on success, sets the session cookie.</summary>
        [HttpPost("verify-code")]
        public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeBody body, CancellationToken ct)
        {
            var email = body?.Email?.Trim() ?? string.Empty;
            var code = body?.Code?.Trim() ?? string.Empty;
            if (!IsValidEmail(email) || code.Length == 0)
                return BadRequest(new { message = "Email and code are required." });

            var result = await _verification.VerifyCodeAsync(email, code, ct);
            switch (result)
            {
                case VerifyResult.Success:
                    var normalized = EmailVerificationService.Normalize(email);
                    Response.Cookies.Append(EmailAuthCookie.CookieName, _cookie.Create(normalized), BuildCookieOptions());
                    return Ok(new { email = normalized });

                case VerifyResult.LockedOut:
                    return StatusCode(StatusCodes.Status429TooManyRequests,
                        new { message = "Too many attempts. Please request a new code." });

                default:
                    return BadRequest(new { message = "That code is invalid or has expired." });
            }
        }

        /// <summary>
        /// Signs in to the shared test-mode account. Available only when TestMode:Enabled is set;
        /// 404 otherwise, so a deployment that has not opted in has no such endpoint at all.
        /// </summary>
        [HttpPost("test-mode")]
        public IActionResult TestModeSignIn()
        {
            if (!_testMode.Enabled) return NotFound();

            Response.Cookies.Append(
                EmailAuthCookie.CookieName,
                _cookie.Create(TestModeOptions.Email),
                BuildCookieOptions());

            return Ok(new { email = TestModeOptions.Email, displayName = TestModeOptions.DisplayName });
        }

        /// <summary>Clears the email-code session cookie.</summary>
        [HttpPost("logout")]
        public IActionResult Logout()
        {
            Response.Cookies.Delete(EmailAuthCookie.CookieName, BuildCookieOptions(forDelete: true));
            return Ok(new { message = "Signed out." });
        }

        /// <summary>
        /// Returns the current session's identity, or 401 if not signed in. The SPA calls this on
        /// load to discover an existing email-code session (the cookie is HttpOnly, so JS cannot
        /// read it directly). Populated for the cookie path by the policy auth scheme.
        /// </summary>
        [HttpGet("session")]
        public IActionResult Session()
        {
            var user = AuthenticatedUser.FromClaimsPrincipal(HttpContext.User);
            if (!user.IsAuthenticated)
                return Unauthorized();

            return Ok(new { email = user.Email, displayName = user.DisplayName, userId = user.UserId });
        }

        private CookieOptions BuildCookieOptions(bool forDelete = false) => new()
        {
            HttpOnly = true,
            // Same-origin SPA + API, so Strict gives strong CSRF protection while still being
            // sent on the app's own XHR calls.
            //
            // Secure defaults on, and every hosted deployment keeps it that way. It is
            // configurable because a Secure cookie is not sent over plain HTTP to anything but
            // localhost: a container published on a LAN address without a TLS-terminating proxy
            // would set the cookie and never see it again, so sign-in would appear to succeed
            // and then silently fail. See Auth:RequireSecureCookie in docker/README.md.
            Secure = _requireSecureCookie,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = forDelete ? TimeSpan.Zero : EmailAuthCookie.Lifetime,
        };

        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return false;
            return MailAddress.TryCreate(email, out _);
        }
    }
}
