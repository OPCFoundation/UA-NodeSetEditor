import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import api, { ApiError } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';

import { useQuery, useQueryClient } from '@tanstack/react-query';

import WidgetsIcon from '@mui/icons-material/Widgets';
import CategoryIcon from '@mui/icons-material/Category';
import TuneIcon from '@mui/icons-material/Tune';
import SchemaIcon from '@mui/icons-material/Schema';
import LinkIcon from '@mui/icons-material/Link';
import VisibilityIcon from '@mui/icons-material/Visibility';
import EditIcon from '@mui/icons-material/Edit';
import CallSplitIcon from '@mui/icons-material/CallSplit';
import DeleteIcon from '@mui/icons-material/Delete';
import PlaylistAddIcon from '@mui/icons-material/PlaylistAdd';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Avatar from '@mui/material/Avatar';
import InputLabel from '@mui/material/InputLabel';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemIcon from '@mui/material/ListItemIcon';
import MenuItem from '@mui/material/MenuItem';
import Pagination from '@mui/material/Pagination';
import Select from '@mui/material/Select';
import FormControl from '@mui/material/FormControl';
import type { SelectChangeEvent } from '@mui/material/Select';

import { SearchBar } from '../components/SearchBar';
import { ContentLoader } from '../components/ContentLoader';
import { ActionBar } from '../components/ActionBar';
import { TypeDetailView } from '../components/TypeDetailView';
import { EditTypeDialog, type CreatedNodeInfo } from '../components/EditTypeDialog';
import { CreateInstanceDialog } from '../components/CreateInstanceDialog';
import type { CreatedInstanceInfo } from '../components/CreateInstanceDialog';
import { ModelDialog } from '../components/ModelDialog';
import { WorkspaceContext } from '../WorkspaceContext';
import { useCanWriteWorkspace } from '../hooks/useCurrentWorkspace';
import { alpha, useTheme } from '@mui/material/styles';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { getNodePlainName } from '../model/Node';
import type { Node } from '../model/Node';
import { nodeClassToNum } from '../model/NodeFormatting';
import { extractNamespaceUri } from '../utils/formatNodeId';

const nodeClassOptions = [
   { value: '', label: 'All Types' },
   { value: 'ObjectType', label: 'ObjectType' },
   { value: 'VariableType', label: 'VariableType' },
   { value: 'DataType', label: 'DataType' },
   { value: 'ReferenceType', label: 'ReferenceType' },
   // Top-level instances. Picking Object or Variable filters the list to nodes
   // of that NodeClass that have no parent (i.e., entry points into the
   // address-space tree, like server-level objects).
   { value: 'Object', label: 'Object (top-level)' },
   { value: 'Variable', label: 'Variable (top-level)' },
];


function getNodeClassIcon(nodeClass?: string) {
   switch (nodeClass) {
      case 'ObjectType':
         return <CategoryIcon />;
      case 'VariableType':
         return <TuneIcon />;
      case 'DataType':
         return <SchemaIcon />;
      case 'ReferenceType':
         return <LinkIcon />;
      case 'Object':
         return <CategoryIcon />;
      case 'Variable':
         return <TuneIcon />;
      default:
         return <WidgetsIcon />;
   }
}

