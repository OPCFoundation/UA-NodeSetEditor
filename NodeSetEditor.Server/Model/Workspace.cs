namespace NodeSetEditor.Server.Model
{
    public class Workspace
    {
        public Guid? Id { get; set; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        public DateTime? CreateDate { get; set; }

        public DateTime? ModifyDate { get; set; }

        public List<ModelReference>? Models { get; set; }

        /// <summary>
        /// The userId (e.g. Azure AD oid) of the workspace creator.
        /// </summary>
        public string? Owner { get; set; }

        /// <summary>
        /// Email address of the workspace creator.
        /// </summary>
        public string? OwnerEmail { get; set; }

        /// <summary>
        /// Emails of users with access to this workspace (excludes owner).
        /// Null or empty when the workspace is not shared.
        /// </summary>
        public List<string>? Acl { get; set; }
    }
}
