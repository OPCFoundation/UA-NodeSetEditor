using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// The CSV ruleset on its own: parsing, header → role proposal, DataType and unit
/// resolution, name sanitizing. No server, no database — this is the part that has to be
/// right before any of it reaches the address space.
/// </summary>
public class CsvTypeMappingTests
{
    [Fact]
    public void Parse_ReadsHeaderAndRows()
    {
        var table = CsvTypeMapping.ParseText("Name,Type\nTemp01,REAL\nPressure02,LREAL\n");

        Assert.Equal(new[] { "Name", "Type" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "Temp01", "REAL" }, table.Rows[0]);
    }

    [Fact]
    public void Parse_HandlesQuotedFieldsWithDelimitersAndNewlines()
    {
        var table = CsvTypeMapping.ParseText("Name,Description\r\nTemp01,\"Inlet, outlet\"\r\nTemp02,\"line one\nline two\"\r\n");

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Inlet, outlet", table.Rows[0][1]);
        Assert.Equal("line one\nline two", table.Rows[1][1]);
    }

    [Fact]
    public void Parse_HandlesEscapedQuotes()
    {
        var table = CsvTypeMapping.ParseText("Name\n\"He said \"\"hi\"\"\"\n");

        Assert.Equal("He said \"hi\"", table.Rows[0][0]);
    }

    [Fact]
    public void Parse_SniffsSemicolonDelimiter()
    {
        var table = CsvTypeMapping.ParseText("Name;Type;Units\nTemp01;REAL;degC\n");

        Assert.Equal(';', table.Delimiter);
        Assert.Equal(new[] { "Name", "Type", "Units" }, table.Headers);
        Assert.Equal("degC", table.Rows[0][2]);
    }

    [Fact]
    public void Parse_SniffsTabDelimiter()
    {
        var table = CsvTypeMapping.ParseText("Name\tType\nTemp01\tREAL\n");

        Assert.Equal('\t', table.Delimiter);
        Assert.Equal(new[] { "Name", "Type" }, table.Headers);
    }

    [Fact]
    public void Parse_StripsByteOrderMark()
    {
        var table = CsvTypeMapping.ParseText("﻿Name,Type\nTemp01,REAL\n");

        Assert.Equal("Name", table.Headers[0]);
    }

    [Fact]
    public void Parse_TolaratesShortRows()
    {
        var table = CsvTypeMapping.ParseText("Name,Type,Units\nTemp01,REAL\n");

        Assert.Equal("", CsvTable.Cell(table.Rows[0], 2));
    }

    [Fact]
    public void Parse_NamesBlankAndDuplicateHeaders()
    {
        var table = CsvTypeMapping.ParseText("Name,,Name\na,b,c\n");

        Assert.Equal(new[] { "Name", "Column2", "Name_2" }, table.Headers);
    }

    [Fact]
    public void ProposeRoles_MapsTheCommonHeaderSpellings()
    {
        var roles = CsvTypeMapping.ProposeRoles(
            new[] { "Tag Name", "Data_Type", "UOM", "High Limit", "low limit", "Notes", "Scan Rate" });

        Assert.Equal(
            new[]
            {
                CsvColumnRole.BrowseName, CsvColumnRole.DataType, CsvColumnRole.EngineeringUnits,
                CsvColumnRole.EuRangeHigh, CsvColumnRole.EuRangeLow, CsvColumnRole.Description,
                CsvColumnRole.Property,
            },
            roles);
    }

    [Fact]
    public void ProposeRoles_SecondClaimOfASingleWinnerRoleBecomesAProperty()
    {
        var roles = CsvTypeMapping.ProposeRoles(new[] { "Name", "Tag" });

        Assert.Equal(CsvColumnRole.BrowseName, roles[0]);
        Assert.Equal(CsvColumnRole.Property, roles[1]);
    }

