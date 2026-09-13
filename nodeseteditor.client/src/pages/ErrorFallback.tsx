import { type FallbackProps } from "react-error-boundary";

import {
   Alert,
   AlertTitle,
   Typography
} from '@mui/material';

import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';

import type { ApiError } from "../api/axios.api";

function ErrorFallback({ error }: FallbackProps) {
   const apiError = error as ApiError
   const baseError = error as Error;
   const code = apiError?.code ?? baseError?.name ?? "Unknown";
   const message = apiError?.message ?? baseError?.message ?? "Unknown";
   return (
      <Alert icon={<ErrorOutlineIcon fontSize="inherit" />} severity="error">
         {code ?
            <AlertTitle sx={{ fontWeight: 'bolder' }} >
               {code}
            </AlertTitle> : undefined
         }
         {message ?
            <Typography variant="body1" component="div" noWrap>
               {message}
            </Typography> : undefined
         }
      </Alert>
   );
}

export default ErrorFallback;
