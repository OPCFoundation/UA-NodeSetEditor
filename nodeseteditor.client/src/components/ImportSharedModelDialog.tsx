import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import IconButton from '@mui/material/IconButton';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemButton from '@mui/material/ListItemButton';
import Paper from '@mui/material/Paper';
import TextField from '@mui/material/TextField';
import InputAdornment from '@mui/material/InputAdornment';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import CheckIcon from '@mui/icons-material/Check';
import SearchIcon from '@mui/icons-material/Search';
import RefreshIcon from '@mui/icons-material/Refresh';
import { useTheme, alpha } from '@mui/material/styles';

import api, { ApiError } from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import { ModelDialog } from './ModelDialog';

/** Shape returned by GET /opcua/v1/namespaces/shared (server ModelInfo, camelCased). */
export interface SharedModelInfo {
   id?: string;
   modelUri?: string;
   name?: string;
   modelVersion?: string;
   publicationDate?: string;
   description?: string;
   hasErrors?: boolean;
   creator?: string;
}

type SortMode = 'newest' | 'alpha';
type ViewMode = 'all' | 'selected';

function formatDate(s?: string | null): string | null {
   if (!s) return null;
   const d = new Date(s);
   return isNaN(d.getTime()) ? s : d.toISOString().split('T')[0];
}

function modelKey(m: SharedModelInfo): string {
   return m.id ?? m.modelUri ?? '';
}

interface ImportSharedModelDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /** URIs already present in the workspace; these are hidden from the list. */
   existingUris: string[];
}

