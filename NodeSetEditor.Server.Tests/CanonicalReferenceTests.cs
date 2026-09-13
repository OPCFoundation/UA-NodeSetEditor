using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Verifies the canonical-parent semantics introduced in
/// <c>UaRestApiController.GetStructuralParent</c>:
/// <list type="bullet">
///   <item><c>/references</c> tags each row with <c>isCanonicalParent</c>
///   so the UI can suppress the delete affordance for structural refs.</item>
///   <item><c>/children</c> emits exactly one row per child, via the
///   canonical structural reference — extra hierarchical refs (e.g.
///   HasNotifier alongside HasComponent) appear only in the references
///   tab.</item>
///   <item><c>DELETE /nodes/{id}/references/...</c> succeeds whether the
///   reference was authored on the source or on the target (the inverse
///   form CreateChildNode emits).</item>
/// </list>
/// Each test seeds its own BrowseNames so the shared TestModel namespace
/// can host parallel scenarios without collision.
/// </summary>
[Collection("Api")]
public class CanonicalReferenceTests : UaRestTestBase
{
    private const string HasComponent = "i=47";
    private const string HasNotifier = "i=48";
    private const string HasSubtype = "i=45";
    private const string Organizes = "i=35";
    private const string BaseObjectType = "i=58";

    public CanonicalReferenceTests(ApiFixture fixture) : base(fixture) { }

    // ---- helpers ----

