import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import type { Node as RestNode } from '../model/Node';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';

import { ModelDialog } from './ModelDialog';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { extractNamespaceUri } from '../utils/formatNodeId';
import { getAllowedValueRankOptions } from '../utils/valueRank';

interface BrowseNodeResult {
   nodeId?: string;
   browseName?: string;
   displayName?: string;
   nodeClass: number;
   modelUri?: string;
}

interface VariableTypeInfo {
   dataType?: string;
   valueRank?: number | null;
}

export interface CreateChildData {
   modelUri: string;
   browseNameModelUri?: string;
   referenceTypeId: string;
   nodeClass: number;
   browseName: string;
   displayName: string;
   typeDefinitionId?: string;
   modellingRuleId?: string;
   description: string;
   dataType?: string;
   valueRank?: number;
   arrayDimensions?: string;
}

/**
 * Pre-configured child-creation variants. When set, the dialog locks the
 * NodeClass and reference type and restricts the TypeDefinition picker to the
 * appropriate type subtree, so e.g. "Create Property" can only ever produce a
 * HasProperty/PropertyType Variable.
 */
export type ChildKind = 'object' | 'datavariable' | 'property' | 'method';

interface ChildKindConfig {
   nodeClass: number;
   referenceTypeId: string;
   typeDefRootId: string | null;
   typeDefCategory: 'object-types' | 'variable-types' | null;
   lockTypeDef: boolean;
   titleKey: string;
}

const CHILD_KIND_CONFIG: Record<ChildKind, ChildKindConfig> = {
   object: { nodeClass: 1, referenceTypeId: 'i=47', typeDefRootId: 'i=58', typeDefCategory: 'object-types', lockTypeDef: false, titleKey: 'typeDetail.createObject' },
   datavariable: { nodeClass: 2, referenceTypeId: 'i=47', typeDefRootId: 'i=63', typeDefCategory: 'variable-types', lockTypeDef: false, titleKey: 'typeDetail.createDataVariable' },
   property: { nodeClass: 2, referenceTypeId: 'i=46', typeDefRootId: 'i=68', typeDefCategory: 'variable-types', lockTypeDef: true, titleKey: 'typeDetail.createProperty' },
   method: { nodeClass: 4, referenceTypeId: 'i=47', typeDefRootId: null, typeDefCategory: null, lockTypeDef: false, titleKey: 'typeDetail.createMethod' },
};

interface AddChildDialogProps {
   open: boolean;
   onClose: () => void;
   onSave: (data: CreateChildData) => void;
   isSaving: boolean;
   saveError: string | null;
   workspaceId: string;
   parentNodeId: string;
   parentNodeClass: number;
   showModellingRule: boolean;
   /** When set, locks NodeClass/reference type and restricts the TypeDefinition. */
   childKind?: ChildKind;
}

const NodeClassValues = {
   UAObject: 1,
   UAVariable: 2,
   UAMethod: 4,
   UAObjectType: 8,
   UAVariableType: 16,
} as const;

const modellingRuleOptions = [
   { value: '', labelKey: 'typeDetail.none' },
   { value: 'i=78', labelKey: 'typeDetail.mandatory' },
   { value: 'i=80', labelKey: 'typeDetail.optionalRule' },
   { value: 'i=11508', labelKey: 'typeDetail.optionalPlaceholder' },
   { value: 'i=11510', labelKey: 'typeDetail.mandatoryPlaceholder' },
];

