import * as React from 'react';

import { WorkspaceContext, type WorkspaceContextType, type NavigateToNode, type SelectedType } from './WorkspaceContext';

interface WorkspaceProviderProps {
   children?: React.ReactNode
}

export const WorkspaceProvider = ({ children }: WorkspaceProviderProps) => {
   const [selectedWorkspaceId, setSelectedWorkspaceId] = React.useState<string>('');
   const [highlightModelUri, setHighlightModelUri] = React.useState<string>('');
   const [selectedModelUri, setSelectedModelUriRaw] = React.useState<string>('');
   const [selectedNodeClass, setSelectedNodeClass] = React.useState<string>('');
   const [navigateToNode, setNavigateToNode] = React.useState<NavigateToNode | null>(null);
   const [modelLibraryCategories, setModelLibraryCategories] = React.useState<string[]>(['private', 'shared']);
   const [selectedType, setSelectedType] = React.useState<SelectedType | null>(null);
   const [preferredDetailTab, setPreferredDetailTab] = React.useState<string>('attributes');

   // Setting selectedModelUri also updates highlightModelUri to keep them in sync
   const setSelectedModelUri = React.useCallback((uri: string) => {
      setSelectedModelUriRaw(uri);
      setHighlightModelUri(uri);
   }, []);

   const context = {
      selectedWorkspaceId,
      setSelectedWorkspaceId,
      highlightModelUri,
      setHighlightModelUri,
      selectedModelUri,
      setSelectedModelUri,
      selectedNodeClass,
      setSelectedNodeClass,
      navigateToNode,
      setNavigateToNode,
      modelLibraryCategories,
      setModelLibraryCategories,
      selectedType,
      setSelectedType,
      preferredDetailTab,
      setPreferredDetailTab,
   } as WorkspaceContextType;

   return (
      <WorkspaceContext.Provider value={context}>
         {children}
      </WorkspaceContext.Provider>
   );
};

export default WorkspaceProvider;
