export type ValidationStatus = 'Ready' | 'Queued' | 'Running' | 'Completed' | 'Failed';

export interface ValidationDocumentInfo {
   id: string;
   fileName: string;
   sizeBytes: number;
   uploadedUtc: string;
   uploadedByUserId?: string;
   status: ValidationStatus;
   jobId?: string;
   /** URI of the model the job was run against; shown under the file name. Cleared on reset. */
   modelUri?: string;
   errorCount?: number;
   warningCount?: number;
   infoCount?: number;
   /** Validator options (editable via the document's Edit icon). */
   profileGroupName?: string;
   verbose?: boolean;
   suppressedCodes?: string[];
}

/** A profile group from profiles.opcfoundation.org (options dropdown). */
export interface ProfileGroup {
   fullName: string;
   sort: number;
}

/** Payload for saving a document's validator options. */
export interface ValidationDocumentSettings {
   profileGroupName?: string | null;
   verbose: boolean;
   suppressedCodes: string[];
}

export interface DocumentUploadResult {
   uploadId: string;
   chunksReceived: number;
   totalChunks: number;
   isComplete: boolean;
   documentId?: string;
}

export interface ValidationEntry {
   section?: string;
   table?: string;
   severity?: string;
   code?: string;
   description?: string;
}

export interface ValidationResult {
   jobId: string;
   state: string;
   errorCount: number;
   warningCount: number;
   infoCount: number;
   summary?: string;
   exitCode?: number;
   completedUtc?: string;
   entries: ValidationEntry[];
}
