using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Reads the conformance units a model's nodes declare. Callers pass an already-resolved
    /// model id; the (workspace, model) linkage is checked by the controller, as everywhere
    /// else. The profile group these are assessed against is model metadata, edited through
    /// the model-update path (<c>INodeSetStorageService.UpdateModelInfoAsync</c>).
    /// </summary>
    public interface IConformanceUnitService
    {
        /// <summary>
        /// Every distinct conformance unit declared anywhere in the model, name-sorted, each
        /// with the number of nodes declaring it. Empty when the model declares none.
        /// </summary>
        Task<IReadOnlyList<ConformanceUnitInfo>> ListAsync(Guid modelId);
    }
}
