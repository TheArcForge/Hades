import { parseArgs } from 'node:util';
import { Worker } from 'worker_threads';
import { cpus } from 'os';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import { discoverCsFiles } from './src/discovery.js';
import { parseFile } from './src/ts-parser.js';
import { resolveGuid } from './src/meta-resolver.js';
import { computeContentHash } from './src/hasher.js';
import { DbWriter } from './src/db-writer.js';
import { scanMetaFiles, getSupportedExtensions } from './src/meta-scanner.js';

// ─── Exit codes ──────────────────────────────────────────────────────────────
const EXIT_OK = 0;
const EXIT_ERROR = 1;
const EXIT_DB_LOCKED = 2;
const EXIT_DB_OPEN_FAILED = 3;

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const WORKER_THRESHOLD = 1000;

// ─── Parallel parse (worker threads) ────────────────────────────────────────

function runParallelParse(files) {
  const workerCount = Math.max(1, cpus().length - 1);
  const chunkSize = Math.ceil(files.length / workerCount);
  const chunks = [];
  for (let i = 0; i < files.length; i += chunkSize) {
    chunks.push(files.slice(i, i + chunkSize));
  }

  return new Promise((resolve, reject) => {
    const allResults = [];
    let completed = 0;

    for (const chunk of chunks) {
      const worker = new Worker(join(__dirname, 'src', 'worker.js'));
      worker.postMessage({ type: 'parse', files: chunk });
      worker.on('message', (msg) => {
        if (msg.type === 'results') {
          allResults.push(...msg.results);
          completed++;
          if (completed === chunks.length) resolve(allResults);
        }
      });
      worker.on('error', reject);
    }
  });
}

// ─── Core scan logic ─────────────────────────────────────────────────────────

/**
 * Scan C# files and write results to the graph database.
 *
 * @param {{ db: DbWriter, mode: 'full'|'incremental', dirs: string[], projectRoot: string,
 *            scannerVersion?: number, guids?: string[]|null, tier?: string }} opts
 * @returns {Promise<number>} exit code
 */
export async function scan({
  db,
  mode,
  dirs,
  projectRoot,
  scannerVersion = 3,
  guids = null,
  tier = 'project',
}) {
  try {
    db.setTier(tier);

    // Discover all .cs files to build guid→filePath map
    const allFiles = discoverCsFiles(dirs);

    // Build guid→filePath index
    const guidToFile = new Map();
    for (const filePath of allFiles) {
      const guid = resolveGuid(filePath);
      if (guid) {
        guidToFile.set(guid, filePath);
      }
    }

    if (mode === 'full') {
      _runMetaScan({ db, dirs });
      return await _runFullScan({ db, allFiles, guidToFile, scannerVersion, tier });
    } else if (mode === 'incremental') {
      return await _runIncrementalScan({ db, guids: guids ?? [], guidToFile, scannerVersion, tier });
    } else {
      process.stderr.write(`Unknown mode: ${mode}\n`);
      return EXIT_ERROR;
    }
  } catch (err) {
    process.stderr.write(`Scan error: ${err.message}\n`);
    return EXIT_ERROR;
  }
}

// ─── Full scan ───────────────────────────────────────────────────────────────

