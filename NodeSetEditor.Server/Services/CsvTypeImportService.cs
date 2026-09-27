extern alias JsonNodeSet;

using System.Globalization;
using System.Text.Json;
using JsonNodeSet::Opc.Ua.NodeSetSerializer;
using NodeSetEditor.Server.Model;

using UaModel = JsonNodeSet::Opc.Ua.NodeSetSerializer.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Turns a CSV of tags into one ObjectType whose components are the rows.
    ///
    /// Two halves, matching the two-step UI: <see cref="Analyze"/> proposes a mapping from
    /// column headers to OPC UA meaning, and <see cref="Build"/> turns a confirmed mapping
    /// into the node graph. Building is deliberately separate from writing — the controller
    /// applies the result as a single changeset, so a 500-row CSV is one database round trip
    /// rather than 500.
    ///
    /// The header ruleset itself lives in <see cref="CsvTypeMapping"/>.
    /// </summary>
    public class CsvTypeImportService : ICsvTypeImportService
    {
        private const string BaseObjectType = "i=58";
        private const string FolderType = "i=61";
        private const string BaseDataVariableType = "i=63";
        private const string PropertyType = "i=68";
        private const string DataItemType = "i=2365";
        private const string AnalogItemType = "i=2368";

        /// <summary>
        /// Namespace 0. Named here only so a rejected cross-model parent can say which model
        /// the offending NodeId was in — the importer never writes into it.
        /// </summary>
        private const string OpcUaCoreModelUri = "http://opcfoundation.org/UA/";

        private const string Organizes = "i=35";
        private const string HasProperty = "i=46";
        private const string HasComponent = "i=47";
        private const string HasSubtype = "i=45";
        private const string HasTypeDefinition = "i=40";
        private const string HasModellingRule = "i=37";
        private const string Mandatory = "i=78";

        private const string NumberDataType = "i=26";
        private const string IntegerDataType = "i=27";
        private const string UIntegerDataType = "i=28";
        private const string BooleanDataType = "i=1";

        /// <summary>Maximum rows accepted, so one upload can't push tens of thousands of nodes.</summary>
        public const int MaxRows = 5000;

        private const int SampleValueCount = 3;

        /// <summary>How many offending values a warning names before it summarizes the rest.</summary>
        private const int WarningListLimit = 10;

        public CsvTable Parse(Stream csv) => CsvTypeMapping.Parse(csv);

        #region Analyze

        public CsvAnalysisResult Analyze(CsvTable table, string? fileName, AddressSpace addressSpace)
        {
            var roles = CsvTypeMapping.ProposeRoles(table.Headers);
            var dataTypesByName = BuildDataTypeIndex(addressSpace);

            var result = new CsvAnalysisResult
            {
                FileName = fileName ?? string.Empty,
                RowCount = table.Rows.Count,
                Delimiter = table.Delimiter == '\t' ? "\\t" : table.Delimiter.ToString(),
                ProposedObjectBrowseName = CsvTypeMapping.ProposeObjectName(fileName),
                ProposedTypeBrowseName = CsvTypeMapping.ProposeTypeName(fileName),
            };

            for (var i = 0; i < table.Headers.Count; i++)
            {
                var role = roles[i];
                var values = ColumnValues(table, i).ToList();
                var column = new CsvColumnAnalysis
                {
                    Index = i,
                    Header = table.Headers[i],
                    SampleValues = values
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.Ordinal)
                        .Take(SampleValueCount)
                        .ToList(),
                    ProposedRole = role.ToString(),
                };

                if (role == CsvColumnRole.Property)
                {
                    column.ProposedBrowseName = CsvTypeMapping.PascalCaseBrowseName(table.Headers[i]);
                    column.ProposedDataTypeNodeId = CsvTypeMapping.InferDataType(values);
                }
                else if (role == CsvColumnRole.DataType)
                {
                    column.DataTypeValues = values
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .Select(v =>
                        {
                            var nodeId = ResolveDataType(v, dataTypesByName);
                            return new CsvDataTypeMatch
                            {
                                RawValue = v,
                                DataTypeNodeId = nodeId,
                                DataTypeName = nodeId == null ? null : DataTypeName(addressSpace, nodeId),
                            };
                        })
                        .ToList();
                }

                result.Columns.Add(column);
            }

            CollectAnalysisWarnings(table, roles, result);
            return result;
        }

        /// <summary>
        /// The things worth telling the user before they press OK: a missing name column
        /// (which blocks the import), plus the data-quality problems that would otherwise
        /// only show up as odd-looking nodes afterwards.
        /// </summary>
        private static void CollectAnalysisWarnings(
            CsvTable table, List<CsvColumnRole> roles, CsvAnalysisResult result)
        {
            if (table.Rows.Count == 0)
                result.Warnings.Add("The file has a header row but no data rows.");

            if (table.Rows.Count > MaxRows)
                result.Warnings.Add($"The file has {table.Rows.Count} rows; at most {MaxRows} can be imported at once.");

            var nameIndex = roles.IndexOf(CsvColumnRole.BrowseName);
            if (nameIndex < 0)
            {
                result.Warnings.Add(
                    "No column looked like a name. Set one column's role to BrowseName — every row needs a name.");
            }
            else
            {
                var blank = 0;
                var deduper = new CsvTypeMapping.BrowseNameDeduper();
                var renamed = new List<string>();
                foreach (var row in table.Rows)
                {
                    var name = CsvTypeMapping.SanitizeBrowseName(CsvTable.Cell(row, nameIndex));
                    if (name.Length == 0) { blank++; continue; }
                    var (unique, wasDuplicate) = deduper.Unique(name);
                    if (wasDuplicate) renamed.Add($"{name} → {unique}");
                }

                if (blank > 0)
                    result.Warnings.Add(
                        $"{blank} row(s) have no usable name in '{table.Headers[nameIndex]}' and will be skipped.");
                if (renamed.Count > 0)
                    result.Warnings.Add("Duplicate names will be suffixed: " + Summarize(renamed));
            }

            foreach (var column in result.Columns)
            {
                if (column.DataTypeValues is not { } matches) continue;
                var unresolved = matches.Where(m => m.DataTypeNodeId == null).Select(m => m.RawValue).ToList();
                if (unresolved.Count > 0)
                    result.Warnings.Add(
                        $"These '{column.Header}' values are not known DataTypes and will fall back to the default: "
                        + Summarize(unresolved));
            }

            var unitIndex = roles.IndexOf(CsvColumnRole.EngineeringUnits);
            if (unitIndex >= 0)
            {
                var unknown = ColumnValues(table, unitIndex)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(v => !UnitLookup.Resolve(v).IsKnown)
                    .ToList();
                if (unknown.Count > 0)
                    result.Warnings.Add(
                        "These units have no UNECE code; they will be imported with UnitId 0 and the text as the "
                        + "display name: " + Summarize(unknown));
            }
        }

        #endregion

        #region Build

        /// <summary>
        /// Everything one CSV row resolves to, worked out once so the row can be emitted
        /// twice — as a Mandatory instance declaration on the ObjectType, and as an ordinary
        /// child of the Object. Both trees must agree on names, DataTypes and values, which
        /// resolving per-tree would not guarantee.
        /// </summary>
        private sealed record RowPlan(
            string BrowseName,
            string DisplayName,
            string? Description,
            string DataTypeNodeId,
            string TypeDefinitionId,
            string? ValueText,
            bool IsAnalog,
            string? UnitsText,
            double? EuLow,
            double? EuHigh,
            double? InstrumentLow,
            double? InstrumentHigh,
            List<RowProperty> Properties)
        {
            /// <summary>Property children this row will carry, for the result counts.</summary>
            public int PropertyCount =>
                (IsAnalog && !string.IsNullOrWhiteSpace(UnitsText) ? 1 : 0)
                + (IsAnalog && (EuLow.HasValue || EuHigh.HasValue) ? 1 : 0)
                + (IsAnalog && (InstrumentLow.HasValue || InstrumentHigh.HasValue) ? 1 : 0)
                + Properties.Count;
        }

        /// <summary>One Property child from a mapped column, with its cell already resolved.</summary>
        private sealed record RowProperty(string BrowseName, string DataTypeNodeId, string Cell);

        public CsvBuildResult Build(
            CsvTable table,
            CsvCreateTypeRequest request,
            AddressSpace addressSpace,
            Func<string, string> allocateNodeId,
            CsvVariantEncoder encodeValue)
        {
            var modelUri = request.ModelUri;
            if (string.IsNullOrWhiteSpace(modelUri))
                throw new InvalidOperationException("modelUri is required.");

            var objectBrowseName = CsvTypeMapping.SanitizeBrowseName(request.ObjectBrowseName);
            if (objectBrowseName.Length == 0)
                throw new InvalidOperationException("objectBrowseName is required.");

            if (table.Rows.Count > MaxRows)
                throw new InvalidOperationException(
                    $"The file has {table.Rows.Count} rows; at most {MaxRows} can be imported at once.");

            var result = new CsvBuildResult();
            var columns = ResolveColumns(table, request, result);

            if (!columns.TryGetValue(CsvColumnRole.BrowseName, out var nameColumns))
                throw new InvalidOperationException("One column must be mapped to BrowseName.");

            var context = new BuildContext(
                ModelUri: modelUri!,
                AllocateNodeId: allocateNodeId,
                EncodeValue: encodeValue,
                AddressSpace: addressSpace,
                AnalogDeclarations: new Dictionary<string, InstanceDeclaration>(StringComparer.OrdinalIgnoreCase));

            // --- work out what every row becomes, before creating anything --------------
            var plans = PlanRows(table, request, columns, nameColumns[0].Index, addressSpace, result, context);

            // --- the folder, when one was asked for ------------------------------------
            var parentNodeId = ResolveParent(request, addressSpace);
            if (request.NewFolder)
            {
                var folderName = CsvTypeMapping.SanitizeBrowseName(
                    string.IsNullOrWhiteSpace(request.FolderBrowseName)
                        ? objectBrowseName : request.FolderBrowseName);
                if (folderName.Length == 0)
                    throw new InvalidOperationException("folderBrowseName is required when newFolder is set.");

                var folderNode = NewObject(context, folderName, folderName, null, FolderType, parentNodeId);
                result.FolderNode = folderNode;
                result.Nodes.Add(folderNode);
                context.FolderNodeIds.Add(folderNode.NodeId!);
                // A folder organizes what it holds, so the Object hangs off it from here.
                parentNodeId = folderNode.NodeId!;
            }

            // --- the ObjectType, when one was asked for --------------------------------
            string instanceTypeDefinitionId = BaseObjectType;
            if (request.CreateType)
            {
                var typeBrowseName = CsvTypeMapping.SanitizeBrowseName(
                    string.IsNullOrWhiteSpace(request.TypeBrowseName)
                        ? objectBrowseName + "Type" : request.TypeBrowseName);
                if (typeBrowseName.Length == 0)
                    throw new InvalidOperationException("typeBrowseName is required when createType is set.");

                var superTypeNodeId = string.IsNullOrWhiteSpace(request.SuperTypeNodeId)
                    ? BaseObjectType : request.SuperTypeNodeId;
                var superType = addressSpace.Read(superTypeNodeId)
                    ?? throw new InvalidOperationException($"SuperType '{superTypeNodeId}' not found.");
                if (superType.NodeClass != UaModel.NodeClass.UAObjectType)
                    throw new InvalidOperationException($"SuperType '{superTypeNodeId}' is not an ObjectType.");

                var typeNodeId = NewNodeId(modelUri!, allocateNodeId);
                var typeNode = new UaModel.UAObjectType
                {
                    NodeId = typeNodeId,
                    NodeClass = UaModel.NodeClass.UAObjectType,
                    BrowseName = $"nsu={modelUri};{typeBrowseName}",
                    DisplayName = LocalizedText(string.IsNullOrWhiteSpace(request.TypeDisplayName)
                        ? typeBrowseName : request.TypeDisplayName!),
                    Description = LocalizedText(request.Description),
                    ParentId = null,
                    IsAbstract = false,
                    References = new List<UaModel.Reference>
                    {
                        new() { ReferenceTypeId = HasSubtype, TargetId = superTypeNodeId, IsForward = false },
                    },
                };
                result.TypeNode = typeNode;
                result.Nodes.Add(typeNode);
                instanceTypeDefinitionId = typeNodeId;

                var declarationModellingRule = string.IsNullOrWhiteSpace(request.ModellingRuleId)
                    ? Mandatory : request.ModellingRuleId;
                foreach (var plan in plans)
                    EmitRow(result, context, plan, typeNodeId, declarationModellingRule);
            }

            // --- the Object, always -----------------------------------------------------
            var instanceNode = NewObject(context, objectBrowseName,
                string.IsNullOrWhiteSpace(request.ObjectDisplayName)
                    ? objectBrowseName : request.ObjectDisplayName!,
                // The description belongs on the type when there is one; repeating it on the
                // instance would just duplicate it in the nodeset.
                request.CreateType ? null : request.Description,
                instanceTypeDefinitionId, parentNodeId);
            result.InstanceNode = instanceNode;
            result.Nodes.Add(instanceNode);

            // An instance carries no modelling rules, so the same rows are emitted again
            // with a null rule rather than reusing the declaration nodes.
            foreach (var plan in plans)
                EmitRow(result, context, plan, instanceNode.NodeId!, modellingRuleId: null);

            result.VariablesCreated = plans.Count;
            result.PropertiesCreated = plans.Sum(p => p.PropertyCount);

            if (result.RowsSkipped > 0)
                result.Warnings.Add($"Skipped {result.RowsSkipped} row(s) with no usable name.");

            return result;
        }

        /// <summary>
        /// The values every emitted node needs, bundled so the emit helpers don't take a
        /// dozen parameters each.
        /// </summary>
        private sealed record BuildContext(
            string ModelUri,
            Func<string, string> AllocateNodeId,
            CsvVariantEncoder EncodeValue,
            AddressSpace AddressSpace,
            Dictionary<string, InstanceDeclaration> AnalogDeclarations)
        {
            /// <summary>
            /// Folders created by this build. They are not in the address space yet, so
            /// <see cref="ReferenceToParent"/> cannot discover their FolderType by reading.
            /// </summary>
            public HashSet<string> FolderNodeIds { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// Resolves every row to a <see cref="RowPlan"/>, collecting the warnings that come
        /// out of the data itself (blank names, renamed duplicates, unrecognized types and
        /// units) once rather than once per tree.
        /// </summary>
        private List<RowPlan> PlanRows(
            CsvTable table,
            CsvCreateTypeRequest request,
            Dictionary<CsvColumnRole, List<ResolvedColumn>> columns,
            int nameIndex,
            AddressSpace addressSpace,
            CsvBuildResult result,
            BuildContext context)
        {
            var variableTypeDefinitionId = ResolveVariableTypeDefinition(request, addressSpace, result);
            var canPromote = CanPromoteToAnalogItem(request, variableTypeDefinitionId, addressSpace, result);
            if (canPromote)
            {
                foreach (var (name, declaration) in IndexDeclarations(addressSpace, AnalogItemType))
                    context.AnalogDeclarations[name] = declaration;
            }

            var dataTypesByName = BuildDataTypeIndex(addressSpace);

            // The fallback for a row whose DataType cell is missing or unrecognized, and for
            // every row when no column is mapped to DataType at all. A mapped Value column
            // is the best evidence available — sniffing it means the editor needs no
            // "fallback DataType" control — and the request's own value is the last resort.
            var defaultDataType = string.IsNullOrWhiteSpace(request.DefaultDataTypeNodeId)
                ? CsvTypeMapping.Double : request.DefaultDataTypeNodeId;
            if (columns.TryGetValue(CsvColumnRole.Value, out var valueColumns))
                defaultDataType = CsvTypeMapping.InferDataType(ColumnValues(table, valueColumns[0].Index));

            columns.TryGetValue(CsvColumnRole.Property, out var propertyColumnsOrNull);
            var propertyColumns = propertyColumnsOrNull ?? new List<ResolvedColumn>();

            // A Property column's DataType is decided once for the whole column, not per
            // row: an inferred type that drifted between rows would give sibling instances
            // of the same declaration different DataTypes.
            var propertyDataTypes = new Dictionary<int, string>();
            foreach (var column in propertyColumns)
            {
                propertyDataTypes[column.Index] =
                    !string.IsNullOrWhiteSpace(column.Mapping.DataTypeNodeId)
                    && addressSpace.Read(column.Mapping.DataTypeNodeId!) != null
                        ? column.Mapping.DataTypeNodeId!
                        : CsvTypeMapping.InferDataType(ColumnValues(table, column.Index));
            }

            var unresolvedDataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unknownUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduper = new CsvTypeMapping.BrowseNameDeduper();
            var renamed = new List<string>();
            var plans = new List<RowPlan>();

            foreach (var row in table.Rows)
            {
                var browseName = CsvTypeMapping.SanitizeBrowseName(CsvTable.Cell(row, nameIndex));
                if (browseName.Length == 0) { result.RowsSkipped++; continue; }

                var (uniqueName, wasDuplicate) = deduper.Unique(browseName);
                if (wasDuplicate) renamed.Add($"{browseName} → {uniqueName}");

                var dataTypeNodeId = ResolveRowDataType(
                    row, columns, dataTypesByName, defaultDataType, unresolvedDataTypes);

                var unitsText = CellFor(row, columns, CsvColumnRole.EngineeringUnits);
                var euHigh = CsvTypeMapping.ParseNumber(CellFor(row, columns, CsvColumnRole.EuRangeHigh));
                var euLow = CsvTypeMapping.ParseNumber(CellFor(row, columns, CsvColumnRole.EuRangeLow));
                var instHigh = CsvTypeMapping.ParseNumber(CellFor(row, columns, CsvColumnRole.InstrumentRangeHigh));
                var instLow = CsvTypeMapping.ParseNumber(CellFor(row, columns, CsvColumnRole.InstrumentRangeLow));

                var hasAnalogData = !string.IsNullOrWhiteSpace(unitsText)
                    || euHigh.HasValue || euLow.HasValue || instHigh.HasValue || instLow.HasValue;
                // AnalogItemType constrains its Value to a Number, so a row with units on a
                // Boolean or String stays a plain DataItem. The units column is still
                // carried for such a row — just not as an EUInformation.
                var isAnalog = canPromote && hasAnalogData
                    && addressSpace.IsTypeOf(dataTypeNodeId, NumberDataType);
                if (isAnalog && !string.IsNullOrWhiteSpace(unitsText)
                    && !UnitLookup.Resolve(unitsText).IsKnown)
                {
                    unknownUnits.Add(unitsText.Trim());
                }

                var displayName = CellFor(row, columns, CsvColumnRole.DisplayName);

                // Named through one deduper per row, so a Property column headed "EURange"
                // can't collide with the real one.
                var childNames = new CsvTypeMapping.BrowseNameDeduper();
                if (isAnalog)
                {
                    if (!string.IsNullOrWhiteSpace(unitsText)) childNames.Unique("EngineeringUnits");
                    if (euLow.HasValue || euHigh.HasValue) childNames.Unique("EURange");
                    if (instLow.HasValue || instHigh.HasValue) childNames.Unique("InstrumentRange");
                }

                var properties = new List<RowProperty>();
                foreach (var column in propertyColumns)
                {
                    var cell = CsvTable.Cell(row, column.Index);
                    if (string.IsNullOrWhiteSpace(cell)) continue;

                    // A name the user typed is used as typed (sanitized only); one falling back
                    // to the header goes through the PascalCase rule.
                    var propertyName = string.IsNullOrWhiteSpace(column.Mapping.BrowseName)
                        ? CsvTypeMapping.PascalCaseBrowseName(table.Headers[column.Index])
                        : CsvTypeMapping.SanitizeBrowseName(column.Mapping.BrowseName);
                    if (propertyName.Length == 0) continue;
                    (propertyName, _) = childNames.Unique(propertyName);

                    properties.Add(new RowProperty(propertyName, propertyDataTypes[column.Index], cell));
                }

                plans.Add(new RowPlan(
                    BrowseName: uniqueName,
                    DisplayName: string.IsNullOrWhiteSpace(displayName) ? uniqueName : displayName,
                    Description: CellFor(row, columns, CsvColumnRole.Description),
                    DataTypeNodeId: dataTypeNodeId,
                    TypeDefinitionId: isAnalog ? AnalogItemType : variableTypeDefinitionId,
                    ValueText: CellFor(row, columns, CsvColumnRole.Value),
                    IsAnalog: isAnalog,
                    UnitsText: unitsText,
                    EuLow: euLow,
                    EuHigh: euHigh,
                    InstrumentLow: instLow,
                    InstrumentHigh: instHigh,
                    Properties: properties));
            }

            if (renamed.Count > 0)
                result.Warnings.Add("Renamed duplicates: " + Summarize(renamed));
            if (unresolvedDataTypes.Count > 0)
                result.Warnings.Add("Used the default DataType for unrecognized values: "
                    + Summarize(unresolvedDataTypes));
            if (unknownUnits.Count > 0)
                result.Warnings.Add("Units with no UNECE code (imported with UnitId 0): "
                    + Summarize(unknownUnits));

            return plans;
        }

        /// <summary>
        /// Emits one row under <paramref name="parentNodeId"/>: the Variable plus the
        /// property children it carries. <paramref name="modellingRuleId"/> is the rule for
        /// an instance declaration on a type, or null for an Object's own children — an
        /// instance never carries modelling rules.
        /// </summary>
        private static void EmitRow(
            CsvBuildResult result, BuildContext context, RowPlan plan,
            string parentNodeId, string? modellingRuleId)
        {
            var nodeId = NewNodeId(context.ModelUri, context.AllocateNodeId);
            var references = new List<UaModel.Reference>
            {
                new() { ReferenceTypeId = HasComponent, TargetId = parentNodeId, IsForward = false },
                new() { ReferenceTypeId = HasTypeDefinition, TargetId = plan.TypeDefinitionId, IsForward = true },
            };
            if (modellingRuleId != null)
                references.Add(new UaModel.Reference
                {
                    ReferenceTypeId = HasModellingRule, TargetId = modellingRuleId, IsForward = true,
                });

            var variable = new UaModel.UAVariable
            {
                NodeId = nodeId,
                NodeClass = UaModel.NodeClass.UAVariable,
                BrowseName = $"nsu={context.ModelUri};{plan.BrowseName}",
                DisplayName = LocalizedText(plan.DisplayName),
                Description = LocalizedText(plan.Description),
                ParentId = parentNodeId,
                TypeId = plan.TypeDefinitionId,
                ModellingRuleId = modellingRuleId,
                DataType = plan.DataTypeNodeId,
                ValueRank = -1,
                References = references,
            };

            if (!string.IsNullOrWhiteSpace(plan.ValueText))
                variable.Value = context.EncodeValue(
                    CellToJson(plan.ValueText, plan.DataTypeNodeId, context.AddressSpace), plan.DataTypeNodeId);

            result.Nodes.Add(variable);

            if (plan.IsAnalog)
            {
                // Only the properties the row has data for. The rest stay inherited from
                // AnalogItemType — materializing an empty EURange would add a node that
                // says nothing, and the Instantiate Children action already exists for
                // anyone who wants the full set.
                if (!string.IsNullOrWhiteSpace(plan.UnitsText))
                {
                    // NamespaceUri names whichever system the unit was found in, and is blank
                    // when it was found in neither — an unknown unit belongs to no system, so
                    // the UnitId stays 0 and the cell's own text carries the meaning.
                    var unit = UnitLookup.Resolve(plan.UnitsText);
                    AddDeclaredProperty(result, context, "EngineeringUnits", nodeId, modellingRuleId,
                        JsonSerializer.SerializeToElement(new
                        {
                            NamespaceUri = unit.NamespaceUri,
                            UnitId = unit.UnitId,
                            DisplayName = new { Text = unit.DisplayName },
                            Description = new { Text = unit.Description ?? unit.DisplayName },
                        }));
                }

                if (plan.EuLow.HasValue || plan.EuHigh.HasValue)
                    AddDeclaredProperty(result, context, "EURange", nodeId, modellingRuleId,
                        JsonSerializer.SerializeToElement(
                            new { Low = plan.EuLow ?? 0d, High = plan.EuHigh ?? 0d }));

                if (plan.InstrumentLow.HasValue || plan.InstrumentHigh.HasValue)
                    AddDeclaredProperty(result, context, "InstrumentRange", nodeId, modellingRuleId,
                        JsonSerializer.SerializeToElement(
                            new { Low = plan.InstrumentLow ?? 0d, High = plan.InstrumentHigh ?? 0d }));
            }

            foreach (var property in plan.Properties)
            {
                var propertyReferences = new List<UaModel.Reference>
                {
                    new() { ReferenceTypeId = HasProperty, TargetId = nodeId, IsForward = false },
                    new() { ReferenceTypeId = HasTypeDefinition, TargetId = PropertyType, IsForward = true },
                };
                if (modellingRuleId != null)
                    propertyReferences.Add(new UaModel.Reference
                    {
                        ReferenceTypeId = HasModellingRule, TargetId = modellingRuleId, IsForward = true,
                    });

                result.Nodes.Add(new UaModel.UAVariable
                {
                    NodeId = NewNodeId(context.ModelUri, context.AllocateNodeId),
                    NodeClass = UaModel.NodeClass.UAVariable,
                    BrowseName = $"nsu={context.ModelUri};{property.BrowseName}",
                    DisplayName = LocalizedText(property.BrowseName),
                    ParentId = nodeId,
                    TypeId = PropertyType,
                    ModellingRuleId = modellingRuleId,
                    DataType = property.DataTypeNodeId,
                    ValueRank = -1,
                    Value = context.EncodeValue(
                        CellToJson(property.Cell, property.DataTypeNodeId, context.AddressSpace),
                        property.DataTypeNodeId),
                    References = propertyReferences,
                });
            }
        }

        /// <summary>
        /// Builds an Object node with the given TypeDefinition. A null
        /// <paramref name="parentNodeId"/> makes it top-level: no ParentId and no
        /// hierarchical reference, only HasTypeDefinition — the same shape CreateTopLevelNode
        /// produces, and the only legal shape when there is no in-model parent to hang it off.
        /// </summary>
        private static UaModel.UAObject NewObject(
            BuildContext context, string browseName, string displayName, string? description,
            string typeDefinitionId, string? parentNodeId)
        {
            var references = new List<UaModel.Reference>
            {
                new() { ReferenceTypeId = HasTypeDefinition, TargetId = typeDefinitionId, IsForward = true },
            };
            if (parentNodeId != null)
                references.Insert(0, new UaModel.Reference
                {
                    ReferenceTypeId = ReferenceToParent(context, parentNodeId),
                    TargetId = parentNodeId,
                    IsForward = false,
                });

            return new UaModel.UAObject
            {
                NodeId = NewNodeId(context.ModelUri, context.AllocateNodeId),
                NodeClass = UaModel.NodeClass.UAObject,
                BrowseName = $"nsu={context.ModelUri};{browseName}",
                DisplayName = LocalizedText(displayName),
                Description = LocalizedText(description),
                ParentId = parentNodeId,
                TypeId = typeDefinitionId,
                References = references,
            };
        }

        /// <summary>
        /// Where the Object goes, or null for a top-level node.
        ///
        /// A ParentId may never point into another model: the parent reference is stored on
        /// the child, so a cross-model parent would make this model's nodeset depend on a node
        /// it cannot see, and the reference would dangle wherever the other model isn't
        /// loaded. That rules out the Core Objects folder (i=85) as well as any Object in a
        /// different private model — when there is nothing in-model to nest under, the node is
        /// top-level instead.
        /// </summary>
        private static string? ResolveParent(CsvCreateTypeRequest request, AddressSpace addressSpace)
        {
            if (string.IsNullOrWhiteSpace(request.ParentNodeId)) return null;

            var parentNodeId = request.ParentNodeId!;
            var parentModelUri = ExtractModelUri(parentNodeId);
            if (!string.Equals(parentModelUri, request.ModelUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Parent '{parentNodeId}' is in model '{parentModelUri}', not '{request.ModelUri}'. "
                    + "A parent must be in the same model as the node it contains.");

            var parent = addressSpace.Read(parentNodeId)
                ?? throw new InvalidOperationException($"Parent '{parentNodeId}' not found.");
            if (parent.NodeClass != UaModel.NodeClass.UAObject)
                throw new InvalidOperationException($"Parent '{parentNodeId}' is not an Object.");

            return parentNodeId;
        }

        /// <summary>
        /// A folder organizes what it holds (Part 3, 5.5.3); any other Object composes it.
        /// A folder created in this same build isn't in the address space yet, so that case is
        /// answered from the context's own record of it rather than by reading.
        /// </summary>
        private static string ReferenceToParent(BuildContext context, string parentNodeId)
        {
            if (context.FolderNodeIds.Contains(parentNodeId)) return Organizes;

            var parent = context.AddressSpace.Read(parentNodeId);
            if (parent?.TypeId is { } typeId && context.AddressSpace.IsTypeOf(typeId, FolderType))
                return Organizes;

            return HasComponent;
        }

        /// <summary>
        /// The model a NodeId belongs to. A NodeId with no <c>nsu=</c> prefix is namespace 0,
        /// the Core model.
        /// </summary>
        private static string ExtractModelUri(string nodeId)
        {
            if (nodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase))
            {
                var semi = nodeId.IndexOf(';');
                if (semi > 4) return nodeId[4..semi];
            }
            return OpcUaCoreModelUri;
        }

        /// <summary>
        /// Creates one of AnalogItemType's own property children (EngineeringUnits, EURange,
        /// InstrumentRange) under the row's variable, copying BrowseName, TypeDefinition and
        /// DataType from the instance declaration rather than hardcoding them.
        ///
        /// Copying the declaration's BrowseName verbatim matters: these live in the Core
        /// namespace and are stored without an <c>nsu=</c> prefix, while the node itself is
        /// allocated in the target model. Qualifying the BrowseName with the target model
        /// would silently move it out of ns0.
        /// </summary>
        private static void AddDeclaredProperty(
            CsvBuildResult result,
            BuildContext context,
            string declarationName,
            string parentNodeId,
            string? modellingRuleId,
            JsonElement value)
        {
            if (!context.AnalogDeclarations.TryGetValue(declarationName, out var declaration))
            {
                // The Core nodeset in this workspace doesn't declare it — skip rather than
                // invent a child whose DataType we would have to guess.
                result.Warnings.Add($"AnalogItemType declares no '{declarationName}' in this workspace; skipped.");
                return;
            }

            var source = declaration.SourceNode as UaModel.UAVariable;
            var dataType = source?.DataType;
            var typeDefinitionId = declaration.TypeId ?? PropertyType;
            var referenceTypeId = declaration.ReferenceTypeId ?? HasProperty;
            // On a type, the declaration's own rule (EURange is Mandatory, EngineeringUnits
            // Optional) beats the row's — it is what AnalogItemType says. On an instance
            // there is no rule at all.
            var effectiveModellingRule = modellingRuleId == null
                ? null
                : declaration.ModellingRuleId ?? modellingRuleId;

            var references = new List<UaModel.Reference>
            {
                new() { ReferenceTypeId = referenceTypeId, TargetId = parentNodeId, IsForward = false },
                new() { ReferenceTypeId = HasTypeDefinition, TargetId = typeDefinitionId, IsForward = true },
            };
            if (!string.IsNullOrEmpty(effectiveModellingRule))
                references.Add(new UaModel.Reference
                {
                    ReferenceTypeId = HasModellingRule, TargetId = effectiveModellingRule, IsForward = true,
                });

            result.Nodes.Add(new UaModel.UAVariable
            {
                NodeId = NewNodeId(context.ModelUri, context.AllocateNodeId),
                NodeClass = UaModel.NodeClass.UAVariable,
                BrowseName = declaration.BrowseName,
                DisplayName = declaration.DisplayName ?? LocalizedText(declarationName),
                ParentId = parentNodeId,
                TypeId = typeDefinitionId,
                ModellingRuleId = effectiveModellingRule,
                DataType = dataType,
                ValueRank = source?.ValueRank ?? -1,
                Value = context.EncodeValue(value, dataType),
                References = references,
            });
        }

        #endregion

        #region Mapping helpers

        /// <summary>A column the user kept, paired with the role they confirmed for it.</summary>
        private sealed record ResolvedColumn(int Index, CsvColumnRole Role, CsvColumnMapping Mapping);

        /// <summary>
        /// Turns the request's column list into a role → columns lookup, dropping entries
        /// that name a column the file doesn't have or a role that doesn't parse. Only
        /// Property may hold more than one column; a duplicate single-winner role keeps the
        /// first and warns, rather than letting the last one silently win.
        /// </summary>
        private static Dictionary<CsvColumnRole, List<ResolvedColumn>> ResolveColumns(
            CsvTable table, CsvCreateTypeRequest request, CsvBuildResult result)
        {
            var byRole = new Dictionary<CsvColumnRole, List<ResolvedColumn>>();

            foreach (var mapping in request.Columns ?? new List<CsvColumnMapping>())
            {
                if (mapping.Index < 0 || mapping.Index >= table.Headers.Count)
                {
                    result.Warnings.Add($"Ignored a mapping for column {mapping.Index}, which the file does not have.");
                    continue;
                }

                if (!Enum.TryParse<CsvColumnRole>(mapping.Role, ignoreCase: true, out var role))
                {
                    result.Warnings.Add(
                        $"Ignored unknown role '{mapping.Role}' on column '{table.Headers[mapping.Index]}'.");
                    continue;
                }
                if (role == CsvColumnRole.Ignore) continue;

                if (!byRole.TryGetValue(role, out var list))
                {
                    byRole[role] = new List<ResolvedColumn> { new(mapping.Index, role, mapping) };
                }
                else if (role == CsvColumnRole.Property)
                {
                    list.Add(new ResolvedColumn(mapping.Index, role, mapping));
                }
                else
                {
                    result.Warnings.Add(
                        $"Column '{table.Headers[mapping.Index]}' also asked for role {role}, which "
                        + $"'{table.Headers[list[0].Index]}' already holds; it was ignored.");
                }
            }

            return byRole;
        }

        /// <summary>The cell for a single-winner role, or an empty string when unmapped.</summary>
        private static string CellFor(
            List<string> row, Dictionary<CsvColumnRole, List<ResolvedColumn>> columns, CsvColumnRole role) =>
            columns.TryGetValue(role, out var list) ? CsvTable.Cell(row, list[0].Index) : string.Empty;

        private static string ResolveRowDataType(
            List<string> row,
            Dictionary<CsvColumnRole, List<ResolvedColumn>> columns,
            Dictionary<string, string> dataTypesByName,
            string defaultDataType,
            HashSet<string> unresolved)
        {
            var raw = CellFor(row, columns, CsvColumnRole.DataType);
            if (string.IsNullOrWhiteSpace(raw)) return defaultDataType;

            var resolved = ResolveDataType(raw, dataTypesByName);
            if (resolved != null) return resolved;

            unresolved.Add(raw.Trim());
            return defaultDataType;
        }

        /// <summary>
        /// The requested TypeDefinition for the row variables, or BaseDataVariableType when
        /// it isn't a VariableType in this workspace (a workspace whose Core nodeset predates
        /// DataItemType, say) — a dangling HasTypeDefinition would otherwise be baked into
        /// the model.
        /// </summary>
        private static string ResolveVariableTypeDefinition(
            CsvCreateTypeRequest request, AddressSpace addressSpace, CsvBuildResult result)
        {
            var requested = string.IsNullOrWhiteSpace(request.VariableTypeDefinitionId)
                ? DataItemType : request.VariableTypeDefinitionId;

            if (addressSpace.Read(requested)?.NodeClass == UaModel.NodeClass.UAVariableType) return requested;

            result.Warnings.Add($"TypeDefinition '{requested}' is not a VariableType in this workspace; "
                + "used BaseDataVariableType instead.");
            return BaseDataVariableType;
        }

        /// <summary>
        /// Promotion is only sound when AnalogItemType exists here and actually derives from
        /// the chosen TypeDefinition — otherwise swapping it in for a row would move that row
        /// outside the type the user picked.
        /// </summary>
        private static bool CanPromoteToAnalogItem(
            CsvCreateTypeRequest request, string variableTypeDefinitionId,
            AddressSpace addressSpace, CsvBuildResult result)
        {
            if (!request.PromoteToAnalogItem) return false;

            if (addressSpace.Read(AnalogItemType) == null)
            {
                result.Warnings.Add(
                    "AnalogItemType is not present in this workspace; units and ranges were not promoted.");
                return false;
            }

            if (!addressSpace.IsTypeOf(AnalogItemType, variableTypeDefinitionId))
            {
                result.Warnings.Add("AnalogItemType does not derive from the chosen TypeDefinition, "
                    + "so units and ranges were not promoted.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// The instance declarations of a type, keyed by BrowseName with any namespace
        /// prefix stripped — the form the CSV ruleset names them in.
        /// </summary>
        private static Dictionary<string, InstanceDeclaration> IndexDeclarations(
            AddressSpace addressSpace, string typeNodeId)
        {
            var map = new Dictionary<string, InstanceDeclaration>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in addressSpace.GetInstanceDeclarations(typeNodeId))
            {
                var name = StripNamespace(declaration.BrowseName);
                if (name.Length > 0) map.TryAdd(name, declaration);
            }
            return map;
        }

        #endregion

        #region DataType resolution

        /// <summary>
        /// Every concrete DataType in the workspace, keyed by BrowseName without its
        /// namespace, so a CSV naming a model's own DataType ("MotorState") resolves as
        /// readily as one naming a core type. First definition wins on a name collision
        /// across namespaces; abstract DataTypes are left out because they cannot type a
        /// Variable.
        /// </summary>
        private static Dictionary<string, string> BuildDataTypeIndex(AddressSpace addressSpace)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in addressSpace.Nodes)
            {
                if (node.NodeClass != UaModel.NodeClass.UADataType) continue;
                if (node.NodeId == null || node.IsAbstract == true) continue;

                var name = StripNamespace(node.BrowseName);
                if (name.Length > 0) map.TryAdd(name, node.NodeId);
            }
            return map;
        }

        /// <summary>
        /// The alias table first — it is what maps PLC spellings like REAL and DINT — then
        /// the workspace's own DataType BrowseNames. Null when neither knows the value.
        /// </summary>
        private static string? ResolveDataType(string raw, Dictionary<string, string> dataTypesByName)
        {
            var alias = CsvTypeMapping.ResolveDataTypeAlias(raw);
            if (alias != null) return alias;
            return dataTypesByName.TryGetValue(raw.Trim(), out var nodeId) ? nodeId : null;
        }

        private static string DataTypeName(AddressSpace addressSpace, string nodeId) =>
            StripNamespace(addressSpace.Read(nodeId)?.BrowseName) is { Length: > 0 } name ? name : nodeId;

        /// <summary>
        /// Converts a cell into the JSON shape the Variant encoder expects for the target
        /// DataType: a JSON number for a Number, a boolean for a Boolean, a string otherwise.
        /// A cell that doesn't parse falls through to a string, which the encoder stores
        /// as-is rather than throwing the user's data away.
        /// </summary>
        private static JsonElement CellToJson(string cell, string? dataTypeNodeId, AddressSpace addressSpace)
        {
            var text = cell.Trim();
            if (dataTypeNodeId == null) return JsonSerializer.SerializeToElement(text);

            if (addressSpace.IsTypeOf(dataTypeNodeId, BooleanDataType))
            {
                var parsed = CsvTypeMapping.Normalize(text) switch
                {
                    "true" or "yes" or "y" or "on" or "1" => (bool?)true,
                    "false" or "no" or "n" or "off" or "0" => false,
                    _ => null,
                };
                if (parsed.HasValue) return JsonSerializer.SerializeToElement(parsed.Value);
            }
            else if (addressSpace.IsTypeOf(dataTypeNodeId, IntegerDataType)
                || addressSpace.IsTypeOf(dataTypeNodeId, UIntegerDataType))
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return JsonSerializer.SerializeToElement(l);
            }
            else if (addressSpace.IsTypeOf(dataTypeNodeId, NumberDataType))
            {
                if (CsvTypeMapping.ParseNumber(text) is { } d)
                    return JsonSerializer.SerializeToElement(d);
            }

            return JsonSerializer.SerializeToElement(text);
        }

        #endregion

        #region Small helpers

        private static IEnumerable<string> ColumnValues(CsvTable table, int index) =>
            table.Rows.Select(row => CsvTable.Cell(row, index));

        private static string NewNodeId(string modelUri, Func<string, string> allocateNodeId) =>
            $"nsu={modelUri};i={allocateNodeId(modelUri)}";

        private static UaModel.LocalizedText? LocalizedText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new UaModel.LocalizedText { T = new List<List<string>> { new() { "", text } } };
        }

        /// <summary>
        /// Drops a BrowseName's or NodeId's namespace prefix. A bare name (no ';') is
        /// already unqualified — that is how ns0 BrowseNames are stored — so it comes back
        /// unchanged.
        /// </summary>
        private static string StripNamespace(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var semi = value.IndexOf(';');
            return semi >= 0 ? value[(semi + 1)..] : value;
        }

        /// <summary>Joins a warning's offending values, capped so one bad column can't fill the dialog.</summary>
        private static string Summarize(IEnumerable<string> values)
        {
            var list = values.ToList();
            var head = string.Join(", ", list.Take(WarningListLimit));
            return list.Count > WarningListLimit ? $"{head} (+{list.Count - WarningListLimit} more)" : head;
        }

        #endregion
    }
}
