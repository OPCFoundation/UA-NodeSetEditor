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
