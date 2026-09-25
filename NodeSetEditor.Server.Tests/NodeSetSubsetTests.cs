using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The "Remove unused nodes" download option: each DEPENDENCY is trimmed to the nodes the model
/// being downloaded actually reaches, while the model itself ships whole.
///
/// <para>The fixture is a two-model stand-in for "a vendor model on top of Core" — a dependency
/// holding both used and unused nodes, and a primary model that reaches some of them. What
/// matters most is what gets left OUT: a trim that keeps everything still produces a valid
/// NodeSet and would pass a round-trip test while doing nothing.</para>
/// </summary>
[Collection("Api")]
public class NodeSetSubsetTests : IAsyncLifetime
{
    private const string NamespaceMetadataTypeId = "i=11616";
    private const string HasTypeDefinition = "i=40";
    private const string HasProperty = "i=46";
    private const string HasComponent = "i=47";
    private const string HasSubtype = "i=45";
    private const string FromState = "i=51";
    private const string ToState = "i=52";
    private const string HasCause = "i=53";
    private const string HasEffect = "i=54";
    private const string GeneratesEvent = "i=41";
    private const string HasInterface = "i=17603";
    private const string Organizes = "i=35";

    private static readonly XNamespace UaNs = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    private readonly ApiFixture _fixture;
    private readonly string _dependencyUri = $"urn:test:subset:dep:{Guid.NewGuid():N}";
    private readonly string _primaryUri = $"urn:test:subset:main:{Guid.NewGuid():N}";

    private Guid _dependencyId;
    private Guid _primaryId;
    private readonly Dictionary<string, string> _ids = new();

    public NodeSetSubsetTests(ApiFixture fixture) => _fixture = fixture;

    private string Id(string name) => _ids[name];

    public async Task InitializeAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();

        var dependency = NewModel(_dependencyUri, "SubsetDependency");
        var primary = NewModel(_primaryUri, "SubsetPrimary");
        db.Models.Add(dependency);
        db.Models.Add(primary);
        _dependencyId = dependency.Id;
        _primaryId = primary.Id;

        var ordinal = 0;
        var refOrdinal = 0;

        Node Add(string name, NodeSetEditor.Model.Model model, string uri, int nodeClass,
            JsonObject? attributes = null, string? parent = null,
            string? superType = null, string? typeDefinition = null, string? browseName = null)
        {
            var nodeId = $"nsu={uri};i={1000 + _ids.Count}";
            _ids[name] = nodeId;
            var node = new Node
            {
                ModelId = model.Id,
                NodeId = nodeId,
                BrowseName = browseName ?? name,
                DisplayName = name,
                NodeClass = nodeClass,
                Attributes = attributes,
                ParentNodeId = parent,
                SuperTypeId = superType,
                TypeDefinitionId = typeDefinition,
                Ordinal = ordinal += 100,
            };
            db.Nodes.Add(node);
            return node;
        }

        void Ref(Guid modelId, string source, string referenceType, string target, bool isForward = true) =>
            db.References.Add(new Reference
            {
                ModelId = modelId,
                SourceNodeId = source,
                ReferenceTypeId = referenceType,
                IsForward = isForward,
                TargetNodeId = target,
                Ordinal = refOrdinal += 100,
            });

        // ---- The dependency ----
        Add("DepBaseType", dependency, _dependencyUri, UaNodeClass.ObjectType);
        Add("DepUsedType", dependency, _dependencyUri, UaNodeClass.ObjectType, superType: Id("DepBaseType"));
        Add("DepDataType", dependency, _dependencyUri, UaNodeClass.DataType);
        // Nothing in the primary model reaches these two, so the trim must drop them. They are
        // deliberately parentless: a shared parent would pull them in via the HasChild rule.
        Add("DepUnusedType", dependency, _dependencyUri, UaNodeClass.ObjectType);
        Add("DepUnusedDataType", dependency, _dependencyUri, UaNodeClass.DataType);

        // The dependency's NamespaceMetadata object, with the property the subset must flip.
        Add("DepNamespaceMetadata", dependency, _dependencyUri, UaNodeClass.Object,
            typeDefinition: NamespaceMetadataTypeId, browseName: _dependencyUri);
        Add("IsNamespaceSubset", dependency, _dependencyUri, UaNodeClass.Variable,
            new JsonObject { ["DataType"] = "i=1", ["Value"] = new JsonObject { ["Boolean"] = false } },
            parent: Id("DepNamespaceMetadata"), browseName: "IsNamespaceSubset");

