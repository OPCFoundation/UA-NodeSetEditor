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
import Chip from '@mui/material/Chip';
import TextField from '@mui/material/TextField';
import InputAdornment from '@mui/material/InputAdornment';
import MenuItem from '@mui/material/MenuItem';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import CheckIcon from '@mui/icons-material/Check';
import SearchIcon from '@mui/icons-material/Search';
import RefreshIcon from '@mui/icons-material/Refresh';
import { useTheme, alpha } from '@mui/material/styles';

import api, { ApiError } from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import { ModelDialog } from './ModelDialog';

export interface CloudLibraryModelInfo {
   identifier?: string;
   title?: string;
   description?: string;
   namespaceUri?: string;
   version?: string;
   publicationDate?: string;
   license?: string;
   copyrightText?: string;
   documentationUrl?: string;
   iconUrl?: string;
   keywords?: string[];
   numberOfDownloads?: number;
}

const PAGE_SIZE = 100;
const MAX_PAGES = 100;

const DEFAULT_DOMAIN = 'opcfoundation.org';
const ALL_DOMAINS = '__all__';

type SortMode = 'popular' | 'newest' | 'alpha';
type ViewMode = 'all' | 'selected';

async function fetchAllCloudLibraryModels(): Promise<CloudLibraryModelInfo[]> {
   const all: CloudLibraryModelInfo[] = [];
   for (let page = 0; page < MAX_PAGES; page++) {
      const response = await api.get<CloudLibraryModelInfo[]>(
         '/opcua/v1/cloudlibrary/search',
         { params: { offset: page * PAGE_SIZE, limit: PAGE_SIZE } }
      );
      const batch = response.data ?? [];
      if (batch.length === 0) break;
      all.push(...batch);
   }
   return all;
}

/** Extract a registrable domain (e.g., "opcfoundation.org") from a namespace URI. */
function extractDomain(uri?: string | null): string | null {
   if (!uri) return null;
   try {
      const url = new URL(uri);
      if (url.hostname) return url.hostname.toLowerCase();
   } catch {
      // not parseable as URL (urn:..., etc.)
   }
   const match = uri.match(/[a-z0-9-]+(?:\.[a-z0-9-]+)+\.[a-z]{2,}/i)
              ?? uri.match(/[a-z0-9-]+\.[a-z]{2,}/i);
   return match ? match[0].toLowerCase() : null;
}

function modelDomain(m: CloudLibraryModelInfo): string | null {
   return extractDomain(m.namespaceUri);
}

function formatDate(s?: string | null): string | null {
   if (!s) return null;
   const d = new Date(s);
   return isNaN(d.getTime()) ? s : d.toISOString().split('T')[0];
}

function modelKey(m: CloudLibraryModelInfo): string {
   return m.identifier ?? m.namespaceUri ?? '';
}

interface ImportModelDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
}