const TypeLibraryPage: React.FC = () => {
   const [pageSize, setPageSize] = React.useState<number>(25);
   const [start, setStart] = React.useState<number>(0);
   const [filter, setFilter] = React.useState<string>('');
   const [highlightedTypeNodeId, setHighlightedTypeNodeId] = React.useState<string>('');
   const [extendType, setExtendType] = React.useState<Node | null>(null);
   const [deleteType, setDeleteType] = React.useState<Node | null>(null);
   const [isDeleting, setIsDeleting] = React.useState(false);
   const [deleteError, setDeleteError] = React.useState<string | null>(null);
   const [createInstanceType, setCreateInstanceType] = React.useState<Node | null>(null);
   const { t } = useTranslation();
   const theme = useTheme();
   const queryClient = useQueryClient();
   const { selectedWorkspaceId, selectedModelUri, setSelectedModelUri, selectedNodeClass, setSelectedNodeClass, selectedType, setSelectedType, setNavigateToNode, preferredDetailTab, setPreferredDetailTab } = React.useContext(WorkspaceContext);
   // Owner-only write permission; shared workspaces are read-only.
   const canWrite = useCanWriteWorkspace();
   const [searchParams, setSearchParams] = useSearchParams();

   // Sync selectedModelUri from the ?ns=<uri> URL param. This lets other pages
   // (e.g. Model Library's "View Type Definitions") deep-link to a specific
   // namespace without depending on the WorkspaceContext state update racing
   // the route change.
   React.useEffect(() => {
      const nsParam = searchParams.get('ns');
      if (nsParam && nsParam !== selectedModelUri) {
         setSelectedModelUri(nsParam);
      }
   }, [searchParams]); // eslint-disable-line react-hooks/exhaustive-deps

   // Sync selectedType from URL search params on mount and popstate
   const skipNextPush = React.useRef(false);
   React.useEffect(() => {
      const typeNodeId = searchParams.get('type');
      if (typeNodeId) {
         const name = searchParams.get('name') ?? typeNodeId;
         const nc = Number(searchParams.get('nc') ?? '0');
         const tab = searchParams.get('tab') ?? undefined;
         if (selectedType?.nodeId !== typeNodeId || selectedType?.tab !== tab) {
            skipNextPush.current = true;
            setSelectedType({ nodeId: typeNodeId, displayName: name, nodeClass: nc, tab });
            // A deep-link / back-forward with an explicit tab seeds the sticky preference.
            if (tab) setPreferredDetailTab(tab);
         }
      } else if (selectedType) {
         skipNextPush.current = true;
         setSelectedType(null);
      }
   }, [searchParams]); // eslint-disable-line react-hooks/exhaustive-deps

   // Push search params when selectedType changes (unless triggered by URL sync)
   React.useEffect(() => {
      if (skipNextPush.current) {
         skipNextPush.current = false;
         return;
      }
      if (selectedType) {
         const params: Record<string, string> = {
            type: selectedType.nodeId,
            name: selectedType.displayName,
            nc: String(selectedType.nodeClass),
         };
         if (selectedType.tab) params.tab = selectedType.tab;
         setSearchParams(params, { replace: false });
      } else {
         if (searchParams.has('type')) {
            setSearchParams({}, { replace: false });
         }
      }
   }, [selectedType]); // eslint-disable-line react-hooks/exhaustive-deps

   // Called by TypeDetailView when user switches tabs — update URL in-place (replace, not push)
   const handleTabChange = React.useCallback((tab: string) => {
      if (!selectedType) return;
      const params: Record<string, string> = {
         type: selectedType.nodeId,
         name: selectedType.displayName,
         nc: String(selectedType.nodeClass),
         tab,
      };
      setSearchParams(params, { replace: true });
   }, [selectedType, setSearchParams]);

   // Fetch namespace info for model ownership and namespace dropdown
   const { data: namespacesData } = useQuery({
      queryKey: ['namespaces', selectedWorkspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) }
         });
         return response.data;
      },
      enabled: !!selectedWorkspaceId,
   });

   const namespaces = namespacesData?.results ?? [];

   const namespaceOptions = React.useMemo(() => {
      return namespaces
         .filter(ns => !!ns.uri)
         .map(ns => ({
            uri: ns.uri,
            label: ns.name ? `${ns.name} - ${ns.uri}` : ns.uri,
         }));
   }, [namespaces]);

   const isModelUriPrivate = React.useCallback((modelUri?: string): boolean => {
      if (!modelUri) return false;
      const ns = namespaces.find(n => n.uri === modelUri);
      return ns?.isPrivate ?? false;
   }, [namespaces]);

   // Fetch types with optional filters
   const { data: typesData, isLoading, isError, error } = useQuery({
      queryKey: ['queryTypes', selectedWorkspaceId, pageSize, start, filter, selectedModelUri, selectedNodeClass],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<Node>>('/opcua/v1/query/types', {
            params: {
               filter: filter || undefined,
               namespaceUri: selectedModelUri || undefined,
               nodeClass: selectedNodeClass || undefined,
               start: start * pageSize,
               count: pageSize,
            },
            headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) },
         });
         return response.data;
      },
      enabled: !!selectedWorkspaceId,
      placeholderData: (prev) => prev,
   });

   const nodes = typesData?.results;
   const totalCount = typesData?.totalCount ?? 0;
   const pageCount = Math.max(1, Math.ceil(totalCount / pageSize));

   const handlePageChange = (_: React.ChangeEvent<unknown>, value: number) => {
      setStart(value - 1);
   };

   const handleFilterChange = (event: React.ChangeEvent<HTMLInputElement>) => {
      setStart(0);
      setFilter(event.target.value);
   };

   const handlePageSizeChange = (event: unknown) => {
      const e = event as SelectChangeEvent;
      const size = Number(e.target.value);
      setPageSize(size ?? 25);
      setStart(0);
   };

   const handleNamespaceChange = (event: SelectChangeEvent<string>) => {
      setSelectedModelUri(event.target.value);
      setStart(0);
   };

   const handleNodeClassChange = (event: SelectChangeEvent<string>) => {
      setSelectedNodeClass(event.target.value);
      setStart(0);
   };


   const handleHighlightType = (item: Node) => {
      const nodeId = item.nodeId ?? '';
      setHighlightedTypeNodeId(highlightedTypeNodeId === nodeId ? '' : nodeId);
      // Signal the sidebar tree to expand to / re-root on this item. In complete
      // view AddressSpaceTree expands the path so the row is visible; in focus
      // mode it re-roots the focused tree at this node.
      setNavigateToNode({
         nodeId,
         superTypeIds: item.superTypeIds ?? [],
         nodeClass: nodeClassToNum(item.nodeClass),
         displayName: getNodePlainName(item),
      });
   };

   const handleSelectType = (item: Node) => {
      setSelectedType({
         nodeId: item.nodeId ?? '',
         displayName: getNodePlainName(item),
         nodeClass: nodeClassToNum(item.nodeClass),
      });
   };

   const handleBack = () => {
      window.history.back();
   };

   // Clearing the type-related search params triggers the effect at
   // line ~122 to drop selectedType, which switches the page back to the
   // list-view render path. Doing it through the URL instead of calling
   // setSelectedType(null) directly keeps the back/forward stack honest.
   const handleTitleClick = () => {
      const next = new URLSearchParams(searchParams);
      next.delete('type');
      next.delete('name');
      next.delete('nc');
      next.delete('tab');
      setSearchParams(next);
   };

   const handleDeleteDialogClose = () => {
      setDeleteType(null);
      setDeleteError(null);
   };

   const handleDeleteConfirm = async () => {
      if (!deleteType?.nodeId || !selectedWorkspaceId) return;
      setIsDeleting(true);
      setDeleteError(null);
      try {
         await api.delete(
            `/opcua/v1/nodes/${slugifyNodeId(deleteType.nodeId)}`,
            { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } }
         );
         setDeleteType(null);
         queryClient.invalidateQueries({ queryKey: ['queryTypes'] });
         queryClient.invalidateQueries({ queryKey: ['subtypes'] });
      } catch (e) {
         const msg = e instanceof ApiError ? e.message : (e instanceof Error ? e.message : 'Failed to delete');
         setDeleteError(msg);
      } finally {
         setIsDeleting(false);
      }
   };

   // If a type is selected, show the detail view
   if (selectedType && selectedWorkspaceId) {
      return (
         <Box sx={{ p: 8, display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
            {/* Row 1: Title */}
            <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, mb: 4 }}>
               <Box
                  onClick={handleTitleClick}
                  sx={{
                     display: 'flex',
                     flexDirection: 'row',
                     alignItems: 'center',
                     gap: 8,
                     flexGrow: 1,
                     cursor: 'pointer',
                     '&:hover': { color: 'primary.main' },
                  }}
               >
                  <WidgetsIcon />
                  <Typography variant='h5' sx={{ fontWeight: 'bolder' }}>{t('typeLibrary.title')}</Typography>
               </Box>
            </Box>
            <TypeDetailView
               nodeId={selectedType.nodeId}
               displayName={selectedType.displayName}
               nodeClass={selectedType.nodeClass}
               isEditable={isModelUriPrivate(extractNamespaceUri(selectedType.nodeId))}
               onBack={handleBack}
               workspaceId={selectedWorkspaceId}
               initialTab={preferredDetailTab}
               onTabChange={(tab) => { setPreferredDetailTab(tab); handleTabChange(tab); }}
            />
         </Box>
      );
   }

   return (
      <Box p={8}>
         {/* Row 1: Title */}
         <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, mb: 4 }}>
            <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, flexGrow: 1 }}>
               <WidgetsIcon />
               <Typography variant='h5' sx={{ fontWeight: 'bolder' }}>{t('typeLibrary.title')}</Typography>
            </Box>
         </Box>

         <SearchBar
            value={filter}
            onChange={handleFilterChange}
         >
            <FormControl
               size="small"
               sx={{
                  // Full width on xs/sm so it sits as its own row inside
                  // the SearchBar's column layout; fixed width on md+
                  // where the row goes back to inline.
                  width: { xs: '100%', md: 500 },
                  minWidth: 0,
                  ml: { md: 2 },
               }}
            >
               <InputLabel id="namespace-filter-label">{t('typeLibrary.namespace')}</InputLabel>
               <Select
                  labelId="namespace-filter-label"
                  id="namespace-filter"
                  value={selectedModelUri}
                  label={t('typeLibrary.namespace')}
                  onChange={handleNamespaceChange}
               >
                  <MenuItem value="">
                     <em>{t('typeLibrary.allNamespaces')}</em>
                  </MenuItem>
                  {namespaceOptions.map((opt) => (
                     <MenuItem key={opt.uri} value={opt.uri}>
                        {opt.label}
                     </MenuItem>
                  ))}
               </Select>
            </FormControl>
            <FormControl
               size="small"
               sx={{
                  width: { xs: '100%', md: 150 },
                  minWidth: 0,
                  ml: { md: 2 },
               }}
            >
               <InputLabel id="nodeclass-filter-label">{t('typeLibrary.nodeClass')}</InputLabel>
               <Select
                  labelId="nodeclass-filter-label"
                  id="nodeclass-filter"
                  value={selectedNodeClass}
                  label={t('typeLibrary.nodeClass')}
                  onChange={handleNodeClassChange}
               >
                  {nodeClassOptions.map((opt) => (
                     <MenuItem key={opt.value} value={opt.value}>
                        {opt.value === '' ? <em>{opt.label}</em> : opt.label}
                     </MenuItem>
                  ))}
               </Select>
            </FormControl>
         </SearchBar>

         {/* Pagination */}
         <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', flexGrow: 1, p: 0 }}>
            <Pagination count={pageCount} page={start + 1} onChange={handlePageChange} />
            <Box sx={{ flexGrow: 1 }} />
            <InputLabel id="pagesize-label" sx={{ fontSize: '0.75em', p: 0, ml: 8 }}>{t('main.pageSize')}</InputLabel>
            <Select
               labelId="pagesize-label"
               id="pagesize"
               value={pageSize.toString()}
               label={t('main.pageSize')}
               onChange={handlePageSizeChange}
               sx={{ ml: 8, p: 0, my: 4, fontSize: '0.75em' }}
               variant='standard'
            >
               <MenuItem value={'10'}>10</MenuItem>
               <MenuItem value={'25'}>25</MenuItem>
               <MenuItem value={'50'}>50</MenuItem>
               <MenuItem value={'100'}>100</MenuItem>
            </Select>
         </Box>

         {/* Type list */}
         <Box>
            <ContentLoader isError={isError} isLoading={isLoading} error={error}>
               <List>
                  {nodes?.map((item: Node, index: number) => {
                     const modelUri = extractNamespaceUri(item.nodeId);
                     const editable = isModelUriPrivate(modelUri) && canWrite;
                     const plainName = getNodePlainName(item);
                     const nodeUri = extractNamespaceUri(item.nodeId);
                     return (
                        <ListItem
                           key={index}
                           onClick={() => handleHighlightType(item)}
                           onDoubleClick={() => handleSelectType(item)}
                           sx={{
                              borderTopStyle: 'solid',
                              borderTopColor: theme.palette.grey[200],
                              borderTopWidth: '2px',
                              cursor: 'pointer',
                              backgroundColor: highlightedTypeNodeId && highlightedTypeNodeId === item.nodeId
                                 ? alpha(theme.palette.primary.main, 0.12) : undefined,
                              borderLeft: highlightedTypeNodeId && highlightedTypeNodeId === item.nodeId
                                 ? `3px solid ${theme.palette.primary.main}` : '3px solid transparent',
                              '&:hover': { backgroundColor: 'action.hover' },
                           }}
                        >
                           <ListItemIcon>
                              <Avatar
                                 sx={{
                                    width: 32,
                                    height: 32,
                                    bgcolor: theme.palette.grey[600],
                                    color: theme.palette.grey[200]
                                 }}
                              >
                                 {getNodeClassIcon(item.nodeClass)}
                              </Avatar>
                           </ListItemIcon>
                           <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, overflow: 'hidden' }}>
                              <Typography variant="body2" component="div" noWrap>
                                 {plainName}
                              </Typography>
                              <Typography variant="caption" color="text.secondary" noWrap>
                                 {nodeUri}
                              </Typography>
                           </Box>
                           <Box onClick={(e) => e.stopPropagation()}>
                              <ActionBar
                                 actions={[
                                    {
                                       onAction: () => setDeleteType(item),
                                       icon: <DeleteIcon />,
                                       tooltipKey: 'typeDetail.deleteType',
                                       disabled: !editable,
                                    },
                                    {
                                       onAction: () => setCreateInstanceType(item),
                                       icon: <PlaylistAddIcon />,
                                       tooltipKey: 'typeDetail.createInstance',
                                       // Only Object/Variable types can be
                                       // instantiated; hide the action entirely
                                       // for DataTypes and ReferenceTypes.
                                       hidden: item.nodeClass !== 'ObjectType'
                                          && item.nodeClass !== 'VariableType',
                                       disabled: !canWrite,
                                    },
                                    {
                                       onAction: () => setExtendType(item),
                                       icon: <CallSplitIcon />,
                                       tooltipKey: 'typeDetail.extendType',
                                       disabled: !canWrite
                                          || (item.nodeClass !== 'ObjectType'
                                             && item.nodeClass !== 'VariableType'
                                             && item.nodeClass !== 'DataType'
                                             && item.nodeClass !== 'ReferenceType'),
                                    },
                                    {
                                       onAction: () => handleSelectType(item),
                                       icon: editable ? <EditIcon /> : <VisibilityIcon />,
                                       tooltipKey: editable ? 'typeDetail.edit' : 'typeDetail.view',
                                    },
                                 ]}
                              />
                           </Box>
                        </ListItem>
                     );
                  })}
                  {(!nodes || nodes.length === 0) && !isLoading && selectedWorkspaceId && (
                     <ListItem>
                        <Typography variant="body2" color="text.secondary">
                           No types found in this workspace.
                        </Typography>
                     </ListItem>
                  )}
               </List>
            </ContentLoader>
         </Box>

         {extendType && selectedWorkspaceId && (
            <EditTypeDialog
               open={!!extendType}
               onClose={() => setExtendType(null)}
               workspaceId={selectedWorkspaceId}
               mode="create"
               nodeId=""
               attributes={[]}
               superTypeNodeId={extendType.nodeId}
               superTypeModelUri={extractNamespaceUri(extendType.nodeId)}
               nodeClass={nodeClassToNum(extendType.nodeClass)}
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

         {createInstanceType && selectedWorkspaceId && (
            <CreateInstanceDialog
               open
               onClose={() => setCreateInstanceType(null)}
               workspaceId={selectedWorkspaceId}
               typeDefinitionNodeId={createInstanceType.nodeId}
               nodeClass={nodeClassToNum(createInstanceType.nodeClass)}
               onSaved={(created: CreatedInstanceInfo) => {
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

         {deleteType && selectedWorkspaceId && (
            <ModelDialog
               open
               onClose={handleDeleteDialogClose}
               title={t('typeDetail.deleteTypeTitle')}
               isLoading={isDeleting}
               isError={!!deleteError}
               error={deleteError ? new Error(deleteError) : null}
               actions={deleteError ? [] : [
                  {
                     label: isDeleting ? t('common.deleting') : t('common.ok'),
                     onClick: handleDeleteConfirm,
                     disabled: isDeleting,
                  },
               ]}
            >
               {!deleteError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1">
                        {t('typeDetail.deleteTypeConfirmation', {
                           name: getNodePlainName(deleteType),
                        })}
                     </Typography>
                  </Box>
               )}
            </ModelDialog>
         )}
      </Box>
   );
};

export default TypeLibraryPage;
