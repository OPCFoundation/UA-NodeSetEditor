extern alias JsonNodeSet;

using System.Collections.Concurrent;
using NodeSetEditor.Server.Model;
using JsonNodeSet::NodeSetTool;
using JsonNodeSet::Opc.Ua.JsonNodeSet;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Cached result of building an address space for a workspace.
    /// Tracks both the successfully loaded address space and any
    /// models that failed to load.
    /// </summary>
    public class WorkspaceAddressSpaceResult
    {
        public AddressSpace AddressSpace { get; init; } = new();

        /// <summary>
        /// Model URIs that failed to load (parse error, missing deps, etc.).
        /// These models exist in workspace storage but are not in the address space.
        /// Key = model URI, Value = error message.
        /// </summary>
        public Dictionary<string, string> BadModels { get; init; } = new();

        /// <summary>
        /// Direct RequiredModel URIs per model URI, as declared in the nodeset XML/DB.
        /// Used to compute transitive dependency graphs for cycle detection.
        /// Key = model URI, Value = list of directly required model URIs.
        /// </summary>
        public Dictionary<string, List<string>> ModelDependencies { get; init; } = new();
    }

    public class WorkspaceAddressSpaceService : IWorkspaceAddressSpaceService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WorkspaceAddressSpaceService> _logger;
        private readonly ConcurrentDictionary<Guid, Lazy<Task<WorkspaceAddressSpaceResult>>> _cache = new();
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

        public WorkspaceAddressSpaceService(
            IServiceScopeFactory scopeFactory,
            ILogger<WorkspaceAddressSpaceService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<AddressSpace> GetAddressSpaceAsync(Guid workspaceId)
        {
            var result = await GetResultAsync(workspaceId);
            return result.AddressSpace;
        }

        public async Task<Dictionary<string, string>> GetBadModelsAsync(Guid workspaceId)
        {
            var result = await GetResultAsync(workspaceId);
            return result.BadModels;
        }

        public async Task<Dictionary<string, List<string>>> GetModelDependenciesAsync(Guid workspaceId)
        {
            var result = await GetResultAsync(workspaceId);
            return result.ModelDependencies;
        }

        private async Task<WorkspaceAddressSpaceResult> GetResultAsync(Guid workspaceId)
        {
            var lazy = _cache.GetOrAdd(workspaceId,
                _ => new Lazy<Task<WorkspaceAddressSpaceResult>>(() => BuildAddressSpaceAsync(workspaceId)));
            return await lazy.Value;
        }

        public async Task AddModelAsync(Guid workspaceId, Guid modelId, bool isPrivate)
        {
            var result = await GetResultAsync(workspaceId);
            var semaphore = _locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var storage = scope.ServiceProvider.GetRequiredService<INodeSetStorageService>();

                // Authorization gate: a model may be linked into a workspace only if it is shared
                // (published or a Cloud Library copy) or a Cloud Library catalog model. Model rows
                // are shared across workspaces and read by id, so without this an authenticated user
                // could link another user's PRIVATE model by id and read its contents. Report a 404
                // (not 403) so the response can't be used to probe for the existence of private ids.
                if (!await storage.IsModelLinkableAsync(modelId))
                    throw new KeyNotFoundException($"Model '{modelId}' not found.");

                var workspace = await storage.GetWorkspaceAsync(workspaceId)
                    ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");

                workspace.Models ??= new List<ModelReference>();

                // Load the primary model's XML and parse it.
                // GetSharedModelFileStreamAsync may resolve a file-catalog GUID to a DB GUID.
                Stream primaryFileStream;
                ModelInfo primaryModelInfo;
                if (!isPrivate)
                {
                    (primaryFileStream, primaryModelInfo) = await storage.GetSharedModelFileStreamAsync(modelId);
                }
                else
                {
                    // Private model: temporarily add to workspace so GetModelFileStreamAsync can find it,
                    // then remove so we can add it properly after deps are resolved.
                    workspace.Models.Add(new ModelReference { Id = modelId, IsPrivate = true });
                    await storage.UpdateWorkspaceModelsAsync(workspaceId, workspace.Models);
                    (primaryFileStream, primaryModelInfo) = await storage.GetModelFileStreamAsync(workspaceId, modelId);
                    workspace.Models.RemoveAll(m => m.Id == modelId);
                    await storage.UpdateWorkspaceModelsAsync(workspaceId, workspace.Models);
                }

                // Check for duplicate using the resolved DB ID
                var resolvedId = primaryModelInfo.Id ?? modelId;
                if (workspace.Models.Any(m => m.Id == resolvedId))
                    throw new InvalidOperationException($"Model '{primaryModelInfo.ModelUri ?? resolvedId.ToString()}' is already in the workspace.");

                var primaryMs = new MemoryStream();
                using (primaryFileStream)
                {
                    await primaryFileStream.CopyToAsync(primaryMs);
                    primaryMs.Position = 0;
                }

                var primarySerializer = new NodeSetSerializer();
                primarySerializer.LoadXml(primaryMs);

                // Build set of model URIs already in the workspace
                var existingModels = await storage.GetWorkspaceModelsAsync(workspaceId);
                var workspaceModelUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in existingModels)
                {
                    if (m?.ModelUri != null)
                        workspaceModelUris.Add(m.ModelUri);
                }

                // Load shared model index for dependency resolution
                var sharedIndex = await storage.GetSharedModelsAsync();

                // Resolve transitive dependencies
                var resolvedDeps = new List<(ModelInfo Info, MemoryStream Stream, NodeSetSerializer Serializer)>();
                var resolvedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await ResolveDependenciesAsync(primarySerializer, workspaceModelUris, sharedIndex,
                    resolvedDeps, resolvedUris, storage);

                // Add resolved dependencies to workspace (isPrivate=false, they come from shared index)
                foreach (var dep in resolvedDeps)
                {
                    if (dep.Info.Id == null) continue;
                    if (workspace.Models.Any(m => m.Id == dep.Info.Id.Value)) continue;

                    workspace.Models.Add(new ModelReference { Id = dep.Info.Id.Value, IsPrivate = false });
                    _logger.LogInformation(
                        "Auto-added dependency '{ModelUri}' ({ModelId}) to workspace {WorkspaceId}",
                        dep.Info.ModelUri, dep.Info.Id, workspaceId);
                }

                // Add the primary model (use the resolved DB ID, which may differ from
                // the catalog GUID when importing from the file-based cloud library)
                var resolvedModelId = primaryModelInfo.Id ?? modelId;
                workspace.Models.Add(new ModelReference { Id = resolvedModelId, IsPrivate = isPrivate });
                await storage.UpdateWorkspaceModelsAsync(workspaceId, workspace.Models);

                // Collect all newly added models for loading into address space
                var newModels = new Dictionary<string, (MemoryStream Stream, NodeSetSerializer Serializer, ModelInfo Info)>();

                foreach (var dep in resolvedDeps)
                {
                    if (dep.Info.ModelUri != null)
                        newModels[dep.Info.ModelUri] = (dep.Stream, dep.Serializer, dep.Info);
                }

                if (primaryModelInfo.ModelUri != null)
                    newModels[primaryModelInfo.ModelUri] = (primaryMs, primarySerializer, primaryModelInfo);

                // Topological sort and load into address space
                var sorted = TopologicalSort(newModels);
                var addRetryQueue = new List<string>();

                foreach (var modelUri in sorted)
                {
                    if (!newModels.TryGetValue(modelUri, out var entry)) continue;

                    try
                    {
                        entry.Serializer.LoadInto(result.AddressSpace);
                        result.BadModels.Remove(modelUri);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Model '{ModelUri}' failed to load, queuing for retry", modelUri);
                        addRetryQueue.Add(modelUri);
                    }
                }

                foreach (var modelUri in addRetryQueue)
                {
                    if (!newModels.TryGetValue(modelUri, out var entry)) continue;
                    try
                    {
                        entry.Serializer.LoadInto(result.AddressSpace);
                        result.BadModels.Remove(modelUri);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Model '{ModelUri}' failed to load into address space, flagging as bad",
                            modelUri);
                        result.BadModels[modelUri] = ex.Message;
                    }
                }

                // Re-compute DataTypeForm after new models are loaded
                result.AddressSpace.ComputeDataTypeForms();
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task ResolveDependenciesAsync(
            NodeSetSerializer serializer,
            HashSet<string> workspaceModelUris,
            List<ModelInfo> sharedIndex,
            List<(ModelInfo Info, MemoryStream Stream, NodeSetSerializer Serializer)> resolved,
            HashSet<string> resolvedUris,
            INodeSetStorageService storage)
        {
            foreach (var model in serializer.Models)
            {
                if (model.RequiredModels == null) continue;

                foreach (var req in model.RequiredModels)
                {
                    if (req.ModelUri == null) continue;

                    // Skip if already in workspace or already resolved in this pass
                    if (workspaceModelUris.Contains(req.ModelUri)) continue;
                    if (resolvedUris.Contains(req.ModelUri)) continue;

                    // Try to find a match in the shared index
                    var pubDateStr = req.PublicationDate?.ToString("o");
                    var version = req.ModelVersion ?? req.VarVersion;
                    var matches = ModelInfo.FindMatches(sharedIndex, req.ModelUri, pubDateStr, version).ToList();

                    if (matches.Count > 0)
                    {
                        var match = matches.First();
                        if (match.Id == null) continue;

                        resolvedUris.Add(req.ModelUri);

                        // Load the dependency's XML from shared index
                        try
                        {
                            var (depStream, _) = await storage.GetSharedModelFileStreamAsync(match.Id.Value);
                            var depMs = new MemoryStream();
                            using (depStream)
                            {
                                await depStream.CopyToAsync(depMs);
                                depMs.Position = 0;
                            }

                            var depSerializer = new NodeSetSerializer();
                            depSerializer.LoadXml(depMs);

                            resolved.Add((match, depMs, depSerializer));

                            // Recurse for transitive deps
                            await ResolveDependenciesAsync(depSerializer, workspaceModelUris, sharedIndex,
                                resolved, resolvedUris, storage);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to load dependency '{ModelUri}' from shared index",
                                req.ModelUri);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Required model '{ModelUri}' (version={Version}, date={Date}) not found in shared index",
                            req.ModelUri, version, pubDateStr);
                    }
                }
            }
        }

        public async Task RemoveModelAsync(Guid workspaceId, string modelUri)
        {
            var result = await GetResultAsync(workspaceId);
            var semaphore = _locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            try
            {
                // If the model is in the address space, remove it (validates dependencies).
                // If it's a bad model (not in address space), skip the address space removal.
                if (!result.BadModels.ContainsKey(modelUri))
                {
                    result.AddressSpace.RemoveModel(modelUri);
                }
                else
                {
                    result.BadModels.Remove(modelUri);
                }

                // Update workspace model list
                using var scope = _scopeFactory.CreateScope();
                var storage = scope.ServiceProvider.GetRequiredService<INodeSetStorageService>();

                var models = await storage.GetWorkspaceModelsAsync(workspaceId);
                var workspace = await storage.GetWorkspaceAsync(workspaceId)
                    ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");

                var modelRef = workspace.Models?
                    .Where(mr =>
                    {
                        var info = models.FirstOrDefault(m => m?.Id == mr.Id);
                        return info?.ModelUri == modelUri;
                    })
                    .FirstOrDefault();

                if (modelRef != null)
                {
                    workspace.Models!.Remove(modelRef);
                    await storage.UpdateWorkspaceModelsAsync(workspaceId, workspace.Models!);
                    // A private working copy with no remaining workspace links is unreachable —
                    // delete it (and its nodes/references) so removed models don't accumulate as
                    // orphan rows (which also collide on the unique (Uri, VersionNorm) index).
                    await storage.DeleteModelIfOrphanedAsync(modelRef.Id);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<string> GetNextNodeIdAsync(Guid workspaceId, string modelUri)
        {
            var addressSpace = await GetAddressSpaceAsync(workspaceId);
            var prefix = $"nsu={modelUri};i=";
            long maxId = 0;

            foreach (var node in addressSpace.Nodes)
            {
                if (node.NodeId != null && node.NodeId.StartsWith(prefix))
                {
                    var suffix = node.NodeId.Substring(prefix.Length);
                    if (long.TryParse(suffix, out var numericId) && numericId > maxId)
                        maxId = numericId;
                }
            }

            return (maxId + 1).ToString();
        }

        public void Invalidate(Guid workspaceId)
        {
            _cache.TryRemove(workspaceId, out _);
            _locks.TryRemove(workspaceId, out _);
        }

        private async Task<WorkspaceAddressSpaceResult> BuildAddressSpaceAsync(Guid workspaceId)
        {
            using var scope = _scopeFactory.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<INodeSetStorageService>();

            var workspace = await storage.GetWorkspaceAsync(workspaceId)
                ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");

            var modelInfos = await storage.GetWorkspaceModelsAsync(workspaceId);

            var badModelUris = new Dictionary<string, string>();

            // Load all model streams and parse via NodeSetSerializer for dependency metadata.
            // Models that fail to parse are flagged as bad immediately.
            var modelData = new Dictionary<string, (MemoryStream Stream, NodeSetSerializer Serializer, ModelInfo Info)>();

            foreach (var modelInfo in modelInfos)
            {
                if (modelInfo?.Id == null || modelInfo.ModelUri == null) continue;

                try
                {
                    var (fileStream, _) = await storage.GetModelFileStreamAsync(workspaceId, modelInfo.Id.Value);
                    using (fileStream)
                    {
                        var ms = new MemoryStream();
                        await fileStream.CopyToAsync(ms);
                        ms.Position = 0;

                        var serializer = new NodeSetSerializer();
                        serializer.LoadXml(ms);

                        modelData[modelInfo.ModelUri] = (ms, serializer, modelInfo);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Model '{ModelUri}' ({ModelId}) failed to parse, flagging as bad",
                        modelInfo.ModelUri, modelInfo.Id);
                    badModelUris[modelInfo.ModelUri] = ex.Message;
                }
            }

            // Topological sort by dependencies
            var sorted = TopologicalSort(modelData);

            // Build address space — load each model individually,
            // catching errors so one bad model doesn't block the rest.
            var addressSpace = new AddressSpace();
            var loadedCount = 0;
            var retryQueue = new List<string>();

            foreach (var modelUri in sorted)
            {
                if (!modelData.TryGetValue(modelUri, out var entry)) continue;

                try
                {
                    entry.Serializer.LoadInto(addressSpace);
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Model '{ModelUri}' failed to load into address space, queuing for retry",
                        modelUri);
                    retryQueue.Add(modelUri);
                }
            }

            // Retry models that failed — a second pass handles cases where
            // the topological sort could not resolve a dependency order (e.g.
            // circular RequiredModel declarations caused by stale references).
            foreach (var modelUri in retryQueue)
            {
                if (!modelData.TryGetValue(modelUri, out var entry)) continue;

                try
                {
                    entry.Serializer.LoadInto(addressSpace);
                    loadedCount++;
                    _logger.LogInformation(
                        "Model '{ModelUri}' loaded successfully on retry", modelUri);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Model '{ModelUri}' failed to load into address space, flagging as bad",
                        modelUri);
                    badModelUris[modelUri] = ex.Message;
                }
            }

            // Pre-compute DataTypeForm for all UADataType nodes
            addressSpace.ComputeDataTypeForms();

            // Defensive final canonicalization. NodeSetSerializer.LoadInto
            // already runs ResolveVariants per-model, but if a per-model load
            // throws AFTER AddNodeSet but BEFORE ResolveVariants (e.g., a
            // ComputeDataTypeForms hiccup or — historically — the cross-
            // document XML bug in VariantConverter), the model's nodes end up
            // in the AddressSpace with their variant DOMs uncanonicalized:
            // struct fields stay in the schema-free reader's heuristic types
            // (a Float written in fixed-point comes back as JValue<string>
            // because there is no '.' / 'e' / 'E') and round-trip to the API
            // as JSON strings. Re-running ResolveVariants on the fully built
            // AddressSpace catches any models that didn't get canonicalized
            // earlier; it's idempotent for models that did.
            try
            {
                addressSpace.ResolveVariants();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Final ResolveVariants pass threw for workspace {WorkspaceId}; " +
                    "some variant values may render uncanonicalized.", workspaceId);
            }

            _logger.LogInformation(
                "Built AddressSpace for workspace {WorkspaceId}: {LoadedCount}/{TotalCount} models loaded, {BadCount} bad, {NodeCount} nodes",
                workspaceId, loadedCount, sorted.Count, badModelUris.Count, addressSpace.NodeCount);

            // Collect direct RequiredModel URIs per model for cycle-detection on the client.
            // These come from the serializer's parsed m_models (before AddNodeSet strips unknown
            // URIs), so they reflect what is actually stored in the DB.
            var modelDependencies = new Dictionary<string, List<string>>();
            foreach (var kvp in modelData)
            {
                var reqs = new List<string>();
                foreach (var model in kvp.Value.Serializer.Models)
                {
                    if (model.RequiredModels == null) continue;
                    foreach (var req in model.RequiredModels)
                        if (req.ModelUri != null) reqs.Add(req.ModelUri);
                }
                modelDependencies[kvp.Key] = reqs;
            }

            return new WorkspaceAddressSpaceResult
            {
                AddressSpace = addressSpace,
                BadModels = badModelUris,
                ModelDependencies = modelDependencies
            };
        }

        private static List<string> TopologicalSort(
            Dictionary<string, (MemoryStream Stream, NodeSetSerializer Serializer, ModelInfo Info)> models)
        {
            // Build dependency graph from NodeSetSerializer.Models (which have RequiredModels)
            var deps = new Dictionary<string, HashSet<string>>();
            foreach (var kvp in models)
            {
                var required = new HashSet<string>();
                foreach (var model in kvp.Value.Serializer.Models)
                {
                    if (model.RequiredModels != null)
                    {
                        foreach (var req in model.RequiredModels)
                        {
                            if (req.ModelUri != null && models.ContainsKey(req.ModelUri))
                                required.Add(req.ModelUri);
                        }
                    }
                }
                deps[kvp.Key] = required;
            }

            // Kahn's algorithm
            var result = new List<string>();
            var available = new Queue<string>(deps.Where(d => d.Value.Count == 0).Select(d => d.Key));

            while (available.Count > 0)
            {
                var current = available.Dequeue();
                result.Add(current);

                foreach (var kvp in deps)
                {
                    if (kvp.Value.Remove(current) && kvp.Value.Count == 0)
                        available.Enqueue(kvp.Key);
                }
            }

            // Add any remaining (cycle or missing deps) at the end
            foreach (var key in models.Keys)
            {
                if (!result.Contains(key))
                    result.Add(key);
            }

            return result;
        }
    }
}
