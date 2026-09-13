using System.Text.Json.Nodes;

namespace NodeSetEditor.Model
{
    /// <summary>
    /// Resolves a DataType NodeId to the information the schema generator needs:
    /// browse name, supertype id, own fields. Implementations adapt whatever model
    /// shape they hold (live AddressSpace, DB rows, etc.) to <see cref="DataTypeSchemaInfo"/>.
    /// </summary>
    public interface IDataTypeResolver
    {
        DataTypeSchemaInfo? Resolve(string nodeId);
    }

    public sealed class DataTypeSchemaInfo
    {
        public required string NodeId { get; init; }
        public string? BrowseName { get; init; }
        public string? Description { get; init; }

        /// <summary>The immediate supertype's NodeId, or null at the root of the chain.</summary>
        public string? SuperTypeId { get; init; }

        /// <summary>Own (non-inherited) DataTypeDefinition. Null for built-ins and abstract bases.</summary>
        public DataTypeDefinitionEntry? Definition { get; init; }
    }

    /// <summary>
    /// Generates a JSON Schema (draft 2020-12) describing a single value of a
    /// given OPC UA DataType, suitable for driving a schema-aware JSON editor.
    ///
    /// Top-level ValueRank is intentionally not honored here — the editor wraps the
    /// generator's output for arrays and lets the user navigate elements one at a
    /// time. Field-level ValueRank inside Structures IS honored, since that maps
    /// to a literal JSON array in the structure body.
    /// </summary>
    public static class DataTypeJsonSchemaGenerator
    {
        private const string Draft = "https://json-schema.org/draft/2020-12/schema";

        // OPC UA built-in DataType NodeIds (Part 6, Table A.1) keyed by NodeId i=N.
        // Value is the BuiltInType enum number.
        private static readonly Dictionary<string, int> BuiltInIds = new(StringComparer.Ordinal)
        {
            ["i=1"] = 1,   // Boolean
            ["i=2"] = 2,   // SByte
            ["i=3"] = 3,   // Byte
            ["i=4"] = 4,   // Int16
            ["i=5"] = 5,   // UInt16
            ["i=6"] = 6,   // Int32
            ["i=7"] = 7,   // UInt32
            ["i=8"] = 8,   // Int64
            ["i=9"] = 9,   // UInt64
            ["i=10"] = 10, // Float
            ["i=11"] = 11, // Double
            ["i=12"] = 12, // String
            ["i=13"] = 13, // DateTime
            ["i=14"] = 14, // Guid
            ["i=15"] = 15, // ByteString
            ["i=16"] = 16, // XmlElement
            ["i=17"] = 17, // NodeId
            ["i=18"] = 18, // ExpandedNodeId
            ["i=19"] = 19, // StatusCode
            ["i=20"] = 20, // QualifiedName
            ["i=21"] = 21, // LocalizedText
            ["i=22"] = 22, // Structure (abstract)
            ["i=23"] = 23, // DataValue
            ["i=24"] = 24, // Variant
            ["i=26"] = 12, // BaseDataType (Variant) — treat as Variant; surface as String for editor
            ["i=27"] = 6,  // Number → editor as number
            ["i=28"] = 7,  // Integer → unsigned
            ["i=29"] = 6,  // Enumeration (abstract; root has no fields → Int32 fallback)
        };

        public static JsonObject Generate(string nodeId, IDataTypeResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(nodeId);
            ArgumentNullException.ThrowIfNull(resolver);

            var defs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var building = new HashSet<string>(StringComparer.Ordinal);

            var schema = BuildSchema(nodeId, resolver, defs, building);

            var root = new JsonObject { ["$schema"] = Draft };
            foreach (var (k, v) in schema)
                root[k] = v?.DeepClone();

            if (defs.Count > 0)
            {
                var defsObj = new JsonObject();
                foreach (var (k, v) in defs)
                    defsObj[k] = v.DeepClone();
                root["$defs"] = defsObj;
            }

            return root;
        }

