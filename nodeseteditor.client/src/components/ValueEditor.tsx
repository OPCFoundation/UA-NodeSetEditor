import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import api, { ApiError } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';

import Box from '@mui/material/Box';
import Stack from '@mui/material/Stack';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Alert from '@mui/material/Alert';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Tooltip from '@mui/material/Tooltip';
import SchemaIcon from '@mui/icons-material/Schema';
import AutoFixHighIcon from '@mui/icons-material/AutoFixHigh';
import useMediaQuery from '@mui/material/useMediaQuery';
import { useTheme } from '@mui/material/styles';

import { JsonValueEditor } from './JsonValueEditor';
import { ContentLoader } from './ContentLoader';
import { ViewNodeDialog } from './ViewNodeDialog';

interface ValueEditorProps {
   workspaceId: string;
   nodeId: string;
   /** Variable's DataType NodeId, e.g. "i=12" or "nsu=...;i=42". */
   dataType?: string;
   /** Declared ValueRank. May be abstract (-2 Any, -3 ScalarOrOneDim, 0 OneOrMoreDim). */
   valueRank?: number;
   /** Current value (server's canonical shape). */
   value: unknown;
   isEditable: boolean;
}

/** Concrete shape used by the editor. We don't support multi-dim editing yet. */
type Shape = 'scalar' | 'array1d';

const ValueRank = {
   Any: -2,
   ScalarOrOneDimension: -3,
   Scalar: -1,
   OneOrMoreDimensions: 0,
} as const;

function isAbstractRank(rank: number | undefined): boolean {
   return rank === ValueRank.Any
      || rank === ValueRank.ScalarOrOneDimension
      || rank === ValueRank.OneOrMoreDimensions;
}

/** Map declared rank → allowed concrete shapes for the selector. */
function allowedShapes(rank: number | undefined): Shape[] {
   if (rank === undefined || rank === null) return ['scalar'];
   if (rank === ValueRank.Any) return ['scalar', 'array1d'];
   if (rank === ValueRank.ScalarOrOneDimension) return ['scalar', 'array1d'];
   if (rank === ValueRank.OneOrMoreDimensions) return ['array1d'];
   if (rank === ValueRank.Scalar) return ['scalar'];
   if (rank === 1) return ['array1d'];
   // ≥ 2: not supported in v1
   return [];
}

/** Infer a concrete shape from the current value when the declared rank is abstract. */
function inferShape(value: unknown, allowed: Shape[]): Shape {
   if (allowed.length === 0) return 'scalar';
   if (Array.isArray(value) && allowed.includes('array1d')) return 'array1d';
   if (allowed.includes('scalar')) return 'scalar';
   return allowed[0];
}

/**
 * Materialize a default value matching the schema. For Structures this seeds
 * every (required, then all if no required) field so the editor surfaces the
 * valid shape rather than a bare `null`. For Unions (oneOf) we pick the first
 * branch as a starting point. `$ref` is resolved against the root's `$defs`.
 */
function defaultFromSchema(
   schema: Record<string, unknown> | undefined,
   root?: Record<string, unknown>,
   depth: number = 0,
): unknown {
   if (!schema || depth > 12) return null;

   // $ref resolution against #/$defs/<key>.
   const ref = schema['$ref'];
   if (typeof ref === 'string') {
      const prefix = '#/$defs/';
      if (ref.startsWith(prefix)) {
         const defs = (root?.['$defs'] ?? schema['$defs']) as Record<string, unknown> | undefined;
         const key = ref.substring(prefix.length);
         const target = defs?.[key] as Record<string, unknown> | undefined;
         if (target) return defaultFromSchema(target, root ?? schema, depth + 1);
      }
      return null;
   }

   // Explicit default in the schema wins.
   if (Object.prototype.hasOwnProperty.call(schema, 'default')) {
      return schema['default'];
   }

   const enumVals = (schema as { enum?: unknown[] }).enum;
   if (Array.isArray(enumVals) && enumVals.length > 0) return enumVals[0];

   const oneOf = (schema as { oneOf?: Array<Record<string, unknown>> }).oneOf;
   if (Array.isArray(oneOf) && oneOf.length > 0) {
      return defaultFromSchema(oneOf[0], root ?? schema, depth + 1);
   }

   const t = schema['type'];
   if (t === 'string') return '';
   if (t === 'integer' || t === 'number') return 0;
   if (t === 'boolean') return false;

   if (t === 'array') {
      const items = schema['items'] as Record<string, unknown> | undefined;
      const minItems = (schema['minItems'] as number | undefined) ?? 0;
      const out: unknown[] = [];
      for (let i = 0; i < minItems; i++) {
         out.push(defaultFromSchema(items, root ?? schema, depth + 1));
      }
      return out;
   }

   if (t === 'object') {
      const properties = (schema['properties'] as Record<string, Record<string, unknown>> | undefined) ?? {};
      const required = (schema['required'] as string[] | undefined) ?? [];
      const fieldNames = required.length > 0 ? required : Object.keys(properties);
      const obj: Record<string, unknown> = {};
      for (const name of fieldNames) {
         obj[name] = defaultFromSchema(properties[name], root ?? schema, depth + 1);
      }
      return obj;
   }

   return null;
}

