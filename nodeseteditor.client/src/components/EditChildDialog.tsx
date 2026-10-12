import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import api, { extractErrorMessage } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import type { Node as RestNode } from '../model/Node';

import Box from '@mui/material/Box';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';

import { ModelDialog } from './ModelDialog';
import { NodeIdAutocomplete } from './NodeIdAutocomplete';
import type { NodeIdOption } from './NodeIdAutocomplete';
import { NodeLocationField } from './NodeLocationField';
import { useHierarchicalReferenceTypes } from '../hooks/useHierarchicalReferenceTypes';
import { formatNodeId, extractBrowseNameNamespace, extractNamespaceUri } from '../utils/formatNodeId';
import { getAllowedValueRankOptions, allValueRankOptions } from '../utils/valueRank';
import { parseConformanceUnits, formatConformanceUnits } from '../utils/conformanceUnits';
import { cloneNode, resolveParentLink, ORGANIZES } from '../utils/cloneNode';
import type { ClonedNodeInfo, ParentLink } from '../utils/cloneNode';
import { getLastModelUri, setLastModelUri } from '../utils/lastModelUri';

interface ChildNodeDetails {
   nodeId?: string;
   nodeClass: number;
   browseNameNs?: string;
   browseName?: string;
   displayName?: string;
   description?: string;
   typeDefinitionId?: string;
   modellingRuleId?: string;
   modelUri?: string;
   dataType?: string;
   valueRank?: number | null;
   arrayDimensions?: string;
   designToolOnly?: boolean;
   /** Absent for a top-level node — the only instances that carry conformance units. */
   parentNodeId?: string;
   conformanceUnits?: string[];
}

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

export interface EditChildDialogProps {
   open: boolean;
   onClose: () => void;
   /** Something was written — refresh whatever showed the node. */
   onSaved: () => void;
   workspaceId: string;
   childNodeId: string;
   showModellingRule: boolean;
   readOnly?: boolean;
   /**
    * 'clone' turns the same form into "create a copy of this node": the attributes come
    * up prefilled from `childNodeId` and stay editable, plus the writable model the copy
    * is created in. Its location is the source's own and shown read-only — a clone is a
    * sibling, so that is not a choice — while the reference type holding it there is.
    * The source node is left untouched, so it may live in a read-only model (Core
    * included).
    */
   mode?: 'edit' | 'clone';
   /** Clone mode: the copy that was created, for the caller to navigate to. */
   onCloned?: (created: ClonedNodeInfo) => void;
   /**
    * The hierarchical reference type connecting this child to its parent. When
    * provided, the ReferenceType field is shown (Object/Variable children only).
    * Omitted when editing a node standalone (no structural-parent context).
    */
   referenceTypeId?: string;
   /**
    * Whether the reference type may be changed. False for overridden or
    * inherited children — an override must keep its declaration's reference type.
    */
   canEditReferenceType?: boolean;
}

const NodeClassValues = {
   UAObject: 1,
   UAVariable: 2,
   UAMethod: 4,
   UAObjectType: 8,
   UAVariableType: 16,
} as const;

const nodeClassLabel: Record<number, string> = {
   [NodeClassValues.UAObject]: 'Object',
   [NodeClassValues.UAVariable]: 'Variable',
   [NodeClassValues.UAMethod]: 'Method',
};

const modellingRuleOptions = [
   { value: '', labelKey: 'typeDetail.none' },
   { value: 'i=78', labelKey: 'typeDetail.mandatory' },
   { value: 'i=80', labelKey: 'typeDetail.optionalRule' },
   { value: 'i=11508', labelKey: 'typeDetail.optionalPlaceholder' },
   { value: 'i=11510', labelKey: 'typeDetail.mandatoryPlaceholder' },
];

