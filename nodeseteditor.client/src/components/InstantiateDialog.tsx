import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { Node as RestNode } from '../model/Node';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { buildNamespaceMap, formatBrowseName, OPC_UA_CORE_URI } from '../utils/formatNodeId';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Checkbox from '@mui/material/Checkbox';
import { ModelDialog } from './ModelDialog';
import { NodeIdLink } from './NodeIdLink';
import { StripedTable } from './StripedTable';
import type { StripedTableColumn } from './StripedTable';
import type { TemplateChildDto } from '../utils/instantiateUtils';
import { stripNamespace, isMandatory, autoInstantiateMandatoryDescendants } from '../utils/instantiateUtils';
import { formatModellingRule } from '../model/NodeFormatting';


interface NodeChildDto {
   nodeId?: string;
   displayName?: string;
   nodeClass: number;
   browseNameRaw?: string;
}

interface InstantiateDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   parentNodeId: string;
   modelUri: string;
   onComplete: () => void;
}

export const InstantiateDialog: React.FC<InstantiateDialogProps> = ({
   open,
   onClose,
   workspaceId,
   parentNodeId,
   modelUri,
   onComplete,
}) => {
   const { t } = useTranslation();
   const [checkedMap, setCheckedMap] = React.useState<Record<string, boolean>>({});
   const [isApplying, setIsApplying] = React.useState(false);
   const [applyError, setApplyError] = React.useState<string | null>(null);

   // Fetch namespaces for formatting BrowseNames with model prefix
   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return response.data;
      },
      enabled: open && !!workspaceId,
   });

   const nsMap = React.useMemo(
      () => buildNamespaceMap(namespacesData?.results ?? []),
      [namespacesData],
   );

   // Fetch the declarations that apply to the target node. This must come from
   // /instance-declarations, not from browsing the TypeDefinition: a type can author
   // extra children under one of its own instance declarations, and when a subtype
   // overrides that declaration those grandchildren stay on the supertype's copy —
   // the TypeDefinition never sees them. The server does the merge.
   const { data: templateChildren, isLoading: templateLoading } = useQuery({
      queryKey: ['instantiateDeclarations', workspaceId, parentNodeId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/nodes/${slugifyNodeId(parentNodeId)}/instance-declarations`,
            {
               params: { count: 10000 },
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            }
         );
         const all = (response.data.results ?? []) as unknown as TemplateChildDto[];
         // Show every Mandatory or Optional instance declaration. Mandatories
         // render checked + disabled (already auto-instantiated under the
         // parent); Optionals can be toggled.
         return all.filter(c => c.modellingRule === 'i=78' || c.modellingRule === 'i=80');
      },
      enabled: open && !!parentNodeId,
   });

   // Fetch existing (own) children of the target node. Distinct cache key
   // from TypeDetailView's ['nodeChildren', …] query, which fetches the same
   // URL with full=true and a different DTO shape — sharing the key would
   // let whichever query primed the cache feed the wrong data to the other.
   const { data: existingChildren } = useQuery({
      queryKey: ['instantiateExistingChildren', workspaceId, parentNodeId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/nodes/${slugifyNodeId(parentNodeId)}/children`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
         );
         return (response.data.results ?? []).map(n => ({
            nodeId: n.nodeId,
            displayName: n.browseName ?? n.displayName?.text,
            nodeClass: n.nodeClass === 'Object' ? 1 : n.nodeClass === 'Variable' ? 2 : n.nodeClass === 'Method' ? 4 : 0,
            browseNameRaw: n.browseName,
         })) as NodeChildDto[];
      },
      enabled: open && !!parentNodeId,
   });

   // Build a set of existing child keys (stripped BrowseName, lowercase)
   const existingKeys = React.useMemo(() => {
      const keys = new Set<string>();
      for (const child of existingChildren ?? []) {
         if (child.browseNameRaw) {
            keys.add(stripNamespace(child.browseNameRaw).toLowerCase());
         }
      }
      return keys;
   }, [existingChildren]);

   // Initialize checkedMap when data loads
   React.useEffect(() => {
      if (!templateChildren) return;
      const map: Record<string, boolean> = {};
      for (const child of templateChildren) {
         const key = stripNamespace(child.browseName ?? '').toLowerCase();
         const exists = existingKeys.has(key);
         const mandatory = isMandatory(child.modellingRule);
         map[key] = exists || mandatory;
      }
      setCheckedMap(map);
      setApplyError(null);
   }, [templateChildren, existingKeys]);

   const handleToggle = (key: string, mandatory: boolean) => {
      if (mandatory) return;
      setCheckedMap(prev => ({ ...prev, [key]: !prev[key] }));
   };

   const handleApply = async () => {
      if (!templateChildren) return;
      setIsApplying(true);
      setApplyError(null);

      try {
         for (const child of templateChildren) {
            const key = stripNamespace(child.browseName ?? '').toLowerCase();
            const isChecked = checkedMap[key] ?? false;
            const exists = existingKeys.has(key);

            if (isChecked && !exists) {
               // Create this child
               const childDisplayName = child.displayName?.text ?? '';
               // The new node's NodeId is allocated in the parent's model, but
               // its BrowseName must keep the namespace of the source
               // declaration (e.g. EngineeringUnits stays in the Core
               // namespace even when the parent lives in a custom model).
               // OPC UA convention: a BrowseName without an `nsu=` prefix is
               // namespace-0 (Core), so we default to the Core URI here.
               const rawBrowseName = child.browseName ?? '';
               let browseNameModelUri: string | undefined = OPC_UA_CORE_URI;
               let browseName: string;
               if (rawBrowseName.startsWith('nsu=')) {
                  const semi = rawBrowseName.indexOf(';');
                  if (semi > 4) {
                     browseNameModelUri = rawBrowseName.substring(4, semi);
                     browseName = rawBrowseName.substring(semi + 1);
                  } else {
                     browseName = childDisplayName;
                  }
               } else {
                  browseName = rawBrowseName || childDisplayName;
               }

               const created = await api.post<RestNode>(
                  `/opcua/v1/nodes/${slugifyNodeId(parentNodeId)}/children`,
                  {
                     modelUri,
                     browseNameModelUri,
                     nodeClass: child.nodeClass ?? 'Object',
                     referenceTypeId: child.referenceTypeId ?? 'i=47',
                     browseName,
                     displayName: childDisplayName || browseName,
                     typeDefinitionId: child.typeDefinition,
                     modellingRuleId: child.modellingRule,
                     description: child.description?.text ?? '',
                     dataType: child.dataType,
                     valueRank: child.valueRank,
                     arrayDimensions: child.arrayDimensions,
                     // The declaration this row came from: a Variable copies its default
                     // Value from there. The declaration may be inherited, so the value
                     // often lives on a supertype's copy rather than on the parent's own
                     // override — the server resolves that.
                     sourceNodeId: child.nodeId,
                  },
                  { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
               );

               // The new node is only the declaration's root — everything mandatory
               // beneath it still has to be materialized, recursively. Drive that from
               // the declaration rather than its TypeDefinition so grandchildren the
               // owning type authored (and that an override inherits) come along.
               const createdNodeId = created.data?.nodeId;
               if (createdNodeId && child.nodeId) {
                  await autoInstantiateMandatoryDescendants(
                     workspaceId, createdNodeId, child.nodeId, modelUri);
               }
            } else if (!isChecked && exists) {
               // Delete existing child by matching BrowseName
               const match = (existingChildren ?? []).find(ec => {
                  if (!ec.browseNameRaw) return false;
                  return stripNamespace(ec.browseNameRaw).toLowerCase() === key;
               });
               if (match?.nodeId) {
                  await api.delete(
                     `/opcua/v1/nodes/${slugifyNodeId(match.nodeId)}`,
                     { headers: { 'OpcUa-Server': idToUrn(workspaceId) } }
                  );
               }
            }
         }

         onComplete();
         onClose();
      } catch (e) {
         const msg = e instanceof Error ? e.message : 'Failed to apply';
         setApplyError(msg);
      } finally {
         setIsApplying(false);
      }
   };

   const columns: StripedTableColumn[] = [
      { key: 'check', label: '', width: '5%' },
      { key: 'displayName', label: t('typeDetail.name'), width: '35%' },
      { key: 'typeDefinition', label: t('typeDetail.typeDefinition'), width: '35%' },
      { key: 'modellingRule', label: t('typeDetail.rule'), width: '10%' },
   ];

   const rows = (templateChildren ?? []).map((child) => {
      const key = stripNamespace(child.browseName ?? '').toLowerCase();
      const mandatory = isMandatory(child.modellingRule);
      const checked = checkedMap[key] ?? false;

      return {
         check: (
            <Checkbox
               size="small"
               checked={checked}
               disabled={mandatory}
               onChange={() => handleToggle(key, mandatory)}
            />
         ),
         displayName: child.displayName?.text ?? '',
         typeDefinition: (
            <NodeIdLink
               nodeId={child.typeDefinition}
               displayName={formatBrowseName(child.typeDefinitionName, nsMap)}
               noLink
            />
         ),
         modellingRule: formatModellingRule(child.modellingRule),
      };
   });

   const hasChildren = (templateChildren ?? []).length > 0;

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('typeDetail.instantiateTitle')}
         maxWidth="md"
         isLoading={templateLoading || isApplying}
         isError={!!applyError}
         error={applyError ? new Error(applyError) : null}
         actions={hasChildren ? [
            {
               label: t('typeDetail.instantiateApply'),
               onClick: handleApply,
               disabled: isApplying || templateLoading,
            },
         ] : []}
      >
         <Box sx={{ pt: 10, px: 6, pb: 6 }}>
            <Typography variant="body2" sx={{ mb: 8 }}>
               {t('typeDetail.instantiateDescription')}
            </Typography>
            {hasChildren ? (
               <StripedTable columns={columns} rows={rows} />
            ) : (
               !templateLoading && (
                  <Typography variant="body2" color="text.secondary">
                     {t('typeDetail.instantiateNoChildren')}
                  </Typography>
               )
            )}
         </Box>
      </ModelDialog>
   );
};
