---
description: "Use when building or modifying Unity scenes — creating GameObjects, setting up hierarchies, configuring components, and organizing scene structure. Guides efficient scene construction workflows."
---

# Scene Authoring

Procedural guide for building and modifying Unity scenes efficiently. Covers hierarchy conventions, programmatic scene setup via editor scripts, and how to use Hades graph queries to understand scene structure before making changes.

## When to Apply

Activate when the task involves:
- Creating a new scene from scratch or populating an empty scene
- Restructuring an existing scene hierarchy (grouping, renaming, reparenting)
- Adding and configuring components on GameObjects en masse
- Setting up lighting, cameras, or environment in a scene
- Writing an editor utility to automate repetitive scene setup
- Migrating or duplicating scene structure across multiple scenes

Do NOT activate for architecture decisions about what the scene should contain — use `hades:unity-architect` and `hades:scene-architecture` first, then return here to execute.

## Project Context Check

Before touching any scene:

1. **Understand the current scene state:**
   - Call `get_scene_summary("<scene_path>")` to retrieve the existing hierarchy, root GameObjects, component counts, and lighting setup — never assume what is already there
   - Call `get_project_summary()` to identify render pipeline (URP/HDRP/Built-in), Unity version, and project scale; render pipeline determines which components and shader variants are valid
   - Call `recall_memory("scene authoring conventions")` to surface documented naming rules, hierarchy grouping standards, or team agreements about scene organisation

2. **Adapt work based on findings:**
   - If `get_scene_summary` shows an existing structure → match its naming and grouping conventions exactly
   - If `get_project_summary` shows URP → use URP Volume and URP Light components; avoid legacy post-processing stack
   - If memory documents a convention (e.g. "all scenes must have a --- Managers --- group") → follow it; if the convention is missing from the new scene, add it

## Decision Framework

### Should I edit the scene file directly or use an editor script?

```
Is the change additive (adding new GameObjects / components)?
├── Yes, and it affects ≥ 3 GameObjects or is repeatable
│   └── Write an editor script — faster, repeatable, self-documenting
├── Yes, and it is a one-off tweak to 1–2 objects
│   └── Manual Editor workflow is fine
└── Is the change structural (reparenting, renaming, deleting)?
    ├── ≥ 5 objects affected → editor script with Undo.RegisterFullObjectHierarchyUndo
    └── 1–4 objects → manual Editor drag/drop
```

**Never edit `.unity` files as raw text.** Unity scene files are either binary or YAML with implicit object reference hashes. Hand-editing introduces orphaned file references, broken prefab links, and merge conflicts that are difficult to diagnose. Always make scene changes through the Editor or through `EditorSceneManager` + `GameObject` APIs in an editor script.

---

### Scene Hierarchy Organisation Conventions

Every scene in the project follows this top-level grouping pattern. Separator objects use the `--- GroupName ---` naming convention — they are empty GameObjects with no components, used only as hierarchy labels.

```
SceneName
├── --- Environment ---      ← static geometry, terrain, skybox volumes
├── --- Lighting ---         ← directional light, reflection probes, light probes, volumes
├── --- Cameras ---          ← main camera, cinemachine brain, virtual cameras
├── --- UI ---               ← canvas roots, event system
├── --- Gameplay ---         ← level-specific gameplay objects (enemies, pickups, triggers)
├── --- Managers ---         ← scene-scoped manager MonoBehaviours (not DontDestroyOnLoad)
└── --- Debug ---            ← editor-only helpers, gizmo objects; disabled in builds
```

Rules:
- Separator objects must have zero components (not even a Transform script).
- All runtime GameObjects must be a child of one of these groups — no root-level strays.
- Names use PascalCase for real objects, `--- Label ---` only for separators.
- If a group is empty in a given scene, keep the separator object anyway for consistency.

---

### When to Write an Editor Script vs Work Manually

| Situation | Recommended approach |
|-----------|----------------------|
| Populating a scene with 10+ similarly-configured objects | Editor script |
| One-off component value tweak | Manual inspector |
| Establishing the standard separator hierarchy in a new scene | Editor script (use `CreateSceneStructure`) |
| Batch-updating a component value across all instances of a prefab type | Editor script |
| Adjusting light colour interactively | Manual inspector |
| Linking serialised references between two specific objects | Manual inspector drag |

## Code Examples

### CreateSceneStructure — Editor Utility

Creates the standard separator hierarchy in the active scene. Run once per new scene via the `Tools` menu.