        // A state machine in the dependency, reached through the primary's supertype. Each of the
        // four state-machine references points at a node that is otherwise parentless and
        // unreferenced, so only the rule under test can bring it in.
        Add("DepTransition", dependency, _dependencyUri, UaNodeClass.Object, parent: Id("DepUsedType"));
        Add("DepFromState", dependency, _dependencyUri, UaNodeClass.Object);
        Add("DepToState", dependency, _dependencyUri, UaNodeClass.Object);
        Add("DepCauseMethod", dependency, _dependencyUri, UaNodeClass.Method);
        Add("DepEffectEvent", dependency, _dependencyUri, UaNodeClass.ObjectType);

        // More forward non-hierarchical references, each to a node nothing else reaches. The
        // HasTypeDefinition case is expressed only as a reference row (the column is left null) so
        // it exercises the reference rule rather than the dedicated column.
        Add("DepInterface", dependency, _dependencyUri, UaNodeClass.ObjectType);
        Add("DepGeneratedEvent", dependency, _dependencyUri, UaNodeClass.ObjectType);
        Add("DepTransitionType", dependency, _dependencyUri, UaNodeClass.ObjectType);
        // Reachable ONLY across an inverse non-hierarchical reference, which is never walked.
        Add("DepInverseOnly", dependency, _dependencyUri, UaNodeClass.ObjectType);
        // Reachable ONLY across Organizes — hierarchical but not HasChild, so also not walked.
        Add("DepOrganizedOnly", dependency, _dependencyUri, UaNodeClass.ObjectType);

        Ref(_dependencyId, Id("DepUsedType"), HasSubtype, Id("DepBaseType"), isForward: false);
        Ref(_dependencyId, Id("DepUsedType"), HasComponent, Id("DepTransition"));
        Ref(_dependencyId, Id("DepUsedType"), HasInterface, Id("DepInterface"));
        Ref(_dependencyId, Id("DepUsedType"), GeneratesEvent, Id("DepGeneratedEvent"));
        Ref(_dependencyId, Id("DepTransition"), HasTypeDefinition, Id("DepTransitionType"));
        Ref(_dependencyId, Id("DepUsedType"), GeneratesEvent, Id("DepInverseOnly"), isForward: false);
        Ref(_dependencyId, Id("DepUsedType"), Organizes, Id("DepOrganizedOnly"));
        Ref(_dependencyId, Id("DepTransition"), FromState, Id("DepFromState"));
        Ref(_dependencyId, Id("DepTransition"), ToState, Id("DepToState"));
        Ref(_dependencyId, Id("DepTransition"), HasCause, Id("DepCauseMethod"));
        Ref(_dependencyId, Id("DepTransition"), HasEffect, Id("DepEffectEvent"));
        Ref(_dependencyId, Id("DepNamespaceMetadata"), HasTypeDefinition, NamespaceMetadataTypeId);
        Ref(_dependencyId, Id("DepNamespaceMetadata"), HasProperty, Id("IsNamespaceSubset"));

        // ---- The model being downloaded ----
        Add("MainType", primary, _primaryUri, UaNodeClass.ObjectType, superType: Id("DepUsedType"));
        Add("MainVar", primary, _primaryUri, UaNodeClass.Variable,
            new JsonObject { ["DataType"] = Id("DepDataType") },
            parent: Id("MainType"));

        // A top-level object in the downloaded model, anchored into the DEPENDENCY's folder the
        // way a vendor object hangs off Core's Objects folder: no ParentNodeId, just an inverse
        // Organizes. The folder has to survive the trim or this object has nowhere to sit.
        Add("DepAnchorFolder", dependency, _dependencyUri, UaNodeClass.Object);
        Add("MainTopLevelObject", primary, _primaryUri, UaNodeClass.Object);
        Ref(_primaryId, Id("MainTopLevelObject"), Organizes, Id("DepAnchorFolder"), isForward: false);

        Ref(_primaryId, Id("MainType"), HasSubtype, Id("DepUsedType"), isForward: false);
        Ref(_primaryId, Id("MainType"), HasComponent, Id("MainVar"));

