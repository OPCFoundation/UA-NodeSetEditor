export const URN_PREFIX = 'urn:uuid:';

/** Extract the GUID from a urn:uuid: URI, or return the string as-is. */
export function urnToId(urn?: string): string {
   if (urn?.startsWith(URN_PREFIX)) return urn.substring(URN_PREFIX.length);
   return urn ?? '';
}

export function idToUrn(id: string): string {
   if (id.startsWith(URN_PREFIX)) return id;
   return URN_PREFIX + id;
}

export interface LocalizedText {
   locale?: string;
   text?: string;
}

export interface ApplicationDescription {
   applicationUri?: string;
   productUri?: string;
   applicationName?: LocalizedText;
   discoveryUrls?: string[];
   isDefault?: boolean;
}

export interface ServerConfiguration {
   endpointUrl?: string;
   transportProfileUri?: string;
   securityMode?: string;
   securityPolicyUri?: string;
}

export interface WorkspaceDescription extends ApplicationDescription {
   description?: LocalizedText;
   serverConfiguration?: ServerConfiguration;
   owner?: string;
   isOwner?: boolean;
   /** True if the current user may edit this workspace. Only the owner can
       write; shared (read-only) users and legacy no-owner workspaces are false. */
   canWrite?: boolean;
   acl?: string[];
   createdAt?: string;
   modifiedAt?: string;
}

export interface PaginatedResponse<T> {
   results: T[];
   totalCount?: number;
}
