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
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';

import { ModelDialog } from './ModelDialog';
import { NodeIdModeField } from './NodeIdModeField';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { useRequestedNodeId } from '../hooks/useRequestedNodeId';
import { formatWithModelPrefix } from '../utils/formatNodeId';

interface NodeAttributeDto {
   name: string;
   value?: string | null;
}

interface BrowseNodeResult {
   nodeId?: string;
   browseName?: string;
   displayName?: string;
   nodeClass: number;
   modelUri?: string;
   superTypeIds?: string[];
}

interface DataTypeOption {
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

export interface EditDataTypeDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   nodeId: string;
   attributes: NodeAttributeDto[];
   onSaved: (created?: CreatedNodeInfo) => void;
   mode?: 'edit' | 'create';
   superTypeNodeId?: string;
   superTypeModelUri?: string;
}

const UINTEGER_NODE_ID = 'i=28';

function isOrDerives(option: DataTypeOption, targetNodeId: string): boolean {
   if (option.nodeId === targetNodeId) return true;
   return option.superTypeIds.some(id => id === targetNodeId);
}

function stripModelPrefix(value: string): string {
   const match = value.match(/^\[.*?\]:(.+)$/);
   return match ? match[1] : value;
}

export const EditDataTypeDialog: React.FC<EditDataTypeDialogProps> = ({
   open,
   onClose,
   workspaceId,
   nodeId,
   attributes,
   onSaved,
   mode = 'edit',
   superTypeNodeId: superTypeNodeIdProp,
   superTypeModelUri,
}) => {
   const { t } = useTranslation();
   const isCreateMode = mode === 'create';

   const [browseName, setBrowseName] = React.useState('');
   const [displayName, setDisplayName] = React.useState('');
   const [isAbstract, setIsAbstract] = React.useState(false);
   const [selectedSuperTypeId, setSelectedSuperTypeId] = React.useState('');
   const [isOptionSet, setIsOptionSet] = React.useState(false);
   const [description, setDescription] = React.useState('');
   const [isSaving, setIsSaving] = React.useState(false);
   const [saveError, setSaveError] = React.useState<string | null>(null);
   const [initialized, setInitialized] = React.useState(false);

   // Create-mode state
   const [selectedModelUri, setSelectedModelUri] = React.useState('');
   const [displayNameManuallyEdited, setDisplayNameManuallyEdited] = React.useState(false);

   const attrMap = React.useMemo(() => {
      const map = new Map<string, string>();
      for (const a of attributes) {
         if (a.value != null) map.set(a.name, a.value);
      }
      return map;
   }, [attributes]);

   // Fetch all DataTypes for SuperType autocomplete
   const { data: allTypes } = useQuery({
      queryKey: ['workspaceTypes', workspaceId, 64],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: { nodeClass: 'DataType', start: 0, count: 10000 },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId,
            displayName: n.browseName ?? n.displayName?.text,
            nodeClass: 64,
            modelUri: n.modelUri,
            superTypeIds: n.superTypeIds,
         })) as BrowseNodeResult[];
      },
      enabled: open,
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

   const privateModels = React.useMemo((): PrivateModelOption[] => {
      return namespaces
         .filter(ns => ns.isPrivate && ns.uri)
         .map(ns => ({
            modelUri: ns.uri,
            label: ns.name || ns.uri,
         }));
   }, [namespaces]);

   // Build SuperType options: exclude current node and its subtypes
   const superTypeOptions = React.useMemo((): DataTypeOption[] => {
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

   // Derive selected DataTypeOption from nodeId for superTypeIds check
   const selectedSuperType = React.useMemo(() => {
      if (!selectedSuperTypeId) return null;
      return superTypeOptions.find(o => o.nodeId === selectedSuperTypeId) ?? null;
   }, [superTypeOptions, selectedSuperTypeId]);

   // Options for NodeIdAutocomplete
   const superTypeNodeIdOptions = React.useMemo((): NodeIdOption[] => {
      if (!allTypes) return [];
      return superTypeOptions.map(opt => ({
         nodeId: opt.nodeId,
         displayName: opt.displayName,
         modelUri: allTypes.find(t => t.nodeId === opt.nodeId)?.modelUri,
      }));
   }, [superTypeOptions, allTypes]);

   // Create-time NodeId choice. A supertype is not a containment parent, so a new
   // DataType's string identifier starts from its BrowseName rather than extending
   // anything — hence no parentNodeId here.
   const nodeIdState = useRequestedNodeId({
      open: open && isCreateMode,
      workspaceId,
      modelUri: selectedModelUri,
      browseName,
   });

   // Initialize state from attributes when dialog opens and data is available
   React.useEffect(() => {
      if (open && superTypeOptions.length > 0 && !initialized) {
         if (isCreateMode) {
            // Create mode initialization
            setBrowseName('');
            setDisplayName('');
            setDescription('');
            setIsAbstract(false);
            setIsOptionSet(false);
            setDisplayNameManuallyEdited(false);

            // Default model selection: use superType's model if private, else first private model
            if (superTypeModelUri && privateModels.some(m => m.modelUri === superTypeModelUri)) {
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
            setDisplayName(attrMap.get('DisplayName') ?? '');
            setDescription(attrMap.get('Description') ?? '');
            setIsAbstract(attrMap.get('IsAbstract') === 'True');

            // Find current SuperType in options by matching formatted label
            const superTypeDisplay = attrMap.get('SuperType') ?? '';
            const match = superTypeOptions.find(opt => opt.label === superTypeDisplay);
            setSelectedSuperTypeId(match?.nodeId ?? '');

            // IsOptionSet derived from DataTypeForm
            const dtForm = attrMap.get('DataTypeForm') ?? '';
            setIsOptionSet(dtForm === 'OptionSet');
         }
         setInitialized(true);
         setSaveError(null);
      }
      if (!open) {
         setInitialized(false);
      }
   }, [open, superTypeOptions, attrMap, initialized, isCreateMode, superTypeNodeIdProp, superTypeModelUri, privateModels]);

   const showIsOptionSet = selectedSuperType != null && isOrDerives(selectedSuperType, UINTEGER_NODE_ID);

   // Reset dependent fields when SuperType changes
   const prevShowIsOptionSet = React.useRef(showIsOptionSet);
   React.useEffect(() => {
      if (prevShowIsOptionSet.current && !showIsOptionSet) setIsOptionSet(false);
      prevShowIsOptionSet.current = showIsOptionSet;
   }, [showIsOptionSet]);

   const canSave = isCreateMode
      ? browseName.trim() !== '' && displayName.trim() !== '' && selectedSuperTypeId !== ''
         && selectedModelUri !== '' && nodeIdState.canSave
      : browseName.trim() !== '' && displayName.trim() !== '' && selectedSuperTypeId !== '';

   // Clear error when user modifies fields
   const clearError = () => { if (saveError) setSaveError(null); };

   const handleBrowseNameChange = (value: string) => {
      setBrowseName(value);
      clearError();
      if (isCreateMode && !displayNameManuallyEdited) {
         setDisplayName(value);
      }
   };

   const handleDisplayNameChange = (value: string) => {
      setDisplayName(value);
      clearError();
      if (isCreateMode) {
         setDisplayNameManuallyEdited(true);
      }
   };

   const handleSave = async () => {
      if (!canSave || !selectedSuperTypeId) return;
      setIsSaving(true);
      setSaveError(null);

      try {
         if (isCreateMode) {
            const createResponse = await api.post(
               `/opcua/v1/nodes/${slugifyNodeId(selectedSuperTypeId)}/children`,
               {
                  modelUri: selectedModelUri,
                  requestedNodeId: nodeIdState.requestedNodeId,
                  nodeClass: 'DataType',
                  browseName: browseName.trim(),
                  displayName: displayName.trim(),
                  description: description,
                  isAbstract,
                  referenceTypeId: 'i=45',
                  isOptionSet: showIsOptionSet ? isOptionSet : false,
               },
               { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
            );
            // The server is the only authority on the id — it allocates a numeric one
            // unless a string id was requested, so never report back what was asked for.
            const createdNodeId = createResponse.data?.nodeId;
            if (!createdNodeId) {
               throw new Error('The server did not return a NodeId for the new DataType.');
            }
            // Build superTypeIds chain: parent's chain + parent itself
            const parentSuperTypeIds = selectedSuperType?.superTypeIds
               ? [...selectedSuperType.superTypeIds, selectedSuperTypeId]
               : [selectedSuperTypeId];
            onSaved({
               nodeId: createdNodeId,
               displayName: displayName.trim(),
               modelUri: selectedModelUri,
               nodeClass: 64, // UADataType
               superTypeIds: parentSuperTypeIds,
            });
         } else {
            await api.put(
               `/opcua/v1/nodes/${slugifyNodeId(nodeId)}`,
               {
                  browseName: browseName.trim(),
                  displayName: displayName.trim(),
                  description: description,
                  isAbstract,
                  isOptionSet: showIsOptionSet ? isOptionSet : false,
               },
               { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
            );
            onSaved();
         }
         onClose();
      } catch (e) {
         const msg = e instanceof Error ? e.message : 'Failed to save';
         setSaveError(msg);
      } finally {
         setIsSaving(false);
      }
   };

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={isCreateMode ? t('typeDetail.createDataType') : t('typeDetail.editDataType')}
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
            {isCreateMode ? (
               /* NodeId — numeric by default, or a string identifier the author picks */
               <NodeIdModeField state={nodeIdState} />
            ) : (
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
               value={isCreateMode ? 'UADataType' : (attrMap.get('NodeClass') ?? '')}
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
               readOnly={!isCreateMode}
               recentContext="superType"
            />
            {showIsOptionSet && (
               <FormControlLabel
                  control={
                     <Checkbox
                        checked={isOptionSet}
                        onChange={(e) => { setIsOptionSet(e.target.checked); clearError(); }}
                     />
                  }
                  label="IsOptionSet"
               />
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
         </Box>
      </ModelDialog>
   );
};
