import * as React from 'react';

export interface NavigateToNode {
   nodeId: string;
   superTypeIds: string[];
   nodeClass: number;
   /** Optional — needed by AddressSpaceTree to populate the focus-mode label. */
   displayName?: string;
   /** When true, AddressSpaceTree switches to focused mode with this node
       as the new root (used after a node is created). */
   enterFocusMode?: boolean;
}

export interface SelectedType {
   nodeId: string;
   displayName: string;
   nodeClass: number;
   tab?: string;
   /** URL from the NodeSet <Documentation> element, if present. */
   documentation?: string;
}

export type WorkspaceContextType = {
   selectedWorkspaceId: string,
   setSelectedWorkspaceId: (value: string) => void,
   highlightModelUri: string,
   setHighlightModelUri: (value: string) => void,
   selectedModelUri: string,
   setSelectedModelUri: (value: string) => void,
   selectedNodeClass: string,
   setSelectedNodeClass: (value: string) => void,
   navigateToNode: NavigateToNode | null,
   setNavigateToNode: (value: NavigateToNode | null) => void,
   modelLibraryCategories: string[],
   setModelLibraryCategories: (value: string[]) => void,
   selectedType: SelectedType | null,
   setSelectedType: (value: SelectedType | null) => void,
   /** Sticky node-detail tab: persists across node selections so reviewing
       e.g. property Values stays on the Value tab. Falls back to attributes
       per-node when the selected node's NodeClass doesn't support it. */
   preferredDetailTab: string,
   setPreferredDetailTab: (value: string) => void,
}

export const WorkspaceContext = React.createContext<WorkspaceContextType>({
   selectedWorkspaceId: '',
   setSelectedWorkspaceId: () => { },
   highlightModelUri: '',
   setHighlightModelUri: () => { },
   selectedModelUri: '',
   setSelectedModelUri: () => { },
   selectedNodeClass: '',
   setSelectedNodeClass: () => { },
   navigateToNode: null,
   setNavigateToNode: () => { },
   modelLibraryCategories: ['private', 'shared'],
   setModelLibraryCategories: () => { },
   selectedType: null,
   setSelectedType: () => { },
   preferredDetailTab: 'attributes',
   setPreferredDetailTab: () => { },
});
