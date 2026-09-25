import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import axios from 'axios';
import api, { ApiError } from '../api/axios.api';

import { useQuery, useQueryClient } from '@tanstack/react-query';

import EditDocumentIcon from '@mui/icons-material/EditDocument';
import DescriptionIcon from '@mui/icons-material/Description';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import AccountTreeIcon from '@mui/icons-material/AccountTree';
import DownloadIcon from '@mui/icons-material/Download';
import DeleteIcon from '@mui/icons-material/Delete';
import FactCheckIcon from '@mui/icons-material/FactCheck';
import EditIcon from '@mui/icons-material/Edit';
import VisibilityIcon from '@mui/icons-material/Visibility';
import HistoryIcon from '@mui/icons-material/History';
import LockIcon from '@mui/icons-material/Lock';
import LockOpenIcon from '@mui/icons-material/LockOpen';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Button from '@mui/material/Button';
import MenuItem from '@mui/material/MenuItem';
import Menu from '@mui/material/Menu';
import FormControlLabel from '@mui/material/FormControlLabel';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemIcon from '@mui/material/ListItemIcon';
import Avatar from '@mui/material/Avatar';
import Radio from '@mui/material/Radio';
import RadioGroup from '@mui/material/RadioGroup';
import Checkbox from '@mui/material/Checkbox';
import TextField from '@mui/material/TextField';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';


import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';

import { SearchBar } from '../components/SearchBar';
import { ContentLoader } from '../components/ContentLoader';
import { ModelDialog } from '../components/ModelDialog';
import { ActionBar } from '../components/ActionBar';
import { WorkspaceSelector } from '../components/WorkspaceSelector';
import TruncatedText from '../components/TruncatedText';
import { WorkspaceContext } from '../WorkspaceContext';
import { UserContext } from '../UserContext';
import { alpha } from '@mui/material/styles';
interface ModelInfo {
   id?: string | null;
   modelUri?: string | null;
   name?: string | null;
   modelVersion?: string | null;
   publicationDate?: string | null;
   description?: { text?: string | null } | null;
}

import { urnToId, idToUrn } from '../model/WorkspaceDescription';
import type { WorkspaceDescription, PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';
import { useTheme } from '@mui/material/styles';
import { ImportModelDialog } from '../components/ImportModelDialog';
import { ImportSharedModelDialog } from '../components/ImportSharedModelDialog';
import Link from '@mui/material/Link';
import { useLicenseOptions } from '../hooks/useLicenseOptions';
import { LicenseFields, isLicenseValid, type LicenseValue } from '../components/LicenseFields';
import { ProfileGroupSelect } from '../components/ProfileGroupSelect';
import { ModelVersionsDialog } from '../components/ModelVersionsDialog';

// Helper function to format publication date as YYYY-MM-DD
function formatPublicationDate(dateString: string | null | undefined): string | null {
   if (!dateString) return null;
   const date = new Date(dateString);
   if (isNaN(date.getTime())) return dateString; // Return original if invalid
   return date.toISOString().split('T')[0];
}

// Strip characters that can't appear unescaped in a URN namespace-specific
// string. We keep alphanumerics plus a small set of safe punctuation
// (- . _ ~) and replace runs of whitespace/other chars with a single dash so
// "My Model 1.0!" becomes "My-Model-1.0".
function sanitizeUriSegment(value: string): string {
   if (!value) return '';
   return value
      .trim()
      .replace(/[^A-Za-z0-9._~-]+/g, '-')
      .replace(/^-+|-+$/g, '');
}


// Validate a model namespace URI for the create dialog. Mirrors the server's
// import-time rule (CanonicalUri.IsValid): absolute, ASCII-only, scheme
// restricted to http/https/urn, well-formed percent-encoding (valid %HH accepted,
// bare/truncated "%" rejected), and no ";" — then adds the URN Namespace
// Identifier (NID) check that import intentionally omits. The editor never adds
// percent-encoding; URIs are expected to arrive already correctly encoded, and we
// only reject under-encoding. Returns an error message, or null if valid.
function validateModelUri(uri: string): string | null {
   const value = uri.trim();
   if (!value) return 'A namespace URI is required.';
   if (/\s/.test(value)) return 'Namespace URI must not contain whitespace.';
   // A "%" must be followed by two hex digits; a bare or truncated escape is under-encoded.
   if (/%(?![0-9A-Fa-f]{2})/.test(value)) return 'Namespace URI has malformed percent-encoding (use %HH).';
   if (value.includes(';')) return 'Namespace URI must not contain ";".';
   for (const ch of value) {
      const c = ch.codePointAt(0)!;
      if (c < 0x20 || c === 0x7f) return 'Namespace URI must not contain control characters.';
      if (c > 0x7e) return 'Namespace URI must contain ASCII characters only.';
   }

   const schemeMatch = value.match(/^([a-zA-Z][a-zA-Z0-9+.-]*):/);
   if (!schemeMatch) {
      return 'Namespace URI must be absolute (start with http://, https://, or urn:).';
   }
   const scheme = schemeMatch[1].toLowerCase();
   if (scheme !== 'http' && scheme !== 'https' && scheme !== 'urn') {
      return 'Namespace URI must use the http, https, or urn scheme.';
   }

   if (scheme === 'http' || scheme === 'https') {
      try {
         const url = new URL(value);
         if (!url.hostname) return 'Namespace URI must include a host name.';
      } catch {
         return 'Namespace URI is not a valid URL.';
      }
      return null;
   }

   // urn:<NID>:<NSS>
   const urnMatch = value.match(/^urn:([^:]*):(.*)$/i);
   if (!urnMatch || !urnMatch[1]) {
      return 'A URN must have the form urn:<identifier>:<value>.';
   }
   const nid = urnMatch[1];
   const nss = urnMatch[2];
   if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,30}[A-Za-z0-9]$/.test(nid)) {
      return `Invalid URN identifier "${nid}": use 2–32 letters, digits, or hyphens ` +
         `with no dots or underscores (e.g. "shaleriver-com").`;
   }
   if (!nss) {
      return 'A URN must include text after the identifier (urn:<identifier>:<value>).';
   }
   return null;
}

// Build everything before the final "<model name>" part, of the form
// "urn:opcua:<domain>:<YYYY-MM>:". The fixed "opcua" is the URN Namespace
// Identifier (NID); the domain lives in the namespace-specific string where dots
// are allowed, so it is kept verbatim (only stripped of characters that would
// need encoding). The domain is the user's DefaultDomain preference when set,
// otherwise the email domain. If neither yields a usable domain we return null
// so the caller can fall back to a blank URI rather than emit "urn:opcua::2026-04:".
function computeModelUriPrefix(email: string, now: Date, defaultDomain?: string): string | null {
   let domainSource = defaultDomain?.trim();
   if (!domainSource) {
      const at = email.indexOf('@');
      if (at <= 0 || at === email.length - 1) return null;
      domainSource = email.slice(at + 1);
   }
   const domain = sanitizeUriSegment(domainSource);
   if (!domain) return null;
   const yyyy = now.getFullYear().toString().padStart(4, '0');
   const mm = (now.getMonth() + 1).toString().padStart(2, '0');
   return `urn:opcua:${domain}:${yyyy}-${mm}:`;
}

