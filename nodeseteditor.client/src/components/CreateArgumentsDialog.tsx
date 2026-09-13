import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import api, { extractErrorMessage } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { Node as RestNode } from '../model/Node';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import IconButton from '@mui/material/IconButton';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import Tooltip from '@mui/material/Tooltip';
import AddIcon from '@mui/icons-material/Add';
import DeleteIcon from '@mui/icons-material/Delete';

import { ModelDialog } from './ModelDialog';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { extractNamespaceUri } from '../utils/formatNodeId';
import { allValueRankOptions } from '../utils/valueRank';

/** OPC UA core namespace — InputArguments/OutputArguments BrowseNames live here. */
const CORE_NAMESPACE_URI = 'http://opcfoundation.org/UA/';
/** Argument DataType NodeId (ns0). */
const ARGUMENT_DATATYPE = 'i=296';
/** PropertyType / HasProperty / BaseDataType. */
const PROPERTY_TYPE = 'i=68';
const HAS_PROPERTY = 'i=46';
const BASE_DATA_TYPE = 'i=24';
/** Mandatory ModellingRule. InputArguments/OutputArguments must be Mandatory so
 *  they materialize when the owning Method is instantiated. (The server strips
 *  the ModellingRule automatically when the Method is not inside a type tree.) */
const MANDATORY_MODELLING_RULE = 'i=78';

interface CreateArgumentsDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /** NodeId of the Method whose arguments are being edited. */
   methodNodeId: string;
   onSaved: () => void;
}

/** One editable argument row. */
interface ArgRow {
   name: string;
   dataType: string;
   valueRank: number;
   arrayDimensions: string;
   description: string;
}

const emptyArg = (): ArgRow => ({
   name: '',
   dataType: BASE_DATA_TYPE,
   valueRank: -1,
   arrayDimensions: '',
   description: '',
});

/** Strip a namespace prefix ("nsu=...;Name" or "Model:Name") from a BrowseName. */
function plainBrowseName(bn?: string): string {
   if (!bn) return '';
   const semi = bn.indexOf(';');
   if (semi >= 0) return bn.substring(semi + 1);
   const colon = bn.indexOf(':');
   if (colon > 0) return bn.substring(colon + 1);
   return bn;
}

// ---- Defensive coercion for reading back stored Argument values. The value
// round-trips through ExtensionObject (de)serialization, so a field can come
// back as a primitive, a Part 6 wrapper object, or a NodeId object depending on
// how the source nodeset encoded it. Parse leniently; never throw.

function coerceNodeId(v: unknown): string {
   if (v == null) return '';
   if (typeof v === 'string') return v;
   if (typeof v === 'object') {
      const o = v as Record<string, unknown>;
      if (typeof o.Identifier === 'string') return o.Identifier;
      if (typeof o.Id === 'string') return o.Id;
      if (typeof o.IdType === 'number' && typeof o.Id !== 'undefined') return String(o.Id);
   }
   return '';
}

function coerceText(v: unknown): string {
   if (v == null) return '';
   if (typeof v === 'string') return v;
   if (typeof v === 'object') {
      const o = v as Record<string, unknown>;
      if (typeof o.Text === 'string') return o.Text;
      if (typeof o.text === 'string') return o.text;
   }
   return '';
}

function coerceDims(v: unknown): string {
   if (Array.isArray(v)) return v.join(',');
   if (typeof v === 'string') return v;
   return '';
}

function coerceRank(v: unknown): number {
   const n = Number(v);
   return Number.isFinite(n) ? n : -1;
}

