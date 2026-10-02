using System.Globalization;
using System.Text;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// What a CSV column means in OPC UA terms. One column per role for every role
    /// except <see cref="Property"/>, which any number of columns can take.
    /// </summary>
    public enum CsvColumnRole
    {
        /// <summary>Column is not imported.</summary>
        Ignore,
        /// <summary>The row variable's BrowseName (and DisplayName, unless a DisplayName column exists).</summary>
        BrowseName,
        DisplayName,
        Description,
        /// <summary>The row variable's DataType attribute, resolved from the cell text.</summary>
        DataType,
        /// <summary>The row variable's Value.</summary>
        Value,
        /// <summary>EngineeringUnits property (EUInformation); promotes the row to AnalogItemType.</summary>
        EngineeringUnits,
        /// <summary>EURange.high; promotes the row to AnalogItemType.</summary>
        EuRangeHigh,
        /// <summary>EURange.low; promotes the row to AnalogItemType.</summary>
        EuRangeLow,
        InstrumentRangeHigh,
        InstrumentRangeLow,
        /// <summary>Per-row ModellingRule, overriding the request's default.</summary>
        ModellingRule,
        /// <summary>A Property child on the row variable, named after the column.</summary>
        Property,
    }

    /// <summary>A parsed CSV file: one header row plus the data rows.</summary>
    public sealed class CsvTable
    {
        public List<string> Headers { get; init; } = new();
        public List<List<string>> Rows { get; init; } = new();

        /// <summary>The delimiter <see cref="CsvTypeMapping.Parse"/> sniffed, for display.</summary>
        public char Delimiter { get; init; } = ',';

        /// <summary>
        /// A row can be shorter than the header (trailing empty fields are often omitted),
        /// so every cell read goes through here rather than indexing directly.
        /// </summary>
        public static string Cell(List<string> row, int index) =>
            index >= 0 && index < row.Count ? row[index] : string.Empty;
    }

    /// <summary>
    /// The CSV import ruleset: parsing, the column-header → <see cref="CsvColumnRole"/>
    /// proposal, DataType resolution, and BrowseName sanitizing. Pure string logic with no
    /// dependency on the address space or the database, so it can be exercised directly from
    /// tests. Units are resolved separately, against the embedded published tables — see
    /// <see cref="UnitLookup"/>.
    /// </summary>
    public static class CsvTypeMapping
    {
        private static readonly char[] DelimiterCandidates = { ',', ';', '\t', '|' };

        #region Parsing

        /// <summary>
        /// Parses a CSV/TSV stream: RFC 4180 quoting (double quotes, <c>""</c> escapes,
        /// embedded delimiters and newlines), CR / LF / CRLF line endings, UTF-8 BOM
        /// tolerated. The delimiter is sniffed from the header line.
        ///
        /// The first record with any non-blank field becomes the header; fully blank
        /// records are dropped everywhere.
        /// </summary>
        public static CsvTable Parse(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ParseText(reader.ReadToEnd());
        }

        /// <summary>Text overload of <see cref="Parse(Stream)"/>; the tests use it directly.</summary>
        public static CsvTable ParseText(string text)
        {
            // A UTF-8 BOM survives when the text came from a string rather than a stream.
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];

            var delimiter = SniffDelimiter(text);
            var records = SplitRecords(text, delimiter);

            var headers = new List<string>();
            var rows = new List<List<string>>();
            foreach (var record in records)
            {
                if (record.All(string.IsNullOrWhiteSpace)) continue;
                if (headers.Count == 0)
                {
                    headers = NormalizeHeaders(record);
                    continue;
                }
                rows.Add(record);
            }

            return new CsvTable { Headers = headers, Rows = rows, Delimiter = delimiter };
        }

        /// <summary>
        /// Picks the delimiter by counting candidates in the first record — outside quotes,
        /// so a quoted address like "Smith, John" in the header can't win. Ties and a
        /// header with no candidate at all fall back to a comma.
        /// </summary>
        private static char SniffDelimiter(string text)
        {
            var counts = new Dictionary<char, int>();
            foreach (var c in DelimiterCandidates) counts[c] = 0;

            var inQuotes = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is an escape, not a terminator.
                    if (inQuotes && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    inQuotes = !inQuotes;
                    continue;
                }
                if (inQuotes) continue;
                if (c == '\r' || c == '\n') break;
                if (counts.ContainsKey(c)) counts[c]++;
            }

            var best = ',';
            var bestCount = 0;
            foreach (var c in DelimiterCandidates)
            {
                if (counts[c] > bestCount) { best = c; bestCount = counts[c]; }
            }
            return best;
        }

        private static List<List<string>> SplitRecords(string text, char delimiter)
        {
            var records = new List<List<string>>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var sawAnyChar = false;

            void EndField()
            {
                fields.Add(field.ToString());
                field.Clear();
            }

            void EndRecord()
            {
                EndField();
                records.Add(fields.ToList());
                fields.Clear();
                sawAnyChar = false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        sawAnyChar = true;
                        break;
                    case '\r':
                        // Swallow the LF of a CRLF pair so it doesn't open an empty record.
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        EndRecord();
                        break;
                    case '\n':
                        EndRecord();
                        break;
                    default:
                        if (c == delimiter) { EndField(); sawAnyChar = true; }
                        else { field.Append(c); sawAnyChar = true; }
                        break;
                }
            }

            // Trailing record with no terminating newline.
            if (sawAnyChar || field.Length > 0 || fields.Count > 0) EndRecord();

            // Fields keep their inner whitespace but not their outer: "  Name " is the Name column.
            foreach (var record in records)
            {
                for (var i = 0; i < record.Count; i++) record[i] = record[i].Trim();
            }

            return records;
        }

        /// <summary>
        /// Gives every header column a usable, unique name. A blank header becomes
        /// <c>Column{n}</c>; a repeat gets a numeric suffix, because the header is what
        /// names a Property child and duplicates would collide.
        /// </summary>
        private static List<string> NormalizeHeaders(List<string> raw)
        {
            var result = new List<string>(raw.Count);
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < raw.Count; i++)
            {
                var header = string.IsNullOrWhiteSpace(raw[i]) ? $"Column{i + 1}" : raw[i].Trim();
                if (seen.TryGetValue(header, out var count))
                {
                    seen[header] = count + 1;
                    header = $"{header}_{count + 1}";
                }
                else seen[header] = 1;
                result.Add(header);
            }
            return result;
        }

        #endregion

        #region Column role ruleset

        /// <summary>
        /// Header aliases per role. Matched against the header normalized to lowercase
        /// with every non-alphanumeric character dropped, so "Scan Rate", "scan_rate"
        /// and "SCAN-RATE" all collapse to the same key.
        /// </summary>
        private static readonly Dictionary<string, CsvColumnRole> HeaderAliases = BuildHeaderAliases();

        private static Dictionary<string, CsvColumnRole> BuildHeaderAliases()
        {
            var map = new Dictionary<string, CsvColumnRole>(StringComparer.Ordinal);

            void Add(CsvColumnRole role, params string[] aliases)
            {
                foreach (var a in aliases) map[Normalize(a)] = role;
            }

            Add(CsvColumnRole.BrowseName,
                "name", "browsename", "browse name", "tag", "tagname", "tag name",
                "signal", "signalname", "item", "itemname", "identifier", "id", "variable",
                "point", "pointname");
            Add(CsvColumnRole.DisplayName, "displayname", "display name", "label", "caption", "title");
            Add(CsvColumnRole.Description, "description", "desc", "comment", "comments", "notes", "note");
            Add(CsvColumnRole.DataType, "type", "datatype", "data type", "valuetype", "value type", "dtype");
            Add(CsvColumnRole.Value, "value", "defaultvalue", "default value", "default", "initialvalue", "initial value");
            Add(CsvColumnRole.EngineeringUnits,
                "units", "unit", "uom", "u o m", "eu", "engineeringunits", "engineering units", "measurementunit");
            Add(CsvColumnRole.EuRangeHigh,
                "high", "max", "maximum", "highlimit", "high limit", "euhigh", "eu high",
                "rangehigh", "range high", "maxvalue", "max value", "upperlimit", "upper limit", "hi");
            Add(CsvColumnRole.EuRangeLow,
                "low", "min", "minimum", "lowlimit", "low limit", "eulow", "eu low",
                "rangelow", "range low", "minvalue", "min value", "lowerlimit", "lower limit", "lo");
            Add(CsvColumnRole.InstrumentRangeHigh,
                "instrumenthigh", "instrument high", "instrumentmax", "instrument max",
                "rawhigh", "raw high", "rawmax", "instrumentrangehigh");
            Add(CsvColumnRole.InstrumentRangeLow,
                "instrumentlow", "instrument low", "instrumentmin", "instrument min",
                "rawlow", "raw low", "rawmin", "instrumentrangelow");
            Add(CsvColumnRole.ModellingRule,
                "modellingrule", "modelling rule", "modelingrule", "mandatory", "optional", "required");

            return map;
        }

        /// <summary>
        /// Roles that exactly one column may hold. A second column matching one of these
        /// falls back to <see cref="CsvColumnRole.Property"/> rather than silently
        /// overwriting the first.
        /// </summary>
        private static bool IsSingleWinner(CsvColumnRole role) => role != CsvColumnRole.Property
            && role != CsvColumnRole.Ignore;

        /// <summary>
        /// Proposes a role for every column. Unmatched headers become
        /// <see cref="CsvColumnRole.Property"/> — a site-specific column like "Scan Rate"
        /// is still data worth carrying onto the variable, and the user can flip it to
        /// Ignore in the confirm step.
        /// </summary>
        public static List<CsvColumnRole> ProposeRoles(IReadOnlyList<string> headers)
        {
            var roles = new List<CsvColumnRole>(headers.Count);
            var claimed = new HashSet<CsvColumnRole>();

            foreach (var header in headers)
            {
                var role = HeaderAliases.TryGetValue(Normalize(header), out var matched)
                    ? matched
                    : CsvColumnRole.Property;

                if (IsSingleWinner(role) && !claimed.Add(role)) role = CsvColumnRole.Property;
                roles.Add(role);
            }

            return roles;
        }

        /// <summary>Lowercases and strips every non-alphanumeric character.</summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        #endregion

        #region DataType resolution

        // Core (ns=0) DataType NodeIds.
        public const string Boolean = "i=1";
        public const string SByte = "i=2";
        public const string Byte = "i=3";
        public const string Int16 = "i=4";
        public const string UInt16 = "i=5";
        public const string Int32 = "i=6";
        public const string UInt32 = "i=7";
        public const string Int64 = "i=8";
        public const string UInt64 = "i=9";
        public const string Float = "i=10";
        public const string Double = "i=11";
        public const string String = "i=12";
        public const string DateTime = "i=13";
        public const string Guid = "i=14";
        public const string ByteString = "i=15";
        public const string LocalizedText = "i=21";
        public const string Duration = "i=290";

        /// <summary>
        /// Raw DataType cell text → core DataType NodeId. Covers both OPC UA spelling and
        /// the IEC 61131-3 names that show up in PLC-exported tag lists. Keys are
        /// <see cref="Normalize"/>d, so "Float 32", "FLOAT_32" and "float32" all match.
        /// </summary>
        private static readonly Dictionary<string, string> DataTypeAliases = BuildDataTypeAliases();

        private static Dictionary<string, string> BuildDataTypeAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string nodeId, params string[] aliases)
            {
                foreach (var a in aliases) map[Normalize(a)] = nodeId;
            }

            Add(Boolean, "boolean", "bool", "bit", "binary", "digital", "discrete", "flag", "logical");
            Add(SByte, "sbyte", "int8", "char", "signedbyte", "sint8");
            Add(Byte, "byte", "uint8", "usint", "unsignedbyte");
            Add(Int16, "int16", "short", "int", "sint", "integer16", "signedshort");
            Add(UInt16, "uint16", "ushort", "uint", "word", "unsignedshort");
            Add(Int32, "int32", "dint", "integer", "integer32", "long32", "signedint");
            Add(UInt32, "uint32", "udint", "dword", "unsignedint", "unsignedinteger");
            Add(Int64, "int64", "lint", "long", "bigint", "integer64");
            Add(UInt64, "uint64", "ulint", "qword", "unsignedlong");
            Add(Float, "float", "real", "single", "float32", "real32");
            Add(Double, "double", "lreal", "float64", "real64", "number", "numeric", "analog");
            Add(String, "string", "str", "text", "wstring", "varchar", "chararray", "alphanumeric");
            Add(DateTime, "datetime", "date", "time", "timestamp", "dt", "dateandtime", "tod", "timeofday");
            Add(Guid, "guid", "uuid");
            Add(ByteString, "bytestring", "bytes", "bytearray", "blob", "raw");
            Add(LocalizedText, "localizedtext", "localisedtext");
            Add(Duration, "duration", "elapsedtime");

            return map;
        }

        /// <summary>
        /// Resolves a DataType cell into a NodeId using the alias table alone. Returns null
        /// when nothing matches — <c>CsvTypeImportService</c> then tries the workspace's own
        /// DataType BrowseNames before falling back to the request's default.
        /// </summary>
        public static string? ResolveDataTypeAlias(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DataTypeAliases.TryGetValue(Normalize(raw), out var nodeId) ? nodeId : null;
        }

        /// <summary>
        /// Infers a DataType from a column's cell values, for Property columns and for
        /// rows in a CSV with no DataType column. Blank cells are ignored; a column with
        /// nothing in it is a String.
        /// </summary>
        public static string InferDataType(IEnumerable<string> values)
        {
            var any = false;
            var allBool = true;
            var allIntegral = true;
            var allNumeric = true;
            var needsInt64 = false;

            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                any = true;
                var value = raw.Trim();

                if (!IsTextualBoolean(value)) allBool = false;

                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    if (l < int.MinValue || l > int.MaxValue) needsInt64 = true;
                }
                else allIntegral = false;

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    allNumeric = false;

                if (!allBool && !allIntegral && !allNumeric) break;
            }

            if (!any) return String;
            if (allBool) return Boolean;
            if (allIntegral) return needsInt64 ? Int64 : Int32;
            if (allNumeric) return Double;
            return String;
        }

        /// <summary>
        /// True for the words a spreadsheet uses for a boolean. Deliberately excludes
        /// "0"/"1": a column of zeroes and ones is far more often a count or a bit
        /// position than a flag, and Int32 is the safer wrong guess.
        /// </summary>
        private static bool IsTextualBoolean(string value) => value.ToLowerInvariant() switch
        {
            "true" or "false" or "yes" or "no" or "y" or "n" or "on" or "off" => true,
            _ => false,
        };

        /// <summary>Parses a numeric cell for a Range bound. Null when it isn't a number.</summary>
        public static double? ParseNumber(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : null;
        }

        /// <summary>
        /// Reads a ModellingRule cell. A column headed "Mandatory" holding yes/no is as
        /// common as one headed "ModellingRule" holding the rule name, so both work.
        /// Returns null when the cell says nothing, leaving the request's default in place.
        /// </summary>
        public static string? ResolveModellingRule(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Normalize(raw) switch
            {
                "mandatory" or "m" or "required" or "req" or "true" or "yes" or "y" or "1" => "i=78",
                "optional" or "o" or "false" or "no" or "n" or "0" => "i=80",
                "optionalplaceholder" => "i=11508",
                "mandatoryplaceholder" => "i=11510",
                _ => null,
            };
        }

        #endregion

        #region Names

        // Characters that break a BrowseName or the browse paths built from it: the
        // separators of RelativePath syntax (Part 4, A.2) plus quotes and control chars.
        private static readonly char[] IllegalBrowseNameChars = { '/', '.', '&', '<', '>', ':', '"', '\'', '#', '!', ';', '=' };

        /// <summary>
        /// Makes a BrowseName out of arbitrary text: trims, collapses internal whitespace
        /// to single underscores, and drops the characters that would break a browse path.
        /// Returns an empty string when nothing usable is left, which the caller treats as
        /// "no name" rather than inventing one.
        /// </summary>
        public static string SanitizeBrowseName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var sb = new StringBuilder(raw.Length);
            var pendingSeparator = false;
            foreach (var c in raw.Trim())
            {
                if (char.IsControl(c)) continue;
                if (IllegalBrowseNameChars.Contains(c)) continue;
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) pendingSeparator = true;
                    continue;
                }
                if (pendingSeparator) { sb.Append('_'); pendingSeparator = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// An all-uppercase run this long or shorter is taken for an acronym (ID, PLC, UOM, EU)
        /// and left as it is; anything longer is a shouted word (SCAN_RATE) and gets
        /// title-cased. Without the distinction "PLC_ADDRESS" becomes "PlcAddress" or
        /// "SCAN_RATE" becomes "SCANRATE", and both read worse than the split.
        /// </summary>
        private const int AcronymLength = 3;

        /// <summary>
        /// Turns a column header into a PascalCase BrowseName: <c>scan_rate</c>,
        /// <c>scanRate</c>, <c>Scan Rate</c> and <c>SCAN_RATE</c> all become
        /// <c>ScanRate</c>, and <c>PLC_ADDRESS</c> becomes <c>PLCAddress</c>.
        ///
        /// Word boundaries are any non-alphanumeric character and any lower→upper transition,
        /// which is what makes camelCase split. Applied to names taken from a header, not to
        /// the row values a tag is named after — those are data, and are kept as authored (see
        /// <see cref="SanitizeBrowseName"/>).
        /// </summary>
        public static string PascalCaseBrowseName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var result = new StringBuilder(raw.Length);
            var word = new StringBuilder();

            void FlushWord()
            {
                if (word.Length == 0) return;
                var w = word.ToString();
                word.Clear();

                var hasLower = w.Any(char.IsLower);
                if (!hasLower && w.Length <= AcronymLength)
                {
                    result.Append(w);          // short all-caps run: an acronym, left alone
                    return;
                }

                result.Append(char.ToUpperInvariant(w[0]));
                // A shouted word loses its shouting; a mixed-case one keeps its interior, so
                // an acronym embedded in a longer word ("PLCAddress") survives.
                result.Append(hasLower ? w[1..] : w[1..].ToLowerInvariant());
            }

            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (!char.IsLetterOrDigit(c)) { FlushWord(); continue; }

                // camelCase boundary: a capital directly after a lowercase letter.
                if (char.IsUpper(c) && i > 0 && char.IsLower(raw[i - 1])) FlushWord();

                word.Append(c);
            }
            FlushWord();

            return result.ToString();
        }

        /// <summary>
        /// Makes each name unique within its parent by appending <c>_2</c>, <c>_3</c>, …
        /// A duplicate BrowseName under one parent is illegal in OPC UA, and a CSV with a
        /// repeated tag name is common enough that failing the whole import would be
        /// unhelpful. Case-insensitive, matching how the address space compares BrowseNames.
        /// </summary>
        public sealed class BrowseNameDeduper
        {
            private readonly Dictionary<string, int> _used = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Returns the name to use, and whether it had to be changed.</summary>
            public (string Name, bool WasDuplicate) Unique(string name)
            {
                if (_used.TryGetValue(name, out var count))
                {
                    // Keep bumping until the suffixed form is free too, so an input that
                    // already contains "Temp" and "Temp_2" can't collide on the rename.
                    string candidate;
                    do
                    {
                        count++;
                        candidate = $"{name}_{count}";
                    } while (_used.ContainsKey(candidate));

                    _used[name] = count;
                    _used[candidate] = 1;
                    return (candidate, true);
                }

                _used[name] = 1;
                return (name, false);
            }
        }

        /// <summary>
        /// Proposes the new Object's BrowseName from the CSV file name, PascalCased:
        /// "boiler tag-list.csv" → "BoilerTagList". A trailing "Type" is dropped, so
        /// "PumpType.csv" proposes the Object "Pump" and (via
        /// <see cref="ProposeTypeName"/>) the type "PumpType" rather than "PumpTypeType".
        /// A file name with nothing usable in it yields "CsvImport".
        /// </summary>
        public static string ProposeObjectName(string? fileName)
        {
            var stem = string.IsNullOrWhiteSpace(fileName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(fileName.Trim());

            // Same PascalCase rule the column headers get, so a file and a header spelled the
            // same way propose the same name.
            var name = PascalCaseBrowseName(stem);

            // A BrowseName may not start with a digit in most toolkits, and "2024Tags"
            // reads badly regardless.
            if (name.Length > 0 && char.IsDigit(name[0])) name = "N" + name;
            if (name.Length == 0) return "CsvImport";

            if (name.Length > 4 && name.EndsWith("Type", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];

            return name.Length == 0 ? "CsvImport" : name;
        }

        /// <summary>
        /// Proposes the ObjectType's BrowseName: the Object's name with "Type" appended.
        /// </summary>
        public static string ProposeTypeName(string? fileName) => ProposeObjectName(fileName) + "Type";

        #endregion
    }
}
