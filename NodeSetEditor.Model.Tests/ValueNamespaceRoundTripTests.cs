extern alias JsonNodeSet;

using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;
using Part6Variant = JsonNodeSet::Opc.Ua.NodeSetSerializer.Part6Variant;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// A <c>ns=N</c> is only meaningful against the NamespaceUris table of the document it came
    /// from. Every NodeId-bearing attribute is therefore stored as <c>nsu=URI</c> — and the
    /// NodeIds inside a Variable's Value are no exception. The one that bites is the
    /// ExtensionObject TypeId every Structure value carries: export reorders NamespaceUris
    /// (model first, then dependencies sorted), so a stored index silently comes to mean a
    /// different namespace, and the value ends up pointing at a node that does not exist.
    /// </summary>
    [Collection("Database")]
    public class ValueNamespaceRoundTripTests
    {
        /// <summary>The value's Structure DataType lives here. Sorts AFTER <see cref="AlphaSuffix"/>.</summary>
        private const string ZuluSuffix = "zulu";

        /// <summary>A second dependency, present only to make the reordering happen.</summary>
        private const string AlphaSuffix = "alpha";

        private readonly DatabaseFixture _fixture;

        public ValueNamespaceRoundTripTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        private sealed record Namespaces(string Model, string Zulu, string Alpha);

        private static Namespaces NewNamespaces()
        {
            var id = Guid.NewGuid().ToString("N");
            return new Namespaces(
                $"urn:test:valuens:model:{id}",
                $"urn:test:valuens:{ZuluSuffix}:{id}",
                $"urn:test:valuens:{AlphaSuffix}:{id}");
        }

        /// <summary>
        /// A NodeSet whose NamespaceUris deliberately lists the dependencies in NON-alphabetical
        /// order, so the indices it uses cannot survive an export that sorts them:
        /// <c>ns=1</c> = model, <c>ns=2</c> = zulu, <c>ns=3</c> = alpha.
        ///
        /// <para>Two Variables, so both dependencies are structural references and land in the
        /// exported table regardless of how values are handled. Only the first carries a Value,
        /// an ExtensionObject whose TypeId names zulu's <c>i=5009</c> encoding node.</para>
        /// </summary>
        private static Opc.Ua.Export.UANodeSet BuildNodeSet(Namespaces ns)
        {
            return new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = [ns.Model, ns.Zulu, ns.Alpha],
                Models =
                [
                    new Opc.Ua.Export.ModelTableEntry
                    {
                        ModelUri = ns.Model,
                        Version = "1.0.0",
                        ModelVersion = "1.0.0",
                        PublicationDate = DateTime.UtcNow,
                        PublicationDateSpecified = true,
                        RequiredModel =
                        [
                            new Opc.Ua.Export.ModelTableEntry { ModelUri = ns.Zulu, Version = "1.0.0" },
                            new Opc.Ua.Export.ModelTableEntry { ModelUri = ns.Alpha, Version = "1.0.0" },
                        ],
                    }
                ],
                Items =
                [
                    new Opc.Ua.Export.UAVariable
                    {
                        NodeId = "ns=1;i=1001",
                        BrowseName = "1:Information",
                        DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "Information" }],
                        DataType = "ns=2;i=3006",   // zulu
                        Value = ExtensionObjectValue("ns=2;i=5009"), // zulu's Default XML encoding
                    },
                    new Opc.Ua.Export.UAVariable
                    {
                        NodeId = "ns=1;i=1002",
                        BrowseName = "1:Other",
                        DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "Other" }],
                        DataType = "ns=3;i=3007",   // alpha
                    },
                ]
            };
        }

        private const string UaTypesXsd = "http://opcfoundation.org/UA/2008/02/Types.xsd";

        /// <summary>A Part 6 ExtensionObject value whose TypeId is <paramref name="typeId"/>.</summary>
        private static XmlElement ExtensionObjectValue(string typeId)
        {
            var doc = new XmlDocument();
            doc.LoadXml(
                $"""
                <uax:ExtensionObject xmlns:uax="{UaTypesXsd}">
                  <uax:TypeId><uax:Identifier>{typeId}</uax:Identifier></uax:TypeId>
                </uax:ExtensionObject>
                """);
            return doc.DocumentElement!;
        }

        /// <summary>The Identifier text of the ExtensionObject TypeId in an exported Value.</summary>
        private static string? TypeIdOf(XmlElement? value)
        {
            return value?
                .GetElementsByTagName("Identifier", UaTypesXsd)
                .OfType<XmlElement>()
                .FirstOrDefault()?.InnerText;
        }

        [Fact]
        public async Task Import_StoresAnExtensionObjectTypeIdAsANamespaceUri()
        {
            await using var db = _fixture.CreateNewContext();
            var ns = NewNamespaces();

            var model = await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(ns));

            var attributes = await db.Nodes.AsNoTracking()
                .Where(n => n.ModelId == model.Id && n.NodeId == $"nsu={ns.Model};i=1001")
                .Select(n => n.Attributes)
                .FirstAsync();

            var typeId = attributes?["Value"]?["UaTypeId"]?.GetValue<string>();

            // The document-local "ns=2" must not reach storage: nothing downstream can tell what
            // it meant, and the next document's ns=2 is a different namespace.
            Assert.Equal($"nsu={ns.Zulu};i=5009", typeId);
        }

        [Fact]
        public async Task Export_RewritesAnExtensionObjectTypeIdForTheExportedNamespaceOrder()
        {
            await using var db = _fixture.CreateNewContext();
            var ns = NewNamespaces();

            await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(ns));
            var exported = await NodeSetConverter.CreateNodeSetAsync(db, ns.Model, "1.0.0");

            // Export sorts the dependencies, so alpha and zulu swap places relative to the source
            // file: zulu was ns=2 on the way in and is ns=3 on the way out. Asserted rather than
            // assumed — it is the whole premise of the test.
            Assert.Equal([ns.Model, ns.Alpha, ns.Zulu], exported.NamespaceUris);

            var information = (Opc.Ua.Export.UAVariable)(exported.Items ?? [])
                .First(i => i.BrowseName == "1:Information");

            // The DataType attribute has always been rewritten. The Value's TypeId names the same
            // namespace and has to agree with it — otherwise it resolves to alpha, which has no
            // i=5009, and the file is quietly corrupt.
            Assert.Equal("ns=3;i=3006", information.DataType);
            Assert.Equal("ns=3;i=5009", TypeIdOf(information.Value));
        }

        [Fact]
        public async Task Export_KeepsAValueOnlyNamespaceOutOfRequiredModels()
        {
            await using var db = _fixture.CreateNewContext();
            var ns = NewNamespaces();

            // Drop the Variable that made alpha a structural reference: now alpha is named ONLY by
            // the value's TypeId. Per Part 6 that is an opaque identifier — it needs a
            // NamespaceUris entry so the index round-trips, but the model does not depend on
            // alpha being loadable, so it must not become a RequiredModel.
            var nodeSet = BuildNodeSet(ns);
            var information = (Opc.Ua.Export.UAVariable)nodeSet.Items![0];
            information.DataType = "ns=2;i=3006";                       // zulu, still structural
            information.Value = ExtensionObjectValue("ns=3;i=5009");    // alpha, value-only
            nodeSet.Items = [information];

            await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);
            var exported = await NodeSetConverter.CreateNodeSetAsync(db, ns.Model, "1.0.0");

            Assert.Contains(ns.Alpha, exported.NamespaceUris ?? []);
            Assert.DoesNotContain(
                ns.Alpha,
                (exported.Models?[0].RequiredModel ?? []).Select(r => r.ModelUri));

            // And the index it was given is the one the value actually uses.
            var index = Array.IndexOf(exported.NamespaceUris!, ns.Alpha) + 1;
            var reExported = (Opc.Ua.Export.UAVariable)(exported.Items ?? [])
                .First(i => i.BrowseName == "1:Information");
            Assert.Equal($"ns={index};i=5009", TypeIdOf(reExported.Value));
        }
    }

    /// <summary>
    /// The converter underneath, without a database. Reading and writing a Value have to agree
    /// about what a namespace table is: the writer has always taken one, the reader had not, and
    /// a reader that cannot resolve <c>ns=N</c> leaves the document-local index in place.
    /// </summary>
    public class Part6ValueNamespaceTests
    {
        private const string UaTypesXsd = "http://opcfoundation.org/UA/2008/02/Types.xsd";
        private const string Zulu = "urn:test:part6ns:zulu";
        private const string Alpha = "urn:test:part6ns:alpha";

        private static XmlElement ExtensionObjectValue(string typeId)
        {
            var doc = new XmlDocument();
            doc.LoadXml(
                $"""
                <uax:ExtensionObject xmlns:uax="{UaTypesXsd}">
                  <uax:TypeId><uax:Identifier>{typeId}</uax:Identifier></uax:TypeId>
                </uax:ExtensionObject>
                """);
            return doc.DocumentElement!;
        }

        [Fact]
        public void Read_ResolvesTheExtensionObjectTypeIdAgainstTheSuppliedTable()
        {
            // ns=2 is the second entry: Zulu.
            var json = Part6Variant.ReadXmlValueAsJsonText(
                ExtensionObjectValue("ns=2;i=5009"), null, [Alpha, Zulu]);

            var typeId = JsonNode.Parse(json!)?["UaTypeId"]?.GetValue<string>();
            Assert.Equal($"nsu={Zulu};i=5009", typeId);
        }

        [Fact]
        public void ReadThenWrite_FollowsTheTargetTableRatherThanTheSourceOne()
        {
            // In: Zulu is ns=2. Out: Zulu is ns=1. The identifier has to move with it.
            var json = Part6Variant.ReadXmlValueAsJsonText(
                ExtensionObjectValue("ns=2;i=5009"), null, [Alpha, Zulu]);

            var written = Part6Variant.WriteJsonTextValue(json, null, null, [Zulu, Alpha]);

            var typeId = written?
                .GetElementsByTagName("Identifier", UaTypesXsd)
                .OfType<XmlElement>()
                .FirstOrDefault()?.InnerText;

            Assert.Equal("ns=1;i=5009", typeId);
        }

        [Fact]
        public void Read_WithoutATableLeavesTheIndexAlone()
        {
            // The documented fallback, and the reason every caller that has a table must pass it:
            // with nothing to resolve against there is no honest answer, so the index survives.
            var json = Part6Variant.ReadXmlValueAsJsonText(ExtensionObjectValue("ns=2;i=5009"));

            var typeId = JsonNode.Parse(json!)?["UaTypeId"]?.GetValue<string>();
            Assert.Equal("ns=2;i=5009", typeId);
        }
    }
}