export const ImportModelDialog: React.FC<ImportModelDialogProps> = ({
   open, onClose, workspaceId
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const theme = useTheme();

   const [filter, setFilter] = React.useState('');
   const deferredFilter = React.useDeferredValue(filter);
   const [sortMode, setSortMode] = React.useState<SortMode>('newest');
   const [domain, setDomain] = React.useState<string>(DEFAULT_DOMAIN);
   const [viewMode, setViewMode] = React.useState<ViewMode>('all');
   const [highlightedKey, setHighlightedKey] = React.useState<string | null>(null);

   // The committed selection set. Adds happen immediately (in All mode).
   // Removes only happen when the user exits Selected mode (or clicks OK).
   const [selectedModels, setSelectedModels] = React.useState<CloudLibraryModelInfo[]>([]);

   // While in Selected mode, we work against a frozen snapshot so the list
   // doesn't churn under the user. Removals are staged in a Set and only
   // applied to selectedModels on mode exit.
   const [snapshot, setSnapshot] = React.useState<CloudLibraryModelInfo[]>([]);
   const [markedForRemoval, setMarkedForRemoval] = React.useState<Set<string>>(new Set());

   const [importing, setImporting] = React.useState(false);
   const [importError, setImportError] = React.useState<string | null>(null);

   const { data: allModels, isLoading, isError, error, isFetching, refetch } = useQuery({
      queryKey: ['cloudlibrary-models'],
      queryFn: fetchAllCloudLibraryModels,
      enabled: open,
      staleTime: 1000 * 60 * 10,
   });

   // Reset state when dialog opens
   React.useEffect(() => {
      if (open) {
         setFilter('');
         setSortMode('newest');
         setDomain(DEFAULT_DOMAIN);
         setViewMode('all');
         setSelectedModels([]);
         setSnapshot([]);
         setMarkedForRemoval(new Set());
         setHighlightedKey(null);
         setImportError(null);
      }
   }, [open]);

   // Derive the list of available domains (sorted by frequency desc).
   const availableDomains = React.useMemo<string[]>(() => {
      const counts = new Map<string, number>();
      for (const m of (allModels ?? [])) {
         const d = modelDomain(m);
         if (!d) continue;
         counts.set(d, (counts.get(d) ?? 0) + 1);
      }
      return Array.from(counts.entries())
         .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
         .map(([d]) => d);
   }, [allModels]);

   const selectedKeys = React.useMemo<Set<string>>(
      () => new Set(selectedModels.map(modelKey)),
      [selectedModels]
   );

   // The list to render. In All mode: full catalog filtered by domain + text +
   // sorted. In Selected mode: the frozen snapshot, no filtering applied
   // beyond text search (so the user can find a specific item to remove).
   const visibleModels = React.useMemo<CloudLibraryModelInfo[]>(() => {
      const lower = deferredFilter.trim().toLowerCase();

      const matchesText = (m: CloudLibraryModelInfo): boolean => {
         if (!lower) return true;
         return (m.title?.toLowerCase().includes(lower) ?? false)
            || (m.namespaceUri?.toLowerCase().includes(lower) ?? false)
            || (m.description?.toLowerCase().includes(lower) ?? false)
            || (m.keywords?.some(k => k.toLowerCase().includes(lower)) ?? false);
      };

      if (viewMode === 'selected') {
         return snapshot.filter(matchesText);
      }

      const matchesDomain = (m: CloudLibraryModelInfo): boolean => {
         if (domain === ALL_DOMAINS) return true;
         return modelDomain(m) === domain;
      };

      const filtered = (allModels ?? []).filter(m => matchesDomain(m) && matchesText(m));

      const cmp = (a: CloudLibraryModelInfo, b: CloudLibraryModelInfo): number => {
         switch (sortMode) {
            case 'popular':
               return (b.numberOfDownloads ?? -1) - (a.numberOfDownloads ?? -1);
            case 'newest': {
               const da = a.publicationDate ? new Date(a.publicationDate).getTime() : 0;
               const db = b.publicationDate ? new Date(b.publicationDate).getTime() : 0;
               return db - da;
            }
            case 'alpha': {
               const aa = (a.title ?? a.namespaceUri ?? '').toLowerCase();
               const bb = (b.title ?? b.namespaceUri ?? '').toLowerCase();
               return aa.localeCompare(bb);
            }
         }
      };

      return filtered.slice().sort(cmp);
   }, [allModels, viewMode, snapshot, domain, deferredFilter, sortMode]);

   const highlightedModel = React.useMemo<CloudLibraryModelInfo | null>(() => {
      if (!highlightedKey) return null;
      // Look in both the visible list and (for safety) the full set
      return visibleModels.find(m => modelKey(m) === highlightedKey)
          ?? (allModels ?? []).find(m => modelKey(m) === highlightedKey)
          ?? null;
   }, [highlightedKey, visibleModels, allModels]);

   const enterSelectedMode = () => {
      setSnapshot([...selectedModels]);
      setMarkedForRemoval(new Set());
      setViewMode('selected');
   };

   const commitAndExitSelectedMode = () => {
      if (markedForRemoval.size > 0) {
         setSelectedModels(prev => prev.filter(m => !markedForRemoval.has(modelKey(m))));
      }
      setSnapshot([]);
      setMarkedForRemoval(new Set());
      setViewMode('all');
   };

   const handleViewModeChange = (next: ViewMode | null) => {
      if (!next || next === viewMode) return;
      if (next === 'selected') enterSelectedMode();
      else commitAndExitSelectedMode();
   };

   const handleRowClick = (m: CloudLibraryModelInfo) => {
      const key = modelKey(m);
      setHighlightedKey(key);

      if (viewMode === 'selected') {
         // Toggle marked-for-removal (visual only; commit on mode exit)
         setMarkedForRemoval(prev => {
            const next = new Set(prev);
            if (next.has(key)) next.delete(key);
            else next.add(key);
            return next;
         });
      } else {
         // All mode: clicking toggles selection. Selected mode is still
         // available for bulk review/cleanup, but quick deselect-by-reclick
         // is supported here.
         if (selectedKeys.has(key)) {
            setSelectedModels(prev => prev.filter(s => modelKey(s) !== key));
         } else {
            setSelectedModels(prev => [...prev, m]);
         }
      }
   };

   const pendingSelectionCount = viewMode === 'selected'
      ? selectedModels.length - markedForRemoval.size
      : selectedModels.length;

   const handleImport = async () => {
      if (!workspaceId) return;
      // Capture the final list: if user clicks OK while in Selected mode,
      // commit pending removals first.
      const toImport = viewMode === 'selected'
         ? selectedModels.filter(m => !markedForRemoval.has(modelKey(m)))
         : selectedModels;

      if (toImport.length === 0) return;

      setImporting(true);
      setImportError(null);
      try {
         const headers = { 'OpcUa-Server': idToUrn(workspaceId) };
         for (const m of toImport) {
            if (!m.identifier) continue;
            await api.post(
               `/opcua/v1/cloudlibrary/import/${encodeURIComponent(m.identifier)}`,
               null,
               { headers }
            );
         }
         // Importing changes the whole address space, so refresh EVERY workspace-scoped query
         // (namespaces list AND the tree/type queries: subtypes, nodeChildren, queryTypes, …),
         // not just the model list — otherwise a cached (empty) tree persists until restart.
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
         title={t('modelLibrary.importDialogTitle', 'Import Models')}
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
               disabled: pendingSelectionCount === 0 || importing
            }
         ]}
      >
         <Box sx={{
            p: 2,
            display: 'flex',
            flexDirection: 'column',
            gap: 1,
            height: 600
         }}>
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
                     {t('modelLibrary.viewSelected', 'Selected')} ({pendingSelectionCount})
                  </ToggleButton>
               </ToggleButtonGroup>
               <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                  {isFetching
                     ? t('modelLibrary.loadingModels', 'Loading models...')
                     : `${allModels?.length ?? 0} models`}
               </Typography>
               <IconButton size="small" onClick={() => refetch()} title={t('common.refresh', 'Refresh')}>
                  <RefreshIcon fontSize="small" />
               </IconButton>
            </Box>

            {/* Row 2: authority + sort (hidden in Selected mode — neither applies) */}
            {viewMode === 'all' && (
               <Box sx={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 2,
                  mt: 1,
                  px: 2,
                  pt: 4,
                  pb: 1.5,
                  bgcolor: theme.palette.action.hover,
                  borderRadius: 1,
                  overflow: 'visible',
               }}>
                  <TextField
                     select
                     size="small"
                     label={t('modelLibrary.authorityLabel', 'Authority')}
                     value={domain}
                     onChange={(e) => setDomain(e.target.value)}
                     sx={{ minWidth: 220, bgcolor: theme.palette.background.paper }}
                  >
                     <MenuItem value={ALL_DOMAINS}>
                        {t('modelLibrary.authorityAll', 'All authorities')}
                     </MenuItem>
                     {availableDomains.map(d => (
                        <MenuItem key={d} value={d}>{d}</MenuItem>
                     ))}
                  </TextField>
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
                     <ToggleButton value="popular" sx={{ textTransform: 'none' }}>
                        {t('modelLibrary.sortMostPopular', 'Popular')}
                     </ToggleButton>
                     <ToggleButton value="newest" sx={{ textTransform: 'none' }}>
                        {t('modelLibrary.sortNewest', 'Newest')}
                     </ToggleButton>
                     <ToggleButton value="alpha" sx={{ textTransform: 'none' }}>
                        {t('modelLibrary.sortAlpha', 'A–Z')}
                     </ToggleButton>
                  </ToggleButtonGroup>
                  <Box sx={{ flex: 1 }} />
                  <Typography variant="caption" color="text.secondary">
                     {visibleModels.length} shown
                  </Typography>
               </Box>
            )}

            {/* Main list */}
            <Paper variant="outlined" sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
               {visibleModels.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                     {viewMode === 'selected'
                        ? t('modelLibrary.noSelectedModels', 'No models selected. Switch to All to add some.')
                        : t('modelLibrary.noModelsFound', 'No models found matching the search criteria.')}
                  </Typography>
               ) : (
                  <List dense disablePadding>
                     {visibleModels.map(m => {
                        const key = modelKey(m);
                        const isSelected = selectedKeys.has(key);
                        const isMarked = viewMode === 'selected' && markedForRemoval.has(key);
                        const isHighlighted = highlightedKey === key;

                        return (
                           <ListItem key={key} disablePadding divider>
                              <ListItemButton
                                 onClick={() => handleRowClick(m)}
                                 selected={isHighlighted}
                                 sx={{
                                    bgcolor: isSelected && viewMode === 'all'
                                       ? alpha(theme.palette.primary.main, 0.06)
                                       : undefined,
                                    opacity: isMarked ? 0.5 : 1,
                                    textDecoration: isMarked ? 'line-through' : 'none',
                                 }}
                              >
                                 <Box sx={{ width: 28, display: 'flex', justifyContent: 'center' }}>
                                    {isSelected && !isMarked && (
                                       <CheckIcon fontSize="small" color="primary" />
                                    )}
                                 </Box>
                                 <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 }}>
                                    <Typography variant="body2" noWrap sx={{ fontWeight: isSelected ? 600 : 400 }}>
                                       {m.title ?? m.namespaceUri ?? '(unnamed)'}
                                    </Typography>
                                    <Typography variant="caption" color="text.secondary" noWrap>
                                       {m.namespaceUri}
                                    </Typography>
                                 </Box>
                                 <Box sx={{ display: 'flex', alignItems: 'stretch', ml: 2 }}>
                                    <MetaCell width={60}>
                                       {m.version ? `v${m.version}` : '—'}
                                    </MetaCell>
                                    <MetaCell width={90}>
                                       {formatDate(m.publicationDate) ?? '—'}
                                    </MetaCell>
                                    <MetaCell width={70}>
                                       {typeof m.numberOfDownloads === 'number'
                                          ? `↓ ${m.numberOfDownloads.toLocaleString()}`
                                          : '—'}
                                    </MetaCell>
                                 </Box>
                              </ListItemButton>
                           </ListItem>
                        );
                     })}
                  </List>
               )}
            </Paper>

            {/* Bottom: Info panel for the highlighted model */}
            <Paper variant="outlined" sx={{
               p: 2,
               minHeight: 100,
               maxHeight: 160,
               overflow: 'auto'
            }}>
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