export const ImportSharedModelDialog: React.FC<ImportSharedModelDialogProps> = ({
   open, onClose, workspaceId, existingUris
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const theme = useTheme();

   const [filter, setFilter] = React.useState('');
   const deferredFilter = React.useDeferredValue(filter);
   const [sortMode, setSortMode] = React.useState<SortMode>('newest');
   const [viewMode, setViewMode] = React.useState<ViewMode>('all');
   const [highlightedKey, setHighlightedKey] = React.useState<string | null>(null);

   const [selectedModels, setSelectedModels] = React.useState<SharedModelInfo[]>([]);

   const [importing, setImporting] = React.useState(false);
   const [importError, setImportError] = React.useState<string | null>(null);

   const { data: sharedModels, isLoading, isError, error, isFetching, refetch } = useQuery({
      queryKey: ['shared-models', workspaceId],
      queryFn: async () => {
         const response = await api.get<SharedModelInfo[]>('/opcua/v1/namespaces/shared', {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) }
         });
         return response.data ?? [];
      },
      enabled: open && !!workspaceId,
      staleTime: 1000 * 60 * 5,
   });

   // Models already in the workspace can't be linked again (one version per URI).
   const availableModels = React.useMemo<SharedModelInfo[]>(() => {
      const present = new Set(existingUris.map(u => u.toLowerCase()));
      return (sharedModels ?? []).filter(m => !m.modelUri || !present.has(m.modelUri.toLowerCase()));
   }, [sharedModels, existingUris]);

   React.useEffect(() => {
      if (open) {
         setFilter('');
         setSortMode('newest');
         setViewMode('all');
         setSelectedModels([]);
         setHighlightedKey(null);
         setImportError(null);
      }
   }, [open]);

   const selectedKeys = React.useMemo<Set<string>>(
      () => new Set(selectedModels.map(modelKey)),
      [selectedModels]
   );

   const visibleModels = React.useMemo<SharedModelInfo[]>(() => {
      const lower = deferredFilter.trim().toLowerCase();

      const matchesText = (m: SharedModelInfo): boolean => {
         if (!lower) return true;
         return (m.name?.toLowerCase().includes(lower) ?? false)
            || (m.modelUri?.toLowerCase().includes(lower) ?? false)
            || (m.description?.toLowerCase().includes(lower) ?? false);
      };

      if (viewMode === 'selected') {
         return selectedModels.filter(matchesText);
      }

      const cmp = (a: SharedModelInfo, b: SharedModelInfo): number => {
         switch (sortMode) {
            case 'newest': {
               const da = a.publicationDate ? new Date(a.publicationDate).getTime() : 0;
               const db = b.publicationDate ? new Date(b.publicationDate).getTime() : 0;
               return db - da;
            }
            case 'alpha': {
               const aa = (a.name ?? a.modelUri ?? '').toLowerCase();
               const bb = (b.name ?? b.modelUri ?? '').toLowerCase();
               return aa.localeCompare(bb);
            }
         }
      };

      return availableModels.filter(matchesText).slice().sort(cmp);
   }, [availableModels, viewMode, selectedModels, deferredFilter, sortMode]);

   const highlightedModel = React.useMemo<SharedModelInfo | null>(() => {
      if (!highlightedKey) return null;
      return visibleModels.find(m => modelKey(m) === highlightedKey)
         ?? availableModels.find(m => modelKey(m) === highlightedKey)
         ?? null;
   }, [highlightedKey, visibleModels, availableModels]);

   const handleRowClick = (m: SharedModelInfo) => {
      const key = modelKey(m);
      setHighlightedKey(key);
      if (selectedKeys.has(key)) {
         setSelectedModels(prev => prev.filter(s => modelKey(s) !== key));
      } else {
         setSelectedModels(prev => [...prev, m]);
      }
   };

   const handleViewModeChange = (next: ViewMode | null) => {
      if (!next || next === viewMode) return;
      setViewMode(next);
   };

   const handleImport = async () => {
      if (!workspaceId || selectedModels.length === 0) return;
      setImporting(true);
      setImportError(null);
      try {
         const headers = { 'OpcUa-Server': idToUrn(workspaceId) };
         for (const m of selectedModels) {
            if (!m.id) continue;
            await api.post(
               `/opcua/v1/namespaces/info/${encodeURIComponent(m.id)}/link`,
               { isPrivate: false },
               { headers }
            );
         }
         // Linking a model changes the whole address space, so refresh EVERY workspace-scoped
         // query (namespaces list AND the tree/type queries: subtypes, nodeChildren, queryTypes,
         // …), not just the model list — otherwise a cached (empty) tree persists until restart.
         queryClient.invalidateQueries({
            predicate: (q) =>
               Array.isArray(q.queryKey) &&
               (q.queryKey[0] === 'discovery' || q.queryKey.includes(workspaceId)),
         });
         onClose();
      } catch (e) {
         const msg = e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : 'Import failed');
         setImportError(msg);
      } finally {
         setImporting(false);
      }
   };

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('modelLibrary.importSharedDialogTitle', 'Import Shared Model')}
         maxWidth="lg"
         isLoading={isLoading || importing}
         isError={isError || !!importError}
         error={importError ? new Error(importError) : (error as Error | null)}
         actions={[
            {
               label: importing
                  ? t('modelLibrary.importing', 'Importing...')
                  : t('common.ok', 'OK'),
               onClick: handleImport,
               disabled: selectedModels.length === 0 || importing
            }
         ]}
      >
         <Box sx={{ p: 2, display: 'flex', flexDirection: 'column', gap: 1, height: 600 }}>
            {/* Row 1: search + view toggle + refresh + counter */}
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: '4px' }}>
               <TextField
                  size="small"
                  fullWidth
                  placeholder={t('main.search', 'Search...')}
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  slotProps={{
                     input: {
                        startAdornment: (
                           <InputAdornment position="start">
                              <SearchIcon fontSize="small" />
                           </InputAdornment>
                        )
                     }
                  }}
               />
               <ToggleButtonGroup
                  value={viewMode}
                  exclusive
                  size="small"
                  onChange={(_, v) => handleViewModeChange(v as ViewMode | null)}
                  sx={{ height: 40, mx: 2 }}
               >
                  <ToggleButton value="all" sx={{ textTransform: 'none', whiteSpace: 'nowrap' }}>
                     {t('modelLibrary.viewAll', 'All')}
                  </ToggleButton>
                  <ToggleButton value="selected" sx={{ textTransform: 'none', whiteSpace: 'nowrap' }}>
                     {t('modelLibrary.viewSelected', 'Selected')} ({selectedModels.length})
                  </ToggleButton>
               </ToggleButtonGroup>
               <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                  {isFetching
                     ? t('modelLibrary.loadingModels', 'Loading models...')
                     : `${availableModels.length} ${t('modelLibrary.total', 'total')}`}
               </Typography>
               <IconButton size="small" onClick={() => refetch()} title={t('common.refresh', 'Refresh')}>
                  <RefreshIcon fontSize="small" />
               </IconButton>
            </Box>

            {/* Row 2: sort (hidden in Selected mode) */}
            {viewMode === 'all' && (
               <Box sx={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 2,
                  mt: 1,
                  px: 2,
                  py: 1.5,
                  bgcolor: theme.palette.action.hover,
                  borderRadius: 1,
               }}>
                  <Typography variant="caption" color="text.secondary">
                     {t('modelLibrary.sortLabel', 'Sort')}:
                  </Typography>
                  <ToggleButtonGroup
                     value={sortMode}
                     exclusive
                     size="small"
                     onChange={(_, v) => v && setSortMode(v as SortMode)}
                     sx={{ bgcolor: theme.palette.background.paper }}
                  >
                     <ToggleButton value="newest" sx={{ textTransform: 'none' }}>
                        {t('modelLibrary.sortNewest', 'Newest')}
                     </ToggleButton>
                     <ToggleButton value="alpha" sx={{ textTransform: 'none' }}>
                        {t('modelLibrary.sortAlpha', 'A–Z')}
                     </ToggleButton>
                  </ToggleButtonGroup>
                  <Box sx={{ flex: 1 }} />
                  <Typography variant="caption" color="text.secondary">
                     {visibleModels.length} {t('modelLibrary.shownCount', 'shown')}
                  </Typography>
               </Box>
            )}

            {/* Main list */}
            <Paper variant="outlined" sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
               {visibleModels.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                     {viewMode === 'selected'
                        ? t('modelLibrary.noSelectedModels', 'No models selected. Switch to All to add some.')
                        : t('modelLibrary.noSharedModels', 'No shared models available to import.')}
                  </Typography>
               ) : (
                  <List dense disablePadding>
                     {visibleModels.map(m => {
                        const key = modelKey(m);
                        const isSelected = selectedKeys.has(key);
                        const isHighlighted = highlightedKey === key;
                        return (
                           <ListItem key={key} disablePadding divider>
                              <ListItemButton
                                 onClick={() => handleRowClick(m)}
                                 selected={isHighlighted}
                                 sx={{
                                    bgcolor: isSelected
                                       ? alpha(theme.palette.primary.main, 0.06)
                                       : undefined,
                                 }}
                              >
                                 <Box sx={{ width: 28, display: 'flex', justifyContent: 'center' }}>
                                    {isSelected && <CheckIcon fontSize="small" color="primary" />}
                                 </Box>
                                 <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 }}>
                                    <Typography variant="body2" noWrap sx={{ fontWeight: isSelected ? 600 : 400 }}>
                                       {m.name ?? m.modelUri ?? '(unnamed)'}
                                    </Typography>
                                    <Typography variant="caption" color="text.secondary" noWrap>
                                       {m.modelUri}
                                    </Typography>
                                 </Box>
                                 <Box sx={{ display: 'flex', alignItems: 'stretch', ml: 2 }}>
                                    <MetaCell width={100}>
                                       {m.creator ?? '—'}
                                    </MetaCell>
                                    <MetaCell width={60}>
                                       {m.modelVersion ? `v${m.modelVersion}` : '—'}
                                    </MetaCell>
                                    <MetaCell width={90}>
                                       {formatDate(m.publicationDate) ?? '—'}
                                    </MetaCell>
                                 </Box>
                              </ListItemButton>
                           </ListItem>
                        );
                     })}
                  </List>
               )}
            </Paper>

            {/* Bottom: info panel for the highlighted model */}
            <Paper variant="outlined" sx={{ p: 2, minHeight: 100, maxHeight: 160, overflow: 'auto' }}>
               {highlightedModel ? (
                  <ModelInfoPanel model={highlightedModel} />
               ) : (
                  <Typography variant="body2" color="text.secondary">
                     {t('modelLibrary.selectModelToViewInfo', 'Select a model to view its details.')}
                  </Typography>
               )}
            </Paper>
         </Box>
      </ModelDialog>
   );
};