    [Theory]
    [InlineData("REAL", "i=10")]
    [InlineData("LREAL", "i=11")]
    [InlineData("BOOL", "i=1")]
    [InlineData("DINT", "i=6")]
    [InlineData("Float32", "i=10")]
    [InlineData("float 32", "i=10")]
    [InlineData("String", "i=12")]
    [InlineData("WORD", "i=5")]
    public void ResolveDataTypeAlias_CoversUaAndIec61131Spellings(string raw, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.ResolveDataTypeAlias(raw));
    }

    [Fact]
    public void ResolveDataTypeAlias_ReturnsNullForSomethingItDoesNotKnow()
    {
        Assert.Null(CsvTypeMapping.ResolveDataTypeAlias("SPARE"));
    }

    [Theory]
    [InlineData(new[] { "true", "false" }, "i=1")]
    [InlineData(new[] { "1", "2", "3" }, "i=6")]
    [InlineData(new[] { "1.5", "2" }, "i=11")]
    [InlineData(new[] { "DB1.DBD0" }, "i=12")]
    [InlineData(new string[0], "i=12")]
    public void InferDataType_PicksTheNarrowestTypeThatFitsEveryValue(string[] values, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.InferDataType(values));
    }

    [Fact]
    public void InferDataType_UsesInt64ForValuesOutsideInt32()
    {
        Assert.Equal("i=8", CsvTypeMapping.InferDataType(new[] { "5000000000" }));
    }

    [Theory]
    // The official display name, plus the ASCII spellings a spreadsheet can hold.
    [InlineData("°C")]
    [InlineData("degC")]
    [InlineData("deg C")]
    [InlineData("DEGC")]
    public void Resolve_FindsCelsiusInIec62720HoweverItIsSpelled(string raw)
    {
        var unit = UnitLookup.Resolve(raw);

        Assert.True(unit.IsKnown);
        // IEC 62720 is searched first, so its degree Celsius wins over UNECE's (4408652).
        Assert.Equal(705741427, unit.UnitId);
        Assert.Equal(UnitLookup.Iec62720NamespaceUri, unit.NamespaceUri);
        // The published display name, not the text as written.
        Assert.Equal("°C", unit.DisplayName);
        Assert.Equal("degree Celsius", unit.Description);
    }

    [Theory]
    // Units an engineering tag list actually carries, all of which IEC 62720 defines.
    [InlineData("bar", 705744467)]
    [InlineData("psi", 705748497)]
    [InlineData("%", 705741328)]
    [InlineData("m", 705748566)]
    [InlineData("mm", 705749714)]
    [InlineData("kg", 705746740)]
    [InlineData("Hz", 705742576)]
    [InlineData("V", 705743670)]
    [InlineData("A", 705742353)]
    [InlineData("W", 705744406)]
    [InlineData("s", 705750770)]
    public void Resolve_PrefersIec62720(string raw, int expectedUnitId)
    {
        var unit = UnitLookup.Resolve(raw);

        Assert.Equal(expectedUnitId, unit.UnitId);
        Assert.Equal(UnitLookup.Iec62720NamespaceUri, unit.NamespaceUri);
    }

    [Fact]
    public void Resolve_FallsBackToUneceForATradeOnlyUnit()
    {
        // "piece" is a UN/CEFACT counting unit for trade with no engineering equivalent, so
        // IEC 62720 cannot answer and the fallback does — and the NamespaceUri says which.
        var unit = UnitLookup.Resolve("piece");

        Assert.True(unit.IsKnown);
        Assert.Equal(4732983, unit.UnitId);
        Assert.Equal(UnitLookup.UneceNamespaceUri, unit.NamespaceUri);
    }

    [Fact]
    public void Resolve_LeavesASymbolMeaningWhatTheStandardsSayItMeans()
    {
        // A bare "C" is a coulomb and "F" a farad, even though a tag list most likely means a
        // temperature. Guessing otherwise would silently swap one valid unit for another;
        // "degC" is how a temperature is reached.
        Assert.Equal("coulomb", UnitLookup.Resolve("C").Description);
        Assert.Equal("farad", UnitLookup.Resolve("F").Description);
        Assert.NotEqual(UnitLookup.Resolve("C").UnitId, UnitLookup.Resolve("degC").UnitId);
    }

    [Fact]
    public void Resolve_LeavesTheNamespaceBlankForAnUnknownUnit()
    {
        var unit = UnitLookup.Resolve("widgets/fortnight");

        Assert.False(unit.IsKnown);
        Assert.Equal(0, unit.UnitId);
        // An unknown unit belongs to neither system, so claiming one would be a lie.
        Assert.Equal(string.Empty, unit.NamespaceUri);
        Assert.Equal("widgets/fortnight", unit.DisplayName);
        Assert.Null(unit.Description);
    }

    [Fact]
    public void Resolve_ReachesSymbolsAKeyboardCannotType()
    {
        Assert.True(UnitLookup.Resolve("ohm").IsKnown);
        Assert.True(UnitLookup.Resolve("um").IsKnown);
    }

    [Theory]
    // Case is the whole meaning of an SI prefix, so matching is case-sensitive and an
    // upper-cased export does not silently resolve to a unit orders of magnitude out.
    [InlineData("mV", "millivolt")]
    [InlineData("MV", "megavolt")]
    [InlineData("mg", "milligram")]
    [InlineData("Mg", "megagram")]
    [InlineData("mm", "millimetre")]
    [InlineData("Mm", "megametre")]
    [InlineData("t", "tonne")]
    [InlineData("T", "tesla")]
    [InlineData("s", "second")]
    [InlineData("S", "siemens")]
    public void Resolve_TreatsCaseAsMeaningful(string raw, string expectedDescription)
    {
        Assert.Equal(expectedDescription, UnitLookup.Resolve(raw).Description);
    }

    [Fact]
    public void Resolve_DoesNotFallBackToACaseInsensitiveMatch()
    {
        // "BAR" is not how the tables spell bar, and guessing that it meant "bar" is the same
        // guess that would turn "MM" into a megametre.
        Assert.False(UnitLookup.Resolve("BAR").IsKnown);
        Assert.False(UnitLookup.Resolve("HZ").IsKnown);
    }

    [Theory]
    // The disambiguation rules, one case each. Both candidates in every pair are real rows
    // from the published tables; the rule says which one a tag list means.
    [InlineData("ms", "millisecond")]                   // not the spin quantum number
    [InlineData("pH", "pH (potential of hydrogen)")]    // not picohenry
    [InlineData("rad", "radian")]                       // not radiation
    [InlineData("rev/min", "revolution per minute")]    // not the plural spelling
    [InlineData("st", "stere")]                         // not stone (UK)
    public void Resolve_AppliesTheDisambiguationRulesInIec62720(string raw, string expectedDescription)
    {
        var unit = UnitLookup.Resolve(raw);

        Assert.Equal(expectedDescription, unit.Description);
        Assert.Equal(UnitLookup.Iec62720NamespaceUri, unit.NamespaceUri);
    }

    [Fact]
    public void Resolve_AppliesADisambiguationRuleInTheUneceFallback()
    {
        // "r/min" is UNECE's spelling (IEC 62720 writes "rev/min"), so this is the one rule
        // whose effect is only visible in the fallback table.
        var unit = UnitLookup.Resolve("r/min");

        Assert.Equal("revolution per minute", unit.Description);
        Assert.Equal(UnitLookup.UneceNamespaceUri, unit.NamespaceUri);
    }

    [Theory]
    // These names are ambiguous in UNECE but unambiguous in IEC 62720, which is searched
    // first — so IEC answers and the UNECE ambiguity is never reached. The rules for them
    // still settle the UNECE group, which is why it is not in the unresolved list.
    [InlineData("kvar", "kilovolt ampere reactive")]  // reactive kVA, not megavar
    [InlineData("MW", "megawatt")]                    // not module width
    [InlineData("mil", "milli-inch")]                 // the same unit UNECE calls "mil"
    // IEC 62720 has one "V", plain volt. The "V" → volt DC rule settles UNECE's three-way
    // group but cannot be reached, so an imported "V" is still a plain volt.
    [InlineData("V", "volt")]
    public void Resolve_LetsIec62720AnswerNamesUneceIsAmbiguousAbout(string raw, string expectedDescription)
    {
        var unit = UnitLookup.Resolve(raw);

        Assert.Equal(expectedDescription, unit.Description);
        Assert.Equal(UnitLookup.Iec62720NamespaceUri, unit.NamespaceUri);
    }

    [Fact]
    public void UnresolvedCollisions_AreExactlyTheKnownOnes()
    {
        // What is left after the rules, after groups that merely list one unit twice, and
        // after dropping UNECE names IEC 62720 already decides. Pinned rather than counted:
        // when a table update introduces a new ambiguity this fails and names it, which is the
        // prompt to write a rule.
        //
        // The one survivor is a defect in the published file rather than a choice — two
        // genuinely different units, gram and milligram per cubic metre divided by cubic
        // centimetre per minute, sharing one display name. No preference can be right.
        Assert.Equal(
            new[] { "(g/m³)/(cm³/min)" },
            UnitLookup.UnresolvedIec62720Collisions);

        // Every UNECE collision is on a name IEC 62720 answers, so none is reachable.
        Assert.Empty(UnitLookup.UnresolvedUneceCollisions);
    }

    [Theory]
    // A group whose rows describe the same unit under different codes is not an ambiguity:
    // either row is right, so the first in the file is taken and no rule is wanted.
    [InlineData("W/m²", "watt per square metre")]   // identical descriptions, two IEC codes
    [InlineData("cord", "cord")]                    // "cord" and "cord (128 ft3)"
    public void Resolve_TakesTheFirstRowWhenBothDescribeTheSameUnit(string raw, string expectedDescription)
    {
        Assert.Equal(expectedDescription, UnitLookup.Resolve(raw).Description);
    }

    [Theory]
    [InlineData("Scan Rate", "Scan_Rate")]
    [InlineData("PLC/Address", "PLCAddress")]
    [InlineData("  Temp 01  ", "Temp_01")]
    [InlineData("///", "")]
    public void SanitizeBrowseName_DropsWhatWouldBreakABrowsePath(string raw, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.SanitizeBrowseName(raw));
    }

    [Theory]
    // snake_case, camelCase, spaces and kebab all reach the same PascalCase name.
    [InlineData("scan_rate", "ScanRate")]
    [InlineData("scanRate", "ScanRate")]
    [InlineData("Scan Rate", "ScanRate")]
    [InlineData("scan-rate", "ScanRate")]
    [InlineData("SCAN_RATE", "ScanRate")]
    [InlineData("ScanRate", "ScanRate")]
    // An all-caps run of three or fewer is an acronym and survives; a longer one is a
    // shouted word and gets title-cased.
    [InlineData("PLC_ADDRESS", "PLCAddress")]
    [InlineData("plc address", "PlcAddress")]
    [InlineData("UOM", "UOM")]
    [InlineData("ID", "ID")]
    // An acronym already embedded in a mixed-case word keeps its shape.
    [InlineData("PLCAddress", "PLCAddress")]
    [InlineData("high_limit_2", "HighLimit2")]
    [InlineData("  spaced  out  ", "SpacedOut")]
    [InlineData("///", "")]
    [InlineData("", "")]
    public void PascalCaseBrowseName_NormalizesHeaderSpellings(string raw, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.PascalCaseBrowseName(raw));
    }

    [Fact]
    public void BrowseNameDeduper_SuffixesRepeatsAndSkipsNamesAlreadyTaken()
    {
        var deduper = new CsvTypeMapping.BrowseNameDeduper();

        Assert.Equal(("Temp", false), deduper.Unique("Temp"));
        Assert.Equal(("Temp_2", false), deduper.Unique("Temp_2"));
        // "Temp" repeats; "Temp_2" is gone, so it has to keep counting.
        Assert.Equal(("Temp_3", true), deduper.Unique("Temp"));
    }

    [Theory]
    [InlineData("boiler tag-list.csv", "BoilerTagList")]
    [InlineData("2024 tags.csv", "N2024Tags")]
    [InlineData("---.csv", "CsvImport")]
    [InlineData(null, "CsvImport")]
    // A file already named for a type proposes the Object without the suffix, so the
    // derived type name comes out "PumpType" rather than "PumpTypeType".
    [InlineData("PumpType.csv", "Pump")]
    [InlineData("Type.csv", "Type")]
    public void ProposeObjectName_BuildsAPascalCaseObjectNameFromTheFileName(string? fileName, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.ProposeObjectName(fileName));
    }

    [Theory]
    [InlineData("boiler tag-list.csv", "BoilerTagListType")]
    [InlineData("PumpType.csv", "PumpType")]
    [InlineData("---.csv", "CsvImportType")]
    public void ProposeTypeName_IsTheObjectNamePlusType(string? fileName, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.ProposeTypeName(fileName));
    }

    [Theory]
    [InlineData("Mandatory", "i=78")]
    [InlineData("yes", "i=78")]
    [InlineData("Optional", "i=80")]
    [InlineData("no", "i=80")]
    public void ResolveModellingRule_ReadsBothTheRuleNameAndAYesNoColumn(string raw, string expected)
    {
        Assert.Equal(expected, CsvTypeMapping.ResolveModellingRule(raw));
    }

    [Fact]
    public void ResolveModellingRule_SaysNothingForAnEmptyCell()
    {
        Assert.Null(CsvTypeMapping.ResolveModellingRule(""));
    }
}