/** Turn a stored Argument[] value into editable rows. */
function valueToRows(value: unknown): ArgRow[] {
   // The value is normally a JSON array of Argument objects, but a single
   // argument can round-trip as a bare object, and a multi-dim wrapper as
   // { Array: [...] }. Normalize all of those to a flat list before mapping.
   let items: unknown[];
   if (Array.isArray(value)) {
      items = value;
   } else if (value && typeof value === 'object' && Array.isArray((value as { Array?: unknown[] }).Array)) {
      items = (value as { Array: unknown[] }).Array;
   } else if (value && typeof value === 'object') {
      items = [value];
   } else {
      return [];
   }
   return items.map((item) => {
      const o = (item ?? {}) as Record<string, unknown>;
      return {
         name: coerceText(o.Name ?? o.name),
         dataType: coerceNodeId(o.DataType ?? o.dataType) || BASE_DATA_TYPE,
         valueRank: coerceRank(o.ValueRank ?? o.valueRank ?? -1),
         arrayDimensions: coerceDims(o.ArrayDimensions ?? o.arrayDimensions),
         description: coerceText(o.Description ?? o.description),
      };
   });
}

/** Build the Part 6 inline-fields ExtensionObject for one argument. */
function rowToArgument(r: ArgRow): Record<string, unknown> {
   const dims = r.arrayDimensions.trim()
      ? r.arrayDimensions.split(',')
         .map((s) => parseInt(s.trim(), 10))
         .filter((n) => Number.isFinite(n))
      : [];
   const arg: Record<string, unknown> = {
      UaTypeId: ARGUMENT_DATATYPE,
      Name: r.name.trim(),
      DataType: r.dataType || BASE_DATA_TYPE,
      ValueRank: r.valueRank,
      ArrayDimensions: dims,
   };
   if (r.description.trim()) arg.Description = { Text: r.description.trim() };
   return arg;
}

