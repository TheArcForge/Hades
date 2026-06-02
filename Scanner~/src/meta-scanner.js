import { readdirSync, readFileSync, existsSync } from 'fs';
import { join, extname, basename } from 'path';

const GUID_REGEX = /^guid:\s*([0-9a-f]{32})/m;

const EXTENSION_TO_TYPE = {
  '.png': 'Texture', '.jpg': 'Texture', '.jpeg': 'Texture',
  '.tga': 'Texture', '.psd': 'Texture', '.gif': 'Texture',
  '.exr': 'Texture', '.hdr': 'Texture', '.bmp': 'Texture',
  '.fbx': 'Model', '.obj': 'Model', '.blend': 'Model',
  '.dae': 'Model', '.3ds': 'Model',
  '.anim': 'AnimationClip',
  '.controller': 'AnimatorController', '.overrideController': 'AnimatorController',
  '.wav': 'AudioClip', '.mp3': 'AudioClip', '.ogg': 'AudioClip',
  '.aif': 'AudioClip', '.aiff': 'AudioClip',
  '.ttf': 'Font', '.otf': 'Font',
  '.spriteatlas': 'SpriteAtlas', '.spriteatlasv2': 'SpriteAtlas',
  '.renderTexture': 'RenderTexture',
  '.cubemap': 'Cubemap',
  '.mask': 'AvatarMask',
  '.physicMaterial': 'PhysicsMaterial', '.physicsMaterial': 'PhysicsMaterial',
  '.flare': 'Flare',
  '.guiskin': 'GUISkin',
  '.mixer': 'AudioMixer',
  '.signal': 'SignalAsset',
  '.playable': 'PlayableAsset',
};

/**
 * Returns the set of all asset extensions the MetaScanner handles.
 * @returns {Set<string>}
 */
export function getSupportedExtensions() {
  return new Set(Object.keys(EXTENSION_TO_TYPE));
}

/**
 * Walk directories and collect asset nodes from .meta files.
 *
 * @param {string[]} dirs — directories to walk recursively
 * @param {string[]} extensions — file extensions to include (e.g. ['.png', '.fbx'])
 * @returns {Array<{ guid: string, name: string, path: string, type: string }>}
 */
export function scanMetaFiles(dirs, extensions) {
  const extSet = new Set(extensions.map(e => e.toLowerCase()));
  const results = [];

  for (const dir of dirs) {
    if (!existsSync(dir)) continue;
    _walkDir(dir, extSet, results);
  }

  return results;
}

function _walkDir(dir, extSet, results) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }

  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      _walkDir(fullPath, extSet, results);
      continue;
    }

    // Skip non-meta files
    if (!entry.name.endsWith('.meta')) continue;

    // Derive the asset path (strip .meta suffix)
    const assetPath = fullPath.slice(0, -5);
    const assetExt = extname(assetPath).toLowerCase();
    if (!extSet.has(assetExt)) continue;

    const nodeType = EXTENSION_TO_TYPE[assetExt];
    if (!nodeType) continue;

    // Read .meta file for GUID
    let metaContent;
    try {
      metaContent = readFileSync(fullPath, 'utf8');
    } catch {
      continue;
    }

    const guidMatch = GUID_REGEX.exec(metaContent);
    if (!guidMatch) continue;

    results.push({
      guid: guidMatch[1],
      name: basename(assetPath, extname(assetPath)),
      path: assetPath,
      type: nodeType,
    });
  }
}