const ModelLibraryPage: React.FC = () => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   const theme = useTheme();
   const queryClient = useQueryClient();
   const { selectedWorkspaceId, setSelectedWorkspaceId, highlightModelUri, selectedModelUri, setSelectedModelUri, modelLibraryCategories, setModelLibraryCategories, setSelectedType } = React.useContext(WorkspaceContext);
   const { email: userEmail, defaultDomain: userDefaultDomain,
      defaultLicense: userDefaultLicense, defaultLicenseUrl: userDefaultLicenseUrl,
      defaultCopyrightHolder: userDefaultCopyright, betaTester, admin } = React.useContext(UserContext);
   const { data: licenseOptions } = useLicenseOptions();
   const [filter, setFilter] = React.useState<string>('');
   const visibleCategories = modelLibraryCategories;

   // Import dialog state — the dialog itself owns its filter/selection state.
   const [importDialogOpen, setImportDialogOpen] = React.useState(false);
   const [sharedImportDialogOpen, setSharedImportDialogOpen] = React.useState(false);

   // Delete dialog state
   const [deleteDialogOpen, setDeleteDialogOpen] = React.useState(false);
   const [modelToDelete, setModelToDelete] = React.useState<ModelInfo | null>(null);
   const [isDeleting, setIsDeleting] = React.useState(false);
   const [deleteError, setDeleteError] = React.useState<string | null>(null);

   // Download dialog state
   const [downloadDialogOpen, setDownloadDialogOpen] = React.useState(false);
   const [modelToDownload, setModelToDownload] = React.useState<ModelInfo | null>(null);
   const [downloadFormat, setDownloadFormat] = React.useState<string>('xml');
   const [downloadIncludeDeps, setDownloadIncludeDeps] = React.useState<boolean>(false);
   const [downloadRemoveUnused, setDownloadRemoveUnused] = React.useState<boolean>(false);

   // Create menu and workspace dialog state
   const [createMenuAnchor, setCreateMenuAnchor] = React.useState<HTMLElement | null>(null);
   const [createWorkspaceDialogOpen, setCreateWorkspaceDialogOpen] = React.useState(false);
   const [newWorkspaceName, setNewWorkspaceName] = React.useState('');
   const [newWorkspaceDescription, setNewWorkspaceDescription] = React.useState('');
   const [newWorkspaceAcl, setNewWorkspaceAcl] = React.useState('');
   const [isCreatingWorkspace, setIsCreatingWorkspace] = React.useState(false);
   const [createWorkspaceError, setCreateWorkspaceError] = React.useState<string | null>(null);

   // Edit workspace dialog state
   const [editWorkspaceDialogOpen, setEditWorkspaceDialogOpen] = React.useState(false);
   const [editWorkspaceName, setEditWorkspaceName] = React.useState('');
   const [editWorkspaceDescription, setEditWorkspaceDescription] = React.useState('');
   const [editWorkspaceAcl, setEditWorkspaceAcl] = React.useState('');
   const [isSavingWorkspace, setIsSavingWorkspace] = React.useState(false);
   const [editWorkspaceError, setEditWorkspaceError] = React.useState<string | null>(null);

   // Delete workspace dialog state
   const [deleteWorkspaceDialogOpen, setDeleteWorkspaceDialogOpen] = React.useState(false);
   const [isDeletingWorkspace, setIsDeletingWorkspace] = React.useState(false);
   const [deleteWorkspaceError, setDeleteWorkspaceError] = React.useState<string | null>(null);

   // Create model dialog state
   const [createModelDialogOpen, setCreateModelDialogOpen] = React.useState(false);
   const [newModelName, setNewModelName] = React.useState('');
   const [newModelUri, setNewModelUri] = React.useState('');
   const [newModelVersion, setNewModelVersion] = React.useState('');
   const [newModelDescription, setNewModelDescription] = React.useState('');
   const [isCreatingModel, setIsCreatingModel] = React.useState(false);
   const [createModelError, setCreateModelError] = React.useState<string | null>(null);
   // License & copyright for a new model. Default to the user's preferences via the
   // "use my defaults" checkbox; unchecking enables a per-model override.
   const [newModelUseDefaults, setNewModelUseDefaults] = React.useState(true);
   const [newModelLicense, setNewModelLicense] = React.useState<LicenseValue>({ license: '', licenseUrl: '' });
   const [newModelCopyright, setNewModelCopyright] = React.useState('');
   // Captures the urn:<domain>:YYYY-MM:<localpart>: prefix for the *current*
   // dialog session. Frozen when the dialog opens so the YYYY-MM doesn't tick
   // forward mid-edit and the email lookup happens only once.
   const [newModelUriPrefix, setNewModelUriPrefix] = React.useState<string | null>(null);
   // Whether the user has manually edited the URI field. Once true, Name
   // changes no longer overwrite the URI for the rest of the session.
   const [newModelUriManuallyEdited, setNewModelUriManuallyEdited] = React.useState(false);

   // Edit model dialog state
   const [editModelDialogOpen, setEditModelDialogOpen] = React.useState(false);
   const [editModel, setEditModel] = React.useState<ModelInfo | null>(null);
   const [editModelName, setEditModelName] = React.useState('');
   const [editModelVersion, setEditModelVersion] = React.useState('');
   const [editModelDescription, setEditModelDescription] = React.useState('');
   const [isSavingModel, setIsSavingModel] = React.useState(false);
   const [editModelError, setEditModelError] = React.useState<string | null>(null);
   const [editModelReadOnly, setEditModelReadOnly] = React.useState(false);
   // License & copyright are read-only in the edit dialog (set once at genesis).
   const [editModelLicense, setEditModelLicense] = React.useState('');
   const [editModelLicenseUrl, setEditModelLicenseUrl] = React.useState('');
   const [editModelCopyright, setEditModelCopyright] = React.useState('');
   const [editModelProfileGroup, setEditModelProfileGroup] = React.useState('');
   // True when the model being edited is shared — used to warn an admin that the edit is global.
   const [editModelIsShared, setEditModelIsShared] = React.useState(false);
   // While a model is checked out the version is owned by the checkout/check-in
   // lifecycle, so the Version field is disabled in the edit dialog.
   const [editModelVersionLocked, setEditModelVersionLocked] = React.useState(false);
   const [versionsDialogOpen, setVersionsDialogOpen] = React.useState(false);

   // Checkout (lock → unlock) dialog state
   const [checkoutDialogOpen, setCheckoutDialogOpen] = React.useState(false);
   const [checkoutModel, setCheckoutModel] = React.useState<WorkspaceNamespaceInfo | null>(null);
   const [isCheckingOut, setIsCheckingOut] = React.useState(false);
   const [checkoutError, setCheckoutError] = React.useState<string | null>(null);

   // Check-in (unlock → lock) dialog state
   const [checkinDialogOpen, setCheckinDialogOpen] = React.useState(false);
   const [checkinModel, setCheckinModel] = React.useState<WorkspaceNamespaceInfo | null>(null);
   const [checkinAction, setCheckinAction] = React.useState<'keep' | 'publish' | 'discard'>('keep');
   const [checkinVersion, setCheckinVersion] = React.useState('');
   const [checkinDescription, setCheckinDescription] = React.useState('');
   const [isCheckingIn, setIsCheckingIn] = React.useState(false);
   const [checkinError, setCheckinError] = React.useState<string | null>(null);

   // Import menu and file upload state
   const [importMenuAnchor, setImportMenuAnchor] = React.useState<HTMLElement | null>(null);
   const [uploadError, setUploadError] = React.useState<string | null>(null);
   const [isUploading, setIsUploading] = React.useState(false);
   // File-import license confirmation: after a file is picked we detect any embedded
   // license/copyright, then prompt the user to confirm or set them before importing.
   const [pendingImportFile, setPendingImportFile] = React.useState<File | null>(null);
   const [importLicenseDialogOpen, setImportLicenseDialogOpen] = React.useState(false);
   const [importDetecting, setImportDetecting] = React.useState(false);
   const [importLicense, setImportLicense] = React.useState<LicenseValue>({ license: '', licenseUrl: '' });
   const [importCopyright, setImportCopyright] = React.useState('');
   const fileInputRef = React.useRef<HTMLInputElement>(null);

   // Fetch discovery data to get ownership info for the selected workspace
   const { data: discoveryData } = useQuery({
      queryKey: ['discovery'],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceDescription>>('/opcua/v1/discovery', {
            params: { start: 0, count: 100 }
         });
         return response.data;
      }
   });

   const currentWorkspace = React.useMemo(() => {
      if (!selectedWorkspaceId || !discoveryData?.results) return undefined;
      return discoveryData.results.find(ws => urnToId(ws.applicationUri) === selectedWorkspaceId);
   }, [discoveryData, selectedWorkspaceId]);

   const isWorkspaceOwner = currentWorkspace?.isOwner ?? false;
   // Write permission for workspace content (models, types). Owner-only:
   // workspaces shared with the user are read-only. Equivalent to isOwner
   // today, but kept distinct so per-user write grants can change it later.
   const canWrite = currentWorkspace?.canWrite ?? false;

   // Fetch namespace info (models) for the selected workspace
   const { data: namespacesData, isLoading: namespacesLoading, isError: namespacesError, error: namespacesErrorObj } = useQuery({
      queryKey: ['namespaces', selectedWorkspaceId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>('/opcua/v1/namespaces/info', {
            headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) }
         });
         return response.data;
      },
      enabled: !!selectedWorkspaceId
   });

   const namespaces = namespacesData?.results ?? [];

   const filteredModels = React.useMemo(() => {
      const showPrivate = visibleCategories.includes('private');
      const showShared = visibleCategories.includes('shared');
      let result = namespaces;
      if (!showPrivate || !showShared) {
         result = result.filter(ns => {
            return ns.isPrivate ? showPrivate : showShared;
         });
      }
      if (filter) {
         const lowerFilter = filter.toLowerCase();
         result = result.filter(ns =>
            ns.name?.toLowerCase().includes(lowerFilter) ||
            ns.uri?.toLowerCase().includes(lowerFilter) ||
            ns.description?.text?.toLowerCase().includes(lowerFilter)
         );
      }
      return result;
   }, [namespaces, filter, visibleCategories]);

   const handleFilterChange = (event: React.ChangeEvent<HTMLInputElement>) => {
      setFilter(event.target.value);
   };

   const handleImportFromCloud = () => {
      setImportMenuAnchor(null);
      setImportDialogOpen(true);
   };

   const handleImportFromShared = () => {
      setImportMenuAnchor(null);
      setSharedImportDialogOpen(true);
   };

   const handleImportFromFile = () => {
      setImportMenuAnchor(null);
      fileInputRef.current?.click();
   };

   const CHUNK_SIZE = 4 * 1024 * 1024; // 4MB

   // Picking a file no longer uploads immediately: detect any embedded license/copyright, then
   // open a dialog for the user to confirm or set them before the import proceeds.
   const handleFileSelected = async (event: React.ChangeEvent<HTMLInputElement>) => {
      const selectedFile = event.target.files?.[0];
      // Reset the input so re-picking the same file fires onChange again.
      if (fileInputRef.current) fileInputRef.current.value = '';
      if (!selectedFile || !selectedWorkspaceId) return;

      setUploadError(null);
      setPendingImportFile(selectedFile);
      // Seed from the user's defaults; auto-detection below may overwrite.
      setImportLicense({ license: userDefaultLicense ?? '', licenseUrl: userDefaultLicenseUrl ?? '' });
      setImportCopyright(userDefaultCopyright ?? '');
      setImportLicenseDialogOpen(true);

      setImportDetecting(true);
      try {
         const form = new FormData();
         form.append('file', selectedFile, selectedFile.name);
         const res = await api.post<{ license?: string | null; licenseUrl?: string | null; copyrightHolder?: string | null }>(
            '/opcua/v1/namespaces/info/detect-license', form,
            { headers: { 'Content-Type': 'multipart/form-data', 'OpcUa-Server': idToUrn(selectedWorkspaceId) } });
         const d = res.data;
         if (d?.license) setImportLicense({ license: d.license, licenseUrl: d.licenseUrl ?? '' });
         if (d?.copyrightHolder) setImportCopyright(d.copyrightHolder);
      } catch {
         // Detection is best-effort; keep the seeded defaults.
      } finally {
         setImportDetecting(false);
      }
   };

   const handleImportLicenseDialogClose = () => {
      setImportLicenseDialogOpen(false);
      setPendingImportFile(null);
   };

   const handleConfirmImport = async () => {
      const file = pendingImportFile;
      setImportLicenseDialogOpen(false);
      setPendingImportFile(null);
      if (file) {
         await uploadImportFile(file, importLicense.license.trim(), importLicense.licenseUrl.trim(), importCopyright.trim());
      }
   };

   const uploadImportFile = async (file: File, license: string, licenseUrl: string, copyright: string) => {
      if (!selectedWorkspaceId) return;

      setIsUploading(true);
      setUploadError(null);

      try {
         const totalChunks = Math.max(1, Math.ceil(file.size / CHUNK_SIZE));
         let uploadId: string | undefined;

         for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++) {
            const start = chunkIndex * CHUNK_SIZE;
            const end = Math.min(start + CHUNK_SIZE, file.size);
            const chunk = file.slice(start, end);

            const formData = new FormData();
            formData.append('file', chunk, file.name);
            formData.append('fileName', file.name);
            formData.append('chunkIndex', chunkIndex.toString());
            formData.append('totalChunks', totalChunks.toString());
            if (uploadId) {
               formData.append('uploadId', uploadId);
            }
            // User-confirmed license/copyright (the server treats these as authoritative).
            formData.append('license', license);
            if (licenseUrl) formData.append('licenseUrl', licenseUrl);
            formData.append('copyrightHolder', copyright);

            const response = await api.post('/opcua/v1/namespaces/info/import', formData, {
               headers: {
                  'Content-Type': 'multipart/form-data',
                  'OpcUa-Server': idToUrn(selectedWorkspaceId)
               }
            });

            const result = response.data;

            if (!result.isComplete) {
               uploadId = result.uploadId;
            } else {
               // Upload complete
               setUploadError(null);
               await invalidateWorkspaceData();
            }
         }
      } catch (e) {
         const errorMessage = e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : t('modelLibrary.uploadFailed'));
         setUploadError(errorMessage);
      } finally {
         setIsUploading(false);
      }
   };

   const handleCreateModel = () => {
      setCreateMenuAnchor(null);
      setNewModelName('');
      const prefix = computeModelUriPrefix(userEmail, new Date(), userDefaultDomain);
      setNewModelUriPrefix(prefix);
      setNewModelUri(prefix ?? '');
      setNewModelUriManuallyEdited(false);
      // New models start as an editable working copy — default to the -alpha pre-release.
      setNewModelVersion('1.0.0-alpha');
      setNewModelDescription('');
      setNewModelUseDefaults(true);
      setNewModelLicense({ license: userDefaultLicense ?? '', licenseUrl: userDefaultLicenseUrl ?? '' });
      setNewModelCopyright(userDefaultCopyright ?? '');
      setCreateModelError(null);
      setCreateModelDialogOpen(true);
   };

   // The effective license/copyright submitted for a new model: the user's defaults when
   // "use my defaults" is checked, otherwise the per-model override fields.
   const effectiveNewLicense = newModelUseDefaults
      ? { license: userDefaultLicense ?? '', licenseUrl: userDefaultLicenseUrl ?? '' }
      : newModelLicense;
   const effectiveNewCopyright = newModelUseDefaults ? (userDefaultCopyright ?? '') : newModelCopyright;
   const newModelLicenseOk = isLicenseValid(
      effectiveNewLicense.license, effectiveNewLicense.licenseUrl, licenseOptions ?? []);
   const newModelCopyrightOk = !!effectiveNewCopyright.trim();
   // Name is mandatory and must be at least 2 characters.
   const newModelNameOk = newModelName.trim().length >= 2;

   const handleNewModelNameChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      const next = e.target.value;
      setNewModelName(next);
      // Mirror the (sanitized) name into the URI's last segment until the
      // user takes manual control. We rebuild from the frozen prefix rather
      // than mutating the existing URI to avoid drifting if the user has
      // already partially typed something.
      if (!newModelUriManuallyEdited && newModelUriPrefix) {
         setNewModelUri(newModelUriPrefix + sanitizeUriSegment(next));
      }
   };

   const handleNewModelUriChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      setNewModelUri(e.target.value);
      // Any keystroke in the URI field locks it from further name-driven
      // updates. We don't try to detect "the user typed exactly what we
      // would have generated" — once they touch it, they own it.
      setNewModelUriManuallyEdited(true);
   };

   // Adding, removing, checking out/in, or editing a model changes the whole address
   // space, so refresh EVERY workspace-scoped query (the namespaces list plus the tree
   // and type queries: subtypes, nodeChildren, queryTypes, workspaceTypes, nextNodeId, …),
   // not just the model list. Each of those keys carries the workspace id, so match on it.
   const invalidateWorkspaceData = React.useCallback(async () => {
      await queryClient.invalidateQueries({
         predicate: (q) =>
            Array.isArray(q.queryKey) &&
            (q.queryKey[0] === 'discovery' ||
               (selectedWorkspaceId != null && q.queryKey.includes(selectedWorkspaceId))),
      });
   }, [queryClient, selectedWorkspaceId]);

   const handleCreateModelDialogClose = () => {
      setCreateModelDialogOpen(false);
      setCreateModelError(null);
   };

   const handleCreateModelConfirm = async () => {
      if (!newModelUri.trim() || validateModelUri(newModelUri) || !selectedWorkspaceId) return;
      if (!newModelNameOk || !newModelLicenseOk || !newModelCopyrightOk) return;

      setIsCreatingModel(true);
      setCreateModelError(null);

      try {
         await api.post('/opcua/v1/namespaces/info', {
            uri: newModelUri.trim(),
            name: newModelName.trim(),
            version: newModelVersion.trim() || null,
            description: newModelDescription.trim() || null,
            license: effectiveNewLicense.license.trim(),
            licenseUrl: effectiveNewLicense.licenseUrl.trim() || null,
            copyrightHolder: effectiveNewCopyright.trim()
         }, { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } });

         setCreateModelDialogOpen(false);
         await invalidateWorkspaceData();
      } catch (e) {
         const errorMessage = e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : 'Failed to create model');
         setCreateModelError(errorMessage);
      } finally {
         setIsCreatingModel(false);
      }
   };

   const handleCreateWorkspace = () => {
      setCreateMenuAnchor(null);
      setNewWorkspaceName('');
      setNewWorkspaceDescription('');
      setNewWorkspaceAcl('');
      setCreateWorkspaceError(null);
      setCreateWorkspaceDialogOpen(true);
   };

   const handleCreateWorkspaceDialogClose = () => {
      setCreateWorkspaceDialogOpen(false);
      setCreateWorkspaceError(null);
   };

   const handleCreateWorkspaceConfirm = async () => {
      if (!newWorkspaceName.trim()) return;

      setIsCreatingWorkspace(true);
      setCreateWorkspaceError(null);

      try {
         const acl = newWorkspaceAcl
            .split('\n')
            .map(e => e.trim())
            .filter(Boolean);

         const response = await api.post('/opcua/v1/servers', {
            applicationName: newWorkspaceName.trim(),
            description: newWorkspaceDescription.trim() || null,
            acl: acl.length > 0 ? acl : null
         });

         const created = response.data as WorkspaceDescription;
         setCreateWorkspaceDialogOpen(false);
         await queryClient.invalidateQueries({ queryKey: ['discovery'] });

         if (created?.applicationUri) {
            const id = urnToId(created.applicationUri);
            setSelectedWorkspaceId(id);
            api.put('/opcua/v1/user/preferences', { selectedServer: idToUrn(id) }).catch(() => { /* best-effort */ });
         }
      } catch (e) {
         const errorMessage = e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : 'Failed to create workspace');
         setCreateWorkspaceError(errorMessage);
      } finally {
         setIsCreatingWorkspace(false);
      }
   };

   // Edit workspace handlers
   const handleEditWorkspace = () => {
      setEditWorkspaceName(currentWorkspace?.applicationName?.text ?? '');
      setEditWorkspaceDescription(currentWorkspace?.description?.text ?? '');
      setEditWorkspaceAcl(currentWorkspace?.acl?.join('\n') ?? '');
      setEditWorkspaceError(null);
      setEditWorkspaceDialogOpen(true);
   };

   const handleEditWorkspaceConfirm = async () => {
      if (!selectedWorkspaceId || !editWorkspaceName.trim()) return;

      setIsSavingWorkspace(true);
      setEditWorkspaceError(null);

      try {
         const acl = editWorkspaceAcl
            .split('\n')
            .map(e => e.trim())
            .filter(Boolean);

         await api.put(`/opcua/v1/servers/${encodeURIComponent(idToUrn(selectedWorkspaceId))}`, {
            applicationName: editWorkspaceName.trim(),
            description: editWorkspaceDescription.trim() || null,
            acl: acl.length > 0 ? acl : null
         });

         setEditWorkspaceDialogOpen(false);
         await queryClient.invalidateQueries({ queryKey: ['discovery'] });
         await queryClient.invalidateQueries({ queryKey: ['namespaces', selectedWorkspaceId] });
      } catch (e) {
         const errorMessage = e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : 'Failed to update workspace');
         setEditWorkspaceError(errorMessage);
      } finally {
         setIsSavingWorkspace(false);
      }
   };

   // Delete workspace handlers
   const handleDeleteWorkspace = () => {
      setDeleteWorkspaceError(null);
      setDeleteWorkspaceDialogOpen(true);
   };

   const handleDeleteWorkspaceConfirm = async () => {
      if (!selectedWorkspaceId) return;

      setIsDeletingWorkspace(true);
      setDeleteWorkspaceError(null);

      try {
         await api.delete(`/opcua/v1/servers/${encodeURIComponent(idToUrn(selectedWorkspaceId))}`);

         setDeleteWorkspaceDialogOpen(false);
         setSelectedWorkspaceId('');
         await queryClient.invalidateQueries({ queryKey: ['discovery'] });
      } catch (e) {
         const errorMessage = e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : 'Failed to delete workspace');
         setDeleteWorkspaceError(errorMessage);
      } finally {
         setIsDeletingWorkspace(false);
      }
   };

   /** Convert WorkspaceNamespaceInfo to ModelInfo for legacy API calls */
   const nsToModelInfo = (ns: WorkspaceNamespaceInfo): ModelInfo => ({
      id: ns.id,
      modelUri: ns.uri,
      name: ns.name,
      modelVersion: ns.version,
      publicationDate: ns.publicationDate,
      description: ns.description?.text ? { text: ns.description.text } : null,
   });

   const isCoreModel = (ns: WorkspaceNamespaceInfo): boolean => {
      return ns.name?.toLowerCase() === 'core';
   };

   // Models in the reserved OPC Foundation namespace are read-only even when held privately —
   // their canonical license/metadata must not be edited.
   const isOpcFoundationModel = (ns: WorkspaceNamespaceInfo): boolean =>
      (ns.uri ?? '').toLowerCase().startsWith('http://opcfoundation.org/');

   const handleViewTypeDefinitions = (ns: WorkspaceNamespaceInfo) => {
      // Pass the namespace URI in the URL as well as the context. URL is the
      // source of truth on the destination page so we don't depend on the
      // context state update racing with the route change (and so back/forward
      // and shared links work).
      setSelectedModelUri(ns.uri ?? '');
      setSelectedType(null);
      const params = ns.uri ? `?ns=${encodeURIComponent(ns.uri)}` : '';
      navigate(`/type_library${params}`);
   };

   const handleValidateModel = (ns: WorkspaceNamespaceInfo) => {
      // Validation applies to a workspace's own private models. Pass the model id
      // (used by the validation API) and the URI (for display) on the URL.
      const params = new URLSearchParams();
      if (ns.id) params.set('model', ns.id);
      if (ns.uri) params.set('ns', ns.uri);
      navigate(`/validation?${params.toString()}`);
   };

   const handleDownloadModel = (ns: WorkspaceNamespaceInfo) => {
      setModelToDownload(nsToModelInfo(ns));
      setDownloadFormat('xml');
      setDownloadIncludeDeps(false);
      setDownloadRemoveUnused(false);
      setDownloadDialogOpen(true);
   };

   const handleDownloadDialogClose = () => {
      setDownloadDialogOpen(false);
      setModelToDownload(null);
   };

   const handleDownloadConfirm = async () => {
      if (!modelToDownload?.id || !selectedWorkspaceId) {
         return;
      }

      try {
         const depsParam = downloadIncludeDeps ? '&includeDependencies=true' : '';
         // Mirrors the checkbox's own enablement — the server ignores it otherwise anyway.
         const trimParam = downloadIncludeDeps && downloadRemoveUnused && downloadFormat === 'xml'
            ? '&removeUnusedNodes=true' : '';
         const response = await api.get(
            `/opcua/v1/namespaces/info/${modelToDownload.id}/export?format=${downloadFormat}${depsParam}${trimParam}`,
            {
               responseType: 'blob',
               headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) }
            }
         );

         // Extract filename from Content-Disposition header if available
         const contentDisposition = response.headers['content-disposition'];
         const defaultExt = downloadIncludeDeps ? '.zip'
            : downloadFormat === 'json' ? '.json'
            : downloadFormat === 'compressed' ? '.uanodeset'
            : downloadFormat === 'jsonld' ? '.jsonld'
            : '.xml';
         let fileName = `nodeset${defaultExt}`;
         if (contentDisposition) {
            const fileNameMatch = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
            if (fileNameMatch && fileNameMatch[1]) {
               fileName = fileNameMatch[1].replace(/['"]/g, '');
            }
         }

         // Determine blob type based on format
         const blobType = downloadIncludeDeps
            ? 'application/zip'
            : downloadFormat === 'json'
            ? 'application/json'
            : downloadFormat === 'compressed'
               ? 'application/gzip'
               : downloadFormat === 'jsonld'
                  ? 'application/ld+json'
                  : 'text/xml';

         const blob = new Blob([response.data], { type: blobType });
         const url = window.URL.createObjectURL(blob);
         const link = document.createElement('a');
         link.href = url;
         link.download = fileName;
         document.body.appendChild(link);
         link.click();
         document.body.removeChild(link);
         window.URL.revokeObjectURL(url);

         setDownloadDialogOpen(false);
         setModelToDownload(null);
      } catch (e) {
         // If the server returned an error as a blob, try to extract the message
         if (axios.isAxiosError(e) && e.response?.data instanceof Blob) {
            const text = await e.response.data.text();
            console.error('Download error:', text);
            alert(text);
         } else {
            console.error('Error downloading model:', e);
            alert(e instanceof Error ? e.message : 'Download failed');
         }
         setDownloadDialogOpen(false);
         setModelToDownload(null);
      }
   };

   const handleDeleteModel = (ns: WorkspaceNamespaceInfo) => {
      setModelToDelete(nsToModelInfo(ns));
      setDeleteError(null);
      setDeleteDialogOpen(true);
   };

   const handleDeleteDialogClose = () => {
      setDeleteDialogOpen(false);
      setModelToDelete(null);
      setDeleteError(null);
   };

   const handleDeleteConfirm = async () => {
      if (!modelToDelete || !selectedWorkspaceId) {
         return;
      }

      setIsDeleting(true);
      setDeleteError(null);

      try {
         await api.delete(`/opcua/v1/namespaces/info/${modelToDelete.id}`, {
            headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) }
         });

         setDeleteDialogOpen(false);
         setModelToDelete(null);
         await invalidateWorkspaceData();
      } catch (e) {
         const errorMessage = e instanceof ApiError ? e.message : (e instanceof Error ? e.message : 'Failed to remove model');
         setDeleteError(errorMessage);
      } finally {
         setIsDeleting(false);
      }
   };

   const handleEditModel = (ns: WorkspaceNamespaceInfo) => {
      setEditModel(nsToModelInfo(ns));
      setEditModelName(ns.name ?? '');
      setEditModelVersion(ns.version ?? '');
      setEditModelDescription(ns.description?.text ?? '');
      setEditModelError(null);
      // Admins curate the standard models for everyone, so neither the private-only rule nor
      // the read-only OPC Foundation namespace closes the dialog for them. The server applies
      // the same exemption; this only decides what the form offers.
      setEditModelReadOnly(!admin && (!ns.isPrivate || !canWrite || isOpcFoundationModel(ns)));
      setEditModelIsShared(!ns.isPrivate);
      setEditModelVersionLocked(!!ns.isEditable);
      setEditModelLicense(ns.license ?? '');
      setEditModelLicenseUrl(ns.licenseUrl ?? '');
      setEditModelCopyright(ns.copyrightHolder ?? '');
      setEditModelProfileGroup(ns.profileGroupName ?? '');
      setEditModelDialogOpen(true);
   };

   const handleEditModelDialogClose = () => {
      setEditModelDialogOpen(false);
      setEditModel(null);
      setEditModelError(null);
      setVersionsDialogOpen(false);
   };

   const handleEditModelConfirm = async () => {
      if (!editModel?.id || !selectedWorkspaceId) return;

      setIsSavingModel(true);
      setEditModelError(null);

      try {
         const payload: Record<string, unknown> = {
            name: editModelName.trim(),
            description: editModelDescription.trim(),
         };
         // Version is owned by the checkout/check-in lifecycle while checked out — only send it
         // when it's actually editable, or the server rejects the (unchanged) value with a 400.
         if (!editModelVersionLocked) {
            payload.version = editModelVersion.trim();
         }
         // License & copyright are editable for the user's own private models.
         if (!editModelReadOnly) {
            payload.license = editModelLicense.trim();
            payload.licenseUrl = editModelLicenseUrl.trim() || null;
            payload.copyrightHolder = editModelCopyright.trim();
            // Always sent when editable — an empty string is how the profile group is cleared.
            payload.profileGroupName = editModelProfileGroup.trim();
         }
         await api.put(`/opcua/v1/namespaces/info/${editModel.id}`, payload,
            { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } });

         setEditModelDialogOpen(false);
         setEditModel(null);
         await invalidateWorkspaceData();
      } catch (e) {
         const msg = e instanceof ApiError ? e.message : (e instanceof Error ? e.message : 'Failed to update model');
         setEditModelError(msg);
      } finally {
         setIsSavingModel(false);
      }
   };

   // --- Checkout / Check-in (lock / unlock) ---

   const handleLockModel = (ns: WorkspaceNamespaceInfo) => {
      setCheckoutModel(ns);
      setCheckoutError(null);
      setCheckoutDialogOpen(true);
   };

   const handleCheckoutDialogClose = () => {
      setCheckoutDialogOpen(false);
      setCheckoutModel(null);
      setCheckoutError(null);
   };

   const handleCheckoutConfirm = async () => {
      if (!checkoutModel?.id || !selectedWorkspaceId) return;

      setIsCheckingOut(true);
      setCheckoutError(null);
      try {
         await api.post(`/opcua/v1/namespaces/info/${checkoutModel.id}/checkout`, null,
            { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } });

         setCheckoutDialogOpen(false);
         setCheckoutModel(null);
         await invalidateWorkspaceData();
      } catch (e) {
         const msg = e instanceof ApiError ? e.message : (e instanceof Error ? e.message : 'Failed to check out model');
         setCheckoutError(msg);
      } finally {
         setIsCheckingOut(false);
      }
   };

   const handleUnlockModel = (ns: WorkspaceNamespaceInfo) => {
      setCheckinModel(ns);
      setCheckinAction('keep');
      // Suggest the working version's numeric core (drop any -alpha/-beta suffix) and
      // pre-fill the existing description; both are editable when publishing.
      setCheckinVersion((ns.version ?? '').split('-')[0]);
      setCheckinDescription(ns.description?.text ?? '');
      setCheckinError(null);
      setCheckinDialogOpen(true);
   };

   const handleCheckinDialogClose = () => {
      setCheckinDialogOpen(false);
      setCheckinModel(null);
      setCheckinError(null);
   };

   const handleCheckinConfirm = async () => {
      if (!checkinModel?.id || !selectedWorkspaceId) return;
      // A description is mandatory to publish.
      if (checkinAction === 'publish' && !checkinDescription.trim()) return;

      setIsCheckingIn(true);
      setCheckinError(null);
      try {
         // Keep and publish both settle on a version; only publish carries a description.
         // Discard throws the working copy away, so neither applies.
         const body = checkinAction === 'publish'
            ? { action: checkinAction, version: checkinVersion.trim(), description: checkinDescription.trim() }
            : checkinAction === 'keep'
               ? { action: checkinAction, version: checkinVersion.trim() }
               : { action: checkinAction };
         await api.post(`/opcua/v1/namespaces/info/${checkinModel.id}/checkin`,
            body,
            { headers: { 'OpcUa-Server': idToUrn(selectedWorkspaceId) } });

         setCheckinDialogOpen(false);
         setCheckinModel(null);
         await invalidateWorkspaceData();
      } catch (e) {
         const msg = e instanceof ApiError ? e.message : (e instanceof Error ? e.message : 'Failed to check in model');
         setCheckinError(msg);
      } finally {
         setIsCheckingIn(false);
      }
   };

   const isLoading = namespacesLoading;
   const pageError = uploadError ? new Error(uploadError) : namespacesErrorObj;
   const isError = namespacesError || !!uploadError;
   const error = pageError;

   return (
      <Box p={8}>
         {/* Row 1: Title and actions */}
         <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, mb: 4 }}>
            <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8, flexGrow: 1 }}>
               <EditDocumentIcon />
               <Typography variant='h5' sx={{ fontWeight: 'bolder' }}>{t('modelLibrary.title')}</Typography>
            </Box>
            <Box sx={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: 8 }}>
               <WorkspaceSelector />
               <Tooltip title={canWrite ? '' : t('modelLibrary.readOnlyWorkspace', 'This workspace is read-only — shared with you by its owner')}>
                  <span>
                     <Button
                        variant="contained"
                        disabled={!canWrite}
                        onClick={(e) => setImportMenuAnchor(e.currentTarget)}
                        sx={{
                           px: '30px',
                           borderRadius: '50px'
                        }}
                     >
                        {isUploading ? t("common.uploading") : t("modelLibrary.importAction")}
                     </Button>
                  </span>
               </Tooltip>
               <Menu
                  anchorEl={importMenuAnchor}
                  open={Boolean(importMenuAnchor)}
                  onClose={() => setImportMenuAnchor(null)}
               >
                  <MenuItem onClick={handleImportFromCloud}>
                     {t('modelLibrary.importFromCloud')}
                  </MenuItem>
                  <MenuItem onClick={handleImportFromShared}>
                     {t('modelLibrary.importFromShared')}
                  </MenuItem>
                  <MenuItem onClick={handleImportFromFile} disabled={isUploading}>
                     {t('modelLibrary.importFromFile')}
                  </MenuItem>
               </Menu>
               <Button
                  variant="contained"
                  onClick={(e) => setCreateMenuAnchor(e.currentTarget)}
                  sx={{
                     px: '30px',
                     borderRadius: '50px'
                  }}
               >
                  {t("modelLibrary.createAction")}
               </Button>
               <Menu
                  anchorEl={createMenuAnchor}
                  open={Boolean(createMenuAnchor)}
                  onClose={() => setCreateMenuAnchor(null)}
               >
                  <MenuItem onClick={handleCreateWorkspace}>
                     {t('modelLibrary.createWorkspace')}
                  </MenuItem>
                  <MenuItem onClick={handleCreateModel} disabled={!canWrite}>
                     {t('modelLibrary.createModel')}
                  </MenuItem>
               </Menu>
            </Box>
         </Box>

         {/* Beta disclaimer */}
         <Alert severity="warning" sx={{ mb: 4 }}>
            This site is currently in a public beta. Any models created should be backed up using the download feature.
            Please help us make this site better by reporting any bugs, feature requests, or other feedback to{' '}
            <a href="mailto:webmaster@opcfoundation.org?subject=OPC%20UA%20NodeSetEditor%20Feedback">webmaster@opcfoundation.org</a>.
         </Alert>

         {/* Row 4: SearchBar with filter toggles and workspace actions */}
         <SearchBar
            value={filter}
            onChange={handleFilterChange}
            rightActions={selectedWorkspaceId ? (
               <>
                  <Tooltip title={isWorkspaceOwner
                     ? t('modelLibrary.editWorkspace', 'Edit Workspace')
                     : t('modelLibrary.editWorkspaceDisabled', 'Only the owner can edit this workspace')}>
                     <span>
                        <IconButton size="small" onClick={handleEditWorkspace} disabled={!isWorkspaceOwner}>
                           <EditIcon fontSize="small" />
                        </IconButton>
                     </span>
                  </Tooltip>
                  <Tooltip title={isWorkspaceOwner
                     ? t('modelLibrary.deleteWorkspace', 'Delete Workspace')
                     : t('modelLibrary.deleteWorkspaceDisabled', 'Only the owner can delete this workspace')}>
                     <span>
                        <IconButton size="small" onClick={handleDeleteWorkspace} disabled={!isWorkspaceOwner}>
                           <DeleteIcon fontSize="small" />
                        </IconButton>
                     </span>
                  </Tooltip>
               </>
            ) : undefined}
         >
            <ToggleButtonGroup
               value={visibleCategories}
               onChange={(_, newValue: string[]) => {
                  if (newValue.length > 0) setModelLibraryCategories(newValue);
               }}
               size="small"
               sx={{ ml: 2 }}
            >
               <ToggleButton value="private" sx={{ px: 4, py: 0.5, textTransform: 'none' }}>{t('modelLibrary.private')}</ToggleButton>
               <ToggleButton value="shared" sx={{ px: 4, py: 0.5, textTransform: 'none' }}>{t('modelLibrary.shared')}</ToggleButton>
            </ToggleButtonGroup>
         </SearchBar>

         {/* Remainder: List of models */}
         <Box>
            <ContentLoader isError={isError} isLoading={isLoading} error={error}>
               <List>
                  {filteredModels.map((ns: WorkspaceNamespaceInfo, index: number) => (
                     <ListItem
                        key={index}
                        onClick={() => setSelectedModelUri(
                           selectedModelUri === ns.uri ? '' : (ns.uri ?? '')
                        )}
                        sx={{
                           borderTopStyle: 'solid',
                           borderTopColor: theme.palette.grey[200],
                           borderTopWidth: '2px',
                           cursor: 'pointer',
                           backgroundColor: highlightModelUri && highlightModelUri === ns.uri
                              ? alpha(theme.palette.primary.main, 0.12) : undefined,
                           borderLeft: highlightModelUri && highlightModelUri === ns.uri
                              ? `3px solid ${theme.palette.primary.main}` : '3px solid transparent',
                           '&:hover': { backgroundColor: 'action.hover' },
                        }}
                     >
                        <ListItemIcon>
                           <Tooltip title={ns.errorMessage ?? ''} disableHoverListener={!ns.hasErrors}>
                              <Avatar
                                 sx={{
                                    width: 32,
                                    height: 32,
                                    bgcolor: ns.hasErrors
                                       ? theme.palette.error.main
                                       : ns.isPrivate ? theme.palette.primary.main : theme.palette.grey[600],
                                    color: ns.hasErrors
                                       ? theme.palette.error.contrastText
                                       : ns.isPrivate ? theme.palette.primary.contrastText : theme.palette.grey[200]
                                 }}
                              >
                                 {ns.hasErrors ? <ErrorOutlineIcon /> : <DescriptionIcon />}
                              </Avatar>
                           </Tooltip>
                        </ListItemIcon>
                        <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, overflow: 'hidden' }}>
                           <Typography
                              variant="body1"
                              component="div"
                              noWrap
                              sx={ns.hasErrors
                                 ? { color: theme.palette.error.main, fontWeight: 'bold' }
                                 : ns.isPrivate ? { color: theme.palette.primary.main, fontWeight: 'bold' } : undefined}
                           >
                              {ns.name ?? ns.uri}
                           </Typography>
                           {ns.description?.text && (
                              <TruncatedText text={ns.description.text} variant="caption" color="text.secondary" />
                           )}
                           {(ns.version || ns.publicationDate || ns.license) && (
                              <Typography variant="caption" color="text.secondary">
                                 {ns.version && `Version: ${ns.version}`}
                                 {ns.version && ns.publicationDate && ' | '}
                                 {ns.publicationDate && `Published: ${formatPublicationDate(ns.publicationDate)}`}
                                 {ns.license && (ns.version || ns.publicationDate) && ' | '}
                                 {ns.license && `License: ${ns.license}`}
                              </Typography>
                           )}
                        </Box>
                        <ActionBar
                           actions={[
                              {
                                 onAction: () => ns.isEditable ? handleUnlockModel(ns) : handleLockModel(ns),
                                 icon: ns.isEditable ? <LockOpenIcon /> : <LockIcon />,
                                 tooltipKey: ns.isEditable ? 'modelLibrary.checkin' : 'modelLibrary.checkout',
                                 // Core/OPC Foundation models stay read-only and cannot be checked out.
                                 disabled: isCoreModel(ns) || !canWrite
                              },
                              {
                                 onAction: () => handleEditModel(ns),
                                 // An admin gets the editable dialog on every model, so the
                                 // action reads as Edit rather than View for them.
                                 icon: (ns.isEditable || admin) ? <EditIcon /> : <VisibilityIcon />,
                                 tooltipKey: (ns.isEditable || admin) ? 'modelLibrary.editModel' : 'modelLibrary.viewModel'
                              },
                              {
                                 onAction: () => handleViewTypeDefinitions(ns),
                                 icon: <AccountTreeIcon />,
                                 tooltipKey: 'modelLibrary.viewTypeDefinitions'
                              },
                              {
                                 onAction: () => handleValidateModel(ns),
                                 icon: <FactCheckIcon />,
                                 tooltipKey: 'validation.open',
                                 // Validation is only for the workspace's own private models.
                                 hidden: !ns.isPrivate
                              },
                              {
                                 onAction: () => handleDownloadModel(ns),
                                 icon: <DownloadIcon />,
                                 tooltipKey: 'modelLibrary.download'
                              },
                              {
                                 onAction: () => handleDeleteModel(ns),
                                 icon: <DeleteIcon />,
                                 tooltipKey: 'modelLibrary.delete',
                                 disabled: isCoreModel(ns) || !canWrite
                              }
                           ]}
                        />
                     </ListItem>
                  ))}
                  {filteredModels.length === 0 && !isLoading && (
                     <ListItem>
                        <Typography variant="body2" color="text.secondary">
                           No models found in this workspace.
                        </Typography>
                     </ListItem>
                  )}
               </List>
            </ContentLoader>
         </Box>

         {/* Import from Cloud Library: tag-tree dialog */}
         <ImportModelDialog
            open={importDialogOpen}
            onClose={() => setImportDialogOpen(false)}
            workspaceId={selectedWorkspaceId}
         />

         {/* Import from the server's shared index (latest version per model) */}
         <ImportSharedModelDialog
            open={sharedImportDialogOpen}
            onClose={() => setSharedImportDialogOpen(false)}
            workspaceId={selectedWorkspaceId}
            existingUris={namespaces.map(n => n.uri ?? '').filter(Boolean)}
         />

         {/* Hidden file input for NodeSet file import */}
         <input
            type="file"
            ref={fileInputRef}
            accept=".xml,.json,.uanodeset,.tar.gz,.gz"
            style={{ display: 'none' }}
            onChange={handleFileSelected}
         />

         {/* Confirm license & copyright before importing a picked NodeSet file */}
         {importLicenseDialogOpen && (
            <ModelDialog
               open
               onClose={handleImportLicenseDialogClose}
               title={t('modelLibrary.importLicenseTitle', 'Confirm License & Copyright')}
               actions={[
                  {
                     label: isUploading ? t('common.uploading') : t('common.ok'),
                     onClick: handleConfirmImport,
                     disabled: importDetecting || isUploading
                        || !importCopyright.trim()
                        || !isLicenseValid(importLicense.license, importLicense.licenseUrl, licenseOptions ?? []),
                  }
               ]}
            >
               <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <Typography variant="body2" color="text.secondary">
                     {importDetecting
                        ? t('modelLibrary.importLicenseDetecting', 'Checking the file for license information…')
                        : t('modelLibrary.importLicenseHelp',
                           'Confirm the license and copyright holder for this NodeSet. Anything detected in the file is pre-filled; set them if none were found. These cannot be changed after import.')}
                  </Typography>
                  <TextField
                     label={t('modelLibrary.copyrightHolder', 'Copyright holder')}
                     value={importCopyright}
                     onChange={(e) => setImportCopyright(e.target.value)}
                     fullWidth
                     required
                     error={!importCopyright.trim()}
                     helperText={!importCopyright.trim()
                        ? t('modelLibrary.copyrightRequired', 'A copyright holder is required.')
                        : undefined}
                  />
                  <LicenseFields
                     options={licenseOptions ?? []}
                     license={importLicense.license}
                     licenseUrl={importLicense.licenseUrl}
                     onChange={setImportLicense}
                     size="medium"
                  />
               </Box>
            </ModelDialog>
         )}

         {/* Download Format Dialog */}
         {downloadDialogOpen && (
            <ModelDialog
               open
               onClose={handleDownloadDialogClose}
               title={t('modelLibrary.downloadDialogTitle', 'Download NodeSet')}
               actions={[
                  {
                     label: t('modelLibrary.download', 'Download'),
                     onClick: handleDownloadConfirm
                  }
               ]}
            >
               <Box sx={{ p: 3 }}>
                  <Box
                     component="fieldset"
                     sx={{
                        border: 1,
                        borderColor: 'divider',
                        borderRadius: 1,
                        px: 2.5,
                        pt: 1,
                        pb: 1,
                        m: 0,
                     }}
                  >
                     <Typography
                        component="legend"
                        variant="body2"
                        sx={{ px: 0.5, color: 'text.secondary' }}
                     >
                        {t('modelLibrary.downloadFormatLabel', 'Select format')}
                     </Typography>
                     <RadioGroup
                        value={downloadFormat}
                        onChange={(e) => setDownloadFormat(e.target.value)}
                        sx={{ '& .MuiFormControlLabel-root': { ml: 0 } }}
                     >
                        <FormControlLabel
                           value="xml"
                           control={<Radio />}
                           label={t('modelLibrary.downloadFormatXml', 'XML')}
                        />
                        {/* JSON is a beta format: shown to everyone so the roadmap is visible,
                            but only selectable for accounts the server's BetaTesterDomains list
                            admits. The server enforces the same rule. */}
                        <FormControlLabel
                           value="json"
                           disabled={!betaTester}
                           control={<Radio />}
                           label={t('modelLibrary.downloadFormatJson', 'JSON')}
                        />
                        {/* The archive and RDF encodings are prototypes of a draft specification
                            and live in the internal NodeSet Tool, not this build — the server has
                            no route to serve them. Left visible as roadmap placeholders, so these
                            are unconditionally disabled rather than gated on betaTester. */}
                        <FormControlLabel
                           value="compressed"
                           disabled
                           control={<Radio />}
                           label={t('modelLibrary.downloadFormatCompressed', 'Compressed (uanodeset)')}
                        />
                        <FormControlLabel
                           value="jsonld"
                           disabled
                           control={<Radio />}
                           label={t('modelLibrary.downloadFormatJsonLd', 'RDF (JSON-LD)')}
                        />
                     </RadioGroup>
                  </Box>
                  <FormControlLabel
                     sx={{ mt: 2 }}
                     control={
                        <Checkbox
                           checked={downloadIncludeDeps}
                           onChange={(e) => setDownloadIncludeDeps(e.target.checked)}
                        />
                     }
                     label={t('modelLibrary.downloadIncludeDependencies', 'Include all dependencies (ZIP)')}
                  />
                  {/* Only meaningful alongside dependencies, and only XML has a trimmed
                      generator — the other formats are serialized from the whole address space. */}
                  <Tooltip title={!downloadIncludeDeps
                     ? t('modelLibrary.removeUnusedNeedsDependencies', 'Only available when dependencies are included.')
                     : downloadFormat !== 'xml'
                        ? t('modelLibrary.removeUnusedXmlOnly', 'Only available for the XML format.')
                        : t('modelLibrary.removeUnusedHelp',
                           'Each dependency keeps only the nodes this model uses. The model itself is unchanged.')}>
                     <FormControlLabel
                        sx={{ ml: 6 }}
                        control={
                           <Checkbox
                              checked={downloadRemoveUnused && downloadIncludeDeps && downloadFormat === 'xml'}
                              disabled={!downloadIncludeDeps || downloadFormat !== 'xml'}
                              onChange={(e) => setDownloadRemoveUnused(e.target.checked)}
                           />
                        }
                        label={t('modelLibrary.downloadRemoveUnusedNodes', 'Remove unused nodes')}
                     />
                  </Tooltip>
               </Box>
            </ModelDialog>
         )}

         {/* Create Workspace Dialog */}
         {createWorkspaceDialogOpen && (
            <ModelDialog
               open
               onClose={handleCreateWorkspaceDialogClose}
               title={t('modelLibrary.createWorkspaceDialogTitle')}
               isLoading={isCreatingWorkspace}
               isError={!!createWorkspaceError}
               error={createWorkspaceError ? new Error(createWorkspaceError) : null}
               actions={createWorkspaceError ? [] : [
                  {
                     label: isCreatingWorkspace ? t('modelLibrary.creating') : t('common.ok'),
                     onClick: handleCreateWorkspaceConfirm,
                     disabled: isCreatingWorkspace || !newWorkspaceName.trim()
                  }
               ]}
            >
               {!createWorkspaceError && (
                  <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                     <TextField
                        label={t('modelLibrary.createWorkspaceName')}
                        value={newWorkspaceName}
                        onChange={(e) => setNewWorkspaceName(e.target.value)}
                        fullWidth
                        autoFocus
                     />
                     <TextField
                        label={t('modelLibrary.createWorkspaceDescription')}
                        value={newWorkspaceDescription}
                        onChange={(e) => setNewWorkspaceDescription(e.target.value)}
                        fullWidth
                        multiline
                        rows={3}
                     />
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Create Model Dialog */}
         {createModelDialogOpen && (
            <ModelDialog
               open
               onClose={handleCreateModelDialogClose}
               title={t('modelLibrary.createModelDialogTitle')}
               isLoading={isCreatingModel}
               isError={!!createModelError}
               error={createModelError ? new Error(createModelError) : null}
               actions={createModelError ? [] : [
                  {
                     label: isCreatingModel ? t('modelLibrary.creating') : t('common.ok'),
                     onClick: handleCreateModelConfirm,
                     disabled: isCreatingModel || !newModelNameOk || !newModelUri.trim() || !!validateModelUri(newModelUri)
                        || !newModelLicenseOk || !newModelCopyrightOk
                  }
               ]}
            >
               {!createModelError && (
                  <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                     <TextField
                        label={t('modelLibrary.createModelName')}
                        value={newModelName}
                        onChange={handleNewModelNameChange}
                        fullWidth
                        required
                        autoFocus
                        error={!!newModelName && !newModelNameOk}
                        helperText={!!newModelName && !newModelNameOk
                           ? t('modelLibrary.createModelNameTooShort', 'Name must be at least 2 characters.')
                           : undefined}
                     />
                     <TextField
                        label={t('modelLibrary.createModelUri')}
                        value={newModelUri}
                        onChange={handleNewModelUriChange}
                        fullWidth
                        required
                        error={!!newModelUri.trim() && !!validateModelUri(newModelUri)}
                        helperText={newModelUri.trim() ? (validateModelUri(newModelUri) ?? undefined) : undefined}
                     />
                     <TextField
                        label={t('modelLibrary.createModelVersion')}
                        value={newModelVersion}
                        onChange={(e) => setNewModelVersion(e.target.value)}
                        fullWidth
                     />
                     <TextField
                        label={t('modelLibrary.createModelDescription')}
                        value={newModelDescription}
                        onChange={(e) => setNewModelDescription(e.target.value)}
                        fullWidth
                        multiline
                        rows={3}
                     />

                     {/* License & copyright — locked once the model is created. */}
                     <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                        <FormControlLabel
                           control={
                              <Checkbox
                                 checked={newModelUseDefaults}
                                 onChange={(e) => setNewModelUseDefaults(e.target.checked)}
                              />
                           }
                           label={t('modelLibrary.useDefaultLicense', 'Use my default license & copyright')}
                        />
                        <TextField
                           label={t('modelLibrary.copyrightHolder', 'Copyright holder')}
                           value={effectiveNewCopyright}
                           onChange={(e) => setNewModelCopyright(e.target.value)}
                           fullWidth
                           required
                           disabled={newModelUseDefaults}
                           error={!newModelCopyrightOk}
                           helperText={!newModelCopyrightOk
                              ? t('modelLibrary.copyrightRequired', 'A copyright holder is required.')
                              : undefined}
                        />
                        <LicenseFields
                           options={licenseOptions ?? []}
                           license={effectiveNewLicense.license}
                           licenseUrl={effectiveNewLicense.licenseUrl}
                           onChange={setNewModelLicense}
                           disabled={newModelUseDefaults}
                           size="medium"
                        />
                        {newModelUseDefaults && !newModelLicenseOk && (
                           <Typography variant="caption" color="error">
                              {t('modelLibrary.defaultLicenseMissing',
                                 'Set a default license and copyright holder in your account settings.')}
                           </Typography>
                        )}
                     </Box>
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Edit / View Model Dialog */}
         {editModelDialogOpen && editModel && (
            <ModelDialog
               open
               onClose={handleEditModelDialogClose}
               title={editModelReadOnly
                  ? t('modelLibrary.viewModelDialogTitle')
                  : t('modelLibrary.editModelDialogTitle')}
               isLoading={isSavingModel}
               isError={!!editModelError}
               error={editModelError ? new Error(editModelError) : null}
               actions={editModelReadOnly ? [] : (editModelError ? [] : [
                  {
                     label: isSavingModel ? t('common.save') : t('common.ok'),
                     onClick: handleEditModelConfirm,
                     disabled: isSavingModel || !editModelName.trim()
                        || (!editModelReadOnly && (!editModelCopyright.trim()
                           || !isLicenseValid(editModelLicense, editModelLicenseUrl, licenseOptions ?? []))),
                  }
               ])}
            >
               {!editModelError && (
                  <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                     {/* An admin editing a shared model changes it for every workspace linked
                         to that model row — say so before they start typing. */}
                     {!editModelReadOnly && editModelIsShared && (
                        <Alert severity="warning" variant="outlined">
                           {t('modelLibrary.adminSharedModelWarning',
                              'This is a shared model. Changes you make here apply to every user and workspace that uses it.')}
                        </Alert>
                     )}
                     <TextField
                        label={t('modelLibrary.createModelName')}
                        value={editModelName}
                        onChange={(e) => setEditModelName(e.target.value)}
                        fullWidth
                        autoFocus={!editModelReadOnly}
                        slotProps={{ input: { readOnly: editModelReadOnly } }}
                     />
                     <TextField
                        label={t('modelLibrary.createModelUri')}
                        value={editModel.modelUri ?? ''}
                        fullWidth
                        slotProps={{ input: { readOnly: true } }}
                     />
                     {/* The version box carries a way into the full version history: a URI
                         normally has several stored versions (the one in use, the backup a
                         checkout retained, published releases) and only this field hints at it. */}
                     <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
                        <TextField
                           label={t('modelLibrary.createModelVersion')}
                           value={editModelVersion}
                           onChange={(e) => setEditModelVersion(e.target.value)}
                           fullWidth
                           disabled={editModelVersionLocked}
                           helperText={editModelVersionLocked ? t('modelLibrary.versionLockedWhileCheckedOut') : undefined}
                           slotProps={{ input: { readOnly: editModelReadOnly } }}
                        />
                        <Tooltip title={t('modelLibrary.showVersions', 'Show all versions')}>
                           <IconButton
                              onClick={() => setVersionsDialogOpen(true)}
                              aria-label={t('modelLibrary.showVersions', 'Show all versions')}
                              sx={{ mt: 4 }}
                           >
                              <HistoryIcon />
                           </IconButton>
                        </Tooltip>
                     </Box>
                     <TextField
                        label={t('modelLibrary.createModelDescription')}
                        value={editModelDescription}
                        onChange={(e) => setEditModelDescription(e.target.value)}
                        fullWidth
                        multiline
                        rows={3}
                        slotProps={{ input: { readOnly: editModelReadOnly } }}
                     />

                     {/* License & copyright: editable for the user's own private models, read-only
                         for shared/published ones. Always shown so they're visible in both modes. */}
                     <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                        {editModelReadOnly ? (
                           <>
                              <TextField
                                 label={t('modelLibrary.copyrightHolder', 'Copyright holder')}
                                 value={editModelCopyright || ''}
                                 placeholder={t('common.notSet', 'Not set')}
                                 fullWidth
                                 slotProps={{ input: { readOnly: true } }}
                              />
                              <TextField
                                 label={t('license.label', 'License')}
                                 value={(licenseOptions ?? []).find(o => o.spdxId === editModelLicense)?.name ?? editModelLicense ?? ''}
                                 placeholder={t('common.notSet', 'Not set')}
                                 fullWidth
                                 slotProps={{ input: { readOnly: true } }}
                              />
                              {editModelLicenseUrl && (
                                 <Link href={editModelLicenseUrl} target="_blank" rel="noopener" variant="caption">
                                    {editModelLicenseUrl}
                                 </Link>
                              )}
                              <TextField
                                 label={t('modelLibrary.profileGroup', 'Profile Group')}
                                 value={editModelProfileGroup || ''}
                                 placeholder={t('common.notSet', 'Not set')}
                                 fullWidth
                                 slotProps={{ input: { readOnly: true } }}
                              />
                           </>
                        ) : (
                           <>
                              <TextField
                                 label={t('modelLibrary.copyrightHolder', 'Copyright holder')}
                                 value={editModelCopyright}
                                 onChange={(e) => setEditModelCopyright(e.target.value)}
                                 fullWidth
                                 required
                                 error={!editModelCopyright.trim()}
                                 helperText={!editModelCopyright.trim()
                                    ? t('modelLibrary.copyrightRequired', 'A copyright holder is required.')
                                    : undefined}
                              />
                              <LicenseFields
                                 options={licenseOptions ?? []}
                                 license={editModelLicense}
                                 licenseUrl={editModelLicenseUrl}
                                 onChange={(v) => { setEditModelLicense(v.license); setEditModelLicenseUrl(v.licenseUrl); }}
                                 size="medium"
                              />
                              {/* Profile group the model's conformance units are assessed
                                  against — surfaced on the Conformance Units view. */}
                              <ProfileGroupSelect
                                 value={editModelProfileGroup}
                                 onChange={setEditModelProfileGroup}
                                 workspaceId={selectedWorkspaceId ?? undefined}
                                 enabled={editModelDialogOpen}
                              />
                           </>
                        )}
                     </Box>
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Version history for the model being edited. Stacked on top of the edit dialog
             rather than replacing it, so closing it returns to the edit form. */}
         {versionsDialogOpen && editModel?.id && selectedWorkspaceId && (
            <ModelVersionsDialog
               open
               onClose={() => setVersionsDialogOpen(false)}
               workspaceId={selectedWorkspaceId}
               modelId={editModel.id}
               modelUri={editModel.modelUri}
               canWrite={canWrite}
               onDeleted={invalidateWorkspaceData}
            />
         )}

         {/* Checkout Confirmation Dialog (lock → unlock) */}
         {checkoutDialogOpen && checkoutModel && (
            <ModelDialog
               open
               onClose={handleCheckoutDialogClose}
               title={t('modelLibrary.checkoutDialogTitle')}
               isLoading={isCheckingOut}
               isError={!!checkoutError}
               error={checkoutError ? new Error(checkoutError) : null}
               actions={checkoutError ? [] : [
                  {
                     label: isCheckingOut ? t('modelLibrary.checkingOut') : t('common.ok'),
                     onClick: handleCheckoutConfirm,
                     disabled: isCheckingOut
                  }
               ]}
            >
               {!checkoutError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1">
                        {t('modelLibrary.checkoutConfirmation', { name: checkoutModel.name ?? checkoutModel.uri })}
                     </Typography>
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Check-in Dialog (unlock → lock) */}
         {checkinDialogOpen && checkinModel && (
            <ModelDialog
               open
               onClose={handleCheckinDialogClose}
               title={t('modelLibrary.checkinDialogTitle')}
               isLoading={isCheckingIn}
               isError={!!checkinError}
               error={checkinError ? new Error(checkinError) : null}
               actions={checkinError ? [] : [
                  {
                     label: isCheckingIn ? t('modelLibrary.checkingIn') : t('common.ok'),
                     onClick: handleCheckinConfirm,
                     disabled: isCheckingIn || (checkinAction === 'publish' && !checkinDescription.trim()),
                     color: checkinAction === 'discard' ? 'error' : 'primary'
                  }
               ]}
            >
               {!checkinError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1" sx={{ mb: 2 }}>
                        {t('modelLibrary.checkinPrompt', { name: checkinModel.name ?? checkinModel.uri })}
                     </Typography>
                     <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2 }}>
                        <RadioGroup
                           value={checkinAction}
                           onChange={(e) => setCheckinAction(e.target.value as 'keep' | 'publish' | 'discard')}
                        >
                           {([
                              { value: 'keep', label: t('modelLibrary.checkinKeep'), desc: t('modelLibrary.checkinKeepDesc') },
                              { value: 'publish', label: t('modelLibrary.checkinPublish'), desc: t('modelLibrary.checkinPublishDesc') },
                              { value: 'discard', label: t('modelLibrary.checkinDiscard'), desc: t('modelLibrary.checkinDiscardDesc') },
                           ] as const).map(opt => (
                              <FormControlLabel
                                 key={opt.value}
                                 value={opt.value}
                                 control={<Radio sx={{ pt: 0.5 }} />}
                                 sx={{ alignItems: 'flex-start', mb: 1 }}
                                 label={(
                                    <Box sx={{ display: 'flex', flexDirection: 'column' }}>
                                       <Typography variant="body2">{opt.label}</Typography>
                                       <Typography variant="caption" color="text.secondary">{opt.desc}</Typography>
                                    </Box>
                                 )}
                              />
                           ))}
                        </RadioGroup>
                     </Box>

                     {/* Keeping and publishing both settle on a version; publishing also
                         requires a (mandatory) description. */}
                     {checkinAction !== 'discard' && (
                        // Theme spacing is 1px per unit, so these are pixel values; 8 is the
                        // gap the other dialogs put between form fields.
                        <Box sx={{ mt: 8, display: 'flex', flexDirection: 'column', gap: 8 }}>
                           <TextField
                              label={t('modelLibrary.checkinVersionLabel', 'Version')}
                              value={checkinVersion}
                              onChange={(e) => setCheckinVersion(e.target.value)}
                              helperText={checkinAction === 'keep'
                                 ? t('modelLibrary.checkinKeepVersionHelp',
                                    'The -alpha suffix marks a model as being edited, so it is dropped.')
                                 : undefined}
                              size="small"
                              fullWidth
                           />
                           {checkinAction === 'publish' && (
                              <TextField
                                 label={t('modelLibrary.checkinDescriptionLabel', 'Description')}
                                 value={checkinDescription}
                                 onChange={(e) => setCheckinDescription(e.target.value)}
                                 required
                                 error={!checkinDescription.trim()}
                                 helperText={!checkinDescription.trim()
                                    ? t('modelLibrary.checkinDescriptionRequired', 'A description is required to publish.')
                                    : ' '}
                                 multiline
                                 minRows={3}
                                 fullWidth
                              />
                           )}
                        </Box>
                     )}
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Delete Confirmation Dialog — mounted only when open to guarantee fresh state */}
         {deleteDialogOpen && (
            <ModelDialog
               open
               onClose={handleDeleteDialogClose}
               title={t('modelLibrary.deleteDialogTitle', 'Delete Model')}
               isLoading={isDeleting}
               isError={!!deleteError}
               error={deleteError ? new Error(deleteError) : null}
               actions={deleteError ? [] : [
                  {
                     label: isDeleting ? t('common.deleting', 'Deleting...') : t('common.ok', 'OK'),
                     onClick: handleDeleteConfirm,
                     disabled: isDeleting
                  }
               ]}
            >
               {!deleteError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1">
                        {(() => {
                           const ns = namespaces.find(n => n.id === modelToDelete?.id);
                           const name = modelToDelete?.name ?? modelToDelete?.modelUri;
                           return ns?.isPrivate
                              ? t('modelLibrary.deletePrivateConfirmation', { name })
                              : t('modelLibrary.deleteSharedConfirmation', { name });
                        })()}
                     </Typography>
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Edit Workspace Dialog */}
         {editWorkspaceDialogOpen && (
            <ModelDialog
               open
               onClose={() => { setEditWorkspaceDialogOpen(false); setEditWorkspaceError(null); }}
               title={t('modelLibrary.editWorkspaceDialogTitle', 'Edit Workspace')}
               isLoading={isSavingWorkspace}
               isError={!!editWorkspaceError}
               error={editWorkspaceError ? new Error(editWorkspaceError) : null}
               actions={editWorkspaceError ? [] : [
                  {
                     label: isSavingWorkspace ? t('common.saving', 'Saving...') : t('common.ok', 'OK'),
                     onClick: handleEditWorkspaceConfirm,
                     disabled: isSavingWorkspace || !editWorkspaceName.trim()
                  }
               ]}
            >
               {!editWorkspaceError && (
                  <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                     <TextField
                        label={t('modelLibrary.createWorkspaceName', 'Name')}
                        value={editWorkspaceName}
                        onChange={(e) => setEditWorkspaceName(e.target.value)}
                        fullWidth
                        autoFocus
                     />
                     <TextField
                        label={t('modelLibrary.createWorkspaceDescription', 'Description')}
                        value={editWorkspaceDescription}
                        onChange={(e) => setEditWorkspaceDescription(e.target.value)}
                        fullWidth
                        multiline
                        rows={3}
                     />
                     <TextField
                        label={t('modelLibrary.workspaceAcl', 'Access List (one email per line)')}
                        value={editWorkspaceAcl}
                        onChange={(e) => setEditWorkspaceAcl(e.target.value)}
                        fullWidth
                        multiline
                        rows={4}
                        helperText={t('modelLibrary.workspaceAclHelp', 'Email addresses of users who can access this workspace')}
                     />
                  </Box>
               )}
            </ModelDialog>
         )}

         {/* Delete Workspace Dialog */}
         {deleteWorkspaceDialogOpen && (
            <ModelDialog
               open
               onClose={() => { setDeleteWorkspaceDialogOpen(false); setDeleteWorkspaceError(null); }}
               title={t('modelLibrary.deleteWorkspaceDialogTitle', 'Delete Workspace')}
               isLoading={isDeletingWorkspace}
               isError={!!deleteWorkspaceError}
               error={deleteWorkspaceError ? new Error(deleteWorkspaceError) : null}
               actions={deleteWorkspaceError ? [] : [
                  {
                     label: isDeletingWorkspace ? t('common.deleting', 'Deleting...') : t('common.ok', 'OK'),
                     onClick: handleDeleteWorkspaceConfirm,
                     disabled: isDeletingWorkspace
                  }
               ]}
            >
               {!deleteWorkspaceError && (
                  <Box sx={{ p: 3 }}>
                     <Typography variant="body1">
                        {t('modelLibrary.deleteWorkspaceConfirmation',
                           { name: currentWorkspace?.applicationName?.text ?? selectedWorkspaceId,
                             defaultValue: 'Are you sure you want to delete workspace "{{name}}"? This cannot be undone.' })}
                     </Typography>
                  </Box>
               )}
            </ModelDialog>
         )}
      </Box>
   );
};

export default ModelLibraryPage;