async function _runFullScan({ db, allFiles, guidToFile, scannerVersion, tier }) {
  const total = allFiles.length;
  let current = 0;

  // Collect all GUIDs to check cache
  const allGuids = [];
  const fileGuids = new Map(); // filePath → guid|null
  for (const filePath of allFiles) {
    const guid = resolveGuid(filePath);
    fileGuids.set(filePath, guid);
    if (guid) allGuids.push(guid);
  }

  // Fetch cached hashes for all GUIDs
  const cached = db.getScannedAssets(allGuids);

  // First pass: determine which files need scanning and emit progress
  const toScan = [];
  for (const filePath of allFiles) {
    current++;
    if (current % 50 === 0 || current === total) {
      process.stdout.write(`PROGRESS:${current}/${total}\n`);
    }

    const guid = fileGuids.get(filePath);
    if (!guid) continue; // no meta, skip

    const contentHash = computeContentHash(filePath);
    const existing = cached.get(guid);

    // Skip if hash and version match
    if (existing && existing.contentHash === contentHash && existing.scannerVersion === scannerVersion) {
      continue;
    }

    // Stale or new — needs re-scan
    toScan.push({ filePath, guid, contentHash });
  }

  // Second pass: scan using workers (large) or synchronous (small)
  if (toScan.length >= WORKER_THRESHOLD) {
    const workerResults = await runParallelParse(toScan.map(f => f.filePath));
    const resultByPath = new Map(workerResults.map(r => [r.filePath, r]));

    db.runInTransaction(() => {
      for (const item of toScan) {
        const wr = resultByPath.get(item.filePath);
        if (!wr || !wr.guid) continue;
        _writeParseResult({
          db,
          filePath: item.filePath,
          guid: wr.guid,
          contentHash: wr.hash,
          scannerVersion,
          parsed: wr.parsed,
        });
      }
    });
  } else {
    for (const item of toScan) {
      _scanFile({ db, filePath: item.filePath, guid: item.guid, contentHash: item.contentHash, scannerVersion });
    }
  }

  return EXIT_OK;
}

// ─── Incremental scan ────────────────────────────────────────────────────────

async function _runIncrementalScan({ db, guids, guidToFile, scannerVersion, tier }) {
  const total = guids.length;
  let current = 0;

  // Fetch cached hashes for the requested GUIDs
  const cached = db.getScannedAssets(guids);

  for (const guid of guids) {
    current++;
    if (current % 50 === 0 || current === total) {
      process.stdout.write(`PROGRESS:${current}/${total}\n`);
    }

    const filePath = guidToFile.get(guid);
    if (!filePath) continue; // file not found in dirs

    const contentHash = computeContentHash(filePath);
    const existing = cached.get(guid);

    // Skip if hash and version match
    if (existing && existing.contentHash === contentHash && existing.scannerVersion === scannerVersion) {
      continue;
    }

    // Re-scan this file
    _scanFile({ db, filePath, guid, contentHash, scannerVersion });
  }

  return EXIT_OK;
}

// ─── File scanning ───────────────────────────────────────────────────────────

function _writeParseResult({ db, filePath, guid, contentHash, scannerVersion, parsed }) {
  if (parsed.nodes.length === 0) return;

  // Delete old nodes + pending edges for this guid
  db.deleteNodesByGuid(guid);
  db.deletePendingEdgesBySourceAsset(guid);

  // Local parse id → DB row id mapping
  const idMap = new Map();

  // Write Script node first (id=0 in parsed)
  const scriptNode = parsed.nodes.find(n => n.type === 'Script');
  if (!scriptNode) return;

  const scriptDbId = db.insertNode({
    type: scriptNode.type,
    name: scriptNode.name,
    path: scriptNode.path,
    guid,
    properties: scriptNode.properties ? JSON.stringify(scriptNode.properties) : null,
    sourceRange: scriptNode.sourceRange ?? null,
  });
  idMap.set(scriptNode.id, scriptDbId);

  // Write remaining nodes (ScriptType, ScriptMethod, etc.)
  for (const node of parsed.nodes) {
    if (node.type === 'Script') continue; // already written

    const dbId = db.insertNode({
      type: node.type,
      name: node.name,
      path: node.path,
      guid: node.type === 'Script' ? guid : null,
      fileId: scriptDbId,
      properties: node.properties ? JSON.stringify(node.properties) : null,
      sourceRange: node.sourceRange ?? null,
    });
    idMap.set(node.id, dbId);

    // Write pending edges for ScriptType nodes
    if (node.type === 'ScriptType') {
      const props = node.properties ?? {};
      if (props.base_type) {
        db.insertPendingEdge(dbId, 'inherits_from', props.base_type, props.namespace ?? null, guid);
      }
      if (Array.isArray(props.interfaces)) {
        for (const iface of props.interfaces) {
          db.insertPendingEdge(dbId, 'implements', iface, props.namespace ?? null, guid);
        }
      }
    }
  }

  // Write code_references as pending edges for later resolution
  if (parsed.codeReferences) {
    for (const ref of parsed.codeReferences) {
      const sourceType = parsed.nodes.find(
        n => n.type === 'ScriptType' && n.name === ref.sourceTypeName
      );
      const sourceDbId = sourceType ? idMap.get(sourceType.id) : null;
      if (sourceDbId == null) continue;

      db.insertPendingEdge(
        sourceDbId,
        'code_references',
        ref.targetTypeName,
        ref.referenceKind, // stored in target_namespace column
        guid,
      );
    }
  }

  // Write edges (local ids → DB ids)
  for (const edge of parsed.edges) {
    const srcDbId = idMap.get(edge.sourceId);
    const tgtDbId = idMap.get(edge.targetId);
    if (srcDbId != null && tgtDbId != null) {
      db.insertEdge(srcDbId, tgtDbId, edge.type);
    }
  }

  // Record scanned asset
  db.recordScannedAsset(guid, contentHash, scannerVersion);
}

