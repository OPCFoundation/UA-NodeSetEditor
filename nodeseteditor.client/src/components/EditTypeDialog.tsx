import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import api, { extractErrorMessage } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import type { Node as RestNode } from '../model/Node';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormHelperText from '@mui/material/FormHelperText';
import MenuItem from '@mui/material/MenuItem';

import { ModelDialog } from './ModelDialog';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { validateNodeIdValue, type NodeIdPrefix } from '../utils/nodeIdValidation';
import { formatWithModelPrefix } from '../utils/formatNodeId';
import { getAllowedValueRankOptions } from '../utils/valueRank';
import { getLastModelUri, setLastModelUri } from '../utils/lastModelUri';
import { parseConformanceUnits, formatConformanceUnits } from '../utils/conformanceUnits';

interface NodeAttributeDto {
   name: string;
   value?: string | null;
   nodeId?: string | null;
   nodeClass?: number;
}

interface BrowseNodeResult {
   nodeId?: string;
   browseName?: string;
   displayName?: string;
   nodeClass: number;
   modelUri?: string;
   superTypeIds?: string[];
}

interface TypeOption {
   nodeId: string;
   displayName: string;
   label: string;
   superTypeIds: string[];
}

interface PrivateModelOption {
   modelUri: string;
   label: string;
}

export interface CreatedNodeInfo {
   nodeId: string;
   displayName: string;
   modelUri: string;
   nodeClass: number;
   superTypeIds: string[];
}

export interface EditTypeDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   nodeId: string;
   attributes: NodeAttributeDto[];
   /**
    * Conformance units the type already declares, one per entry. Passed as an
    * array rather than through `attributes` because the field is a list, and
    * joining it into a display string would make the split back ambiguous.
    */
   conformanceUnits?: string[];
   onSaved: (created?: CreatedNodeInfo) => void;
   mode?: 'edit' | 'create';
   superTypeNodeId?: string;
   superTypeModelUri?: string;
   /**
    * Pre-selects the namespace dropdown when opening in create mode. Use
    * this to honour the page-level namespace context (e.g. the Type
    * Library's currently-selected model). Ignored in edit mode and when
    * the URI doesn't resolve to a private model. Falls back through
    * superTypeModelUri → first private model.
    */
   defaultModelUri?: string;
   nodeClass: number; // 8 = ObjectType, 16 = VariableType, 32 = ReferenceType, 64 = DataType
}

const UINTEGER_NODE_ID = 'i=28';
const HIERARCHICAL_REFERENCES_NODE_ID = 'i=33';

function isOrDerives(option: TypeOption, targetNodeId: string): boolean {
   if (option.nodeId === targetNodeId) return true;
   return option.superTypeIds.some(id => id === targetNodeId);
}

function stripModelPrefix(value: string): string {
   // Strip [Model]:Name or Model:Name prefix
   const bracketMatch = value.match(/^\[.*?\]:(.+)$/);
   if (bracketMatch) return bracketMatch[1];
   const colonIdx = value.indexOf(':');
   if (colonIdx > 0) return value.substring(colonIdx + 1);
   return value;
}



function getNodeClassString(nodeClass: number): string {
   switch (nodeClass) {
      case 8: return 'ObjectType';
      case 16: return 'VariableType';
      case 32: return 'ReferenceType';
      case 64: return 'DataType';
      default: return 'DataType';
   }
}

function getTitleKey(nodeClass: number, isCreate: boolean): string {
   switch (nodeClass) {
      case 8: return isCreate ? 'typeDetail.createObjectType' : 'typeDetail.editObjectType';
      case 16: return isCreate ? 'typeDetail.createVariableType' : 'typeDetail.editVariableType';
      case 32: return isCreate ? 'typeDetail.createReferenceType' : 'typeDetail.editReferenceType';
      case 64: return isCreate ? 'typeDetail.createDataType' : 'typeDetail.editDataType';
      default: return isCreate ? 'typeDetail.createDataType' : 'typeDetail.editDataType';
   }
}

interface VariableTypeInfo {
   dataType?: string;
   valueRank?: number | null;
}