/**
 * Right-aligned, fixed-width column cell with a left divider, used for
 * the per-row metadata strip (version / date / downloads). Fixed widths
 * keep values vertically aligned across rows.
 */
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

interface ModelInfoPanelProps {
   model: CloudLibraryModelInfo;
}

const ModelInfoPanel: React.FC<ModelInfoPanelProps> = ({ model }) => {
   const { t } = useTranslation();
   const publishedDate = formatDate(model.publicationDate);
   return (
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
         <Typography variant="subtitle1" sx={{ fontWeight: 'bold' }} noWrap>
            {model.title ?? model.namespaceUri ?? '(unnamed)'}
         </Typography>
         {model.namespaceUri && (
            <Typography variant="caption" color="text.secondary" noWrap>
               {model.namespaceUri}
            </Typography>
         )}
         {(() => {
            const parts = [
               model.version && `${t('modelLibrary.versionLabel', 'Version')}: ${model.version}`,
               publishedDate && `${t('modelLibrary.publishedLabel', 'Published')}: ${publishedDate}`,
               model.license && `${t('modelLibrary.licenseLabel', 'License')}: ${model.license}`,
               typeof model.numberOfDownloads === 'number'
                  && `${t('modelLibrary.downloadsLabel', 'Downloads')}: ${model.numberOfDownloads}`,
            ].filter(Boolean);
            return parts.length > 0 ? (
               <Typography variant="caption" color="text.secondary">
                  {parts.join(' | ')}
               </Typography>
            ) : null;
         })()}
         {model.description && (
            <Typography variant="body2">
               {model.description}
            </Typography>
         )}
         {model.keywords && model.keywords.length > 0 && (
            <Box sx={{ display: 'flex', gap: 0.5, flexWrap: 'wrap', mt: 0.5 }}>
               {model.keywords.map(k => (
                  <Chip key={k} label={k} size="small" variant="outlined" />
               ))}
            </Box>
         )}
      </Box>
   );
};

export default ImportModelDialog;
