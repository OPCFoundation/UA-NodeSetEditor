import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Chip from '@mui/material/Chip';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Typography from '@mui/material/Typography';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import FormControl from '@mui/material/FormControl';
import InputLabel from '@mui/material/InputLabel';
import MenuItem from '@mui/material/MenuItem';
import Select, { type SelectChangeEvent } from '@mui/material/Select';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import FactCheckIcon from '@mui/icons-material/FactCheck';
import DownloadIcon from '@mui/icons-material/Download';

import { SearchBar } from './SearchBar';
import { ContentLoader } from './ContentLoader';
import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import { getNodePlainName } from '../model/Node';
import type { Node } from '../model/Node';
import { nodeClassToNum } from '../model/NodeFormatting';
import type { ValidationResult, ValidationEntry } from '../model/Validation';

interface Props {
   workspaceId: string;
   jobId: string;
   documentName?: string;
   /** Namespace URI of the model the job ran against — used to resolve Section
       names to types in that model so they can be linked to the Type page. */
   modelUri?: string;
   onBack: () => void;
}

type Severity = 'Error' | 'Warning' | 'Info';

const severityColor = (s?: string): 'error' | 'warning' | 'info' | 'default' => {
   switch ((s ?? '').toLowerCase()) {
      case 'error': return 'error';
      case 'warning': return 'warning';
      case 'info': return 'info';
      default: return 'default';
   }
};

// Column model. `wrap` columns keep their text flowing (Description); all others
// truncate with an ellipsis. `width` seeds the resizable width (px).
interface Column {
   key: keyof ValidationEntry;
   labelKey: string;
   defaultLabel: string;
   width: number;
   wrap?: boolean;
}

const COLUMNS: Column[] = [
   { key: 'severity', labelKey: 'validation.colSeverity', defaultLabel: 'Severity', width: 110 },
   { key: 'section', labelKey: 'validation.colSection', defaultLabel: 'Section', width: 280 },
   { key: 'table', labelKey: 'validation.colTable', defaultLabel: 'Table', width: 180 },
   { key: 'code', labelKey: 'validation.colCode', defaultLabel: 'Code', width: 150 },
   { key: 'description', labelKey: 'validation.colDescription', defaultLabel: 'Description', width: 560, wrap: true },
];

const MIN_COL_WIDTH = 60;

// Plain browse name from a "nsu=URI;Name" (or "Model:Name") BrowseName. Types in a
// validation finding's Section column are referenced by this plain name.
const plainBrowseName = (browseName?: string): string => {
   const b = browseName ?? '';
   const semi = b.indexOf(';');
   if (semi >= 0) return b.substring(semi + 1);
   const colon = b.indexOf(':');
   if (colon > 0) return b.substring(colon + 1);
   return b;
};

