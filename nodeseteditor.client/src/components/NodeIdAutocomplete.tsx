import * as React from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';

import Autocomplete from '@mui/material/Autocomplete';
import TextField from '@mui/material/TextField';

import { formatWithModelPrefix } from '../utils/formatNodeId';
import { getRecentTypes, recordRecentType, RECENT_SHOWN } from '../utils/recentTypes';
import type { RecentTypeContext } from '../utils/recentTypes';

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
   /**
    * Which recently-used list this dropdown draws from and contributes to. Omit to opt
    * out of the "frequently used" group entirely.
    */
   recentContext?: RecentTypeContext;
}

export const NodeIdAutocomplete: React.FC<NodeIdAutocompleteProps> = ({
   options,
   value,
   onChange,
   workspaceId,
   label,
   placeholder,
   readOnly,
   recentContext,
}) => {
   const { t } = useTranslation();
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

   // Re-read on open rather than holding a live subscription: several of these can be on
   // screen at once (one per Arguments row), and opening the list is the only moment the
   // order matters.
   const [recentIds, setRecentIds] = React.useState<string[]>(
      () => (recentContext && !readOnly) ? getRecentTypes(recentContext, workspaceId) : [],
   );
   const refreshRecent = React.useCallback(() => {
      if (recentContext) setRecentIds(getRecentTypes(recentContext, workspaceId));
   }, [recentContext, workspaceId]);

   // Recent ids are intersected with the options this dropdown actually offers, so a
   // type that is out of scope here — or has since been deleted — simply drops out, and
   // the group is left empty.
   const { orderedOptions, frequentIds } = React.useMemo(() => {
      if (!recentContext || recentIds.length === 0) {
         return { orderedOptions: formattedOptions, frequentIds: new Set<string>() };
      }
      const byId = new Map(formattedOptions.map((o) => [o.nodeId, o]));
      const frequent: FormattedOption[] = [];
      for (const id of recentIds) {
         const opt = byId.get(id);
         if (opt) frequent.push(opt);
         if (frequent.length >= RECENT_SHOWN) break;
      }
      const ids = new Set(frequent.map((o) => o.nodeId));
      // The remainder keeps the label sort established above.
      return {
         orderedOptions: [...frequent, ...formattedOptions.filter((o) => !ids.has(o.nodeId))],
         frequentIds: ids,
      };
   }, [formattedOptions, recentIds, recentContext]);

   const handleChange = React.useCallback((nodeId: string) => {
      if (nodeId && recentContext) recordRecentType(recentContext, workspaceId, nodeId);
      onChange(nodeId);
   }, [onChange, recentContext, workspaceId]);

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
         options={orderedOptions}
         getOptionLabel={(opt) => opt.label}
         value={selectedOption}
         onChange={(_, newValue) => handleChange(newValue?.nodeId ?? '')}
         onOpen={refreshRecent}
         isOptionEqualToValue={(opt, val) => opt.nodeId === val.nodeId}
         // Only group once there is something recent to lift out, so dropdowns with an
         // empty list look exactly as they did before.
         groupBy={frequentIds.size > 0
            ? (opt) => t(frequentIds.has(opt.nodeId) ? 'typeDetail.frequentlyUsed' : 'typeDetail.allTypes')
            : undefined}
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
