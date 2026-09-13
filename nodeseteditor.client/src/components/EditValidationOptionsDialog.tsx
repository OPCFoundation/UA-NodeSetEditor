import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Autocomplete from '@mui/material/Autocomplete';
import TextField from '@mui/material/TextField';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import Checkbox from '@mui/material/Checkbox';
import Switch from '@mui/material/Switch';
import Typography from '@mui/material/Typography';

import { ModelDialog } from './ModelDialog';
import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { ProfileGroup, ValidationDocumentInfo, ValidationResult, ValidationDocumentSettings } from '../model/Validation';

interface Props {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   doc: ValidationDocumentInfo;
}

const NONE = '';

export const EditValidationOptionsDialog: React.FC<Props> = ({ open, onClose, workspaceId, doc }) => {
   const { t } = useTranslation();
   const queryClient = useQueryClient();
   const header = React.useMemo(() => ({ 'OpcUa-Server': idToUrn(workspaceId) }), [workspaceId]);

   const [profileGroupName, setProfileGroupName] = React.useState(doc.profileGroupName ?? NONE);
   const [verbose, setVerbose] = React.useState(!!doc.verbose);
   const [suppressed, setSuppressed] = React.useState<Set<string>>(new Set(doc.suppressedCodes ?? []));
   const [saving, setSaving] = React.useState(false);
   const [error, setError] = React.useState<Error | null>(null);

   // Profile groups (sorted server-side) for the dropdown.
   const { data: profileGroups } = useQuery<ProfileGroup[]>({
      queryKey: ['validation-profile-groups'],
      queryFn: async () => (await api.get<ProfileGroup[]>('/opcua/v1/validation/profile-groups', { headers: header })).data,
      enabled: open,
      staleTime: 6 * 60 * 60 * 1000,
   });

   // Error codes present in the latest result, for the suppress checklist.
   const { data: result } = useQuery<ValidationResult>({
      queryKey: ['validation-result', workspaceId, doc.jobId],
      queryFn: async () => (await api.get<ValidationResult>(`/opcua/v1/validation/jobs/${doc.jobId}/result`, { headers: header })).data,
      enabled: open && !!doc.jobId,
   });

   // Union of codes found in the current results with any already-suppressed codes
   // (so a saved suppression stays visible/checked even if it's no longer produced).
   const codes = React.useMemo(() => {
      const set = new Set<string>();
      for (const e of result?.entries ?? []) if (e.code) set.add(e.code);
      for (const c of doc.suppressedCodes ?? []) set.add(c);
      return Array.from(set).sort((a, b) => a.localeCompare(b));
   }, [result, doc.suppressedCodes]);

   const toggleCode = (code: string) => setSuppressed(prev => {
      const next = new Set(prev);
      if (next.has(code)) next.delete(code); else next.add(code);
      return next;
   });

   const handleSave = async () => {
      setSaving(true);
      setError(null);
      try {
         const payload: ValidationDocumentSettings = {
            profileGroupName: profileGroupName || null,
            verbose,
            suppressedCodes: Array.from(suppressed),
         };
         await api.put(`/opcua/v1/validation/documents/${doc.id}/settings`, payload, { headers: header });
         await queryClient.invalidateQueries({ queryKey: ['validation-docs', workspaceId] });
         onClose();
      } catch (e) {
         setError(e instanceof Error ? e : new Error(t('validation.optionsSaveFailed', 'Failed to save options.')));
      } finally {
         setSaving(false);
      }
   };

   const groupOptions = React.useMemo(() => (profileGroups ?? []).map(g => g.fullName), [profileGroups]);

   return (
      <ModelDialog
         open={open}
         onClose={onClose}
         title={t('validation.optionsTitle', 'Validation options')}
         maxWidth="sm"
         isError={!!error}
         error={error}
         actions={[
            { label: t('common.cancel', 'Cancel'), onClick: onClose, color: 'inherit' },
            { label: saving ? t('common.saving', 'Saving…') : t('common.save', 'Save'), onClick: handleSave, disabled: saving },
         ]}
      >
         <Box sx={{ px: 20, py: 20, display: 'flex', flexDirection: 'column', gap: 16 }}>
            {/* Extra mb so the space below the filename matches the py above it. */}
            <Typography variant="body2" color="text.secondary" noWrap sx={{ mb: 4 }}>
               {doc.fileName}
            </Typography>

            {/* 1) Profile Group Name — freeSolo: pick a known group or type any value; clear = none. */}
            <Autocomplete
               freeSolo
               size="small"
               fullWidth
               options={groupOptions}
               inputValue={profileGroupName}
               onInputChange={(_e, val) => setProfileGroupName(val)}
               renderInput={(params) => (
                  <TextField
                     {...params}
                     label={t('validation.profileGroup', 'Profile Group Name')}
                     placeholder={t('validation.profileGroupNone', 'Select none')}
                  />
               )}
            />

            {/* 2) Verbose */}
            <FormControlLabel
               control={<Switch checked={verbose} onChange={(e) => setVerbose(e.target.checked)} />}
               label={t('validation.verbose', 'Verbose')}
            />

            {/* 3) Errors to suppress — hidden entirely when there are no result codes. */}
            {codes.length > 0 && (
               <Box>
                  <Typography variant="subtitle2" sx={{ mb: 1 }}>
                     {t('validation.suppressErrors', 'Errors to suppress')}
                  </Typography>
                  <FormGroup sx={{ maxHeight: 260, overflowY: 'auto' }}>
                     {codes.map(code => (
                        <FormControlLabel
                           key={code}
                           control={<Checkbox size="small" checked={suppressed.has(code)} onChange={() => toggleCode(code)} />}
                           label={code}
                        />
                     ))}
                  </FormGroup>
               </Box>
            )}
         </Box>
      </ModelDialog>
   );
};
