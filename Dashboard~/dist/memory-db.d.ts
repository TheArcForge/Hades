export interface MemoryFileMeta {
    filename: string;
    validation_status: string;
    last_reviewed: string | null;
    last_validated: string | null;
    size: number;
}
export interface MemoryFileDetail extends MemoryFileMeta {
    content: string;
    body: string;
}
export interface ProposalMeta {
    id: string;
    target_file: string;
    created_at: string;
    rationale: string;
    status: string;
    content: string;
}
export interface InferredFileMeta {
    filename: string;
    analyzer: string;
    confidence: string;
    sample_size: string;
    first_observed: string | null;
    last_confirmed: string | null;
    promotion_status: string;
    conflicts_with: string | null;
    description: string;
}
export declare class MemoryDB {
    private memoryDir;
    constructor(memoryDir: string);
    listFiles(): MemoryFileMeta[];
    getFile(filename: string): MemoryFileDetail | null;
    listProposals(): ProposalMeta[];
    acceptProposal(id: string): boolean;
    rejectProposal(id: string): boolean;
    listInferredFiles(): InferredFileMeta[];
    getInferredFile(filename: string): (InferredFileMeta & {
        content: string;
        body: string;
    }) | null;
}
