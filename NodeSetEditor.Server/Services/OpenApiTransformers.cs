using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Fills in the document-level metadata that cannot be derived from the controllers: the
    /// <c>info</c> block and the three authentication schemes the API accepts. Without this the
    /// generated spec has no <c>securitySchemes</c> at all, so a reader cannot tell how to
    /// authenticate and the Swagger UI "Authorize" button has nothing to offer.
    /// </summary>
    public sealed class ApiInfoDocumentTransformer : IOpenApiDocumentTransformer
    {
        public const string BearerScheme = "AzureAd";
        public const string CookieScheme = "EmailCookie";

        public Task TransformAsync(
            OpenApiDocument document,
            OpenApiDocumentTransformerContext context,
            CancellationToken cancellationToken)
        {
            document.Info = new OpenApiInfo
            {
                Title = "OPC UA NodeSet Editor API",
                Version = "v1",
                Description =
                    "REST API for the OPC UA NodeSet Editor.\n\n" +
                    "Most endpoints act on a workspace identified by the `OpcUa-Server` header, and " +
                    "NodeIds appearing in a URL path are base64url-encoded rather than percent-encoded.\n\n" +
                    "This document is generated from the controllers at build time and checked into the " +
                    "repository; it is not produced at runtime.",
                Contact = new OpenApiContact
                {
                    Name = "OPC Foundation",
                    Url = new Uri("https://opcfoundation.org"),
                },
            };

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

            document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Azure AD access token — the \"sign in with Microsoft\" path.",
            };

            document.Components.SecuritySchemes[CookieScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = EmailAuthCookie.CookieName,
                Description =
                    "Session cookie issued by the passwordless email-code sign-in " +
                    "(`POST /api/auth/verify-code`). Sent automatically by the browser.",
            };

            // The validation worker's X-Api-Key scheme is deliberately absent: those endpoints are
            // hidden from this document, so advertising the scheme here would describe nothing.

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Marks each operation with the authentication it actually requires. The server fails closed —
    /// a global RequireAuthenticatedUser fallback policy covers everything that does not opt out with
    /// <see cref="AllowAnonymousAttribute"/> — so the default is "credentials required" and the
    /// anonymous endpoints are the exceptions that get an explicit empty requirement.
    /// </summary>
    public sealed class SecurityRequirementOperationTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(
            OpenApiOperation operation,
            OpenApiOperationTransformerContext context,
            CancellationToken cancellationToken)
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var isAnonymous = metadata.OfType<IAllowAnonymous>().Any();

            // An empty list means "no credentials needed" and overrides any document-level default;
            // omitting the property entirely would instead inherit it, which is the opposite.
            operation.Security = isAnonymous
                ? new List<OpenApiSecurityRequirement>()
                : RequirementsFor(context.Document);

            return Task.CompletedTask;
        }

        private static List<OpenApiSecurityRequirement> RequirementsFor(OpenApiDocument? document)
        {
            // The reference must be constructed against the host document; without it the scheme
            // name cannot be resolved and the requirement serializes as an empty object.
            // Separate requirement entries are OR-ed by OpenAPI: either sign-in path is accepted.
            OpenApiSecurityRequirement Require(string scheme) =>
                new() { [new OpenApiSecuritySchemeReference(scheme, document)] = new List<string>() };

            return new List<OpenApiSecurityRequirement>
            {
                Require(ApiInfoDocumentTransformer.BearerScheme),
                Require(ApiInfoDocumentTransformer.CookieScheme),
            };
        }
    }
}
