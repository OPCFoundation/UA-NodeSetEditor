import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import IconButton from '@mui/material/IconButton';
import LinearProgress from '@mui/material/LinearProgress';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemIcon from '@mui/material/ListItemIcon';
import Avatar from '@mui/material/Avatar';
import CircularProgress from '@mui/material/CircularProgress';
import FormControl from '@mui/material/FormControl';
import InputLabel from '@mui/material/InputLabel';
import MenuItem from '@mui/material/MenuItem';
import Select, { type SelectChangeEvent } from '@mui/material/Select';
import { useTheme } from '@mui/material/styles';

import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import UploadFileIcon from '@mui/icons-material/UploadFile';
import DescriptionIcon from '@mui/icons-material/Description';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import CancelIcon from '@mui/icons-material/Cancel';
import VisibilityIcon from '@mui/icons-material/Visibility';
import RestartAltIcon from '@mui/icons-material/RestartAlt';

import api, { ApiError } from '../api/axios.api';
import { WorkspaceContext } from '../WorkspaceContext';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { ModelDialog } from '../components/ModelDialog';
import { SearchBar } from '../components/SearchBar';
import { ActionBar, type ActionBarItem } from '../components/ActionBar';
import { ValidationResultsView } from '../components/ValidationResultsView';
import { EditValidationOptionsDialog } from '../components/EditValidationOptionsDialog';
import type { ValidationDocumentInfo, DocumentUploadResult } from '../model/Validation';

const CHUNK_SIZE = 4 * 1024 * 1024; // 4MB

const formatBytes = (bytes: number): string => {
   if (bytes < 1024) return `${bytes} B`;
   if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
   return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
};

const statusChip = (status: string): { color: 'default' | 'primary' | 'success' | 'error' | 'info'; key: string } => {
   switch (status) {
      case 'Queued': return { color: 'info', key: 'validation.statusQueued' };
      case 'Running': return { color: 'primary', key: 'validation.statusRunning' };
      case 'Completed': return { color: 'success', key: 'validation.statusCompleted' };
      case 'Failed': return { color: 'error', key: 'validation.statusFailed' };
      default: return { color: 'default', key: 'validation.statusReady' };
   }
};

