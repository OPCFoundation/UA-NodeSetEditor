import * as React from 'react';

import {
   Alert,
   AlertTitle,
   Box,
   CircularProgress,
   Typography
} from '@mui/material';

import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import type { ApiError } from '../api/axios.api';

interface ContentLoaderProps {
   children?: React.ReactNode
   isError?: boolean,
   isLoading?: boolean,
   isWarning?: boolean,
   warningMessage?: string,
   error?: Error | null
}

export const ContentLoader = ({ children, isError, isLoading, isWarning, warningMessage, error }: ContentLoaderProps) => {
   if (isError) {
      const apiError = error as ApiError;
      const code = apiError?.code ?? error?.name;
      return (
         <>
            <Alert icon={<ErrorOutlineIcon fontSize="inherit" />} severity="error">
               {code ?
                  <AlertTitle sx={{ fontWeight: 'bolder' }} >
                     {apiError?.code ?? error?.name}
                  </AlertTitle> : undefined
               }
               {error?.message ?
                  <Typography variant="body1" component="div" sx={{ whiteSpace: 'pre-line', wordBreak: 'break-word' }}>
                     {error?.message}
                  </Typography> : undefined
               }
            </Alert>
            {children}
         </>
      );
   }
   if (isLoading) {
      return (
         <Box sx={{ display: 'flex', width: '100%', minHeight: '200px', alignItems: 'center', justifyContent: 'center' }}>
            <CircularProgress />
         </Box>
      );
   }
   if (isWarning) {
      return (
         <>
            <Alert icon={<WarningAmberIcon fontSize="inherit" />} severity="warning">
               <Typography variant="body1" component="div">
                  {warningMessage ?? 'No data available.'}
               </Typography>
            </Alert>
            {children}
         </>
      );
   }

   return <>{children}</>;
}