export const EditTypeDialog: React.FC<EditTypeDialogProps> = ({
   open,
   onClose,
   workspaceId,
   nodeId,
   attributes,
   conformanceUnits: conformanceUnitsProp,
   onSaved,
   mode = 'edit',
   superTypeNodeId: superTypeNodeIdProp,
   superTypeModelUri,
   defaultModelUri,
   nodeClass,
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const isCreateMode = mode === 'create';
   const isDataType = nodeClass === 64;
   const isVariableType = nodeClass === 16;
   const isReferenceType = nodeClass === 32;

   const [browseName, setBrowseName] = React.useState('');
   const [displayName, setDisplayName] = React.useState('');
   const [isAbstract, setIsAbstract] = React.useState(false);
   const [selectedSuperTypeId, setSelectedSuperTypeId] = React.useState('');
   const [isOptionSet, setIsOptionSet] = React.useState(false);
   const [description, setDescription] = React.useState('');
   // Free text, one conformance unit per line.
   const [conformanceUnits, setConformanceUnits] = React.useState('');
   const [isSaving, setIsSaving] = React.useState(false);
   const [saveError, setSaveError] = React.useState<string | null>(null);
   const [initialized, setInitialized] = React.useState(false);

   // ReferenceType-specific state. A symmetric reference browses the same in
   // both directions, so it carries no InverseName (Part 3, 5.3.3).
   const [symmetric, setSymmetric] = React.useState(false);
   const [inverseName, setInverseName] = React.useState('');

   // VariableType-specific state
   const [vtDataType, setVtDataType] = React.useState('');
   const [vtValueRank, setVtValueRank] = React.useState<number>(-1);
   const [vtArrayDimensions, setVtArrayDimensions] = React.useState('');

   // Create-mode state
   const [selectedModelUri, setSelectedModelUri] = React.useState('');
   const [nodeIdPrefix, setNodeIdPrefix] = React.useState<NodeIdPrefix>('i');
   const [nodeIdValue, setNodeIdValue] = React.useState('');
   const [, setDisplayNameManuallyEdited] = React.useState(false);
   const [defaultNumericId, setDefaultNumericId] = React.useState<string>('');

   const attrMap = React.useMemo(() => {
      const map = new Map<string, string>();
      for (const a of attributes) {
         if (a.value != null) map.set(a.name, a.value);
      }
      return map;
   }, [attributes]);

   const attrNodeIdMap = React.useMemo(() => {
      const map = new Map<string, string>();
      for (const a of attributes) {
         if (a.nodeId) map.set(a.name, a.nodeId);
      }
      return map;
   }, [attributes]);

   // Fetch all types of the same nodeClass for SuperType autocomplete
   const { data: allTypes } = useQuery({
      queryKey: ['workspaceTypes', workspaceId, nodeClass],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: { nodeClass: getNodeClassString(nodeClass), start: 0, count: 10000 },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId,
            displayName: n.browseName ?? n.displayName?.text,
            nodeClass: nodeClass,
            modelUri: n.modelUri,
            superTypeIds: n.superTypeIds,
         })) as BrowseNodeResult[];
      },
      enabled: open,
   });

   // Fetch parent VariableType's effective DataType + ValueRank when SuperType is selected
   const { data: parentVtInfo } = useQuery({
      queryKey: ['variableTypeInfo', workspaceId, selectedSuperTypeId],
      queryFn: async () => {
         const response = await api.get<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(selectedSuperTypeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         const n = response.data;
         return { dataType: n.dataType, valueRank: n.valueRank } as VariableTypeInfo;
      },
      enabled: open && isVariableType && !!selectedSuperTypeId,
   });

   // Fetch allowed DataType subtypes (parent DataType + its subtypes)
   const parentDataType = parentVtInfo?.dataType;
   const { data: dataTypeSubtypes } = useQuery({
      queryKey: ['dataTypeSubtypes', workspaceId, parentDataType],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/data-types/${slugifyNodeId(parentDataType!)}/subtypes`,
            {
               params: { depth: 10, count: 10000, includeSelf: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId, displayName: n.browseName ?? n.displayName?.text, nodeClass: 64,
         })) as BrowseNodeResult[];
      },
      enabled: open && isVariableType && !!parentDataType,
   });

   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) }
         });
         return response.data;
      },
      enabled: open,
   });

   const namespaces = namespacesData?.results ?? [];

   // Build list of private models for create-mode model dropdown
   const privateModels = React.useMemo((): PrivateModelOption[] => {
      return namespaces
         .filter(ns => ns.isPrivate && ns.uri)
         .map(ns => ({
            modelUri: ns.uri,
            label: ns.name ? `${ns.name} - ${ns.uri}` : ns.uri,
         }));
   }, [namespaces]);

   // Build SuperType options: exclude current node and its subtypes
   const superTypeOptions = React.useMemo((): TypeOption[] => {
      if (!allTypes) return [];

      const modelNameMap = new Map<string, string>();
      for (const ns of namespaces) {
         if (ns.uri && ns.name) modelNameMap.set(ns.uri, ns.name);
      }

      return allTypes
         .filter(node => {
            if (!isCreateMode) {
               if (node.nodeId === nodeId) return false;
               if (node.superTypeIds?.includes(nodeId)) return false;
            }
            return true;
         })
         .map(node => ({
            nodeId: node.nodeId ?? '',
            displayName: node.displayName ?? node.browseName ?? '',
            label: formatWithModelPrefix(
               node.displayName ?? node.browseName ?? '',
               node.modelUri,
               modelNameMap,
            ),
            superTypeIds: node.superTypeIds ?? [],
         }))
         .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }));
   }, [allTypes, namespaces, nodeId, isCreateMode]);

   // Derive selected TypeOption from nodeId for superTypeIds check
   const selectedSuperType = React.useMemo(() => {
      if (!selectedSuperTypeId) return null;
      return superTypeOptions.find(o => o.nodeId === selectedSuperTypeId) ?? null;
   }, [superTypeOptions, selectedSuperTypeId]);

   // Options for SuperType NodeIdAutocomplete
   const superTypeNodeIdOptions = React.useMemo((): NodeIdOption[] => {
      if (!allTypes) return [];
      return superTypeOptions.map(opt => ({
         nodeId: opt.nodeId,
         displayName: opt.displayName,
         modelUri: allTypes.find(t => t.nodeId === opt.nodeId)?.modelUri,
      }));
   }, [superTypeOptions, allTypes]);

   // DataType options: only parent DataType and its subtypes
   const dataTypeNodeIdOptions = React.useMemo((): NodeIdOption[] => {
      if (!dataTypeSubtypes) return [];
      return dataTypeSubtypes.map(node => ({
         nodeId: node.nodeId ?? '',
         displayName: node.displayName ?? node.browseName ?? '',
         modelUri: node.modelUri,
      }));
   }, [dataTypeSubtypes]);

   // Allowed ValueRank options based on parent's ValueRank
   const allowedValueRankOptions = React.useMemo(
      () => getAllowedValueRankOptions(parentVtInfo?.valueRank),
      [parentVtInfo?.valueRank],
   );

   // NodeId is allocated server-side on create; this is for display preview only
   const { data: nextNodeIdData } = useQuery({
      queryKey: ['nextNodeId', workspaceId, selectedModelUri],
      queryFn: async () => {
         return '';
      },
      enabled: open && isCreateMode && !!selectedModelUri,
   });

   // Update default numeric ID when fetched
   React.useEffect(() => {
      if (nextNodeIdData) {
         setDefaultNumericId(nextNodeIdData);
         if (nodeIdPrefix === 'i') {
            setNodeIdValue(nextNodeIdData);
         }
      }
   }, [nextNodeIdData]); // eslint-disable-line react-hooks/exhaustive-deps

   // Invalidate cached next node ID when dialog opens in create mode
   React.useEffect(() => {
      if (open && isCreateMode) {
         queryClient.invalidateQueries({ queryKey: ['nextNodeId'] });
      }
   }, [open, isCreateMode, queryClient]);

   // Initialize state from attributes when dialog opens and data is available.
   // Create mode only needs namespace data (for private model selection); it does not
   // require superTypeOptions because the supertype is supplied directly as a prop.
   // Edit mode must wait for superTypeOptions so it can look up the supertype by label.
   const readyToInit = isCreateMode ? namespacesData != null : superTypeOptions.length > 0;
   React.useEffect(() => {
      if (open && readyToInit && !initialized) {
         if (isCreateMode) {
            // Create mode initialization
            setBrowseName('');
            setDisplayName('');
            setDescription('');
            setConformanceUnits('');
            setIsAbstract(false);
            setIsOptionSet(false);
            setSymmetric(false);
            setInverseName('');
            setVtDataType('');
            setVtValueRank(-1);
            setVtArrayDimensions('');
            setDisplayNameManuallyEdited(false);
            setNodeIdPrefix('i');
            setNodeIdValue('');

            // Default model selection precedence:
            //   1) Caller's explicit page-context model (defaultModelUri)
            //   2) Last model used in a create operation (localStorage)
            //   3) Supertype's model when private
            //   4) First private model — last-resort fallback
            // Each candidate must exist in the private-models list before
            // we accept it; otherwise the dropdown would render an empty selection.
            const lastUsed = getLastModelUri();
            if (defaultModelUri && privateModels.some(m => m.modelUri === defaultModelUri)) {
               setSelectedModelUri(defaultModelUri);
            } else if (lastUsed && privateModels.some(m => m.modelUri === lastUsed)) {
               setSelectedModelUri(lastUsed);
            } else if (superTypeModelUri && privateModels.some(m => m.modelUri === superTypeModelUri)) {
               setSelectedModelUri(superTypeModelUri);
            } else if (privateModels.length > 0) {
               setSelectedModelUri(privateModels[0].modelUri);
            }

            // Set SuperType from prop
            if (superTypeNodeIdProp) {
               setSelectedSuperTypeId(superTypeNodeIdProp);
            }
         } else {
            // Edit mode initialization
            const bn = attrMap.get('BrowseName') ?? '';
            setBrowseName(stripModelPrefix(bn));
            setDisplayName(stripModelPrefix(attrMap.get('DisplayName') ?? ''));
            setDescription(attrMap.get('Description') ?? '');
            setConformanceUnits(formatConformanceUnits(conformanceUnitsProp));
            setIsAbstract(attrMap.get('IsAbstract') === 'True');

            // The SuperType attribute carries the raw NodeId on its `nodeId` field;
            // fall back to a label match if it isn't supplied.
            const superTypeId = attrNodeIdMap.get('SuperType') ?? '';
            if (superTypeId) {
               setSelectedSuperTypeId(superTypeId);
            } else {
               const superTypeDisplay = attrMap.get('SuperType') ?? '';
               const match = superTypeOptions.find(opt => opt.label === superTypeDisplay);
               setSelectedSuperTypeId(match?.nodeId ?? '');
            }

            // ReferenceType-specific: Symmetric, InverseName. The attributes
            // carry Symmetric as the word 'True'/'False' (like IsAbstract).
            if (isReferenceType) {
               setSymmetric(attrMap.get('Symmetric') === 'True');
               setInverseName(attrMap.get('InverseName') ?? '');
            }

            // DataType-specific: IsOptionSet
            if (isDataType) {
               const dtForm = attrMap.get('DataTypeForm') ?? '';
               setIsOptionSet(dtForm === 'OptionSet');
            }

            // VariableType-specific: DataType, ValueRank, ArrayDimensions.
            // The DataType attribute carries the raw NodeId on its `nodeId` field.
            if (isVariableType) {
               setVtDataType(attrNodeIdMap.get('DataType') ?? '');
               const vr = attrMap.get('ValueRank');
               setVtValueRank(vr != null ? parseInt(vr, 10) : -1);
               setVtArrayDimensions(attrMap.get('ArrayDimensions') ?? '');
            }
         }
         setInitialized(true);
         setSaveError(null);
      }
      if (!open) {
         setInitialized(false);
      }
   }, [open, readyToInit, superTypeOptions, attrMap, attrNodeIdMap, initialized, isCreateMode, superTypeNodeIdProp, superTypeModelUri, privateModels, defaultNumericId, isDataType, isVariableType, isReferenceType, conformanceUnitsProp]);

   const showIsOptionSet = isDataType && selectedSuperType != null && isOrDerives(selectedSuperType, UINTEGER_NODE_ID);

   // A hierarchical ReferenceType is directional by definition: it is never
   // symmetric, and it must name the inverse direction. The supertype decides —
   // in create mode that is the type being extended, in edit mode the node's own.
   const isHierarchical = isReferenceType && selectedSuperType != null
      && isOrDerives(selectedSuperType, HIERARCHICAL_REFERENCES_NODE_ID);
   // Derived rather than pushed back into state: everything that reads Symmetric
   // (the checkbox, the request body) reads this, so a stale `true` behind a
   // hierarchical supertype can never leak out.
   const effectiveSymmetric = symmetric && !isHierarchical;
   const inverseNameRequired = isHierarchical && inverseName.trim() === '';

   // Reset dependent fields when SuperType changes
   const prevShowIsOptionSet = React.useRef(showIsOptionSet);
   React.useEffect(() => {
      if (prevShowIsOptionSet.current && !showIsOptionSet) setIsOptionSet(false);
      prevShowIsOptionSet.current = showIsOptionSet;
   }, [showIsOptionSet]);

   // Default VariableType DataType/ValueRank from parent when parentVtInfo loads
   const prevParentVtInfoRef = React.useRef<VariableTypeInfo | undefined>(undefined);
   React.useEffect(() => {
      if (!isVariableType || !parentVtInfo) return;
      // Only apply defaults when parentVtInfo actually changes (new supertype selected)
      if (prevParentVtInfoRef.current === parentVtInfo) return;
      prevParentVtInfoRef.current = parentVtInfo;

      if (isCreateMode) {
         // Default to parent's DataType and ValueRank
         setVtDataType(parentVtInfo.dataType ?? '');
         setVtValueRank(parentVtInfo.valueRank ?? -2);
         setVtArrayDimensions('');
      } else {
         // In edit mode, ensure current value is still valid; if not, reset to parent's
         const allowed = getAllowedValueRankOptions(parentVtInfo.valueRank);
         if (!allowed.some(o => o.value === vtValueRank)) {
            setVtValueRank(parentVtInfo.valueRank ?? -2);
         }
      }
   }, [parentVtInfo, isVariableType, isCreateMode]); // eslint-disable-line react-hooks/exhaustive-deps

   // NodeId validation for create mode
   const nodeIdError = React.useMemo(() => {
      if (!isCreateMode || !nodeIdValue) return null;
      return validateNodeIdValue(nodeIdPrefix, nodeIdValue);
   }, [isCreateMode, nodeIdPrefix, nodeIdValue]);
   void nodeIdError;

   // Compute full NodeId for create mode
   const fullNodeId = isCreateMode
      ? `nsu=${selectedModelUri};${nodeIdPrefix}=${nodeIdValue}`
      : nodeId;

   const canSave = !inverseNameRequired && (isCreateMode
      ? browseName.trim() !== '' && selectedSuperTypeId !== '' && selectedModelUri !== ''
      : browseName.trim() !== '' && selectedSuperTypeId !== '');

   // Clear error when user modifies fields
   const clearError = () => { if (saveError) setSaveError(null); };

   const handleBrowseNameChange = (value: string) => {
      setBrowseName(value);
      clearError();
   };

   const handleDisplayNameChange = (value: string) => {
      setDisplayName(value);
      clearError();
      if (isCreateMode) {
         setDisplayNameManuallyEdited(true);
      }
   };

   const handleNodeIdPrefixChange = (newPrefix: NodeIdPrefix) => {
      setNodeIdPrefix(newPrefix);
      clearError();
      // Reset value when changing prefix; restore default for 'i'
      setNodeIdValue(newPrefix === 'i' ? defaultNumericId : '');
   };
   void handleNodeIdPrefixChange;

   const handleSave = async () => {
      if (!canSave || !selectedSuperTypeId) return;
      setIsSaving(true);
      setSaveError(null);

      try {
         if (isCreateMode) {
            const body: Record<string, unknown> = {
               modelUri: selectedModelUri,
               nodeClass: getNodeClassString(nodeClass),
               browseName: browseName.trim(),
               displayName: displayName.trim(),
               description: description,
               category: parseConformanceUnits(conformanceUnits),
               isAbstract,
               referenceTypeId: 'i=45',
            };
            // DataType-specific
            if (isDataType) {
               body.isOptionSet = showIsOptionSet && isOptionSet;
            }
            // ReferenceType-specific. A symmetric type may not carry an
            // InverseName — the server rejects the pair.
            if (isReferenceType) {
               body.symmetric = effectiveSymmetric;
               body.inverseName = effectiveSymmetric ? '' : inverseName.trim();
            }
            // VariableType-specific
            if (isVariableType) {
               body.dataType = vtDataType || null;
               body.valueRank = vtValueRank;
               body.arrayDimensions = vtArrayDimensions || null;
            }

            const createResponse = await api.post(
               `/opcua/v1/nodes/${slugifyNodeId(selectedSuperTypeId)}/children`,
               body,
               { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
            );
            const createdNodeId = createResponse.data?.nodeId ?? fullNodeId;

            // Refresh any cached type lists so other dialogs (e.g. AddChild's
            // TypeDefinition dropdown) pick up the newly created type.
            queryClient.invalidateQueries({ queryKey: ['workspaceTypes'] });
            queryClient.invalidateQueries({ queryKey: ['subtypes'] });
            setLastModelUri(selectedModelUri);

            // Build superTypeIds chain: parent's chain + parent itself
            const parentSuperTypeIds = selectedSuperType?.superTypeIds
               ? [...selectedSuperType.superTypeIds, selectedSuperTypeId]
               : [selectedSuperTypeId];
            onSaved({
               nodeId: createdNodeId,
               displayName: displayName.trim(),
               modelUri: selectedModelUri,
               nodeClass,
               superTypeIds: parentSuperTypeIds,
            });
         } else {
            const body: Record<string, unknown> = {
               browseName: browseName.trim(),
               displayName: displayName.trim(),
               description: description,
               category: parseConformanceUnits(conformanceUnits),
               isAbstract,
            };
            // IsOptionSet is deliberately absent: it is fixed at creation, and the
            // server rejects a PUT that would change it.
            // ReferenceType-specific. Both go in the same call so switching
            // Symmetric on clears the InverseName in one step (the server
            // validates the resulting state, not the request in isolation).
            if (isReferenceType) {
               body.symmetric = effectiveSymmetric;
               body.inverseName = effectiveSymmetric ? '' : inverseName.trim();
            }
            // VariableType-specific
            if (isVariableType) {
               body.dataType = vtDataType || null;
               body.valueRank = vtValueRank;
               body.arrayDimensions = vtArrayDimensions || null;
            }

            await api.put(
               `/opcua/v1/nodes/${slugifyNodeId(nodeId)}`,
               body,
               { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
            );
            // BrowseName / DisplayName / DataType edits change how this type
            // shows up in cached type-list dropdowns elsewhere.
            queryClient.invalidateQueries({ queryKey: ['workspaceTypes'] });
            onSaved();
         }
         onClose();
      } catch (e) {
         setSaveError(extractErrorMessage(e, 'Failed to save'));
      } finally {
         setIsSaving(false);
      }
   };

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t(getTitleKey(nodeClass, isCreateMode))}
         isLoading={isSaving}
         isError={!!saveError}
         error={saveError ? new Error(saveError) : null}
         actions={[
            {
               label: t('common.ok'),
               onClick: handleSave,
               disabled: !canSave || isSaving,
            },
         ]}
      >
         <Box sx={{ pt: 10, px: 6, pb: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
            {isCreateMode && (
               <TextField
                  label={t('typeDetail.model')}
                  value={selectedModelUri}
                  onChange={(e) => { setSelectedModelUri(e.target.value); clearError(); }}
                  select
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true } }}
               >
                  {privateModels.map((m) => (
                     <MenuItem key={m.modelUri} value={m.modelUri}>
                        {m.label}
                     </MenuItem>
                  ))}
               </TextField>
            )}
            {!isCreateMode && (
               <TextField
                  label="NodeId"
                  value={attrMap.get('NodeId') ?? nodeId}
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
               />
            )}
            <TextField
               label="NodeClass"
               value={getNodeClassString(nodeClass)}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
            />
            <TextField
               label="BrowseName"
               value={browseName}
               onChange={(e) => handleBrowseNameChange(e.target.value)}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true } }}
            />
            <TextField
               label="DisplayName"
               value={displayName}
               onChange={(e) => handleDisplayNameChange(e.target.value)}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true } }}
            />
            <FormControlLabel
               control={
                  <Checkbox
                     checked={isAbstract}
                     onChange={(e) => { setIsAbstract(e.target.checked); clearError(); }}
                  />
               }
               label="IsAbstract"
            />
            <NodeIdAutocomplete
               options={superTypeNodeIdOptions}
               value={selectedSuperTypeId}
               onChange={(id) => { setSelectedSuperTypeId(id); clearError(); }}
               workspaceId={workspaceId}
               label="SuperType"
               readOnly={!isCreateMode || !!superTypeNodeIdProp}
            />
            {isReferenceType && (
               <>
                  <Box>
                     <FormControlLabel
                        control={
                           <Checkbox
                              checked={effectiveSymmetric}
                              disabled={isHierarchical}
                              onChange={(e) => {
                                 setSymmetric(e.target.checked);
                                 // Clear rather than just disable: a symmetric type
                                 // must not keep a stale inverse name behind the
                                 // greyed-out field.
                                 if (e.target.checked) setInverseName('');
                                 clearError();
                              }}
                           />
                        }
                        label="Symmetric"
                     />
                     {isHierarchical && (
                        <FormHelperText>
                           {t('typeDetail.symmetricHierarchical', 'A subtype of HierarchicalReferences is directional, so it is never symmetric.')}
                        </FormHelperText>
                     )}
                  </Box>
                  <TextField
                     label="InverseName"
                     value={inverseName}
                     onChange={(e) => { setInverseName(e.target.value); clearError(); }}
                     disabled={effectiveSymmetric}
                     required={isHierarchical}
                     error={inverseNameRequired}
                     helperText={
                        inverseNameRequired
                           ? t('typeDetail.inverseNameRequired', 'A subtype of HierarchicalReferences must name its inverse direction.')
                           : effectiveSymmetric
                              ? t('typeDetail.inverseNameSymmetric', 'A symmetric ReferenceType browses the same in both directions, so it has no inverse name.')
                              : t('typeDetail.inverseNameHint', 'How the reference reads when browsed backwards (e.g. "ComponentOf" for HasComponent).')}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true } }}
                  />
               </>
            )}
            {showIsOptionSet && (
               <Box>
                  <FormControlLabel
                     control={
                        <Checkbox
                           checked={isOptionSet}
                           disabled={!isCreateMode}
                           onChange={(e) => { setIsOptionSet(e.target.checked); clearError(); }}
                        />
                     }
                     label="IsOptionSet"
                  />
                  <FormHelperText>
                     {isCreateMode
                        ? t('typeDetail.isOptionSetHint', 'The fields of an OptionSet are bit positions in the underlying unsigned integer rather than values.')
                        : t('typeDetail.isOptionSetCreateOnly', 'Whether a DataType is an OptionSet is fixed when it is created.')}
                  </FormHelperText>
               </Box>
            )}
            {isVariableType && (
               <>
                  <NodeIdAutocomplete
                     options={dataTypeNodeIdOptions}
                     value={vtDataType}
                     onChange={(id) => { setVtDataType(id); clearError(); }}
                     workspaceId={workspaceId}
                     label={t('typeDetail.dataType')}
                  />
                  <TextField
                     label={t('typeDetail.valueRank')}
                     value={vtValueRank}
                     onChange={(e) => { setVtValueRank(Number(e.target.value)); clearError(); }}
                     select
                     fullWidth
                     size="small"
                     disabled={allowedValueRankOptions.length <= 1}
                     slotProps={{ inputLabel: { shrink: true } }}
                  >
                     {allowedValueRankOptions.map((opt) => (
                        <MenuItem key={opt.value} value={opt.value}>
                           {t(opt.labelKey)}
                        </MenuItem>
                     ))}
                  </TextField>
                  <TextField
                     label={t('typeDetail.arrayDimensions')}
                     value={vtArrayDimensions}
                     onChange={(e) => { setVtArrayDimensions(e.target.value); clearError(); }}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true } }}
                     placeholder={t('typeDetail.fieldArrayDimensionsPlaceholder')}
                  />
               </>
            )}
            <TextField
               label={t('typeDetail.description')}
               value={description}
               onChange={(e) => { setDescription(e.target.value); clearError(); }}
               fullWidth
               size="small"
               multiline
               minRows={3}
               slotProps={{ inputLabel: { shrink: true } }}
            />
            <TextField
               label={t('typeDetail.conformanceUnits', 'Conformance Units')}
               value={conformanceUnits}
               onChange={(e) => { setConformanceUnits(e.target.value); clearError(); }}
               helperText={t('typeDetail.conformanceUnitsHint',
                  'One conformance unit per line. Written to the NodeSet as Category elements.')}
               fullWidth
               size="small"
               multiline
               minRows={3}
               slotProps={{ inputLabel: { shrink: true } }}
            />
         </Box>
      </ModelDialog>
   );
};
