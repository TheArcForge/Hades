import { readdirSync, existsSync } from 'fs';
import { join } from 'path';

export function discoverCsFiles(dirs) {
  const results = [];
  for (const dir of dirs) {
    if (!existsSync(dir)) continue;
    walkDir(dir, results);
  }
  return results;
}

function walkDir(dir, results) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      walkDir(fullPath, results);
    } else if (entry.isFile() && entry.name.endsWith('.cs')) {
      results.push(fullPath);
    }
  }
}
