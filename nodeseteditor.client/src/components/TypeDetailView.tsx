import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import api, { ApiError, extractErrorMessage } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import { getNodePlainName } from '../model/Node';
import type { Node as RestNode, ReferenceDescription } from '../model/Node';
import { formatModellingRule, formatValueRank } from '../model/NodeFormatting';
import { extractNamespaceUri, extractBrowseNameNamespace, formatBrowseName, formatNodeId, buildNamespaceMap } from '../utils/formatNodeId';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { WorkspaceContext } from '../WorkspaceContext';
import { useCanWriteWorkspace } from '../hooks/useCurrentWorkspace';

interface DataTypeDefinitionResponse {
   form?: string;
   fields?: Array<{
      name?: string;
      value?: number;
      dataType?: string;
      dataTypeName?: string;
      valueRank?: number;
      arrayDimensions?: string;
      isOptional?: boolean;
      allowSubTypes?: boolean;
      description?: { text?: string };
      isInherited?: boolean;
      sourceTypeNodeId?: string;
   }>;
}

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import IconButton from '@mui/material/IconButton';
import Tabs from '@mui/material/Tabs';
import Tab from '@mui/material/Tab';
import Avatar from '@mui/material/Avatar';
import Tooltip from '@mui/material/Tooltip';
import Breadcrumbs from '@mui/material/Breadcrumbs';
import Link from '@mui/material/Link';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import ArrowForwardIcon from '@mui/icons-material/ArrowForward';
import ArrowBackIosIcon from '@mui/icons-material/ArrowBackIos';
import EditIcon from '@mui/icons-material/Edit';
import AddIcon from '@mui/icons-material/Add';
import DeleteIcon from '@mui/icons-material/Delete';
import KeyboardArrowUpIcon from '@mui/icons-material/KeyboardArrowUp';
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown';
import VisibilityIcon from '@mui/icons-material/Visibility';
import VisibilityOffIcon from '@mui/icons-material/VisibilityOff';
import SubdirectoryArrowRightIcon from '@mui/icons-material/SubdirectoryArrowRight';
import FolderIcon from '@mui/icons-material/Folder';
import DataObjectIcon from '@mui/icons-material/DataObject';
import FunctionsIcon from '@mui/icons-material/Functions';
import LabelIcon from '@mui/icons-material/Label';
import SettingsEthernetIcon from '@mui/icons-material/SettingsEthernet';
import { useTheme } from '@mui/material/styles';
import type { SxProps, Theme } from '@mui/material/styles';

import { StripedTable } from './StripedTable';
import type { StripedTableColumn } from './StripedTable';
import { ModelDialog } from './ModelDialog';
import { ContentLoader } from './ContentLoader';
import { AddFieldDialog } from './AddFieldDialog';
import type { AddFieldData, FieldDialogInitialData } from './AddFieldDialog';
import { EditTypeDialog } from './EditTypeDialog';
import type { CreatedNodeInfo } from './EditTypeDialog';
import { CreateInstanceDialog } from './CreateInstanceDialog';
import { CreateArgumentsDialog } from './CreateArgumentsDialog';
import { AddChildDialog } from './AddChildDialog';
import type { CreateChildData, ChildKind } from './AddChildDialog';
import { AddInterfaceDialog } from './AddInterfaceDialog';
import { EditChildDialog } from './EditChildDialog';
import { InstantiateDialog } from './InstantiateDialog';
import { AddReferenceDialog } from './AddReferenceDialog';
import type { AddReferenceData } from './AddReferenceDialog';
import { NodeIdLink } from './NodeIdLink';
import { DataTypeCell } from './DataTypeCell';
import { ValueEditor } from './ValueEditor';
import { getNodeClassIcon } from './nodeClassIcon';
import {
   autoInstantiateMandatoryChildren,
   autoInstantiateMandatoryDescendants,
   stripNamespace,
} from '../utils/instantiateUtils';
import type { TemplateChildDto } from '../utils/instantiateUtils';
import AccountTreeIcon from '@mui/icons-material/AccountTree';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import CallSplitIcon from '@mui/icons-material/CallSplit';
import PlaylistAddIcon from '@mui/icons-material/PlaylistAdd';
import ListAltIcon from '@mui/icons-material/ListAlt';

const NodeClassValues = {
   UAObject: 1,
   UAVariable: 2,
   UAMethod: 4,
   UAObjectType: 8,
   UAVariableType: 16,
   UAReferenceType: 32,
   UADataType: 64,
} as const;

/** Maps a string NodeClass (as returned by the REST API) to its numeric value. */
const nodeClassToNumber = (nodeClass?: string): number => {
   switch (nodeClass) {
      case 'Object': return NodeClassValues.UAObject;
      case 'Variable': return NodeClassValues.UAVariable;
      case 'Method': return NodeClassValues.UAMethod;
      case 'ObjectType': return NodeClassValues.UAObjectType;
      case 'VariableType': return NodeClassValues.UAVariableType;
      case 'ReferenceType': return NodeClassValues.UAReferenceType;
      case 'DataType': return NodeClassValues.UADataType;
      default: return 0;
   }
};

interface NodeAttributeDto {
   name: string;
   value?: string | null;
   /** Raw NodeId for attributes that link to another node */
   nodeId?: string | null;
   /** NodeClass of the link target */
   nodeClass?: number;
}

interface NodeChildDto {
   nodeId?: string;
   displayName?: string;
   nodeClass: number;
   referenceType?: string;
   referenceTypeId?: string;
   typeDefinition?: string;
   dataType?: string;
   modellingRule?: string;
   typeDefinitionId?: string;
   dataTypeId?: string;
   modellingRuleId?: string;
   valueRank?: number | null;
   browseNameRaw?: string;
   isInherited?: boolean;
   sourceTypeNodeId?: string;
   isOverride?: boolean;
}

interface NodeReferenceDto {
   referenceType?: string;
   referenceTypeId?: string;
   targetDisplayName?: string;
   isForward: boolean;
   targetNodeId?: string;
   targetNodeClass?: number;
   isCanonicalParent?: boolean;
}

interface DataTypeFieldDto {
   name: string;
   value?: number | null;
   dataType?: string | null;
   dataTypeId?: string | null;
   dataTypeName?: string | null;
   valueRank?: number | null;
   arrayDimensions?: string | null;
   isOptional: boolean;
   allowSubTypes: boolean;
   description?: string | null;
   isInherited: boolean;
   sourceTypeName?: string | null;
}

interface TypeDetailViewProps {
   nodeId: string;
   displayName: string;
   nodeClass: number;
   isEditable: boolean;
   onBack: () => void;
   workspaceId: string;
   initialTab?: string;
   onTabChange?: (tab: string) => void;
}

interface BreadcrumbNode {
   nodeId: string;
   displayName: string;
   nodeClass: number;
   isEditable: boolean;
   typeDefinitionId?: string;
}

type TabId = 'attributes' | 'fields' | 'value' | 'children' | 'references';

/** Strip a leading "[Model]:" or bare "Model:" prefix added by formatBrowseName. */
function stripModelPrefix(value: string): string {
   const bracket = value.match(/^\[[^\]]+\]:(.+)$/);
   if (bracket) return bracket[1];
   const colon = value.indexOf(':');
   if (colon > 0) return value.substring(colon + 1);
   return value;
}

/** Tooltip label + glyph for each per-NodeClass child-create button. */
const CHILD_KIND_META: Record<ChildKind, { labelKey: string; icon: React.ReactElement }> = {
   object: { labelKey: 'typeDetail.createObject', icon: <FolderIcon /> },
   datavariable: { labelKey: 'typeDetail.createDataVariable', icon: <DataObjectIcon /> },
   property: { labelKey: 'typeDetail.createProperty', icon: <LabelIcon /> },
   method: { labelKey: 'typeDetail.createMethod', icon: <FunctionsIcon /> },
};

