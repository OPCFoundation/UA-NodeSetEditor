import type { LocalizedText, PaginatedResponse } from './WorkspaceDescription';

export interface Namespace {
   index: number;
   uri: string;
}

export type IdType = 'Numeric' | 'String' | 'Guid' | 'Opaque';

export interface NamespaceInfo extends Namespace {
   name?: string;
   version?: string;
   publicationDate?: string;
   description?: LocalizedText;
   isNamespaceSubset?: boolean;
   staticNodeIdTypes?: IdType[];
   staticNumericNodeIdRange?: string[];
   staticStringNodeIdPattern?: string;
}

export interface WorkspaceNamespaceInfo extends NamespaceInfo {
   id?: string;
   isPrivate?: boolean;
   /** True when the model is checked out for editing in this workspace. */
   isEditable?: boolean;
   isReadOnly?: boolean;
   /**
    * Where the content came from: 'CloudLibrary' | 'Upload' | 'Authored' | 'Unknown'.
    *
    * Distinguishes a Cloud Library model from one that is merely shared. They are both
    * read-only to a non-admin, but only the Cloud Library one can never be published — the
    * namespace is already published there — so they need different wording and icons.
    */
   origin?: string;
   /**
    * True when this workspace holds a Cloud Library copy of this namespace, so the namespace
    * is published there and check-in can never publish it from here.
    *
    * Keyed on the URI rather than this row's own origin: the private working copy of a Cloud
    * Library namespace reads as 'Authored', because checkout resets the provenance. Advisory —
    * the server's publish guard is the real check, and a namespace can be in the Cloud Library
    * with nothing cached locally.
    */
   isCloudLibraryNamespace?: boolean;
   requiredNamespaceUris?: string[];
   hasErrors?: boolean;
   errorMessage?: string;
   /** License identifier (SPDX id or custom "LicenseRef-…" id). Set once at genesis. */
   license?: string;
   /** Reference URL for the license (present for custom/"Other" licenses). */
   licenseUrl?: string;
   /** Copyright holder (e.g. "OPC Foundation, Inc."). Set once at genesis. */
   copyrightHolder?: string;
   /**
    * Profile group the NodeSet's conformance units are assessed against (a profile-group
    * fullName from profiles.opcfoundation.org); null/absent = none. Edited in the model
    * dialog, shown on the Conformance Units view.
    */
   profileGroupName?: string | null;
}

export type { PaginatedResponse };
