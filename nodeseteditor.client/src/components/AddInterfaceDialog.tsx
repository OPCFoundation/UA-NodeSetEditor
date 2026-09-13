import * as React from 'react';
import { useTranslation } from 'react-i18next';

import api, { extractErrorMessage } from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import { extractNamespaceUri } from '../utils/formatNodeId';
import { NodePickerDialog } from './NodePickerDialog';

// BaseInterfaceType — root of the InterfaceTypes hierarchy (OPC UA Part 5).
// It is an ObjectType, so the picker walks the object-types HasSubtype tree.
// Stable module constant: passed as NodePickerDialog's rootType, whose identity
// feeds a reset effect — a fresh literal each render would thrash it.
const INTERFACE_ROOT_TYPE = { category: 'object-types', nodeId: 'i=17602' } as const;

interface AddInterfaceDialogProps {
   open: boolean;
   onClose: () => void;
   workspaceId: string;
   /** NodeId of the Object or ObjectType the interface is added to. */
   nodeId: string;
   onComplete: () => void;
}

/**
 * Picks an InterfaceType and adds it to the target node. Reuses the shared
 * NodePickerDialog (the same dialog the Add Reference flow uses to pick a
 * target), but with the tree fixed at the InterfaceType hierarchy
 * (BaseInterfaceType + its subtypes, following HasSubtype). On confirm the
 * server authors a HasInterface reference and materializes every member of
 * the interface as ordinary children in the node's model.
 */
export const AddInterfaceDialog: React.FC<AddInterfaceDialogProps> = ({
   open,
   onClose,
   workspaceId,
   nodeId,
   onComplete,
}) => {
   const { t } = useTranslation();
   const [isApplying, setIsApplying] = React.useState(false);
   const [applyError, setApplyError] = React.useState<string | null>(null);

   // Clear any prior error as the dialog closes, so reopening starts clean.
   const handleClose = () => {
      setApplyError(null);
      onClose();
   };

   const handlePick = async (interfaceTypeNodeId: string) => {
      setIsApplying(true);
      setApplyError(null);
      try {
         await api.post(
            `/opcua/v1/nodes/${slugifyNodeId(nodeId)}/add-interface`,
            {
               interfaceTypeNodeId,
               modelUri: extractNamespaceUri(nodeId),
            },
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         onComplete();
         onClose();
      } catch (e) {
         setApplyError(extractErrorMessage(e, 'Failed to add interface'));
      } finally {
         setIsApplying(false);
      }
   };

   return (
      <NodePickerDialog
         open={open}
         onClose={handleClose}
         workspaceId={workspaceId}
         title={t('typeDetail.addInterfaceTitle', 'Add Interface')}
         rootType={INTERFACE_ROOT_TYPE}
         confirmLabel={t('common.ok')}
         onPick={handlePick}
         isBusy={isApplying}
         pickError={applyError}
      />
   );
};