export const AddChildDialog: React.FC<AddChildDialogProps> = ({
   open,
   onClose,
   onSave,
   isSaving,
   saveError,
   workspaceId,
   parentNodeId: _parentNodeId,
   parentNodeClass,
   showModellingRule,
   childKind,
}) => {
   const { t } = useTranslation();

   const kindConfig = childKind ? CHILD_KIND_CONFIG[childKind] : null;

   // Child nodes must be in the same model as the parent
   const parentModelUri = extractNamespaceUri(_parentNodeId);
   // Variables and VariableTypes can only contain Variable children.
   const parentRequiresVariableChild =
      parentNodeClass === NodeClassValues.UAVariable
      || parentNodeClass === NodeClassValues.UAVariableType;
   const [selectedModelUri, setSelectedModelUri] = React.useState(parentModelUri);
   const [browseNameNs, setBrowseNameNs] = React.useState(parentModelUri);
   const [browseName, setBrowseName] = React.useState('');
   const [displayName, setDisplayName] = React.useState('');
   const [referenceTypeId, setReferenceTypeId] = React.useState('i=47'); // HasComponent default
   const [nodeClassValue, setNodeClassValue] = React.useState<number>(NodeClassValues.UAObject);
   const [typeDefinitionId, setTypeDefinitionId] = React.useState('i=58'); // BaseObjectType default
   const [modellingRuleId, setModellingRuleId] = React.useState('i=80'); // Optional default
   const [dataType, setDataType] = React.useState('i=24'); // BaseDataType default
   const [valueRank, setValueRank] = React.useState(-2); // Any default; refined when varTypeInfo loads
   const [arrayDimensions, setArrayDimensions] = React.useState('');
   const [description, setDescription] = React.useState('');
   const [, setDisplayNameManuallyEdited] = React.useState(false);

   // Fetch hierarchical reference types
   const { data: refTypes } = useQuery({
      queryKey: ['hierarchicalRefTypes', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/reference-types/${slugifyNodeId('i=33')}/subtypes`,
            {
               params: { depth: 10, count: 10000 },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId,
            displayName: n.browseName ?? n.displayName?.text,
            nodeClass: 32,
         })) as BrowseNodeResult[];
      },
      enabled: open,
   });

   const refTypeOptions = React.useMemo((): NodeIdOption[] => {
      return (refTypes ?? []).map((rt): NodeIdOption => ({
         nodeId: rt.nodeId ?? '',
         displayName: rt.displayName ?? rt.nodeId ?? '',
         modelUri: rt.modelUri,
      }));
   }, [refTypes]);

   // Determine which TypeDefinition nodeClass to filter by
   const typeDefNodeClass = nodeClassValue === NodeClassValues.UAObject
      ? NodeClassValues.UAObjectType
      : NodeClassValues.UAVariableType;

   const isVariable = nodeClassValue === NodeClassValues.UAVariable;
   const showTypeDefinition = nodeClassValue === NodeClassValues.UAObject || isVariable;
   // The reference type connecting the new child to its parent. Shown for Object
   // and Variable children (a Property is a Variable); only Methods keep it
   // implied. The picker is restricted to subtypes of HierarchicalReferences.
   const showReferenceType = nodeClassValue === NodeClassValues.UAObject || isVariable;

   // Fetch type definitions (ObjectTypes or VariableTypes). When a childKind
   // restricts the TypeDefinition, query the subtypes of the kind's root
   // (e.g. PropertyType, BaseDataVariableType) instead of every workspace type.
   const restrictTypeDef = !!kindConfig?.typeDefRootId;
   const { data: typeDefTypes } = useQuery({
      queryKey: restrictTypeDef
         ? ['childTypeDefSubtypes', workspaceId, kindConfig!.typeDefCategory, kindConfig!.typeDefRootId]
         : ['workspaceTypes', workspaceId, typeDefNodeClass],
      queryFn: async () => {
         if (restrictTypeDef) {
            const response = await api.get<PaginatedResponse<RestNode>>(
               `/opcua/v1/types/${kindConfig!.typeDefCategory}/${slugifyNodeId(kindConfig!.typeDefRootId!)}/subtypes`,
               {
                  params: { depth: 10, count: 10000, includeSelf: true },
                  headers: { 'OpcUa-Server': idToUrn(workspaceId) },
               });
            return (response.data.results ?? []).map(n => ({
               nodeId: n.nodeId, displayName: n.browseName ?? n.displayName?.text, nodeClass: typeDefNodeClass,
            })) as BrowseNodeResult[];
         }
         const ncLabel: Record<number, string> = { 8: 'ObjectType', 16: 'VariableType' };
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: { nodeClass: ncLabel[typeDefNodeClass], start: 0, count: 10000 },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId, displayName: n.browseName ?? n.displayName?.text, nodeClass: typeDefNodeClass,
         })) as BrowseNodeResult[];
      },
      enabled: open && showTypeDefinition,
   });

   const typeDefOptions = React.useMemo((): NodeIdOption[] => {
      return (typeDefTypes ?? []).map((node): NodeIdOption => ({
         nodeId: node.nodeId ?? '',
         displayName: node.displayName ?? node.browseName ?? '',
         modelUri: node.modelUri,
      }));
   }, [typeDefTypes]);

   // Fetch VariableType info (DataType + ValueRank) when TypeDefinition is selected
   const { data: varTypeInfo } = useQuery({
      queryKey: ['variableTypeInfo', workspaceId, typeDefinitionId],
      queryFn: async () => {
         const response = await api.get<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(typeDefinitionId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         const n = response.data;
         return { dataType: n.dataType, valueRank: n.valueRank } as VariableTypeInfo;
      },
      enabled: open && isVariable && !!typeDefinitionId,
   });

   // Whenever the selected TypeDefinition's varTypeInfo arrives or changes, snap
   // DataType/ValueRank/ArrayDimensions to the TypeDef's defaults. The user's
   // own edits to those fields don't change varTypeInfo, so they are preserved
   // until the user picks a different TypeDef (which causes varTypeInfo to
   // change).
   React.useEffect(() => {
      if (!isVariable || !varTypeInfo) return;
      if (varTypeInfo.dataType) setDataType(varTypeInfo.dataType);
      setValueRank(varTypeInfo.valueRank ?? -2);
      setArrayDimensions('');
   }, [varTypeInfo, isVariable]);

   // Fetch allowed DataType subtypes based on the VariableType's DataType
   const typeDefDataType = varTypeInfo?.dataType;
   const { data: dataTypeSubtypes } = useQuery({
      queryKey: ['dataTypeSubtypes', workspaceId, typeDefDataType],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/data-types/${slugifyNodeId(typeDefDataType!)}/subtypes`,
            {
               params: { depth: 10, count: 10000, includeSelf: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId, displayName: n.browseName ?? n.displayName?.text, nodeClass: 64,
         })) as BrowseNodeResult[];
      },
      enabled: open && isVariable && !!typeDefDataType,
   });

   const dataTypeOptions = React.useMemo((): NodeIdOption[] => {
      return (dataTypeSubtypes ?? []).map((dt): NodeIdOption => ({
         nodeId: dt.nodeId ?? '',
         displayName: dt.displayName ?? dt.nodeId ?? '',
         modelUri: dt.modelUri,
      }));
   }, [dataTypeSubtypes]);

   // Compute allowed ValueRank options based on TypeDef's ValueRank
   const allowedValueRankOptions = React.useMemo(
      () => getAllowedValueRankOptions(varTypeInfo?.valueRank),
      [varTypeInfo?.valueRank],
   );

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

   const parentModelLabel = React.useMemo(() => {
      const ns = namespaces.find(n => n.uri === parentModelUri);
      return ns?.name ? `${ns.name} (${parentModelUri})` : parentModelUri;
   }, [namespaces, parentModelUri]);

   const browseNameNsOptions = React.useMemo(() => {
      return namespaces
         .filter(ns => !!ns.uri)
         .map(ns => ({ uri: ns.uri, label: ns.name || ns.uri }));
   }, [namespaces]);

   // Initialize when dialog opens
   React.useEffect(() => {
      if (open) {
         setBrowseNameNs(parentModelUri);
         setBrowseName('');
         setDisplayName('');
         setDescription('');
         setModellingRuleId('i=80'); // Optional
         setArrayDimensions('');
         setDisplayNameManuallyEdited(false);

         if (kindConfig) {
            // childKind locks NodeClass, reference type and the TypeDefinition root.
            setNodeClassValue(kindConfig.nodeClass);
            setReferenceTypeId(kindConfig.referenceTypeId);
            setTypeDefinitionId(kindConfig.typeDefRootId ?? '');
         } else if (parentRequiresVariableChild) {
            // Variable / VariableType parents can only have Variable children.
            // DataType/ValueRank are populated by the varTypeInfo sync effect once
            // the TypeDef loads.
            setReferenceTypeId('i=47');
            setNodeClassValue(NodeClassValues.UAVariable);
            setTypeDefinitionId('i=63'); // BaseDataVariableType
         } else {
            setReferenceTypeId('i=47');
            setNodeClassValue(NodeClassValues.UAObject);
            setTypeDefinitionId('i=58'); // BaseObjectType
         }

         // Force model to parent's namespace
         setSelectedModelUri(parentModelUri);
      }
   }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

   const canSave = browseName.trim() !== ''
      && selectedModelUri !== ''
      && referenceTypeId !== ''
      && (!showTypeDefinition || typeDefinitionId !== '');

   const handleBrowseNameChange = (value: string) => {
      setBrowseName(value);
   };

   const handleDisplayNameChange = (value: string) => {
      setDisplayName(value);
      setDisplayNameManuallyEdited(true);
   };

   const handleTypeDefinitionChange = (id: string) => {
      setTypeDefinitionId(id);
      // DataType/ValueRank/ArrayDimensions get reset by the varTypeInfo sync
      // effect once the new TypeDef's data arrives.
   };

   const handleNodeClassChange = (nc: number) => {
      setNodeClassValue(nc);
      setArrayDimensions('');
      setTypeDefinitionId(nc === NodeClassValues.UAVariable
         ? 'i=63'  // BaseDataVariableType
         : 'i=58'); // BaseObjectType
   };

   const handleSave = () => {
      if (!canSave) return;
      onSave({
         modelUri: selectedModelUri,
         browseNameModelUri: browseNameNs !== parentModelUri ? browseNameNs : undefined,
         referenceTypeId,
         nodeClass: nodeClassValue,
         browseName: browseName.trim(),
         displayName: displayName.trim(),
         typeDefinitionId: showTypeDefinition ? typeDefinitionId : undefined,
         modellingRuleId: showModellingRule && modellingRuleId ? modellingRuleId : undefined,
         dataType: isVariable ? dataType : undefined,
         valueRank: isVariable ? valueRank : undefined,
         arrayDimensions: isVariable ? arrayDimensions : undefined,
         description: description,
      });
   };

   // Available node class options
   const nodeClassOptions = parentRequiresVariableChild
      ? [{ value: NodeClassValues.UAVariable, labelKey: 'typeDetail.variable' }]
      : [
         { value: NodeClassValues.UAObject, labelKey: 'typeDetail.object' },
         { value: NodeClassValues.UAVariable, labelKey: 'typeDetail.variable' },
         { value: NodeClassValues.UAMethod, labelKey: 'typeDetail.method' },
      ];

   const lockedNodeClassLabel = nodeClassValue === NodeClassValues.UAObject
      ? t('typeDetail.object')
      : nodeClassValue === NodeClassValues.UAMethod
         ? t('typeDetail.method')
         : t('typeDetail.variable');

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={kindConfig ? t(kindConfig.titleKey) : t('typeDetail.addChild')}
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
            {/* Model (locked to parent's namespace) */}
            <TextField
               label={t('typeDetail.model')}
               value={parentModelLabel}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
            />

            {/* Reference Type — shown for Object/Variable children, restricted to
                subtypes of HierarchicalReferences. Implied (hidden) for Methods. */}
            {showReferenceType && (
               <NodeIdAutocomplete
                  options={refTypeOptions}
                  value={referenceTypeId}
                  onChange={setReferenceTypeId}
                  workspaceId={workspaceId}
                  label={t('typeDetail.referenceType')}
               />
            )}

            {/* Node Class — locked to a read-only label for a fixed childKind */}
            {kindConfig ? (
               <TextField
                  label={t('typeDetail.nodeClass')}
                  value={lockedNodeClassLabel}
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
               />
            ) : (
               <TextField
                  label={t('typeDetail.nodeClass')}
                  value={nodeClassValue}
                  onChange={(e) => handleNodeClassChange(Number(e.target.value))}
                  select
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true } }}
               >
                  {nodeClassOptions.map((opt) => (
                     <MenuItem key={opt.value} value={opt.value}>
                        {t(opt.labelKey)}
                     </MenuItem>
                  ))}
               </TextField>
            )}

            {/* BrowseName Namespace */}
            <TextField
               label={t('typeDetail.browseNameNs')}
               value={browseNameNs}
               onChange={(e) => setBrowseNameNs(e.target.value)}
               select
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true } }}
            >
               {browseNameNsOptions.map((opt) => (
                  <MenuItem key={opt.uri} value={opt.uri}>
                     {opt.label} - {opt.uri}
                  </MenuItem>
               ))}
            </TextField>

            {/* BrowseName */}
            <TextField
               label={t('typeDetail.browseName')}
               value={browseName}
               onChange={(e) => handleBrowseNameChange(e.target.value)}
               required
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true } }}
            />

            {/* DisplayName */}
            <TextField
               label={t('typeDetail.displayName')}
               value={displayName}
               onChange={(e) => handleDisplayNameChange(e.target.value)}
               required
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true } }}
            />

            {/* TypeDefinition (Object / Variable only) */}
            {showTypeDefinition && (
               <NodeIdAutocomplete
                  options={typeDefOptions}
                  value={typeDefinitionId}
                  onChange={handleTypeDefinitionChange}
                  workspaceId={workspaceId}
                  label={t('typeDetail.typeDefinition')}
                  placeholder={t('typeDetail.fieldDataTypePlaceholder')}
                  readOnly={kindConfig?.lockTypeDef}
               />
            )}


            {/* Variable-specific fields */}
            {isVariable && (
               <>
                  <NodeIdAutocomplete
                     options={dataTypeOptions}
                     value={dataType}
                     onChange={setDataType}
                     workspaceId={workspaceId}
                     label={t('typeDetail.dataType')}
                  />
                  <TextField
                     label={t('typeDetail.valueRank')}
                     value={valueRank}
                     onChange={(e) => setValueRank(Number(e.target.value))}
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
                     value={arrayDimensions}
                     onChange={(e) => setArrayDimensions(e.target.value)}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true } }}
                  />
               </>
            )}

            {/* Modelling Rule */}
            {showModellingRule && (
               <TextField
                  label={t('typeDetail.modellingRule')}
                  value={modellingRuleId}
                  onChange={(e) => setModellingRuleId(e.target.value)}
                  select
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true } }}
               >
                  {modellingRuleOptions.map((opt) => (
                     <MenuItem key={opt.value} value={opt.value}>
                        {t(opt.labelKey)}
                     </MenuItem>
                  ))}
               </TextField>
            )}

            {/* Description */}
            <TextField
               label={t('typeDetail.description')}
               value={description}
               onChange={(e) => setDescription(e.target.value)}
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
