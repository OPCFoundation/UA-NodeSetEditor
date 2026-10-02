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
    private const string HasEncoding = "i=38";
    private const string Organizes = "i=35";

    // Core's Server object, its Namespaces folder, and ServerType. A NamespaceMetadata object is
    // conventionally hung under Namespaces; following that link reaches Server and everything
    // ServerType owns, which is what the trim must refuse to do.
    private const string ServerNamespacesId = "i=11715";
    private const string ServerId = "i=2253";
    private const string ServerTypeId = "i=2004";
    private const string ServerStatusId = "i=2256";
    private const string ServerTypeChildId = "i=2138";
    private const string CoreNamespaceMetadataId = "i=15957";

    // The type-dictionary machinery: a DataType's encodings point by HasDescription at entries in
    // a dictionary that holds one per type in the namespace, plus the whole schema as a Value.
    private const string HasDescription = "i=39";
    private const string DataTypeEncodingType = "i=76";
    private const string DataTypeDictionaryType = "i=72";
    private const string DataTypeDescriptionType = "i=69";

    private static readonly XNamespace UaNs = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    private readonly ApiFixture _fixture;
    private readonly string _dependencyUri = $"urn:test:subset:dep:{Guid.NewGuid():N}";
    private readonly string _primaryUri = $"urn:test:subset:main:{Guid.NewGuid():N}";
    private readonly string _coreUri = $"urn:test:subset:core:{Guid.NewGuid():N}";

    private Guid _dependencyId;
    private Guid _primaryId;
    private Guid _coreId;
    private readonly Dictionary<string, string> _ids = new();

    public NodeSetSubsetTests(ApiFixture fixture) => _fixture = fixture;

    private string Id(string name) => _ids[name];

    public async Task InitializeAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeSetEditorDbContext>();

        var dependency = NewModel(_dependencyUri, "SubsetDependency");
        var primary = NewModel(_primaryUri, "SubsetPrimary");
        var core = NewModel(_coreUri, "SubsetCore");
        db.Models.Add(dependency);
        db.Models.Add(primary);
        db.Models.Add(core);
        _dependencyId = dependency.Id;
        _primaryId = primary.Id;
        _coreId = core.Id;

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

        // Core nodes are stored with bare NodeIds, so the stand-in below uses the real ones.
        Node AddCore(string nodeId, string name, int nodeClass,
            string? parent = null, string? typeDefinition = null)
        {
            var node = new Node
            {
                ModelId = core.Id,
                NodeId = nodeId,
                BrowseName = name,
                DisplayName = name,
                NodeClass = nodeClass,
                ParentNodeId = parent,
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

        // ---- Core's Server corner ----
        AddCore(ServerTypeId, "ServerType", UaNodeClass.ObjectType);
        // ServerType's own instance declarations. Keeping ServerType so the Server stub has a
        // TypeDefinition must not bring these with it — that tree is what made Core unusable.
        AddCore(ServerTypeChildId, "ServerStatus", UaNodeClass.Variable, parent: ServerTypeId);
        Ref(_coreId, ServerTypeChildId, HasComponent, ServerTypeId, isForward: false);
        AddCore(ServerId, "Server", UaNodeClass.Object, typeDefinition: ServerTypeId);
        AddCore(ServerNamespacesId, "Namespaces", UaNodeClass.Object, parent: ServerId);
        // A sibling of Namespaces. Nothing reaches it, so keeping Server must not bring it along —
        // in real Core this is the other 16 children of the Server object.
        AddCore(ServerStatusId, "ServerStatus", UaNodeClass.Variable, parent: ServerId);
        Ref(_coreId, ServerNamespacesId, HasComponent, ServerId, isForward: false);
        Ref(_coreId, ServerStatusId, HasComponent, ServerId, isForward: false);
        Ref(_coreId, ServerId, HasTypeDefinition, ServerTypeId);

        // Core's OWN NamespaceMetadata object, hung under Server's Namespaces folder. The seed
        // loop takes every NamespaceMetadata object in every model, so this one is always walked —
        // and because it and the folder are BOTH in Core, a same-model climb reaches Server.
        AddCore(CoreNamespaceMetadataId, "http://opcfoundation.org/UA/", UaNodeClass.Object,
            typeDefinition: NamespaceMetadataTypeId);
        Ref(_coreId, CoreNamespaceMetadataId, HasTypeDefinition, NamespaceMetadataTypeId);
        Ref(_coreId, CoreNamespaceMetadataId, HasComponent, ServerNamespacesId, isForward: false);

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

        // DepDataType's encodings and the dictionary behind them. DepUsedDataType is never reached,
        // but its description sits in the same dictionary — so if the dictionary came along, its
        // entry would too, which is exactly the blow-up being prevented.
        Add("DepDictionary", dependency, _dependencyUri, UaNodeClass.Variable,
            typeDefinition: DataTypeDictionaryType, browseName: "Opc.Ua");
        Add("DepDescription", dependency, _dependencyUri, UaNodeClass.Variable,
            parent: Id("DepDictionary"), typeDefinition: DataTypeDescriptionType,
            browseName: "DepDataType");
        Add("DepOtherDescription", dependency, _dependencyUri, UaNodeClass.Variable,
            parent: Id("DepDictionary"), typeDefinition: DataTypeDescriptionType,
            browseName: "DepUnusedDataType");
        Add("DepBinaryEncoding", dependency, _dependencyUri, UaNodeClass.Object,
            parent: Id("DepDataType"), typeDefinition: DataTypeEncodingType,
            browseName: "Default Binary");
        Add("DepJsonEncoding", dependency, _dependencyUri, UaNodeClass.Object,
            parent: Id("DepDataType"), typeDefinition: DataTypeEncodingType,
            browseName: "Default JSON");
        // HasEncoding is stored on the encoding object as an inverse, the way Core writes it.
        Ref(_dependencyId, Id("DepBinaryEncoding"), HasEncoding, Id("DepDataType"), isForward: false);
        Ref(_dependencyId, Id("DepJsonEncoding"), HasEncoding, Id("DepDataType"), isForward: false);
        // The column alone is not emitted as a reference; a real import writes both.
        Ref(_dependencyId, Id("DepBinaryEncoding"), HasTypeDefinition, DataTypeEncodingType);
        Ref(_dependencyId, Id("DepJsonEncoding"), HasTypeDefinition, DataTypeEncodingType);
        Ref(_dependencyId, Id("DepBinaryEncoding"), HasDescription, Id("DepDescription"));
        Ref(_dependencyId, Id("DepDictionary"), HasComponent, Id("DepDescription"));
        Ref(_dependencyId, Id("DepDictionary"), HasComponent, Id("DepOtherDescription"));

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
        // Hung under Server's Namespaces folder with an inverse HasComponent and NO ParentNodeId —
        // how an imported vendor NodeSet spells it.
        Ref(_dependencyId, Id("DepNamespaceMetadata"), HasComponent, ServerNamespacesId, isForward: false);

        // ---- The model being downloaded ----
        Add("MainType", primary, _primaryUri, UaNodeClass.ObjectType, superType: Id("DepUsedType"));
        Add("MainVar", primary, _primaryUri, UaNodeClass.Variable,
            new JsonObject { ["DataType"] = Id("DepDataType") },
            parent: Id("MainType"));

        // A top-level object in the downloaded model, anchored into the DEPENDENCY's folder the
        // way a vendor object hangs off Core's Objects folder: no ParentNodeId, just an inverse
        // Organizes. The folder has to survive the trim or this object has nowhere to sit.
        Add("DepAnchorFolder", dependency, _dependencyUri, UaNodeClass.Object);
        // Something else already in that folder. The primary never names it, so keeping the folder
        // must not bring it along — the folder is a mounting point, not a request for its contents.
        Add("DepAnchorFolderChild", dependency, _dependencyUri, UaNodeClass.Object,
            parent: Id("DepAnchorFolder"));
        Ref(_dependencyId, Id("DepAnchorFolder"), HasComponent, Id("DepAnchorFolderChild"));
        Add("MainTopLevelObject", primary, _primaryUri, UaNodeClass.Object);
        Ref(_primaryId, Id("MainTopLevelObject"), Organizes, Id("DepAnchorFolder"), isForward: false);

        // The downloaded model's own NamespaceMetadata object, spelling the Server link the OTHER
        // way — as a ParentNodeId as well as an inverse HasComponent, which is what the editor
        // writes. The primary is never trimmed, so this node is always walked: if the closure
        // followed it, Server would come in no matter what the dependencies look like.
        Add("MainNamespaceMetadata", primary, _primaryUri, UaNodeClass.Object,
            typeDefinition: NamespaceMetadataTypeId, parent: ServerNamespacesId,
            browseName: _primaryUri);
        Ref(_primaryId, Id("MainNamespaceMetadata"), HasTypeDefinition, NamespaceMetadataTypeId);
        Ref(_primaryId, Id("MainNamespaceMetadata"), HasComponent, ServerNamespacesId, isForward: false);

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
        var ids = new[] { _dependencyId, _primaryId, _coreId };
        await db.References.Where(r => ids.Contains(r.ModelId)).ExecuteDeleteAsync();
        await db.Nodes.Where(n => ids.Contains(n.ModelId)).ExecuteDeleteAsync();
        await db.Models.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync();
    }

    private async Task<IReadOnlySet<string>> ClosureAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<INodeSetSubsetService>();
        return await service.ComputeDependencyClosureAsync(_primaryId, [_dependencyId, _coreId]);
    }

    private async Task<XElement> TrimmedDependencyAsync(SubsetProvenance? provenance = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<INodeSetSubsetService>();

        var keep = await service.ComputeDependencyClosureAsync(_primaryId, [_dependencyId, _coreId]);
        var content = await service.ExportTrimmedAsync(_dependencyId, keep,
            provenance ?? new SubsetProvenance("Test User", "Test Workspace", _primaryUri, DateTime.UtcNow));

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

    /// <summary>
    /// A container is kept so the reference naming it resolves, and its own container chain comes
    /// with it — all as placeholders, none of them expanded. Three models here hang a
    /// NamespaceMetadata object under Server's Namespaces folder: the primary by ParentNodeId, the
    /// dependency by an inverse HasComponent, and Core's own one within Core itself. So the folder
    /// and the Server object above it survive as bare nodes, while ServerStatus beside them and
    /// ServerType behind them stay out.
    /// </summary>
    [Fact]
    public async Task AContainerIsKeptWithoutItsContents()
    {
        var closure = await ClosureAsync();

        // The chain the metadata objects hang from, kept so nothing dangles — including the
        // TypeDefinition every Object shall have (OPC 10000-3, 7.13).
        Assert.Contains(ServerNamespacesId, closure);
        Assert.Contains(ServerId, closure);
        Assert.Contains(ServerTypeId, closure);

        // All of it as stubs: neither the Server object's other children nor ServerType's
        // instance declarations come along.
        Assert.DoesNotContain(ServerStatusId, closure);
        Assert.DoesNotContain(ServerTypeChildId, closure);

        // The trim still works for everything else — this is not a closure that stopped early.
        Assert.Contains(Id("DepUsedType"), closure);
        Assert.Contains(Id("DepNamespaceMetadata"), closure);
    }

    /// <summary>
    /// The same rule, reached the other way round: DepAnchorFolder is the dependency's folder that
    /// a top-level object in the PRIMARY is organised under. It is kept — and its own unrelated
    /// contents are not dragged in with it.
    /// </summary>
    [Fact]
    public async Task AMountingPointDoesNotDragInItsSiblingsFromTheOtherModel()
    {
        var closure = await ClosureAsync();

        Assert.Contains(Id("DepAnchorFolder"), closure);
        Assert.DoesNotContain(Id("DepAnchorFolderChild"), closure);
    }

    /// <summary>
    /// A DataType keeps the encodings a reader resolves an ExtensionObject TypeId against, and
    /// nothing else of the Part 3 dictionary machinery: no DataTypeDictionary, no
    /// DataTypeDescription entries, and no Default JSON encoding.
    /// </summary>
    [Fact]
    public async Task TypeDictionariesAndJsonEncodingsAreNotInTheClosure()
    {
        var closure = await ClosureAsync();

        Assert.Contains(Id("DepDataType"), closure);
        Assert.Contains(Id("DepBinaryEncoding"), closure);

        Assert.DoesNotContain(Id("DepJsonEncoding"), closure);
        Assert.DoesNotContain(Id("DepDictionary"), closure);
        Assert.DoesNotContain(Id("DepDescription"), closure);
        // The one that would have ridden in on the dictionary's coat-tails.
        Assert.DoesNotContain(Id("DepOtherDescription"), closure);
    }

    /// <summary>
    /// The HasDescription reference goes with the entry it named. The export keeps a reference
    /// whose target was dropped — right for a cross-model link, wrong for a link into a dictionary
    /// that was deliberately removed.
    /// </summary>
    [Fact]
    public async Task TrimmedDependencyHasNoHasDescriptionReferencesLeft()
    {
        var root = await TrimmedDependencyAsync();

        var references = root.Elements()
            .Elements(UaNs + "References")
            .Elements(UaNs + "Reference")
            .ToArray();

        Assert.DoesNotContain(HasDescription, references.Select(r => (string?)r.Attribute("ReferenceType")));

        // The encoding object itself survives — it is the thing worth keeping.
        var encodings = root.Elements()
            .Where(e => e.Elements(UaNs + "References").Elements(UaNs + "Reference")
                .Any(r => (string?)r.Attribute("ReferenceType") == HasTypeDefinition
                          && r.Value == DataTypeEncodingType))
            .Select(e => (string?)e.Attribute("BrowseName"))
            .ToArray();

        Assert.Contains("Default Binary", encodings);
        Assert.DoesNotContain("Default JSON", encodings);
    }

    /// <summary>
    /// A container kept as a placeholder still holds a forward reference to every child it owns,
    /// and those children are exactly what the trim removed. A reference to a node in the file's
    /// OWN namespace that the file does not define is broken, so it goes — unlike a reference
    /// into another namespace, which is a legitimate cross-model link.
    /// </summary>
    [Fact]
    public async Task TrimmedDependencyDoesNotNameItsOwnMissingNodes()
    {
        var root = await TrimmedDependencyAsync();

        var present = root.Elements()
            .Select(e => (string?)e.Attribute("NodeId"))
            .Where(id => id != null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in root.Elements().Where(e => e.Attribute("NodeId") != null))
        {
            var parent = (string?)node.Attribute("ParentNodeId");
            if (parent != null && parent.StartsWith("ns=1;", StringComparison.Ordinal))
                Assert.Contains(parent, present);

            foreach (var reference in node.Elements(UaNs + "References").Elements(UaNs + "Reference"))
            {
                // Bare ids are Core's, and Core is a different file — those may point outward.
                if (!reference.Value.StartsWith("ns=1;", StringComparison.Ordinal)) continue;
                Assert.Contains(reference.Value, present);
            }
        }
    }

    [Fact]
    public async Task TrimmedDependencyRecordsWhoExportedItAndWhatFor()
    {
        var exportTime = new DateTime(2026, 9, 25, 10, 30, 0, DateTimeKind.Utc);
        var root = await TrimmedDependencyAsync(
            new SubsetProvenance("Randy", "Dosing Review", _primaryUri, exportTime));

        var extension = root.Element(UaNs + "Extensions")?.Elements()
            .SelectMany(e => e.DescendantsAndSelf())
            .FirstOrDefault(e => e.Name.LocalName == "NodeSetSubset");

        Assert.NotNull(extension);
        Assert.Equal(_primaryUri, extension!.Attribute("TargetModelUri")?.Value);
        Assert.Equal("Randy", extension.Attribute("ExportedBy")?.Value);
        Assert.Equal("Dosing Review", extension.Attribute("Workspace")?.Value);
        Assert.Equal(exportTime, DateTime.Parse(extension.Attribute("ExportTime")!.Value).ToUniversalTime());

        // ExportedBy is the account name. The old attributes are gone, and no email is recorded.
        Assert.Null(extension.Attribute("ExportedByName"));
        Assert.Null(extension.Attribute("ExportedByEmail"));
        Assert.Null(extension.Attribute("ExportedUtc"));
        Assert.All(extension.Attributes(), a => Assert.DoesNotContain("@", a.Value));
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
