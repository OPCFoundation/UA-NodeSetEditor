import { useQuery } from '@tanstack/react-query';

import api from '../api/axios.api';
import type { LicenseOption } from '../model/LicenseOption';

/**
 * Fetches the selectable license catalog (curated SPDX subset plus an
 * "Other / Proprietary" entry). The list is small and rarely changes, so it
 * is cached for the session.
 */
export function useLicenseOptions() {
   return useQuery({
      queryKey: ['licenses'],
      queryFn: async () => {
         const response = await api.get<LicenseOption[]>('/opcua/v1/licenses');
         return response.data ?? [];
      },
      staleTime: 1000 * 60 * 60, // 1 hour
   });
}
