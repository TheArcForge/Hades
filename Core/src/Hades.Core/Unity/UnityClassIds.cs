using System.Collections.Frozen;

namespace Hades.Core.Unity;

/// <summary>
/// Unity's class id → type name map. This is the one genuinely version-sensitive artifact in the
/// reader, and it is data rather than logic: new Unity versions add ids, they do not move
/// existing ones. Only 50 distinct ids appear across a real 612-asset project, so this covers the
/// ground comfortably; anything unrecognised degrades to a stable synthetic name.
/// </summary>
public static class UnityClassIds
{
    static readonly FrozenDictionary<int, string> Names = new Dictionary<int, string>
    {
        [1] = "GameObject", [2] = "Component", [3] = "LevelGameManager", [4] = "Transform",
        [8] = "Behaviour", [20] = "Camera", [21] = "Material", [23] = "MeshRenderer",
        [25] = "Renderer", [28] = "Texture2D", [29] = "OcclusionCullingSettings",
        [30] = "GraphicsSettings", [33] = "MeshFilter", [43] = "Mesh", [48] = "Shader",
        [54] = "Rigidbody", [64] = "MeshCollider", [65] = "BoxCollider", [81] = "AudioListener",
        [82] = "AudioSource", [91] = "AnimatorController", [95] = "Animator",
        [1101] = "AnimatorStateTransition", [1102] = "AnimatorState", [1107] = "AnimatorStateMachine",
        [104] = "RenderSettings", [108] = "Light", [114] = "MonoBehaviour", [115] = "MonoScript",
        [136] = "CapsuleCollider", [143] = "CharacterController", [147] = "ResourceManager",
        [157] = "LightmapSettings", [196] = "NavMeshSettings", [212] = "SpriteRenderer",
        [213] = "Sprite", [222] = "CanvasRenderer", [223] = "Canvas", [224] = "RectTransform",
        [225] = "CanvasGroup", [320] = "PlayableDirector", [329] = "VideoPlayer",
        [1001] = "PrefabInstance", [1002] = "EditorExtensionImpl", [1003] = "AssetImporter",
        [850595691] = "LightingSettings", [1660057539] = "SceneRoots",
    }.ToFrozenDictionary();

    public static bool IsKnown(int classId) => Names.ContainsKey(classId);

    /// <summary>The friendly name, or a stable synthetic one. Never throws — an unrecognised
    /// builtin should still produce an addressable node.</summary>
    public static string NameFor(int classId) =>
        Names.TryGetValue(classId, out var name) ? name : $"UnityType_{classId}";
}