    private async Task<JsonElement> GetReferences(string nodeId)
    {
        var req = WithServer(HttpMethod.Get,
            $"/api/opcua/v1/nodes/{Slug(nodeId)}/references");
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetChildren(string nodeId, bool includeSubtypes = false)
    {
        var url = $"/api/opcua/v1/nodes/{Slug(nodeId)}/children";
        if (includeSubtypes) url += "?includeSubtypes=true";
        var req = WithServer(HttpMethod.Get, url);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> AddReference(
        string sourceNodeId, string referenceTypeId, string targetNodeId, bool isForward = true)
    {
        var req = WithServer(HttpMethod.Post,
            $"/api/opcua/v1/nodes/{Slug(sourceNodeId)}/references");
        req.Content = JsonContent.Create(new
        {
            referenceTypeId,
            targetNodeId,
            isForward,
        });
        return await Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> DeleteReference(
        string sourceNodeId, string referenceTypeId, string targetNodeId, bool isForward)
    {
        var url = $"/api/opcua/v1/nodes/{Slug(sourceNodeId)}/references/"
            + $"{Slug(referenceTypeId)}/{Slug(targetNodeId)}?isForward={isForward.ToString().ToLowerInvariant()}";
        var req = WithServer(HttpMethod.Delete, url);
        return await Client.SendAsync(req);
    }

    /// <summary>
    /// Iterates the "results" array, returning the first row whose
    /// <c>referenceTypeId</c>, <c>targetNodeId</c>, and <c>isForward</c> match.
    /// Returns null if no such row exists (so tests can assert absence).
    /// </summary>
    private static JsonElement? FindRef(
        JsonElement refsResponse, string referenceTypeId, string targetNodeId, bool isForward)
    {
        foreach (var r in refsResponse.GetProperty("results").EnumerateArray())
        {
            if (r.GetProperty("referenceTypeId").GetString() == referenceTypeId
                && r.GetProperty("targetNodeId").GetString() == targetNodeId
                && r.GetProperty("isForward").GetBoolean() == isForward)
            {
                return r;
            }
        }
        return null;
    }

    private static int CountByTarget(JsonElement childrenResponse, string targetNodeId)
    {
        var n = 0;
        foreach (var r in childrenResponse.GetProperty("results").EnumerateArray())
        {
            if (r.GetProperty("nodeId").GetString() == targetNodeId) n++;
        }
        return n;
    }

    private static string UniqueName(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}".Substring(0, Math.Min(prefix.Length + 9, 24));

    // ---- /references: isCanonicalParent flag ----

    /// <summary>
    /// Forward view from a parent: the structural HasComponent reference
    /// to a child created via <c>CreateChildNode</c> must be flagged
    /// canonical, while an additional <c>HasNotifier</c> on the same
    /// (parent, child) pair must not be.
    /// </summary>
    [Fact]
    public async Task GetReferences_ParentSide_FlagsCanonicalForward()
    {
        var parent = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("RefCanonParent"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parent, "Variable", UniqueName("RefCanonChild"));

        var add = await AddReference(parent, HasNotifier, child, isForward: true);
        Assert.True(add.IsSuccessStatusCode,
            $"AddReference HasNotifier failed: {await add.Content.ReadAsStringAsync()}");

        var refs = await GetReferences(parent);

        var canonical = FindRef(refs, HasComponent, child, isForward: true);
        Assert.NotNull(canonical);
        Assert.True(canonical.Value.GetProperty("isCanonicalParent").GetBoolean(),
            "Forward HasComponent to a structurally-owned child should be flagged canonical");

        var extra = FindRef(refs, HasNotifier, child, isForward: true);
        Assert.NotNull(extra);
        Assert.False(extra.Value.GetProperty("isCanonicalParent").GetBoolean(),
            "Additional HasNotifier on the same child must not be flagged canonical");
    }

    /// <summary>
    /// Inverse view from the child: the structural HasComponent reference
    /// back to the parent is flagged canonical.
    /// </summary>
    [Fact]
    public async Task GetReferences_ChildSide_FlagsCanonicalInverse()
    {
        var parent = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("RefInvParent"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parent, "Variable", UniqueName("RefInvChild"));

        var refs = await GetReferences(child);

        var canonical = FindRef(refs, HasComponent, parent, isForward: false);
        Assert.NotNull(canonical);
        Assert.True(canonical.Value.GetProperty("isCanonicalParent").GetBoolean(),
            "Inverse HasComponent to the structural parent should be flagged canonical");
    }

    /// <summary>
    /// Type-side view: an inverse HasSubtype to the supertype is the
    /// canonical structural reference for a type node.
    /// </summary>
    [Fact]
    public async Task GetReferences_TypeNode_FlagsHasSubtypeCanonical()
    {
        var subtype = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("RefSub"), referenceTypeId: HasSubtype);

        var refs = await GetReferences(subtype);

        var canonical = FindRef(refs, HasSubtype, BaseObjectType, isForward: false);
        Assert.NotNull(canonical);
        Assert.True(canonical.Value.GetProperty("isCanonicalParent").GetBoolean(),
            "Inverse HasSubtype on a subtype must be flagged canonical");
    }

    // ---- /children: dedup to canonical ----

    /// <summary>
    /// When a child has both a structural HasComponent and an extra
    /// HasNotifier from the same parent, /children must surface the child
    /// exactly once (via the canonical reference).
    /// </summary>
    [Fact]
    public async Task GetChildren_DedupesAcrossExtraHierarchicalRefs()
    {
        var parent = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("ChDedupP"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parent, "Variable", UniqueName("ChDedupC"));

        // Add a second hierarchical ref between the same two nodes so the
        // unfiltered browse would otherwise return two rows.
        var add = await AddReference(parent, HasNotifier, child, isForward: true);
        Assert.True(add.IsSuccessStatusCode,
            $"AddReference HasNotifier failed: {await add.Content.ReadAsStringAsync()}");

        var children = await GetChildren(parent);

        Assert.Equal(1, CountByTarget(children, child));
        // The single emitted row carries the canonical reference type,
        // not the extra one.
        var row = children.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("nodeId").GetString() == child);
        Assert.Equal(HasComponent, row.GetProperty("referenceTypeId").GetString());
    }

    /// <summary>
    /// A node reachable from the queried parent only via a non-structural
    /// hierarchical reference (its real structural parent is somewhere
    /// else) must not appear in /children — it would misrepresent the
    /// hierarchy. The reference still surfaces in /references.
    /// </summary>
    [Fact]
    public async Task GetChildren_ExcludesNonStructuralTargets()
    {
        var parentA = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("ChNonA"), referenceTypeId: HasSubtype);
        var parentB = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("ChNonB"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parentA, "Variable", UniqueName("ChNonC"));

        // parentB has a HasNotifier to a child that is structurally under parentA.
        var add = await AddReference(parentB, HasNotifier, child, isForward: true);
        Assert.True(add.IsSuccessStatusCode,
            $"AddReference failed: {await add.Content.ReadAsStringAsync()}");

        var bChildren = await GetChildren(parentB);
        Assert.Equal(0, CountByTarget(bChildren, child));

        // But the reference is still visible from parentB's references tab.
        var bRefs = await GetReferences(parentB);
        var extra = FindRef(bRefs, HasNotifier, child, isForward: true);
        Assert.NotNull(extra);
        Assert.False(extra.Value.GetProperty("isCanonicalParent").GetBoolean());
    }

