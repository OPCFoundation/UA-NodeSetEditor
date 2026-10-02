import * as React from 'react';

import { WorkspaceContext } from '../WorkspaceContext';
import { useTypeFamilies } from '../hooks/useTypeFamilies';
import { TypeFamiliesContext } from './typeFamiliesContext';

/**
 * Loads the type-family subtype closures for the selected workspace and publishes them
 * to every NodeIcon. Mount once, inside WorkspaceProvider — see ./typeFamiliesContext.ts
 * for why this is a context rather than a hook in the icon itself.
 */
export const TypeFamiliesProvider: React.FC<{ children?: React.ReactNode }> = ({ children }) => {
   const { selectedWorkspaceId } = React.useContext(WorkspaceContext);
   const families = useTypeFamilies(selectedWorkspaceId);
   return (
      <TypeFamiliesContext.Provider value={families}>
         {children}
      </TypeFamiliesContext.Provider>
   );
};

export default TypeFamiliesProvider;