        private static JsonObject BuildSchema(
            string nodeId,
            IDataTypeResolver resolver,
            Dictionary<string, JsonObject> defs,
            HashSet<string> building)
        {
            // Cycle: emit $ref and ensure the def is materialized.
            if (building.Contains(nodeId))
            {
                EnsureDef(nodeId, resolver, defs);
                return new JsonObject { ["$ref"] = "#/$defs/" + DefKey(nodeId) };
            }

            // Direct built-in lookup short-circuits any resolver work.
            if (BuiltInIds.TryGetValue(nodeId, out var directBuiltIn))
                return BuildBuiltIn(directBuiltIn);

            var info = resolver.Resolve(nodeId);
            if (info == null)
            {
                return new JsonObject
                {
                    ["description"] = $"Unresolved DataType {nodeId}",
                };
            }

            var classification = Classify(info, resolver);

            building.Add(nodeId);
            try
            {
                JsonObject schema = classification.Kind switch
                {
                    TypeKind.BuiltIn => BuildBuiltIn(classification.BuiltInId ?? 12),
                    TypeKind.Enumeration => BuildEnumeration(info),
                    TypeKind.OptionSet => BuildOptionSet(info, classification.BuiltInId ?? 7),
                    TypeKind.Union => BuildStructureLike(nodeId, info, resolver, defs, building, isUnion: true),
                    TypeKind.Structure => BuildStructureLike(nodeId, info, resolver, defs, building, isUnion: false),
                    _ => new JsonObject(),
                };

                AnnotateTitleAndDescription(schema, info);
                return schema;
            }
            finally
            {
                building.Remove(nodeId);
            }
        }

        private enum TypeKind { BuiltIn, Enumeration, OptionSet, Structure, Union }

        private readonly struct Classification
        {
            public TypeKind Kind { get; init; }
            public int? BuiltInId { get; init; }
        }

        /// <summary>
        /// Walks the supertype chain to determine the effective category of the type.
        /// </summary>
        private static Classification Classify(DataTypeSchemaInfo info, IDataTypeResolver resolver)
        {
            // OptionSet is signalled on the leaf definition; supertype is typically a UInt.
            if (info.Definition?.IsOptionSet == true)
                return new Classification { Kind = TypeKind.OptionSet, BuiltInId = WalkToBuiltIn(info, resolver) ?? 7 };

            // Walk up to find the abstract base or built-in.
            string? current = info.NodeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            DataTypeSchemaInfo? cursor = info;
            while (current != null && visited.Add(current))
            {
                if (BuiltInIds.TryGetValue(current, out var b))
                {
                    // Distinguish abstract bases that imply a kind:
                    //   i=29 Enumeration  → Enumeration
                    //   i=22 Structure    → Structure / Union (decided on the leaf definition)
                    if (current == "i=29")
                        return new Classification { Kind = TypeKind.Enumeration, BuiltInId = 6 };
                    if (current == "i=22")
                    {
                        var isUnion = info.Definition?.IsUnion == true;
                        return new Classification { Kind = isUnion ? TypeKind.Union : TypeKind.Structure, BuiltInId = 22 };
                    }
                    return new Classification { Kind = TypeKind.BuiltIn, BuiltInId = b };
                }

                // The cursor steps as well, so we can read the next supertype.
                cursor = current == info.NodeId ? info : resolver.Resolve(current);
                current = cursor?.SuperTypeId;
            }

            // Couldn't classify — leaf has a Definition with fields → assume Structure.
            if (info.Definition?.Fields is { Count: > 0 })
                return new Classification { Kind = info.Definition.IsUnion == true ? TypeKind.Union : TypeKind.Structure, BuiltInId = 22 };

            return new Classification { Kind = TypeKind.BuiltIn, BuiltInId = 12 };
        }

        private static int? WalkToBuiltIn(DataTypeSchemaInfo info, IDataTypeResolver resolver)
        {
            string? current = info.SuperTypeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (current != null && visited.Add(current))
            {
                if (BuiltInIds.TryGetValue(current, out var b)) return b;
                var next = resolver.Resolve(current);
                current = next?.SuperTypeId;
            }
            return null;
        }

