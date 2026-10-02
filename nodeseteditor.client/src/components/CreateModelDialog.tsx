import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';

import api, { ApiError } from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import { UserContext } from '../UserContext';
import { useLicenseOptions } from '../hooks/useLicenseOptions';
import { computeModelUriPrefix, sanitizeUriSegment, validateModelUri } from '../utils/modelUri';

import { ModelDialog } from './ModelDialog';
import { LicenseFields, isLicenseValid, type LicenseValue } from './LicenseFields';

interface CreateModelDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /** Called after the model exists, with its namespace URI. */
   onCreated?: (modelUri: string) => void;
}

/**
 * Creates a new, private, editable model in a workspace.
 *
 * Extracted from ModelLibraryPage so the CSV import flow can offer the same dialog rather
 * than a second, drifting one — a model's license and copyright are fixed at genesis, so
 * there is exactly one chance to get these fields right and only one place they should be
 * asked for.
 *
 * Mount it on demand (render only while it should be open): the fields are seeded on mount
 * from the user's defaults, and a fresh mount is what resets them.
 */
export const CreateModelDialog: React.FC<CreateModelDialogProps> = ({
   open,
   onClose,
   workspaceId,
   onCreated,
}) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const {
      email: userEmail,
      defaultDomain: userDefaultDomain,
      defaultLicense: userDefaultLicense,
      defaultLicenseUrl: userDefaultLicenseUrl,
      defaultCopyrightHolder: userDefaultCopyright,
   } = React.useContext(UserContext);
   const { data: licenseOptions } = useLicenseOptions();

   // Captures the urn:<domain>:YYYY-MM:<localpart>: prefix for this dialog session. Frozen on
   // mount so the YYYY-MM doesn't tick forward mid-edit and the email lookup happens once.
   const [uriPrefix] = React.useState<string | null>(
      () => computeModelUriPrefix(userEmail, new Date(), userDefaultDomain));

   const [name, setName] = React.useState('');
   const [uri, setUri] = React.useState(uriPrefix ?? '');
   // New models start as an editable working copy — default to the -alpha pre-release.
   const [version, setVersion] = React.useState('1.0.0-alpha');
   const [description, setDescription] = React.useState('');
   const [isCreating, setIsCreating] = React.useState(false);
   const [createError, setCreateError] = React.useState<string | null>(null);

   // License & copyright default to the user's preferences via the "use my defaults"
   // checkbox; unchecking enables a per-model override.
   const [useDefaults, setUseDefaults] = React.useState(true);
   const [license, setLicense] = React.useState<LicenseValue>({ license: '', licenseUrl: '' });
   const [copyright, setCopyright] = React.useState('');

   // Whether the user has manually edited the URI. Once true, Name changes no longer
   // overwrite it for the rest of the session.
   const [uriManuallyEdited, setUriManuallyEdited] = React.useState(false);

   const effectiveLicense = useDefaults
      ? { license: userDefaultLicense ?? '', licenseUrl: userDefaultLicenseUrl ?? '' }
      : license;
   const effectiveCopyright = useDefaults ? (userDefaultCopyright ?? '') : copyright;

   const licenseOk = isLicenseValid(
      effectiveLicense.license, effectiveLicense.licenseUrl, licenseOptions ?? []);
   const copyrightOk = !!effectiveCopyright.trim();
   // Name is mandatory and must be at least 2 characters.
   const nameOk = name.trim().length >= 2;
   const uriError = validateModelUri(uri);

   const handleNameChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      const next = e.target.value;
      setName(next);
      // Mirror the (sanitized) name into the URI's last segment until the user takes manual
      // control. We rebuild from the frozen prefix rather than mutating the existing URI to
      // avoid drifting if the user has already partially typed something.
      if (!uriManuallyEdited && uriPrefix) setUri(uriPrefix + sanitizeUriSegment(next));
   };

   const handleUriChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      setUri(e.target.value);
      // Any keystroke in the URI field locks it from further name-driven updates. We don't
      // try to detect "the user typed exactly what we would have generated" — once they
      // touch it, they own it.
      setUriManuallyEdited(true);
   };

   const handleConfirm = async () => {
      const trimmedUri = uri.trim();
      if (!trimmedUri || uriError || !workspaceId) return;
      if (!nameOk || !licenseOk || !copyrightOk) return;

      setIsCreating(true);
      setCreateError(null);
      try {
         await api.post('/opcua/v1/namespaces/info', {
            uri: trimmedUri,
            name: name.trim(),
            version: version.trim() || null,
            description: description.trim() || null,
            license: effectiveLicense.license.trim(),
            licenseUrl: effectiveLicense.licenseUrl.trim() || null,
            copyrightHolder: effectiveCopyright.trim(),
         }, { headers: { 'OpcUa-Server': idToUrn(workspaceId) } });

         // Adding a model changes the whole address space, so refresh every workspace-scoped
         // query (namespaces, plus the tree and type queries), not just the model list.
         await queryClient.invalidateQueries({
            predicate: (q) => Array.isArray(q.queryKey)
               && (q.queryKey[0] === 'discovery' || q.queryKey.includes(workspaceId)),
         });

         onCreated?.(trimmedUri);
         onClose();
      } catch (e) {
         setCreateError(e instanceof ApiError
            ? e.message
            : (e instanceof Error ? e.message : 'Failed to create model'));
      } finally {
         setIsCreating(false);
      }
   };

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('modelLibrary.createModelDialogTitle')}
         isLoading={isCreating}
         isError={!!createError}
         error={createError ? new Error(createError) : null}
         actions={createError ? [] : [
            {
               label: isCreating ? t('modelLibrary.creating') : t('common.ok'),
               onClick: handleConfirm,
               disabled: isCreating || !nameOk || !uri.trim() || !!uriError
                  || !licenseOk || !copyrightOk,
            },
         ]}
      >
         {!createError && (
            <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
               <TextField
                  label={t('modelLibrary.createModelName')}
                  value={name}
                  onChange={handleNameChange}
                  fullWidth
                  required
                  autoFocus
                  error={!!name && !nameOk}
                  helperText={!!name && !nameOk
                     ? t('modelLibrary.createModelNameTooShort', 'Name must be at least 2 characters.')
                     : undefined}
               />
               <TextField
                  label={t('modelLibrary.createModelUri')}
                  value={uri}
                  onChange={handleUriChange}
                  fullWidth
                  required
                  error={!!uri.trim() && !!uriError}
                  helperText={uri.trim() ? (uriError ?? undefined) : undefined}
               />
               <TextField
                  label={t('modelLibrary.createModelVersion')}
                  value={version}
                  onChange={(e) => setVersion(e.target.value)}
                  fullWidth
               />
               <TextField
                  label={t('modelLibrary.createModelDescription')}
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  fullWidth
                  multiline
                  rows={3}
               />

               {/* License & copyright — locked once the model is created. */}
               <Box sx={{ p: 6, display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <FormControlLabel
                     control={
                        <Checkbox
                           checked={useDefaults}
                           onChange={(e) => setUseDefaults(e.target.checked)}
                        />
                     }
                     label={t('modelLibrary.useDefaultLicense', 'Use my default license & copyright')}
                  />
                  <TextField
                     label={t('modelLibrary.copyrightHolder', 'Copyright holder')}
                     value={effectiveCopyright}
                     onChange={(e) => setCopyright(e.target.value)}
                     fullWidth
                     required
                     disabled={useDefaults}
                     error={!copyrightOk}
                     helperText={!copyrightOk
                        ? t('modelLibrary.copyrightRequired', 'A copyright holder is required.')
                        : undefined}
                  />
                  <LicenseFields
                     options={licenseOptions ?? []}
                     license={effectiveLicense.license}
                     licenseUrl={effectiveLicense.licenseUrl}
                     onChange={setLicense}
                     disabled={useDefaults}
                     size="medium"
                  />
                  {useDefaults && !licenseOk && (
                     <Typography variant="caption" color="error">
                        {t('modelLibrary.defaultLicenseMissing',
                           'Set a default license and copyright holder in your account settings.')}
                     </Typography>
                  )}
               </Box>
            </Box>
         )}
      </ModelDialog>
   );
};

export default CreateModelDialog;
