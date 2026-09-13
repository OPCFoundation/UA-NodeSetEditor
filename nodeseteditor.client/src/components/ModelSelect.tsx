import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import Button from '@mui/material/Button';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import Divider from '@mui/material/Divider';

import ListAltIcon from '@mui/icons-material/ListAlt';
import ArrowDropDownIcon from '@mui/icons-material/ArrowDropDown';

import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';

interface ModelSelectProps {
   workspaceId: string;
   /** Selected model namespace URI; empty string means "All Models". */
   value: string;
   onChange: (uri: string) => void;
}

/**
 * Compact Button+Menu model selector. Lists the workspace's namespaces plus an
 * "All Models" reset; selecting a model filters the address-space tree to it.
 * Shared between the sidebar toolbar and the node-picker dialog.
 */
export const ModelSelect: React.FC<ModelSelectProps> = ({ workspaceId, value, onChange }) => {
   const { t } = useTranslation();
   const [anchor, setAnchor] = React.useState<HTMLElement | null>(null);

   // Same shape contract as ModelLibraryPage — return the full
   // PaginatedResponse so the shared ['namespaces', ws] cache stays
   // consistent regardless of which consumer hydrated it.
   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', workspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
            '/opcua/v1/namespaces/info',
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         return response.data;
      },
      enabled: !!workspaceId,
   });
   const namespaces = namespacesData?.results ?? [];

   const currentModel = namespaces.find((n) => n.uri === value);
   const currentModelLabel = value
      ? (currentModel?.name ?? value)
      : t('sidebarToolbar.allModels', 'All Models');

   const handleSelect = (uri: string) => {
      onChange(uri);
      setAnchor(null);
   };

   return (
      <>
         <Tooltip title={t('sidebarToolbar.modelTooltip', 'Active model')}>
            <Button
               size="small"
               color="primary"
               onClick={(e) => setAnchor(e.currentTarget)}
               startIcon={<ListAltIcon fontSize="small" />}
               endIcon={<ArrowDropDownIcon fontSize="small" />}
               sx={{
                  textTransform: 'none',
                  minWidth: 0,
                  flex: 1,
                  justifyContent: 'flex-start',
                  overflow: 'hidden',
                  color: 'primary.main',
                  // AppBar inherits white as its on-primary text colour, and
                  // the Button picks that up for start/end icons unless we
                  // pin the SVG fill back to primary explicitly.
                  '& .MuiButton-startIcon, & .MuiButton-endIcon': {
                     color: 'primary.main',
                  },
                  '& .MuiSvgIcon-root': { color: 'primary.main' },
               }}
            >
               <Typography variant="body2" noWrap sx={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
                  {currentModelLabel}
               </Typography>
            </Button>
         </Tooltip>
         <Menu
            anchorEl={anchor}
            open={Boolean(anchor)}
            onClose={() => setAnchor(null)}
         >
            <MenuItem
               selected={!value}
               onClick={() => handleSelect('')}
            >
               <em>{t('sidebarToolbar.allModels', 'All Models')}</em>
            </MenuItem>
            <Divider />
            {namespaces.map((ns) => (
               <MenuItem
                  key={ns.uri}
                  selected={ns.uri === value}
                  onClick={() => handleSelect(ns.uri ?? '')}
               >
                  {ns.name ?? ns.uri}
               </MenuItem>
            ))}
            {namespaces.length === 0 && (
               <MenuItem disabled>
                  {t('sidebarToolbar.noModels', 'No models')}
               </MenuItem>
            )}
         </Menu>
      </>
   );
};

export default ModelSelect;