export const TypeDetailView: React.FC<TypeDetailViewProps> = ({
   nodeId,
   displayName,
   nodeClass,
   isEditable,
   onBack,
   workspaceId,
   initialTab,
   onTabChange,
}) => {
   const { t } = useTranslation();
   const theme = useTheme();
   const queryClient = useQueryClient();
   const { setSelectedType, setNavigateToNode } = React.useContext(WorkspaceContext);
   const validTabs: TabId[] = ['attributes', 'fields', 'value', 'children', 'references'];
   // The tab the user picked, which is sticky across node selections and may name a
   // tab the newly selected node doesn't have. Read `activeTab` below, never this.
   const [selectedTab, setSelectedTabRaw] = React.useState<TabId>(
      initialTab && validTabs.includes(initialTab as TabId) ? initialTab as TabId : 'attributes'
   );
   const setActiveTab = React.useCallback((tab: TabId) => {
      setSelectedTabRaw(tab);
      onTabChange?.(tab);
   }, [onTabChange]);

   // Drill-down navigation stack
   const [drillStack, setDrillStack] = React.useState<BreadcrumbNode[]>([]);

   // Write permission for the selected workspace. Owner-only: when the
   // workspace is shared with the user (read-only) this is false, which
   // collapses every edit affordance below via activeIsEditable.
   const canWrite = useCanWriteWorkspace();

   // Derive active node from drill stack or props
   const activeNodeId = drillStack.length > 0 ? drillStack[drillStack.length - 1].nodeId : nodeId;
   const activeNodeClass = drillStack.length > 0 ? drillStack[drillStack.length - 1].nodeClass : nodeClass;
   // A node is editable only when its model allows it AND the user can write
   // to the workspace. Gating here cascades to the add/edit/delete buttons,
   // child-create buttons, row actions, and the dialogs' isEditable prop.
   const activeIsEditable = canWrite
      && (drillStack.length > 0 ? drillStack[drillStack.length - 1].isEditable : isEditable);
   const activeTypeDefId = drillStack.length > 0 ? drillStack[drillStack.length - 1].typeDefinitionId : undefined;
   const isDrilledDown = drillStack.length > 0;

   // Namespace map for formatting BrowseNames with model prefixes
   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) }
         });
         return response.data;
      },
   });
   const nsMap = React.useMemo(
      () => buildNamespaceMap(namespacesData?.results ?? []),
      [namespacesData],
   );

   // Reset drill stack when top-level nodeId changes
   React.useEffect(() => {
      setDrillStack([]);
      setSelectedTabRaw(
         initialTab && validTabs.includes(initialTab as TabId) ? initialTab as TabId : 'attributes'
      );
   }, [nodeId]); // eslint-disable-line react-hooks/exhaustive-deps

   // Field dialog state
   const [fieldDialogOpen, setFieldDialogOpen] = React.useState(false);
   const [fieldDialogMode, setFieldDialogMode] = React.useState<'add' | 'edit' | 'view'>('add');
   const [fieldDialogInitial, setFieldDialogInitial] = React.useState<FieldDialogInitialData | undefined>(undefined);
   const [editingFieldName, setEditingFieldName] = React.useState<string | null>(null);
   const [isAddingField, setIsAddingField] = React.useState(false);
   const [addFieldError, setAddFieldError] = React.useState<string | null>(null);

   // Edit DataType dialog state
   const [editDataTypeOpen, setEditDataTypeOpen] = React.useState(false);

   // Extend type dialog state
   const [extendTypeOpen, setExtendTypeOpen] = React.useState(false);

   // Create instance dialog state
   const [createInstanceOpen, setCreateInstanceOpen] = React.useState(false);

   // Edit method arguments dialog state
   const [argumentsOpen, setArgumentsOpen] = React.useState(false);

   // Child dialog state
   const [childDialogOpen, setChildDialogOpen] = React.useState(false);
   const [childDialogKind, setChildDialogKind] = React.useState<ChildKind | undefined>(undefined);
   const [isAddingChild, setIsAddingChild] = React.useState(false);
   const [addChildError, setAddChildError] = React.useState<string | null>(null);

   // Add Interface dialog state
   const [addInterfaceOpen, setAddInterfaceOpen] = React.useState(false);

   // Delete-node confirmation state
   const [deleteConfirmOpen, setDeleteConfirmOpen] = React.useState(false);
   const [isDeletingNode, setIsDeletingNode] = React.useState(false);
   const [deleteNodeError, setDeleteNodeError] = React.useState<string | null>(null);

   // Generic delete confirmation for the inline field / child / reference deletes (the top-level
   // node delete has its own dialog). Holds the prompt text and the action to run on confirm.
   const [confirmDelete, setConfirmDelete] = React.useState<{ message: string; run: () => Promise<void> } | null>(null);
   const [confirmDeleting, setConfirmDeleting] = React.useState(false);
   const [confirmDeleteError, setConfirmDeleteError] = React.useState<string | null>(null);

   const requestDelete = (message: string, run: () => Promise<void>) => {
      setConfirmDeleteError(null);
      setConfirmDelete({ message, run });
   };

   const handleConfirmDelete = async () => {
      if (!confirmDelete) return;
      setConfirmDeleting(true);
      setConfirmDeleteError(null);
      try {
         await confirmDelete.run();
         setConfirmDelete(null);
      } catch (e) {
         setConfirmDeleteError(e instanceof Error ? e.message : t('typeDetail.deleteFailed', 'Delete failed'));
      } finally {
         setConfirmDeleting(false);
      }
   };

   // Edit child dialog state
   const [editChildOpen, setEditChildOpen] = React.useState(false);
   const [editChildNodeId, setEditChildNodeId] = React.useState('');
   const [editChildReadOnly, setEditChildReadOnly] = React.useState(false);
   const [editChildRefTypeId, setEditChildRefTypeId] = React.useState<string | undefined>(undefined);
   const [editChildRefEditable, setEditChildRefEditable] = React.useState(false);

   // Instantiate dialog state
   const [instantiateDialogOpen, setInstantiateDialogOpen] = React.useState(false);
   const [instantiateTargetNodeId, setInstantiateTargetNodeId] = React.useState('');
   const [instantiateModelUri, setInstantiateModelUri] = React.useState('');

   // Children filter state
   const [hideInherited, setHideInherited] = React.useState(false);
   const [hideInheritedFields, setHideInheritedFields] = React.useState(false);
   // References view filter: hide the structural parent/child links (both directions) — i.e. the
   // references derived from ParentNodeId where this node is the parent or the child.
   const [hideParentChildRefs, setHideParentChildRefs] = React.useState(false);

   // Add reference dialog state
   const [refDialogOpen, setRefDialogOpen] = React.useState(false);
   const [isAddingRef, setIsAddingRef] = React.useState(false);
   const [addRefError, setAddRefError] = React.useState<string | null>(null);

   const isDataType = activeNodeClass === NodeClassValues.UADataType;

   const { data: attributesData, isLoading: attrsLoading, isError: attrsError, error: attrsErrorObj } = useQuery({
      queryKey: ['nodeAttributes', workspaceId, activeNodeId],
      queryFn: async () => {
         const response = await api.get<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         // Convert Node to name/value attribute pairs for the table
         const n = response.data;
         const raw = n;
         const attrs: NodeAttributeDto[] = [];
         const add = (name: string, rawValue?: unknown, nodeId?: string | null, nc?: number) => {
            // Coerce LocalizedText objects to string
            const value = typeof rawValue === 'object' && rawValue !== null
               ? (rawValue as { text?: string }).text ?? ''
               : rawValue as string | null | undefined;
            if (value) attrs.push({ name, value, nodeId, nodeClass: nc });
         };
         const fmt = (bn?: string) => formatBrowseName(bn, nsMap);
         // NodeId and BrowseName can live in different namespaces — e.g. an
         // optional child created from AnalogItemType (Core) under a parent
         // in a private namespace keeps the Core BrowseName but takes the
         // parent's NodeId namespace. Derive the prefix from the NodeId's
         // own nsu= URI, not from the BrowseName, otherwise the Attributes
         // view shows the BrowseName's namespace on the NodeId.
         add('NodeId', formatNodeId(n.nodeId, nsMap));
         add('NodeClass', n.nodeClass);
         add('BrowseName', fmt(n.browseName) || n.browseName);
         add('DisplayName', getNodePlainName(n));
         add('Description', n.description?.text);
         add('IsAbstract', n.isAbstract ? 'True' : 'False');
         if (n.nodeClass === 'ReferenceType') {
            // Symmetric is a two-state attribute like IsAbstract, so send the word
            // 'False' rather than an empty string — add() drops falsy values, and
            // the edit dialog reads its initial state back out of these strings.
            add('Symmetric', n.symmetric ? 'True' : 'False');
            add('InverseName', n.inverseName?.text);
         }
         // Resolve supertype display name
         if (n.superTypeId) {
            try {
               const stResponse = await api.get<RestNode>(
                  `/opcua/v1/nodes/${slugifyNodeId(n.superTypeId)}`,
                  { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
               );
               const stName = fmt(stResponse.data.browseName) || getNodePlainName(stResponse.data) || n.superTypeId;
               const stNodeClass = stResponse.data.nodeClass;
               const stNc = stNodeClass === 'ObjectType' ? 8 : stNodeClass === 'VariableType' ? 16
                  : stNodeClass === 'DataType' ? 64 : stNodeClass === 'ReferenceType' ? 32 : 0;
               add('SuperType', stName, n.superTypeId, stNc);
            } catch {
               add('SuperType', n.superTypeId, n.superTypeId, nodeClass);
            }
         }
         add('TypeDefinition', fmt(n.typeDefinitionName) || n.typeDefinition,
            n.typeDefinition, NodeClassValues.UAObjectType);
         add('ModellingRule', formatModellingRule(n.modellingRule));
         // Parent link, only meaningful for instance node classes (Object,
         // Variable, Method). For types the SuperType row already conveys the
         // hierarchy. We resolve the parent's BrowseName so the user sees
         // [prefix]:BrowseName rather than the raw NodeId.
         const isInstance = n.nodeClass === 'Object'
            || n.nodeClass === 'Variable'
            || n.nodeClass === 'Method';
         if (isInstance && n.parentNodeId) {
            try {
               const pResponse = await api.get<RestNode>(
                  `/opcua/v1/nodes/${slugifyNodeId(n.parentNodeId)}`,
                  { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
               );
               const pName = fmt(pResponse.data.browseName) || getNodePlainName(pResponse.data) || n.parentNodeId;
               const pNodeClass = pResponse.data.nodeClass;
               const pNc = pNodeClass === 'Object' ? 1
                  : pNodeClass === 'Variable' ? 2
                  : pNodeClass === 'Method' ? 4
                  : pNodeClass === 'ObjectType' ? 8
                  : pNodeClass === 'VariableType' ? 16
                  : pNodeClass === 'DataType' ? 64
                  : pNodeClass === 'ReferenceType' ? 32
                  : 0;
               add('ParentNode', pName, n.parentNodeId, pNc);
            } catch {
               add('ParentNode', n.parentNodeId, n.parentNodeId, 0);
            }
         }
         add('DataType', fmt(n.dataTypeName) || n.dataType,
            n.dataType, NodeClassValues.UADataType);
         if (n.valueRank !== undefined && n.valueRank !== null) add('ValueRank', String(n.valueRank));
         add('ArrayDimensions', n.arrayDimensions?.join(','));
         add('DataTypeForm', n.dataTypeForm);
         // The edit dialogs take the units as an array (see EditTypeDialog); this row
         // is display only, so the list is flattened onto one line. Semicolons, not
         // commas — a conformance unit name may contain a comma.
         add('ConformanceUnits', n.category?.join('; '));
         return { attrs, raw };
      },
      enabled: true,
   });

   // Derive DataTypeForm from attributes (needed early for tab visibility)
   const dataTypeForm = React.useMemo(() => {
      const attrs = attributesData?.attrs ?? [];
      const attrMap = new Map(attrs.map((a) => [a.name, a.value ?? '']));
      return attrMap.get('DataTypeForm') ?? '';
   }, [attributesData]);

   // Only show Fields tab for DataTypes that support fields (Structure, Union, Enumeration, OptionSet)
   const showFieldsTab = isDataType && !!dataTypeForm;

   // Show Value tab for Variables and VariableTypes (anything that carries a Value attribute).
   const showValueTab = activeNodeClass === NodeClassValues.UAVariable
      || activeNodeClass === NodeClassValues.UAVariableType;

   // If the sticky tab is one the active node doesn't have, fall back to attributes for
   // DISPLAY only — `selectedTab` keeps the user's preference, so selecting another
   // Variable returns to Value. Only an explicit tab click changes the preference.
   //
   // Derived during render rather than corrected in an effect: an effect only fires after
   // MUI has already been handed a `value` with no matching Tab, and it logs "None of the
   // Tabs' children match with …" for that frame. Selecting an Object right after editing
   // a Variable's value hit exactly that.
   const activeTab: TabId =
      (selectedTab === 'fields' && !showFieldsTab) || (selectedTab === 'value' && !showValueTab)
         ? 'attributes'
         : selectedTab;

   const { data: fieldsData, isLoading: fieldsLoading, isError: fieldsError, error: fieldsErrorObj } = useQuery({
      queryKey: ['nodeDataTypeFields', workspaceId, activeNodeId],
      queryFn: async () => {
         const response = await api.get<DataTypeDefinitionResponse>(
            `/opcua/v1/types/data-types/${slugifyNodeId(activeNodeId)}/definition`,
            {
               params: { full: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.fields ?? []).map(f => ({
            name: f.name ?? '',
            value: f.value,
            dataType: formatBrowseName(f.dataTypeName ?? undefined, nsMap) || f.dataType,
            dataTypeId: f.dataType,
            dataTypeName: f.dataTypeName,
            valueRank: f.valueRank,
            arrayDimensions: f.arrayDimensions,
            isOptional: f.isOptional ?? false,
            allowSubTypes: f.allowSubTypes ?? false,
            description: f.description?.text,
            isInherited: f.isInherited ?? false,
            sourceTypeName: f.sourceTypeNodeId,
         })) as DataTypeFieldDto[];
      },
      enabled: showFieldsTab && activeTab === 'fields',
   });

   const { data: childrenData, isLoading: childrenLoading, isError: childrenError, error: childrenErrorObj } = useQuery({
      queryKey: ['nodeChildren', workspaceId, activeNodeId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}/children`,
            {
               params: { full: true },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId,
            displayName: formatBrowseName(n.browseName, nsMap) || n.displayName?.text,
            nodeClass: n.nodeClass === 'Object' ? 1 : n.nodeClass === 'Variable' ? 2 : n.nodeClass === 'Method' ? 4 : 0,
            referenceType: formatBrowseName(n.referenceType, nsMap) || n.referenceType,
            referenceTypeId: n.referenceTypeId,
            typeDefinition: formatBrowseName(n.typeDefinitionName, nsMap) || n.typeDefinition,
            dataType: formatBrowseName(n.dataTypeName, nsMap) || n.dataType,
            modellingRule: n.modellingRule,
            typeDefinitionId: n.typeDefinition,
            dataTypeId: n.dataType,
            modellingRuleId: undefined,
            valueRank: n.valueRank,
            isInherited: n.isInherited ?? false,
            sourceTypeNodeId: n.sourceTypeNodeId,
            isOverride: n.isOverride ?? false,
         })) as NodeChildDto[];
      },
      enabled: activeTab === 'children',
   });

   // When drilled down, fetch the type definition's own (non-inherited)
   // declarations so we can flag type-derived rows as view-only. This
   // intentionally uses a different cache key from the InstantiateDialog
   // (which fetches the same URL with full=true to include inherited
   // declarations) — sharing keys was causing the dialog to render the
   // own-children-only response and hide every Optional inherited child.
   const { data: typeTemplateChildren } = useQuery({
      queryKey: ['typeOwnChildren', workspaceId, activeTypeDefId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/nodes/${slugifyNodeId(activeTypeDefId ?? '')}/children`,
            {
               params: { depth: 1, count: 10000 },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         return (response.data.results ?? []) as unknown as TemplateChildDto[];
      },
      enabled: isDrilledDown && !!activeTypeDefId && activeTab === 'children',
   });

   const typeDerivedNames = React.useMemo(() => {
      const names = new Set<string>();
      if (!isDrilledDown || !typeTemplateChildren) return names;
      for (const tc of typeTemplateChildren) {
         names.add(stripNamespace(tc.browseName ?? '').toLowerCase());
      }
      return names;
   }, [isDrilledDown, typeTemplateChildren]);

   const { data: refsData, isLoading: refsLoading, isError: refsError, error: refsErrorObj } = useQuery({
      queryKey: ['nodeReferences', workspaceId, activeNodeId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<ReferenceDescription>>(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}/references`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         return (response.data.results ?? []).map(r => ({
            referenceType: formatBrowseName(r.referenceTypeName, nsMap) || r.referenceTypeName,
            referenceTypeId: r.referenceTypeId,
            targetDisplayName: formatBrowseName(r.targetBrowseName, nsMap)
               || (r.targetDisplayName?.text ?? r.targetNodeId),
            isForward: r.isForward,
            targetNodeId: r.targetNodeId,
            targetNodeClass: nodeClassToNumber(r.targetNodeClass),
            isCanonicalParent: r.isCanonicalParent,
         })) as NodeReferenceDto[];
      },
      enabled: activeTab === 'references',
   });

   // Add/Edit Field handler
   const handleSaveField = async (data: AddFieldData) => {
      setIsAddingField(true);
      setAddFieldError(null);

      try {
         // Build updated field list: remove old field if editing, add new field. Enum fields omit
         // dataType/valueRank/arrayDimensions/optional flags, so those are optional in the payload.
         type FieldPayload = {
            name: string;
            value?: number | null;
            dataType?: string;
            valueRank?: number | null;
            arrayDimensions?: string | null;
            description?: { text: string };
            isOptional?: boolean;
            allowSubTypes?: boolean;
         };
         const currentFields = (fieldsData ?? []).filter(f => !f.isInherited);
         let updatedFields: FieldPayload[] = currentFields.map(f => ({
            name: f.name, value: f.value, dataType: f.dataTypeId ?? undefined,
            valueRank: f.valueRank, arrayDimensions: f.arrayDimensions,
            description: f.description ? { text: f.description ?? '' } : undefined,
            isOptional: f.isOptional, allowSubTypes: f.allowSubTypes,
         }));

         if (fieldDialogMode === 'edit' && editingFieldName) {
            updatedFields = updatedFields.filter(f => f.name !== editingFieldName);
         }

         // Enumeration and OptionSet fields carry only Name + number + Description — an
         // enumeration value in the first case, a bit position in the second. DataType /
         // ValueRank / ArrayDimensions don't apply to either and are left off.
         const isValueOnly = dataTypeForm === 'Enumeration' || dataTypeForm === 'OptionSet';
         updatedFields.push({
            name: data.name,
            value: isValueOnly ? data.value : undefined,
            dataType: isValueOnly ? undefined : (data.dataTypeId || undefined),
            valueRank: isValueOnly ? undefined : data.valueRank,
            arrayDimensions: isValueOnly ? undefined : (data.arrayDimensions || undefined),
            description: data.description ? { text: data.description } : undefined,
            isOptional: isValueOnly ? undefined : (data.isOptional ?? false),
            allowSubTypes: isValueOnly ? undefined : (data.allowSubTypes ?? false),
         });

         await api.put(
            `/opcua/v1/types/data-types/${slugifyNodeId(activeNodeId)}/definition`,
            { fields: updatedFields },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );

         setFieldDialogOpen(false);
         queryClient.invalidateQueries({ queryKey: ['nodeDataTypeFields', workspaceId, activeNodeId] });
      } catch (e) {
         const errorMessage = e instanceof ApiError ? e.message : (e instanceof Error ? e.message : 'Failed to save field');
         setAddFieldError(errorMessage);
      } finally {
         setIsAddingField(false);
      }
   };

   // Delete Field handler
   const handleDeleteField = async (fieldName: string) => {
      const currentFields = (fieldsData ?? []).filter(f => !f.isInherited);
      const updatedFields = currentFields
         .filter(f => f.name !== fieldName)
         .map(f => ({
            name: f.name, value: f.value, dataType: f.dataTypeId ?? undefined,
            valueRank: f.valueRank, arrayDimensions: f.arrayDimensions,
            description: f.description ? { text: f.description } : undefined,
            isOptional: f.isOptional, allowSubTypes: f.allowSubTypes,
         }));

      await api.put(
         `/opcua/v1/types/data-types/${slugifyNodeId(activeNodeId)}/definition`,
         { fields: updatedFields },
         { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
      );

      queryClient.invalidateQueries({ queryKey: ['nodeDataTypeFields', workspaceId, activeNodeId] });
   };

   // Move an (own, non-inherited) field up or down in the definition order. Inherited fields come
   // from the base type and keep their position; only the local fields are reordered and persisted.
   const [isReorderingField, setIsReorderingField] = React.useState(false);

   const handleMoveField = async (fieldName: string, direction: 'up' | 'down') => {
      const own = (fieldsData ?? []).filter(f => !f.isInherited);
      const idx = own.findIndex(f => f.name === fieldName);
      const swapWith = direction === 'up' ? idx - 1 : idx + 1;
      if (idx < 0 || swapWith < 0 || swapWith >= own.length) return;

      const reordered = [...own];
      [reordered[idx], reordered[swapWith]] = [reordered[swapWith], reordered[idx]];

      const updatedFields = reordered.map(f => ({
         name: f.name, value: f.value, dataType: f.dataTypeId ?? undefined,
         valueRank: f.valueRank, arrayDimensions: f.arrayDimensions,
         description: f.description ? { text: f.description } : undefined,
         isOptional: f.isOptional, allowSubTypes: f.allowSubTypes,
      }));

      setIsReorderingField(true);
      try {
         await api.put(
            `/opcua/v1/types/data-types/${slugifyNodeId(activeNodeId)}/definition`,
            { fields: updatedFields },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         queryClient.invalidateQueries({ queryKey: ['nodeDataTypeFields', workspaceId, activeNodeId] });
      } finally {
         setIsReorderingField(false);
      }
   };

   // Open field dialog in a given mode
   const openFieldDialog = (mode: 'add' | 'edit' | 'view', field?: DataTypeFieldDto) => {
      setFieldDialogMode(mode);
      setAddFieldError(null);
      if (field) {
         setEditingFieldName(field.name);
         setFieldDialogInitial({
            name: field.name,
            dataTypeName: field.dataType ?? '',
            dataTypeId: field.dataTypeId ?? undefined,
            valueRank: field.valueRank ?? -1,
            arrayDimensions: field.arrayDimensions ?? '',
            description: field.description ?? '',
            isOptional: field.isOptional,
            allowSubTypes: field.allowSubTypes,
            value: field.value ?? undefined,
         });
      } else {
         setEditingFieldName(null);
         setFieldDialogInitial(undefined);
      }
      setFieldDialogOpen(true);
   };

   // Add/Delete Child handlers
   const handleSaveChild = async (data: CreateChildData) => {
      setIsAddingChild(true);
      setAddChildError(null);
      try {
         const ncMap: Record<number, string> = { 1: 'Object', 2: 'Variable', 4: 'Method' };
         const response = await api.post<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}/children`,
            {
               modelUri: data.modelUri,
               browseNameModelUri: data.browseNameModelUri,
               nodeClass: ncMap[data.nodeClass] ?? 'Object',
               browseName: data.browseName,
               displayName: data.displayName,
               description: data.description,
               referenceTypeId: data.referenceTypeId,
               typeDefinitionId: data.typeDefinitionId,
               modellingRuleId: data.modellingRuleId,
               dataType: data.dataType,
               valueRank: data.valueRank,
               arrayDimensions: data.arrayDimensions,
            },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );

         // Auto-instantiate mandatory children for Object/Variable with a TypeDefinition
         const createdNodeId = response.data?.nodeId;
         if ((data.nodeClass === 1 || data.nodeClass === 2) && data.typeDefinitionId && createdNodeId) {
            try {
               await autoInstantiateMandatoryChildren(
                  workspaceId, createdNodeId, data.typeDefinitionId, data.modelUri);
            } catch (e) {
               console.warn('Auto-instantiation failed:', e);
            }
         }

         setChildDialogOpen(false);
         queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
         queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
         queryClient.invalidateQueries({ queryKey: ['subtypes'] });
      } catch (e) {
         setAddChildError(extractErrorMessage(e, 'Failed to create child'));
      } finally {
         setIsAddingChild(false);
      }
   };

   const handleDeleteChild = async (childNodeId: string) => {
      await api.delete(
         `/opcua/v1/nodes/${slugifyNodeId(childNodeId)}`,
         { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
      );
      queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['subtypes'] });
   };

   const handleEditChild = (
      childNodeId: string,
      viewOnly = false,
      refInfo?: { referenceTypeId?: string; canEditReferenceType?: boolean },
   ) => {
      setEditChildNodeId(childNodeId);
      setEditChildReadOnly(viewOnly);
      setEditChildRefTypeId(refInfo?.referenceTypeId);
      setEditChildRefEditable(refInfo?.canEditReferenceType ?? false);
      setEditChildOpen(true);
   };

   const handleChildSaved = () => {
      queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['subtypes'] });
      // EditChildDialog also serves as the editor for the active node itself
      // (instance edit), so refresh its attributes/header too.
      queryClient.invalidateQueries({ queryKey: ['nodeAttributes', workspaceId, editChildNodeId] });
      queryClient.invalidateQueries({ queryKey: ['childNodeDetails', workspaceId, editChildNodeId] });
   };

   const handleOverrideChild = async (child: NodeChildDto) => {
      if (!child.nodeId) return;
      try {
         // Fetch the inherited child's full attributes
         const response = await api.get<RestNode>(
            `/opcua/v1/nodes/${slugifyNodeId(child.nodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         const src = response.data;
         const ncMap: Record<number, string> = { 1: 'Object', 2: 'Variable', 4: 'Method' };

         // Determine model URI from current node's raw nodeId
         const modelUri = extractNamespaceUri(activeNodeId);

         // Find the reference type used by the source type to connect this child
         // by browsing the source type's references
         let referenceTypeId = 'i=47'; // default HasComponent
         if (child.sourceTypeNodeId) {
            try {
               const refsResponse = await api.get<PaginatedResponse<ReferenceDescription>>(
                  `/opcua/v1/nodes/${slugifyNodeId(child.sourceTypeNodeId)}/references`,
                  { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
               );
               const matchingRef = (refsResponse.data.results ?? []).find(
                  r => r.isForward && r.targetNodeId === child.nodeId
               );
               if (matchingRef) referenceTypeId = matchingRef.referenceTypeId;
            } catch {
               // fall back to default
            }
         }

         const createResponse = await api.post(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}/children`,
            {
               modelUri,
               nodeClass: ncMap[child.nodeClass] ?? src.nodeClass,
               browseName: getNodePlainName(src),
               // The override must keep the inherited property's BrowseName
               // namespace (e.g. ns0 for AnalogItemType.Definition), which is
               // independent of where the source node's NodeId lives. Derive it
               // from the source BrowseName, not its NodeId.
               browseNameModelUri: extractBrowseNameNamespace(src.browseName),
               displayName: getNodePlainName(src),
               description: src.description?.text,
               referenceTypeId,
               typeDefinitionId: src.typeDefinition,
               modellingRuleId: src.modellingRule,
               dataType: src.dataType,
               valueRank: src.valueRank,
               arrayDimensions: src.arrayDimensions?.join(','),
               // The declaration being overridden: a Variable override keeps the
               // default Value it inherits instead of starting out empty.
               sourceNodeId: child.nodeId,
            },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         console.log('Override child created:', createResponse.data?.nodeId);

         // An override re-declares only the child itself. Its mandatory descendants
         // still have to be materialized, recursively — otherwise the Instantiate
         // Children dialog shows them checked + greyed (mandatory ⇒ already present)
         // while nothing exists under the override. Drive this from the inherited
         // declaration being overridden, which is what carries those descendants.
         const overrideNodeId = createResponse.data?.nodeId;
         if (overrideNodeId) {
            try {
               await autoInstantiateMandatoryDescendants(
                  workspaceId, overrideNodeId, child.nodeId, modelUri);
            } catch (e) {
               console.warn('Override created but mandatory children not populated:', e);
            }
         }

         queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
         queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
         queryClient.invalidateQueries({ queryKey: ['subtypes'] });
      } catch (e) {
         console.error('Failed to override child:', e);
      }
   };

   const handleInstantiate = (child: NodeChildDto) => {
      if (!child.nodeId || !child.typeDefinitionId) return;
      setInstantiateTargetNodeId(child.nodeId);
      // Extract modelUri from child nodeId
      const nsuMatch = child.nodeId.match(/^nsu=([^;]+);/);
      setInstantiateModelUri(nsuMatch ? nsuMatch[1] : '');
      setInstantiateDialogOpen(true);
   };

   // Instantiate the declarations that apply to the CURRENT node.
   const handleInstantiateNode = () => {
      const typeDef = attributesData?.raw?.typeDefinition;
      if (!typeDef) return;
      setInstantiateTargetNodeId(activeNodeId);
      const nsuMatch = activeNodeId.match(/^nsu=([^;]+);/);
      setInstantiateModelUri(nsuMatch ? nsuMatch[1] : '');
      setInstantiateDialogOpen(true);
   };

   const handleInstantiateComplete = async () => {
      await queryClient.resetQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
      await queryClient.resetQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
      await queryClient.resetQueries({ queryKey: ['instantiateExistingChildren', workspaceId, activeNodeId] });
      if (instantiateTargetNodeId !== activeNodeId) {
         await queryClient.resetQueries({ queryKey: ['nodeChildren', workspaceId, instantiateTargetNodeId] });
         await queryClient.resetQueries({ queryKey: ['nodeReferences', workspaceId, instantiateTargetNodeId] });
         await queryClient.resetQueries({ queryKey: ['instantiateExistingChildren', workspaceId, instantiateTargetNodeId] });
      }
      queryClient.invalidateQueries({ queryKey: ['subtypes'] });
   };

   // Drill-down into a child node
   const handleDrillDown = (child: NodeChildDto) => {
      if (!child.nodeId) return;
      const childNs = child.nodeId.match(/^nsu=([^;]+);/)?.[1] ?? '';
      const parentNs = activeNodeId.match(/^nsu=([^;]+);/)?.[1] ?? '';
      const childEditable = childNs === parentNs && activeIsEditable;

      setDrillStack(prev => [...prev, {
         nodeId: child.nodeId!,
         displayName: child.displayName ?? '',
         nodeClass: child.nodeClass,
         isEditable: childEditable,
         typeDefinitionId: child.typeDefinitionId,
      }]);
   };

   // Back button: pop drill stack or go back to type library
   const handleBackClick = () => {
      if (drillStack.length > 0) {
         setDrillStack(prev => prev.slice(0, -1));
      } else {
         onBack();
      }
   };

   // "+" button click handler. Children get their own per-NodeClass create
   // buttons (see openCreateChild); the generic "+" only serves fields and
   // references now.
   const handleAddClick = () => {
      if (activeTab === 'fields') {
         openFieldDialog('add');
      } else if (activeTab === 'references') {
         setAddRefError(null);
         setRefDialogOpen(true);
      }
   };

   // Open the Add Child dialog pre-configured for a specific NodeClass kind.
   const openCreateChild = (kind: ChildKind) => {
      setAddChildError(null);
      setChildDialogKind(kind);
      setChildDialogOpen(true);
   };

   const handleDeleteNode = async () => {
      setIsDeletingNode(true);
      setDeleteNodeError(null);
      try {
         await api.delete(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         setDeleteConfirmOpen(false);
         queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
         queryClient.invalidateQueries({ queryKey: ['subtypes'] });
         queryClient.invalidateQueries({ queryKey: ['nodeChildren'] });
         // Leave the now-deleted node: pop the drill stack, or return to the
         // type library if we're at the top level.
         if (drillStack.length > 0) {
            setDrillStack(prev => prev.slice(0, -1));
         } else {
            onBack();
         }
      } catch (e) {
         setDeleteNodeError(extractErrorMessage(e, 'Failed to delete node'));
      } finally {
         setIsDeletingNode(false);
      }
   };

   // Design-tool-only nodes take no children and no new references; Properties
   // (Variables typed PropertyType) may not be the source of hierarchical
   // references, so they take no children. The backend enforces both; here we
   // hide the "+" actions and explain why on the Children/References tabs.
   const activeDesignToolOnly = attributesData?.raw?.designToolOnly === true;
   const activeIsProperty = attributesData?.raw?.isProperty === true;
   const childrenLocked = activeDesignToolOnly || activeIsProperty;
   const referencesLocked = activeDesignToolOnly;

   // The "+" button adds a field/reference depending on the active tab.
   // Hidden on the References tab for design-tool-only nodes.
   const showAddButton = activeIsEditable
      && (activeTab === 'fields' || (activeTab === 'references' && !referencesLocked));

   // Per-NodeClass child-create buttons (children tab, private nodes).
   const childCreateKinds: ChildKind[] =
      (activeNodeClass === NodeClassValues.UAObject || activeNodeClass === NodeClassValues.UAObjectType)
         ? ['object', 'datavariable', 'property', 'method']
      : (activeNodeClass === NodeClassValues.UAVariable || activeNodeClass === NodeClassValues.UAVariableType)
         ? ['datavariable', 'property']
      : ['property'];
   const showAddInterface = activeNodeClass === NodeClassValues.UAObject
      || activeNodeClass === NodeClassValues.UAObjectType;
   const showChildCreateButtons = activeIsEditable && activeTab === 'children' && !childrenLocked;
   // Instantiate-children button: an instance Object/Variable with a TypeDefinition, on the
   // children tab. Rendered right after Add Interface so it sits next to it for Objects and at
   // the end for Variables (which have no Add Interface).
   const showInstantiateChildren = activeIsEditable && activeTab === 'children'
      && (activeNodeClass === NodeClassValues.UAObject || activeNodeClass === NodeClassValues.UAVariable)
      && !!attributesData?.raw?.typeDefinition
      && !childrenLocked;

   const isTypeNode = activeNodeClass === NodeClassValues.UAObjectType
      || activeNodeClass === NodeClassValues.UAVariableType
      || activeNodeClass === NodeClassValues.UAReferenceType
      || activeNodeClass === NodeClassValues.UADataType;
   const isInstanceNode = activeNodeClass === NodeClassValues.UAObject
      || activeNodeClass === NodeClassValues.UAVariable
      || activeNodeClass === NodeClassValues.UAMethod;
   // Edit (attributes tab, private): types use EditTypeDialog; instances reuse
   // EditChildDialog (it edits any node by NodeId).
   const showEditButton = activeIsEditable && activeTab === 'attributes'
      && (isTypeNode || isInstanceNode);
   // Delete is available for every private node on the attributes tab.
   const showDeleteButton = activeIsEditable && activeTab === 'attributes';

   const handleEditClick = () => {
      if (isTypeNode) {
         setEditDataTypeOpen(true);
      } else {
         handleEditChild(activeNodeId, false);
      }
   };

   // Show Extend Type button for the top-level type (not when drilled into a child),
   // and only on the Attributes tab where type-level actions are contextually relevant.
   // Extend/Instantiate create new nodes in the current workspace, so they
   // require write access just like the in-place edit actions.
   const isExtendableType = canWrite && !isDrilledDown && activeTab === 'attributes' && (
      nodeClass === NodeClassValues.UAObjectType
      || nodeClass === NodeClassValues.UAVariableType
      || nodeClass === NodeClassValues.UADataType
      || nodeClass === NodeClassValues.UAReferenceType
   );

   // Show Create Instance button for ObjectType and VariableType only, on Attributes tab.
   const isInstantiableType = canWrite && !isDrilledDown && activeTab === 'attributes' && (
      nodeClass === NodeClassValues.UAObjectType
      || nodeClass === NodeClassValues.UAVariableType
   );

   // Show Edit Arguments button on a Method node's Attributes and Children tabs.
   // Methods are reached by drilling in, so (unlike Create Instance) this does
   // not require being at the top level — just an editable Method.
   const isMethodNode = activeIsEditable
      && (activeTab === 'attributes' || activeTab === 'children')
      && activeNodeClass === NodeClassValues.UAMethod;

   const handleDataTypeSaved = () => {
      queryClient.invalidateQueries({ queryKey: ['nodeAttributes', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['nodeDataTypeFields', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
      queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
      queryClient.invalidateQueries({ queryKey: ['subtypes'] });
   };

   const handleSaveReference = async (data: AddReferenceData) => {
      setIsAddingRef(true);
      setAddRefError(null);
      try {
         await api.post(
            `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}/references`,
            {
               referenceTypeId: data.referenceTypeId,
               targetNodeId: data.targetNodeId,
               isForward: data.isForward,
            },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
         queryClient.invalidateQueries({ queryKey: ['subtypes'] });
         // Hierarchical refs change tree membership: an inverse ref from the
         // Objects folder (i=85) means this node becomes a child of Objects
         // and must appear there. Either endpoint may gain or lose a child,
         // and the cache key shape varies by caller (3- vs 4-element), so
         // invalidate the whole 'nodeChildren' family.
         queryClient.invalidateQueries({ queryKey: ['nodeChildren'] });
         setRefDialogOpen(false);
      } catch (e) {
         setAddRefError(extractErrorMessage(e, 'Failed to add reference'));
      } finally {
         setIsAddingRef(false);
      }
   };

   const handleDeleteReference = async (ref: NodeReferenceDto) => {
      if (!ref.targetNodeId || !ref.referenceTypeId) return;

      await api.delete(
         `/opcua/v1/nodes/${slugifyNodeId(activeNodeId)}/references/${slugifyNodeId(ref.referenceTypeId)}/${slugifyNodeId(ref.targetNodeId)}`,
         {
            params: { isForward: ref.isForward },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         }
      );
      queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
      // Same reasoning as handleSaveReference: hierarchical refs can be
      // hierarchical-children of either endpoint, and the cache key shape
      // varies by caller (3- vs 4-element). Invalidate the whole family.
      queryClient.invalidateQueries({ queryKey: ['nodeChildren'] });
      queryClient.invalidateQueries({ queryKey: ['subtypes'] });
   };

   const attributeColumns: StripedTableColumn[] = [
      { key: 'name', label: t('typeDetail.name'), width: 180 },
      { key: 'value', label: t('typeDetail.value') },
   ];

   const attributeRows = React.useMemo(() => {
      const rawAttrs = attributesData?.attrs ?? [];
      const attrMap = new Map(rawAttrs.map((a) => [a.name, a]));

      const renderValue = (attr: NodeAttributeDto): React.ReactNode => {
         if (attr.nodeId) {
            return <NodeIdLink nodeId={attr.nodeId} displayName={attr.value ?? ''} nodeClass={attr.nodeClass} />;
         }
         // ValueRank is stored as a raw integer string so dialogs can parse it;
         // format it for display here.
         if (attr.name === 'ValueRank' && attr.value != null && attr.value !== '') {
            const n = Number(attr.value);
            return Number.isNaN(n) ? attr.value : formatValueRank(n);
         }
         return attr.value ?? '';
      };

      // Attribute keys whose row label reads differently from the key itself.
      const rowLabels: Record<string, string> = {
         DataTypeForm: 'DataType Form',
         ConformanceUnits: t('typeDetail.conformanceUnits', 'Conformance Units'),
      };

      let order: string[];
      if (isDataType) {
         order = ['NodeId', 'NodeClass', 'BrowseName', 'DisplayName', 'Description',
            'IsAbstract', 'SuperType', 'DataTypeForm', 'ConformanceUnits'];
      } else if (activeNodeClass === NodeClassValues.UAObjectType) {
         order = ['NodeId', 'NodeClass', 'BrowseName', 'DisplayName', 'Description',
            'IsAbstract', 'SuperType', 'ConformanceUnits'];
      } else if (activeNodeClass === NodeClassValues.UAVariableType) {
         order = ['NodeId', 'NodeClass', 'BrowseName', 'DisplayName', 'Description',
            'IsAbstract', 'SuperType', 'DataType', 'ValueRank', 'ArrayDimensions',
            'ConformanceUnits'];
      } else if (activeNodeClass === NodeClassValues.UAReferenceType) {
         // Symmetric before InverseName: it decides whether an inverse name is
         // allowed at all, and a symmetric type never shows one.
         order = ['NodeId', 'NodeClass', 'BrowseName', 'DisplayName', 'Description',
            'IsAbstract', 'SuperType', 'Symmetric', 'InverseName', 'ConformanceUnits'];
      } else {
         return rawAttrs.map((attr) => ({
            name: rowLabels[attr.name] ?? attr.name,
            value: renderValue(attr),
         }));
      }

      return order
         .filter((key) => attrMap.has(key))
         .map((key) => ({
            name: rowLabels[key] ?? key,
            value: renderValue(attrMap.get(key)!),
         }));
   }, [attributesData, isDataType, activeNodeClass, t]);

   // Derive column visibility from field data (not from StructureType attribute)
   const { hasAnyOptional, hasAnyAllowSubTypes } = React.useMemo(() => ({
      hasAnyOptional: (fieldsData ?? []).some(f => f.isOptional),
      hasAnyAllowSubTypes: (fieldsData ?? []).some(f => f.allowSubTypes),
   }), [fieldsData]);

   const isStructureOrUnion = dataTypeForm === 'Structure' || dataTypeForm === 'Union';

   // Checkbox visibility logic for AddFieldDialog
   const fieldDialogCheckboxProps = React.useMemo(() => {
      if (!isStructureOrUnion) {
         return { showOptionalCheckbox: false, showAllowSubTypesCheckbox: false, isMutuallyExclusive: false };
      }
      if (hasAnyOptional && !hasAnyAllowSubTypes) {
         return { showOptionalCheckbox: true, showAllowSubTypesCheckbox: false, isMutuallyExclusive: false };
      }
      if (hasAnyAllowSubTypes && !hasAnyOptional) {
         return { showOptionalCheckbox: false, showAllowSubTypesCheckbox: true, isMutuallyExclusive: false };
      }
      // Neither or both — show both, mutually exclusive
      return { showOptionalCheckbox: true, showAllowSubTypesCheckbox: true, isMutuallyExclusive: true };
   }, [isStructureOrUnion, hasAnyOptional, hasAnyAllowSubTypes]);

   const fieldColumns: StripedTableColumn[] = React.useMemo(() => {
      const cols: StripedTableColumn[] = [
         { key: 'name', label: t('typeDetail.name'), width: '25%' },
      ];
      if (dataTypeForm === 'Enumeration') {
         cols.push({ key: 'value', label: t('typeDetail.value'), width: '15%' });
      } else if (dataTypeForm === 'OptionSet') {
         cols.push({ key: 'bit', label: t('typeDetail.bit'), width: '15%' });
      } else {
         // Structure / Union
         cols.push({ key: 'dataType', label: t('typeDetail.dataType'), width: '20%' });
         if (hasAnyOptional) {
            cols.push({ key: 'optional', label: t('typeDetail.optional'), width: '10%' });
         }
         if (hasAnyAllowSubTypes) {
            cols.push({ key: 'allowSubTypes', label: t('typeDetail.allowSubTypes'), width: '10%' });
         }
      }
      cols.push({ key: 'description', label: t('typeDetail.description') });
      // Editable rows carry four actions (up, down, edit, delete), so they need more room.
      cols.push({ key: 'actions', label: '', width: activeIsEditable ? '15%' : '5%' });
      return cols;
   }, [dataTypeForm, hasAnyOptional, hasAnyAllowSubTypes, activeIsEditable, t]);

   // Ordered names of the local (non-inherited) fields — drives the reorder up/down boundaries.
   const ownFieldNames = React.useMemo(
      () => (fieldsData ?? []).filter(f => !f.isInherited).map(f => f.name),
      [fieldsData]
   );

   const fieldActions = (f: DataTypeFieldDto) => {
      if (f.isInherited) {
         // Inherited fields: view-only
         return (
            <Tooltip title={t('typeDetail.view')}>
               <IconButton size="small" onClick={() => openFieldDialog('view', f)}>
                  <VisibilityIcon fontSize="small" />
               </IconButton>
            </Tooltip>
         );
      }
      const ownIndex = ownFieldNames.indexOf(f.name);
      const isFirstOwn = ownIndex === 0;
      const isLastOwn = ownIndex === ownFieldNames.length - 1;
      return activeIsEditable ? (
         <Box sx={{ display: 'flex', gap: 2 }}>
            <Tooltip title={t('typeDetail.moveFieldUp')}>
               <span>
                  <IconButton
                     size="small"
                     disabled={isFirstOwn || isReorderingField}
                     onClick={() => handleMoveField(f.name, 'up')}
                  >
                     <KeyboardArrowUpIcon fontSize="small" />
                  </IconButton>
               </span>
            </Tooltip>
            <Tooltip title={t('typeDetail.moveFieldDown')}>
               <span>
                  <IconButton
                     size="small"
                     disabled={isLastOwn || isReorderingField}
                     onClick={() => handleMoveField(f.name, 'down')}
                  >
                     <KeyboardArrowDownIcon fontSize="small" />
                  </IconButton>
               </span>
            </Tooltip>
            <Tooltip title={t('typeDetail.edit')}>
               <IconButton size="small" onClick={() => openFieldDialog('edit', f)}>
                  <EditIcon fontSize="small" />
               </IconButton>
            </Tooltip>
            <Tooltip title={t('typeDetail.deleteField')}>
               <IconButton size="small" onClick={() => requestDelete(
                  t('typeDetail.deleteFieldConfirm', { name: f.name, defaultValue: 'Delete field "{{name}}"?' }),
                  () => handleDeleteField(f.name))}>
                  <DeleteIcon fontSize="small" />
               </IconButton>
            </Tooltip>
         </Box>
      ) : (
         <Tooltip title={t('typeDetail.view')}>
            <IconButton size="small" onClick={() => openFieldDialog('view', f)}>
               <VisibilityIcon fontSize="small" />
            </IconButton>
         </Tooltip>
      );
   };

   const filteredFields = React.useMemo(() => {
      const all = fieldsData ?? [];
      return hideInheritedFields ? all.filter(f => !f.isInherited) : all;
   }, [fieldsData, hideInheritedFields]);

   const fieldRows: Record<string, React.ReactNode>[] = filteredFields.map((f) => {
      const nameCell = f.isInherited ? (
         <Tooltip title={t('typeDetail.inheritedFrom', { source: f.sourceTypeName ?? '' })}>
            <span style={{ fontStyle: 'italic' }}>{f.name}</span>
         </Tooltip>
      ) : f.name;

      if (dataTypeForm === 'Enumeration') {
         return {
            name: nameCell,
            value: f.value?.toString() ?? '',
            description: f.description ?? '',
            actions: fieldActions(f),
         };
      }
      if (dataTypeForm === 'OptionSet') {
         return {
            name: nameCell,
            bit: f.value?.toString() ?? '',
            description: f.description ?? '',
            actions: fieldActions(f),
         };
      }
      // Structure / Union
      const row: Record<string, React.ReactNode> = {
         name: nameCell,
         dataType: <DataTypeCell
            dataTypeId={f.dataTypeId}
            dataTypeName={f.dataType}
            valueRank={f.valueRank}
         />,
         description: f.description ?? '',
         actions: fieldActions(f),
      };
      if (hasAnyOptional) {
         row.optional = f.isOptional ? t('typeDetail.yes') : '';
      }
      if (hasAnyAllowSubTypes) {
         row.allowSubTypes = f.allowSubTypes ? t('typeDetail.yes') : '';
      }
      return row;
   });

   // Per-row styling for inherited fields
   const fieldRowSx: (SxProps<Theme> | undefined)[] = filteredFields.map((f) =>
      f.isInherited ? { opacity: 0.7 } : undefined
   );

   const filteredChildren = React.useMemo(() => {
      const all = childrenData ?? [];
      return hideInherited ? all.filter(c => !c.isInherited) : all;
   }, [childrenData, hideInherited]);

   // Per-row styling for inherited children
   const childRowSx: (SxProps<Theme> | undefined)[] = filteredChildren.map((c) =>
      c.isInherited ? { opacity: 0.7 } : undefined
   );

   const showModellingRule = activeNodeClass !== NodeClassValues.UADataType
      && activeNodeClass !== NodeClassValues.UAReferenceType;

   const childColumns = React.useMemo((): StripedTableColumn[] => {
      const cols: StripedTableColumn[] = [
         { key: 'name', label: t('typeDetail.name'), width: '25%' },
         { key: 'reference', label: t('typeDetail.reference'), width: '15%' },
         { key: 'typeDefinition', label: t('typeDetail.typeDefinition'), width: '20%' },
         { key: 'dataType', label: t('typeDetail.dataType') },
      ];
      if (showModellingRule) {
         cols.push({ key: 'modellingRule', label: t('typeDetail.rule'), width: '6%' });
      }
      cols.push({ key: 'actions', label: '', width: activeIsEditable ? '10%' : '8%' });
      return cols;
   }, [showModellingRule, activeIsEditable, t]);

   const childRows = filteredChildren.map((child) => {
      const nameCell = (
         <Tooltip title={
            child.isInherited ? t('typeDetail.inheritedFrom', { source: child.sourceTypeNodeId ?? '' })
            : child.isOverride ? t('typeDetail.overrides')
            : ''
         }>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 4 }}>
               <Avatar
                  sx={{
                     width: 22,
                     height: 22,
                     bgcolor: theme.palette.grey[600],
                     color: theme.palette.grey[200],
                  }}
               >
                  {getNodeClassIcon(child.nodeClass)}
               </Avatar>
               <span style={{ fontStyle: child.isInherited ? 'italic' : undefined }}>
                  <NodeIdLink
                     nodeId={child.nodeId}
                     displayName={child.displayName}
                     nodeClass={child.nodeClass}
                  />
               </span>
               {child.isOverride && (
                  <CallSplitIcon fontSize="small" color="action" sx={{ ml: -2 }} />
               )}
            </Box>
         </Tooltip>
      );
      const row: Record<string, React.ReactNode> = {
         name: nameCell,
         reference: <NodeIdLink
            nodeId={child.referenceTypeId}
            displayName={child.referenceType}
            nodeClass={NodeClassValues.UAReferenceType}
         />,
         typeDefinition: <NodeIdLink
            nodeId={child.typeDefinitionId}
            displayName={child.typeDefinition}
            nodeClass={child.nodeClass === 1 ? NodeClassValues.UAObjectType : NodeClassValues.UAVariableType}
         />,
         dataType: <DataTypeCell
            dataTypeId={child.dataTypeId}
            dataTypeName={child.dataType}
            valueRank={child.valueRank}
         />,
      };
      if (showModellingRule) {
         row.modellingRule = formatModellingRule(child.modellingRule);
      }
      const childBrowseKey = child.browseNameRaw
         ? stripNamespace(child.browseNameRaw).toLowerCase() : '';
      const isTypeDerived = isDrilledDown && typeDerivedNames.has(childBrowseKey);
      const childEditable = activeIsEditable && !child.isInherited && !isTypeDerived;
      const canInstantiate = childEditable
         && (child.nodeClass === 1 || child.nodeClass === 2)
         && !!child.typeDefinitionId;
      row.actions = (
         <Box sx={{ display: 'flex', gap: 2 }}>
            <Tooltip title={t('typeDetail.showChildren')}>
               <IconButton size="small" onClick={() => handleDrillDown(child)}>
                  <SubdirectoryArrowRightIcon fontSize="small" />
               </IconButton>
            </Tooltip>
            {child.isInherited && activeIsEditable
               && child.modellingRule !== 'i=11508' && child.modellingRule !== 'i=11510' && (
               <Tooltip title={t('typeDetail.override')}>
                  <IconButton size="small" onClick={() => handleOverrideChild(child)}>
                     <ContentCopyIcon fontSize="small" />
                  </IconButton>
               </Tooltip>
            )}
            {childEditable ? (
               <>
                  {canInstantiate && (
                     <Tooltip title={t('typeDetail.instantiate')}>
                        <IconButton size="small" onClick={() => handleInstantiate(child)}>
                           <AccountTreeIcon fontSize="small" />
                        </IconButton>
                     </Tooltip>
                  )}
                  <Tooltip title={t('typeDetail.edit')}>
                     <IconButton size="small" onClick={() => handleEditChild(child.nodeId!, false, {
                        referenceTypeId: child.referenceTypeId,
                        // An override must keep its declaration's reference type.
                        canEditReferenceType: !child.isOverride,
                     })}>
                        <EditIcon fontSize="small" />
                     </IconButton>
                  </Tooltip>
                  <Tooltip title={t('typeDetail.deleteChild')}>
                     <IconButton size="small" onClick={() => requestDelete(
                        t('typeDetail.deleteChildConfirm', { name: child.displayName ?? '', defaultValue: 'Delete "{{name}}"? This permanently removes the node and its children.' }),
                        () => handleDeleteChild(child.nodeId!))}>
                        <DeleteIcon fontSize="small" />
                     </IconButton>
                  </Tooltip>
               </>
            ) : (
               <Tooltip title={t('typeDetail.view')}>
                  <IconButton size="small" onClick={() => handleEditChild(child.nodeId!, true, {
                     referenceTypeId: child.referenceTypeId,
                     canEditReferenceType: false,
                  })}>
                     <VisibilityIcon fontSize="small" />
                  </IconButton>
               </Tooltip>
            )}
         </Box>
      );
      return row;
   });

   const refColumns: StripedTableColumn[] = [
      { key: 'direction', label: t('typeDetail.direction'), width: '5%' },
      { key: 'referenceType', label: t('typeDetail.referenceType'), width: '30%' },
      { key: 'target', label: t('typeDetail.target') },
      { key: 'actions', label: '', width: activeIsEditable ? '8%' : '5%' },
   ];

   // A reference is a genuine OWNERSHIP parent/child only when it is the canonical-parent link
   // AND both endpoints are in the same model namespace — delete-cascade ownership is always
   // within one model. A canonical-parent reference whose other endpoint is in a DIFFERENT
   // namespace is cross-namespace containment, not ownership (e.g. a top-level object's
   // HasComponent to the Core Namespaces folder, like the NamespaceMetadata object). Only the
   // genuine ownership link is hidden by the toggle and protected from standalone deletion;
   // every other hierarchical reference — including cross-namespace containment — stays visible
   // and deletable.
   const isOwnershipParentChild = (ref: NodeReferenceDto) =>
      !!ref.isCanonicalParent
      && extractNamespaceUri(ref.targetNodeId) === extractNamespaceUri(activeNodeId);

   const refRows = (refsData ?? []).filter((ref) => {
      if (isOwnershipParentChild(ref) && hideParentChildRefs) return false;
      return true;
   }).map((ref) => ({
      direction: ref.isForward
         ? <ArrowForwardIcon fontSize="small" color="action" titleAccess={t('typeDetail.forward')} />
         : <ArrowBackIcon fontSize="small" color="action" titleAccess={t('typeDetail.inverse')} />,
      referenceType: <NodeIdLink
         nodeId={ref.referenceTypeId}
         displayName={ref.referenceType}
         nodeClass={NodeClassValues.UAReferenceType}
      />,
      target: <NodeIdLink
         nodeId={ref.targetNodeId}
         displayName={ref.targetDisplayName}
         nodeClass={ref.targetNodeClass}
      />,
      // Only the genuine same-model ownership parent/child link is protected from standalone
      // deletion (deleting it would orphan the child). Other hierarchical references — extra
      // refs between the same nodes, or cross-namespace containment — remain deletable.
      actions: activeIsEditable && !isOwnershipParentChild(ref) ? (
         <Tooltip title={t('typeDetail.deleteReference')}>
            <IconButton size="small" onClick={() => requestDelete(
               t('typeDetail.deleteReferenceConfirm', { target: ref.targetDisplayName ?? '', defaultValue: 'Delete this reference to "{{target}}"?' }),
               () => handleDeleteReference(ref))}>
               <DeleteIcon fontSize="small" />
            </IconButton>
         </Tooltip>
      ) : null,
   }));

   return (
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, flex: 1 }}>
         {/* Header */}
         <Box sx={{ display: 'flex', alignItems: 'center', gap: 8, mb: 8 }}>
            <IconButton onClick={handleBackClick} title={t('typeDetail.back')}>
               <ArrowBackIosIcon />
            </IconButton>
            <Avatar
               sx={{
                  width: 32,
                  height: 32,
                  bgcolor: theme.palette.grey[600],
                  color: theme.palette.grey[200],
               }}
            >
               {getNodeClassIcon(activeNodeClass)}
            </Avatar>
            {drillStack.length > 0 ? (
               <Breadcrumbs separator=">" sx={{ flex: 1 }}>
                  <Link
                     component="button"
                     variant="body1"
                     underline="hover"
                     onClick={() => setDrillStack([])}
                  >
                     {displayName}
                  </Link>
                  {drillStack.slice(0, -1).map((node, idx) => (
                     <Link
                        key={idx}
                        component="button"
                        variant="body1"
                        underline="hover"
                        onClick={() => setDrillStack(prev => prev.slice(0, idx + 1))}
                     >
                        {stripModelPrefix(node.displayName)}
                     </Link>
                  ))}
                  <Typography variant="body1" sx={{ fontWeight: 'bold' }}>
                     {stripModelPrefix(drillStack[drillStack.length - 1].displayName)}
                  </Typography>
               </Breadcrumbs>
            ) : (
               <Typography variant="h6" sx={{ fontWeight: 'bold' }}>
                  {attributesData?.attrs.find(a => a.name === 'BrowseName')?.value ?? displayName}
               </Typography>
            )}
            {activeIsEditable && (
               <EditIcon fontSize="small" color="action" />
            )}
         </Box>

         {/* Tabs with "+" button */}
         <Box sx={{ display: 'flex', alignItems: 'center', mb: 8 }}>
            <Tabs value={activeTab} onChange={(_, v: TabId) => setActiveTab(v)} sx={{ flexGrow: 1 }}>
               <Tab value="attributes" label={t('typeDetail.attributes')} />
               {showFieldsTab && <Tab value="fields" label={t('typeDetail.fields')} />}
               {showValueTab && <Tab value="value" label={t('typeDetail.value', 'Value')} />}
               <Tab value="children" label={t('typeDetail.children')} />
               <Tab value="references" label={t('typeDetail.references')} />
            </Tabs>
            {activeTab === 'children' && (
               <Tooltip title={hideInherited ? t('typeDetail.showInherited') : t('typeDetail.hideInherited')}>
                  <IconButton onClick={() => setHideInherited(!hideInherited)} size="small">
                     {hideInherited ? <VisibilityOffIcon /> : <VisibilityIcon />}
                  </IconButton>
               </Tooltip>
            )}
            {activeTab === 'fields' && (
               <Tooltip title={hideInheritedFields ? t('typeDetail.showInherited') : t('typeDetail.hideInherited')}>
                  <IconButton onClick={() => setHideInheritedFields(!hideInheritedFields)} size="small">
                     {hideInheritedFields ? <VisibilityOffIcon /> : <VisibilityIcon />}
                  </IconButton>
               </Tooltip>
            )}
            {activeTab === 'references' && (
               <Tooltip title={hideParentChildRefs
                  ? t('typeDetail.showParentChildRefs', 'Show parent/child references')
                  : t('typeDetail.hideParentChildRefs', 'Hide parent/child references')}>
                  <IconButton onClick={() => setHideParentChildRefs(!hideParentChildRefs)} size="small">
                     {hideParentChildRefs ? <VisibilityOffIcon /> : <VisibilityIcon />}
                  </IconButton>
               </Tooltip>
            )}
            {showChildCreateButtons && childCreateKinds.map((kind) => (
               <Tooltip key={kind} title={t(CHILD_KIND_META[kind].labelKey)}>
                  <IconButton onClick={() => openCreateChild(kind)} size="small">
                     {CHILD_KIND_META[kind].icon}
                  </IconButton>
               </Tooltip>
            ))}
            {showChildCreateButtons && showAddInterface && (
               <Tooltip title={t('typeDetail.addInterface', 'Add Interface')}>
                  <IconButton onClick={() => setAddInterfaceOpen(true)} size="small">
                     <SettingsEthernetIcon />
                  </IconButton>
               </Tooltip>
            )}
            {showInstantiateChildren && (
               <Tooltip title={t('typeDetail.instantiate', 'Instantiate Children')}>
                  <IconButton onClick={handleInstantiateNode} size="small">
                     <AccountTreeIcon />
                  </IconButton>
               </Tooltip>
            )}
            {showAddButton && (
               <IconButton
                  onClick={handleAddClick}
                  size="small"
                  title={activeTab === 'references' ? t('typeDetail.addReference') : t('typeDetail.addField')}
               >
                  <AddIcon />
               </IconButton>
            )}
            {showEditButton && (
               <Tooltip title={t('typeDetail.edit')}>
                  <IconButton onClick={handleEditClick} size="small">
                     <EditIcon />
                  </IconButton>
               </Tooltip>
            )}
            {showDeleteButton && (
               <Tooltip title={t('typeDetail.deleteNode', 'Delete')}>
                  <IconButton
                     onClick={() => { setDeleteNodeError(null); setDeleteConfirmOpen(true); }}
                     size="small"
                  >
                     <DeleteIcon />
                  </IconButton>
               </Tooltip>
            )}
            {isExtendableType && (
               <Tooltip title={t('typeDetail.extendType')}>
                  <IconButton onClick={() => setExtendTypeOpen(true)} size="small">
                     <CallSplitIcon />
                  </IconButton>
               </Tooltip>
            )}
            {isInstantiableType && (
               <Tooltip title={t('typeDetail.createInstance', 'Create Instance')}>
                  <IconButton onClick={() => setCreateInstanceOpen(true)} size="small">
                     <PlaylistAddIcon />
                  </IconButton>
               </Tooltip>
            )}
            {isMethodNode && (
               <Tooltip title={t('typeDetail.editArguments', 'Edit Arguments')}>
                  <IconButton onClick={() => setArgumentsOpen(true)} size="small">
                     <ListAltIcon />
                  </IconButton>
               </Tooltip>
            )}
         </Box>

         {/* Tab panels */}
         <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
            {activeTab === 'attributes' && (
               <ContentLoader isLoading={attrsLoading} isError={attrsError} error={attrsErrorObj}>
                  <>
                     <StripedTable columns={attributeColumns} rows={attributeRows} />
                     {activeDesignToolOnly && (
                        <Box sx={{ px: 3, pb: 3, mt: 8 }}>
                           <Typography variant="body2" color="text.secondary">
                              {t('typeDetail.designToolOnlyAttributesNote',
                                 'This {{kind}} is used to define a well-known BrowseName.',
                                 { kind: activeNodeClass === NodeClassValues.UAVariable ? 'Variable' : 'Object' })}
                           </Typography>
                        </Box>
                     )}
                  </>
               </ContentLoader>
            )}

            {showFieldsTab && activeTab === 'fields' && (
               <ContentLoader isLoading={fieldsLoading} isError={fieldsError} error={fieldsErrorObj}>
                  <StripedTable columns={fieldColumns} rows={fieldRows} rowSx={fieldRowSx} />
               </ContentLoader>
            )}

            {showValueTab && activeTab === 'value' && (
               <ContentLoader isLoading={attrsLoading} isError={attrsError} error={attrsErrorObj}>
                  {attributesData?.raw && (
                     <ValueEditor
                        key={activeNodeId + '|' + JSON.stringify(attributesData.raw.value ?? null)}
                        workspaceId={workspaceId}
                        nodeId={activeNodeId}
                        dataType={attributesData.raw.dataType}
                        valueRank={attributesData.raw.valueRank}
                        value={attributesData.raw.value}
                        isEditable={activeIsEditable}
                     />
                  )}
               </ContentLoader>
            )}

            {activeTab === 'children' && (
               <ContentLoader isLoading={childrenLoading} isError={childrenError} error={childrenErrorObj}>
                  <>
                     {childrenLocked && (
                        <Box sx={{ p: 3 }}>
                           <Typography variant="body2" color="text.secondary">
                              {activeDesignToolOnly
                                 ? t('typeDetail.childrenNotPermitted', 'Adding children is not permitted.')
                                 : t('typeDetail.childrenNotPermittedProperty', 'Adding children to Properties is not permitted.')}
                           </Typography>
                        </Box>
                     )}
                     {childRows.length > 0 ? (
                        <StripedTable columns={childColumns} rows={childRows} rowSx={childRowSx} />
                     ) : !childrenLocked && (
                        <Box sx={{ p: 3 }}>
                           <Typography variant="body2" color="text.secondary">
                              {t('typeDetail.noChildren', 'No children defined.')}
                           </Typography>
                        </Box>
                     )}
                  </>
               </ContentLoader>
            )}

            {activeTab === 'references' && (
               <ContentLoader isLoading={refsLoading} isError={refsError} error={refsErrorObj}>
                  <>
                     {referencesLocked && (
                        <Box sx={{ p: 3 }}>
                           <Typography variant="body2" color="text.secondary">
                              {t('typeDetail.referencesNotPermitted', 'Adding references is not permitted.')}
                           </Typography>
                        </Box>
                     )}
                     {refRows.length > 0 ? (
                        <StripedTable columns={refColumns} rows={refRows} />
                     ) : !referencesLocked && (
                        <Box sx={{ p: 3 }}>
                           <Typography variant="body2" color="text.secondary">
                              {t('typeDetail.noReferences', 'No references defined.')}
                           </Typography>
                        </Box>
                     )}
                  </>
               </ContentLoader>
            )}
         </Box>

         {/* Add Field Dialog */}
         <AddFieldDialog
            open={fieldDialogOpen}
            onClose={() => setFieldDialogOpen(false)}
            onAdd={handleSaveField}
            isAdding={isAddingField}
            addError={addFieldError}
            workspaceId={workspaceId}
            mode={fieldDialogMode}
            initialData={fieldDialogInitial}
            showOptionalCheckbox={fieldDialogCheckboxProps.showOptionalCheckbox}
            showAllowSubTypesCheckbox={fieldDialogCheckboxProps.showAllowSubTypesCheckbox}
            isMutuallyExclusive={fieldDialogCheckboxProps.isMutuallyExclusive}
            isStructureOrUnion={isStructureOrUnion}
            isEnumeration={dataTypeForm === 'Enumeration'}
            isOptionSet={dataTypeForm === 'OptionSet'}
         />

         {/* Edit Type Dialog */}
         {isTypeNode && (
            <EditTypeDialog
               open={editDataTypeOpen}
               onClose={() => setEditDataTypeOpen(false)}
               workspaceId={workspaceId}
               nodeId={activeNodeId}
               attributes={attributesData?.attrs ?? []}
               conformanceUnits={attributesData?.raw?.category}
               onSaved={handleDataTypeSaved}
               nodeClass={activeNodeClass}
            />
         )}

         {/* Extend Type Dialog */}
         {isExtendableType && (
            <EditTypeDialog
               open={extendTypeOpen}
               onClose={() => setExtendTypeOpen(false)}
               workspaceId={workspaceId}
               mode="create"
               nodeId=""
               attributes={[]}
               superTypeNodeId={nodeId}
               superTypeModelUri={extractNamespaceUri(nodeId)}
               nodeClass={nodeClass}
               onSaved={(created?: CreatedNodeInfo) => {
                  queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
                  queryClient.invalidateQueries({ queryKey: ['subtypes'] });
                  queryClient.invalidateQueries({ queryKey: ['nextNodeId'] });
                  if (created) {
                     setSelectedType({
                        nodeId: created.nodeId,
                        displayName: created.displayName,
                        nodeClass: created.nodeClass,
                     });
                     setNavigateToNode({
                        nodeId: created.nodeId,
                        superTypeIds: [],
                        nodeClass: created.nodeClass,
                        displayName: created.displayName,
                        enterFocusMode: true,
                     });
                  }
               }}
            />
         )}

         {/* Create Instance Dialog */}
         {isInstantiableType && (
            <CreateInstanceDialog
               open={createInstanceOpen}
               onClose={() => setCreateInstanceOpen(false)}
               workspaceId={workspaceId}
               typeDefinitionNodeId={nodeId}
               nodeClass={nodeClass}
               onSaved={(created) => {
                  queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
                  setSelectedType({
                     nodeId: created.nodeId,
                     displayName: created.displayName,
                     nodeClass: created.nodeClass,
                  });
                  setNavigateToNode({
                     nodeId: created.nodeId,
                     superTypeIds: [],
                     nodeClass: created.nodeClass,
                     displayName: created.displayName,
                     enterFocusMode: true,
                  });
               }}
            />
         )}

         {/* Edit Method Arguments Dialog */}
         {activeNodeClass === NodeClassValues.UAMethod && (
            <CreateArgumentsDialog
               open={argumentsOpen}
               onClose={() => setArgumentsOpen(false)}
               workspaceId={workspaceId}
               methodNodeId={activeNodeId}
               onSaved={() => {
                  queryClient.invalidateQueries({ queryKey: ['nodeChildren', workspaceId, activeNodeId] });
                  queryClient.invalidateQueries({ queryKey: ['nodeReferences', workspaceId, activeNodeId] });
               }}
            />
         )}

         {/* Add Child Dialog */}
         <AddChildDialog
            open={childDialogOpen}
            onClose={() => setChildDialogOpen(false)}
            onSave={handleSaveChild}
            isSaving={isAddingChild}
            saveError={addChildError}
            workspaceId={workspaceId}
            parentNodeId={activeNodeId}
            parentNodeClass={activeNodeClass}
            showModellingRule={showModellingRule}
            childKind={childDialogKind}
         />

         {/* Add Interface Dialog */}
         <AddInterfaceDialog
            open={addInterfaceOpen}
            onClose={() => setAddInterfaceOpen(false)}
            workspaceId={workspaceId}
            nodeId={activeNodeId}
            onComplete={handleInstantiateComplete}
         />

         {/* Edit Child Dialog */}
         <EditChildDialog
            open={editChildOpen}
            onClose={() => setEditChildOpen(false)}
            onSaved={handleChildSaved}
            workspaceId={workspaceId}
            childNodeId={editChildNodeId}
            showModellingRule={showModellingRule}
            readOnly={editChildReadOnly}
            referenceTypeId={editChildRefTypeId}
            canEditReferenceType={editChildRefEditable}
         />

         {/* Instantiate Type Children Dialog */}
         <InstantiateDialog
            open={instantiateDialogOpen}
            onClose={() => setInstantiateDialogOpen(false)}
            workspaceId={workspaceId}
            parentNodeId={instantiateTargetNodeId}
            modelUri={instantiateModelUri}
            onComplete={handleInstantiateComplete}
         />

         {/* Add Reference Dialog */}
         <AddReferenceDialog
            open={refDialogOpen}
            onClose={() => setRefDialogOpen(false)}
            onSave={handleSaveReference}
            isSaving={isAddingRef}
            saveError={addRefError}
            workspaceId={workspaceId}
         />

         {/* Delete Node Confirmation */}
         {deleteConfirmOpen && (
            <ModelDialog
               open
               onClose={() => setDeleteConfirmOpen(false)}
               title={t('typeDetail.deleteNodeTitle', 'Delete Node')}
               isLoading={isDeletingNode}
               isError={!!deleteNodeError}
               error={deleteNodeError ? new Error(deleteNodeError) : null}
               actions={deleteNodeError ? [] : [
                  {
                     label: isDeletingNode ? t('common.deleting') : t('common.ok'),
                     onClick: handleDeleteNode,
                     disabled: isDeletingNode,
                  },
               ]}
            >
               {!deleteNodeError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1">
                        {t('typeDetail.deleteNodeConfirmation', {
                           name: attributesData?.attrs.find(a => a.name === 'BrowseName')?.value ?? displayName,
                           defaultValue: 'Delete this node and all of its children?',
                        })}
                     </Typography>
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Generic delete confirmation for field / child / reference deletes */}
         {confirmDelete && (
            <ModelDialog
               open
               onClose={() => setConfirmDelete(null)}
               title={t('typeDetail.deleteConfirmTitle', 'Confirm Delete')}
               isLoading={confirmDeleting}
               isError={!!confirmDeleteError}
               error={confirmDeleteError ? new Error(confirmDeleteError) : null}
               actions={confirmDeleteError ? [] : [
                  {
                     label: confirmDeleting ? t('common.deleting') : t('common.ok'),
                     onClick: handleConfirmDelete,
                     disabled: confirmDeleting,
                     color: 'error',
                  },
               ]}
            >
               {!confirmDeleteError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1">{confirmDelete.message}</Typography>
                  </Box>
               )}
            </ModelDialog>
         )}
      </Box>
   );
};
