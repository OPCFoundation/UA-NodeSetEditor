extern alias JsonNodeSet;

using JsonNodeSet::Opc.Ua.NodeSetSerializer;

namespace NodeSetEditor.Server.Services
{
    public interface IWorkspaceAddressSpaceService
    {
        Task<AddressSpace> GetAddressSpaceAsync(Guid workspaceId);
        Task<Dictionary<string, string>> GetBadModelsAsync(Guid workspaceId);
        Task<Dictionary<string, List<string>>> GetModelDependenciesAsync(Guid workspaceId);
        Task AddModelAsync(Guid workspaceId, Guid modelId, bool isPrivate);
        Task RemoveModelAsync(Guid workspaceId, string modelUri);
        Task<string> GetNextNodeIdAsync(Guid workspaceId, string modelUri);
        void Invalidate(Guid workspaceId);
    }
}