    /// <summary>
    /// With <c>includeSubtypes=true</c> the picker tree must list each
    /// subtype exactly once via the canonical HasSubtype, even when the
    /// subtype is also reachable via a forward ref the test author added.
    /// </summary>
    [Fact]
    public async Task GetChildren_IncludeSubtypes_DedupesSubtypeRow()
    {
        var super = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("SubDedupS"), referenceTypeId: HasSubtype);
        var sub = await CreateChildNode(super, "ObjectType",
            UniqueName("SubDedupSub"), referenceTypeId: HasSubtype);

        var children = await GetChildren(super, includeSubtypes: true);

        Assert.Equal(1, CountByTarget(children, sub));
        var row = children.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("nodeId").GetString() == sub);
        Assert.Equal(HasSubtype, row.GetProperty("referenceTypeId").GetString());
    }

    // ---- DELETE reference: cross-side authoring ----

    /// <summary>
    /// CreateChildNode authors the structural HasComponent on the child
    /// (inverse). Deleting from the parent's view (forward) must still
    /// remove the underlying row — exercises the inverse-stored fix in
    /// <c>RemoveReference</c> and <c>PersistChangesAsync</c>.
    /// </summary>
    [Fact]
    public async Task DeleteReference_RemovesInverseStoredCanonical()
    {
        var parent = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("DelInvP"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parent, "Variable", UniqueName("DelInvC"));

        // Sanity: canonical ref present before deletion.
        var before = await GetReferences(parent);
        Assert.NotNull(FindRef(before, HasComponent, child, isForward: true));

        var resp = await DeleteReference(parent, HasComponent, child, isForward: true);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Underlying row really gone — survives Invalidate+reload.
        var after = await GetReferences(parent);
        Assert.Null(FindRef(after, HasComponent, child, isForward: true));
    }

    /// <summary>
    /// A non-canonical hierarchical reference added via <c>AddReference</c>
    /// is authored on the source. Deleting it via the same source/forward
    /// orientation succeeds.
    /// </summary>
    [Fact]
    public async Task DeleteReference_RemovesNonCanonicalHierarchicalRef()
    {
        var parent = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("DelNonP"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parent, "Variable", UniqueName("DelNonC"));

        var add = await AddReference(parent, HasNotifier, child, isForward: true);
        Assert.True(add.IsSuccessStatusCode,
            $"AddReference HasNotifier failed: {await add.Content.ReadAsStringAsync()}");

        var resp = await DeleteReference(parent, HasNotifier, child, isForward: true);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var after = await GetReferences(parent);
        Assert.Null(FindRef(after, HasNotifier, child, isForward: true));

        // The structural HasComponent must remain — the delete should not
        // have collateral-damaged the canonical reference.
        Assert.NotNull(FindRef(after, HasComponent, child, isForward: true));
    }

    // ---- Picker traversal regression ----

    /// <summary>
    /// The picker tree drills through the standard core address space:
    /// Root → Types folder → category folder → top-level type. Pre-fix,
    /// <c>GetStructuralParent</c> only matched type nodes via inverse
    /// HasSubtype, so a top-level type (BaseObjectType, BaseDataType, …)
    /// returned null and was filtered out — the tree dead-ended at the
    /// category folder. This walks the chain via the same endpoint the
    /// picker hits and asserts each level is non-empty.
    /// </summary>
    [Fact]
    public async Task GetChildren_PickerTraversal_DrillsPastTopLevelType()
    {
        // Level 1 → 2: Root → Types folder (i=86).
        var rootChildren = await GetChildren("i=84", includeSubtypes: true);
        Assert.Contains(rootChildren.GetProperty("results").EnumerateArray(),
            n => n.GetProperty("nodeId").GetString() == "i=86");

        // Level 2 → 3: Types → at least one category folder.
        var typesChildren = await GetChildren("i=86", includeSubtypes: true);
        var typesArr = typesChildren.GetProperty("results");
        Assert.True(typesArr.GetArrayLength() > 0,
            "Types folder (i=86) should expose category folders");

        // Level 3 → 4: pick a category folder and confirm its top-level
        // type is reachable. Pre-fix this is where the tree died.
        string? topLevelTypeId = null;
        foreach (var cat in typesArr.EnumerateArray())
        {
            var catId = cat.GetProperty("nodeId").GetString()!;
            var catChildren = await GetChildren(catId, includeSubtypes: true);
            if (catChildren.GetProperty("results").GetArrayLength() > 0)
            {
                topLevelTypeId = catChildren.GetProperty("results")[0]
                    .GetProperty("nodeId").GetString();
                break;
            }
        }
        Assert.False(string.IsNullOrEmpty(topLevelTypeId),
            "At least one Types-folder category must surface its top-level type");

        // Level 4 → 5: top-level type → its subtypes via HasSubtype.
        var subtypeChildren = await GetChildren(topLevelTypeId!, includeSubtypes: true);
        Assert.True(subtypeChildren.GetProperty("results").GetArrayLength() > 0,
            $"Top-level type {topLevelTypeId} should expose at least one subtype");
    }

    /// <summary>
    /// Direct check on the well-known BaseObjectType (i=58): even though
    /// it has no supertype, it must report a structural parent of its own
    /// (its containing folder via Organizes) so the canonical filter
    /// doesn't hide it from <c>/children</c> of that folder. We look at
    /// the *inverse* canonical row — forward HasSubtype rows on i=58 are
    /// also flagged canonical, but those represent each subtype's parent,
    /// not i=58's own.
    /// </summary>
    [Fact]
    public async Task GetReferences_BaseObjectType_HasOwnInverseCanonicalParent()
    {
        var refs = await GetReferences("i=58");
        var ownParent = refs.GetProperty("results").EnumerateArray()
            .FirstOrDefault(r =>
                r.GetProperty("isCanonicalParent").GetBoolean()
                && !r.GetProperty("isForward").GetBoolean());
        Assert.True(ownParent.ValueKind == JsonValueKind.Object,
            "BaseObjectType must expose an inverse canonical-parent reference — its containing folder. "
            + "Without it, /children of that folder would dead-end and the picker tree would stop "
            + "before reaching any object type.");
        // i=58 has no supertype, so its own structural parent ref must be
        // hierarchical but NOT HasSubtype.
        Assert.NotEqual(HasSubtype, ownParent.GetProperty("referenceTypeId").GetString());
    }

    /// <summary>
    /// Same non-canonical reference, deleted from the child's inverse view
    /// instead of the parent's forward view. The delete query must match
    /// the inverse representation in storage.
    /// </summary>
    [Fact]
    public async Task DeleteReference_RemovesNonCanonicalFromInverseSide()
    {
        var parent = await CreateChildNode(BaseObjectType, "ObjectType",
            UniqueName("DelInvSP"), referenceTypeId: HasSubtype);
        var child = await CreateChildNode(parent, "Variable", UniqueName("DelInvSC"));

        var add = await AddReference(parent, HasNotifier, child, isForward: true);
        Assert.True(add.IsSuccessStatusCode,
            $"AddReference HasNotifier failed: {await add.Content.ReadAsStringAsync()}");

        // Delete from child's view — source=child, target=parent, isForward=false.
        var resp = await DeleteReference(child, HasNotifier, parent, isForward: false);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var parentRefs = await GetReferences(parent);
        Assert.Null(FindRef(parentRefs, HasNotifier, child, isForward: true));
        var childRefs = await GetReferences(child);
        Assert.Null(FindRef(childRefs, HasNotifier, parent, isForward: false));
    }
}
