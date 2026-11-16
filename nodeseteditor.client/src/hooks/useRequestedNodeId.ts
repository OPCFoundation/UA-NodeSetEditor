import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';

import api from '../api/axios.api';
import { slugifyNodeId } from '../api/slug';
import { idToUrn } from '../model/WorkspaceDescription';
import type { PaginatedResponse } from '../model/WorkspaceDescription';
import type { Node as RestNode } from '../model/Node';
import { validateNodeIdValue } from '../utils/nodeIdValidation';
import { buildDefaultStringIdentifier, hasStringNodeId } from '../utils/defaultStringNodeId';

/** Who picks the NodeId: the server (numeric) or the author (string). */
export type NodeIdMode = 'auto' | 'string';

export interface UseRequestedNodeIdOptions {
   open: boolean;
   workspaceId: string;
   /** The model the node will be created in — scopes the uniqueness check. */
   modelUri: string;
   /** The dialog's live BrowseName; the derived identifier follows it. */
   browseName: string;
   /**
    * The containment parent, when there is one. Its own string id — if it has
    * one — is what a child extends, and it is what preselects String mode.
    *
    * Type dialogs pass nothing: a supertype is not a containment parent, so a
    * new type's identifier starts from its BrowseName alone.
    */
   parentNodeId?: string;
}

export interface RequestedNodeIdState {
   mode: NodeIdMode;
   setMode: (mode: NodeIdMode) => void;
   identifier: string;
   setIdentifier: (identifier: string) => void;
   /** Send as `requestedNodeId` on create; undefined in auto mode. */
   requestedNodeId?: string;
   /** Format problem or clash, ready to render under the field. */
   error: string | null;
   /** True while the uniqueness check is still deciding. */
   isChecking: boolean;
   /** False when the dialog must not save. */
   canSave: boolean;
}

/**
 * Owns the create-time NodeId choice: auto-numeric (the server allocates, as it
 * always has) or a string identifier the author can edit.
 *
 * The identifier is only ever offered as a starting point. It tracks the
 * BrowseName until the author types in the field, and it is frozen the moment
 * the node exists — renaming a NodeId afterwards is not supported, because the
 * id is stored by value as the join key in rows, JSON blobs and the exported
 * NodeSet alike.
 */
export function useRequestedNodeId(options: UseRequestedNodeIdOptions): RequestedNodeIdState {
   const { open, workspaceId, modelUri, browseName, parentNodeId } = options;
   const { t } = useTranslation();

   const parentHasStringId = hasStringNodeId(parentNodeId);

   // Both the mode and the identifier are derived until the author overrides
   // them, which keeps the offered default live as the BrowseName is typed
   // without an effect syncing one piece of state into another.
   const [modeOverride, setModeOverride] = React.useState<NodeIdMode | null>(null);
   const [editedIdentifier, setEditedIdentifier] = React.useState<string | null>(null);

   // Clear the overrides whenever the dialog opens or closes, so reopening it
   // doesn't inherit the last session's choice. Adjusted during render — the
   // documented alternative to an effect, which would cost a cascading render.
   const [lastOpen, setLastOpen] = React.useState(open);
   if (lastOpen !== open) {
      setLastOpen(open);
      setModeOverride(null);
      setEditedIdentifier(null);
   }

   // A parent with a string id has set the convention for its subtree, so that
   // is the mode to open in. Anything else starts on the numeric default.
   const mode: NodeIdMode = modeOverride ?? (parentHasStringId ? 'string' : 'auto');
   const setMode = setModeOverride as (mode: NodeIdMode) => void;

   // Only needed to build a BrowseName path, which only happens when there is
   // no parent string id to extend — so the common case costs no request.
   const needsAncestors = open && !!parentNodeId && !parentHasStringId;
   const { data: ancestorBrowseNames } = useQuery({
      queryKey: ['nodePathBrowseNames', workspaceId, parentNodeId],
      queryFn: async () => {
         const response = await api.get<PaginatedResponse<RestNode>>(
            `/opcua/v1/nodes/${slugifyNodeId(parentNodeId!)}/path`,
            { headers: { 'OpcUa-Server': idToUrn(workspaceId) } },
         );
         // Root-most first, and the parent itself is the last entry.
         return (response.data.results ?? []).map(n => n.browseName ?? '');
      },
      enabled: needsAncestors,
      staleTime: 60 * 1000,
   });

   const defaultIdentifier = React.useMemo(
      () => buildDefaultStringIdentifier(browseName, parentNodeId, ancestorBrowseNames ?? []),
      [browseName, parentNodeId, ancestorBrowseNames],
   );

   // Follows the BrowseName until the author takes the field over by hand —
   // the same latch the dialogs use for DisplayName, derived rather than synced.
   const identifier = editedIdentifier ?? defaultIdentifier;

   const setIdentifier = React.useCallback((value: string) => {
      // The field renders "s=" as a fixed prefix, so an id pasted whole from an
      // existing nodeset ("s=NamespaceMetadata.ModelVersion") would otherwise end
      // up as "s=s=...". Pasting one is the likeliest way this field gets filled.
      setEditedIdentifier(value.startsWith('s=') ? value.substring(2) : value);
   }, []);

   const formatError = mode === 'string' && identifier
      ? validateNodeIdValue('s', identifier)
      : null;

   const candidate = mode === 'string' && identifier && !formatError
      ? `nsu=${modelUri};s=${identifier}`
      : null;

   // Debounce so a check doesn't fire on every keystroke of a name being typed.
   const [checkedCandidate, setCheckedCandidate] = React.useState<string | null>(null);
   React.useEffect(() => {
      const timer = setTimeout(() => setCheckedCandidate(candidate), 300);
      return () => clearTimeout(timer);
   }, [candidate]);

   // null = couldn't tell (the request failed for some other reason), which
   // never blocks: the server rejects a duplicate with 409 regardless.
   const { data: exists, isFetching } = useQuery<boolean | null>({
      queryKey: ['nodeExists', workspaceId, checkedCandidate],
      queryFn: async () => {
         try {
            await api.get(`/opcua/v1/nodes/${slugifyNodeId(checkedCandidate!)}`, {
               headers: { 'OpcUa-Server': idToUrn(workspaceId) },
            });
            return true;
         } catch (e) {
            if (axios.isAxiosError(e) && e.response?.status === 404) return false;
            return null;
         }
      },
      enabled: open && !!checkedCandidate,
      staleTime: 60 * 1000,
   });

   const isChecking = !!candidate && (checkedCandidate !== candidate || isFetching);
   const takenError = exists === true && !isChecking
      ? t('typeDetail.nodeIdTaken', 'This NodeId is already used in this model.')
      : null;
   const error = formatError ?? takenError;

   const canSave = mode === 'auto'
      || (identifier.trim() !== '' && !formatError && exists !== true);

   return {
      mode,
      setMode,
      identifier,
      setIdentifier,
      requestedNodeId: mode === 'string' && identifier ? `s=${identifier}` : undefined,
      error,
      isChecking,
      canSave,
   };
}