/** Right-aligned, fixed-width metadata cell (version / date), matching the Cloud Library dialog. */
const MetaCell: React.FC<{ width: number; children: React.ReactNode }> = ({ width, children }) => (
   <Box sx={{
      width,
      flexShrink: 0,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'flex-end',
      pl: '12px',
      pr: '4px',
      borderLeft: 1,
      borderColor: 'divider',
   }}>
      <Typography
         variant="caption"
         color="text.secondary"
         sx={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}
      >
         {children}
      </Typography>
   </Box>
);

const ModelInfoPanel: React.FC<{ model: SharedModelInfo }> = ({ model }) => {
   const { t } = useTranslation();
   const publishedDate = formatDate(model.publicationDate);
   return (
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
         <Typography variant="subtitle1" sx={{ fontWeight: 'bold' }} noWrap>
            {model.name ?? model.modelUri ?? '(unnamed)'}
         </Typography>
         {model.modelUri && (
            <Typography variant="caption" color="text.secondary" noWrap>
               {model.modelUri}
            </Typography>
         )}
         <Box sx={{ display: 'flex', gap: 2, flexWrap: 'wrap' }}>
            {model.modelVersion && (
               <Typography variant="caption" color="text.secondary">
                  {t('modelLibrary.versionLabel', 'Version')}: {model.modelVersion}
               </Typography>
            )}
            {publishedDate && (
               <Typography variant="caption" color="text.secondary">
                  {t('modelLibrary.publishedLabel', 'Published')}: {publishedDate}
               </Typography>
            )}
            {model.creator && (
               <Typography variant="caption" color="text.secondary">
                  {t('modelLibrary.creatorLabel', 'Creator')}: {model.creator}
               </Typography>
            )}
         </Box>
         {model.description && (
            <Typography variant="body2">
               {model.description}
            </Typography>
         )}
      </Box>
   );
};

export default ImportSharedModelDialog;