export const EditChildDialog: React.FC<EditChildDialogProps> = ({
   open,
   onClose,
   onSaved,
   workspaceId,
   childNodeId,
   showModellingRule,
   readOnly = false,
   mode = 'edit',
   onCloned,
   referenceTypeId,
   canEditReferenceType = false,
}) => {
   const { t } = useTranslation();
   const isClone = mode === 'clone';

   // Clone-only fields: where the copy goes. `targetModelUri` has to be a writable
   // (private, checked-out) model — the server refuses node writes anywhere else —
   // while the source may come from any namespace. The LOCATION is not among them: a
   // clone lands beside the original, so it is the source's own and read-only.
   const [targetModelUri, setTargetModelUri] = React.useState('');
   const [includeChildren, setIncludeChildren] = React.useState(true);
   // The BrowseName namespace follows the target model until the user picks one
   // themselves — see the model field below.
   const browseNameNsEdited = React.useRef(false);

   const [browseNameNs, setBrowseNameNs] = React.useState('');
   const [browseName, setBrowseName] = React.useState('');
   const [referenceType, setReferenceType] = React.useState('');
   const [displayName, setDisplayName] = React.useState('');
   const [description, setDescription] = React.useState('');
   const [typeDefinitionId, setTypeDefinitionId] = React.useState('');
   const [modellingRuleId, setModellingRuleId] = React.useState('');
   const [dataType, setDataType] = React.useState('');
   const [valueRank, setValueRank] = React.useState(-1);
   const [arrayDimensions, setArrayDimensions] = React.useState('');
   // Free text, one conformance unit per line.
   const [conformanceUnits, setConformanceUnits] = React.useState('');
   const [isSaving, setIsSaving] = React.useState(false);
   const [saveError, setSaveError] = React.useState<string | null>(null);
   const [initialized, setInitialized] = React.useState(false);

   // Fetch child node details
   const { data: details, isLoading: detailsLoading } = useQuery({
      queryKey: ['childNodeDetails', workspaceId, childNodeId],
      queryFn: async () => {
         const response = await api.get(
            `/opcua/v1/nodes/${slugifyNodeId(childNodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         const n = response.data as Record<string, unknown>;
         const ncStr = n.nodeClass as string;
         const nodeClassNum = ncStr === 'Object' ? 1 : ncStr === 'Variable' ? 2 : ncStr === 'Method' ? 4
            : ncStr === 'ObjectType' ? 8 : ncStr === 'VariableType' ? 16 : ncStr === 'DataType' ? 64 : 0;
         // Parse raw BrowseName "nsu=URI;Name" into namespace URI and plain name.
         // The BrowseName's namespace is taken from the BrowseName itself — NOT
         // the node's NodeId namespace. An overridden inherited property keeps
         // its declaring namespace (e.g. ns0) even though the override node lives
         // in the current model; a bare BrowseName denotes ns0. Falling back to
         // the NodeId namespace here would flip the BrowseName to the current
         // model on save.
         const rawBrowseName = (n.browseName as string) ?? '';
         const semiIdx = rawBrowseName.indexOf(';');
         const browseNsUri = extractBrowseNameNamespace(rawBrowseName);
         const plainBrowseName = semiIdx >= 0 ? rawBrowseName.substring(semiIdx + 1) : rawBrowseName;

         return {
            nodeId: n.nodeId as string,
            nodeClass: nodeClassNum,
            browseNameNs: browseNsUri,
            browseName: plainBrowseName,
            displayName: (n.displayName as { text?: string })?.text ?? '',
            description: (n.description as { text?: string })?.text ?? '',
            typeDefinitionId: n.typeDefinition as string | undefined,
            modellingRuleId: n.modellingRule as string | undefined,
            modelUri: n.modelUri as string | undefined,
            dataType: n.dataType as string | undefined,
            valueRank: n.valueRank as number | undefined,
            arrayDimensions: n.arrayDimensions as string | undefined,
            designToolOnly: n.designToolOnly as boolean | undefined,
            parentNodeId: n.parentNodeId as string | undefined,
            conformanceUnits: n.category as string[] | undefined,
         } as ChildNodeDetails;
      },
      enabled: open && !!childNodeId,
   });

   const nc = details?.nodeClass ?? 0;
   const isVariable = nc === NodeClassValues.UAVariable;
   const isMethod = nc === NodeClassValues.UAMethod;
   // A design-tool-only node keeps its defining HasTypeDefinition as read-only
   // metadata, so the field is shown but not editable (see the autocomplete's
   // readOnly below). Its DataType remains editable.
   const designToolOnly = details?.designToolOnly === true;
   const showTypeDefinition = !isMethod && nc !== 0;
   // The parent→child reference type. Shown for Object/Variable children when the
   // caller supplies the current type (i.e. edited from a parent's child list).
   // Editable only for genuine (non-override, non-inherited) children.
   const isObjectOrVariable = nc === NodeClassValues.UAObject || isVariable;

   // TypeDefinition options. Objects pick any ObjectType. Variables are
   // constrained to their type family so the picker can't switch a Property to a
   // non-PropertyType (or a data variable to a PropertyType): a Property
   // (TypeDefinition under PropertyType, i=68) may only pick PropertyType
   // subtypes; a data variable may only pick BaseDataVariableType (i=63) subtypes.
   const PROPERTY_TYPE_ID = 'i=68';
   const BASE_DATA_VARIABLE_TYPE_ID = 'i=63';

   const { data: objectTypes } = useQuery({
      queryKey: ['workspaceTypes', workspaceId, NodeClassValues.UAObjectType],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: { nodeClass: 'ObjectType', start: 0, count: 10000 },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return response.data.results ?? [];
      },
      enabled: open && showTypeDefinition && !isVariable,
   });

   const fetchVariableTypeSubtypes = React.useCallback(async (rootId: string) => {
      const response = await api.get<PaginatedResponse<RestNode>>(
         `/opcua/v1/types/variable-types/${slugifyNodeId(rootId)}/subtypes`,
         {
            params: { depth: 10, count: 10000, includeSelf: true },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
      return response.data.results ?? [];
   }, [workspaceId]);

   const { data: propertyTypeSubtypes } = useQuery({
      queryKey: ['variableTypeSubtypes', workspaceId, PROPERTY_TYPE_ID],
      queryFn: () => fetchVariableTypeSubtypes(PROPERTY_TYPE_ID),
      enabled: open && isVariable,
   });
   const { data: dataVariableTypeSubtypes } = useQuery({
      queryKey: ['variableTypeSubtypes', workspaceId, BASE_DATA_VARIABLE_TYPE_ID],
      queryFn: () => fetchVariableTypeSubtypes(BASE_DATA_VARIABLE_TYPE_ID),
      enabled: open && isVariable,
   });

   // A Property's current TypeDefinition lives in the PropertyType subtree.
   const isProperty = React.useMemo(
      () => isVariable && (propertyTypeSubtypes ?? []).some(n => n.nodeId === typeDefinitionId),
      [isVariable, propertyTypeSubtypes, typeDefinitionId],
   );

   const typeDefOptions = React.useMemo((): NodeIdOption[] => {
      const source = isVariable
         ? (isProperty ? propertyTypeSubtypes : dataVariableTypeSubtypes)
         : objectTypes;
      return (source ?? []).map((n): NodeIdOption => ({
         nodeId: n.nodeId ?? '',
         displayName: n.browseName ?? n.displayName?.text ?? n.nodeId ?? '',
         modelUri: n.modelUri,
      }));
   }, [isVariable, isProperty, propertyTypeSubtypes, dataVariableTypeSubtypes, objectTypes]);

   // Fetch VariableType info (DataType + ValueRank) when TypeDefinition is set
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

   // DataType picker options. Normally constrained to subtypes of the
   // VariableType's DataType; but a variable with no TypeDefinition (e.g. a
   // design-tool-only top-level variable) has no such constraint, so fall back
   // to every DataType (subtypes of BaseDataType, i=24) — otherwise the picker
   // would be empty and the stored DataType would render blank.
   const typeDefDataType = varTypeInfo?.dataType;
   const dataTypeConstraintRoot =
      typeDefDataType ?? (isVariable && !typeDefinitionId ? 'i=24' : undefined);
   const { data: dataTypeSubtypes } = useQuery({
      queryKey: ['dataTypeSubtypes', workspaceId, dataTypeConstraintRoot],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/types/data-types/${slugifyNodeId(dataTypeConstraintRoot!)}/subtypes`,
            {
               params: { depth: 10, count: 10000, includeSelf: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId, displayName: n.browseName ?? n.displayName?.text, nodeClass: 64,
         })) as BrowseNodeResult[];
      },
      enabled: open && isVariable && !!dataTypeConstraintRoot,
   });

   const dataTypeOptions = React.useMemo((): NodeIdOption[] => {
      return (dataTypeSubtypes ?? []).map((dt): NodeIdOption => ({
         nodeId: dt.nodeId ?? '',
         displayName: dt.displayName ?? dt.nodeId ?? '',
         modelUri: dt.modelUri,
      }));
   }, [dataTypeSubtypes]);

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

   const browseNameNsOptions = React.useMemo(() => {
      return (namespacesData?.results ?? [])
         .filter(ns => !!ns.uri)
         .map(ns => ({ uri: ns.uri, label: ns.name || ns.uri }));
   }, [namespacesData]);

   // Models a node may be written to: private AND checked out. The server enforces
   // exactly this pair (see PersistModelAsync), so offering anything else would only
   // produce a "check it out before editing" failure on save.
   const writableModelOptions = React.useMemo(() => {
      return (namespacesData?.results ?? [])
         .filter(ns => ns.isPrivate && ns.isEditable && !!ns.uri)
         .map(ns => ({ uri: ns.uri as string, label: ns.name ? `${ns.name} (${ns.uri})` : ns.uri as string }));
   }, [namespacesData]);

   // The source's own parent and the reference type holding it there — the clone's
   // defaults. Not needed in edit mode, where the caller passes the reference type.
   const { data: sourceParentLink } = useQuery({
      queryKey: ['nodeParentLink', workspaceId, childNodeId],
      queryFn: () => resolveParentLink(workspaceId, childNodeId),
      enabled: open && isClone && !!childNodeId,
   });

   const modelNameMap = React.useMemo(() => {
      const map = new Map<string, string>();
      for (const ns of (namespacesData?.results ?? [])) {
         if (ns.uri && ns.name) map.set(ns.uri, ns.name);
      }
      return map;
   }, [namespacesData]);

   const formattedNodeId = React.useMemo(
      () => formatNodeId(childNodeId, modelNameMap),
      [childNodeId, modelNameMap],
   );

   /**
    * How the clone will attach: whatever relationship the source has, which is what
    * makes the copy its sibling — an owned child stays owned, a top-level node organized
    * under something stays top-level and organized rather than silently acquiring a
    * ParentId. The location is fixed for that reason; only the reference type is the
    * user's to change.
    */
   const cloneLink: ParentLink = React.useMemo(() => ({
      kind: sourceParentLink?.kind ?? 'none',
      targetNodeId: sourceParentLink?.targetNodeId,
      referenceTypeId: referenceType || ORGANIZES,
   }), [sourceParentLink, referenceType]);

   // The reference type tying the node to the one above it. In edit mode it is shown when
   // the caller supplies the current value (i.e. the node was opened from a parent's child
   // list) and editable only for genuine, non-inherited children. A clone resolves the
   // link itself, so it shows the field whenever there is somewhere to attach to — that
   // and the read-only location are the two fields describing the placement.
   const showReferenceType = isObjectOrVariable
      && (isClone ? !!cloneLink.targetNodeId : referenceTypeId !== undefined);
   const referenceTypeEditable = showReferenceType && !readOnly
      && (isClone || canEditReferenceType);

   // Options for that picker — hierarchical types only, so the choice can never describe
   // a placement no tree could show. Fetched whenever the field is shown, so the current
   // value resolves to a display name even when read-only.
   const refTypeOptions = useHierarchicalReferenceTypes(workspaceId, open && showReferenceType);

   // Conformance units annotate what a specification defines, so among instances they
   // only make sense on a top-level Object — a child is covered by whatever unit its
   // owning node belongs to, and has no independent place in a conformance statement.
   // An organized clone IS top-level (it takes no ParentId), so it keeps them; only a
   // clone created as a child loses them.
   const showConformanceUnits = nc === NodeClassValues.UAObject
      && (isClone ? cloneLink.kind !== 'parent' : !details?.parentNodeId);

   const modellingRuleDisplayName = React.useMemo(() => {
      const opt = modellingRuleOptions.find(o => o.value === modellingRuleId);
      return opt ? opt.labelKey : '';
   }, [modellingRuleId]);

   const valueRankDisplayName = React.useMemo(() => {
      const opt = allValueRankOptions.find(o => o.value === valueRank);
      return opt ? opt.labelKey : String(valueRank);
   }, [valueRank]);

   // When user changes TypeDefinition, sync DataType/ValueRank from new TypeDef
   const typeDefChangedByUser = React.useRef(false);
   const handleTypeDefinitionChange = (id: string) => {
      setTypeDefinitionId(id);
      clearError();
      typeDefChangedByUser.current = true;
   };

   React.useEffect(() => {
      if (!varTypeInfo || !typeDefChangedByUser.current) return;
      typeDefChangedByUser.current = false;
      if (varTypeInfo.dataType) setDataType(varTypeInfo.dataType);
      setValueRank(varTypeInfo.valueRank ?? -2);
      setArrayDimensions('');
   }, [varTypeInfo, typeDefinitionId]);

   // Initialize form when details load. A clone additionally needs its parent link,
   // so it waits for that too; everything else is seeded the same way, which is what
   // makes the clone form read as "this node's attributes, ready to be revised".
   React.useEffect(() => {
      const cloneInputsReady = !isClone || (!!sourceParentLink && !!namespacesData);
      if (open && details && cloneInputsReady && !initialized) {
         setBrowseName(isClone ? `${details.browseName ?? ''}Copy` : (details.browseName ?? ''));
         setDisplayName(isClone ? `${details.displayName ?? ''}Copy` : (details.displayName ?? ''));
         setDescription(details.description ?? '');
         setTypeDefinitionId(details.typeDefinitionId ?? '');
         setModellingRuleId(details.modellingRuleId ?? '');
         setDataType(details.dataType ?? '');
         setValueRank(details.valueRank ?? -1);
         setArrayDimensions(details.arrayDimensions ?? '');
         setConformanceUnits(formatConformanceUnits(details.conformanceUnits));
         setSaveError(null);

         if (isClone) {
            // Target model: the source's own if it can be written to (cloning inside a
            // private model is the common case), else the one last used for a create,
            // else the first writable model. Taken from the NodeId, which always
            // carries the namespace.
            const writable = writableModelOptions.map(o => o.uri);
            const candidates = [extractNamespaceUri(childNodeId), getLastModelUri()];
            const target = candidates.find(uri => uri && writable.includes(uri))
               ?? writable[0] ?? '';
            setTargetModelUri(target);
            // The clone is a node the user authors in their own model, so its BrowseName
            // belongs in that namespace by default — not in the source's, which for a
            // Core node would mint a new name in the OPC UA namespace. Overridable below.
            setBrowseNameNs(target);
            browseNameNsEdited.current = false;
            // Default to the reference type the source is held by, so the copy starts out
            // as its sibling in every respect; the user can pick another.
            setReferenceType(sourceParentLink?.referenceTypeId ?? '');
            setIncludeChildren(true);
         } else {
            setBrowseNameNs(details.browseNameNs ?? '');
            setReferenceType(referenceTypeId ?? '');
         }
         setInitialized(true);
      }
      if (!open) {
         setInitialized(false);
      }
   }, [open, details, initialized, referenceTypeId, isClone, sourceParentLink,
      namespacesData, writableModelOptions, childNodeId]);

   const canSave = browseName.trim() !== ''
      && (!showTypeDefinition || typeDefinitionId !== '')
      // A clone has nowhere to go without a writable model, and a Method must be created
      // as a child: only Objects and Variables may be top-level nodes, which rules out
      // both the standalone and the organized shapes for one.
      && (!isClone || (targetModelUri !== '' && (!isMethod || cloneLink.kind === 'parent')));

   const clearError = () => { if (saveError) setSaveError(null); };

   const handleModelChange = (uri: string) => {
      setTargetModelUri(uri);
      // Keep the BrowseName namespace on the model the clone is created in, unless the
      // user has deliberately pointed it somewhere else.
      if (!browseNameNsEdited.current) setBrowseNameNs(uri);
      clearError();
   };

   const handleClone = async () => {
      if (!canSave) return;
      setIsSaving(true);
      setSaveError(null);
      try {
         const created = await cloneNode({
            workspaceId,
            sourceNodeId: childNodeId,
            nodeClass: nc,
            modelUri: targetModelUri,
            link: cloneLink,
            browseName: browseName.trim(),
            browseNameModelUri: browseNameNs || undefined,
            displayName: displayName.trim(),
            description: description || undefined,
            typeDefinitionId: showTypeDefinition ? (typeDefinitionId || undefined) : undefined,
            modellingRuleId: showModellingRule ? (modellingRuleId || undefined) : undefined,
            dataType: isVariable ? dataType : undefined,
            valueRank: isVariable ? valueRank : undefined,
            arrayDimensions: isVariable ? arrayDimensions : undefined,
            designToolOnly: details?.designToolOnly,
            category: showConformanceUnits ? parseConformanceUnits(conformanceUnits) : undefined,
            includeChildren,
         });
         setLastModelUri(targetModelUri);
         onSaved();
         onCloned?.(created);
         onClose();
      } catch (e) {
         setSaveError(extractErrorMessage(e, 'Failed to clone node'));
      } finally {
         setIsSaving(false);
      }
   };

   const handleSave = async () => {
      if (!canSave) return;
      setIsSaving(true);
      setSaveError(null);

      try {
         const payload: Record<string, unknown> = {
            browseName: browseName.trim(),
            browseNameModelUri: browseNameNs || undefined,
            displayName: displayName.trim(),
            description: description,
            modellingRuleId: modellingRuleId || '',
         };

         // Only send a reference-type change when the field is editable and the
         // value actually changed — the server retypes the parent→child reference.
         if (referenceTypeEditable && referenceType && referenceType !== referenceTypeId) {
            payload.referenceTypeId = referenceType;
         }

         // A design-tool-only node's TypeDefinition is read-only — don't resend it
         // (the server preserves the existing value when it is omitted).
         if (showTypeDefinition && !designToolOnly) {
            payload.typeDefinitionId = typeDefinitionId;
         }

         if (isVariable) {
            payload.dataType = dataType;
            payload.valueRank = valueRank;
            payload.arrayDimensions = arrayDimensions;
         }

         // Only sent when the field is shown — omitted, the server keeps what is stored,
         // so a child edit can't clear units a node acquired before it was re-parented.
         if (showConformanceUnits) {
            payload.category = parseConformanceUnits(conformanceUnits);
         }

         await api.put(
            `/opcua/v1/nodes/${slugifyNodeId(childNodeId)}`,
            payload,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         onSaved();
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
         title={isClone
            ? t('typeDetail.cloneTitle', 'Clone Node')
            : readOnly ? t('typeDetail.viewChild') : t('typeDetail.editChild')}
         // A clone also waits on its parent link and the namespace list, so the form
         // appears already filled in rather than blank-then-populated.
         isLoading={isSaving || detailsLoading || (isClone && !initialized)}
         isError={!!saveError}
         error={saveError ? new Error(saveError) : null}
         disableClose={isClone && isSaving}
         actions={readOnly ? [] : [
            {
               label: t('common.ok'),
               onClick: isClone ? handleClone : handleSave,
               disabled: !canSave || isSaving || detailsLoading,
            },
         ]}
      >
         <Box sx={{ pt: 10, px: 6, pb: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
            {isClone ? (
               <>
                  {/* Reads top to bottom as the sentence it is: a copy OF this node, INTO
                      this model, AT this location, held BY this reference type. */}
                  <TextField
                     label={t('typeDetail.cloneSource', 'Copy of')}
                     value={formattedNodeId}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
                  />

                  {/* Restricted to writable models, so a node from any namespace — Core
                      included — can be cloned into one of the user's own. */}
                  <TextField
                     label={t('typeDetail.model')}
                     value={targetModelUri}
                     onChange={(e) => handleModelChange(e.target.value)}
                     select
                     required
                     fullWidth
                     size="small"
                     disabled={writableModelOptions.length === 0}
                     helperText={writableModelOptions.length === 0
                        ? t('typeDetail.cloneNoWritableModel',
                           'No writable model in this workspace. Create a private model, or check one out, first.')
                        : undefined}
                     slotProps={{ inputLabel: { shrink: true } }}
                  >
                     {writableModelOptions.map((opt) => (
                        <MenuItem key={opt.uri} value={opt.uri}>
                           {opt.label}
                        </MenuItem>
                     ))}
                  </TextField>

                  {/* The source's own location, read-only: a clone is a sibling of the
                      node it was taken from, so this is not a choice. The reference type
                      next to it is — and the pair describes the placement the same way
                      the Create dialog's does. */}
                  <NodeLocationField
                     workspaceId={workspaceId}
                     value={cloneLink.targetNodeId ?? ''}
                  />
                  {showReferenceType && (
                     <NodeIdAutocomplete
                        options={refTypeOptions}
                        value={referenceType}
                        onChange={(v) => { setReferenceType(v); clearError(); }}
                        workspaceId={workspaceId}
                        label={t('typeDetail.referenceType')}
                        recentContext="referenceType"
                     />
                  )}
               </>
            ) : (
               /* NodeId (read-only) */
               <TextField
                  label="NodeId"
                  value={formattedNodeId}
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
               />
            )}

            {/* NodeClass (read-only) */}
            <TextField
               label={t('typeDetail.nodeClass')}
               value={nodeClassLabel[nc] ?? ''}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
            />

            {/* Reference Type (Object/Variable children) — restricted to subtypes of
                HierarchicalReferences. Read-only for overrides/inherited. A clone shows
                it above instead, next to the location it belongs with. */}
            {showReferenceType && !isClone && (
               <NodeIdAutocomplete
                  options={refTypeOptions}
                  value={referenceType}
                  onChange={(v) => { setReferenceType(v); clearError(); }}
                  workspaceId={workspaceId}
                  label={t('typeDetail.referenceType')}
                  readOnly={!referenceTypeEditable}
                  recentContext="referenceType"
               />
            )}

            {/* BrowseName Namespace */}
            {readOnly ? (
               <TextField
                  label={t('typeDetail.browseNameNs')}
                  value={(() => {
                     const opt = browseNameNsOptions.find(o => o.uri === browseNameNs);
                     return opt ? `${opt.label} - ${browseNameNs}` : browseNameNs;
                  })()}
                  fullWidth
                  size="small"
                  slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
               />
            ) : (
               <TextField
                  label={t('typeDetail.browseNameNs')}
                  value={browseNameNs}
                  onChange={(e) => {
                     setBrowseNameNs(e.target.value);
                     // Stop following the target model once the user has had their say.
                     browseNameNsEdited.current = true;
                     clearError();
                  }}
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
            )}

            {/* BrowseName */}
            <TextField
               label={t('typeDetail.browseName')}
               value={browseName}
               onChange={(e) => { setBrowseName(e.target.value); clearError(); }}
               required={!readOnly}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
            />

            {/* DisplayName */}
            <TextField
               label={t('typeDetail.displayName')}
               value={displayName}
               onChange={(e) => { setDisplayName(e.target.value); clearError(); }}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
            />

            {/* TypeDefinition (Object/Variable only) */}
            {showTypeDefinition && (
               <NodeIdAutocomplete
                  options={typeDefOptions}
                  value={typeDefinitionId}
                  onChange={handleTypeDefinitionChange}
                  workspaceId={workspaceId}
                  label={t('typeDetail.typeDefinition')}
                  readOnly={readOnly || designToolOnly}
                  recentContext="typeDefinition"
               />
            )}

            {/* Modelling Rule */}
            {showModellingRule && (
               readOnly ? (
                  <TextField
                     label={t('typeDetail.modellingRule')}
                     value={modellingRuleDisplayName ? t(modellingRuleDisplayName) : ''}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
                  />
               ) : (
                  <TextField
                     label={t('typeDetail.modellingRule')}
                     value={modellingRuleId}
                     onChange={(e) => { setModellingRuleId(e.target.value); clearError(); }}
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
               )
            )}

            {/* Variable-specific fields */}
            {isVariable && (
               <>
                  <NodeIdAutocomplete
                     options={dataTypeOptions}
                     value={dataType}
                     onChange={(v) => { setDataType(v); clearError(); }}
                     workspaceId={workspaceId}
                     label={t('typeDetail.dataType')}
                     readOnly={readOnly}
                     recentContext="dataType"
                  />
                  {readOnly ? (
                     <TextField
                        label={t('typeDetail.valueRank')}
                        value={t(valueRankDisplayName)}
                        fullWidth
                        size="small"
                        slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
                     />
                  ) : (
                     <TextField
                        label={t('typeDetail.valueRank')}
                        value={valueRank}
                        onChange={(e) => { setValueRank(Number(e.target.value)); clearError(); }}
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
                  )}
                  <TextField
                     label={t('typeDetail.arrayDimensions')}
                     value={arrayDimensions}
                     onChange={(e) => { setArrayDimensions(e.target.value); clearError(); }}
                     fullWidth
                     size="small"
                     slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
                  />
               </>
            )}

            {/* Description */}
            <TextField
               label={t('typeDetail.description')}
               value={description}
               onChange={(e) => { setDescription(e.target.value); clearError(); }}
               fullWidth
               size="small"
               multiline
               minRows={3}
               slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
            />

            {/* Conformance Units (top-level Objects) */}
            {showConformanceUnits && (
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
                  slotProps={{ inputLabel: { shrink: true }, input: { readOnly } }}
               />
            )}

            {isClone && (
               <>
                  <FormControlLabel
                     control={
                        <Checkbox
                           size="small"
                           checked={includeChildren}
                           onChange={(e) => setIncludeChildren(e.target.checked)}
                        />
                     }
                     label={t('typeDetail.cloneChildren', 'Clone the children as well')}
                  />
                  <Typography variant="caption" color="text.secondary" sx={{ mt: -4 }}>
                     {t('typeDetail.cloneDescription',
                        'Attributes and values are copied. References outside the containment hierarchy (e.g. interfaces) are not.')}
                  </Typography>
               </>
            )}
         </Box>
      </ModelDialog>
   );
};