        // Builds the schema fragment for an OPC UA built-in type. Each fragment
        // carries a `title` (the type name) so editors can show "NodeId" /
        // "DateTime" etc. as a hover hint, but no `description` — descriptions
        // are reserved for what the NodeSet itself supplies on a DataType or
        // a structure field, so the user sees real metadata not boilerplate.
        private static JsonObject BuildBuiltIn(int builtInId)
        {
            JsonObject schema = builtInId switch
            {
                1 => new JsonObject { ["type"] = "boolean" },
                2 => IntSchema(sbyte.MinValue, sbyte.MaxValue, "SByte"),
                3 => IntSchema(byte.MinValue, byte.MaxValue, "Byte"),
                4 => IntSchema(short.MinValue, short.MaxValue, "Int16"),
                5 => IntSchema(ushort.MinValue, ushort.MaxValue, "UInt16"),
                6 => IntSchema(int.MinValue, int.MaxValue, "Int32"),
                7 => IntSchema(uint.MinValue, uint.MaxValue, "UInt32"),
                // Int64 / UInt64 are JSON STRINGS per Part 6 §5.4. JS Numbers
                // (used by the editor) are IEEE 754 doubles and cannot represent
                // the full 64-bit integer range losslessly — typing the value as
                // a string keeps the editor and JSON wire format precision-safe
                // all the way through. The pattern enforces "digits only" with
                // an optional leading minus on Int64.
                8 => new JsonObject { ["type"] = "string", ["format"] = "Int64", ["pattern"] = "^-?[0-9]+$" },
                9 => new JsonObject { ["type"] = "string", ["format"] = "UInt64", ["pattern"] = "^[0-9]+$" },
                10 => new JsonObject { ["type"] = "number", ["format"] = "float" },
                11 => new JsonObject { ["type"] = "number", ["format"] = "double" },
                12 => new JsonObject { ["type"] = "string" },
                13 => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                14 => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                15 => new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" },
                16 => new JsonObject { ["type"] = "string" },
                17 => new JsonObject { ["type"] = "string", ["format"] = "UaNodeId" },
                18 => new JsonObject { ["type"] = "string", ["format"] = "UaExpandedNodeId" },
                19 => StatusCodeSchema(),
                20 => QualifiedNameSchema(),
                21 => LocalizedTextSchema(),
                22 => new JsonObject { ["type"] = "object" },
                23 => new JsonObject { ["type"] = "object" },
                24 => new JsonObject(),
                _ => new JsonObject { ["type"] = "string" },
            };

            if (schema["title"] == null)
            {
                var name = BuiltInIdToName(builtInId);
                if (name != null) schema["title"] = name;
            }
            return schema;
        }

        /// <summary>
        /// Maps an OPC UA Part 6 built-in type number to its canonical name.
        /// Returns null for values outside the documented 1..24 range.
        /// </summary>
        private static string? BuiltInIdToName(int builtInId) => builtInId switch
        {
            1 => "Boolean",
            2 => "SByte",
            3 => "Byte",
            4 => "Int16",
            5 => "UInt16",
            6 => "Int32",
            7 => "UInt32",
            8 => "Int64",
            9 => "UInt64",
            10 => "Float",
            11 => "Double",
            12 => "String",
            13 => "DateTime",
            14 => "Guid",
            15 => "ByteString",
            16 => "XmlElement",
            17 => "NodeId",
            18 => "ExpandedNodeId",
            19 => "StatusCode",
            20 => "QualifiedName",
            21 => "LocalizedText",
            22 => "ExtensionObject",
            23 => "DataValue",
            24 => "Variant",
            _ => null,
        };

        private static JsonObject IntSchema(long min, long max, string format) => new()
        {
            ["type"] = "integer",
            ["format"] = format,
            ["minimum"] = min,
            ["maximum"] = max,
        };

        private static JsonObject IntSchema(ulong min, ulong max, string format)
        {
            // JsonValue supports ulong; max can exceed long for UInt64.
            return new JsonObject
            {
                ["type"] = "integer",
                ["format"] = format,
                ["minimum"] = min,
                ["maximum"] = JsonValue.Create(max),
            };
        }

        private static JsonObject QualifiedNameSchema() => new()
        {
            ["type"] = "string",
            ["format"] = "UaQualifiedName",
        };

