import * as React from 'react';
import { useTranslation } from 'react-i18next';

import { useTheme, type SxProps, type Theme } from '@mui/material';
import AppBar from '@mui/material/AppBar';
import SearchIcon from '@mui/icons-material/Search';
import RefreshIcon from '@mui/icons-material/Refresh';
import IconButton from '@mui/material/IconButton';
import Box from '@mui/material/Box';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';

interface SearchBarProps {
   hint?: string,
   value?: string,
   filters?: string[],
   onChange?: React.ChangeEventHandler<HTMLInputElement | HTMLTextAreaElement>,
   onRefresh?: () => void,
   sx?: SxProps<Theme>;
   children?: React.ReactNode;
   rightActions?: React.ReactNode;
}

export const SearchBar = ({ hint, value, onChange, onRefresh, sx, children, rightActions }: SearchBarProps) => {
   const theme = useTheme();
   const { t } = useTranslation();

   return (
      <Box
         sx={{
            flexGrow: 1,
            padding: 2,
            ...(Object(sx)),
         }}
      >
         <AppBar position="static" sx={{ backgroundColor: theme.palette.grey[200] }}>
            {/* Stack drops to a column on xs/sm — each child (search box,
                page-specific filters via `children`, right actions, refresh)
                becomes its own row. On md+ the original single-row toolbar
                shape is restored. */}
            <Stack
               direction={{ xs: 'column', md: 'row' }}
               // Theme `spacing` is 1 (1px units), so spacing={1} between
               // stacked rows was a literal pixel and the FormControl
               // outlined-input labels overlapped the row above. Bump the
               // xs gap to leave headroom for the floating labels; give
               // md+ a real inline gap so the controls aren't crammed
               // together on wide screens.
               spacing={{ xs: 12, md: 8 }}
               alignItems={{ xs: 'stretch', md: 'center' }}
               sx={{ px: { xs: 4, md: 6 }, py: { xs: 4, md: 1 } }}
            >
               <Box
                  sx={{
                     display: 'flex',
                     alignItems: 'center',
                     gap: 4,
                     flex: { md: '0 0 auto' },
                  }}
               >
                  <SearchIcon sx={{ color: theme.palette.grey[800] }} />
                  <TextField
                     variant="outlined"
                     placeholder={hint ?? t('main.search')}
                     size="small"
                     value={value}
                     onChange={onChange}
                     sx={{ flex: 1, minWidth: 0 }}
                  />
               </Box>
               {children}
               {/* Spacer only matters on md+ where the row is wide enough
                   to push right-side actions to the edge. On xs/sm the
                   stack is a column, so the spacer is harmless. */}
               <Box sx={{ display: { xs: 'none', md: 'block' }, flexGrow: 1 }} />
               {rightActions}
               {onRefresh && (
                  <IconButton
                     onClick={onRefresh}
                     size="small"
                     sx={{ color: theme.palette.grey[800], alignSelf: { xs: 'flex-end', md: 'center' } }}
                     title={t('common.refresh', 'Refresh')}
                  >
                     <RefreshIcon />
                  </IconButton>
               )}
            </Stack>
         </AppBar>
      </Box>
   );
}
