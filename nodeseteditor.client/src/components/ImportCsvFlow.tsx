import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';

import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Snackbar from '@mui/material/Snackbar';
import Alert from '@mui/material/Alert';

import api, { extractErrorMessage } from '../api/axios.api';
import { UserContext } from '../UserContext';
import { WorkspaceContext } from '../WorkspaceContext';
import { idToUrn, urnToId } from '../model/WorkspaceDescription';
import type { WorkspaceDescription, PaginatedResponse } from '../model/WorkspaceDescription';
import type { WorkspaceNamespaceInfo } from '../model/WorkspaceNamespaceInfo';

import LoginDialog from './LoginDialog';
import { ModelDialog } from './ModelDialog';
import { CreateModelDialog } from './CreateModelDialog';
import { ImportCsvTypeDialog } from './ImportCsvTypeDialog';
import type { CsvImportedTypeInfo } from './ImportCsvTypeDialog';

/**
 * Where the flow is. It runs strictly forward — each step hands off to the next, and
 * cancelling any of them returns to `idle` rather than skipping ahead.
 *
 *   idle ──▶ login        not signed in: sign in, then stop (the user starts again)
 *        ──▶ createModel  signed in with no editable model
 *        ──▶ chooseFile   prerequisites met; a prompt whose button opens the OS picker
 *        ──▶ wizard       a file is chosen
 *
 * `chooseFile` looks like an unnecessary click, and it is not: a browser only opens a file
 * picker during a user activation, and every route here reaches that point after an awaited
 * request — the workspace and model checks, or the create-model POST. Calling
 * <c>input.click()</c> then is silently ignored, which looked exactly like the button doing
 * nothing. The prompt gives the picker a real click of its own to open from.
 */
type FlowStep = 'idle' | 'login' | 'createModel' | 'chooseFile' | 'wizard';

export interface ImportCsvFlowHandle {
   /** Begins the flow. Safe to call repeatedly; ignored while a step is already showing. */
   start: () => void;
   /** True while the workspace/model checks are in flight, so a caller can disable its button. */
   busy: boolean;
}

interface ImportCsvFlowProps {
   children: (handle: ImportCsvFlowHandle) => React.ReactNode;
}

/**
 * The CSV import journey, from a button press to the mapping wizard.
 *
 * It exists because the wizard needs three preconditions the welcome page can't assume: a
 * signed-in user, a workspace (the welcome page has no sidebar, so none is selected yet), and
 * an editable model to create nodes in. Rather than have the wizard fail late on any of those,
 * this walks them in order and only opens the wizard once a file is in hand.
 *
 * Renders nothing itself — it hands a `start()` to its child and owns the dialogs.
 */
