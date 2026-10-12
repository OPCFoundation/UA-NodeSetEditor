import { useTranslation } from 'react-i18next';

import Box from '@mui/material/Box';
import Link from '@mui/material/Link';
import Toolbar from '@mui/material/Toolbar';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';

import { getRuntimeConfig } from '../runtimeConfig';
import { FooterLinks } from './FooterLinks.tsx';

const REPO_URL = 'https://github.com/OPCFoundation/UA-NodeSetEditor';

/** The server sends the commit timestamp as ISO 8601 ("O"); state it in UTC, as it was sent. */
const formatCommitDate = (iso: string): string => {
   const parsed = new Date(iso);
   return Number.isNaN(parsed.getTime())
      ? iso
      : `${parsed.toISOString().replace('T', ' ').slice(0, 19)} UTC`;
};

export const Footer = () => {
   const { t } = useTranslation();

   // Reported by the server through /api/config, which main.tsx awaits before the app mounts, so
   // this is already resolved on first render and cannot change while the page is open.
   const { buildVersion, commitId, commitDate } = getRuntimeConfig();

   const label = [buildVersion, commitDate.slice(0, 10)].filter(Boolean).join(' · ');

   return (
      <Toolbar variant='dense' disableGutters sx={{ py: 0, minHeight: '36px', justifyContent: 'space-between' }}>
         <Box ml={6} sx={{ flexGrow: 0, display: { xs: 'none', md: 'flex' } }}>
            <FooterLinks />
         </Box>
         <Box
            alignContent='right'
            textAlign='right'
            mx={6}
            sx={{
               display: 'flex',
               alignItems: 'center'
            }}
         >
            {/* Without a commit there is nothing to link to — a container image built from a
                context with no git history reports the version alone. With nothing at all,
                show nothing rather than an empty bracket. */}
            {label && (commitId ? (
               <Tooltip title={t('footer.commitTooltip', { commit: commitId, date: formatCommitDate(commitDate) })}>
                  {/* The footer bar IS the accent colour in light mode, so this has to take the
                      bar's foreground like the links beside it — stated per link state so no
                      global `a:link` rule can outrank it. */}
                  <Link
                     href={`${REPO_URL}/tree/${commitId}`}
                     target='_blank'
                     rel='noopener noreferrer'
                     underline='hover'
                     sx={{
                        color: 'inherit',
                        '&:link, &:visited, &:hover, &:active': { color: 'inherit' },
                     }}
                  >
                     <Typography variant='body2'>{label}</Typography>
                  </Link>
               </Tooltip>
            ) : (
               <Typography variant='body2'>{label}</Typography>
            ))}
         </Box>
      </Toolbar>
   );
}
