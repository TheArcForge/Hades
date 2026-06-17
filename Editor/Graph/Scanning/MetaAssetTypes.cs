using System.Collections.Generic;
using System.IO;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    /// <summary>
    /// Extension → node-type map for meta-scanned (non-code, non-Unity-API) assets.
    /// MUST stay in sync with Scanner~/src/meta-scanner.js EXTENSION_TO_TYPE. A parity
    /// test (MetaAssetLifecycleTests) reads the JS file and asserts equality.
    /// </summary>
    public static class MetaAssetTypes
    {
        // Cheap sentinel identity — mirror of Scanner~/src/meta-constants.js.
        public const string SentinelHash = "meta";
        public const int ScannerVersion = 1;

        static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            { ".png", "Texture" }, { ".jpg", "Texture" }, { ".jpeg", "Texture" },
            { ".tga", "Texture" }, { ".psd", "Texture" }, { ".gif", "Texture" },
            { ".exr", "Texture" }, { ".hdr", "Texture" }, { ".bmp", "Texture" },
            { ".fbx", "Model" }, { ".obj", "Model" }, { ".blend", "Model" },
            { ".dae", "Model" }, { ".3ds", "Model" },
            { ".anim", "AnimationClip" },
            { ".controller", "AnimatorController" }, { ".overrideController", "AnimatorController" },
            { ".wav", "AudioClip" }, { ".mp3", "AudioClip" }, { ".ogg", "AudioClip" },
            { ".aif", "AudioClip" }, { ".aiff", "AudioClip" },
            { ".ttf", "Font" }, { ".otf", "Font" },
            { ".spriteatlas", "SpriteAtlas" }, { ".spriteatlasv2", "SpriteAtlas" },
            { ".renderTexture", "RenderTexture" },
            { ".cubemap", "Cubemap" },
            { ".mask", "AvatarMask" },
            { ".physicMaterial", "PhysicsMaterial" }, { ".physicsMaterial", "PhysicsMaterial" },
            { ".flare", "Flare" },
            { ".guiskin", "GUISkin" },
            { ".mixer", "AudioMixer" },
            { ".signal", "SignalAsset" },
            { ".playable", "PlayableAsset" },
        };

        public static bool IsMetaAsset(string assetPath) =>
            TryGetType(assetPath, out _);

        public static bool TryGetType(string assetPath, out string nodeType)
        {
            nodeType = null;
            if (string.IsNullOrEmpty(assetPath)) return false;
            var ext = Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(ext)) return false;
            return Map.TryGetValue(ext, out nodeType);
        }

        public static IReadOnlyDictionary<string, string> Entries => Map;
    }
}
