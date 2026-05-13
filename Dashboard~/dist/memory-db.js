// Dashboard~/src/memory-db.ts
import { readFileSync, readdirSync, existsSync, writeFileSync, unlinkSync } from "fs";
import { join } from "path";
function parseFrontmatter(raw) {
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
    const fm = {};
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
    memoryDir;
    constructor(memoryDir) {
        this.memoryDir = memoryDir;
    }
    listFiles() {
        if (!existsSync(this.memoryDir))
            return [];
        const files = readdirSync(this.memoryDir).filter((f) => f.endsWith(".md") && !f.startsWith("."));
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
    getFile(filename) {
        const name = filename.endsWith(".md") ? filename : filename + ".md";
        const filePath = join(this.memoryDir, name);
        if (!existsSync(filePath))
            return null;
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
    listProposals() {
        const dir = join(this.memoryDir, "proposals");
        if (!existsSync(dir))
            return [];
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
    acceptProposal(id) {
        const proposalPath = join(this.memoryDir, "proposals", id + ".md");
        if (!existsSync(proposalPath))
            return false;
        const raw = readFileSync(proposalPath, "utf-8");
        const { frontmatter, body } = parseFrontmatter(raw);
        const targetFile = frontmatter.target_file;
        if (!targetFile)
            return false;
        const targetPath = join(this.memoryDir, targetFile + ".md");
        if (existsSync(targetPath)) {
            const existing = readFileSync(targetPath, "utf-8");
            writeFileSync(targetPath, existing.trimEnd() + "\n\n" + body);
        }
        else {
            writeFileSync(targetPath, body);
        }
        unlinkSync(proposalPath);
        return true;
    }
    rejectProposal(id) {
        const proposalPath = join(this.memoryDir, "proposals", id + ".md");
        if (!existsSync(proposalPath))
            return false;
        unlinkSync(proposalPath);
        return true;
    }
    listInferredFiles() {
        const dir = join(this.memoryDir, "inferred");
        if (!existsSync(dir))
            return [];
        const files = readdirSync(dir).filter((f) => f.endsWith(".md"));
        return files.map((f) => {
            const raw = readFileSync(join(dir, f), "utf-8");
            const { frontmatter, body } = parseFrontmatter(raw);
            // Extract description: lines between "INFERRED PATTERN..." header and "Observed in..." footer
            const lines = body.split("\n");
            const descLines = [];
            let pastHeader = false;
            for (const line of lines) {
                if (!pastHeader) {
                    if (line.trim().startsWith("INFERRED PATTERN")) {
                        pastHeader = true;
                    }
                    continue;
                }
                const trimmed = line.trim();
                if (trimmed.startsWith("Observed in ") && trimmed.includes("traces with"))
                    continue;
                if (trimmed)
                    descLines.push(trimmed);
            }
            return {
                filename: f,
                analyzer: frontmatter.analyzer || "unknown",
                confidence: frontmatter.confidence || "0",
                sample_size: frontmatter.sample_size || "0",
                first_observed: frontmatter.first_observed || null,
                last_confirmed: frontmatter.last_confirmed || null,
                promotion_status: frontmatter.promotion_status || "pending",
                conflicts_with: frontmatter.conflicts_with || null,
                description: descLines.join(" "),
            };
        });
    }
    getInferredFile(filename) {
        const name = filename.endsWith(".md") ? filename : filename + ".md";
        const filePath = join(this.memoryDir, "inferred", name);
        if (!existsSync(filePath))
            return null;
        const raw = readFileSync(filePath, "utf-8");
        const { frontmatter, body } = parseFrontmatter(raw);
        const meta = this.listInferredFiles().find((f) => f.filename === name);
        if (!meta)
            return null;
        return { ...meta, content: raw, body };
    }
}
//# sourceMappingURL=memory-db.js.map