/// <summary>
/// The CSV import end to end. The default is an Object under the Objects folder whose
/// children are the rows; "Create Type" additionally produces the ObjectType those rows
/// describe and makes the Object an instance of it, and "New Folder" puts the Object inside
/// a new folder. Covers the AnalogItemType promotion, the Property fallback for
/// site-specific columns, and the guards.
/// </summary>
[Collection("Api")]
public class CsvTypeImportTests : UaRestTestBase
{
    private const string ObjectsFolder = "i=85";
    private const string BaseObjectType = "i=58";
    private const string FolderType = "i=61";
    private const string DataItemType = "i=2365";
    private const string AnalogItemType = "i=2368";
    private const string PropertyType = "i=68";

    public CsvTypeImportTests(ApiFixture fixture) : base(fixture) { }

    /// <summary>Tests share one model, so every import needs its own name.</summary>
    private static string UniqueName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..24];

    private HttpRequestMessage CsvUpload(string url, string csv, string fileName, string? mappingJson = null)
    {
        var request = WithServer(HttpMethod.Post, url);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName);
        if (mappingJson != null) form.Add(new StringContent(mappingJson), "mapping");
        request.Content = form;
        return request;
    }

    private async Task<JsonElement> AnalyzeAsync(string csv, string fileName = "tags.csv")
    {
        var response = await Client.SendAsync(CsvUpload("/api/opcua/v1/csv/analyze", csv, fileName));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"analyze failed ({response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    /// <summary>
    /// Echoes the analyzer's proposal back verbatim — what the dialog sends when the user
    /// accepts the defaults.
    /// </summary>
    private static List<object> ColumnsFrom(JsonElement analysis) =>
        analysis.GetProperty("columns").EnumerateArray().Select(c => (object)new
        {
            index = c.GetProperty("index").GetInt32(),
            role = c.GetProperty("proposedRole").GetString(),
            browseName = StringOrNull(c, "proposedBrowseName"),
            dataTypeNodeId = StringOrNull(c, "proposedDataTypeNodeId"),
        }).ToList();

    private static string? StringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>An instance's children carry no ModellingRule; a type's declarations do.</summary>
    private static bool HasNoModellingRule(JsonElement node) => IsAbsentOrNull(node, "modellingRule");

    /// <summary>
    /// True when a property is missing or null. The API omits nulls, so "not asked for"
    /// shows up as an absent property rather than a null one.
    /// </summary>
    private static bool IsAbsentOrNull(JsonElement node, string name) =>
        !node.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    private async Task<(HttpStatusCode Status, JsonElement Body)> ImportAsync(
        string csv, object mapping, string fileName = "tags.csv")
    {
        var response = await Client.SendAsync(CsvUpload(
            "/api/opcua/v1/csv/create-type", csv, fileName, JsonSerializer.Serialize(mapping)));
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(body));
    }

    /// <summary>Runs the import, asserting success, and returns the result body.</summary>
    private async Task<JsonElement> ImportOkAsync(string csv, object mapping, string fileName = "tags.csv")
    {
        var (status, body) = await ImportAsync(csv, mapping, fileName);
        Assert.True(status is HttpStatusCode.Created or HttpStatusCode.OK,
            $"create-type failed ({status}): {body.GetRawText()}");
        return body;
    }

    /// <summary>A node's hierarchical children, keyed by BrowseName with any namespace stripped.</summary>
    private async Task<Dictionary<string, JsonElement>> ChildrenByNameAsync(string nodeId)
    {
        var response = await Client.SendAsync(
            WithServer(HttpMethod.Get, $"/api/opcua/v1/nodes/{Slug(nodeId)}/children"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"children failed ({response.StatusCode}): {body}");

        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in JsonSerializer.Deserialize<JsonElement>(body).GetProperty("results").EnumerateArray())
        {
            var browseName = child.GetProperty("browseName").GetString() ?? "";
            var semi = browseName.IndexOf(';');
            map[semi >= 0 ? browseName[(semi + 1)..] : browseName] = child;
        }
        return map;
    }

    private const string TagListCsv =
        "Name,Type,Units,High,Low,Scan Rate,PLC Address\n"
        + "Temp01,REAL,degC,150,-40,100,DB1.DBD0\n"
        + "Pressure02,LREAL,bar,10,0,250,DB1.DBD4\n"
        + "RunState,BOOL,,,,1000,DB1.DBX8.0\n";

    /// <summary>The default mapping: an Object under the Objects folder, no type, no folder.</summary>
    private static object Mapping(JsonElement analysis, string objectName) => new
    {
        modelUri = ApiFixture.TestModelUri,
        objectBrowseName = objectName,
        columns = ColumnsFrom(analysis),
    };

    #region Analyze

    [Fact]
    public async Task Analyze_ProposesARoleForEveryColumn()
    {
        var analysis = await AnalyzeAsync(TagListCsv, "boiler tags.csv");

        Assert.Equal(3, analysis.GetProperty("rowCount").GetInt32());
        Assert.Equal(",", analysis.GetProperty("delimiter").GetString());

        var roles = analysis.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("header").GetString()!, c => c.GetProperty("proposedRole").GetString());

        Assert.Equal("BrowseName", roles["Name"]);
        Assert.Equal("DataType", roles["Type"]);
        Assert.Equal("EngineeringUnits", roles["Units"]);
        Assert.Equal("EuRangeHigh", roles["High"]);
        Assert.Equal("EuRangeLow", roles["Low"]);
        // The two columns no rule knows about are still carried, as Properties.
        Assert.Equal("Property", roles["Scan Rate"]);
        Assert.Equal("Property", roles["PLC Address"]);
    }

    [Fact]
    public async Task Analyze_ProposesAnObjectNameAndTheTypeNameDerivedFromIt()
    {
        var analysis = await AnalyzeAsync(TagListCsv, "boiler tags.csv");

        Assert.Equal("BoilerTags", analysis.GetProperty("proposedObjectBrowseName").GetString());
        Assert.Equal("BoilerTagsType", analysis.GetProperty("proposedTypeBrowseName").GetString());
    }

    [Fact]
    public async Task Analyze_InfersAPropertyColumnsDataTypeFromItsValues()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var columns = analysis.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("header").GetString()!, c => c);

        Assert.Equal("i=6", StringOrNull(columns["Scan Rate"], "proposedDataTypeNodeId"));
        Assert.Equal("ScanRate", StringOrNull(columns["Scan Rate"], "proposedBrowseName"));
        Assert.Equal("i=12", StringOrNull(columns["PLC Address"], "proposedDataTypeNodeId"));
        // "PLC" is short enough to be taken for an acronym, so it keeps its case.
        Assert.Equal("PLCAddress", StringOrNull(columns["PLC Address"], "proposedBrowseName"));
    }

    [Fact]
    public async Task Analyze_ShowsHowEachDataTypeValueResolved()
    {
        var analysis = await AnalyzeAsync("Name,Type\na,REAL\nb,SPARE\n");
        var typeColumn = analysis.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("header").GetString() == "Type");

        var matches = typeColumn.GetProperty("dataTypeValues").EnumerateArray()
            .ToDictionary(m => m.GetProperty("rawValue").GetString()!, m => m);

        Assert.Equal("i=10", StringOrNull(matches["REAL"], "dataTypeNodeId"));
        Assert.Equal("Float", StringOrNull(matches["REAL"], "dataTypeName"));
        Assert.Null(StringOrNull(matches["SPARE"], "dataTypeNodeId"));

        var warnings = analysis.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("SPARE"));
    }

    [Fact]
    public async Task Analyze_WarnsWhenNoColumnLooksLikeAName()
    {
        var analysis = await AnalyzeAsync("Alpha,Beta\n1,2\n");

        var warnings = analysis.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("BrowseName"));
    }

    [Fact]
    public async Task Analyze_WarnsAboutAUnitWithNoUneceCode()
    {
        var analysis = await AnalyzeAsync("Name,Units\nTemp01,widgets\n");

        var warnings = analysis.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("widgets"));
    }

    #endregion

    #region The default: an Object, no type

    [Fact]
    public async Task Import_MakesATopLevelObjectWithOneVariablePerRow()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var objectName = UniqueName("Tags");

        var result = await ImportOkAsync(TagListCsv, Mapping(analysis, objectName));

        Assert.Equal(3, result.GetProperty("variablesCreated").GetInt32());
        Assert.Equal(0, result.GetProperty("rowsSkipped").GetInt32());
        // No type and no folder were asked for, so neither appears in the result.
        Assert.True(IsAbsentOrNull(result, "type"));
        Assert.True(IsAbsentOrNull(result, "folder"));

        var instance = result.GetProperty("instance");
        Assert.Equal("Object", instance.GetProperty("nodeClass").GetString());
        Assert.Equal(BaseObjectType, instance.GetProperty("typeDefinition").GetString());
        // No parent was given, and the Core Objects folder is not a legal one, so the Object
        // is top-level: no ParentId at all.
        Assert.True(IsAbsentOrNull(instance, "parentNodeId"));
        Assert.EndsWith($";{objectName}", instance.GetProperty("browseName").GetString());

        var children = await ChildrenByNameAsync(instance.GetProperty("nodeId").GetString()!);
        Assert.Equal(new[] { "Temp01", "Pressure02", "RunState" }.Order(), children.Keys.Order());

        // An instance's children are not declarations, so they carry no modelling rule.
        Assert.All(children.Values, c => Assert.True(HasNoModellingRule(c),
            $"{c.GetProperty("browseName").GetString()} should carry no modelling rule"));
    }

    [Fact]
    public async Task Import_ListsTheNewObjectAmongTheModelsTopLevelObjects()
    {
        // A top-level node carries no hierarchical reference, so it is invisible in the
        // address-space tree; query/types is where it can be found, and it is what feeds the
        // wizard's own Parent dropdown.
        var analysis = await AnalyzeAsync(TagListCsv);
        var objectName = UniqueName("Listed");

        await ImportOkAsync(TagListCsv, Mapping(analysis, objectName));

        var topLevel = await TopLevelObjectNamesAsync();
        Assert.Contains(objectName, topLevel);
    }

    [Fact]
    public async Task Import_DoesNotReferenceTheCoreObjectsFolder()
    {
        // The parent reference is stored on the child, so pointing at i=85 would make this
        // model's nodeset depend on a node from another one.
        var analysis = await AnalyzeAsync(TagListCsv);
        var objectName = UniqueName("NoCross");

        await ImportOkAsync(TagListCsv, Mapping(analysis, objectName));

        var objectsFolderChildren = await ChildrenByNameAsync(ObjectsFolder);
        Assert.DoesNotContain(objectName, objectsFolderChildren.Keys);
    }

    /// <summary>The BrowseNames of the test model's parentless Objects.</summary>
    private async Task<List<string>> TopLevelObjectNamesAsync()
    {
        var request = WithServer(HttpMethod.Get,
            $"/api/opcua/v1/query/types?nodeClass=Object&namespaceUri={Uri.EscapeDataString(ApiFixture.TestModelUri)}"
            + "&start=0&count=1000");
        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"query/types failed ({response.StatusCode}): {body}");

        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("results").EnumerateArray()
            .Select(n => n.GetProperty("browseName").GetString() ?? "")
            .Select(bn => bn.Contains(';') ? bn[(bn.IndexOf(';') + 1)..] : bn)
            .ToList();
    }

    [Fact]
    public async Task Import_PromotesANumericRowWithUnitsAndRangeToAnalogItemType()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var result = await ImportOkAsync(TagListCsv, Mapping(analysis, UniqueName("Analog")));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);

        var temp = children["Temp01"];
        Assert.Equal(AnalogItemType, temp.GetProperty("typeDefinition").GetString());
        Assert.Equal("i=10", temp.GetProperty("dataType").GetString());

        var tempChildren = await ChildrenByNameAsync(temp.GetProperty("nodeId").GetString()!);

        // EngineeringUnits keeps its Core BrowseName even though the node itself was
        // allocated in the private model.
        var units = tempChildren["EngineeringUnits"];
        Assert.Equal("EngineeringUnits", units.GetProperty("browseName").GetString());
        Assert.Equal("i=887", units.GetProperty("dataType").GetString());
        Assert.True(HasNoModellingRule(units));

        var unitsValue = await ValueOfAsync(units.GetProperty("nodeId").GetString()!);
        // Resolved against the embedded IEC 62720 table, which is searched before UNECE.
        Assert.Equal(705741427, unitsValue.GetProperty("UnitId").GetInt32());
        Assert.Equal("°C", unitsValue.GetProperty("DisplayName").GetProperty("Text").GetString());
        Assert.Equal("degree Celsius", unitsValue.GetProperty("Description").GetProperty("Text").GetString());
        Assert.Equal(UnitLookup.Iec62720NamespaceUri, unitsValue.GetProperty("NamespaceUri").GetString());

        var euRange = tempChildren["EURange"];
        Assert.Equal("i=884", euRange.GetProperty("dataType").GetString());
        var rangeValue = await ValueOfAsync(euRange.GetProperty("nodeId").GetString()!);
        Assert.Equal(150d, rangeValue.GetProperty("High").GetDouble());
        Assert.Equal(-40d, rangeValue.GetProperty("Low").GetDouble());

        // No InstrumentRange: the CSV had no columns for it, so nothing was materialized.
        Assert.DoesNotContain("InstrumentRange", tempChildren.Keys);
    }

    [Fact]
    public async Task Import_LeavesANonNumericRowAsADataItem()
    {
        // RunState is a BOOL with no range, and a Boolean can't be an AnalogItem.
        var analysis = await AnalyzeAsync(TagListCsv);
        var result = await ImportOkAsync(TagListCsv, Mapping(analysis, UniqueName("Mixed")));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);
        var runState = children["RunState"];

        Assert.Equal(DataItemType, runState.GetProperty("typeDefinition").GetString());
        Assert.Equal("i=1", runState.GetProperty("dataType").GetString());
    }

    [Fact]
    public async Task Import_KeepsUnitsOnANonNumericRowOutOfAnEuInformation()
    {
        // A Boolean with a units column must not be promoted — AnalogItemType constrains
        // its Value to a Number — so no EngineeringUnits child appears.
        const string csv = "Name,Type,Units\nRunState,BOOL,degC\n";
        var analysis = await AnalyzeAsync(csv);
        var result = await ImportOkAsync(csv, Mapping(analysis, UniqueName("BoolUnits")));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);
        var runState = children["RunState"];
        Assert.Equal(DataItemType, runState.GetProperty("typeDefinition").GetString());

        var runStateChildren = await ChildrenByNameAsync(runState.GetProperty("nodeId").GetString()!);
        Assert.DoesNotContain("EngineeringUnits", runStateChildren.Keys);
    }

    [Fact]
    public async Task Import_TurnsAnUnmatchedColumnIntoAProperty()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var result = await ImportOkAsync(TagListCsv, Mapping(analysis, UniqueName("Props")));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);
        var runStateChildren = await ChildrenByNameAsync(children["RunState"].GetProperty("nodeId").GetString()!);

        var scanRate = runStateChildren["ScanRate"];
        Assert.Equal(PropertyType, scanRate.GetProperty("typeDefinition").GetString());
        Assert.Equal("i=6", scanRate.GetProperty("dataType").GetString());
        Assert.True(HasNoModellingRule(scanRate));
        Assert.Equal(1000, (await ValueOfAsync(scanRate.GetProperty("nodeId").GetString()!)).GetInt32());

        var address = runStateChildren["PLCAddress"];
        Assert.Equal("i=12", address.GetProperty("dataType").GetString());
        Assert.Equal("DB1.DBX8.0", (await ValueOfAsync(address.GetProperty("nodeId").GetString()!)).GetString());
    }

    [Fact]
    public async Task Import_SkipsNamelessRowsAndSuffixesDuplicates()
    {
        const string csv = "Name,Type\nTemp,REAL\n,REAL\nTemp,REAL\n";
        var analysis = await AnalyzeAsync(csv);
        var result = await ImportOkAsync(csv, Mapping(analysis, UniqueName("Dupes")));

        Assert.Equal(2, result.GetProperty("variablesCreated").GetInt32());
        Assert.Equal(1, result.GetProperty("rowsSkipped").GetInt32());

        var warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("no usable name"));
        Assert.Contains(warnings, w => w.Contains("Temp_2"));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);
        Assert.Equal(new[] { "Temp", "Temp_2" }.Order(), children.Keys.Order());
    }

    [Fact]
    public async Task Import_FallsBackToTheDefaultDataTypeAndSaysSo()
    {
        const string csv = "Name,Type\nWidget,SPARE\n";
        var analysis = await AnalyzeAsync(csv);
        var result = await ImportOkAsync(csv, new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("Fallback"),
            defaultDataTypeNodeId = "i=12",
            columns = ColumnsFrom(analysis),
        });

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);
        Assert.Equal("i=12", children["Widget"].GetProperty("dataType").GetString());

        var warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("SPARE"));
    }

    [Fact]
    public async Task Import_InfersTheDataTypeFromTheValueColumnWhenThereIsNoTypeColumn()
    {
        // No DataType column, so the fallback has to come from somewhere. A mapped Value
        // column is the best evidence there is, which is why the editor offers no
        // "fallback DataType" control.
        const string csv = "Name,Value\nSetpoint,42\nLimit,7\n";
        var analysis = await AnalyzeAsync(csv);
        var result = await ImportOkAsync(csv, Mapping(analysis, UniqueName("Sniffed")));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);

        Assert.Equal("i=6", children["Setpoint"].GetProperty("dataType").GetString());
        Assert.Equal(42, (await ValueOfAsync(children["Setpoint"].GetProperty("nodeId").GetString()!)).GetInt32());
    }

    [Fact]
    public async Task Import_DefaultsToDoubleWithNeitherATypeNorAValueColumn()
    {
        const string csv = "Name\nTemp01\n";
        var analysis = await AnalyzeAsync(csv);
        var result = await ImportOkAsync(csv, Mapping(analysis, UniqueName("Bare")));

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);

        Assert.Equal("i=11", children["Temp01"].GetProperty("dataType").GetString());
    }

    [Fact]
    public async Task Import_HonoursPromotionBeingTurnedOff()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var result = await ImportOkAsync(TagListCsv, new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("NoPromote"),
            promoteToAnalogItem = false,
            columns = ColumnsFrom(analysis),
        });

        var children = await ChildrenByNameAsync(
            result.GetProperty("instance").GetProperty("nodeId").GetString()!);
        Assert.Equal(DataItemType, children["Temp01"].GetProperty("typeDefinition").GetString());

        var tempChildren = await ChildrenByNameAsync(children["Temp01"].GetProperty("nodeId").GetString()!);
        Assert.DoesNotContain("EngineeringUnits", tempChildren.Keys);
    }

    #endregion

    #region Create Type

    [Fact]
    public async Task Import_WithCreateType_MakesBothTheTypeAndAnInstanceOfIt()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var objectName = UniqueName("Typed");

        var result = await ImportOkAsync(TagListCsv, new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = objectName,
            createType = true,
            columns = ColumnsFrom(analysis),
        });

        var type = result.GetProperty("type");
        Assert.Equal("ObjectType", type.GetProperty("nodeClass").GetString());
        Assert.Equal(BaseObjectType, type.GetProperty("superTypeId").GetString());
        // The type name defaults to the object name with "Type" appended.
        Assert.EndsWith($";{objectName}Type", type.GetProperty("browseName").GetString());

        // The instance is an instance of that type, not of BaseObjectType.
        var instance = result.GetProperty("instance");
        Assert.Equal(type.GetProperty("nodeId").GetString(), instance.GetProperty("typeDefinition").GetString());
        Assert.True(IsAbsentOrNull(instance, "parentNodeId"));

        // The type carries the rows as Mandatory declarations...
        var declarations = await ChildrenByNameAsync(type.GetProperty("nodeId").GetString()!);
        Assert.Equal(new[] { "Temp01", "Pressure02", "RunState" }.Order(), declarations.Keys.Order());
        Assert.All(declarations.Values, d => Assert.Equal("i=78", d.GetProperty("modellingRule").GetString()));

        // ...and the instance carries the same rows with no rule at all.
        var children = await ChildrenByNameAsync(instance.GetProperty("nodeId").GetString()!);
        Assert.Equal(new[] { "Temp01", "Pressure02", "RunState" }.Order(), children.Keys.Order());
        Assert.All(children.Values, c => Assert.True(HasNoModellingRule(c)));

        // The declaration and the instance child agree on DataType and TypeDefinition.
        Assert.Equal(declarations["Temp01"].GetProperty("dataType").GetString(),
            children["Temp01"].GetProperty("dataType").GetString());
        Assert.Equal(AnalogItemType, declarations["Temp01"].GetProperty("typeDefinition").GetString());
        Assert.Equal(AnalogItemType, children["Temp01"].GetProperty("typeDefinition").GetString());
    }

    [Fact]
    public async Task Import_WithCreateType_PutsTheUnitsAndRangeOnBothTrees()
    {
        const string csv = "Name,Type,Units,High,Low\nTemp01,REAL,degC,150,-40\n";
        var analysis = await AnalyzeAsync(csv);
        var result = await ImportOkAsync(csv, new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("TypedAnalog"),
            createType = true,
            columns = ColumnsFrom(analysis),
        });

        foreach (var root in new[] { "type", "instance" })
        {
            var rows = await ChildrenByNameAsync(
                result.GetProperty(root).GetProperty("nodeId").GetString()!);
            var tempChildren = await ChildrenByNameAsync(rows["Temp01"].GetProperty("nodeId").GetString()!);

            var unitsValue = await ValueOfAsync(tempChildren["EngineeringUnits"].GetProperty("nodeId").GetString()!);
            Assert.Equal(705741427, unitsValue.GetProperty("UnitId").GetInt32());

            var rangeValue = await ValueOfAsync(tempChildren["EURange"].GetProperty("nodeId").GetString()!);
            Assert.Equal(150d, rangeValue.GetProperty("High").GetDouble());

            // On the type these keep AnalogItemType's own rules — EURange Mandatory,
            // EngineeringUnits Optional — and on the instance they keep none.
            if (root == "type")
            {
                Assert.Equal("i=78", tempChildren["EURange"].GetProperty("modellingRule").GetString());
                Assert.Equal("i=80", tempChildren["EngineeringUnits"].GetProperty("modellingRule").GetString());
            }
            else
            {
                Assert.True(HasNoModellingRule(tempChildren["EURange"]));
                Assert.True(HasNoModellingRule(tempChildren["EngineeringUnits"]));
            }
        }
    }

    [Fact]
    public async Task Import_WithCreateType_HonoursAnExplicitTypeName()
    {
        var analysis = await AnalyzeAsync("Name\nTemp\n");
        var typeName = UniqueName("Explicit") + "Type";

        var result = await ImportOkAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("Obj"),
            createType = true,
            typeBrowseName = typeName,
            columns = ColumnsFrom(analysis),
        });

        Assert.EndsWith($";{typeName}", result.GetProperty("type").GetProperty("browseName").GetString());
    }

    #endregion

    #region Parent and New Folder

    [Fact]
    public async Task Import_PutsTheObjectInANewFolderWhenAsked()
    {
        var analysis = await AnalyzeAsync(TagListCsv);
        var folderName = UniqueName("Plant");
        var objectName = UniqueName("Boiler");

        var result = await ImportOkAsync(TagListCsv, new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = objectName,
            newFolder = true,
            folderBrowseName = folderName,
            columns = ColumnsFrom(analysis),
        });

        var folder = result.GetProperty("folder");
        Assert.Equal("Object", folder.GetProperty("nodeClass").GetString());
        Assert.Equal(FolderType, folder.GetProperty("typeDefinition").GetString());
        // The folder itself is top-level — it may not reference the Core Objects folder.
        Assert.True(IsAbsentOrNull(folder, "parentNodeId"));
        Assert.DoesNotContain(folderName, (await ChildrenByNameAsync(ObjectsFolder)).Keys);
        Assert.Contains(folderName, await TopLevelObjectNamesAsync());

        var folderChildren = await ChildrenByNameAsync(folder.GetProperty("nodeId").GetString()!);
        Assert.Contains(objectName, folderChildren.Keys);

        var instance = result.GetProperty("instance");
        Assert.Equal(folder.GetProperty("nodeId").GetString(), instance.GetProperty("parentNodeId").GetString());
        // A folder organizes what it holds (Part 3, 5.5.3).
        Assert.Equal("i=35", folderChildren[objectName].GetProperty("referenceTypeId").GetString());
    }

    [Fact]
    public async Task Import_DefaultsTheNewFoldersNameToTheObjectName()
    {
        var analysis = await AnalyzeAsync("Name\nTemp\n");
        var objectName = UniqueName("Same");

        var result = await ImportOkAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = objectName,
            newFolder = true,
            columns = ColumnsFrom(analysis),
        });

        Assert.EndsWith($";{objectName}", result.GetProperty("folder").GetProperty("browseName").GetString());
    }

    [Fact]
    public async Task Import_PutsTheObjectUnderAChosenParentObject()
    {
        // First import supplies the parent, the second nests under it.
        var analysis = await AnalyzeAsync("Name\nTemp\n");
        var parentName = UniqueName("Parent");
        var parent = await ImportOkAsync("Name\nTemp\n",
            Mapping(analysis, parentName));
        var parentNodeId = parent.GetProperty("instance").GetProperty("nodeId").GetString()!;

        var childName = UniqueName("Child");
        var result = await ImportOkAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = childName,
            parentNodeId,
            columns = ColumnsFrom(analysis),
        });

        var instance = result.GetProperty("instance");
        Assert.Equal(parentNodeId, instance.GetProperty("parentNodeId").GetString());

        var parentChildren = await ChildrenByNameAsync(parentNodeId);
        Assert.Contains(childName, parentChildren.Keys);
        // A plain Object composes its children rather than organizing them.
        Assert.Equal("i=47", parentChildren[childName].GetProperty("referenceTypeId").GetString());
    }

    [Fact]
    public async Task Import_RejectsAParentInAnotherModel()
    {
        // The Core Objects folder is the parent a caller is most likely to reach for, and it
        // is exactly the cross-model reference the rule forbids.
        var analysis = await AnalyzeAsync("Name\nTemp\n");

        var (status, body) = await ImportAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("CrossModel"),
            parentNodeId = ObjectsFolder,
            columns = ColumnsFrom(analysis),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var message = body.GetProperty("message").GetString()!;
        Assert.Contains("same model", message);
        Assert.Contains("http://opcfoundation.org/UA/", message);
    }

    [Fact]
    public async Task Import_RejectsAnInModelParentThatIsNotAnObject()
    {
        // A Variable from an earlier import: in the right model, wrong NodeClass.
        var analysis = await AnalyzeAsync("Name\nTemp\n");
        var host = await ImportOkAsync("Name\nTemp\n", Mapping(analysis, UniqueName("Host")));
        var rows = await ChildrenByNameAsync(
            host.GetProperty("instance").GetProperty("nodeId").GetString()!);
        var variableNodeId = rows["Temp"].GetProperty("nodeId").GetString()!;

        var (status, body) = await ImportAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("BadParent"),
            parentNodeId = variableNodeId,
            columns = ColumnsFrom(analysis),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not an Object", body.GetProperty("message").GetString());
    }

    #endregion

    #region Guards

    [Fact]
    public async Task Import_RejectsAMappingWithNoBrowseNameColumn()
    {
        var (status, body) = await ImportAsync("Alpha,Beta\n1,2\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("NoName"),
            columns = new[] { new { index = 0, role = "Property" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("BrowseName", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Import_RejectsAMappingWithNoObjectName()
    {
        var analysis = await AnalyzeAsync("Name\nTemp\n");

        var (status, body) = await ImportAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            columns = ColumnsFrom(analysis),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("objectBrowseName", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Import_RejectsASupertypeThatIsNotAnObjectType()
    {
        var analysis = await AnalyzeAsync("Name\nTemp\n");

        var (status, body) = await ImportAsync("Name\nTemp\n", new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("BadSuper"),
            createType = true,
            superTypeNodeId = "i=63", // BaseDataVariableType
            columns = ColumnsFrom(analysis),
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("ObjectType", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Import_RejectsAFileWithMoreRowsThanTheCap()
    {
        var csv = new StringBuilder("Name\n");
        for (var i = 0; i <= CsvTypeImportService.MaxRows; i++) csv.Append($"Tag{i}\n");

        var (status, body) = await ImportAsync(csv.ToString(), new
        {
            modelUri = ApiFixture.TestModelUri,
            objectBrowseName = UniqueName("TooBig"),
            columns = new[] { new { index = 0, role = "BrowseName" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("at most", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Import_RejectsAnInvalidMappingPayload()
    {
        var response = await Client.SendAsync(CsvUpload(
            "/api/opcua/v1/csv/create-type", "Name\nTemp\n", "tags.csv", "{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Serialization

    [Fact]
    public async Task Import_WritesTheUnitsAndRangeAsRealStructuresInTheNodeSetXml()
    {
        // The EUInformation / Range values only pay off if they survive serialization: a
        // struct Value that reaches the XML as a bare string is worthless to a consumer.
        const string csv = "Name,Type,Units,High,Low\nTemp01,REAL,degC,150,-40\n";
        var analysis = await AnalyzeAsync(csv);
        var objectName = UniqueName("Export");
        await ImportOkAsync(csv, Mapping(analysis, objectName));

        var xml = await ExportTestModelXmlAsync();

        Assert.Contains($"BrowseName=\"1:{objectName}\"", xml);
        // Matched prefix-tolerantly: the struct wrapper elements are written in the Types.xsd
        // namespace, and which prefix the writer picks for it is not part of the contract.
        Assert.Matches(@"<(\w+:)?EUInformation\b", xml);
        Assert.Matches(@"<(\w+:)?NamespaceUri>http://www\.opcfoundation\.org/UA/units/cdd/IEC62720<", xml);
        Assert.Matches(@"<(\w+:)?UnitId>705741427<", xml);
        Assert.Matches(@"<(\w+:)?Range\b", xml);
        Assert.Matches(@"<(\w+:)?Low>-40<", xml);
        Assert.Matches(@"<(\w+:)?High>150<", xml);
    }

    /// <summary>The whole test model serialized as NodeSet XML.</summary>
    private async Task<string> ExportTestModelXmlAsync()
    {
        var listResponse = await Client.SendAsync(WithServer(HttpMethod.Get, "/api/opcua/v1/namespaces/info"));
        listResponse.EnsureSuccessStatusCode();
        var namespaces = (await listResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");

        var modelId = namespaces.EnumerateArray()
            .First(ns => ns.GetProperty("uri").GetString() == ApiFixture.TestModelUri)
            .GetProperty("id").GetString();

        var exportResponse = await Client.SendAsync(
            WithServer(HttpMethod.Get, $"/api/opcua/v1/namespaces/info/{modelId}/export?format=xml"));
        var xml = await exportResponse.Content.ReadAsStringAsync();
        Assert.True(exportResponse.IsSuccessStatusCode, $"export failed ({exportResponse.StatusCode}): {xml}");
        return xml;
    }

    #endregion

    /// <summary>The <c>value</c> of a node, read back through a fresh GET.</summary>
    private async Task<JsonElement> ValueOfAsync(string nodeId)
    {
        var node = await GetNode(nodeId);
        return node.GetProperty("value");
    }
}
