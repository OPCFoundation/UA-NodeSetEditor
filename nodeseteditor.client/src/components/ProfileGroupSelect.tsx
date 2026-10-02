import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import Autocomplete from '@mui/material/Autocomplete';
import TextField from '@mui/material/TextField';

import api from '../api/axios.api';
import { idToUrn } from '../model/WorkspaceDescription';
import type { ProfileGroup } from '../model/Validation';

interface Props {
   /** Current profile group full name; empty string = none. */
   value: string;
   onChange: (value: string) => void;
   /** Workspace for the OpcUa-Server header. The endpoint needs auth but no workspace. */
   workspaceId?: string;
   /** Defer the fetch until the control is actually on screen (e.g. a dialog is open). */
   enabled?: boolean;
   disabled?: boolean;
}

/**
 * Picks a profile group from profiles.opcfoundation.org (proxied server-side, sorted there).
 * `freeSolo` so a group that isn't in the list can still be typed, and clearing the box means
 * "none". Shared by the validator's options dialog and the conformance-unit profile settings,
 * which also share the react-query cache entry — the list changes rarely.
 */
export const ProfileGroupSelect: React.FC<Props> = ({ value, onChange, workspaceId, enabled = true, disabled }) => {
   const { t } = useTranslation();
   const header = React.useMemo(
      () => (workspaceId ? { 'OpcUa-Server': idToUrn(workspaceId) } : undefined),
      [workspaceId]);

   const { data: profileGroups } = useQuery<ProfileGroup[]>({
      queryKey: ['validation-profile-groups'],
      queryFn: async () => (await api.get<ProfileGroup[]>('/opcua/v1/validation/profile-groups', { headers: header })).data,
      enabled,
      staleTime: 6 * 60 * 60 * 1000,
   });

   const options = React.useMemo(() => (profileGroups ?? []).map(g => g.fullName), [profileGroups]);

   return (
      <Autocomplete
         freeSolo
         size="small"
         fullWidth
         disabled={disabled}
         options={options}
         inputValue={value}
         onInputChange={(_e, val) => onChange(val)}
         renderInput={(params) => (
            <TextField
               {...params}
               label={t('validation.profileGroup', 'Profile Group Name')}
               placeholder={t('validation.profileGroupNone', 'Select none')}
            />
         )}
      />
   );
};
