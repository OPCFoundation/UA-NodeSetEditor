import * as React from 'react';

import type { TypeFamilies } from './spec';

/**
 * The type-family subtype closures every NodeIcon resolves against. Populated by
 * TypeFamiliesProvider; `undefined` until the closures have loaded, which
 * resolveNodeIcon() treats as "use the NodeClass-level glyph".
 *
 * A context rather than a hook call inside NodeIcon because the icon renders once per
 * tree row: calling useTypeFamilies() there would open four query subscriptions per
 * visible node instead of four for the whole app.
 */
export const TypeFamiliesContext = React.createContext<TypeFamilies | undefined>(undefined);

export function useTypeFamiliesContext(): TypeFamilies | undefined {
   return React.useContext(TypeFamiliesContext);
}
