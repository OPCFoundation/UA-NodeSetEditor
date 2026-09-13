import * as React from 'react';
import { useQuery } from '@tanstack/react-query';
import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';

import Autocomplete from '@mui/material/Autocomplete';
import TextField from '@mui/material/TextField';

import { formatWithModelPrefix } from '../utils/formatNodeId';

export interface NodeIdOption {
   nodeId: string;
   displayName: string;
   modelUri?: string;
}

interface FormattedOption {
   nodeId: string;
   displayName: string;
   label: string;
}

interface NodeIdAutocompleteProps {
   options: NodeIdOption[];
   value: string;  // nodeId
   onChange: (nodeId: string) => void;
   workspaceId: string;
   label: string;
   placeholder?: string;
   readOnly?: boolean;
}

export const NodeIdAutocomplete: React.FC<NodeIdAutocompleteProps> = ({
   options,
   value,
   onChange,
   workspaceId,
   label,
   placeholder,
   readOnly,
}) => {
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

   const formattedOptions = React.useMemo((): FormattedOption[] => {
      const modelNameMap = new Map<string, string>();
      for (const ns of (namespacesData?.results ?? [])) {
         if (ns.uri && ns.name) modelNameMap.set(ns.uri, ns.name);
      }

      return options
         .map((opt): FormattedOption => ({
            nodeId: opt.nodeId,
            displayName: opt.displayName,
            label: formatWithModelPrefix(opt.displayName, opt.modelUri, modelNameMap),
         }))
         .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }));
   }, [options, namespacesData]);

   const selectedOption = React.useMemo(() => {
      return formattedOptions.find(o => o.nodeId === value) ?? null;
   }, [formattedOptions, value]);

   if (readOnly) {
      return (
         <TextField
            label={label}
            value={selectedOption?.label ?? value}
            fullWidth
            size="small"
            slotProps={{ inputLabel: { shrink: true }, input: { readOnly: true } }}
         />
      );
   }

   return (
      <Autocomplete
         options={formattedOptions}
         getOptionLabel={(opt) => opt.label}
         value={selectedOption}
         onChange={(_, newValue) => onChange(newValue?.nodeId ?? '')}
         isOptionEqualToValue={(opt, val) => opt.nodeId === val.nodeId}
         filterOptions={(opts, { inputValue }) => {
            const lower = inputValue.toLowerCase();
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
