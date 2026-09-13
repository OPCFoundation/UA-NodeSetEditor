using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NodeSetEditor.Server.Services
{
    public class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions
    {
    }

    /// <summary>
    /// Authenticates the external validation worker (a headless machine, not a user) via a shared
    /// secret in the <c>X-Api-Key</c> header, compared in constant time to
    /// <c>Validation:WorkerApiKey</c>. The value in appsettings.json is a local-testing placeholder;
    /// deployed environments set it as an App Service application setting, which overrides the file.
    /// When the key is not configured the scheme yields <c>NoResult</c> so the endpoint simply stays
    /// unauthenticated (the feature is effectively disabled). A successful match produces a synthetic
    /// principal in the <see cref="Worker"/> role, consumed by the <c>Worker</c> authorization policy.
    /// </summary>
    public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>
    {
        public const string SchemeName = "ApiKey";
        public const string Worker = "Worker";
        private const string HeaderName = "X-Api-Key";

        private readonly string? _configuredKey;

        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration configuration)
            : base(options, logger, encoder)
        {
            _configuredKey = configuration["Validation:WorkerApiKey"];
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Feature disabled unless a key is configured — don't fail, just don't authenticate.
            if (string.IsNullOrEmpty(_configuredKey))
                return Task.FromResult(AuthenticateResult.NoResult());

            if (!Request.Headers.TryGetValue(HeaderName, out var provided) || string.IsNullOrEmpty(provided))
                return Task.FromResult(AuthenticateResult.NoResult());

            var candidate = Encoding.UTF8.GetBytes(provided.ToString());
            var expected = Encoding.UTF8.GetBytes(_configuredKey);
            if (!CryptographicOperations.FixedTimeEquals(candidate, expected))
                return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "validation-worker"),
                new Claim(ClaimTypes.Role, Worker),
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
