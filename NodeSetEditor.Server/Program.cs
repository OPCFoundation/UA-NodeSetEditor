using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Identity.Web;
using Microsoft.OpenApi;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// The build-time OpenAPI generator (GetDocument.Insider, from Microsoft.Extensions.ApiDescription.Server)
// executes this entry point up to host build so it can ask the DI container for the document. It runs on
// a build machine with none of the deployed configuration available, and it never serves a request or
// opens a database connection. Startup requirements that exist to fail a real deployment fast are
// therefore relaxed for it — and only for it, since a spec build can never turn into a running server.
var isOpenApiBuild = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

builder.Services.AddMemoryCache();

// Register database context
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(connectionString))
{
    try
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        Console.WriteLine($"[Startup] Postgres: host={csb.Host}, database={csb.Database}");
    }
    catch { Console.WriteLine("[Startup] Postgres: (connection string set)"); }
}
else
{
    Console.WriteLine("[Startup] Postgres: (not set)");
}
if (string.IsNullOrEmpty(connectionString))
{
    if (!isOpenApiBuild)
        throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
    // AddDbContext only records the connection string; nothing connects during a spec build.
    connectionString = "Host=localhost;Database=openapi-build;Username=none;Password=none";
}

builder.Services.AddDbContext<NodeSetEditorDbContext>(options =>
    // Workspace loads two collection navigations (Acl + Models) in one query;
    // a single JOIN would cartesian-explode (ACL rows × model rows). Default to
    // split queries so each collection is fetched in its own round-trip.
    options.UseNpgsql(connectionString,
        npgsql => npgsql
            .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            // Azure Postgres has transient blips (failovers, connection/pool pressure) that
            // surface as Npgsql timeouts and otherwise bubble up to callers as bare 500s.
            // Retry them in-process so a momentary DB hiccup no longer fails a request.
            .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));
builder.Services.AddScoped<INodeSetStorageService, DbNodeSetStorageService>();
builder.Services.AddScoped<IValidationService, ValidationService>();
builder.Services.AddScoped<INodeSetSubsetService, NodeSetSubsetService>();
Console.WriteLine("[Startup] Using DbNodeSetStorageService (PostgreSQL)");

// Register workspace address space service
builder.Services.AddSingleton<IWorkspaceAddressSpaceService, WorkspaceAddressSpaceService>();

// Test mode: one shared account with every feature on, for evaluating a deployment that has no
// mail provider. Opt-in only, and independent of the Development-gated DevAuth bypass below.
var testMode = TestModeOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(testMode);
if (testMode.Enabled)
{
    Console.WriteLine("[Startup] *** TEST MODE ENABLED *** Anyone who can reach this instance can");
    Console.WriteLine("[Startup]     sign in to the shared '" + TestModeOptions.DisplayName + "' account. Use a throwaway database.");
}

// Allow-list for beta features (the non-XML download formats). Read once at startup.
builder.Services.AddSingleton<NodeSetEditor.Server.Services.BetaTesterPolicy>();

// Allow-list for who may edit shared/standard models for everyone (AdminEmails). Read once.
builder.Services.AddSingleton<NodeSetEditor.Server.Services.AdminPolicy>();

// Allow-list for who may request a sign-in code at all. Empty means everyone.
builder.Services.AddSingleton<NodeSetEditor.Server.Services.EmailDomainPolicy>();

// Register Cloud Library client
var cloudLibUrl = builder.Configuration["CloudLibraryUrl"];
if (!string.IsNullOrEmpty(cloudLibUrl))
{
    builder.Services.AddHttpClient<Opc.Ua.CloudLibraryApi.CloudLibraryClient>(client =>
    {
        client.BaseAddress = new Uri(cloudLibUrl.TrimEnd('/') + "/");
    });
    Console.WriteLine($"[Startup] Cloud Library: {cloudLibUrl}");
}

