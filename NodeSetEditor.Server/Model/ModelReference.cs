namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// A reference to a model in a workspace.
    /// </summary>
    public class ModelReference
    {
        /// <summary>
        /// The unique identifier of the model.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Indicates whether the model is private to this workspace (exists only in the workspace's models.json).
        /// If false, the model is from the global nodesets index.
        /// </summary>
        public bool IsPrivate { get; set; }

        /// <summary>
        /// Indicates whether the model has been checked out for editing in this workspace.
        /// Editing requires both <see cref="IsPrivate"/> and <see cref="IsEditable"/>.
        /// </summary>
        public bool IsEditable { get; set; }
    }
}
