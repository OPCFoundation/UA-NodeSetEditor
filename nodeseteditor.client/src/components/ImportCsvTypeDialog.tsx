import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import Alert from '@mui/material/Alert';
import Paper from '@mui/material/Paper';

import api, { extractErrorMessage } from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import type { Node as RestNode } from '../model/Node';
import { nodeClassToNum } from '../model/NodeFormatting';
import { setLastModelUri, getLastModelUri } from '../utils/lastModelUri';
import { extractNamespaceUri } from '../utils/formatNodeId';

import { ModelDialog } from './ModelDialog';
import { StripedTable } from './StripedTable';

/**
 * The Parent dropdown's "no parent" value, which creates a top-level node. A ParentId may
 * never point into another model, so the Core Objects folder (i=85) is not offered — there is
 * nothing to nest under until the model has an Object of its own.
 */
const NO_PARENT = '';

/**
 * The DataTypes offered for a Property column: the Part 6 built-in scalars. The extra columns
 * of a tag list hold plain numbers, flags, text or timestamps, and these are exactly the types
 * the server's value sniffer produces — so offering every DataType in the workspace (thousands
 * of structures and enumerations) was noise. Fixed rather than fetched: these are ns0 and
 * always present.
 */
const BUILTIN_DATA_TYPES = [
   { nodeId: 'i=1', name: 'Boolean' },
   { nodeId: 'i=2', name: 'SByte' },
   { nodeId: 'i=3', name: 'Byte' },
   { nodeId: 'i=4', name: 'Int16' },
   { nodeId: 'i=5', name: 'UInt16' },
   { nodeId: 'i=6', name: 'Int32' },
   { nodeId: 'i=7', name: 'UInt32' },
   { nodeId: 'i=8', name: 'Int64' },
   { nodeId: 'i=9', name: 'UInt64' },
   { nodeId: 'i=10', name: 'Float' },
   { nodeId: 'i=11', name: 'Double' },
   { nodeId: 'i=12', name: 'String' },
   { nodeId: 'i=13', name: 'DateTime' },
   { nodeId: 'i=14', name: 'Guid' },
   { nodeId: 'i=15', name: 'ByteString' },
];

// The theme's spacing unit is 1px, so these read as pixels.
const FORM_ROW_GAP = 20;
const FIELD_GAP = 16;
const PAGE_PADDING = 20;

/**
 * Width of a single-line form field. Fixed rather than fullWidth: a name or a parent picker
 * stretched across an "lg" dialog is mostly empty, and a fixed width leaves room beside it
 * for the checkbox that qualifies it.
 */
const FIELD_WIDTH = 340;

/**
 * Roles offered in the UI, in the order they read down a tag list. A subset of the
 * CsvColumnRole enum in NodeSetEditor.Server/Services/CsvTypeMapping.cs — the names travel
 * over the wire as strings, so they must match it exactly, but the UI need not offer every
 * one. ModellingRule is deliberately absent: every row is Mandatory, which is the server's
 * default when the mapping omits it.
 */
const COLUMN_ROLES = [
   'Ignore',
   'BrowseName',
   'DisplayName',
   'Description',
   'DataType',
   'Value',
   'EngineeringUnits',
   'EuRangeLow',
   'EuRangeHigh',
   'InstrumentRangeLow',
   'InstrumentRangeHigh',
   'Property',
] as const;

type ColumnRole = typeof COLUMN_ROLES[number];

/**
 * The analyzer can propose a role this UI doesn't offer (a column headed "Mandatory" comes
 * back as ModellingRule). Such a column is ignored rather than left holding a value the
 * dropdown can't display — the user can still map it to something else by hand.
 */
const toColumnRole = (proposed: string): ColumnRole =>
   (COLUMN_ROLES as readonly string[]).includes(proposed) ? proposed as ColumnRole : 'Ignore';

/** Every role except Property (and Ignore) may be held by exactly one column. */
const isSingleWinner = (role: ColumnRole) => role !== 'Ignore' && role !== 'Property';

/** Roles for which the DataType picker is meaningful. */
const usesDataType = (role: ColumnRole) => role === 'Property';