```csharp
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor utility: Tools > Hades > Create Scene Structure
/// Inserts the project-standard separator hierarchy into the active scene.
/// Safe to run on an existing scene — skips groups that already exist.
/// </summary>
public static class CreateSceneStructure
{
    private static readonly string[] GroupNames =
    {
        "--- Environment ---",
        "--- Lighting ---",
        "--- Cameras ---",
        "--- UI ---",
        "--- Gameplay ---",
        "--- Managers ---",
        "--- Debug ---",
    };

    [MenuItem("Tools/Hades/Create Scene Structure")]
    public static void Execute()
    {
        Scene active = SceneManager.GetActiveScene();
        if (!active.IsValid())
        {
            Debug.LogError("[CreateSceneStructure] No valid active scene.");
            return;
        }

        int created = 0;
        foreach (string groupName in GroupNames)
        {
            if (FindRootByName(active, groupName) != null)
            {
                Debug.Log($"[CreateSceneStructure] Skipped '{groupName}' — already exists.");
                continue;
            }

            GameObject separator = new GameObject(groupName);
            SceneManager.MoveGameObjectToScene(separator, active);
            Undo.RegisterCreatedObjectUndo(separator, $"Create {groupName}");
            created++;
        }

        if (created > 0)
            EditorSceneManager.MarkSceneDirty(active);

        Debug.Log($"[CreateSceneStructure] Done — created {created} group(s) in '{active.name}'.");
    }

    private static GameObject FindRootByName(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
        }
        return null;
    }
}
```

---

### BatchAddComponent — Programmatic Component Configuration

Adds a component to every matching child object in a hierarchy group, setting properties in a single pass. Useful for configuring a set of static geometry with the same settings.

```csharp
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Editor utility for batch-configuring static environment objects.
/// Adjust the target group name and configuration logic for your use case.
/// </summary>
public static class BatchConfigureEnvironment
{
    [MenuItem("Tools/Hades/Batch Configure Environment")]
    public static void Execute()
    {
        // Find the separator group by name in the active scene.
        GameObject envGroup = GameObject.Find("--- Environment ---");
        if (envGroup == null)
        {
            Debug.LogError("[BatchConfigure] '--- Environment ---' not found. Run Create Scene Structure first.");
            return;
        }

        int processed = 0;
        MeshRenderer[] renderers = envGroup.GetComponentsInChildren<MeshRenderer>(includeInactive: true);

        Undo.SetCurrentGroupName("Batch Configure Environment");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (MeshRenderer renderer in renderers)
        {
            Undo.RecordObject(renderer, "Configure MeshRenderer");
            // Mark all environment geometry as static contributor.
            renderer.staticShadowCaster = true;
            renderer.receiveGI = ReceiveGI.Lightmaps;
            processed++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        Debug.Log($"[BatchConfigure] Configured {processed} MeshRenderer(s).");
    }
}
```

---

### EditorSceneManager Usage — Programmatic Scene Operations

When an editor script needs to open, modify, and save a scene without user interaction (e.g. a batch pipeline):

```csharp
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Example: batch-process a list of scene paths, adding the standard structure to each.
/// Run via Tools menu or called from a CI/CD script with -batchmode -executeMethod.
/// </summary>
public static class BatchSceneProcessor
{
    // Scenes to process — populate from AssetDatabase query or hard-code for a pipeline.
    private static readonly string[] ScenePaths =
    {
        "Assets/Scenes/Level01.unity",
        "Assets/Scenes/Level02.unity",
        "Assets/Scenes/Level03.unity",
    };

    [MenuItem("Tools/Hades/Batch Add Scene Structure")]
    public static void Execute()
    {
        // Save and close the currently open scene first.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("[BatchScene] Cancelled by user.");
            return;
        }

        foreach (string path in ScenePaths)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogWarning($"[BatchScene] Could not open '{path}'.");
                continue;
            }

            // Delegate to the CreateSceneStructure utility.
            CreateSceneStructure.Execute();

            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[BatchScene] Processed '{path}'.");
        }

        Debug.Log("[BatchScene] All scenes processed.");
    }

    /// <summary>
    /// Query all scene paths in the project — useful for building the target list dynamically.
    /// </summary>
    public static string[] FindAllScenePaths()
    {
        string[] guids = AssetDatabase.FindAssets("t:Scene");
        string[] paths = new string[guids.Length];
        for (int i = 0; i < guids.Length; i++)
            paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
        return paths;
    }
}
```