export const ValidationResultsView: React.FC<Props> = ({ workspaceId, jobId, documentName, modelUri, onBack }) => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   const [search, setSearch] = React.useState('');
   const [severities, setSeverities] = React.useState<Severity[]>(['Error', 'Warning', 'Info']);
   const [codeFilter, setCodeFilter] = React.useState('');
   const [widths, setWidths] = React.useState<number[]>(() => COLUMNS.map(c => c.width));

   const { data, isLoading, isError, error } = useQuery<ValidationResult>({
      queryKey: ['validation-result', workspaceId, jobId],
      queryFn: async () => {
         const res = await api.get<ValidationResult>(`/opcua/v1/validation/jobs/${jobId}/result`, {
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return res.data;
      },
      enabled: !!jobId && !!workspaceId,
   });

   // All types in the validated model, used to turn a Section name into a link to its
   // Type page. Fetched once (large count) and indexed by plain BrowseName below.
   const { data: typesData } = useQuery<PaginatedResponse<Node>>({
      queryKey: ['queryTypes', workspaceId, modelUri, 'validation-section-links'],
      queryFn: async () => {
         const res = await api.get<PaginatedResponse<Node>>('/opcua/v1/query/types', {
            params: { namespaceUri: modelUri, start: 0, count: 100000 },
            headers: { 'OpcUa-Server': idToUrn(workspaceId) },
         });
         return res.data;
      },
      enabled: !!workspaceId && !!modelUri,
      staleTime: 5 * 60 * 1000,
   });

   // BrowseName -> type node. First match wins on collision (browse names are unique
   // within a single model in practice).
   const typeByName = React.useMemo(() => {
      const m = new Map<string, Node>();
      for (const n of typesData?.results ?? []) {
         const name = plainBrowseName(n.browseName);
         if (name && !m.has(name)) m.set(name, n);
      }
      return m;
   }, [typesData]);

   // Deep-link into the Type Library detail view for the given type (same workspace,
   // selected from context there; ns preselects the namespace filter).
   const openType = React.useCallback((node: Node) => {
      const params = new URLSearchParams({
         type: node.nodeId,
         name: getNodePlainName(node),
         nc: String(nodeClassToNum(node.nodeClass)),
      });
      if (modelUri) params.set('ns', modelUri);
      navigate(`/type_library?${params.toString()}`);
   }, [navigate, modelUri]);

   // Render a Section value as a mix of links and plain text: split on whitespace and turn
   // any word that matches a Type's BrowseName in the validated model into a link to its
   // Type page, leaving the rest as plain text. Sections read like a heading — e.g.
   // "7.1 STSysMaterialTransportLineType ObjectType Definition" links only the type name.
   const renderSection = React.useCallback((section?: string): React.ReactNode => {
      const s = section ?? '';
      if (!s) return '';
      // Split on whitespace but keep the whitespace runs as tokens so spacing round-trips.
      const tokens = s.split(/(\s+)/);
      let linked = false;
      const parts = tokens.map((tok, idx) => {
         const type = tok.trim() ? typeByName.get(tok) : undefined;
         if (!type) return tok;
         linked = true;
         return (
            <Link
               key={idx}
               component="button"
               type="button"
               underline="hover"
               onClick={() => openType(type)}
               sx={{ textAlign: 'left', verticalAlign: 'baseline' }}
            >
               {tok}
            </Link>
         );
      });
      return linked ? <>{parts}</> : s;
   }, [typeByName, openType]);

   // Drag-to-resize a column header. Captures the starting X + width and updates on mousemove
   // until the button is released; listeners live on window so the drag survives leaving the header.
   const startResize = React.useCallback((index: number, e: React.MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      const startX = e.clientX;
      let startW = 0;
      setWidths(w => { startW = w[index]; return w; });
      const onMove = (ev: MouseEvent) => {
         const delta = ev.clientX - startX;
         setWidths(w => {
            const next = [...w];
            next[index] = Math.max(MIN_COL_WIDTH, startW + delta);
            return next;
         });
      };
      const onUp = () => {
         window.removeEventListener('mousemove', onMove);
         window.removeEventListener('mouseup', onUp);
      };
      window.addEventListener('mousemove', onMove);
      window.addEventListener('mouseup', onUp);
   }, []);

   // Distinct codes present in this file's findings, for the Code dropdown filter.
   const codes = React.useMemo(() => {
      const set = new Set<string>();
      for (const e of data?.entries ?? []) if (e.code) set.add(e.code);
      return Array.from(set).sort((a, b) => a.localeCompare(b));
   }, [data]);

   // If the selected code disappears (new data), reset to "all".
   React.useEffect(() => {
      if (codeFilter && !codes.includes(codeFilter)) setCodeFilter('');
   }, [codes, codeFilter]);

   const filtered = React.useMemo(() => {
      const entries = data?.entries ?? [];
      const q = search.trim().toLowerCase();
      return entries.filter((e: ValidationEntry) => {
         const sev = (e.severity ?? '') as Severity;
         if (severities.length > 0 && !severities.some(s => s.toLowerCase() === sev.toLowerCase())) return false;
         if (codeFilter && e.code !== codeFilter) return false;
         if (!q) return true;
         return [e.section, e.table, e.code, e.description]
            .some(v => (v ?? '').toLowerCase().includes(q));
      });
   }, [data, search, severities, codeFilter]);

   // Export the currently filtered findings as CSV — one row per finding, columns in the
   // same order as the table. Values are escaped per RFC 4180 (quote-wrap + double any quotes)
   // so embedded commas/quotes/newlines survive. A leading BOM makes Excel read it as UTF-8.
   const downloadCsv = React.useCallback(() => {
      const escape = (v: unknown) => {
         const s = v == null ? '' : String(v);
         return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
      };
      const header = COLUMNS.map(c => escape(t(c.labelKey, c.defaultLabel)));
      const rows = filtered.map(e => COLUMNS.map(c => escape(e[c.key])).join(','));
      const csv = '﻿' + [header.join(','), ...rows].join('\r\n');
      const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const base = (documentName ?? 'validation-results').replace(/\.[^./\\]+$/, '');
      a.href = url;
      a.download = `${base}.csv`;
      a.click();
      URL.revokeObjectURL(url);
   }, [filtered, documentName, t]);

   // The wrap column (Description) is the flexible one: it has no fixed width, so with
   // tableLayout:fixed + width:100% it absorbs all remaining horizontal space. The fixed
   // columns' combined width (plus a floor for Description) becomes the table's minWidth,
   // so the container scrolls only when the window is too narrow to honor it.
   const fixedTotal = COLUMNS.reduce((sum, c, i) => (c.wrap ? sum : sum + widths[i]), 0);
   const tableMinWidth = fixedTotal + 240;

   const cellSx = (wrap?: boolean) => wrap
      ? { whiteSpace: 'normal' as const, wordBreak: 'break-word' as const, verticalAlign: 'top' as const }
      : { whiteSpace: 'nowrap' as const, overflow: 'hidden', textOverflow: 'ellipsis', verticalAlign: 'top' as const };

   return (
      <Box sx={{ px: 8, pt: 14 }}>
         {/* Drill-down header: back to the document list + title, mirroring the model/type detail view. */}
         <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, mb: 14 }}>
            <Tooltip title={t('validation.backToDocuments', 'Back to documents')}>
               <IconButton onClick={onBack} size="small"><ArrowBackIcon /></IconButton>
            </Tooltip>
            <FactCheckIcon />
            <Typography variant="h5" noWrap sx={{ fontWeight: 'bolder', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
               {documentName
                  ? `${t('validation.resultsTitle')} — ${documentName}`
                  : t('validation.resultsTitle')}
            </Typography>
         </Box>

         <Stack direction="row" spacing={4} sx={{ mb: 4 }}>
            <Chip color="error" label={`${data?.errorCount ?? 0} ${t('validation.severityError')}`} size="small" />
            <Chip color="warning" label={`${data?.warningCount ?? 0} ${t('validation.severityWarning')}`} size="small" />
            <Chip color="info" label={`${data?.infoCount ?? 0} ${t('validation.severityInfo')}`} size="small" />
         </Stack>

         <SearchBar
            hint={t('validation.searchHint', 'Search findings…')}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            sx={{ px: 0, pt: 0, mb: 4 }}
            rightActions={
               <Tooltip title={t('validation.downloadCsv', 'Download CSV')}>
                  <span>
                     <IconButton
                        onClick={downloadCsv}
                        size="small"
                        disabled={filtered.length === 0}
                        aria-label={t('validation.downloadCsv', 'Download CSV')}
                     >
                        <DownloadIcon />
                     </IconButton>
                  </span>
               </Tooltip>
            }
         >
            <ToggleButtonGroup
               size="small"
               value={severities}
               onChange={(_e, next: Severity[]) => setSeverities(next)}
               aria-label={t('validation.colSeverity')}
               sx={{ ml: { md: 2 } }}
            >
               <ToggleButton value="Error" color="error">{t('validation.severityError')}</ToggleButton>
               <ToggleButton value="Warning" color="warning">{t('validation.severityWarning')}</ToggleButton>
               <ToggleButton value="Info" color="info">{t('validation.severityInfo')}</ToggleButton>
            </ToggleButtonGroup>
            <FormControl size="small" sx={{ width: { xs: '100%', md: 260 }, minWidth: 0, ml: { md: 2 } }}>
               <InputLabel id="validation-code-label">{t('validation.filterCode', 'Code')}</InputLabel>
               <Select
                  labelId="validation-code-label"
                  id="validation-code"
                  value={codeFilter}
                  label={t('validation.filterCode', 'Code')}
                  onChange={(e: SelectChangeEvent<string>) => setCodeFilter(e.target.value)}
               >
                  <MenuItem value="">
                     <em>{t('validation.allCodes', 'All codes')}</em>
                  </MenuItem>
                  {codes.map((c) => (
                     <MenuItem key={c} value={c}>{c}</MenuItem>
                  ))}
               </Select>
            </FormControl>
         </SearchBar>

         <ContentLoader isError={isError} isLoading={isLoading} error={error as Error | null}>
            <Box sx={{ overflowX: 'auto' }}>
               <Table size="small" stickyHeader sx={{ tableLayout: 'fixed', width: '100%', minWidth: tableMinWidth }}>
                  <colgroup>
                     {COLUMNS.map((col, i) => (
                        <col key={col.key} style={{ width: col.wrap ? undefined : widths[i] }} />
                     ))}
                  </colgroup>
                  <TableHead>
                     <TableRow>
                        {COLUMNS.map((col, i) => (
                           <TableCell
                              key={col.key}
                              sx={{
                                 position: 'relative',
                                 fontWeight: 'bold',
                                 whiteSpace: 'nowrap',
                                 overflow: 'hidden',
                                 textOverflow: 'ellipsis',
                                 userSelect: 'none',
                                 pr: 3,
                              }}
                           >
                              {t(col.labelKey, col.defaultLabel)}
                              {/* Resize handle: a persistent vertical divider (always visible so the
                                  column reads as resizable) that thickens/highlights on hover + drag.
                                  The flexible Description column has no handle — it fills remaining space. */}
                              {!col.wrap && (
                                 <Box
                                    onMouseDown={(e) => startResize(i, e)}
                                    sx={{
                                       position: 'absolute',
                                       top: '15%',
                                       right: 0,
                                       height: '70%',
                                       width: '10px',
                                       cursor: 'col-resize',
                                       borderRight: '2px solid',
                                       borderColor: 'grey.400',
                                       transition: 'border-color 0.15s',
                                       '&:hover': { borderColor: 'primary.main', borderRightWidth: '3px' },
                                    }}
                                 />
                              )}
                           </TableCell>
                        ))}
                     </TableRow>
                  </TableHead>
                  <TableBody>
                     {filtered.map((e, i) => (
                        <TableRow key={i} hover>
                           {COLUMNS.map((col) => (
                              // The Section value links each word that matches a Type's
                              // BrowseName in the validated model to its Type page.
                              <TableCell key={col.key} sx={cellSx(col.wrap)}>
                                 {col.key === 'severity'
                                    ? <Chip size="small" color={severityColor(e.severity)} label={e.severity ?? ''} />
                                    : col.key === 'section'
                                       ? renderSection(e.section)
                                       : (e[col.key] ?? '')}
                              </TableCell>
                           ))}
                        </TableRow>
                     ))}
                     {filtered.length === 0 && !isLoading && (
                        <TableRow>
                           <TableCell colSpan={COLUMNS.length}>
                              <Typography variant="body2" color="text.secondary">
                                 {t('validation.noEntries')}
                              </Typography>
                           </TableCell>
                        </TableRow>
                     )}
                  </TableBody>
               </Table>
            </Box>
         </ContentLoader>
      </Box>
   );
};