// 1. Authentication. Two user sign-in paths resolve to one email-keyed identity:
//    - Azure AD bearer JWTs (Microsoft.Identity.Web) — the "sign in with Microsoft" path.
//    - The passwordless email-code cookie (EmailCookie scheme) — no cross-tenant consent.
//    Plus a shared-secret "ApiKey" scheme for the headless validation worker (M2M).
//    A "Smart" policy scheme is the default: it forwards each request to the right concrete
//    scheme (Authorization header → Bearer; opc-email-auth cookie → EmailCookie), so a single
//    fallback RequireAuthenticatedUser policy accepts either sign-in method.
//    Azure AD is optional: a self-hosted deployment that sets no AzureAd:ClientId gets the
//    email-code path only, and the Microsoft sign-in option disappears from the UI (the SPA
//    reads the enabled providers from GET /api/config).
const string smartScheme = "Smart";
var azureAdEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]);
var fallbackScheme = azureAdEnabled
    ? JwtBearerDefaults.AuthenticationScheme
    : EmailCookieAuthenticationHandler.SchemeName;

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = smartScheme;
    options.DefaultChallengeScheme = fallbackScheme;
});
authBuilder.AddPolicyScheme(smartScheme, "Bearer or Email Cookie", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        if (azureAdEnabled && context.Request.Headers.ContainsKey("Authorization"))
            return JwtBearerDefaults.AuthenticationScheme;
        if (context.Request.Cookies.ContainsKey(EmailAuthCookie.CookieName))
            return EmailCookieAuthenticationHandler.SchemeName;
        // No credential presented → forward to whichever scheme can issue the challenge.
        return fallbackScheme;
    };
});
if (azureAdEnabled)
{
    authBuilder.AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
    Console.WriteLine("[Startup] Sign-in: Azure AD bearer + email code");
}
else
{
    Console.WriteLine("[Startup] Sign-in: email code only (AzureAd:ClientId not set)");
}
authBuilder.AddScheme<AuthenticationSchemeOptions, EmailCookieAuthenticationHandler>(
    EmailCookieAuthenticationHandler.SchemeName, _ => { });
authBuilder.AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationHandler.SchemeName, _ => { });

// Email-code sign-in services.
//  - EmailAuthCookie (singleton): HMAC signer for the session cookie. The secret must be set
//    in deployed environments (App Service setting); Development gets a fixed fallback so the
//    flow is exercisable without extra config (dev normally uses the DevAuth bypass anyway).
//  - IEmailSender: Postmark when configured (same provider/keys as the OPC Foundation tooling),
//    otherwise a logging sender that writes the code to the log (local dev only).
var emailCookieSecret = builder.Configuration["EmailAuth:CookieSecret"];
if (string.IsNullOrEmpty(emailCookieSecret))
{
    if (builder.Environment.IsDevelopment() || isOpenApiBuild)
        emailCookieSecret = "dev-only-email-auth-cookie-secret-change-me";
    else
        throw new InvalidOperationException("EmailAuth:CookieSecret is required in non-development environments.");
}
builder.Services.AddSingleton(new EmailAuthCookie(emailCookieSecret));

//  - SMTP (MailKit) for self-hosted deployments that bring their own mail server.
var postmarkApiKey = builder.Configuration["PostmarkApiKey"];
var postmarkSenderEmail = builder.Configuration["PostmarkSenderEmail"];
var smtpOptions = SmtpOptions.FromConfiguration(builder.Configuration);
if (!string.IsNullOrEmpty(postmarkApiKey) && !string.IsNullOrEmpty(postmarkSenderEmail))
{
    builder.Services.AddSingleton(new PostmarkOptions(postmarkApiKey, postmarkSenderEmail));
    builder.Services.AddSingleton<IEmailSender, PostmarkEmailSender>();
    Console.WriteLine("[Startup] Email sender: Postmark");
}
else if (smtpOptions != null)
{
    if (string.IsNullOrWhiteSpace(smtpOptions.From))
        throw new InvalidOperationException("Smtp:From is required when Smtp:Host is set.");

    builder.Services.AddSingleton(smtpOptions);
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
    Console.WriteLine($"[Startup] Email sender: SMTP {smtpOptions.Host}:{smtpOptions.Port} ({smtpOptions.Security})");
}
else
{
    // No mail provider. The email-code path is then the only way in for a real user, so a
    // deployed instance would have no working sign-in at all — fail at startup, where the
    // reason is visible, rather than at the login screen where it looks like a bug. Test mode
    // and local development are the two cases where that is deliberate: the code is written to
    // the log instead of being emailed.
    if (!testMode.Enabled && !builder.Environment.IsDevelopment() && !isOpenApiBuild)
    {
        throw new InvalidOperationException(
            "No email provider is configured, so no one could sign in. Set Smtp:Host (and Smtp:From) " +
            "to send sign-in codes, or set TestMode:Enabled=true to evaluate the application with a " +
            "single shared account.");
    }

    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
    Console.WriteLine(testMode.Enabled
        ? "[Startup] Email sender: logging (test mode — sign-in codes appear in this log)"
        : "[Startup] Email sender: logging (no provider configured)");
}
builder.Services.AddScoped<ILoginCodeStore, DbLoginCodeStore>();
builder.Services.AddScoped<EmailVerificationService>();

