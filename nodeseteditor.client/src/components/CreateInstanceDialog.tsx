import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import api, { extractErrorMessage } from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import type { Node as RestNode } from '../model/Node';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import Typography from '@mui/material/Typography';

import { ModelDialog } from './ModelDialog';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { NodeLocationField } from './NodeLocationField';
import { useHierarchicalReferenceTypes } from '../hooks/useHierarchicalReferenceTypes';
import { autoInstantiateMandatoryChildren } from '../utils/instantiateUtils';
import { getLastModelUri, setLastModelUri } from '../utils/lastModelUri';
import { getAllowedValueRankOptions } from '../utils/valueRank';
import { slugifyNodeId } from '../api/slug';

const BASE_OBJECT_TYPE = 'i=58';
const BASE_DATA_VARIABLE_TYPE = 'i=63';
/** Organizes — the reference a folder-style container holds its contents by. */
const ORGANIZES = 'i=35';

export interface CreatedInstanceInfo {
   nodeId: string;
   displayName: string;
   nodeClass: number;
}

interface CreateInstanceDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /**
    * NodeId of the ObjectType or VariableType to instantiate. Omit to let the user pick
    * one — which is what the Object page's Create does, since it starts from the node the
    * new instance will sit under rather than from a type.
    */
   typeDefinitionNodeId?: string;
   /** 8 = ObjectType → creates Object; 16 = VariableType → creates Variable. */
   nodeClass: number;
   /**
    * Turns the dialog into "create an instance that sits under another node", starting at
    * this one — the user can browse for a different location, and pick the reference type
    * holding it there (Organizes by default).
    *
    * The new node is created top-level (no ParentId) and the containment is an inverse
    * reference instead. That is deliberate: the parent reference is stored on the child,
    * so a ParentId may never point into another model — an inverse reference is the only
    * way an object in one model can sit under a node in another (the Core Objects folder,
    * say). It also means no ownership: deleting the target won't delete this node.
    */
   organizesUnderNodeId?: string;
   onSaved: (created: CreatedInstanceInfo) => void;
}

