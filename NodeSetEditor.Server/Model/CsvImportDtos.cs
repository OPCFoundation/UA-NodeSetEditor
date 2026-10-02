namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// What the CSV analyzer made of an uploaded file: the columns it found, the role it
    /// proposes for each, and a proposed name for the type. Nothing is created by the
    /// analyze call — the user reviews this and posts back a
    /// <see cref="CsvCreateTypeRequest"/>.
    /// </summary>
    public class CsvAnalysisResult
    {
        public string FileName { get; set; } = string.Empty;

        /// <summary>Number of data rows (the header row is not counted).</summary>
        public int RowCount { get; set; }

        /// <summary>The delimiter that was sniffed, as a single character ("," ";" "\t" "|").</summary>
        public string Delimiter { get; set; } = ",";

        /// <summary>Proposed BrowseName for the new Object, derived from the file name.</summary>
        public string ProposedObjectBrowseName { get; set; } = string.Empty;

        /// <summary>
        /// Proposed BrowseName for the ObjectType, used only when the caller asks for one:
        /// <see cref="ProposedObjectBrowseName"/> with "Type" appended.
        /// </summary>
        public string ProposedTypeBrowseName { get; set; } = string.Empty;

        public List<CsvColumnAnalysis> Columns { get; set; } = new();

        /// <summary>
        /// Anything the user should see before confirming — an unresolvable DataType value,
        /// an unknown unit, a blank or duplicated row name. Never fatal on its own.
        /// </summary>
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>One CSV column and the mapping proposed for it.</summary>
    public class CsvColumnAnalysis
    {
        /// <summary>Zero-based position in the header row; the identity used by <see cref="CsvColumnMapping"/>.</summary>
        public int Index { get; set; }

        public string Header { get; set; } = string.Empty;

        /// <summary>Up to three distinct non-blank values, so the user can see what's in the column.</summary>
        public List<string> SampleValues { get; set; } = new();

        /// <summary>A <c>CsvColumnRole</c> name, e.g. "BrowseName", "EuRangeHigh", "Property".</summary>
        public string ProposedRole { get; set; } = "Ignore";

        /// <summary>Sanitized header, for a column proposed as a Property child. Null otherwise.</summary>
        public string? ProposedBrowseName { get; set; }

        /// <summary>
        /// For a Property column, the DataType inferred from the column's values. For the
        /// DataType column, the fallback used where a cell doesn't resolve. Null otherwise.
        /// </summary>
        public string? ProposedDataTypeNodeId { get; set; }

        /// <summary>
        /// Populated only for the DataType column: each distinct value in it and what it
        /// resolved to, so "REAL → Float" and "SPARE → (unresolved)" are visible up front.
        /// </summary>
        public List<CsvDataTypeMatch>? DataTypeValues { get; set; }
    }

    /// <summary>How one distinct value of the DataType column resolved.</summary>
    public class CsvDataTypeMatch
    {
        public string RawValue { get; set; } = string.Empty;

        /// <summary>Resolved DataType NodeId, or null when nothing matched.</summary>
        public string? DataTypeNodeId { get; set; }

        /// <summary>BrowseName of the resolved DataType, for display. Null when unresolved.</summary>
        public string? DataTypeName { get; set; }
    }

    /// <summary>
    /// The confirmed mapping, posted back alongside the same CSV file. The file is
    /// re-uploaded rather than cached between the two calls so there is no server-side
    /// session state to expire.
    /// </summary>
    public class CsvCreateTypeRequest
    {
        /// <summary>Private model the new nodes are created in. Required.</summary>
        public string? ModelUri { get; set; }

        /// <summary>
        /// BrowseName of the Object the rows hang under — the thing this import always
        /// creates. Required.
        /// </summary>
        public string? ObjectBrowseName { get; set; }

        public string? ObjectDisplayName { get; set; }

        /// <summary>
        /// Where the Object goes. Must be an Object <b>in <see cref="ModelUri"/></b> — a
        /// ParentId may never point into another model, so the Core Objects folder (i=85) is
        /// not a legal parent. Null or empty creates a top-level node instead: no ParentId
        /// and no hierarchical reference, the same shape POST /nodes produces.
        /// </summary>
        public string? ParentNodeId { get; set; }

        /// <summary>
        /// Create a new FolderType object and put the Object inside it. The folder takes
        /// <see cref="ParentNodeId"/> as its own parent, so it is top-level when none is given.
        /// </summary>
        public bool NewFolder { get; set; }

        /// <summary>BrowseName of that folder. Defaults to <see cref="ObjectBrowseName"/>.</summary>
        public string? FolderBrowseName { get; set; }

        /// <summary>
        /// Also create an ObjectType carrying the rows as Mandatory instance declarations,
        /// and make the Object an instance of it. Without this the Object is a plain
        /// BaseObjectType instance and the rows are ordinary children.
        /// </summary>
        public bool CreateType { get; set; }

        /// <summary>
        /// BrowseName of that ObjectType. Defaults to <see cref="ObjectBrowseName"/> with
        /// "Type" appended. Ignored unless <see cref="CreateType"/> is set.
        /// </summary>
        public string? TypeBrowseName { get; set; }

        public string? TypeDisplayName { get; set; }

        /// <summary>Applied to the ObjectType when one is created, otherwise to the Object.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Supertype of the ObjectType. BaseObjectType, and not surfaced in the editor's
        /// import wizard — kept so a direct API caller can derive from something else.
        /// </summary>
        public string SuperTypeNodeId { get; set; } = "i=58";

        /// <summary>TypeDefinition for each row's Variable. Defaults to DataItemType.</summary>
        public string VariableTypeDefinitionId { get; set; } = "i=2365";

        /// <summary>
        /// When true, a row carrying units or a range and a numeric DataType gets
        /// AnalogItemType instead, with EngineeringUnits / EURange property children.
        /// </summary>
        public bool PromoteToAnalogItem { get; set; } = true;

        /// <summary>
        /// Last-resort DataType for rows whose DataType cell is missing or unrecognized.
        /// Only consulted when no column is mapped to Value — a Value column's own values are
        /// better evidence, so they are sniffed in preference to this.
        /// </summary>
        public string DefaultDataTypeNodeId { get; set; } = "i=11";

        /// <summary>
        /// ModellingRule for each row's instance declaration on the ObjectType. Defaults to
        /// Mandatory. Never applied to the Object's own children — an instance carries no
        /// modelling rules.
        /// </summary>
        public string ModellingRuleId { get; set; } = "i=78";

        /// <summary>
        /// One entry per column the user kept. A column absent from this list is ignored,
        /// as is one with role "Ignore".
        /// </summary>
        public List<CsvColumnMapping> Columns { get; set; } = new();
    }

    /// <summary>The role the user confirmed for one column.</summary>
    public class CsvColumnMapping
    {
        public int Index { get; set; }

        /// <summary>A <c>CsvColumnRole</c> name. Unrecognized names are treated as "Ignore".</summary>
        public string Role { get; set; } = "Ignore";

        /// <summary>BrowseName for a Property column. Falls back to the sanitized header.</summary>
        public string? BrowseName { get; set; }

        /// <summary>DataType for a Property column. Falls back to the inferred one.</summary>
        public string? DataTypeNodeId { get; set; }
    }

    /// <summary>What the import created.</summary>
    public class CsvCreateTypeResult
    {
        /// <summary>The Object the rows hang under — always present.</summary>
        public Opc.Ua.RestfulApi.Node? Instance { get; set; }

        /// <summary>The ObjectType, when <see cref="CsvCreateTypeRequest.CreateType"/> asked for one.</summary>
        public Opc.Ua.RestfulApi.Node? Type { get; set; }

        /// <summary>The folder, when <see cref="CsvCreateTypeRequest.NewFolder"/> asked for one.</summary>
        public Opc.Ua.RestfulApi.Node? Folder { get; set; }

        /// <summary>
        /// Rows turned into Variables, and the property children they carry. Counted per
        /// tree: when a type is created the same numbers apply to it and to the instance.
        /// </summary>
        public int VariablesCreated { get; set; }
        public int PropertiesCreated { get; set; }

        /// <summary>Rows dropped because they had no usable name.</summary>
        public int RowsSkipped { get; set; }

        public List<string> Warnings { get; set; } = new();
    }
}
