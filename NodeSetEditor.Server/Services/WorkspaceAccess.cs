using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Shared workspace access predicates so controllers enforce the same owner/ACL rules.
    /// Mirrors the checks in <c>UaRestApiController.CanWrite</c> / <c>GetAccessibleWorkspace</c>:
    /// the owner may read and write; ACL (shared) members may read only.
    /// Identity is email-keyed, so both the owner column and the ACL are lowercased emails.
    /// </summary>
    public static class WorkspaceAccess
    {
        /// <summary>Owner-only write.</summary>
        public static bool CanWrite(Workspace workspace, AuthenticatedUser user)
            => !string.IsNullOrEmpty(user.UserId) && workspace.Owner == user.UserId;

        /// <summary>Owner or an ACL member (by lowercased email) may access.</summary>
        public static bool HasAccess(Workspace workspace, AuthenticatedUser user)
        {
            if (!string.IsNullOrEmpty(user.UserId) && workspace.Owner == user.UserId) return true;
            var emailLower = user.Email?.ToLowerInvariant();
            return emailLower != null && workspace.Acl != null && workspace.Acl.Contains(emailLower);
        }
    }
}
