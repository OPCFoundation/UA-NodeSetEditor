extern alias JsonNodeSet;

using System.Text;
using System.Xml.Linq;
using JsonNodeSet::Opc.Ua.NodeSetSerializer;
using JsonNodeSet::Opc.Ua.NodeSetSerializer.DataTypeDictionaries;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// The BSD / XSD generator reproduces what the OPC Foundation ships. DI's dictionaries are
    /// the reference: the generator's output must describe exactly the same types, fields and
    /// values, compared semantically (per type, with every prefix resolved to its namespace).
    /// </summary>
    public class DataTypeDictionaryGeneratorTests
    {
        private const string DiUri = "http://opcfoundation.org/UA/DI/";
        private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

        private static string DiPath => Path.Combine(AppContext.BaseDirectory, "TestNodeSets", "Opc.Ua.Di.NodeSet2.xml");

        private static AddressSpace LoadDi()
        {
            var space = new AddressSpace();
            CoreNodeSetLoader.EnsureCoreLoaded(space);
            var serializer = new JsonNodeSet::NodeSetTool.NodeSetSerializer();
            serializer.LoadXml(DiPath);
            serializer.LoadInto(space);
            space.ComputeDataTypeForms();
            return space;
        }

        /// <summary>The schema DI ships under the given folder (i=93 BSD, i=92 XSD).</summary>
        private static string Shipped(string folder)
        {
            var doc = XDocument.Load(DiPath);
            var aliases = doc.Root!.Element(Ua + "Aliases")!.Elements(Ua + "Alias")
                .ToDictionary(a => a.Attribute("Alias")!.Value, a => a.Value.Trim());
            string Resolve(string? v) => v != null && aliases.TryGetValue(v, out var id) ? id : v ?? "";

            var dict = doc.Root.Elements(Ua + "UAVariable").Single(v =>
            {
                var refs = v.Element(Ua + "References")!.Elements(Ua + "Reference").ToList();
                return refs.Any(r => Resolve(r.Attribute("ReferenceType")?.Value) == "i=40" && r.Value.Trim() == "i=72")
                    && refs.Any(r => Resolve(r.Attribute("ReferenceType")?.Value) == "i=47"
                        && r.Attribute("IsForward")?.Value == "false" && r.Value.Trim() == folder);
            });
            var base64 = string.Concat(dict.Element(Ua + "Value")!.Elements().First().Value.Where(c => !char.IsWhiteSpace(c)));
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }

        /// <summary>Type name → canonical description, prefixes resolved, documentation ignored.</summary>
        private static Dictionary<string, string> Canonical(string schema, bool binary)
        {
            var root = XDocument.Parse(schema).Root!;
            string Q(XElement scope, string? value)
            {
                if (string.IsNullOrEmpty(value)) return "";
                var colon = value.IndexOf(':');
                if (colon < 0) return value;
                var ns = scope.GetNamespaceOfPrefix(value[..colon])?.NamespaceName ?? value[..colon];
                var local = value[(colon + 1)..];
                return "{" + ns + "}" + (local == "CharArray" ? "String" : local);
            }

            var result = new Dictionary<string, string>();
            foreach (var e in root.Elements())
            {
                var name = e.Attribute(binary ? "Name" : "name")?.Value;
                if (name == null) continue;
                var sb = new StringBuilder(e.Name.LocalName);
                foreach (var x in e.DescendantsAndSelf())
                {
                    // XSD defaults are spelled out by some generators and not by others
                    // (maxOccurs="1" is the default; nillable differs between Core and companion
                    // style), so they are not part of what is compared.
                    var attributes = x.Attributes()
                        .Where(a => !a.IsNamespaceDeclaration)
                        .Where(a => !(a.Name.LocalName == "maxOccurs" && a.Value == "1"))
                        .Where(a => a.Name.LocalName != "nillable");
                    foreach (var a in attributes.OrderBy(a => a.Name.LocalName))
                    {
                        var v = a.Name.LocalName is "TypeName" or "BaseType" or "type" or "base" or "SourceType"
                            ? Q(x, a.Value) : a.Value;
                        sb.Append(' ').Append(x.Name.LocalName).Append('.').Append(a.Name.LocalName).Append('=').Append(v);
                    }
                }
                result[e.Name.LocalName + ":" + name] = sb.ToString();
            }
            return result;
        }

        [Fact]
        public void BinarySchema_DescribesWhatDiShips()
        {
            var generator = new DataTypeDictionaryGenerator(LoadDi(), DiUri);

            var expected = Canonical(Shipped("i=93"), binary: true);
            var actual = Canonical(generator.GenerateBinarySchema(), binary: true);

            Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
            foreach (var (type, description) in expected)
                Assert.Equal(description, actual[type]);
            Assert.Empty(generator.Warnings);
        }

        [Fact]
        public void XmlSchema_DescribesWhatDiShips()
        {
            var generator = new DataTypeDictionaryGenerator(LoadDi(), DiUri);

            var expected = Canonical(Shipped("i=92"), binary: false);
            var actual = Canonical(generator.GenerateXmlSchema(), binary: false);

            Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
            foreach (var (type, description) in expected)
                Assert.Equal(description, actual[type]);
        }

        [Fact]
        public void Output_IsDeterministic()
        {
            var space = LoadDi();
            var a = new DataTypeDictionaryGenerator(space, DiUri);
            var b = new DataTypeDictionaryGenerator(space, DiUri);
            Assert.Equal(a.GenerateBinarySchema(), b.GenerateBinarySchema());
            Assert.Equal(a.GenerateXmlSchema(), b.GenerateXmlSchema());
        }

        [Fact]
        public void Sync_OfAConsistentModel_RewritesOnlyTheBlobs_AndThenNothing()
        {
            var space = LoadDi();
            var before = space.Nodes.Select(n => n.NodeId).ToHashSet();

            var first = DataTypeDictionarySync.Sync(space, DiUri, createIfMissing: false);
            Assert.True(first.HasDictionaries);
            Assert.Empty(first.CreatedNodes);
            Assert.Empty(first.DeletedNodeIds);
            Assert.Empty(first.AddedReferences);
            Assert.Empty(first.RemovedReferences);
            Assert.Equal(0, first.DescriptionsUpdated);

            // Same nodes, same NodeIds.
            Assert.Equal(before, space.Nodes.Select(n => n.NodeId).ToHashSet());

            var second = DataTypeDictionarySync.Sync(space, DiUri, createIfMissing: false);
            Assert.False(second.Changed);
        }

        [Fact]
        public void Sync_WithoutCreate_LeavesAModelWithoutDictionariesAlone()
        {
            var space = LoadDi();
            // Core is loaded but is not the model asked about; a model URI with no nodes has none.
            var result = DataTypeDictionarySync.Sync(space, "http://test.example.org/UA/Empty/", createIfMissing: false);
            Assert.False(result.HasDictionaries);
            Assert.False(result.Changed);
        }
    }
}
