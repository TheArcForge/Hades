// Dashboard~/src/memory-db.ts
import { readFileSync, readdirSync, existsSync, writeFileSync, unlinkSync } from "fs";
import { join, basename } from "path";

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

interface Frontmatter {
  [key: string]: string;
}

function parseFrontmatter(raw: string): { frontmatter: Frontmatter; body: string } {
  const lines = raw.split("\n");
  if (lines[0]?.trim() !== "---") {
    return { frontmatter: {}, body: raw };
  }

  let closingIndex = -1;
  for (let i = 1; i < lines.length; i++) {
    if (lines[i].trim() === "---") {
      closingIndex = i;
      break;
    }
  }

  if (closingIndex < 0) {
    return { frontmatter: {}, body: raw };
  }

  const fm: Frontmatter = {};
  for (let i = 1; i < closingIndex; i++) {
    const colonIdx = lines[i].indexOf(":");
    if (colonIdx > 0) {
      const key = lines[i].substring(0, colonIdx).trim();
      const value = lines[i].substring(colonIdx + 1).trim();
      fm[key] = value;
    }
  }

  const bodyLines = lines.slice(closingIndex + 1);
  return { frontmatter: fm, body: bodyLines.join("\n") };
}

export class MemoryDB {
  private memoryDir: string;

  constructor(memoryDir: string) {
    this.memoryDir = memoryDir;
  }

  listFiles(): MemoryFileMeta[] {
    if (!existsSync(this.memoryDir)) return [];

    const files = readdirSync(this.memoryDir).filter(
      (f) => f.endsWith(".md") && !f.startsWith(".")
    );

    return files.map((f) => {
      const raw = readFileSync(join(this.memoryDir, f), "utf-8");
      const { frontmatter } = parseFrontmatter(raw);
      return {
        filename: f,
        validation_status: frontmatter.validation_status || "ok",
        last_reviewed: frontmatter.last_reviewed || null,
        last_validated: frontmatter.last_validated_against_graph || null,
        size: raw.length,
      };
    });
  }

  getFile(filename: string): MemoryFileDetail | null {
    const name = filename.endsWith(".md") ? filename : filename + ".md";
    const filePath = join(this.memoryDir, name);
    if (!existsSync(filePath)) return null;

    const raw = readFileSync(filePath, "utf-8");
    const { frontmatter, body } = parseFrontmatter(raw);

    return {
      filename: name,
      validation_status: frontmatter.validation_status || "ok",
      last_reviewed: frontmatter.last_reviewed || null,
      last_validated: frontmatter.last_validated_against_graph || null,
      size: raw.length,
      content: raw,
      body,
    };
  }

  listProposals(): ProposalMeta[] {
    const dir = join(this.memoryDir, "proposals");
    if (!existsSync(dir)) return [];

    const files = readdirSync(dir).filter((f) => f.endsWith(".md"));
    return files.map((f) => {
      const raw = readFileSync(join(dir, f), "utf-8");
      const { frontmatter, body } = parseFrontmatter(raw);
      return {
        id: f.replace(".md", ""),
        target_file: frontmatter.target_file || "",
        created_at: frontmatter.created_at || "",
        rationale: frontmatter.rationale || "",
        status: frontmatter.status || "pending",
        content: body,
      };
    });
  }

  acceptProposal(id: string): boolean {
    const proposalPath = join(this.memoryDir, "proposals", id + ".md");
    if (!existsSync(proposalPath)) return false;

    const raw = readFileSync(proposalPath, "utf-8");
    const { frontmatter, body } = parseFrontmatter(raw);

    const targetFile = frontmatter.target_file;
    if (!targetFile) return false;

    const targetPath = join(this.memoryDir, targetFile + ".md");
    if (existsSync(targetPath)) {
      const existing = readFileSync(targetPath, "utf-8");
      writeFileSync(targetPath, existing.trimEnd() + "\n\n" + body);
    } else {
      writeFileSync(targetPath, body);
    }

    unlinkSync(proposalPath);
    return true;
  }

  rejectProposal(id: string): boolean {
    const proposalPath = join(this.memoryDir, "proposals", id + ".md");
    if (!existsSync(proposalPath)) return false;
    unlinkSync(proposalPath);
    return true;
  }
}