function _scanFile({ db, filePath, guid, contentHash, scannerVersion }) {
  const parsed = parseFile(filePath);
  db.runInTransaction(() => {
    _writeParseResult({ db, filePath, guid, contentHash, scannerVersion, parsed });
  });
}

// ─── Meta-scan for non-code assets ──────────────────────────────────────────

function _runMetaScan({ db, dirs }) {
  const extensions = [...getSupportedExtensions()];
  const assets = scanMetaFiles(dirs, extensions);
  if (assets.length > 0) {
    db.insertMetaAssets(assets);
  }
  process.stderr.write(`Meta-scan: ${assets.length} asset nodes created\n`);
  return assets.length;
}

// ─── CLI entry point ─────────────────────────────────────────────────────────

const isCLI = process.argv[1] && !process.env.JEST_WORKER_ID &&
  (process.argv[1].endsWith('index.js') || process.argv[1].endsWith('Scanner~/index.js'));

if (isCLI) {
  const { values } = parseArgs({
    args: process.argv.slice(2),
    options: {
      db: { type: 'string' },
      mode: { type: 'string' },
      dirs: { type: 'string' },
      'project-root': { type: 'string' },
      guids: { type: 'string' },
      'scanner-version': { type: 'string' },
      tier: { type: 'string' },
    },
  });

  const dbPath = values['db'];
  const mode = values['mode'] ?? 'full';
  const dirs = (values['dirs'] ?? '').split(',').filter(Boolean);
  const projectRoot = values['project-root'] ?? process.cwd();
  const guidsList = values['guids'] ? values['guids'].split(',').filter(Boolean) : null;
  const scannerVersion = values['scanner-version'] ? Number(values['scanner-version']) : 3;
  const tier = values['tier'] ?? 'project';

  let writer;
  try {
    writer = new DbWriter(dbPath);
  } catch (err) {
    if (err.message && err.message.includes('locked')) {
      process.stderr.write(`DB locked: ${err.message}\n`);
      process.exit(EXIT_DB_LOCKED);
    }
    process.stderr.write(`Failed to open DB: ${err.message}\n`);
    process.exit(EXIT_DB_OPEN_FAILED);
  }

  scan({
    db: writer,
    mode,
    dirs,
    projectRoot,
    scannerVersion,
    guids: guidsList,
    tier,
  }).then(code => {
    writer.close();
    process.exit(code);
  }).catch(err => {
    process.stderr.write(`Fatal: ${err.message}\n`);
    try { writer.close(); } catch (_) {}
    process.exit(EXIT_ERROR);
  });
}