/**
 * True for schemas where a `null` value is uninformative — i.e. the user can't
 * see the structure/options without seeding. Primitives are fine as null.
 */
function schemaWantsTemplate(schema: Record<string, unknown> | undefined): boolean {
   if (!schema) return false;
   const t = schema['type'];
   if (t === 'object' || t === 'array') return true;
   const oneOf = (schema as { oneOf?: unknown[] }).oneOf;
   if (Array.isArray(oneOf) && oneOf.length > 0) return true;
   const ref = schema['$ref'];
   if (typeof ref === 'string') return true;
   return false;
}

/**
 * Wraps an element schema as `{ type: "array", items: element }` so AJV
 * validates the array the editor actually displays in single-view mode.
 * Meta keywords (`$schema`, `$defs`) lift to the wrapper root: $defs so
 * `#/$defs/...` references inside items still resolve from the root being
 * validated, $schema so AJV strict mode doesn't reject the meta-schema
 * declaration appearing inside a sub-schema.
 */
function arraySchemaForElement(
   schema: Record<string, unknown> | undefined,
): Record<string, unknown> | undefined {
   if (!schema) return undefined;
   const { $defs, $schema, ...itemsSchema } = schema as {
      $defs?: unknown;
      $schema?: unknown;
   } & Record<string, unknown>;
   return {
      ...($schema ? { $schema } : {}),
      ...($defs ? { $defs } : {}),
      type: 'array',
      items: itemsSchema,
   };
}

function isEmptyValue(v: unknown): boolean {
   if (v === null || v === undefined) return true;
   if (typeof v === 'object' && !Array.isArray(v)) {
      return Object.keys(v as object).length === 0;
   }
   return false;
}

