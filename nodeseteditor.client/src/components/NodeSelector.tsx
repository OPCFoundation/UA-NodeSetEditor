import * as React from 'react';
import { useQuery } from '@tanstack/react-query';
import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { getNodePlainName } from '../model/Node';
import type { Node as RestNode } from '../model/Node';

import Autocomplete from '@mui/material/Autocomplete';
import TextField from '@mui/material/TextField';

import { formatWithModelPrefix } from '../utils/formatNodeId';

interface NodeOption {
   nodeId: string;
   displayName: string;
   label: string;
}

interface NodeSelectorProps {
   workspaceId: string;
   nodeClass: number;
   value: string;
   onChange: (value: string) => void;
   onNodeIdChange?: (nodeId: string) => void;
   label: string;
   placeholder?: string;
   readOnly?: boolean;
}

const numToNodeClassLabel: Record<number, string> = {
   8: 'ObjectType', 16: 'VariableType', 32: 'ReferenceType', 64: 'DataType',
};

export const NodeSelector: React.FC<NodeSelectorProps> = ({
   workspaceId,
   nodeClass,
   value,
   onChange,
   onNodeIdChange,
   label,
   placeholder,
   readOnly,
}) => {
   const nodeClassLabel = numToNodeClassLabel[nodeClass] ?? '';

   const { data: types } = useQuery({
      queryKey: ['queryTypes', workspaceId, nodeClass],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>('/opcua/v1/query/types', {
            params: { nodeClass: nodeClassLabel, start: 0, count: 10000 },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return response.data.results ?? [];
      },
      enabled: !readOnly,
   });

   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) }
         });
         return response.data;
      },
      enabled: !readOnly,
   });

   const options = React.useMemo((): NodeOption[] => {
      const modelNameMap = new Map<string, string>();
      for (const ns of (namespacesData?.results ?? [])) {
         if (ns.uri && ns.name) modelNameMap.set(ns.uri, ns.name);
      }

      return (types ?? [])
         .map((node): NodeOption => {
            // getNodePlainName walks DisplayName → BrowseName (namespace stripped)
            // → NodeId, so a node with an empty DisplayName still gets a label
            // instead of rendering as a bare "[Model]:".
            const displayName = getNodePlainName(node);
            const label = formatWithModelPrefix(displayName, node.modelUri, modelNameMap);
            return { nodeId: node.nodeId ?? '', displayName, label };
         })
         .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }));
   }, [types, namespacesData]);

   if (readOnly) {
      return (
         <TextField
            label={label}
            value={value}
            fullWidth
            size="small"
            slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
         />
      );
   }

   return (
      <Autocomplete
         freeSolo
         selectOnFocus
         options={options}
         getOptionLabel={(opt) => (typeof opt === 'string' ? opt : opt.label)}
         inputValue={value}
         onInputChange={(_, newInputValue, reason) => {
            // 'reset' fires after a selection (MUI re-syncs the input to the
            // option label). Ignoring it prevents us from clobbering the
            // nodeId that onChange just set. Typing dissociates the nodeId.
            if (reason === 'reset') return;
            onChange(newInputValue);
            if (reason === 'input') onNodeIdChange?.('');
         }}
         onChange={(_, newValue) => {
            if (typeof newValue === 'string') {
               onChange(newValue);
               onNodeIdChange?.('');
            } else if (newValue) {
               // Use the rendered label so the input visibly matches the
               // option the user picked from the dropdown.
               onChange(newValue.label);
               onNodeIdChange?.(newValue.nodeId);
            } else {
               onChange('');
               onNodeIdChange?.('');
            }
         }}
         filterOptions={(opts, { inputValue }) => {
            const lower = inputValue.toLowerCase();
            if (!lower) return opts;
            // When the input is the exact label of an option, the user has
            // an existing selection — show every option so they can pick a
            // different one without having to clear the input first.
            if (opts.some((opt) => opt.label.toLowerCase() === lower)) return opts;
            return opts.filter((opt) => opt.label.toLowerCase().includes(lower));
         }}
         renderInput={(params) => (
            <TextField
               {...params}
               label={label}
               placeholder={placeholder}
               fullWidth
               size="small"
               slotProps={{ inputLabel: { shrink: true } }}
            />
         )}
      />
   );
};
