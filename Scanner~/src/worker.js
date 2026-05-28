import { parentPort } from 'worker_threads';
import { parseFile } from './ts-parser.js';
import { resolveGuid } from './meta-resolver.js';
import { computeContentHash } from './hasher.js';

parentPort.on('message', (msg) => {
  if (msg.type === 'parse') {
    const results = [];
    for (const filePath of msg.files) {
      const guid = resolveGuid(filePath);
      const hash = computeContentHash(filePath);
      const parsed = parseFile(filePath);
      results.push({ filePath, guid, hash, parsed });
    }
    parentPort.postMessage({ type: 'results', results });
  }
});
