using System.Security.Claims;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Middleware that injects fake authentication claims for local development.
    /// Activated by DevAuth:Enabled = true in appsettings and the X-Dev-Auth header.
    /// Must NEVER be enabled in production.
    /// </summary>
    public class DevAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _userId;
        private readonly string _email;
        private readonly string _displayName;

        public DevAuthMiddleware(RequestDelegate next, IConfiguration configuration)
        {
            _next = next;
            _userId = configuration["DevAuth:UserId"] ?? "dev-user-00000000";
            _email = configuration["DevAuth:Email"] ?? "dev@localhost";
            _displayName = configuration["DevAuth:DisplayName"] ?? "Dev User";
        }

        public Task Invoke(HttpContext context)
        {
            if (context.Request.Headers.ContainsKey("X-Dev-Auth"))
            {
                // Per-request overrides via headers, falling back to config defaults
                var userId = context.Request.Headers["X-Dev-UserId"].FirstOrDefault() ?? _userId;
                var email = context.Request.Headers["X-Dev-UserEmail"].FirstOrDefault() ?? _email;
                var displayName = context.Request.Headers["X-Dev-DisplayName"].FirstOrDefault() ?? _displayName;

                var claims = new List<Claim>
                {
                    new("oid", userId),
                    new(ClaimTypes.NameIdentifier, userId),
                    new("preferred_username", email),
                    new(ClaimTypes.Email, email),
                    new("name", displayName),
                    new(ClaimTypes.Name, displayName)
                };

                var identity = new ClaimsIdentity(claims, "DevAuth");
                context.User = new ClaimsPrincipal(identity);
            }

            return _next(context);
        }
    }
}
