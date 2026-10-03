using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Shared workspace access predicates so controllers enforce the same owner/ACL rules.
    /// Mirrors the checks in <c>UaRestApiController.CanWrite</c> / <c>GetAccessibleWorkspace</c>:
    /// the owner may read and write; ACL (shared) members may read only.
    ///
    /// Identity is email-keyed (<see cref="AuthenticatedUser.FromClaimsPrincipal"/> lowercases
    /// it), so every comparison here is case-insensitive rather than trusting that each stored
    /// Owner and ACL entry was written in that form. A row whose Owner differs only in case —
    /// written before the keying was lowercased, or by any path that skipped it — would
    /// otherwise silently stop being owned: the real owner is served isOwner/canWrite false and
    /// the whole workspace turns read-only, with no error to explain it.
    /// </summary>
    public static class WorkspaceAccess
    {
        /// <summary>Owner-only write.</summary>
        public static bool CanWrite(Workspace workspace, AuthenticatedUser user)
            => !string.IsNullOrEmpty(user.UserId)
               && string.Equals(workspace.Owner, user.UserId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Owner or an ACL member (by email) may access.</summary>
        public static bool HasAccess(Workspace workspace, AuthenticatedUser user)
        {
            if (CanWrite(workspace, user)) return true;
            var email = user.Email;
            return !string.IsNullOrEmpty(email) && workspace.Acl != null
                && workspace.Acl.Contains(email, StringComparer.OrdinalIgnoreCase);
        }
    }
}