export const ValueEditor: React.FC<ValueEditorProps> = ({
   workspaceId,
   nodeId,
   dataType,
   valueRank,
   value,
   isEditable,
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const muiTheme = useTheme();
   // Only allow the schema side-pane on screens with room for two editors. On
   // narrower viewports the toggle disappears (and any active schema view is
   // implicitly hidden) so the value tree keeps its full width.
   const canShowSchemaPane = useMediaQuery(muiTheme.breakpoints.up('md'));

   const allowed = React.useMemo(() => allowedShapes(valueRank), [valueRank]);

   // Fetch schema from the server for the variable's DataType.
   const {
      data: schema,
      isLoading: schemaLoading,
      isError: schemaError,
      error: schemaErrorObj,
   } = useQuery({
      queryKey: ['dataTypeSchema', workspaceId, dataType],
      queryFn: async () => {
         if (!dataType) return undefined;
         const response = await api.get<Record<string, unknown>>(
            `/opcua/v1/types/data-types/${slugifyNodeId(dataType)}/json-schema`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         return response.data;
      },
      enabled: !!dataType,
      staleTime: 5 * 60 * 1000,
   });

   // ---- Local state ----
   const [shape, setShape] = React.useState<Shape>(() => inferShape(value, allowed));
   const [scalarValue, setScalarValue] = React.useState<unknown>(
      Array.isArray(value) ? (value[0] ?? null) : (value ?? null),
   );
   const [arrayValue, setArrayValue] = React.useState<unknown[]>(
      Array.isArray(value) ? [...value] : (value !== null && value !== undefined ? [value] : []),
   );
   const [saving, setSaving] = React.useState(false);
   const [saveError, setSaveError] = React.useState<string | null>(null);
   const [dirty, setDirty] = React.useState(false);
   // Toggle for the read-only schema side-pane. The schema view exists so the
   // user can see field titles/descriptions (the JSON-Schema annotations) that
   // the value editor's tree only surfaces on hover or via validation messages.
   const [showSchema, setShowSchema] = React.useState(false);
   // Active "view node" dialog target. Set by clicks on NodeId-typed fields
   // inside the value tree; the dialog auto-dismisses if the NodeId 404s.
   const [viewNodeId, setViewNodeId] = React.useState<string | null>(null);

   // Auto-seed once when the schema describes a non-trivial shape and the
   // current value is empty. This surfaces the valid field set so the user
   // can see what to fill in instead of staring at "null"/"[]". setState is
   // intentional here — we react to async schema arrival.
   const seededRef = React.useRef(false);
   React.useEffect(() => {
      if (seededRef.current) return;
      if (!schema || !schemaWantsTemplate(schema)) return;

      if (shape === 'scalar' && isEmptyValue(scalarValue)) {
         // eslint-disable-next-line react-hooks/set-state-in-effect
         setScalarValue(defaultFromSchema(schema, schema));
         setDirty(true);
         seededRef.current = true;
      } else if (shape === 'array1d' && arrayValue.length === 0) {
         setArrayValue([defaultFromSchema(schema, schema)]);
         setDirty(true);
         seededRef.current = true;
      }
      // eslint-disable-next-line react-hooks/exhaustive-deps
   }, [schema]);

   const handleApplyTemplate = () => {
      if (!schema) return;
      const seed = defaultFromSchema(schema, schema);
      if (shape === 'scalar') {
         setScalarValue(seed);
      } else {
         setArrayValue([seed]);
      }
      setDirty(true);
   };

   const handleScalarChange = (next: unknown) => {
      setDirty(true);
      setScalarValue(next);
   };

   // Array editor returns the whole array. Empty/null collapses to []; a
   // non-array result (e.g. the text mode firing onChange while the user
   // is still typing and the buffer parses as something other than an
   // array) is ignored so we don't blow away the user's in-progress edit.
   const handleArrayChange = (next: unknown) => {
      if (next === null || next === undefined) {
         setArrayValue([]);
         setDirty(true);
         return;
      }
      if (Array.isArray(next)) {
         setArrayValue(next);
         setDirty(true);
      }
   };

   const handleShapeChange = (next: Shape) => {
      if (next === shape) return;
      setDirty(true);
      if (next === 'array1d') {
         // Scalar -> 1-element array (or empty when scalar is null/undefined).
         const seed = scalarValue === null || scalarValue === undefined ? [] : [scalarValue];
         setArrayValue(seed);
      } else {
         // Array -> scalar: keep element 0, drop the rest.
         setScalarValue(arrayValue[0] ?? null);
      }
      setShape(next);
   };

   const handleReset = () => {
      const nextShape = inferShape(value, allowed);
      setShape(nextShape);
      if (Array.isArray(value)) {
         setArrayValue([...value]);
         setScalarValue(value[0] ?? null);
      } else {
         setArrayValue(value !== null && value !== undefined ? [value] : []);
         setScalarValue(value ?? null);
      }
      setDirty(false);
      setSaveError(null);
   };

   const handleSave = async () => {
      setSaving(true);
      setSaveError(null);
      try {
         const body = {
            value: shape === 'scalar' ? scalarValue : arrayValue,
         };
         await api.put(
            `/opcua/v1/nodes/${slugifyNodeId(nodeId)}`,
            body,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         setDirty(false);
         // Refresh attribute view (which carries the value) and any related caches.
         queryClient.invalidateQueries({ queryKey: ['nodeAttributes', workspaceId, nodeId] });
      } catch (e) {
         const msg = e instanceof ApiError ? e.message
            : e instanceof Error ? e.message
            : t('valueEditor.saveFailed', 'Failed to save value');
         setSaveError(msg);
      } finally {
         setSaving(false);
      }
   };

   const showShapeSelector = isAbstractRank(valueRank) || allowed.length > 1;
   const isMultiDimUnsupported = (valueRank ?? -1) >= 2;
   const noDataType = !dataType;

   if (noDataType) {
      return (
         <Box sx={{ p: 3 }}>
            <Alert severity="info">
               {t('valueEditor.noDataType', 'No DataType is set for this Variable.')}
            </Alert>
         </Box>
      );
   }

   if (isMultiDimUnsupported) {
      return (
         <Box sx={{ p: 3 }}>
            <Alert severity="warning">
               {t(
                  'valueEditor.multiDimNotSupported',
                  `Multi-dimensional values (ValueRank=${valueRank}) are not yet editable here. Use the API directly.`,
               )}
            </Alert>
         </Box>
      );
   }

   const showApplyTemplate = isEditable && schemaWantsTemplate(schema);

   return (
      <ContentLoader isLoading={schemaLoading} isError={schemaError} error={schemaErrorObj}>
         <Box sx={{
            p: 2,
            display: 'flex',
            flexDirection: 'column',
            gap: 2,
            flex: 1,
            minHeight: 0,
         }}>
            {/* Toolbar: Shape selector | spacer | edit actions. Shape toggle is
                disabled in read-only mode (changing the shape would imply
                changing the value). Arrays render as a single editor — use
                the editor's tree/table/text mode-switcher for navigation and
                row insert/delete. */}
            {(showShapeSelector || showApplyTemplate || (canShowSchemaPane && schema)) && (
               <Stack direction="row" spacing={2} alignItems="center">
                  {showShapeSelector && (
                     <ToggleButtonGroup
                        size="small"
                        exclusive
                        value={shape}
                        disabled={!isEditable}
                        onChange={(_, v) => v && handleShapeChange(v as Shape)}
                     >
                        {allowed.includes('scalar') && (
                           <ToggleButton value="scalar">
                              {t('valueEditor.scalar', 'Scalar')}
                           </ToggleButton>
                        )}
                        {allowed.includes('array1d') && (
                           <ToggleButton value="array1d">
                              {t('valueEditor.oneDimension', '1-D Array')}
                           </ToggleButton>
                        )}
                     </ToggleButtonGroup>
                  )}

                  <Box sx={{ flexGrow: 1 }} />

                  {showApplyTemplate && (
                     <Tooltip title={t(
                        'valueEditor.applyTemplateTooltip',
                        'Replace the current element with a default shape from the schema (all valid fields populated).',
                     )}>
                        <IconButton size="small" onClick={handleApplyTemplate}>
                           <AutoFixHighIcon fontSize="small" />
                        </IconButton>
                     </Tooltip>
                  )}

                  {canShowSchemaPane && schema && (
                     <Tooltip title={t(
                        'valueEditor.showSchemaTooltip',
                        'Show the JSON Schema for this DataType (titles, descriptions, types) next to the editor.',
                     )}>
                        <ToggleButton
                           value="show-schema"
                           size="small"
                           selected={showSchema}
                           onChange={() => setShowSchema(s => !s)}
                        >
                           <SchemaIcon fontSize="small" />
                        </ToggleButton>
                     </Tooltip>
                  )}
               </Stack>
            )}

            {/* Editor area. When the schema pane is on (and the viewport is wide
                enough), this becomes a two-column flex with the value editor on
                the left and a read-only schema viewer on the right. Each column
                is `flex: 1 1 0` so they share width 50/50; `minWidth: 0` lets
                the inner editors actually shrink instead of overflowing. */}
            <Box sx={{
               flex: 1,
               minHeight: 0,
               display: 'flex',
               flexDirection: 'row',
               gap: 2,
            }}>
               <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                  {shape === 'array1d' ? (
                     <JsonValueEditor
                        // Element schema → array schema for the validator.
                        // The variable's schema describes one element; passing
                        // it as-is makes AJV reject arrays of simple types
                        // ([1,2,3] vs. {type:"integer"}). Wrapping yields a
                        // schema that matches the value the editor actually
                        // sees in single-view array mode.
                        schema={arraySchemaForElement(schema)}
                        value={arrayValue}
                        onChange={handleArrayChange}
                        readOnly={!isEditable || saving}
                        height="100%"
                        onNodeIdClick={setViewNodeId}
                        allowTableMode
                     />
                  ) : (
                     <JsonValueEditor
                        schema={schema}
                        value={scalarValue}
                        onChange={handleScalarChange}
                        readOnly={!isEditable || saving}
                        height="100%"
                        onNodeIdClick={setViewNodeId}
                     />
                  )}
               </Box>

               {canShowSchemaPane && showSchema && schema && (
                  <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                     {/* Read-only schema viewer. We deliberately pass no
                         `schema` prop here — validating the schema against
                         itself is meaningless and would just paint red marks
                         everywhere. The empty `onChange` is required by the
                         component contract but never fires (readOnly). */}
                     <JsonValueEditor
                        value={schema}
                        onChange={() => { /* read-only */ }}
                        readOnly
                        height="100%"
                     />
                  </Box>
               )}
            </Box>

            {saveError && (
               <Alert severity="error" onClose={() => setSaveError(null)}>
                  {saveError}
               </Alert>
            )}

            {isEditable && (
               <Stack direction="row" spacing={1} justifyContent="flex-end">
                  <Button
                     variant="outlined"
                     onClick={handleReset}
                     disabled={!dirty || saving}
                  >
                     {t('common.reset', 'Reset')}
                  </Button>
                  <Button
                     variant="contained"
                     onClick={handleSave}
                     disabled={!dirty || saving}
                  >
                     {t('common.save', 'Save')}
                  </Button>
               </Stack>
            )}
         </Box>
         {viewNodeId && (
            <ViewNodeDialog
               workspaceId={workspaceId}
               nodeId={viewNodeId}
               onClose={() => setViewNodeId(null)}
            />
         )}
      </ContentLoader>
   );
};
