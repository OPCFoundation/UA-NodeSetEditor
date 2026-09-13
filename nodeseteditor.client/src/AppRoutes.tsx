import * as React from 'react';

import WelcomeWizardPage from './pages/WelcomeWizardPage';
import ModelLibraryPage from './pages/ModelLibraryPage';
import TypeLibraryPage from './pages/TypeLibraryPage';
import HomeIcon from '@mui/icons-material/Home';
import WidgetsIcon from '@mui/icons-material/Widgets';
import EditDocumentIcon from '@mui/icons-material/EditDocument';
import { AddressSpaceTree } from './components/AddressSpaceTree';
import { RequireAuth } from './components/RequireAuth';

export interface PageLayout {
   path: string
   title: string
   main: React.ReactNode
   icon: React.ReactNode
   sidebar?: React.ReactNode
}

export const pages : PageLayout[] = [
   {
      path: "/",
      title: "welcomeWizard.shortName",
      main: <WelcomeWizardPage />,
      icon: <HomeIcon />
   },
   {
      path: "/type_library",
      title: "typeLibrary.shortName",
      main: <RequireAuth><TypeLibraryPage /></RequireAuth>,
      icon: <WidgetsIcon />,
      sidebar: <AddressSpaceTree />
   },
   {
      path: "/model_library",
      title: "modelLibrary.shortName",
      main: <RequireAuth><ModelLibraryPage /></RequireAuth>,
      icon: <EditDocumentIcon />,
      sidebar: <AddressSpaceTree />
   }
]