        await db.SaveChangesAsync();
    }

    private static NodeSetEditor.Model.Model NewModel(string uri, string name)
    {
        var model = new NodeSetEditor.Model.Model
        {
            Id = Guid.NewGuid(),
            Uri = uri,
            Name = name,
            Version = "1.0.0",
        };
        model.SetVersionNorm("1.0.0");
        return model;
    }

    public async Task DisposeAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();
        var ids = new[] { _dependencyId, _primaryId };
        await db.References.Where(r => ids.Contains(r.ModelId)).ExecuteDeleteAsync();
        await db.Nodes.Where(n => ids.Contains(n.ModelId)).ExecuteDeleteAsync();
        await db.Models.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync();
    }

    private async Task<IReadOnlySet<string>> ClosureAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<INodeSetSubsetService>();
        return await service.ComputeDependencyClosureAsync(_primaryId, [_dependencyId]);
    }

    private async Task<XElement> TrimmedDependencyAsync(SubsetProvenance? provenance = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<INodeSetSubsetService>();

        var keep = await service.ComputeDependencyClosureAsync(_primaryId, [_dependencyId]);
        var content = await service.ExportTrimmedAsync(_dependencyId, keep,
            provenance ?? new SubsetProvenance("Test User", "test@example.com", _primaryUri, DateTime.UtcNow));

        Assert.NotNull(content);
        using var stream = new MemoryStream(content!);
        return XDocument.Load(stream).Root!;
    }

    [Fact]
    public async Task ClosureKeepsWhatTheDownloadedModelReaches()
    {
        var closure = await ClosureAsync();

        Assert.Contains(Id("DepUsedType"), closure);      // the primary's supertype
        Assert.Contains(Id("DepBaseType"), closure);      // and ITS supertype
        Assert.Contains(Id("DepDataType"), closure);      // named by a primary variable
    }

    [Theory]
    // The state-machine references: a trimmed state machine without the states it moves between,
    // the Method that triggers it or the Event it raises is not usable.
    [InlineData("DepFromState")]
    [InlineData("DepToState")]
    [InlineData("DepCauseMethod")]
    [InlineData("DepEffectEvent")]
    // And the rest of the non-hierarchical family, which the rule covers without naming them.
    [InlineData("DepInterface")]
    [InlineData("DepGeneratedEvent")]
    [InlineData("DepTransitionType")]
    public async Task ClosureFollowsForwardNonHierarchicalReferences(string nodeName)
    {
        var closure = await ClosureAsync();

        Assert.Contains(Id("DepTransition"), closure); // reached as a child of the supertype
        Assert.Contains(Id(nodeName), closure);
    }

    [Fact]
    public async Task ClosureDoesNotFollowInverseNonHierarchicalReferences()
    {
        // An inverse non-hierarchical reference says something else points AT this node, which
        // implies nothing about what this node needs. It is dropped from the output instead.
        var closure = await ClosureAsync();

        Assert.DoesNotContain(Id("DepInverseOnly"), closure);
    }

    [Fact]
    public async Task ClosureKeepsTheCrossModelAnchorOfATopLevelObject()
    {
        // The one case where an inverse hierarchical reference IS followed: a parentless node
        // whose container lives in another model.
        var closure = await ClosureAsync();

        Assert.Contains(Id("DepAnchorFolder"), closure);
    }

    [Fact]
    public async Task TheAnchorRuleDoesNotApplyWithinOneModel()
    {
        // DepOrganizedOnly is organised by a node in its OWN model, so the anchor rule must not
        // reach it — otherwise "not followed" for Organizes would mean nothing.
        var closure = await ClosureAsync();

        Assert.DoesNotContain(Id("DepOrganizedOnly"), closure);
    }

    [Fact]
    public async Task ClosureDoesNotFollowOrganizes()
    {
        // Hierarchical but not HasChild: a folder organising a hundred types must not drag all of
        // them in. Kept in the output only when both ends are in the set.
        var closure = await ClosureAsync();

        Assert.DoesNotContain(Id("DepOrganizedOnly"), closure);
    }

    [Fact]
    public async Task ClosureDropsWhatNothingReaches()
    {
        var closure = await ClosureAsync();

        Assert.DoesNotContain(Id("DepUnusedType"), closure);
        Assert.DoesNotContain(Id("DepUnusedDataType"), closure);
    }

    [Fact]
    public async Task TheDownloadedModelItselfIsNeverTrimmed()
    {
        var closure = await ClosureAsync();

        Assert.Contains(Id("MainType"), closure);
        Assert.Contains(Id("MainVar"), closure);
    }

    [Fact]
    public async Task TrimmedDependencyOmitsTheUnusedNodes()
    {
        var root = await TrimmedDependencyAsync();

        var browseNames = root.Elements()
            .Where(e => e.Attribute("NodeId") != null)
            .Select(e => e.Attribute("BrowseName")?.Value ?? string.Empty)
            .ToList();

        Assert.Contains(browseNames, n => n.EndsWith("DepUsedType", StringComparison.Ordinal));
        Assert.DoesNotContain(browseNames, n => n.EndsWith("DepUnusedType", StringComparison.Ordinal));
        Assert.DoesNotContain(browseNames, n => n.EndsWith("DepUnusedDataType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrimmedDependencyCarriesTheNamespaceObjectWithIsNamespaceSubsetTrue()
    {
        var root = await TrimmedDependencyAsync();

        var metadataObjectIds = root.Elements()
            .Where(e => e.Element(UaNs + "References")?.Elements(UaNs + "Reference")
                .Any(r => r.Attribute("ReferenceType")?.Value == HasTypeDefinition
                          && r.Value == NamespaceMetadataTypeId) == true)
            .Select(e => e.Attribute("NodeId")!.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(metadataObjectIds);

        var flag = root.Elements().FirstOrDefault(e =>
            (e.Attribute("BrowseName")?.Value ?? string.Empty).EndsWith("IsNamespaceSubset", StringComparison.Ordinal)
            && metadataObjectIds.Contains(e.Attribute("ParentNodeId")?.Value ?? string.Empty));

        Assert.NotNull(flag);
        Assert.Equal("true", flag!.Element(UaNs + "Value")?.Value.Trim()
            ?? flag.Descendants().FirstOrDefault(d => d.Name.LocalName == "Boolean")?.Value.Trim());
    }

    [Fact]
    public async Task TrimmedDependencyRecordsWhoExportedItAndWhatFor()
    {
        var exportedUtc = new DateTime(2026, 9, 25, 10, 30, 0, DateTimeKind.Utc);
        var root = await TrimmedDependencyAsync(
            new SubsetProvenance("Randy", "randy@sparhawksoftware.com", _primaryUri, exportedUtc));

        var extension = root.Element(UaNs + "Extensions")?.Elements()
            .SelectMany(e => e.DescendantsAndSelf())
            .FirstOrDefault(e => e.Name.LocalName == "NodeSetSubset");

        Assert.NotNull(extension);
        Assert.Equal(_primaryUri, extension!.Attribute("TargetModelUri")?.Value);
        Assert.Equal("Randy", extension.Attribute("ExportedByName")?.Value);
        Assert.Equal("randy@sparhawksoftware.com", extension.Attribute("ExportedByEmail")?.Value);
        Assert.Equal(exportedUtc, DateTime.Parse(extension.Attribute("ExportedUtc")!.Value).ToUniversalTime());
    }

    [Fact]
    public async Task TrimmedDependencyHasNoUnresolvedReferences()
    {
        // The invariant that makes the trimmed file loadable: every NodeId it mentions in its own
        // namespace must be one it also defines. References out of the model are fine — their
        // namespace is a RequiredModel — but one that merely leaves the subset would dangle.
        var root = await TrimmedDependencyAsync();

        var nodeElements = root.Elements().Where(e => e.Attribute("NodeId") != null).ToList();
        var defined = nodeElements.Select(e => e.Attribute("NodeId")!.Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(defined);

        static string PrefixOf(string id)
        {
            if (!id.StartsWith("ns=", StringComparison.Ordinal)) return string.Empty;
            var semicolon = id.IndexOf(';');
            return semicolon < 0 ? string.Empty : id[..(semicolon + 1)];
        }

        var ownPrefixes = defined.Select(PrefixOf).ToHashSet(StringComparer.Ordinal);

        var dangling = new List<string>();
        var examined = 0;
        foreach (var element in nodeElements)
        {
            var browseName = element.Attribute("BrowseName")?.Value ?? "?";
            foreach (var candidate in ReferencedIds(element))
            {
                if (candidate == null || !ownPrefixes.Contains(PrefixOf(candidate))) continue;
                examined++;
                if (!defined.Contains(candidate)) dangling.Add($"{browseName} -> {candidate}");
            }
        }

        // Guards against the check passing simply by finding nothing to look at.
        Assert.True(examined > 0, "no own-namespace references were examined; the check was vacuous");
        Assert.True(dangling.Count == 0,
            $"trimmed dependency references nodes it does not define ({dangling.Count} of {examined}): "
            + string.Join(", ", dangling.Take(10)));

        static IEnumerable<string?> ReferencedIds(XElement element)
        {
            yield return element.Attribute("ParentNodeId")?.Value;
            yield return element.Attribute("DataType")?.Value;
            foreach (var r in element.Element(UaNs + "References")?.Elements(UaNs + "Reference") ?? [])
            {
                yield return r.Value;
                yield return r.Attribute("ReferenceType")?.Value;
            }
        }
    }
}