const ValidationPage: React.FC = () => {
   const { t } = useTranslation();
   const theme = useTheme();
   const navigate = useNavigate();
   const queryClient = useQueryClient();
   const [searchParams, setSearchParams] = useSearchParams();
   // The model a new job runs against is the workspace's context selection (shared with the
   // Model Library / Type Library), surfaced in the namespace dropdown below.
   const { selectedWorkspaceId, selectedModelUri, setSelectedModelUri } = React.useContext(WorkspaceContext);

   // Seed the context selection from the ?ns= deep-link (Model Library's Validate icon) so
   // arriving at the page pre-selects that namespace in the dropdown.
   React.useEffect(() => {
      const ns = searchParams.get('ns');
      if (ns && ns !== selectedModelUri) setSelectedModelUri(ns);
      // eslint-disable-next-line react-hooks/exhaustive-deps
   }, [searchParams]);

   // Free-text filter over the document list (mirrors the type-definition view's search).
   const [filter, setFilter] = React.useState('');

   const fileInputRef = React.useRef<HTMLInputElement>(null);
   const [isUploading, setIsUploading] = React.useState(false);
   const [uploadProgress, setUploadProgress] = React.useState(0);
   const [uploadError, setUploadError] = React.useState<string | null>(null);
   const [busyJobId, setBusyJobId] = React.useState<string | null>(null);

   const [docToDelete, setDocToDelete] = React.useState<ValidationDocumentInfo | null>(null);
   const [editDoc, setEditDoc] = React.useState<ValidationDocumentInfo | null>(null);

   // The results drill-down lives in the URL (?job=…) so it survives navigating away to a
   // Type page and back — browser Back restores the results view instead of the document list.
   const resultsJob = React.useMemo(() => {
      const jobId = searchParams.get('job');
      if (!jobId) return null;
      return {
         jobId,
         name: searchParams.get('jobName') ?? '',
         modelUri: searchParams.get('jobModel') ?? undefined,
      };
   }, [searchParams]);

   const openResults = (doc: ValidationDocumentInfo) => {
      if (!doc.jobId) return;
      setSearchParams((prev) => {
         const next = new URLSearchParams(prev);
         next.set('job', doc.jobId!);
         next.set('jobName', doc.fileName);
         if (doc.modelUri) next.set('jobModel', doc.modelUri); else next.delete('jobModel');
         return next;
      });
   };

   const closeResults = () => {
      setSearchParams((prev) => {
         const next = new URLSearchParams(prev);
         next.delete('job');
         next.delete('jobName');
         next.delete('jobModel');
         return next;
      });
   };

   const header = React.useMemo(
      () => (selectedWorkspaceId ? { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } : undefined),
      [selectedWorkspaceId]);

   const queryKey = ['validation-docs', selectedWorkspaceId];

   const { data: documents, isLoading, refetch } = useQuery<ValidationDocumentInfo[]>({
      queryKey,
      queryFn: async () => {
         const res = await api.get<ValidationDocumentInfo[]>(
            `/opcua/v1/validation/documents`, { headers: header });
         return res.data;
      },
      enabled: !!selectedWorkspaceId,
      // Poll while a job is in flight so status transitions surface without a manual refresh.
      refetchInterval: (query) => {
         const docs = query.state.data as ValidationDocumentInfo[] | undefined;
         return docs?.some(d => d.status === 'Queued' || d.status === 'Running') ? 3000 : false;
      },
   });

   const invalidate = () => queryClient.invalidateQueries({ queryKey });

   // Namespaces for the model dropdown — only PRIVATE (editable) models can be validated,
   // so the list is filtered to those. Shares the ['namespaces', ws] cache with other pages.
   const { data: namespacesData } = useQuery<PaginatedResponse<WorkspaceNamespaceInfo>>({
      queryKey: ['namespaces', selectedWorkspaceId],
      queryFn: async () => {
         const res = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
            '/opcua/v1/namespaces/info', { headers: header });
         return res.data;
      },
      enabled: !!selectedWorkspaceId,
   });

   const privateModels = React.useMemo(
      () => (namespacesData?.results ?? []).filter(n => n.isPrivate && n.id && n.uri),
      [namespacesData]);

   // The dropdown is keyed by namespace URI (matching the context selection); the job-start
   // API needs the model's GUID, so resolve it from the chosen private model.
   const selectedModelId = React.useMemo(
      () => privateModels.find(n => n.uri === selectedModelUri)?.id ?? '',
      [privateModels, selectedModelUri]);

   // Guard the Select value: only a private-model URI is a valid option, so fall back to
   // empty otherwise (avoids MUI's out-of-range value warning).
   const dropdownValue = privateModels.some(n => n.uri === selectedModelUri) ? selectedModelUri : '';

   const handleNamespaceChange = (e: SelectChangeEvent<string>) => setSelectedModelUri(e.target.value);

   const filteredDocuments = React.useMemo(() => {
      const needle = filter.trim().toLowerCase();
      const docs = documents ?? [];
      return needle ? docs.filter(d => d.fileName.toLowerCase().includes(needle)) : docs;
   }, [documents, filter]);

   const handlePickFile = () => fileInputRef.current?.click();

   const handleFileSelected = async (e: React.ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      e.target.value = ''; // allow re-selecting the same file
      if (!file || !selectedWorkspaceId) return;

      if (!file.name.toLowerCase().endsWith('.docx')) {
         setUploadError(t('validation.onlyDocx'));
         return;
      }

      setIsUploading(true);
      setUploadProgress(0);
      setUploadError(null);
      try {
         const totalChunks = Math.max(1, Math.ceil(file.size / CHUNK_SIZE));
         let uploadId: string | undefined;

         for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++) {
            const start = chunkIndex * CHUNK_SIZE;
            const chunk = file.slice(start, Math.min(start + CHUNK_SIZE, file.size));

            const formData = new FormData();
            formData.append('file', chunk, file.name);
            formData.append('fileName', file.name);
            formData.append('chunkIndex', chunkIndex.toString());
            formData.append('totalChunks', totalChunks.toString());
            if (uploadId) formData.append('uploadId', uploadId);

            const res = await api.post<DocumentUploadResult>(
               `/opcua/v1/validation/documents/upload`, formData, {
               headers: { 'Content-Type': 'multipart/form-data', ...header },
               onUploadProgress: (evt) => {
                  const frac = evt.total ? evt.loaded / evt.total : 0;
                  setUploadProgress(Math.min(1, (chunkIndex + frac) / totalChunks));
               },
            });
            if (!res.data.isComplete) uploadId = res.data.uploadId;
         }
         setUploadProgress(1);
         await invalidate();
      } catch (err) {
         setUploadError(err instanceof ApiError ? err.message
            : (err instanceof Error ? err.message : t('validation.uploadFailed')));
      } finally {
         setIsUploading(false);
      }
   };

   const runJobAction = async (jobId: string, action: () => Promise<unknown>) => {
      setBusyJobId(jobId);
      try {
         await action();
         await invalidate();
      } finally {
         setBusyJobId(null);
      }
   };

   const handleStart = (doc: ValidationDocumentInfo) =>
      runJobAction(doc.id, () => api.post(
         `/opcua/v1/validation/documents/${doc.id}/jobs?modelId=${encodeURIComponent(selectedModelId)}`,
         null, { headers: header }));

   const handleCancel = (doc: ValidationDocumentInfo) =>
      doc.jobId && runJobAction(doc.jobId, () => api.post(
         `/opcua/v1/validation/jobs/${doc.jobId}/cancel`, null, { headers: header }));

   const handleReset = (doc: ValidationDocumentInfo) =>
      doc.jobId && runJobAction(doc.jobId, () => api.post(
         `/opcua/v1/validation/jobs/${doc.jobId}/reset`, null, { headers: header }));

   const handleDeleteConfirmed = async () => {
      if (!docToDelete) return;
      await api.delete(`/opcua/v1/validation/documents/${docToDelete.id}`, { headers: header });
      setDocToDelete(null);
      await invalidate();
   };

   if (!selectedWorkspaceId) {
      return <Typography sx={{ p: 3 }} color="text.secondary">{t('validation.noWorkspace')}</Typography>;
   }

   // Drill-down: viewing a job's results replaces the whole page (like the model/type detail view).
   if (resultsJob) {
      return (
         <ValidationResultsView
            workspaceId={selectedWorkspaceId}
            jobId={resultsJob.jobId}
            documentName={resultsJob.name}
            modelUri={resultsJob.modelUri}
            onBack={() => { closeResults(); refetch(); }}
         />
      );
   }

   return (
      <Box sx={{ px: 8, pt: 14 }}>
         {/* pt:14 above + mb:14 below (SearchBar pt zeroed) => equal whitespace around the Upload button. */}
         <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, mb: 14 }}>
            <Tooltip title={t('validation.backToModels')}>
               <IconButton onClick={() => navigate('/model_library')} size="small"><ArrowBackIcon /></IconButton>
            </Tooltip>
            <Typography variant="h5" sx={{ fontWeight: 'bolder' }}>{t('validation.pageTitle', 'Validate Model')}</Typography>
            <Box sx={{ flexGrow: 1 }} />
            <Button
               variant="contained"
               startIcon={<UploadFileIcon />}
               onClick={handlePickFile}
               disabled={isUploading}
               sx={{ px: '24px', borderRadius: '50px', flexShrink: 0 }}
            >
               {t('validation.uploadDocument')}
            </Button>
            <input ref={fileInputRef} type="file" accept=".docx" hidden onChange={handleFileSelected} />
         </Box>

         <SearchBar
            hint={t('validation.searchHintDocuments', 'Search documents…')}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            onRefresh={() => refetch()}
            sx={{ px: 0, pt: 0 }}
         >
            <FormControl size="small" sx={{ width: { xs: '100%', md: 500 }, minWidth: 0, ml: { md: 2 } }}>
               <InputLabel id="validation-namespace-label">{t('validation.modelNamespace', 'Model namespace')}</InputLabel>
               <Select
                  labelId="validation-namespace-label"
                  id="validation-namespace"
                  value={dropdownValue}
                  label={t('validation.modelNamespace', 'Model namespace')}
                  onChange={handleNamespaceChange}
               >
                  <MenuItem value="">
                     <em>{t('validation.noModelSelected', 'No model selected')}</em>
                  </MenuItem>
                  {privateModels.map((ns) => (
                     <MenuItem key={ns.id} value={ns.uri}>
                        {ns.name ? `${ns.name} - ${ns.uri}` : ns.uri}
                     </MenuItem>
                  ))}
               </Select>
            </FormControl>
         </SearchBar>

         {!selectedModelId && (
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
               {t('validation.noSelectedModel')}
            </Typography>
         )}

         {isUploading && (
            <Box sx={{ mb: 2 }}>
               <Typography variant="body2" color="text.secondary">
                  {t('validation.uploading', { percent: Math.round(uploadProgress * 100) })}
               </Typography>
               <LinearProgress variant="determinate" value={uploadProgress * 100} />
            </Box>
         )}
         {uploadError && <Typography color="error" sx={{ mb: 2 }}>{uploadError}</Typography>}

         <List>
            {filteredDocuments.map((doc) => {
               const chip = statusChip(doc.status);
               const busy = busyJobId === doc.id || busyJobId === doc.jobId;
               const isRunning = doc.status === 'Running';
               const isQueued = doc.status === 'Queued';
               const isCompleted = doc.status === 'Completed' || doc.status === 'Failed';
               const isReady = doc.status === 'Ready';

               const actions: ActionBarItem[] = [
                  {
                     onAction: () => handleStart(doc),
                     icon: <PlayArrowIcon />,
                     tooltipKey: selectedModelId ? 'validation.start' : 'validation.noSelectedModel',
                     hidden: !isReady,
                     disabled: busy || !selectedModelId,
                  },
                  {
                     onAction: () => handleCancel(doc),
                     icon: <CancelIcon />,
                     tooltipKey: 'validation.cancelJob',
                     hidden: !(isQueued || isRunning),
                     disabled: busy,
                  },
                  {
                     onAction: () => openResults(doc),
                     icon: <VisibilityIcon />,
                     tooltipKey: 'validation.viewResults',
                     hidden: !isCompleted,
                     disabled: !doc.jobId,
                  },
                  {
                     onAction: () => handleReset(doc),
                     icon: <RestartAltIcon />,
                     tooltipKey: 'validation.reset',
                     hidden: !isCompleted,
                     disabled: busy,
                  },
                  {
                     onAction: () => setEditDoc(doc),
                     icon: <EditIcon />,
                     tooltipKey: 'validation.editOptions',
                     disabled: isRunning || isQueued,
                  },
                  {
                     onAction: () => setDocToDelete(doc),
                     icon: <DeleteIcon />,
                     tooltipKey: 'validation.delete',
                     disabled: isRunning || isQueued,
                  },
               ];

               return (
                  <ListItem
                     key={doc.id}
                     sx={{
                        borderTopStyle: 'solid',
                        borderTopColor: theme.palette.grey[200],
                        borderTopWidth: '2px',
                        '&:hover': { backgroundColor: 'action.hover' },
                     }}
                  >
                     <ListItemIcon>
                        <Avatar sx={{ width: 32, height: 32, bgcolor: theme.palette.grey[600], color: theme.palette.grey[200] }}>
                           <DescriptionIcon />
                        </Avatar>
                     </ListItemIcon>
                     <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, overflow: 'hidden' }}>
                        <Typography variant="body1" component="div" noWrap sx={{ fontWeight: 'bold' }}>
                           {doc.fileName}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                           {t('validation.columnSize')}: {formatBytes(doc.sizeBytes)}
                           {' | '}{t('validation.columnUploaded')}: {new Date(doc.uploadedUtc).toLocaleString()}
                           {isCompleted && doc.errorCount != null && (
                              <> {' | '}{t('validation.resultsSummary', {
                                 errors: doc.errorCount, warnings: doc.warningCount ?? 0, infos: doc.infoCount ?? 0,
                              })}</>
                           )}
                        </Typography>
                        {doc.modelUri && (
                           <Typography variant="caption" color="text.secondary" noWrap sx={{ textOverflow: 'ellipsis', overflow: 'hidden' }}>
                              {t('validation.appliedModel', { model: doc.modelUri })}
                           </Typography>
                        )}
                     </Box>
                     {/* Status badge: right-aligned (text column has flex:1) and vertically
                         centered by the ListItem's default center alignment. */}
                     <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexShrink: 0, ml: 8, mr: 12 }}>
                        {busy && <CircularProgress size={16} />}
                        <Chip size="small" color={chip.color} label={t(chip.key)} />
                     </Box>
                     <ActionBar actions={actions} />
                  </ListItem>
               );
            })}
            {filteredDocuments.length === 0 && !isLoading && (
               <ListItem>
                  <Typography variant="body2" color="text.secondary">
                     {(documents?.length ?? 0) === 0 ? t('validation.noDocuments') : t('validation.noMatchingDocuments', 'No documents match your search.')}
                  </Typography>
               </ListItem>
            )}
         </List>

         {docToDelete && (
            <ModelDialog
               open
               onClose={() => setDocToDelete(null)}
               title={t('validation.confirmDeleteTitle')}
               actions={[{ label: t('validation.delete'), color: 'error', onClick: handleDeleteConfirmed }]}
            >
               <Box sx={{ px: 20, py: 20 }}>
                  <Typography>{t('validation.confirmDeleteBody', { name: docToDelete.fileName })}</Typography>
               </Box>
            </ModelDialog>
         )}

         {editDoc && (
            <EditValidationOptionsDialog
               open
               onClose={() => setEditDoc(null)}
               workspaceId={selectedWorkspaceId}
               doc={editDoc}
            />
         )}
      </Box>
   );
};

export default ValidationPage;
