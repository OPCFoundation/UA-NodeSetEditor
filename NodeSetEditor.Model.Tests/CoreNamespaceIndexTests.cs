using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// http://opcfoundation.org/UA/ is ns=0 in EVERY NodeSet, including its own. The index is
    /// fixed by the spec rather than chosen per document, so neither import nor export may make
    /// it depend on context — the same Core node must land on the same stored id whichever way a
    /// file happens to spell it.
    /// </summary>
    [Collection("Database")]
    public class CoreNamespaceIndexTests
    {
        private const string UaCoreNamespace = "http://opcfoundation.org/UA/";

        private readonly DatabaseFixture _fixture;

        public CoreNamespaceIndexTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// A model with one ObjectType that names a Core node (BaseObjectType) as its supertype.
        /// <paramref name="namespaceUris"/> and the ns prefix on that reference are the variables.
        /// </summary>
        private static Opc.Ua.Export.UANodeSet BuildNodeSet(
            string uri, string[] namespaceUris, string supertypeNodeId)
        {
            return new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = namespaceUris,
                Models =
                [
                    new Opc.Ua.Export.ModelTableEntry
                    {
                        ModelUri = uri,
                        Version = "1.0.0",
                        ModelVersion = "1.0.0",
                        PublicationDate = DateTime.UtcNow,
                        PublicationDateSpecified = true,
                    }
                ],
                Items =
                [
                    new Opc.Ua.Export.UAObjectType
                    {
                        NodeId = $"ns={Array.IndexOf(namespaceUris, uri) + 1};i=1001",
                        BrowseName = $"{Array.IndexOf(namespaceUris, uri) + 1}:CoreNsType",
                        DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "CoreNsType" }],
                        References =
                        [
                            new Opc.Ua.Export.Reference
                            {
                                ReferenceType = "i=45", // HasSubtype
                                IsForward = false,
                                Value = supertypeNodeId,
                            }
                        ],
                    },
                ]
            };
        }

        [Fact]
        public async Task Import_StoresACoreNodeBare_WhetherOrNotTheFileListsCoreInNamespaceUris()
        {
            await using var db = _fixture.CreateNewContext();

            // (a) The normal spelling: Core is implicit, the reference says "i=58".
            var plainUri = $"urn:test:corens:plain:{Guid.NewGuid():N}";
            var plain = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(plainUri, [plainUri], "i=58"));

            // (b) The awkward spelling: Core is ALSO listed, so ns=2 resolves to it as well.
            var listedUri = $"urn:test:corens:listed:{Guid.NewGuid():N}";
            var listed = await NodeSetConverter.StoreNodeSetAsync(
                db, BuildNodeSet(listedUri, [listedUri, UaCoreNamespace], "ns=2;i=58"));

            var plainSuperType = await db.Nodes.AsNoTracking()
                .Where(n => n.ModelId == plain.Id).Select(n => n.SuperTypeId).FirstAsync();
            var listedSuperType = await db.Nodes.AsNoTracking()
                .Where(n => n.ModelId == listed.Id).Select(n => n.SuperTypeId).FirstAsync();

            // Both are the same Core node, so both must be stored the same way — bare.
            Assert.Equal("i=58", plainSuperType);
            Assert.Equal("i=58", listedSuperType);
        }

        [Fact]
        public async Task Export_OfCoreItselfDoesNotListCoreInNamespaceUris()
        {
            // The fixture truncates the database, so there is no real Core row to export. Build
            // a stand-in under the Core URI — at version 0.0.1, so that if this ever runs against
            // a database that does hold the real Core, "latest" still resolves to the real one.
            await using var db = _fixture.CreateNewContext();

            const string version = "0.0.1";
            var nodeSet = new Opc.Ua.Export.UANodeSet
            {
                NamespaceUris = null, // Core declares no namespace of its own
                Models =
                [
                    new Opc.Ua.Export.ModelTableEntry
                    {
                        ModelUri = UaCoreNamespace,
                        Version = version,
                        ModelVersion = version,
                        PublicationDate = DateTime.UtcNow,
                        PublicationDateSpecified = true,
                    }
                ],
                Items =
                [
                    new Opc.Ua.Export.UAObjectType
                    {
                        NodeId = "i=90001",
                        BrowseName = "CoreNsStandIn",
                        DisplayName = [new Opc.Ua.Export.LocalizedText { Value = "CoreNsStandIn" }],
                    },
                ]
            };

            var model = await NodeSetConverter.StoreNodeSetAsync(db, nodeSet);
            try
            {
                var exported = await NodeSetConverter.CreateNodeSetAsync(db, UaCoreNamespace, version);

                // Listing it would announce Core as ns=1 while its nodes are written bare (ns=0).
                Assert.DoesNotContain(UaCoreNamespace, exported.NamespaceUris ?? []);

                // And its nodes really are bare, which is what makes the above the right call.
                var sample = (exported.Items ?? []).Select(i => i.NodeId).First(id => id != null);
                Assert.DoesNotContain("ns=", sample!);
            }
            finally
            {
                await db.Nodes.Where(n => n.ModelId == model.Id).ExecuteDeleteAsync();
                await db.References.Where(r => r.ModelId == model.Id).ExecuteDeleteAsync();
                await db.Models.Where(m => m.Id == model.Id).ExecuteDeleteAsync();
            }
        }

        [Fact]
        public async Task Export_OfANonCoreModelStillListsItsOwnNamespaceFirst()
        {
            // The Core rule must not disturb the ordinary case: a vendor model is ns=1 in its own
            // file, and Core stays implicit.
            await using var db = _fixture.CreateNewContext();

            var uri = $"urn:test:corens:vendor:{Guid.NewGuid():N}";
            await NodeSetConverter.StoreNodeSetAsync(db, BuildNodeSet(uri, [uri], "i=58"));

            var exported = await NodeSetConverter.CreateNodeSetAsync(db, uri);

            Assert.Equal(uri, (exported.NamespaceUris ?? [])[0]);
            Assert.DoesNotContain(UaCoreNamespace, exported.NamespaceUris ?? []);
        }
    }
}
