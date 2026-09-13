using System.Security.Claims;
using Microsoft.Identity.Web;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Represents the authenticated user identity for API requests.
    /// Named AuthenticatedUser to avoid conflict with Opc.Ua.UserIdentity.
    /// </summary>
    public sealed class AuthenticatedUser
    {
        public static readonly AuthenticatedUser Anonymous = new()
        {
            IsAuthenticated = false,
            DisplayName = "Anonymous"
        };

        public bool IsAuthenticated { get; init; }
        public string? UserId { get; init; }
        public string? Email { get; init; }
        public string? DisplayName { get; init; }
        public string? TenantId { get; init; }
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
        public ClaimsPrincipal? Principal { get; init; }

        /// <summary>
        /// Creates an AuthenticatedUser from a ClaimsPrincipal (used by the REST controllers).
        /// </summary>
        public static AuthenticatedUser FromClaimsPrincipal(ClaimsPrincipal? principal)
        {
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return Anonymous;
            }

            // Use Microsoft.Identity.Web helpers — they try both the short JWT claim
            // name ("oid"/"tid") and the mapped URI form, whichever is present.
            var email = principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
            var objectId = principal.GetObjectId() ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            // Identity is keyed on the lowercased email so the two sign-in paths converge on one
            // user: the Azure AD bearer path (email from preferred_username) and the email-code
            // cookie path (email claim) map to the same UserId, workspaces, and preferences. Fall
            // back to the AAD object id only for the rare token with no email claim.
            var userId = !string.IsNullOrEmpty(email) ? email.ToLowerInvariant() : objectId;
            // Prefer the AAD "name" claim (the human display name the toolbar shows via
            // account.name) over GetDisplayName(), which returns preferred_username first —
            // that is typically the email/UPN, so it would surface the email as the display name.
            var name = principal.FindFirst(ClaimConstants.Name)?.Value ?? principal.FindFirst(ClaimTypes.Name)?.Value;
            var displayName = name ?? principal.GetDisplayName() ?? email;
            var tenantId = principal.GetTenantId();
            var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

            return new AuthenticatedUser
            {
                IsAuthenticated = true,
                UserId = userId,
                Email = email,
                DisplayName = displayName ?? email ?? userId,
                TenantId = tenantId,
                Roles = roles,
                Principal = principal
            };
        }
    }
}
