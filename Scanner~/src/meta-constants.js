// Shared identity constants for meta-scanned (non-code) assets. Mirrored in C#
// (Editor/Graph/Scanning/MetaAssetTypes.cs) — keep both in sync; a parity test
// guards the extension map but these scalars must be updated by hand together.

// Meta nodes derive nothing from binary CONTENT (only guid + path + extension),
// so their scanned_assets row uses this sentinel instead of an MD5 of the file.
// This is what stops the editor re-hashing multi-GB textures on every reload.
export const META_SENTINEL_HASH = 'meta';

// Bump when the meta node SHAPE changes (new type mapping, new properties) so a
// version mismatch in scanned_assets forces meta assets to be recreated.
export const META_SCANNER_VERSION = 1;
