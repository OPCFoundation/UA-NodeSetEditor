import * as React from 'react';
import { useTranslation } from 'react-i18next';

import Box from '@mui/material/Box';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';

import type { LicenseOption } from '../model/LicenseOption';

/** True when the value is a valid absolute http(s) URL. */
export function isValidHttpUrl(value: string | undefined | null): boolean {
   if (!value || !value.trim()) return false;
   try {
      const u = new URL(value.trim());
      return u.protocol === 'http:' || u.protocol === 'https:';
   } catch {
      return false;
   }
}

/**
 * A license is "custom" when its identifier is not one of the non-custom
 * catalog entries (i.e. the user chose "Other / Proprietary" and typed their
 * own id). Empty is treated as not-custom (nothing chosen yet).
 */
export function isCustomLicense(license: string, options: LicenseOption[]): boolean {
   const id = license.trim();
   if (!id) return false;
   return !options.some(o => !o.isCustom && o.spdxId === id);
}

/**
 * Whether the current license selection is submittable: a non-empty license,
 * and — for a custom/"Other" license — a valid http(s) reference URL.
 */
export function isLicenseValid(license: string, licenseUrl: string, options: LicenseOption[]): boolean {
   const id = license.trim();
   if (!id) return false;
   if (isCustomLicense(id, options)) return isValidHttpUrl(licenseUrl);
   return true;
}

export interface LicenseValue {
   license: string;
   licenseUrl: string;
}

interface LicenseFieldsProps {
   license: string;
   licenseUrl: string;
   options: LicenseOption[];
   onChange: (next: LicenseValue) => void;
   disabled?: boolean;
   size?: 'small' | 'medium';
}

/**
 * Data-driven license selector. Renders a dropdown of catalog licenses; when
 * the "Other / Proprietary" entry is chosen it reveals a custom identifier
 * field plus a required reference-URL field (validated as http(s)).
 */
export const LicenseFields: React.FC<LicenseFieldsProps> = ({
   license, licenseUrl, options, onChange, disabled, size = 'small',
}) => {
   const { t } = useTranslation();
   const customOption = options.find(o => o.isCustom);
   const matchedCatalog = options.find(o => !o.isCustom && o.spdxId === license.trim());

   // Whether the user explicitly picked "Other / Proprietary" (so an empty custom license
   // still shows the custom fields). Tracked locally; the derived custom-mode below OR's it in.
   const [userPickedOther, setUserPickedOther] = React.useState(false);

   // Derive custom-mode from the CURRENT options — never latch it. Crucially, only treat a
   // license as custom once the catalog has actually loaded: isCustomLicense() returns true for
   // any non-empty id against an empty options list, which would otherwise misclassify a catalog
   // license (e.g. "MIT") as custom while the catalog is still loading and stick there.
   const optionsLoaded = options.length > 0;
   const licenseIsCustom = optionsLoaded && isCustomLicense(license, options);
   const customMode = userPickedOther || licenseIsCustom;

   const selectValue = customMode
      ? (customOption?.spdxId ?? '')
      : (matchedCatalog ? matchedCatalog.spdxId : '');

   const handleSelect = (value: string) => {
      if (customOption && value === customOption.spdxId) {
         setUserPickedOther(true);
         onChange({ license: '', licenseUrl: '' });
      } else {
         setUserPickedOther(false);
         onChange({ license: value, licenseUrl: '' });
      }
   };

   const urlError = customMode && !!licenseUrl.trim() && !isValidHttpUrl(licenseUrl);

   return (
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
         <TextField
            select
            size={size}
            fullWidth
            disabled={disabled}
            label={t('license.label', 'License')}
            value={selectValue}
            onChange={(e) => handleSelect(e.target.value)}
         >
            {options.map(o => (
               <MenuItem key={o.spdxId} value={o.spdxId}>{o.name}</MenuItem>
            ))}
         </TextField>

         {customMode && (
            <>
               <TextField
                  sx={{ mt: 8 }}
                  size={size}
                  fullWidth
                  disabled={disabled}
                  label={t('license.customId', 'License identifier')}
                  placeholder="LicenseRef-My-License"
                  helperText={t('license.customIdHelp', 'An SPDX-style identifier for your license.')}
                  value={license}
                  onChange={(e) => onChange({ license: e.target.value, licenseUrl })}
               />
               <TextField
                  sx={{ mt: 4 }}
                  size={size}
                  fullWidth
                  required
                  disabled={disabled}
                  label={t('license.url', 'License URL')}
                  placeholder="https://example.com/license"
                  error={urlError}
                  helperText={urlError
                     ? t('license.urlInvalid', 'Enter a valid http(s) URL.')
                     : t('license.urlHelp', 'A link to the full license text (required for a custom license).')}
                  value={licenseUrl}
                  onChange={(e) => onChange({ license, licenseUrl: e.target.value })}
               />
            </>
         )}
      </Box>
   );
};

export default LicenseFields;
