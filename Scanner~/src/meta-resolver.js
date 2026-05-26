import { readFileSync, existsSync } from 'fs';

const GUID_RE = /^guid:\s*([0-9a-f]{32})/m;

export function resolveGuid(csFilePath) {
  const metaPath = csFilePath + '.meta';
  if (!existsSync(metaPath)) return null;
  const content = readFileSync(metaPath, 'utf8');
  const match = content.match(GUID_RE);
  return match ? match[1] : null;
}
