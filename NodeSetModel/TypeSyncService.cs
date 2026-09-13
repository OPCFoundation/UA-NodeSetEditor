using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace NodeSetEditor.Model
{
    /// <summary>
    /// Handles synchronization of denormalized type children trees.
    /// In-model changes cascade eagerly. Cross-model changes are detected
    /// via RequiredModels version comparison and applied on user request.
    /// </summary>
    public class TypeSyncService
    {
        private readonly NodeSetEditorDbContext _db;

        public TypeSyncService(NodeSetEditorDbContext db)
        {
            _db = db;
        }

        // ───────────────────────────────────────────────────────────
        // In-model eager cascade
        // ───────────────────────────────────────────────────────────

        /// <summary>
        /// After a type is saved, propagate changes to all in-model types
        /// that reference it in their children tree.
        /// Call this within the same transaction as the type save.
        /// </summary>
        public async Task CascadeInModelAsync(NodeSetType changedType)
        {
            // Find all in-model types that depend on this type's NodeId
            var dependentTypeIds = await _db.TypeDependencies
                .Where(d => d.ReferencedTypeNodeId == changedType.NodeId
                    && d.Type!.ModelId == changedType.ModelId)
                .Select(d => d.TypeId)
                .Distinct()
                .ToListAsync();

            if (dependentTypeIds.Count == 0) return;

            var dependentTypes = await _db.NodeSetTypes
                .Where(t => dependentTypeIds.Contains(t.Id))
                .ToListAsync();

            foreach (var depType in dependentTypes)
            {
                if (depType.Children == null) continue;

                var updated = SyncChildrenTree(depType.Children, changedType);
                depType.Children = updated;
            }

            // Cascade may have changed children that other types depend on — recurse
            // Use a visited set to prevent infinite loops
            var visited = new HashSet<string> { changedType.NodeId };
            foreach (var depType in dependentTypes)
            {
                if (visited.Add(depType.NodeId))
                {
                    await CascadeInModelAsync(depType);
                }
            }
        }

        /// <summary>
        /// Walk the children tree and sync nodes whose TypeDefinition matches the changed type.
        /// Returns the updated children JsonObject.
        /// </summary>
        private static JsonObject SyncChildrenTree(JsonObject children, NodeSetType changedType)
        {
            if (children["items"] is not JsonArray items) return children;

            var changedChildren = changedType.Children?["items"] as JsonArray;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not JsonObject child) continue;

                var typeDefId = child["typeDefinition"]?.GetValue<string>();

                if (typeDefId == changedType.NodeId)
                {
                    SyncChildNode(child, changedType, changedChildren);
                }

                // Always recurse into nested children
                if (child["children"] is JsonObject nestedChildren)
                {
                    SyncChildrenTree(nestedChildren, changedType);
                }
            }

            return children;
        }

        /// <summary>
        /// Sync a single child node against a changed type definition.
        /// Rules:
        ///   - Unmodified node (_modified=false): full attribute replace
        ///   - Modified node (_modified=true): keep own attributes, but:
        ///     - Add new mandatory children from type
        ///     - Remove children that no longer exist in type (if unmodified)
        ///     - Update existing unmodified children
        ///     - Recurse into modified children
        /// </summary>
        private static void SyncChildNode(
            JsonObject child,
            NodeSetType changedType,
            JsonArray? typeChildren)
        {
            bool isModified = child["_modified"]?.GetValue<bool>() == true;

            if (!isModified)
            {
                // Full replace of attributes from the type
                ReplaceAttributes(child, changedType);
            }

            // Sync children regardless of _modified status
            SyncChildList(child, typeChildren);
        }

        /// <summary>
        /// Replace a child node's attributes from the type definition,
        /// preserving origin markers, nodeId, browseName, and children.
        /// </summary>
        private static void ReplaceAttributes(JsonObject child, NodeSetType changedType)
        {
            // Attributes to update from the type (not exhaustive — extend as needed)
            SetIfPresent(child, "displayName", changedType.DisplayName);
            SetIfPresent(child, "description", GetStringProp(changedType.Children, "description"));

            // Copy type-level attributes that flow down to instances
            if (changedType.Children?["attributes"] is JsonObject typeAttrs)
            {
                child["attributes"] = typeAttrs.DeepClone();
            }
        }

        /// <summary>
        /// Sync the children list of a node against the type's children.
        /// Adds new mandatory, removes stale unmodified, updates existing unmodified.
        /// </summary>
        private static void SyncChildList(JsonObject parentNode, JsonArray? typeChildren)
        {
            if (typeChildren == null || typeChildren.Count == 0) return;

            // Ensure parent has a children container
            if (parentNode["children"] is not JsonObject childrenObj)
            {
                childrenObj = new JsonObject { ["items"] = new JsonArray() };
                parentNode["children"] = childrenObj;
            }

            if (childrenObj["items"] is not JsonArray existingItems)
            {
                existingItems = new JsonArray();
                childrenObj["items"] = existingItems;
            }

            // Index existing children by BrowseName for matching
            var existingByBrowseName = new Dictionary<string, (int index, JsonObject node)>(
                StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < existingItems.Count; i++)
            {
                if (existingItems[i] is JsonObject existing)
                {
                    var bn = existing["browseName"]?.GetValue<string>();
                    if (bn != null)
                        existingByBrowseName[bn] = (i, existing);
                }
            }

            // Track which BrowseNames exist in the type definition
            var typeBrowseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeChildNode in typeChildren)
            {
                if (typeChildNode is not JsonObject typeChild) continue;

                var bn = typeChild["browseName"]?.GetValue<string>();
                if (bn == null) continue;

                typeBrowseNames.Add(bn);

                var modellingRule = typeChild["modellingRule"]?.GetValue<string>();
                // Placeholders (MandatoryPlaceholder i=11510, OptionalPlaceholder
                // i=11508) are cardinality templates, not concrete instance children,
                // so they are never auto-added to existing instances on type sync.
                bool isMandatory = modellingRule == "i=78";

                if (existingByBrowseName.TryGetValue(bn, out var match))
                {
                    // Existing child — update if unmodified, recurse if modified
                    bool childModified = match.node["_modified"]?.GetValue<bool>() == true;

                    if (!childModified)
                    {
                        // Full replace from type child (preserve nodeId and origin markers)
                        var nodeId = match.node["nodeId"]?.DeepClone();
                        var origin = match.node["_origin"]?.DeepClone();
                        var sourceType = match.node["_sourceType"]?.DeepClone();

                        var replacement = typeChild.DeepClone() as JsonObject;
                        if (replacement != null)
                        {
                            replacement["nodeId"] = nodeId;
                            replacement["_origin"] = origin;
                            replacement["_sourceType"] = sourceType;
                            replacement["_modified"] = false;
                            existingItems[match.index] = replacement;
                        }
                    }
                    else
                    {
                        // Modified child — recurse into its children
                        var typeChildChildren = typeChild["children"]?["items"] as JsonArray;
                        SyncChildList(match.node, typeChildChildren);
                    }
                }
                else if (isMandatory)
                {
                    // New mandatory child from type — add it
                    var newChild = typeChild.DeepClone() as JsonObject;
                    if (newChild != null)
                    {
                        newChild["_origin"] = "type";
                        newChild["_sourceType"] = typeChild["_sourceType"]?.DeepClone()
                            ?? JsonValue.Create(typeChild["typeDefinition"]?.GetValue<string>());
                        newChild["_modified"] = false;
                        existingItems.Add(newChild);
                    }
                }
            }

            // Remove children from type that no longer exist (only if unmodified and origin=type)
            for (int i = existingItems.Count - 1; i >= 0; i--)
            {
                if (existingItems[i] is not JsonObject existing) continue;

                var origin = existing["_origin"]?.GetValue<string>();
                if (origin != "type") continue; // user-added children are never removed

                var bn = existing["browseName"]?.GetValue<string>();
                if (bn == null) continue;

                bool isModified = existing["_modified"]?.GetValue<bool>() == true;
                if (!isModified && !typeBrowseNames.Contains(bn))
                {
                    existingItems.RemoveAt(i);
                }
            }
        }

        // ───────────────────────────────────────────────────────────
        // Dependency tracking
        // ───────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuild the TypeDependency rows for a given type by scanning its children tree
        /// for TypeDefinition references to types in the same model.
        /// </summary>
        public async Task RebuildDependenciesAsync(NodeSetType type)
        {
            // Remove existing dependencies
            var existing = await _db.TypeDependencies
                .Where(d => d.TypeId == type.Id)
                .ToListAsync();
            _db.TypeDependencies.RemoveRange(existing);

            if (type.Children == null) return;

            // Collect all TypeDefinition NodeIds from the children tree
            var referencedTypeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectTypeDefinitions(type.Children, referencedTypeIds);

            // Only track in-model dependencies
            var inModelNodeIds = await _db.NodeSetTypes
                .Where(t => t.ModelId == type.ModelId && referencedTypeIds.Contains(t.NodeId))
                .Select(t => t.NodeId)
                .ToListAsync();

            foreach (var nodeId in inModelNodeIds)
            {
                _db.TypeDependencies.Add(new TypeDependency
                {
                    TypeId = type.Id,
                    ReferencedTypeNodeId = nodeId,
                });
            }
        }

        /// <summary>
        /// Recursively collect all typeDefinition values from a children tree.
        /// </summary>
        private static void CollectTypeDefinitions(JsonObject children, HashSet<string> result)
        {
            if (children["items"] is not JsonArray items) return;

            foreach (var item in items)
            {
                if (item is not JsonObject child) continue;

                var typeDef = child["typeDefinition"]?.GetValue<string>();
                if (typeDef != null)
                    result.Add(typeDef);

                if (child["children"] is JsonObject nested)
                    CollectTypeDefinitions(nested, result);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Cross-model staleness detection
        // ───────────────────────────────────────────────────────────

        /// <summary>
        /// Check a model's RequiredModels against current DB state.
        /// Returns list of stale dependencies with old and new version info.
        /// </summary>
        public async Task<List<StaleDependency>> DetectStaleDependenciesAsync(Guid modelId)
        {
            var model = await _db.Models
                .FirstOrDefaultAsync(m => m.Id == modelId);

            if (model?.Metadata == null) return new List<StaleDependency>();

            var requiredModels = model.Metadata["requiredModels"] as JsonArray;
            if (requiredModels == null) return new List<StaleDependency>();

            var stale = new List<StaleDependency>();

            foreach (var rm in requiredModels)
            {
                if (rm is not JsonObject required) continue;

                var uri = required["uri"]?.GetValue<string>()
                    ?? required["ModelUri"]?.GetValue<string>();
                var version = required["version"]?.GetValue<string>()
                    ?? required["Version"]?.GetValue<string>();
                var modelVersion = required["modelVersion"]?.GetValue<string>()
                    ?? required["ModelVersion"]?.GetValue<string>();

                if (uri == null) continue;

                // Normalize the stored required model version for comparison
                var requiredNorm = Model.NormalizeVersion(modelVersion ?? version);

                // Find the latest version of this model in DB
                var latest = await _db.Models
                    .Where(m => m.Uri == uri)
                    .OrderByDescending(m => m.VersionNorm)
                    .FirstOrDefaultAsync();

                if (latest == null) continue;

                // Simple string comparison on normalized versions
                bool isStale = requiredNorm != null && latest.VersionNorm != null
                    && string.Compare(latest.VersionNorm, requiredNorm, StringComparison.Ordinal) > 0;

                if (isStale)
                {
                    var affectedCount = await _db.TypeDependencies
                        .Join(_db.NodeSetTypes.Where(t => t.ModelId == latest.Id),
                            d => d.ReferencedTypeNodeId,
                            t => t.NodeId,
                            (d, t) => d)
                        .Where(d => d.Type!.ModelId == modelId)
                        .Select(d => d.TypeId)
                        .Distinct()
                        .CountAsync();

                    stale.Add(new StaleDependency
                    {
                        ModelUri = uri,
                        CurrentVersion = version,
                        LatestVersion = latest.Version,
                        LatestModelId = latest.Id,
                        AffectedTypeCount = affectedCount,
                    });
                }
            }

            return stale;
        }

        /// <summary>
        /// Apply cross-model sync for a specific dependency.
        /// Re-syncs all types in targetModelId that reference types from sourceModelId.
        /// Uses the same sync rules as in-model cascade.
        /// </summary>
        public async Task SyncCrossModelAsync(Guid targetModelId, Guid sourceModelId)
        {
            // Get all types from the source model (the one that changed)
            var sourceTypes = await _db.NodeSetTypes
                .Where(t => t.ModelId == sourceModelId)
                .ToListAsync();

            var sourceTypesByNodeId = sourceTypes.ToDictionary(t => t.NodeId, StringComparer.OrdinalIgnoreCase);

            // Get all types in the target model that reference source types
            var sourceNodeIds = sourceTypes.Select(t => t.NodeId).ToList();
            var affectedTypeIds = await _db.TypeDependencies
                .Where(d => d.Type!.ModelId == targetModelId
                    && sourceNodeIds.Contains(d.ReferencedTypeNodeId))
                .Select(d => d.TypeId)
                .Distinct()
                .ToListAsync();

            var affectedTypes = await _db.NodeSetTypes
                .Where(t => affectedTypeIds.Contains(t.Id))
                .ToListAsync();

            foreach (var type in affectedTypes)
            {
                if (type.Children == null) continue;

                foreach (var sourceType in sourceTypes)
                {
                    SyncChildrenTree(type.Children, sourceType);
                }

                // Rebuild dependencies since cross-model type NodeIds may have changed
                await RebuildDependenciesAsync(type);
            }

            // Update the RequiredModels entry to the new version
            await UpdateRequiredModelVersionAsync(targetModelId, sourceModelId);
        }

        /// <summary>
        /// Update the RequiredModels metadata entry for a dependency after sync.
        /// </summary>
        private async Task UpdateRequiredModelVersionAsync(Guid targetModelId, Guid sourceModelId)
        {
            var targetModel = await _db.Models.FirstOrDefaultAsync(m => m.Id == targetModelId);
            var sourceModel = await _db.Models.FirstOrDefaultAsync(m => m.Id == sourceModelId);

            if (targetModel?.Metadata == null || sourceModel == null) return;

            if (targetModel.Metadata["requiredModels"] is not JsonArray requiredModels) return;

            foreach (var rm in requiredModels)
            {
                if (rm is not JsonObject required) continue;
                if (required["uri"]?.GetValue<string>() != sourceModel.Uri) continue;

                required["version"] = sourceModel.Version;
                if (sourceModel.PublicationDate != null)
                    required["publicationDate"] = sourceModel.PublicationDate;
                break;
            }
        }

        // ───────────────────────────────────────────────────────────
        // Helpers
        // ───────────────────────────────────────────────────────────

        private static void SetIfPresent(JsonObject target, string key, string? value)
        {
            if (value != null)
                target[key] = value;
        }

        private static string? GetStringProp(JsonObject? obj, string key)
        {
            return obj?[key]?.GetValue<string>();
        }
    }

    /// <summary>
    /// Represents a cross-model dependency that is out of date.
    /// </summary>
    public class StaleDependency
    {
        public string? ModelUri { get; set; }
        public string? CurrentVersion { get; set; }
        public string? LatestVersion { get; set; }
        public Guid LatestModelId { get; set; }
        public int AffectedTypeCount { get; set; }
    }
}
