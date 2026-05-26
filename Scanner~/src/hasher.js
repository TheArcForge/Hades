import { createHash } from 'crypto';
import { readFileSync, existsSync } from 'fs';

export function computeContentHash(filePath) {
  if (!existsSync(filePath)) return '';
  const data = readFileSync(filePath);
  return createHash('md5').update(data).digest('hex');
}