// Rate limit the anonymous auth endpoints (per client IP) to blunt code enumeration and
// email-bombing. The service-level resend throttle and attempt lockout are the second layer.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(Program.AuthRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// Fail closed: every endpoint requires an authenticated user unless it opts out
// with [AllowAnonymous]. Without this, a new controller/action added without a
// manual auth check would be anonymously reachable by default.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // The validation worker authenticates with the ApiKey scheme and must be in the Worker role.
    options.AddPolicy(ApiKeyAuthenticationHandler.Worker, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireRole(ApiKeyAuthenticationHandler.Worker));
});

// Add services to the container.
builder.Services.AddScoped<NodeSetEditor.Server.Controllers.NodeIdSlugFilter>();
builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

// The document is emitted to OpenApi\v1.json at build time (see the OpenApiGenerateDocuments
// properties in the .csproj) and checked in. 3.0 rather than 3.1 because the client generators
// and tooling around OPC UA specs still handle 3.0 far more reliably.
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
    options.AddDocumentTransformer<NodeSetEditor.Server.Services.ApiInfoDocumentTransformer>();
    options.AddOperationTransformer<NodeSetEditor.Server.Services.SecurityRequirementOperationTransformer>();
});

var app = builder.Build();

// Security response headers on every response (API and SPA assets alike).
// Only frame-ancestors is ENFORCED via CSP for now (clickjacking — browsers
// ignore frame-ancestors in report-only policies, hence the split). The full
// content policy ships Report-Only first: violations show in the browser
// console without breaking anything; once it has soaked, promote it to the
// enforced Content-Security-Policy header.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";          // never MIME-sniff (we serve user-supplied nodeset bytes back out)
    headers["X-Frame-Options"] = "DENY";                    // legacy-browser fallback for frame-ancestors
    headers["Referrer-Policy"] = "no-referrer";             // help system opens attacker-chosen doc URLs; don't leak our URLs
    headers["Content-Security-Policy"] = "frame-ancestors 'none'";
    headers["Content-Security-Policy-Report-Only"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +              // MUI/Emotion injects inline styles
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' https://login.microsoftonline.com; " + // MSAL token endpoints
        "frame-src https://login.microsoftonline.com; " +   // MSAL silent token renewal iframe
        "object-src 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

// utilities.opcfoundation.org is a vanity host that serves no app content of its own:
//   /account (or /account/) -> the member portal's access-request flow
//   any other path          -> the NodeSet Editor at uanodeseteditor.opcfoundation.org, path dropped
// Host-gated, so no other domain (production, staging) is affected. Runs before auth so it never
// 401s. 302 (temporary) so the targets can change without browsers caching them permanently.
app.Use(async (context, next) =>
{
    if (string.Equals(context.Request.Host.Host, "utilities.opcfoundation.org", StringComparison.OrdinalIgnoreCase))
    {
        var path = context.Request.Path;
        var isAccount = path.Equals("/account", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/account/", StringComparison.OrdinalIgnoreCase);
        context.Response.Redirect(
            isAccount
                ? "https://memberportal.opcfoundation.org/api/access-request"
                : "https://uanodeseteditor.opcfoundation.org/",
            permanent: false);
        return;
    }
    await next();
});

app.UseDefaultFiles();
// The SPA's static assets must be served to anonymous users — otherwise the
// global RequireAuthenticatedUser fallback policy (see AddAuthorization) would
// 401 the JS/CSS before the user can reach the Azure AD login flow.
app.MapStaticAssets().AllowAnonymous();

// Send HSTS in deployed environments only — localhost / the dev certificate
// should not pin Strict-Transport-Security in the developer's browser.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// Dev auth bypass — must run BEFORE UseAuthentication so fake claims are set.
// Hard-gated on the Development environment so it can NEVER be enabled in a
// deployed environment (staging/production), even if DevAuth:Enabled is set via
// a stray env var or mis-copied config. Inside Development it still honors the flag.
if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DevAuth:Enabled"))
{
    app.UseMiddleware<DevAuthMiddleware>();
}

// 2. Enable Authentication & Authorization
app.UseAuthentication(); // Checks if the token is valid
app.UseAuthorization();  // Checks if the user has permission

app.UseRateLimiter();    // Enforces per-IP limits on the [EnableRateLimiting] auth endpoints

// API documentation. The spec is OpenApi\v1.json — generated from the controllers at build time
// and checked into the repository — so what is served here is exactly what the build produced;
// nothing is generated at runtime. None of these routes calls AllowAnonymous, so the global
// RequireAuthenticatedUser fallback policy applies and the documentation, including the shape of
// every request body, is visible only to a signed-in user. They are mapped as explicit endpoints
// rather than served from wwwroot precisely because wwwroot is served anonymously, and they are
// excluded from the description so the documentation plumbing doesn't document itself.
var docsRoot = Path.Combine(app.Environment.ContentRootPath, "OpenApi");

app.MapGet("/openapi/{document}.json", (string document) =>
{
    // GetFileName strips any traversal in the route value before it reaches the filesystem.
    var path = Path.Combine(docsRoot, Path.GetFileName(document) + ".json");
    return File.Exists(path)
        ? Results.File(path, "application/json; charset=utf-8")
        : Results.NotFound();
}).ExcludeFromDescription();

app.MapGet("/swagger", () => Results.File(Path.Combine(docsRoot, "index.html"), "text/html; charset=utf-8"))
   .ExcludeFromDescription();

app.MapGet("/swagger/{file}", (string file) =>
{
    var name = Path.GetFileName(file);
    var path = Path.Combine(docsRoot, "swagger-ui", name);
    if (!File.Exists(path)) return Results.NotFound();

    var contentType = Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
    return Results.File(path, contentType);
}).ExcludeFromDescription();

// Deployment shape, read by the SPA before it renders. This is what lets one image serve a
// deployment with Azure AD and one without: the client cannot know at build time which features
// exist, so it asks. Anonymous by necessity — it is read before anyone has signed in — and it
// names only which features are switched on, never any credential or endpoint.
app.MapGet("/api/config", (TestModeOptions testModeOptions, IConfiguration configuration) => Results.Ok(new
{
    authProviders = azureAdEnabled ? new[] { "email", "azuread" } : new[] { "email" },
    testMode = testModeOptions.Enabled,
    cloudLibraryEnabled = !string.IsNullOrEmpty(configuration["CloudLibraryUrl"]),
})).AllowAnonymous().ExcludeFromDescription();

// Liveness + readiness for container orchestrators: the process is up and the database answers.
app.MapGet("/health", async (NodeSetEditorDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "healthy" })
            : Results.Json(new { status = "unhealthy", reason = "database unreachable" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        // The reason is deliberately coarse: this endpoint is anonymous, and a connection
        // exception carries the host, database and user name.
        app.Logger.LogError(ex, "Health check failed to reach the database");
        return Results.Json(new { status = "unhealthy", reason = "database unreachable" }, statusCode: 503);
    }
}).AllowAnonymous().ExcludeFromDescription();

app.MapControllers();

// The SPA shell (index.html) must load anonymously so an unauthenticated user
// can bootstrap the app and sign in; the fallback authorization policy would
// otherwise return 401 for the page itself. API controllers stay fail-closed.
app.MapFallbackToFile("/index.html").AllowAnonymous();

app.Run();

// Expose for WebApplicationFactory in integration tests
public partial class Program
{
    /// <summary>Rate-limiter policy name applied to the anonymous email-code auth endpoints.</summary>
    public const string AuthRateLimitPolicy = "auth";
}
