import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemIcon from '@mui/material/ListItemIcon';
import Avatar from '@mui/material/Avatar';
import { useTheme } from '@mui/material/styles';

import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import ChecklistIcon from '@mui/icons-material/Checklist';

import api from '../api/axios.api';
import { WorkspaceContext } from '../WorkspaceContext';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { SearchBar } from '../components/SearchBar';
import type { ConformanceUnitInfo } from '../model/ConformanceUnit';

/**
 * Views one NodeSet by conformance unit: every <Category> its nodes declare, listed once with a
 * node count. Read-only — producing a trimmed NodeSet is done by the Model Library's download
 * dialog ("Remove unused nodes"), not by selecting units here.
 *
 * The model is fixed by the link that got you here (the Model Library's conformance-unit action
 * puts ?model=<id>&ns=<uri> on the URL) and is NOT switchable from this page: an earlier version
 * had a namespace dropdown here, but it retargeted the shared workspace context rather than
 * filtering the list, which read as "the filter is broken".
 */
const ConformanceUnitsPage: React.FC = () => {
   const { t } = useTranslation();
   const theme = useTheme();
   const navigate = useNavigate();
   const [searchParams] = useSearchParams();
   const { selectedWorkspaceId } = React.useContext(WorkspaceContext);

   const [filter, setFilter] = React.useState('');

   const header = React.useMemo(
      () => (selectedWorkspaceId ? { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } : undefined),
      [selectedWorkspaceId]);

   // Any model linked to the workspace can be viewed — unlike validation, this is read-only and
   // applies just as well to a shared or Core nodeset.
   const { data: namespacesData } = useQuery<PaginatedResponse<WorkspaceNamespaceInfo>>({
      queryKey: ['namespaces', selectedWorkspaceId],
      queryFn: async () => {
         const res = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
            '/opcua/v1/namespaces/info', { headers: header });
         return res.data;
      },
      enabled: !!selectedWorkspaceId,
   });

   // ?model= is the model id the API needs; ?ns= is the fallback for a link that carries only
   // the URI. Resolving against the workspace's list also gives the name and profile group.
   const model = React.useMemo(() => {
      const all = namespacesData?.results ?? [];
      const modelId = searchParams.get('model');
      const modelUri = searchParams.get('ns');
      return all.find(n => modelId && n.id === modelId)
         ?? all.find(n => modelUri && n.uri === modelUri);
   }, [namespacesData, searchParams]);

   const modelId = model?.id ?? '';

   const { data: units, isLoading, refetch } = useQuery<ConformanceUnitInfo[]>({
      queryKey: ['conformance-units', selectedWorkspaceId, modelId],
      queryFn: async () => {
         const res = await api.get<ConformanceUnitInfo[]>(
            `/opcua/v1/conformance-units?modelId=${encodeURIComponent(modelId)}`, { headers: header });
         return res.data;
      },
      enabled: !!selectedWorkspaceId && !!modelId,
   });

   const filteredUnits = React.useMemo(() => {
      const needle = filter.trim().toLowerCase();
      const all = units ?? [];
      return needle ? all.filter(u => u.name.toLowerCase().includes(needle)) : all;
   }, [units, filter]);

   if (!selectedWorkspaceId) {
      return <Typography sx={{ p: 3 }} color="text.secondary">
         {t('conformanceUnits.noWorkspace', 'Select a workspace to view conformance units.')}
      </Typography>;
   }

   return (
      <Box sx={{ px: 8, pt: 14 }}>
         <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, mb: 14 }}>
            <Tooltip title={t('conformanceUnits.backToModels', 'Back to models')}>
               <IconButton onClick={() => navigate('/model_library')} size="small"><ArrowBackIcon /></IconButton>
            </Tooltip>
            {/* Which NodeSet this is, and what it's assessed against — the header carries both,
                since neither is selectable here. */}
            <Box sx={{ minWidth: 0 }}>
               <Typography variant="h5" sx={{ fontWeight: 'bolder' }} noWrap>
                  {model?.name
                     ? t('conformanceUnits.pageTitleFor', { model: model.name, defaultValue: 'Conformance Units — {{model}}' })
                     : t('conformanceUnits.pageTitle', 'View Conformance Units')}
               </Typography>
               <Typography variant="caption" color="text.secondary" component="div" noWrap>
                  {model?.uri}
               </Typography>
               <Typography variant="caption" color="text.secondary" component="div">
                  {t('conformanceUnits.profileGroup', 'Profile Group')}:{' '}
                  {model?.profileGroupName || t('common.notSet', 'Not set')}
               </Typography>
            </Box>
         </Box>

         <SearchBar
            hint={t('conformanceUnits.searchHint', 'Search conformance units…')}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            onRefresh={() => refetch()}
            sx={{ px: 0, pt: 0 }}
         />

         {!modelId && (
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
               {t('conformanceUnits.noSelectedModel',
                  'No model selected — open this page from a model\'s Conformance Units icon.')}
            </Typography>
         )}

         <List>
            {filteredUnits.map((unit) => (
               <ListItem
                  key={unit.name}
                  sx={{
                     borderTopStyle: 'solid',
                     borderTopColor: theme.palette.grey[200],
                     borderTopWidth: '2px',
                     '&:hover': { backgroundColor: 'action.hover' },
                  }}
               >
                  <ListItemIcon>
                     <Avatar sx={{ width: 32, height: 32, bgcolor: theme.palette.grey[600], color: theme.palette.grey[200] }}>
                        <ChecklistIcon />
                     </Avatar>
                  </ListItemIcon>
                  <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, overflow: 'hidden' }}>
                     <Typography variant="body1" component="div" noWrap sx={{ fontWeight: 'bold' }}>
                        {unit.name}
                     </Typography>
                     <Typography variant="caption" color="text.secondary">
                        {t('conformanceUnits.nodeCount', { count: unit.nodeCount })}
                     </Typography>
                  </Box>
               </ListItem>
            ))}
            {filteredUnits.length === 0 && !isLoading && !!modelId && (
               <ListItem>
                  <Typography variant="body2" color="text.secondary">
                     {(units?.length ?? 0) === 0
                        ? t('conformanceUnits.noUnits', 'This NodeSet does not declare any conformance units.')
                        : t('conformanceUnits.noMatchingUnits', 'No conformance units match your search.')}
                  </Typography>
               </ListItem>
            )}
         </List>
      </Box>
   );
};

export default ConformanceUnitsPage;