/** Roles for which the Property Name field is meaningful. */
const usesPropertyName = (role: ColumnRole) => role === 'Property';

/** The wizard's two pages: map the columns, then describe the Object. */
type WizardStep = 'columns' | 'details';

interface CsvDataTypeMatch {
   rawValue: string;
   dataTypeNodeId?: string | null;
   dataTypeName?: string | null;
}

interface CsvColumnAnalysis {
   index: number;
   header: string;
   sampleValues: string[];
   proposedRole: string;
   proposedBrowseName?: string | null;
   proposedDataTypeNodeId?: string | null;
   dataTypeValues?: CsvDataTypeMatch[] | null;
}

interface CsvAnalysisResult {
   fileName: string;
   rowCount: number;
   delimiter: string;
   proposedObjectBrowseName: string;
   proposedTypeBrowseName: string;
   columns: CsvColumnAnalysis[];
   warnings: string[];
}

interface CsvCreateTypeResult {
   instance?: RestNode;
   type?: RestNode;
   folder?: RestNode;
   variablesCreated: number;
   propertiesCreated: number;
   rowsSkipped: number;
   warnings: string[];
}

/** What the caller needs to switch the view to the Object that was just created. */
export interface CsvImportedTypeInfo {
   nodeId: string;
   displayName: string;
   nodeClass: number;
   superTypeIds: string[];
}

interface ImportCsvTypeDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /** The CSV to import. Chosen before the wizard opens; see ImportCsvFlow. */
   file: File;
   /** Re-opens the file picker. A different file replaces this one. */
   onChangeFile: () => void;
   /** Pre-selects the namespace when it is one of the workspace's editable models. */
   defaultModelUri?: string;
   onCreated: (created: CsvImportedTypeInfo) => void;
}

/**
 * Maps an already-chosen CSV onto a new Object: one row becomes one Variable under it.
 * Optionally also creates the ObjectType those rows describe, and makes the Object an
 * instance of it.
 *
 * Two pages — map the columns, then describe the Object. The split exists because the two
 * decisions are unrelated: which column means what is about the spreadsheet, and where the
 * Object lives is about the model. Showing both at once made a tall form where the column
 * table (the part that actually needs attention) competed with fields usually left alone.
 *
 * Nothing is created until the last page: the server proposes the mapping (POST csv/analyze),
 * the user adjusts it, and only then does POST csv/create-type run. The file is sent again
 * with the confirmed mapping rather than parked server-side between the two calls.
 *
 * Mount it per file (`key` on the file's identity): the proposal is the base for every
 * column choice, so a new file must start from a clean slate.
 */