export const CreateArgumentsDialog: React.FC<CreateArgumentsDialogProps> = ({
   open,
   onClose,
   workspaceId,
   methodNodeId,
   onSaved,
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const headers = React.useMemo(
      () => ({ 'OpcUa-Server': idToUrn(workspaceId) }),
      [workspaceId],
   );

   const [inputEnabled, setInputEnabled] = React.useState(false);
   const [outputEnabled, setOutputEnabled] = React.useState(false);
   const [inputRows, setInputRows] = React.useState<ArgRow[]>([]);
   const [outputRows, setOutputRows] = React.useState<ArgRow[]>([]);
   const [isSaving, setIsSaving] = React.useState(false);
   const [saveError, setSaveError] = React.useState<string | null>(null);

   // DataType options: every concrete DataType (subtypes of BaseDataType).
   const { data: dataTypeOptions = [] } = useQuery({
      queryKey: ['allDataTypes', workspaceId],
      queryFn: async () => {
         const res = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/data-types/${slugifyNodeId(BASE_DATA_TYPE)}/subtypes`,
            { params: { depth: 10, count: 10000, includeSelf: true }, headers },
         );
         return (res.data.results ?? []).map((n): NodeIdOption => ({
            nodeId: n.nodeId,
            displayName: n.browseName ?? n.displayName?.text ?? n.nodeId,
            modelUri: n.modelUri,
         }));
      },
      enabled: open,
   });

   // The Method's direct children — used to locate existing argument properties.
   const { data: methodChildren } = useQuery({
      queryKey: ['methodArgumentChildren', workspaceId, methodNodeId],
      queryFn: async () => {
         const res = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/nodes/${slugifyNodeId(methodNodeId)}/children`,
            { params: { full: true }, headers },
         );
         return res.data.results ?? [];
      },
      enabled: open,
   });

   // The children query already carries each property's Value, so we can locate
   // the argument properties and read their current contents in one pass — no
   // separate per-property fetch (which previously left the form un-seeded).
   const inputProp = React.useMemo(
      () => methodChildren?.find((c) => plainBrowseName(c.browseName) === 'InputArguments'),
      [methodChildren],
   );
   const outputProp = React.useMemo(
      () => methodChildren?.find((c) => plainBrowseName(c.browseName) === 'OutputArguments'),
      [methodChildren],
   );

   // Seed the form once per open, as soon as the children query resolves.
   const seededRef = React.useRef(false);
   React.useEffect(() => {
      if (!open) {
         seededRef.current = false;
         return;
      }
      if (seededRef.current) return;
      if (methodChildren === undefined) return;

      // setState in effect is intentional: we seed the form from the async
      // children query once it resolves (same pattern as ValueEditor's seed).
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setInputEnabled(!!inputProp);
      setOutputEnabled(!!outputProp);
      setInputRows(inputProp ? valueToRows(inputProp.value) : []);
      setOutputRows(outputProp ? valueToRows(outputProp.value) : []);
      seededRef.current = true;
   }, [open, methodChildren, inputProp, outputProp]);

   const handleClose = () => {
      setInputEnabled(false);
      setOutputEnabled(false);
      setInputRows([]);
      setOutputRows([]);
      setSaveError(null);
      // Drop this dialog's children cache so the next open is a cold fetch.
      // Otherwise React Query serves the stale (pre-edit) children and the seed
      // effect — which fires as soon as data is defined — would re-populate from
      // them before the background refetch lands, showing an empty/old list.
      queryClient.removeQueries({ queryKey: ['methodArgumentChildren', workspaceId, methodNodeId] });
      onClose();
   };

   const methodModelUri = extractNamespaceUri(methodNodeId);

   const canSave = (inputEnabled || outputEnabled) && !isSaving;

   const upsertArguments = async (
      browseName: 'InputArguments' | 'OutputArguments',
      enabled: boolean,
      rows: ArgRow[],
      existingId?: string,
   ) => {
      if (!enabled) return;
      let propId = existingId;
      if (!propId) {
         const res = await api.post<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(methodNodeId)}/children`,
            {
               modelUri: methodModelUri,
               browseNameModelUri: CORE_NAMESPACE_URI,
               nodeClass: 'Variable',
               browseName,
               displayName: browseName,
               referenceTypeId: HAS_PROPERTY,
               typeDefinitionId: PROPERTY_TYPE,
               dataType: ARGUMENT_DATATYPE,
               valueRank: 1,
               modellingRuleId: MANDATORY_MODELLING_RULE,
            },
            { headers },
         );
         propId = res.data?.nodeId;
      }
      if (!propId) throw new Error(`Failed to create ${browseName}`);

      const value = rows
         .filter((r) => r.name.trim() !== '')
         .map(rowToArgument);
      await api.put(
         `/opcua/v1/nodes/${slugifyNodeId(propId)}`,
         { value },
         { headers },
      );
   };

   const handleSave = async () => {
      if (!canSave) return;
      setIsSaving(true);
      setSaveError(null);
      try {
         await upsertArguments('InputArguments', inputEnabled, inputRows, inputProp?.nodeId);
         await upsertArguments('OutputArguments', outputEnabled, outputRows, outputProp?.nodeId);
         queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, methodNodeId] });
         queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, methodNodeId] });
         onSaved();
         handleClose();
      } catch (e) {
         setSaveError(extractErrorMessage(e, 'Failed to save arguments'));
      } finally {
         setIsSaving(false);
      }
   };

   const renderSection = (
      label: string,
      enabled: boolean,
      setEnabled: (v: boolean) => void,
      rows: ArgRow[],
      setRows: React.Dispatch<React.SetStateAction<ArgRow[]>>,
   ) => {
      const updateRow = (idx: number, patch: Partial<ArgRow>) =>
         setRows((prev) => prev.map((r, i) => (i === idx ? { ...r, ...patch } : r)));
      const removeRow = (idx: number) =>
         setRows((prev) => prev.filter((_, i) => i !== idx));
      const addRow = () => setRows((prev) => [...prev, emptyArg()]);

      return (
         <Box
            component="fieldset"
            sx={{ border: 1, borderColor: 'divider', borderRadius: 1, px: 4, pb: 4, m: 0 }}
         >
            <Typography component="legend" sx={{ px: 2 }}>
               <FormControlLabel
                  control={
                     <Checkbox
                        checked={enabled}
                        onChange={(e) => setEnabled(e.target.checked)}
                     />
                  }
                  label={label}
               />
            </Typography>

            {enabled && (
               <Box sx={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                  {rows.length === 0 && (
                     <Typography variant="body2" color="text.secondary">
                        {t('typeDetail.noArguments', 'No arguments defined.')}
                     </Typography>
                  )}
                  {rows.map((row, idx) => (
                     <Box
                        key={idx}
                        sx={{ display: 'flex', gap: 4, alignItems: 'flex-start' }}
                     >
                        <TextField
                           label={t('typeDetail.name')}
                           value={row.name}
                           onChange={(e) => updateRow(idx, { name: e.target.value })}
                           size="small"
                           required
                           sx={{ flex: 2, minWidth: 0 }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        />
                        <Box sx={{ flex: 3, minWidth: 0 }}>
                           <NodeIdAutocomplete
                              options={dataTypeOptions}
                              value={row.dataType}
                              onChange={(id) => updateRow(idx, { dataType: id })}
                              workspaceId={workspaceId}
                              label={t('typeDetail.dataType')}
                           />
                        </Box>
                        <TextField
                           label={t('typeDetail.valueRank')}
                           value={row.valueRank}
                           onChange={(e) => updateRow(idx, { valueRank: Number(e.target.value) })}
                           select
                           size="small"
                           sx={{ flex: 2, minWidth: 0 }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        >
                           {allValueRankOptions.map((opt) => (
                              <MenuItem key={opt.value} value={opt.value}>
                                 {t(opt.labelKey)}
                              </MenuItem>
                           ))}
                        </TextField>
                        <TextField
                           label={t('typeDetail.arrayDimensions')}
                           value={row.arrayDimensions}
                           onChange={(e) => updateRow(idx, { arrayDimensions: e.target.value })}
                           size="small"
                           sx={{ flex: 2, minWidth: 0 }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        />
                        <TextField
                           label={t('typeDetail.description')}
                           value={row.description}
                           onChange={(e) => updateRow(idx, { description: e.target.value })}
                           size="small"
                           sx={{ flex: 3, minWidth: 0 }}
                           slotProps={{ inputLabel: { shrink: true } }}
                        />
                        <Tooltip title={t('typeDetail.deleteArgument', 'Remove argument')}>
                           <IconButton size="small" onClick={() => removeRow(idx)} sx={{ mt: 1 }}>
                              <DeleteIcon fontSize="small" />
                           </IconButton>
                        </Tooltip>
                     </Box>
                  ))}
                  <Box>
                     <Button size="small" startIcon={<AddIcon />} onClick={addRow}>
                        {t('typeDetail.addArgument', 'Add Argument')}
                     </Button>
                  </Box>
               </Box>
            )}
         </Box>
      );
   };

   return (
      <ModelDialog
         open={open}
         onClose={handleClose}
         title={t('typeDetail.editArgumentsTitle', 'Edit Method Arguments')}
         maxWidth="lg"
         isLoading={isSaving}
         isError={!!saveError}
         error={saveError ? new Error(saveError) : null}
         actions={saveError ? [] : [
            {
               label: isSaving ? t('common.saving', 'Saving...') : t('common.ok'),
               onClick: handleSave,
               disabled: !canSave,
            },
         ]}
      >
         {!saveError && (
            <Box sx={{ pt: 8, px: 6, pb: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
               {renderSection(
                  t('typeDetail.inputArguments', 'Input Arguments'),
                  inputEnabled, setInputEnabled, inputRows, setInputRows,
               )}
               {renderSection(
                  t('typeDetail.outputArguments', 'Output Arguments'),
                  outputEnabled, setOutputEnabled, outputRows, setOutputRows,
               )}
            </Box>
         )}
      </ModelDialog>
   );
};
