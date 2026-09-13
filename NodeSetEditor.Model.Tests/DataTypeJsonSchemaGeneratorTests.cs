using System.Text.Json.Nodes;
using NodeSetEditor.Model;

namespace NodeSetEditor.Model.Tests
{
    public class DataTypeJsonSchemaGeneratorTests
    {
        // ---------- Built-in scalars ----------

        [Fact]
        public void Boolean_BuiltIn_Produces_Boolean_Schema()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("i=1", new TestResolver());

            Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string?)schema["$schema"]);
            Assert.Equal("boolean", (string?)schema["type"]);
        }

        [Fact]
        public void String_BuiltIn_Produces_String_Schema()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("i=12", new TestResolver());
            Assert.Equal("string", (string?)schema["type"]);
        }

        [Fact]
        public void Int32_Has_Bounds()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("i=6", new TestResolver());
            Assert.Equal("integer", (string?)schema["type"]);
            Assert.Equal("Int32", (string?)schema["format"]);
            Assert.Equal(int.MinValue, (long)schema["minimum"]!);
            Assert.Equal(int.MaxValue, (long)schema["maximum"]!);
        }

        [Fact]
        public void Int64_UInt64_Are_Strings_Per_Part6()
        {
            // Part 6 §5.4 encodes Int64 / UInt64 as JSON strings — JS Numbers
            // can't hold the full 64-bit range losslessly. The schema must
            // declare these fields as strings so the editor stays string-mode
            // end to end.
            var int64 = DataTypeJsonSchemaGenerator.Generate("i=8", new TestResolver());
            Assert.Equal("string", (string?)int64["type"]);
            Assert.Equal("Int64", (string?)int64["format"]);
            Assert.Equal("^-?[0-9]+$", (string?)int64["pattern"]);

            var uint64 = DataTypeJsonSchemaGenerator.Generate("i=9", new TestResolver());
            Assert.Equal("string", (string?)uint64["type"]);
            Assert.Equal("UInt64", (string?)uint64["format"]);
            Assert.Equal("^[0-9]+$", (string?)uint64["pattern"]);
        }

        [Fact]
        public void DateTime_Uses_DateTime_Format()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("i=13", new TestResolver());
            Assert.Equal("string", (string?)schema["type"]);
            Assert.Equal("date-time", (string?)schema["format"]);
        }

        [Fact]
        public void Guid_Uses_Uuid_Format()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("i=14", new TestResolver());
            Assert.Equal("uuid", (string?)schema["format"]);
        }

        [Fact]
        public void LocalizedText_Has_Locale_And_Text_Properties()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("i=21", new TestResolver());
            Assert.Equal("object", (string?)schema["type"]);
            var props = schema["properties"]!.AsObject();
            Assert.NotNull(props["Locale"]);
            Assert.NotNull(props["Text"]);
            var required = schema["required"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Contains("Text", required);
            Assert.False((bool)schema["additionalProperties"]!);
        }

        // ---------- Subtypes of built-ins ----------

        [Fact]
        public void Custom_Subtype_Of_String_Resolves_To_String()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=100"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=100",
                    BrowseName = "1:DurationString",
                    SuperTypeId = "i=12",
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=100", resolver);
            Assert.Equal("string", (string?)schema["type"]);
            Assert.Equal("DurationString", (string?)schema["title"]);
        }

        // ---------- Enumerations ----------

        [Fact]
        public void Enumeration_Emits_Enum_With_EnumNames()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=200"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=200",
                    BrowseName = "Color",
                    SuperTypeId = "i=29",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Red", Value = 0 },
                            new() { Name = "Green", Value = 1 },
                            new() { Name = "Blue", Value = 2 },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=200", resolver);
            Assert.Equal("integer", (string?)schema["type"]);
            var values = schema["enum"]!.AsArray().Select(n => (int)n!).ToList();
            Assert.Equal(new[] { 0, 1, 2 }, values);
            var names = schema["x-enumNames"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Equal(new[] { "Red", "Green", "Blue" }, names);
        }

        [Fact]
        public void Enumeration_With_Sparse_Values_Preserves_Explicit_Numbers()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=201"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=201",
                    BrowseName = "Severity",
                    SuperTypeId = "i=29",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Low", Value = 100 },
                            new() { Name = "High", Value = 800 },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=201", resolver);
            var values = schema["enum"]!.AsArray().Select(n => (int)n!).ToList();
            Assert.Equal(new[] { 100, 800 }, values);
        }

        // ---------- OptionSet ----------

        [Fact]
        public void OptionSet_Emits_Integer_With_OptionSet_Annotation()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=300"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=300",
                    BrowseName = "AccessFlags",
                    SuperTypeId = "i=7", // UInt32
                    Definition = new DataTypeDefinitionEntry
                    {
                        IsOptionSet = true,
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Read", Value = 0 },
                            new() { Name = "Write", Value = 1 },
                            new() { Name = "Execute", Value = 2 },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=300", resolver);
            Assert.Equal("integer", (string?)schema["type"]);
            Assert.Equal("UInt32", (string?)schema["format"]);
            var options = schema["x-optionSet"]!.AsArray();
            Assert.Equal(3, options.Count);
            Assert.Equal("Read", (string?)options[0]!["name"]);
            Assert.Equal(0, (int)options[0]!["value"]!);
            Assert.Equal("Execute", (string?)options[2]!["name"]);
            Assert.Equal(2, (int)options[2]!["value"]!);
        }

        // ---------- Structures ----------

        [Fact]
        public void Structure_Emits_Object_With_Required_Fields()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=400"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=400",
                    BrowseName = "Person",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Name", DataType = "i=12" },
                            new() { Name = "Age", DataType = "i=6" },
                            new() { Name = "Nickname", DataType = "i=12", IsOptional = true },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=400", resolver);
            Assert.Equal("object", (string?)schema["type"]);
            Assert.Equal("Person", (string?)schema["title"]);
            Assert.False((bool)schema["additionalProperties"]!);

            var props = schema["properties"]!.AsObject();
            Assert.Equal("string", (string?)props["Name"]!["type"]);
            Assert.Equal("integer", (string?)props["Age"]!["type"]);
            Assert.Equal("string", (string?)props["Nickname"]!["type"]);

            var required = schema["required"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Contains("Name", required);
            Assert.Contains("Age", required);
            Assert.DoesNotContain("Nickname", required);
        }

        [Fact]
        public void Structure_Field_With_ValueRank_1_Wraps_In_Array()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=401"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=401",
                    BrowseName = "Tags",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Labels", DataType = "i=12", ValueRank = 1 },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=401", resolver);
            var labels = schema["properties"]!["Labels"]!.AsObject();
            Assert.Equal("array", (string?)labels["type"]);
            Assert.Equal("string", (string?)labels["items"]!["type"]);
        }

        [Fact]
        public void Structure_Field_With_ArrayDimensions_Sets_MinMax_Items()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=402"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=402",
                    BrowseName = "FixedVector",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "V", DataType = "i=11", ValueRank = 1, ArrayDimensions = "3" },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=402", resolver);
            var v = schema["properties"]!["V"]!.AsObject();
            Assert.Equal("array", (string?)v["type"]);
            Assert.Equal(3, (int)v["minItems"]!);
            Assert.Equal(3, (int)v["maxItems"]!);
        }

        [Fact]
        public void Structure_Field_With_ValueRank_2_Wraps_In_Nested_Arrays()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=403"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=403",
                    BrowseName = "Matrix",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "M", DataType = "i=11", ValueRank = 2, ArrayDimensions = "2,3" },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=403", resolver);
            var outer = schema["properties"]!["M"]!.AsObject();
            Assert.Equal("array", (string?)outer["type"]);
            Assert.Equal(2, (int)outer["minItems"]!);
            var inner = outer["items"]!.AsObject();
            Assert.Equal("array", (string?)inner["type"]);
            Assert.Equal(3, (int)inner["minItems"]!);
            Assert.Equal("number", (string?)inner["items"]!["type"]);
        }

        // ---------- Inheritance ----------

        [Fact]
        public void Structure_Inherits_Fields_From_Supertype()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=500"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=500",
                    BrowseName = "Animal",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Name", DataType = "i=12" },
                        },
                    },
                },
                ["ns=1;i=501"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=501",
                    BrowseName = "Dog",
                    SuperTypeId = "ns=1;i=500",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Breed", DataType = "i=12" },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=501", resolver);
            var props = schema["properties"]!.AsObject();
            Assert.Contains("Name", props.Select(p => p.Key));
            Assert.Contains("Breed", props.Select(p => p.Key));
            var required = schema["required"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Contains("Name", required);
            Assert.Contains("Breed", required);
        }

        [Fact]
        public void Subclass_Field_Override_Wins_Over_Inherited_Field()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=510"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=510",
                    BrowseName = "Base",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Code", DataType = "i=12" },
                        },
                    },
                },
                ["ns=1;i=511"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=511",
                    BrowseName = "Derived",
                    SuperTypeId = "ns=1;i=510",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Code", DataType = "i=6" }, // override as Int32
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=511", resolver);
            var code = schema["properties"]!["Code"]!.AsObject();
            Assert.Equal("integer", (string?)code["type"]);
        }

        // ---------- Nested structs ----------

        [Fact]
        public void Structure_With_Nested_Struct_Field_Inlines_Subschema()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=600"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=600",
                    BrowseName = "Address",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Street", DataType = "i=12" },
                            new() { Name = "City", DataType = "i=12" },
                        },
                    },
                },
                ["ns=1;i=601"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=601",
                    BrowseName = "Customer",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Name", DataType = "i=12" },
                            new() { Name = "Home", DataType = "ns=1;i=600" },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=601", resolver);
            var home = schema["properties"]!["Home"]!.AsObject();
            Assert.Equal("object", (string?)home["type"]);
            var homeProps = home["properties"]!.AsObject();
            Assert.NotNull(homeProps["Street"]);
            Assert.NotNull(homeProps["City"]);
            // No $defs needed when there are no cycles
            Assert.Null(schema["$defs"]);
        }

        // ---------- Cycles ----------

        [Fact]
        public void Recursive_Structure_Uses_Defs_And_Ref()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=700"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=700",
                    BrowseName = "TreeNode",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Label", DataType = "i=12" },
                            new() { Name = "Child", DataType = "ns=1;i=700", IsOptional = true },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=700", resolver);
            var defs = schema["$defs"]!.AsObject();
            Assert.True(defs.Count >= 1, "Expected at least one $defs entry for the cycle");

            // The recursive field should be a $ref pointing into $defs.
            var child = schema["properties"]!["Child"]!.AsObject();
            var refStr = (string?)child["$ref"];
            Assert.NotNull(refStr);
            Assert.StartsWith("#/$defs/", refStr);
        }

        // ---------- Union ----------

        [Fact]
        public void Union_Emits_OneOf_Branches()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=800"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=800",
                    BrowseName = "Choice",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        IsUnion = true,
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "AsInt", DataType = "i=6" },
                            new() { Name = "AsText", DataType = "i=12" },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=800", resolver);
            Assert.Equal("object", (string?)schema["type"]);
            var oneOf = schema["oneOf"]!.AsArray();
            Assert.Equal(2, oneOf.Count);

            var intBranch = oneOf[0]!.AsObject();
            Assert.Equal("integer", (string?)intBranch["properties"]!["AsInt"]!["type"]);
            var intReq = intBranch["required"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Equal(new[] { "AsInt" }, intReq);

            var strBranch = oneOf[1]!.AsObject();
            Assert.Equal("string", (string?)strBranch["properties"]!["AsText"]!["type"]);
        }

        // ---------- Unresolvable ----------

        [Fact]
        public void Unresolvable_Custom_DataType_Returns_Permissive_Schema()
        {
            var schema = DataTypeJsonSchemaGenerator.Generate("ns=99;i=9999", new TestResolver());
            // No type constraint; description names the unresolved id.
            Assert.Null(schema["type"]);
            var desc = (string?)schema["description"];
            Assert.Contains("ns=99;i=9999", desc);
        }

        // ---------- ValueRank == 0 (one-or-more dim, unknown) ----------

        [Fact]
        public void Structure_Field_With_ValueRank_Zero_Wraps_Array_Without_Bounds()
        {
            var resolver = new TestResolver
            {
                ["ns=1;i=900"] = new DataTypeSchemaInfo
                {
                    NodeId = "ns=1;i=900",
                    BrowseName = "Loose",
                    SuperTypeId = "i=22",
                    Definition = new DataTypeDefinitionEntry
                    {
                        Fields = new List<FieldDefinitionEntry>
                        {
                            new() { Name = "Bag", DataType = "i=12", ValueRank = 0 },
                        },
                    },
                },
            };

            var schema = DataTypeJsonSchemaGenerator.Generate("ns=1;i=900", resolver);
            var bag = schema["properties"]!["Bag"]!.AsObject();
            Assert.Equal("array", (string?)bag["type"]);
            Assert.Null(bag["minItems"]);
            Assert.Null(bag["maxItems"]);
        }

        // -----------------------------------------------------------
        //                  Test resolver
        // -----------------------------------------------------------
        private sealed class TestResolver : IDataTypeResolver
        {
            private readonly Dictionary<string, DataTypeSchemaInfo> _byId =
                new(StringComparer.Ordinal);

            public DataTypeSchemaInfo this[string nodeId]
            {
                set => _byId[nodeId] = value;
            }

            public DataTypeSchemaInfo? Resolve(string nodeId)
                => _byId.TryGetValue(nodeId, out var info) ? info : null;
        }
    }
}