---

### Scene Hierarchy Organisation in Code

When spawning scene-scoped managers or gameplay objects from code at runtime, always parent them under the correct separator group:

```csharp
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Runtime helper: finds a root separator group by name and parents a transform under it.
/// Call this from Awake when dynamically spawning scene-scoped objects.
/// </summary>
public static class SceneHierarchyHelper
{
    /// <summary>
    /// Parents <paramref name="target"/> under the named root group in the active scene.
    /// If the group does not exist, the object is left at scene root and a warning is logged.
    /// </summary>
    public static void ParentToGroup(Transform target, string groupName)
    {
        Scene active = SceneManager.GetActiveScene();
        foreach (GameObject root in active.GetRootGameObjects())
        {
            if (root.name == groupName)
            {
                target.SetParent(root.transform, worldPositionStays: true);
                return;
            }
        }
        Debug.LogWarning($"[SceneHierarchy] Group '{groupName}' not found — '{target.name}' placed at scene root.");
    }
}

// Usage in a manager Awake:
// SceneHierarchyHelper.ParentToGroup(transform, "--- Managers ---");
```

### Alternative: Direct MCP Tool Calls

If you prefer tool calls over scripting, these editor-action tools are available:
- `scene_get_hierarchy` — list all GameObjects as a tree
- `scene_create_gameobject` — create an empty GameObject with optional parent
- `scene_create_primitive` — create a primitive shape (Cube, Sphere, etc.)
- `scene_delete_gameobject` — delete a GameObject (supports undo)
- `scene_reparent_gameobject` — move a GameObject under a new parent
- `scene_rename_gameobject` — rename a GameObject
- `scene_setup` — batch-create multiple GameObjects with components and hierarchy
- `scene_save` / `scene_create` / `scene_open` — scene file management
- `inspector_select` / `inspector_inspect` — selection and property inspection

Choose tools for quick one-off operations. Choose C# scripting for reusable Editor tools, complex batch operations, or when the operation should be committed to the project as an Editor script.

## Anti-Examples

### Editing .unity Scene Files as Raw Text

```yaml
# BAD — do not open a .unity file in a text editor and change values by hand.
# Even in "Force Text" serialisation mode, object references use file-local IDs
# (fileID/guid pairs) that must be consistent across the entire file.
# Hand-editing breaks prefab links, missing script references, and
# corrupts component arrays silently.

# Symptom: "Missing Script" warnings in the Editor after a text edit.
# Fix: revert the file and make the change through the Editor or EditorSceneManager API.
```

Use `EditorSceneManager.OpenScene` + `GameObject` / `Component` APIs instead.

---

### Creating GameObjects One by One Without a Script

```csharp
// BAD — adding 20 environment props by calling Instantiate manually 20 times in an Update loop
// or via a sequence of console commands. No Undo support, no repeatability,
// and no documentation of intent.

// Prefer: write a BatchSpawn editor script that reads from a ScriptableObject or JSON config,
// wraps all operations in an Undo group, and logs what it created.
```

---

### Leaving Objects Outside Separator Groups

```
// BAD — scene root has a mix of separator groups and loose GameObjects:
Scene
├── --- Environment ---
├── --- Managers ---
├── SomeEnemy          ← stray object at root
├── TempLight          ← stray object at root
└── --- Gameplay ---

// This breaks visual scanning and makes grep-based scene diffs noisy.
// Move all non-separator root objects under the appropriate group.
```

## Cross-References

- Related architecture skills: `hades:scene-architecture`, `hades:unity-architect`
- After scene setup, wire prefabs: `hades:prefab-workflow`
- After prefab wiring, add animation: `hades:animation-workflow`
- Hades MCP tools used in this skill: `get_scene_summary`, `get_project_summary`, `recall_memory`, `propose_memory_update`, `scene_get_hierarchy`, `scene_create_gameobject`, `scene_create_primitive`, `scene_delete_gameobject`, `scene_reparent_gameobject`, `scene_rename_gameobject`, `scene_setup`, `scene_save`, `scene_create`, `scene_open`, `inspector_select`, `inspector_inspect`
- Unity docs: [EditorSceneManager](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SceneManagement.EditorSceneManager.html), [Undo](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Undo.html), [Multi-Scene Editing](https://docs.unity3d.com/6000.0/Documentation/Manual/MultiSceneEditing.html)