export const CreateInstanceDialog: React.FC<CreateInstanceDialogProps> = ({
   open,
   onClose,
   workspaceId,
   typeDefinitionNodeId,
   nodeClass,
   organizesUnderNodeId,
   onSaved,
}) => {
   const { t } = useTranslation();

   const instanceNodeClass = nodeClass === 16 ? 'Variable' : 'Object';
   const instanceNodeClassNum = nodeClass === 16 ? 2 : 1;
   const isVariable = instanceNodeClass === 'Variable';
   const organizes = !!organizesUnderNodeId;

   // Where the new instance goes, and how it is held there. Both start from the node the
   // dialog was opened on; a location is always required, so there is no way to clear it.
   const [locationOverride, setLocationOverride] = React.useState('');
   const [locationRefType, setLocationRefType] = React.useState(ORGANIZES);
   const location = locationOverride || organizesUnderNodeId || '';

   // The type to instantiate: fixed by the caller, or picked here when it isn't.
   const [typeDefOverride, setTypeDefOverride] = React.useState('');
   const pickTypeDef = !typeDefinitionNodeId;
   const typeDefNodeId = typeDefinitionNodeId
      ?? (typeDefOverride || (isVariable ? BASE_DATA_VARIABLE_TYPE : BASE_OBJECT_TYPE));

   const [modelUriOverride, setModelUriOverride] = React.useState('');
   const [browseName, setBrowseName] = React.useState('');
   const [displayName, setDisplayName] = React.useState('');
   const [description, setDescription] = React.useState('');
   const [designToolOnlyChecked, setDesignToolOnlyChecked] = React.useState(false);
   const [dataType, setDataType] = React.useState('i=24'); // BaseDataType default
   const [valueRank, setValueRank] = React.useState(-2); // Any default; refined when varTypeInfo loads
   const [arrayDimensions, setArrayDimensions] = React.useState('');
   const [isSaving, setIsSaving] = React.useState(false);
   const [saveError, setSaveError] = React.useState<string | null>(null);

   // Top-level Variables are always design-tool-only (a parentless Variable is
   // not a valid address-space node). Top-level Objects may opt in. A
   // design-tool-only node has no type definition, no children, and skips
   // instantiation entirely.
   //
   // Never when the instance is organized under another node: such a node carries a
   // reference, and a design-tool-only node may carry none.
   const designToolOnly = !organizes && (isVariable || designToolOnlyChecked);

   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return response.data;
      },
      enabled: open,
   });

   const allNamespaces = React.useMemo(() => namespacesData?.results ?? [], [namespacesData]);
   const privateNamespaces = React.useMemo(
      () => allNamespaces.filter(ns => ns.isPrivate && !!ns.uri),
      [allNamespaces],
   );

   const defaultModelUri = React.useMemo(() => {
      if (!open || privateNamespaces.length === 0) return '';
      const lastUsed = getLastModelUri();
      return (lastUsed && privateNamespaces.find(ns => ns.uri === lastUsed))
         ? lastUsed
         : privateNamespaces[0].uri ?? '';
   }, [open, privateNamespaces]);

   const modelUri = modelUriOverride || defaultModelUri;

   // The type picker, for when the caller named no type. Every ObjectType (or
   // VariableType) in the workspace, as the Add Child dialog lists them.
   const { data: pickableTypes } = useQuery({
      queryKey: ['workspaceTypes', workspaceId, nodeClass],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: {
               nodeClass: isVariable ? 'VariableType' : 'ObjectType',
               start: 0,
               count: 10000,
            },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return response.data.results ?? [];
      },
      enabled: open && pickTypeDef,
   });

   const typeDefOptions = React.useMemo((): NodeIdOption[] => {
      return (pickableTypes ?? []).map((n): NodeIdOption => ({
         nodeId: n.nodeId ?? '',
         displayName: n.browseName ?? n.displayName?.text ?? n.nodeId ?? '',
         modelUri: n.modelUri,
      }));
   }, [pickableTypes]);

   // Hierarchical types only: a non-hierarchical reference would describe a placement no
   // tree could show.
   const refTypeOptions = useHierarchicalReferenceTypes(workspaceId, open && organizes);

   // For a VariableType instantiation, fetch the type's DataType + ValueRank so the
   // new Variable's value attributes can default to (and be constrained by) them.
   const { data: varTypeInfo } = useQuery({
      queryKey: ['variableTypeInfo', workspaceId, typeDefNodeId],
      queryFn: async () => {
         const response = await api.get<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(typeDefNodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         return { dataType: response.data.dataType, valueRank: response.data.valueRank };
      },
      enabled: open && isVariable && !!typeDefNodeId,
   });

   // Snap DataType/ValueRank/ArrayDimensions to the VariableType's defaults once
   // its info arrives. User edits don't change varTypeInfo, so they are preserved.
   React.useEffect(() => {
      if (!isVariable || !varTypeInfo) return;
      if (varTypeInfo.dataType) setDataType(varTypeInfo.dataType);
      setValueRank(varTypeInfo.valueRank ?? -2);
      setArrayDimensions('');
   }, [varTypeInfo, isVariable]);

   // DataType picker is restricted to subtypes of the VariableType's DataType.
   const typeDefDataType = varTypeInfo?.dataType;
   const { data: dataTypeSubtypes } = useQuery({
      queryKey: ['dataTypeSubtypes', workspaceId, typeDefDataType],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/data-types/${slugifyNodeId(typeDefDataType!)}/subtypes`,
            {
               params: { depth: 10, count: 10000, includeSelf: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            },
         );
         return response.data.results ?? [];
      },
      enabled: open && isVariable && !!typeDefDataType,
   });

   const dataTypeOptions = React.useMemo((): NodeIdOption[] => {
      return (dataTypeSubtypes ?? []).map((dt): NodeIdOption => ({
         nodeId: dt.nodeId ?? '',
         displayName: dt.browseName ?? dt.displayName?.text ?? dt.nodeId ?? '',
         modelUri: dt.modelUri,
      }));
   }, [dataTypeSubtypes]);

   const allowedValueRankOptions = React.useMemo(
      () => getAllowedValueRankOptions(varTypeInfo?.valueRank),
      [varTypeInfo?.valueRank],
   );

   const handleClose = () => {
      setBrowseName('');
      setDisplayName('');
      setDescription('');
      setSaveError(null);
      setModelUriOverride('');
      setDesignToolOnlyChecked(false);
      setTypeDefOverride('');
      setLocationOverride('');
      setLocationRefType(ORGANIZES);
      setDataType('i=24');
      setValueRank(-2);
      setArrayDimensions('');
      onClose();
   };

   const canSave = browseName.trim() !== '' && modelUri !== '' && typeDefNodeId !== ''
      // An organized instance has to have somewhere to go.
      && (!organizes || (location !== '' && locationRefType !== ''));

   const handleSave = async () => {
      if (!canSave) return;
      setIsSaving(true);
      setSaveError(null);
      try {
         const response = await api.post<RestNode>(
            '/opcua/v1/nodes',
            {
               modelUri,
               nodeClass: instanceNodeClass,
               browseName: browseName.trim(),
               displayName: displayName.trim() || browseName.trim(),
               description: description.trim() || undefined,
               // The type definition is kept even for a design-tool-only node — it
               // is retained as read-only metadata (no children are instantiated).
               typeDefinitionId: typeDefNodeId,
               designToolOnly: designToolOnly || undefined,
               // Value attributes for a Variable instance (kept even when
               // design-tool-only — these are attributes, not references).
               dataType: isVariable ? dataType : undefined,
               valueRank: isVariable ? valueRank : undefined,
               arrayDimensions: isVariable ? (arrayDimensions || undefined) : undefined,
            },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );

         const createdNodeId = response.data?.nodeId;
         if (createdNodeId) {
            setLastModelUri(modelUri);
            // Put the new instance under its location. The reference is authored on the
            // new node and points back INVERSELY, which is what makes it the contained
            // one without taking a ParentId (see organizesUnderNodeId).
            if (organizes && location) {
               await api.post(
                  `/opcua/v1/nodes/${slugifyNodeId(createdNodeId)}/references`,
                  {
                     referenceTypeId: locationRefType,
                     targetNodeId: location,
                     isForward: false,
                  },
                  { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
               );
            }
            // Design-tool-only nodes have no children — instantiation is skipped.
            if (!designToolOnly) {
               try {
                  await autoInstantiateMandatoryChildren(
                     workspaceId, createdNodeId, typeDefNodeId, modelUri);
               } catch {
                  // Non-fatal: instance created but mandatory children not populated
               }
            }
            onSaved({
               nodeId: createdNodeId,
               displayName: displayName.trim() || browseName.trim(),
               nodeClass: instanceNodeClassNum,
            });
         }
         handleClose();
      } catch (e) {
         setSaveError(extractErrorMessage(e, 'Failed to create instance'));
      } finally {
         setIsSaving(false);
      }
   };

   return (
      <ModelDialog
         open={open}
         onClose={handleClose}
         title={organizes
            ? t('typeDetail.createOrganizedTitle', 'Create Object')
            : t('typeDetail.createInstanceTitle', 'Create Instance')}
         isLoading={isSaving}
         isError={!!saveError}
         error={saveError ? new Error(saveError) : null}
         actions={saveError ? [] : [
            {
               label: isSaving ? t('common.saving', 'Creating...') : t('common.ok'),
               onClick: handleSave,
               disabled: !canSave || isSaving,
            },
         ]}
      >
         {!saveError && (
            <Box sx={{ pt: 10, px: 6, pb: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
               {/* Where it goes and how it is held there. The reference type carries the
                   explanation on its own: Organizes means organized under the location,
                   not owned by it — which is also why no ParentId is set. */}
               {organizes && (
                  <>
                     <NodeLocationField
                        workspaceId={workspaceId}
                        value={location}
                        onChange={setLocationOverride}
                     />
                     <NodeIdAutocomplete
                        options={refTypeOptions}
                        value={locationRefType}
                        onChange={setLocationRefType}
                        workspaceId={workspaceId}
                        label={t('typeDetail.referenceType')}
                        recentContext="referenceType"
                     />
                  </>
               )}

               <TextField
                  label={t('typeDetail.model')}
                  value={modelUri}
                  onChange={(e) => setModelUriOverride(e.target.value)}
                  select
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true } }}
               >
                  {privateNamespaces.map((ns) => (
                     <MenuItem key={ns.uri} value={ns.uri}>
                        {ns.name ? `${ns.name} (${ns.uri})` : ns.uri}
                     </MenuItem>
                  ))}
               </TextField>

               {/* Which type to instantiate, when the caller didn't name one. */}
               {pickTypeDef && (
                  <NodeIdAutocomplete
                     options={typeDefOptions}
                     value={typeDefNodeId}
                     onChange={setTypeDefOverride}
                     workspaceId={workspaceId}
                     label={t('typeDetail.typeDefinition')}
                     placeholder={t('typeDetail.fieldDataTypePlaceholder')}
                     recentContext="typeDefinition"
                  />
               )}

               <TextField
                  label={t('typeDetail.browseName')}
                  value={browseName}
                  onChange={(e) => setBrowseName(e.target.value)}
                  required
                  fullWidth
                  size="small"
                  autoFocus
                  slotProps={{ inputLabel: { shrink: true } }}
               />

               <TextField
                  label={t('typeDetail.displayName')}
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true } }}
               />

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

               {/* Value attributes for a Variable instance, constrained by the
                   VariableType: DataType is limited to subtypes of the type's
                   DataType, ValueRank to ranks compatible with the type's. */}
               {isVariable && (
                  <>
                     <NodeIdAutocomplete
                        options={dataTypeOptions}
                        value={dataType}
                        onChange={setDataType}
                        workspaceId={workspaceId}
                        label={t('typeDetail.dataType')}
                        recentContext="dataType"
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

               {/* DesignToolOnly: read-only (always on) for top-level Variables;
                   an opt-in checkbox for top-level Objects. Not offered at all for an
                   organized instance — it carries a reference, which such a node may not. */}
               {!organizes && (
                  <FormControlLabel
                     control={
                        <Checkbox
                           size="small"
                           checked={designToolOnly}
                           disabled={isVariable}
                           onChange={(e) => setDesignToolOnlyChecked(e.target.checked)}
                        />
                     }
                     label={t('typeDetail.designToolOnly', 'Design-tool only (standalone, no children; type definition is read-only)')}
                  />
               )}
               {!organizes && isVariable && (
                  <Typography variant="caption" color="text.secondary" sx={{ mt: -4 }}>
                     {t('typeDetail.designToolOnlyVariableNote',
                        'Top-level variables are always design-tool-only: standalone, with no children. The type definition is kept read-only.')}
                  </Typography>
               )}
            </Box>
         )}
      </ModelDialog>
   );
};