        private static JsonObject LocalizedTextSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["Locale"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Locale",
                },
                ["Text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Text",
                },
            },
            ["required"] = new JsonArray { "Text" },
            ["additionalProperties"] = false,
        };

        private static JsonObject StatusCodeSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["Code"] = IntSchema(0, uint.MaxValue, "UInt32"),
                ["Symbol"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Symbol",
                },
            },
            ["required"] = new JsonArray { "Code" },
            ["additionalProperties"] = false,
        };

        private static JsonObject BuildEnumeration(DataTypeSchemaInfo info)
        {
            var schema = new JsonObject { ["type"] = "integer", ["format"] = "Int32" };
            var fields = info.Definition?.Fields;
            if (fields is { Count: > 0 })
            {
                var values = new JsonArray();
                var names = new JsonArray();
                int next = 0;
                foreach (var f in fields)
                {
                    int v = f.Value ?? next;
                    next = v + 1;
                    values.Add(v);
                    names.Add(f.Name ?? "");
                }
                schema["enum"] = values;
                schema["x-enumNames"] = names;
            }
            return schema;
        }

        private static JsonObject BuildOptionSet(DataTypeSchemaInfo info, int builtInId)
        {
            var schema = BuildBuiltIn(builtInId);
            var fields = info.Definition?.Fields;
            if (fields is { Count: > 0 })
            {
                var arr = new JsonArray();
                int next = 0;
                foreach (var f in fields)
                {
                    int bit = f.Value ?? next;
                    next = bit + 1;
                    var opt = new JsonObject
                    {
                        ["value"] = bit,
                        ["name"] = f.Name ?? "",
                    };
                    var desc = FirstDescription(f.Description);
                    if (!string.IsNullOrEmpty(desc)) opt["description"] = desc;
                    arr.Add(opt);
                }
                schema["x-optionSet"] = arr;
            }
            return schema;
        }

        private static JsonObject BuildStructureLike(
            string nodeId,
            DataTypeSchemaInfo info,
            IDataTypeResolver resolver,
            Dictionary<string, JsonObject> defs,
            HashSet<string> building,
            bool isUnion)
        {
            // Collect inherited + own fields, with own overriding by name.
            var fields = CollectFields(info, resolver);

            if (isUnion)
            {
                var oneOf = new JsonArray();
                foreach (var f in fields)
                {
                    var fieldSchema = BuildFieldSchema(f, resolver, defs, building);
                    var props = new JsonObject { [f.Name ?? ""] = fieldSchema };
                    var branch = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = new JsonArray { f.Name ?? "" },
                        ["additionalProperties"] = false,
                    };
                    oneOf.Add(branch);
                }
                return new JsonObject
                {
                    ["type"] = "object",
                    ["oneOf"] = oneOf,
                };
            }
            else
            {
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var f in fields)
                {
                    var name = f.Name ?? "";
                    properties[name] = BuildFieldSchema(f, resolver, defs, building);
                    if (f.IsOptional != true) required.Add(name);
                }

                var schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["additionalProperties"] = false,
                };
                if (required.Count > 0) schema["required"] = required;
                return schema;
            }
        }

        private static JsonNode BuildFieldSchema(
            FieldDefinitionEntry f,
            IDataTypeResolver resolver,
            Dictionary<string, JsonObject> defs,
            HashSet<string> building)
        {
            var inner = string.IsNullOrEmpty(f.DataType)
                ? new JsonObject { ["description"] = "Field with no DataType" }
                : BuildSchema(f.DataType, resolver, defs, building);

            // The schema's `title` carries the underlying TYPE name ("NodeId",
            // "LocalizedText", "Argument", etc.) so editors can show it as a
            // tooltip / header on the field. The field name itself comes from
            // the parent's property key — we don't override the title here.
            //
            // Description is taken straight from the DataTypeDefinition field
            // entry — we don't synthesize one. If the NodeSet author didn't
            // describe the field, no description is emitted.
            var fieldDesc = FirstDescription(f.Description);
            if (!string.IsNullOrEmpty(fieldDesc))
                inner["description"] = fieldDesc;

            // Field-level ValueRank: scalar (-1, -2, -3 or null) → no wrap.
            // ValueRank == 0 (≥1 dim, unknown) → single array.
            // ValueRank >= 1 → wrap N times; if ArrayDimensions[d] > 0, set min/max equal.
            int rank = f.ValueRank ?? -1;
            if (rank == 0)
            {
                return new JsonObject { ["type"] = "array", ["items"] = inner };
            }
            if (rank >= 1)
            {
                var dims = ParseArrayDimensions(f.ArrayDimensions, rank);
                JsonNode current = inner;
                for (int i = rank - 1; i >= 0; i--)
                {
                    var arr = new JsonObject { ["type"] = "array", ["items"] = current };
                    if (dims != null && dims[i] > 0)
                    {
                        arr["minItems"] = dims[i];
                        arr["maxItems"] = dims[i];
                    }
                    current = arr;
                }
                return current;
            }

            return inner;
        }

        private static int[]? ParseArrayDimensions(string? arrayDimensions, int rank)
        {
            if (string.IsNullOrEmpty(arrayDimensions)) return null;
            var parts = arrayDimensions.Split(',');
            var dims = new int[rank];
            for (int i = 0; i < rank; i++)
            {
                if (i < parts.Length && int.TryParse(parts[i].Trim(), out var d) && d >= 0)
                    dims[i] = d;
                else
                    dims[i] = 0;
            }
            return dims;
        }

        /// <summary>
        /// Walks the supertype chain bottom-up and returns the merged field list,
        /// with own (most-derived) fields overriding inherited fields by name.
        /// Stops at the abstract Structure root (i=22).
        /// </summary>
        private static List<FieldDefinitionEntry> CollectFields(
            DataTypeSchemaInfo leaf, IDataTypeResolver resolver)
        {
            var chain = new List<DataTypeSchemaInfo>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            DataTypeSchemaInfo? cursor = leaf;
            while (cursor != null && visited.Add(cursor.NodeId))
            {
                chain.Add(cursor);
                if (cursor.SuperTypeId == null || cursor.SuperTypeId == "i=22") break;
                cursor = resolver.Resolve(cursor.SuperTypeId);
            }

            // Most-derived (leaf) is at index 0. Walk parents-first to allow override.
            var byName = new Dictionary<string, FieldDefinitionEntry>(StringComparer.Ordinal);
            var order = new List<string>();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var fields = chain[i].Definition?.Fields;
                if (fields == null) continue;
                foreach (var f in fields)
                {
                    var name = f.Name ?? "";
                    if (!byName.ContainsKey(name)) order.Add(name);
                    byName[name] = f;
                }
            }
            return order.Select(n => byName[n]).ToList();
        }

        private static void EnsureDef(
            string nodeId,
            IDataTypeResolver resolver,
            Dictionary<string, JsonObject> defs)
        {
            var key = DefKey(nodeId);
            if (defs.ContainsKey(key)) return;
            // Reserve the slot to break cycles, then build into it.
            defs[key] = new JsonObject();
            var subDefs = defs; // share so nested $refs land in the same root $defs
            var building = new HashSet<string>(StringComparer.Ordinal);
            var built = BuildSchema(nodeId, resolver, subDefs, building);
            // Replace the placeholder with the real shape.
            defs[key] = built;
        }

        private static string DefKey(string nodeId)
        {
            // JSON Pointer-safe key: replace problematic chars.
            return nodeId
                .Replace('/', '_')
                .Replace('~', '_')
                .Replace(';', '_')
                .Replace('=', '_')
                .Replace(':', '_');
        }

        private static void AnnotateTitleAndDescription(JsonObject schema, DataTypeSchemaInfo info)
        {
            // The custom DataType's BrowseName is the more specific name (e.g.
            // "DurationString" rather than the underlying "String") — always
            // override the built-in's title with it when a BrowseName is known.
            if (!string.IsNullOrEmpty(info.BrowseName))
                schema["title"] = StripBrowseNamePrefix(info.BrowseName);
            if (!string.IsNullOrEmpty(info.Description) && schema["description"] == null)
                schema["description"] = info.Description;
        }

        private static string StripBrowseNamePrefix(string browseName)
        {
            // BrowseName from AddressSpace may be "nsu=...;Name" or "1:Name". Strip prefix.
            if (browseName.StartsWith("nsu=", StringComparison.Ordinal))
            {
                var semi = browseName.IndexOf(';');
                if (semi > 0) return browseName.Substring(semi + 1);
            }
            var colon = browseName.IndexOf(':');
            if (colon > 0 && int.TryParse(browseName.AsSpan(0, colon), out _))
                return browseName.Substring(colon + 1);
            return browseName;
        }

        private static string? FirstDescription(List<LocalizedTextEntry>? descriptions)
        {
            if (descriptions == null) return null;
            foreach (var d in descriptions)
                if (!string.IsNullOrEmpty(d.Value)) return d.Value;
            return null;
        }
    }
}
