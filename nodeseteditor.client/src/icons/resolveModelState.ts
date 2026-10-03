import iconMap from './icon-map.json';

/**
 * Where a model stands in its lifecycle, from the workspace's point of view.
 *
 * Four states rather than a single "read-only" flag, because they differ in what the user can
 * do next and in why: a locked private model is theirs and a checkout reopens it, a shared one
 * needs a checkout to get a private copy, and a Cloud Library one can be copied but never
 * published under that namespace.
 */
export type ModelState = 'privateEditable' | 'privateLocked' | 'shared' | 'cloudLibrary';

export interface ModelStateInput {
   /** Whether this workspace holds the model as its own private copy. */
   isPrivate?: boolean;
   /** Whether that private copy is checked out for editing. */
   isEditable?: boolean;
   /** Provenance from the API — 'CloudLibrary' marks the catalog copy. */
   origin?: string;
}

/**
 * Classifies a model.
 *
 * Ownership is decided first: a private copy is the workspace's own whatever its provenance
 * says, which matters because checking out a Cloud Library model produces a private copy whose
 * origin may still read as CloudLibrary on older rows. Only once a model is NOT private does
 * provenance distinguish the catalog copy from an ordinary shared one.
 */
export function resolveModelState(model: ModelStateInput): ModelState {
   if (model.isPrivate) {
      return model.isEditable ? 'privateEditable' : 'privateLocked';
   }
   return model.origin === 'CloudLibrary' ? 'cloudLibrary' : 'shared';
}

/** Iconify name for a model's lifecycle state. */
export function resolveModelStateIcon(model: ModelStateInput): string {
   return iconMap.modelState[resolveModelState(model)];
}
