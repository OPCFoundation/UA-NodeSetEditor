extern alias JsonNodeSet;

using JsonNodeSet::Opc.Ua.NodeSetSerializer;

namespace NodeSetEditor.Server.Services
{
    public interface IWorkspaceAddressSpaceService
    {
        Task<AddressSpace> GetAddressSpaceAsync(Guid workspaceId);
        Task<Dictionary<string, string>> GetBadModelsAsync(Guid workspaceId);

        /// <summary>Icon concept key per type NodeId; cached and invalidated with the address space.</summary>
        Task<Dictionary<string, string>> GetNodeIconsAsync(Guid workspaceId);
        Task<Dictionary<string, List<string>>> GetModelDependenciesAsync(Guid workspaceId);
        Task AddModelAsync(Guid workspaceId, Guid modelId, bool isPrivate);
        /// <summary>
        /// Drop a namespace from the workspace's address space and unlink it.
        /// <paramref name="allPrivateVersions"/> additionally takes the stored versions the
        /// workspace privately holds for the URI (deleting the model, rather than replacing the
        /// version in use) — see INodeSetStorageService.RemoveModelFromWorkspaceAsync.
        /// </summary>
        Task RemoveModelAsync(Guid workspaceId, string modelUri, bool allPrivateVersions = false);
        Task<string> GetNextNodeIdAsync(Guid workspaceId, string modelUri);
        void Invalidate(Guid workspaceId);
    }
}