export const ImportCsvFlow: React.FC<ImportCsvFlowProps> = ({ children }) => {
   const { t } = useTranslation();
   const navigate = useNavigate();
   const queryClient = useQueryClient();
   const { isAuthenticated } = React.useContext(UserContext);
   const { selectedWorkspaceId, setSelectedWorkspaceId, setSelectedType, setNavigateToNode } =
      React.useContext(WorkspaceContext);

   const [step, setStep] = React.useState<FlowStep>('idle');
   const [busy, setBusy] = React.useState(false);
   const [workspaceId, setWorkspaceId] = React.useState('');
   const [file, setFile] = React.useState<File | null>(null);
   const [error, setError] = React.useState<string | null>(null);

   /**
    * The workspace to import into: whatever the app already has selected, else the server's
    * default. The welcome page has no sidebar, so nothing has selected one yet — and the
    * choice is pushed back into context so the rest of the app agrees once the user lands on
    * the new node.
    */
   const resolveWorkspaceId = async (): Promise<string> => {
      if (selectedWorkspaceId) return selectedWorkspaceId;

      const discovery = await queryClient.fetchQuery({
         queryKey: ['discovery'],
         queryFn: async () => {
            const response = await api.get<PaginatedResponse<WorkspaceDescription>>(
               '/opcua/v1/discovery', { params: { start: 0, count: 100 } });
            return response.data;
         },
      });

      const workspaces = discovery.results ?? [];
      if (workspaces.length === 0) return '';
      const chosen = workspaces.find(w => w.isDefault) ?? workspaces[0];
      const id = urnToId(chosen.applicationUri);
      setSelectedWorkspaceId(id);
      return id;
   };

   /** True when the workspace has a private, checked-out model that nodes can be written to. */
   const hasEditableModel = async (id: string): Promise<boolean> => {
      const namespaces = await queryClient.fetchQuery({
         queryKey: ['namespaces', id],
         queryFn: async () => {
            const response = await api.get<PaginatedResponse<WorkspaceNamespaceInfo>>(
               '/opcua/v1/namespaces/info', { headers: { 'OpcUa-Server': idToUrn(id) } });
            return response.data;
         },
      });

      return (namespaces.results ?? []).some(ns => ns.isPrivate && ns.isEditable && ns.uri);
   };

   /**
    * Opens the OS file dialog. The input is built here rather than rendered hidden and
    * clicked through a ref: `start` is handed to the child as a render prop, and a ref read
    * reachable from a value passed during render is exactly what the refs lint rule forbids.
    *
    * <b>Must be called straight from a click handler.</b> A browser only opens a file picker
    * while a user activation is live, and an <c>await</c> in between spends it — the call then
    * does nothing at all, with no error. Anything that needs to reach the picker after async
    * work goes through the `chooseFile` step instead.
    *
    * Nothing fires when the dialog is dismissed, which is the behaviour we want — the flow
    * simply stays where it was, so cancelling "Change file" keeps the current file rather
    * than dropping out of the wizard.
    */
   const openFilePicker = React.useCallback(() => {
      const input = document.createElement('input');
      input.type = 'file';
      input.accept = '.csv,.tsv,.txt';
      input.addEventListener('change', () => {
         const selected = input.files?.[0];
         if (!selected) return;
         setFile(selected);
         setStep('wizard');
      });
      input.click();
   }, []);

   const start = async () => {
      if (step !== 'idle' || busy) return;
      setError(null);

      // Not signed in: sign in and stop there. The user is back on the page they started
      // from and clicks again — deliberately not resumed, because the Microsoft path leaves
      // the app entirely and there would be nothing to resume from.
      if (!isAuthenticated) {
         setStep('login');
         return;
      }

      setBusy(true);
      try {
         const id = await resolveWorkspaceId();
         if (!id) {
            setError(t('csvImport.noWorkspace',
               'No workspace is available to import into. Create one from Manage Workspaces.'));
            return;
         }
         setWorkspaceId(id);

         // Both branches end in a dialog rather than the picker: the awaits above have already
         // spent this click's activation, so the picker has to be opened by a later one.
         setStep(await hasEditableModel(id) ? 'chooseFile' : 'createModel');
      } catch (e) {
         setError(extractErrorMessage(e, t('csvImport.startFailed', 'Could not start the import')));
      } finally {
         setBusy(false);
      }
   };

   const reset = () => {
      setStep('idle');
      setFile(null);
   };

   /** Land on the new node: its detail view, with the sidebar tree re-rooted on it. */
   const handleCreated = (created: CsvImportedTypeInfo) => {
      setSelectedType({
         nodeId: created.nodeId,
         displayName: created.displayName,
         nodeClass: created.nodeClass,
      });
      setNavigateToNode({
         nodeId: created.nodeId,
         superTypeIds: created.superTypeIds,
         nodeClass: created.nodeClass,
         displayName: created.displayName,
         enterFocusMode: true,
      });
      reset();

      // The target page treats the URL as the authority for which node is shown and clears
      // its selection when ?type= is absent, so the node has to travel in the query string —
      // setSelectedType alone would be undone on arrival. URLSearchParams handles the
      // escaping a NodeId ("nsu=…;i=42") needs.
      const search = new URLSearchParams({
         type: created.nodeId,
         name: created.displayName,
         nc: String(created.nodeClass),
      }).toString();
      navigate({ pathname: '/type_library', search });
   };

   return (
      <>
         {children({ start, busy })}

         <LoginDialog open={step === 'login'} onClose={reset} />

         {step === 'createModel' && workspaceId && (
            <CreateModelDialog
               open
               onClose={reset}
               workspaceId={workspaceId}
               // Only a model that actually got created moves the flow on. Carrying on after
               // a cancel would open a wizard with no model to write to, whose Create button
               // could never be enabled.
               onCreated={() => setStep('chooseFile')}
            />
         )}

         {step === 'chooseFile' && (
            <ModelDialog
               open
               onClose={reset}
               title={t('csvImport.title', 'Import CSV')}
               maxWidth="sm"
               actions={[{
                  label: t('csvImport.pickFile', 'Choose file...'),
                  onClick: openFilePicker,
               }]}
            >
               <Box sx={{ p: 20 }}>
                  <Typography variant="body2" color="text.secondary">
                     {t('csvImport.chooseFilePrompt',
                        'Choose the CSV file to import. The next step shows how its columns will '
                        + 'be mapped, before anything is created.')}
                  </Typography>
               </Box>
            </ModelDialog>
         )}

         {step === 'wizard' && workspaceId && file && (
            // Keyed on the file: a different one has different columns, and the wizard's
            // choices are overlays on that file's proposal, so it must start clean.
            <ImportCsvTypeDialog
               key={`${file.name}:${file.size}:${file.lastModified}`}
               open
               onClose={reset}
               workspaceId={workspaceId}
               file={file}
               onChangeFile={openFilePicker}
               onCreated={handleCreated}
            />
         )}

         <Snackbar
            open={!!error}
            autoHideDuration={8000}
            onClose={() => setError(null)}
            anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
         >
            <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>
         </Snackbar>
      </>
   );
};

export default ImportCsvFlow;