export const ImportCsvTypeDialog: React.FC<ImportCsvTypeDialogProps> = ({
   open,
   onClose,
   workspaceId,
   file,
   onChangeFile,
   defaultModelUri,
   onCreated,
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();

   const [step, setStep] = React.useState<WizardStep>('columns');
   const [isCreating, setIsCreating] = React.useState(false);
   const [createError, setCreateError] = React.useState<string | null>(null);

   const headers = { 'OpcUa-Server': idToUrn(workspaceId) };

   // The analysis is the base for every column decision below. A query rather than an effect,
   // so the loading and error states come for free and nothing is seeded by a setState.
   const { data: analysis, isLoading: isAnalyzing, error: analyzeError } = useQuery({
      queryKey: ['csvAnalyze', workspaceId, file.name, file.size, file.lastModified],
      queryFn: async () => {
         const form = new FormData();
         form.append('file', file, file.name);
         const response = await api.post<CsvAnalysisResult>('/opcua/v1/csv/analyze', form, {
            headers: { ...headers, 'Content-Type': 'multipart/form-data' },
         });
         return response.data;
      },
      enabled: open,
      staleTime: Infinity,
      retry: false,
   });

   // Per-column edits, overlaid on the analyzer's proposal rather than copied out of it. An
   // overlay keeps the proposal as the source of truth and — unlike a `choice || proposal`
   // fallback — lets a value be deliberately cleared: the key is present holding "".
   const [roleOverrides, setRoleOverrides] = React.useState<Record<number, ColumnRole>>({});
   const [nameOverrides, setNameOverrides] = React.useState<Record<number, string>>({});
   const [dataTypeOverrides, setDataTypeOverrides] = React.useState<Record<number, string>>({});

   const roles = React.useMemo<Record<number, ColumnRole>>(() => {
      const base: Record<number, ColumnRole> = {};
      for (const c of analysis?.columns ?? []) base[c.index] = toColumnRole(c.proposedRole);
      return { ...base, ...roleOverrides };
   }, [analysis, roleOverrides]);

   const propertyNames = React.useMemo<Record<number, string>>(() => {
      const base: Record<number, string> = {};
      for (const c of analysis?.columns ?? []) base[c.index] = c.proposedBrowseName ?? '';
      return { ...base, ...nameOverrides };
   }, [analysis, nameOverrides]);

   const dataTypes = React.useMemo<Record<number, string>>(() => {
      const base: Record<number, string> = {};
      for (const c of analysis?.columns ?? []) base[c.index] = c.proposedDataTypeNodeId ?? '';
      return { ...base, ...dataTypeOverrides };
   }, [analysis, dataTypeOverrides]);

   // null = untouched (use the proposal); "" = deliberately cleared. `??` keeps them distinct,
   // which `||` would not.
   const [objectNameOverride, setObjectNameOverride] = React.useState<string | null>(null);
   const objectName = objectNameOverride ?? analysis?.proposedObjectBrowseName ?? '';

   const [parentNodeId, setParentNodeId] = React.useState(NO_PARENT);
   const [newFolder, setNewFolder] = React.useState(false);
   const [createType, setCreateType] = React.useState(false);
   // Seeded from the object name when their checkbox is ticked and plain state thereafter.
   // Deliberately not derived: that re-inserted the default the instant the user cleared the
   // field, making it impossible to type a different name.
   const [folderName, setFolderName] = React.useState('');
   const [typeName, setTypeName] = React.useState('');
   const [description, setDescription] = React.useState('');
   const [modelUriChoice, setModelUriChoice] = React.useState('');

   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
            '/opcua/v1/namespaces/info', { headers });
         return response.data;
      },
      enabled: open,
   });

   // Only a private, checked-out model accepts node writes — the same condition the server
   // enforces — so a private-but-not-editable one must not be offered.
   const editableModels = React.useMemo(() => {
      return (namespacesData?.results ?? [])
         .filter(ns => ns.isPrivate && ns.isEditable && ns.uri)
         .map(ns => ({ modelUri: ns.uri, label: ns.name ? `${ns.name} - ${ns.uri}` : ns.uri }));
   }, [namespacesData]);

   // Which namespace to offer before the user picks one: the caller's page context, then the
   // last one used, then the first. Each candidate has to exist in the list or the dropdown
   // renders blank.
   const defaultModelCandidate = React.useMemo(() => {
      if (editableModels.length === 0) return '';
      const lastUsed = getLastModelUri();
      if (defaultModelUri && editableModels.some(m => m.modelUri === defaultModelUri)) return defaultModelUri;
      if (lastUsed && editableModels.some(m => m.modelUri === lastUsed)) return lastUsed;
      return editableModels[0].modelUri;
   }, [editableModels, defaultModelUri]);

   const selectedModelUri = modelUriChoice || defaultModelCandidate;

   // Candidate parents: the top-level Objects already in the selected model. A ParentId may
   // never cross models, so nothing outside it — including the Core Objects folder — can be
   // offered. query/types with nodeClass=Object returns exactly the parentless Objects.
   const { data: modelObjects } = useQuery({
      queryKey: ['csvParentOptions', workspaceId, selectedModelUri],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: { nodeClass: 'Object', namespaceUri: selectedModelUri, start: 0, count: 1000 },
            headers,
         });
         return response.data.results ?? [];
      },
      enabled: open && !!selectedModelUri,
   });

   const parentOptions = React.useMemo(() => {
      const options = [{ nodeId: NO_PARENT, label: t('csvImport.noParent', '(none — top level)') }];
      for (const node of modelObjects ?? []) {
         if (!node.nodeId) continue;
         if (extractNamespaceUri(node.nodeId) !== selectedModelUri) continue;
         options.push({
            nodeId: node.nodeId,
            label: node.browseName?.split(';').pop() ?? node.displayName?.text ?? node.nodeId,
         });
      }
      return options;
   }, [modelObjects, selectedModelUri, t]);

   // Switching the model invalidates a parent picked from the previous one, and submitting it
   // would be rejected — so it falls back to no parent as soon as it leaves the list.
   const effectiveParentNodeId = parentOptions.some(o => o.nodeId === parentNodeId)
      ? parentNodeId : NO_PARENT;

   /**
    * Ticking a box reveals its name field, which is where its default belongs — seeded here,
    * once, rather than derived on every render. An empty field is re-seeded on re-tick so
    * clearing it and changing your mind doesn't leave you with nothing.
    */
   const handleNewFolderChange = (checked: boolean) => {
      setNewFolder(checked);
      if (checked && folderName.trim() === '') setFolderName(objectName);
      setCreateError(null);
   };

   const handleCreateTypeChange = (checked: boolean) => {
      setCreateType(checked);
      if (checked && typeName.trim() === '' && objectName !== '') setTypeName(`${objectName}Type`);
      setCreateError(null);
   };

   /**
    * Changes one column's role. Taking a single-winner role off another column rather than
    * letting both claim it keeps what the dialog shows and what the server will do the same
    * thing. Reads the merged view, because the conflict may come from the proposal.
    */
   const handleRoleChange = (index: number, role: ColumnRole) => {
      setCreateError(null);
      setRoleOverrides(prev => {
         const next = { ...prev, [index]: role };
         if (isSingleWinner(role)) {
            for (const [key, existing] of Object.entries(roles)) {
               const other = Number(key);
               if (other !== index && existing === role) next[other] = 'Ignore';
            }
         }
         return next;
      });
   };

   const nameColumnIndex = React.useMemo(() => {
      const entry = Object.entries(roles).find(([, role]) => role === 'BrowseName');
      return entry ? Number(entry[0]) : -1;
   }, [roles]);

   /** Columns the user kept — what the summary on the last page counts. */
   const mappedColumnCount = React.useMemo(
      () => Object.values(roles).filter(role => role !== 'Ignore').length,
      [roles]);

   // Gate for leaving the Columns page. Enforced here rather than only at Create so the user
   // finds out while still looking at the table they need to fix.
   const columnsAreUsable = !!analysis && analysis.rowCount > 0 && nameColumnIndex >= 0;

   // Each of these is also flagged on the field itself, so a disabled Create button always
   // has something visibly red explaining it.
   const objectNameInvalid = objectName.trim() === '';
   const folderNameInvalid = newFolder && folderName.trim() === '';
   const typeNameInvalid = createType && typeName.trim() === '';

   const canCreate = columnsAreUsable
      && selectedModelUri !== ''
      && !objectNameInvalid
      && !folderNameInvalid
      && !typeNameInvalid;

   const handleCreate = async () => {
      if (!canCreate || !analysis) return;
      setIsCreating(true);
      setCreateError(null);
      try {
         const mapping = {
            modelUri: selectedModelUri,
            objectBrowseName: objectName.trim(),
            parentNodeId: effectiveParentNodeId || undefined,
            newFolder,
            folderBrowseName: newFolder ? folderName.trim() : undefined,
            createType,
            typeBrowseName: createType ? typeName.trim() : undefined,
            description,
            // Omitted on purpose, because the server's defaults are the behaviour we want and
            // there is nothing for the user to decide:
            //   objectDisplayName / typeDisplayName — default to the BrowseName.
            //   superTypeNodeId            — BaseObjectType.
            //   modellingRuleId            — every row's declaration is Mandatory.
            //   variableTypeDefinitionId   — DataItemType, which every row starts as.
            //   promoteToAnalogItem        — on, so a numeric row carrying units or a range
            //                                becomes an AnalogItemType by itself.
            //   defaultDataTypeNodeId      — the server infers a row's DataType, falling back
            //                                to the Value column's sniffed type.
            columns: analysis.columns.map(c => ({
               index: c.index,
               role: roles[c.index] ?? 'Ignore',
               browseName: propertyNames[c.index] || undefined,
               dataTypeNodeId: dataTypes[c.index] || undefined,
            })),
         };

         const form = new FormData();
         form.append('file', file, file.name);
         form.append('mapping', JSON.stringify(mapping));

         const response = await api.post<CsvCreateTypeResult>('/opcua/v1/csv/create-type', form, {
            headers: { ...headers, 'Content-Type': 'multipart/form-data' },
         });

         const created = response.data.instance;
         if (!created?.nodeId) throw new Error(t('csvImport.createFailed', 'Nothing was created'));

         setLastModelUri(selectedModelUri);
         // The import adds an Object and its whole subtree, so every cached type list and
         // tree branch in this workspace is stale.
         queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
         queryClient.invalidateQueries({ queryKey: ['subtypes'] });
         queryClient.invalidateQueries({ queryKey: ['workspaceTypes'] });
         queryClient.invalidateQueries({ queryKey: ['nodeChildren'] });
         queryClient.invalidateQueries({ queryKey: ['nextNodeId'] });
         queryClient.invalidateQueries({ queryKey: ['csvParentOptions'] });

         onCreated({
            nodeId: created.nodeId,
            displayName: created.displayName?.text ?? objectName.trim(),
            nodeClass: nodeClassToNum(created.nodeClass),
            superTypeIds: created.superTypeIds ?? [],
         });
      } catch (e) {
         setCreateError(extractErrorMessage(e, t('csvImport.createFailed', 'Nothing was created')));
      } finally {
         setIsCreating(false);
      }
   };

   const error = createError ?? (analyzeError
      ? extractErrorMessage(analyzeError, t('csvImport.analyzeFailed', 'Could not read the file'))
      : null);

   /**
    * The wizard's Back/Next/Create buttons. ModelDialog always appends its own Cancel, so
    * these are only the step moves. An error replaces the page content, so it offers none.
    */
   const stepActions = () => {
      if (error || !analysis) return [];

      if (step === 'columns') {
         return [
            {
               label: t('csvImport.backToFile', 'Change file'),
               onClick: onChangeFile,
               variant: 'outlined' as const,
            },
            {
               label: t('csvImport.next', 'Next'),
               onClick: () => setStep('details'),
               disabled: !columnsAreUsable,
            },
         ];
      }

      return [
         {
            label: t('common.back', 'Back'),
            onClick: () => setStep('columns'),
            variant: 'outlined' as const,
            disabled: isCreating,
         },
         {
            label: isCreating
               ? t('csvImport.creating', 'Creating...')
               : t('csvImport.createAction', 'Create'),
            onClick: handleCreate,
            disabled: !canCreate || isCreating,
         },
      ];
   };

   const columns = [
      { key: 'header', label: t('csvImport.columnHeader', 'Column'), width: '28%' },
      { key: 'role', label: t('csvImport.columnRole', 'Maps to'), width: '24%' },
      { key: 'dataType', label: t('typeDetail.dataType', 'DataType'), width: '24%' },
      { key: 'propertyName', label: t('csvImport.columnPropertyName', 'Property Name'), width: '24%' },
   ];

   const rows = (analysis?.columns ?? []).map((column) => {
      const role = roles[column.index] ?? 'Ignore';
      return {
         header: (
            <Typography variant="body2" sx={{ fontWeight: role === 'Ignore' ? 400 : 600 }}>
               {column.header}
            </Typography>
         ),
         role: (
            <TextField
               select
               size="small"
               fullWidth
               value={role}
               onChange={(e) => handleRoleChange(column.index, e.target.value as ColumnRole)}
            >
               {COLUMN_ROLES.map(option => (
                  <MenuItem key={option} value={option}>
                     {t(`csvImport.role.${option}`, option)}
                  </MenuItem>
               ))}
            </TextField>
         ),
         dataType: usesDataType(role) ? (
            <TextField
               select
               size="small"
               fullWidth
               value={dataTypes[column.index] ?? ''}
               onChange={(e) =>
                  setDataTypeOverrides(prev => ({ ...prev, [column.index]: e.target.value }))}
            >
               {BUILTIN_DATA_TYPES.map(dt => (
                  <MenuItem key={dt.nodeId} value={dt.nodeId}>{dt.name}</MenuItem>
               ))}
            </TextField>
         ) : role === 'DataType' ? (
            <Typography variant="caption" color="text.secondary">
               {t('csvImport.dataTypePerRow', 'per row')}
            </Typography>
         ) : null,
         propertyName: usesPropertyName(role) ? (
            <TextField
               size="small"
               fullWidth
               value={propertyNames[column.index] ?? ''}
               onChange={(e) =>
                  setNameOverrides(prev => ({ ...prev, [column.index]: e.target.value }))}
            />
         ) : null,
      };
   });

   const unresolvedDataTypes = React.useMemo(() => {
      const column = (analysis?.columns ?? []).find(c => (roles[c.index] ?? '') === 'DataType');
      return (column?.dataTypeValues ?? []).filter(m => !m.dataTypeNodeId).map(m => m.rawValue);
   }, [analysis, roles]);

   const fileSummary = analysis
      ? t('csvImport.fileSummary',
         '{{fileName}} — {{rowCount}} rows, {{columnCount}} columns, delimiter "{{delimiter}}"', {
         fileName: analysis.fileName,
         rowCount: analysis.rowCount,
         columnCount: analysis.columns.length,
         delimiter: analysis.delimiter,
      })
      : '';

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('csvImport.title', 'Import CSV')}
         maxWidth="lg"
         isLoading={isAnalyzing || isCreating}
         isError={!!error}
         error={error ? new Error(error) : null}
         actions={stepActions()}
      >
         {/* ---- Page 1: the CSV's columns, and nothing else ---- */}
         {step === 'columns' && analysis && !error && (
            <Box sx={{ p: PAGE_PADDING, display: 'flex', flexDirection: 'column', gap: FORM_ROW_GAP }}>
               {nameColumnIndex < 0 && (
                  <Alert severity="error">
                     {t('csvImport.needsNameColumn',
                        'Set one column to BrowseName — every row needs a name.')}
                  </Alert>
               )}
               {analysis.rowCount === 0 && (
                  <Alert severity="error">
                     {t('csvImport.noRows', 'The file has a header row but no data rows.')}
                  </Alert>
               )}
               {unresolvedDataTypes.length > 0 && (
                  <Alert severity="warning">
                     {t('csvImport.unresolvedDataTypes',
                        'These values are not known DataTypes and will use the default: {{values}}',
                        { values: unresolvedDataTypes.join(', ') })}
                  </Alert>
               )}

               <Paper variant="outlined" sx={{ maxHeight: 460, overflow: 'auto' }}>
                  <StripedTable columns={columns} rows={rows} />
               </Paper>

               <Typography variant="caption" color="text.secondary">{fileSummary}</Typography>
            </Box>
         )}

         {/* ---- Page 2: where the Object goes and what it is called ---- */}
         {step === 'details' && analysis && !error && (
            <Box sx={{ p: PAGE_PADDING, display: 'flex', flexDirection: 'column', gap: FORM_ROW_GAP }}>
               <Box sx={{ display: 'flex', flexDirection: 'column', gap: FORM_ROW_GAP }}>
                  <TextField
                     label={t('typeDetail.model', 'Model')}
                     value={selectedModelUri}
                     onChange={(e) => { setModelUriChoice(e.target.value); setCreateError(null); }}
                     select
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true } }}
                  >
                     {editableModels.map(m => (
                        <MenuItem key={m.modelUri} value={m.modelUri}>{m.label}</MenuItem>
                     ))}
                  </TextField>

                  {/* Parent and Folder Name share one slot: checking New Folder means the
                      folder IS the parent, so showing both at once only invited the question
                      of which one wins. */}
                  <Box sx={{ display: 'flex', gap: FIELD_GAP, alignItems: 'center' }}>
                     {newFolder ? (
                        <TextField
                           label={t('csvImport.folderName', 'Folder Name')}
                           value={folderName}
                           onChange={(e) => { setFolderName(e.target.value); setCreateError(null); }}
                           required
                           error={folderNameInvalid}
                           size="small"
                           sx={{ width: FIELD_WIDTH }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        />
                     ) : (
                        <TextField
                           label={t('csvImport.parent', 'Parent')}
                           value={effectiveParentNodeId}
                           onChange={(e) => { setParentNodeId(e.target.value); setCreateError(null); }}
                           select
                           size="small"
                           sx={{ width: FIELD_WIDTH }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        >
                           {parentOptions.map(o => (
                              <MenuItem key={o.nodeId} value={o.nodeId}>{o.label}</MenuItem>
                           ))}
                        </TextField>
                     )}
                     <FormControlLabel
                        control={
                           <Checkbox
                              checked={newFolder}
                              onChange={(e) => handleNewFolderChange(e.target.checked)}
                           />
                        }
                        label={t('csvImport.newFolder', 'New Folder')}
                        sx={{ flexShrink: 0, whiteSpace: 'nowrap', mr: 0 }}
                     />
                  </Box>

                  <TextField
                     label={t('csvImport.objectName', 'Object Name')}
                     value={objectName}
                     onChange={(e) => { setObjectNameOverride(e.target.value); setCreateError(null); }}
                     required
                     error={objectNameInvalid}
                     size="small"
                     sx={{ width: FIELD_WIDTH }}
                     slotProps={{ inputLabel: { shrink: true } }}
                  />

                  <Box sx={{ display: 'flex', gap: FIELD_GAP, alignItems: 'center' }}>
                     <FormControlLabel
                        control={
                           <Checkbox
                              checked={createType}
                              onChange={(e) => handleCreateTypeChange(e.target.checked)}
                           />
                        }
                        label={t('csvImport.createTypeToo', 'Create Type')}
                        sx={{ flexShrink: 0, whiteSpace: 'nowrap', mr: 0 }}
                     />
                     {createType && (
                        <TextField
                           label={t('csvImport.typeName', 'Type Name')}
                           value={typeName}
                           onChange={(e) => { setTypeName(e.target.value); setCreateError(null); }}
                           required
                           error={typeNameInvalid}
                           size="small"
                           sx={{ width: FIELD_WIDTH }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        />
                     )}
                  </Box>

                  <TextField
                     label={t('typeDetail.description', 'Description')}
                     value={description}
                     onChange={(e) => setDescription(e.target.value)}
                     fullWidth
                     size="small"
                     multiline
                     minRows={2}
                     slotProps={{ inputLabel: { shrink: true } }}
                  />
               </Box>

               <Typography variant="caption" color="text.secondary">
                  {t('csvImport.detailsSummary',
                     '{{rowCount}} rows from {{fileName}} will become {{rowCount}} Variables, '
                     + 'using {{mappedColumnCount}} of {{columnCount}} columns.', {
                     fileName: analysis.fileName,
                     rowCount: analysis.rowCount,
                     mappedColumnCount,
                     columnCount: analysis.columns.length,
                  })}
               </Typography>

               {/* Repeated here because Back is one click away and these are the two findings
                   a user most often fixes by re-mapping a column. */}
               {nameColumnIndex < 0 && (
                  <Alert severity="error">
                     {t('csvImport.needsNameColumn',
                        'Set one column to BrowseName — every row needs a name.')}
                  </Alert>
               )}
               {unresolvedDataTypes.length > 0 && (
                  <Alert severity="warning">
                     {t('csvImport.unresolvedDataTypes',
                        'These values are not known DataTypes and will use the default: {{values}}',
                        { values: unresolvedDataTypes.join(', ') })}
                  </Alert>
               )}
            </Box>
         )}
      </ModelDialog>
   );
};

export default ImportCsvTypeDialog;
