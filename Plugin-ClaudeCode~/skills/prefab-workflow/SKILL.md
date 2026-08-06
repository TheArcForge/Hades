---
description: "Use when creating, editing, or instantiating prefabs — prefab creation workflows, editing in prefab mode vs in-scene, applying overrides, and nesting strategies."
---

# Prefab Workflow

Procedural guide for creating, editing, and managing Unity prefabs. Covers `PrefabUtility` API for programmatic prefab creation, prefab mode vs in-scene editing, variant strategies, and how to use Hades graph queries to discover existing prefabs before authoring new ones.

## When to Apply

Activate when the task involves:
- Creating a new prefab asset from a scene object or from scratch
- Deciding whether to create a new prefab vs a prefab variant vs modify an existing one
- Editing a prefab's structure, components, or serialised values
- Applying or reverting prefab instance overrides
- Nesting prefabs inside other prefabs
- Writing an editor script to batch-create or batch-configure prefab assets

Do NOT activate for deciding the overall prefab architecture (depth, variant strategy, data vs behaviour) — use `hades:unity-architect` and `hades:prefab-architecture` for that, then return here to execute.

## Project Context Check

Before creating or modifying any prefab:

1. **Discover existing prefabs:**
   - Call `find_prefabs_with_component("<ComponentName>")` to check whether a prefab with the required component already exists — avoid duplicating assets
   - Call `search_by_name("*.prefab")` for a full prefab inventory; narrow with `search_by_name("Enemy*.prefab")` or similar patterns when scope is known
   - Call `recall_memory("prefab workflow conventions")` to surface any documented team conventions about prefab folder structure, naming, or variant depth limits

2. **Adapt work based on findings:**
   - If a matching prefab exists → extend it (variant or composition) rather than creating a parallel asset
   - If memory documents a max nesting depth (e.g. "no more than 2 levels of prefab nesting") → enforce it; flag to the user if a request would violate it
   - If this is a new prefab category not yet in memory → after completing the work, call `propose_memory_update` to record the convention

## Decision Framework

### Create new prefab vs variant vs modify existing?

```
Does a prefab with the required base behaviour already exist?
├── No
│   └── Create a new base prefab from scratch
├── Yes — differs only in serialised data (stats, colours, sizes)?
│   └── Create a Prefab Variant — shares base prefab, overrides data only
├── Yes — differs in component set or hierarchy structure?
│   ├── Difference is minor (one extra component)?
│   │   └── Consider composition: add the component to the base prefab
│   │       and disable/enable it via code or config
│   └── Difference is structural (different child hierarchy)?
│       └── Create a new base prefab — variants cannot change hierarchy
└── Variant depth > 2 levels?
    └── Stop — flatten. Use a ScriptableObject config instead of nesting variants.
```

---

### Prefab editing: prefab mode vs in-scene instance?

```
What kind of change is being made?
├── Structural (add/remove child, add/remove component, rename child)
│   └── Always use Prefab Mode (double-click the .prefab asset)
│       Structural edits on instances create unresolvable overrides.
├── Data / serialised field values
│   ├── Change applies to ALL instances → Prefab Mode
│   └── Change is instance-specific → Inspector override on the instance
│       then decide: Apply to Prefab (if canonical) or leave as override (if unique)
└── You are unsure whether the change is structural or data
    └── Use Prefab Mode — it is always safe; instances update automatically.
```

---

### Nesting depth rules

| Nesting level | Rule |
|---------------|------|
| 0 — flat base prefab | Always allowed |
| 1 — prefab nested inside another | Allowed; keep clear ownership (parent owns child lifetime) |
| 2 — prefab nested two levels deep | Allowed but treat as a warning; consider flattening |
| 3+ levels deep | Do not do this. Propagation order becomes unpredictable; overrides compound. |

## Code Examples

### CreatePrefabFromTemplate — Editor Utility

Creates a new prefab asset from a template GameObject defined in the script. Safe to call from a menu item or a batch pipeline.

```csharp
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor utility for programmatically creating a prefab from a code-defined template.
/// Extend BuildTemplate() to match the GameObject structure your prefab requires.
/// </summary>
public static class CreatePrefabFromTemplate
{
    [MenuItem("Tools/Hades/Create Enemy Prefab Template")]
    public static void Execute()
    {
        string savePath = EditorUtility.SaveFilePanelInProject(
            title:       "Save Prefab",
            defaultName: "NewEnemy",
            extension:   "prefab",
            message:     "Choose location for the new prefab asset"
        );

        if (string.IsNullOrEmpty(savePath)) return;

        // Build the hierarchy in memory — not yet in any scene.
        GameObject root = BuildTemplate("NewEnemy");

        // SaveAsPrefabAsset writes the asset file. The root is NOT added to the scene.
        bool success;
        GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, savePath, out success);

        // Destroy the temporary in-memory object.
        Object.DestroyImmediate(root);

        if (success)
        {
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(prefabAsset);
            Debug.Log($"[CreatePrefab] Saved to '{savePath}'.");
        }
        else
        {
            Debug.LogError($"[CreatePrefab] Failed to save to '{savePath}'.");
        }
    }

    /// <summary>
    /// Builds the full GameObject hierarchy in memory.
    /// Add components and children here — this is where the template is defined.
    /// </summary>
    private static GameObject BuildTemplate(string name)
    {
        // Root object.
        GameObject root = new GameObject(name);

        // Required components.
        var health  = root.AddComponent<Health>();   // assumes Health.cs exists
        var movement = root.AddComponent<Movement>(); // assumes Movement.cs exists

        // Collider child.
        GameObject colliderObj = new GameObject("Collider");
        colliderObj.transform.SetParent(root.transform, worldPositionStays: false);
        var capsule = colliderObj.AddComponent<CapsuleCollider>();
        capsule.height = 2f;
        capsule.radius = 0.4f;

        // Visuals child (assign a default mesh in inspector after creation).
        GameObject visuals = new GameObject("Visuals");
        visuals.transform.SetParent(root.transform, worldPositionStays: false);
        visuals.AddComponent<MeshRenderer>();
        visuals.AddComponent<MeshFilter>();

        return root;
    }
}
```

---

### PrefabUtility.SaveAsPrefabAsset — Canonical Usage

```csharp
using UnityEditor;
using UnityEngine;

/// <summary>
/// Core pattern for saving a prefab asset from an in-memory or in-scene GameObject.
/// </summary>
public static class PrefabSavePatterns
{
    /// <summary>
    /// Pattern A: Save a new prefab from a temporary in-memory object.
    /// The sourceObject is NOT in any scene — built entirely in code.
    /// </summary>
    public static GameObject SaveNewPrefab(GameObject sourceObject, string assetPath)
    {
        // assetPath must start with "Assets/" and end with ".prefab".
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(sourceObject, assetPath, out bool success);
        if (!success)
            Debug.LogError($"[PrefabSave] Failed: {assetPath}");
        return prefab;
    }

    /// <summary>
    /// Pattern B: Save a new prefab from a scene instance AND connect the scene
    /// instance to the new prefab asset (it becomes a prefab instance).
    /// Use this when the user dragged objects into the scene before creating the prefab.
    /// </summary>
    public static GameObject SaveAndConnect(GameObject sceneInstance, string assetPath)
    {
        GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
            sceneInstance,
            assetPath,
            InteractionMode.UserAction, // records undo
            out bool success
        );
        if (!success)
            Debug.LogError($"[PrefabSave] Failed: {assetPath}");
        return prefab;
    }

    /// <summary>
    /// Pattern C: Apply all current overrides on a scene instance back to its prefab asset.
    /// Equivalent to "Apply All" in the inspector override dropdown.
    /// </summary>
    public static void ApplyAllOverrides(GameObject sceneInstance)
    {
        if (!PrefabUtility.IsPartOfPrefabInstance(sceneInstance))
        {
            Debug.LogWarning("[PrefabSave] Object is not a prefab instance — nothing to apply.");
            return;
        }

        string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sceneInstance);
        PrefabUtility.ApplyPrefabInstance(sceneInstance, InteractionMode.UserAction);
        Debug.Log($"[PrefabSave] Applied overrides to '{assetPath}'.");
    }
}
```

---

### Prefab Variant Creation

```csharp
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates a Prefab Variant of an existing base prefab.
/// The variant inherits all base properties and overrides only what is set explicitly.
/// </summary>
public static class CreatePrefabVariant
{
    [MenuItem("Tools/Hades/Create Prefab Variant Example")]
    public static void Execute()
    {
        // Load the base prefab.
        string basePath   = "Assets/Prefabs/Enemies/BaseEnemy.prefab";
        string variantPath = "Assets/Prefabs/Enemies/HeavyEnemy.prefab";

        GameObject basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
        if (basePrefab == null)
        {
            Debug.LogError($"[Variant] Base prefab not found at '{basePath}'.");
            return;
        }

        // Instantiate the base prefab into memory (not into the scene).
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);

        // Override data on the instance — these become the variant's overrides.
        if (instance.TryGetComponent<Health>(out Health health))
        {
            // SerializedObject is the correct way to set serialised fields from editor code.
            SerializedObject so = new SerializedObject(health);
            so.FindProperty("_maxHealth").floatValue = 300f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Save the instance as a variant. PrefabUtility detects the base prefab link
        // and writes a variant asset instead of a standalone prefab.
        GameObject variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath, out bool success);
        Object.DestroyImmediate(instance);

        if (success)
        {
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(variant);
            Debug.Log($"[Variant] Saved variant to '{variantPath}'.");
        }
        else
        {
            Debug.LogError($"[Variant] Failed to save variant to '{variantPath}'.");
        }
    }
}
```

---

### Querying and Batch-Modifying Existing Prefabs

```csharp
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-updates a serialised field on every prefab that has a specific component type.
/// Run from the Tools menu after changing a default value project-wide.
/// </summary>
public static class BatchUpdatePrefabField
{
    [MenuItem("Tools/Hades/Batch Update Health MaxHealth Default")]
    public static void Execute()
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab");
        int updated = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            // Only process prefabs that have the target component.
            if (!prefab.TryGetComponent<Health>(out Health health)) continue;

            // Use SerializedObject to make the edit — avoids dirty-tracking issues.
            SerializedObject so = new SerializedObject(health);
            SerializedProperty prop = so.FindProperty("_maxHealth");

            // Only update if still at the old default (100) — leave intentional overrides alone.
            if (!Mathf.Approximately(prop.floatValue, 100f)) continue;

            prop.floatValue = 150f;
            so.ApplyModifiedProperties();

            // Persist the change back to the prefab asset file.
            PrefabUtility.SavePrefabAsset(prefab);
            updated++;
            Debug.Log($"[BatchUpdate] Updated '{path}'.");
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[BatchUpdate] Done — updated {updated} prefab(s).");
    }
}
```

### Alternative: Direct MCP Tool Calls

If you prefer tool calls over scripting, these editor-action tools are available:
- `prefab_create` — create a prefab from a GameObject
- `prefab_instantiate` — instantiate a prefab in the scene
- `prefab_apply_overrides` — apply instance overrides back to the prefab
- `prefab_get_contents` — inspect prefab contents without instantiating
- `prefab_edit_property` — modify a property on a prefab asset
- `prefab_open_editing` / `prefab_save_editing` — enter/exit prefab editing mode
- `prefab_create_variant` — create a prefab variant
- `component_add` / `component_set_property` — modify components on prefab instances

Choose tools for quick one-off operations. Choose C# scripting (PrefabUtility API) for reusable Editor tools or complex batch operations.

## Anti-Examples

### Editing Prefab Instances in the Scene Without Applying

```csharp
// BAD — making structural changes (adding a child object) to a prefab instance in the scene
// and leaving the change as an override indefinitely.
//
// Override accumulation:
// - Overrides make the scene/prefab diff hard to read.
// - A future "Revert All" wipes the change silently.
// - Other team members see a different hierarchy when they open the prefab directly.
//
// Rule: if a structural change is intended for all instances, open the prefab in
// Prefab Mode and make it there. If it is truly instance-specific, document why
// in a comment component or a note in the Memory system.
```

---

### Deeply Nested Prefabs (More Than 2 Levels)

```
// BAD — three or more levels of prefab nesting:
//
// Vehicle.prefab
//   └── Wheel.prefab               (level 1 — OK)
//         └── BrakeCaliper.prefab  (level 2 — borderline)
//               └── Bolt.prefab   (level 3 — stop here)
//
// Problems:
// - A change to Bolt.prefab triggers a propagation chain through all three assets.
// - Override resolution order is not obvious at level 3.
// - Prefab Mode only shows one level at a time — diagnosing issues requires
//   opening three separate assets.
//
// Fix: flatten BrakeCaliper and Bolt into the Wheel prefab definition,
// or replace deep variant chains with a ScriptableObject config on a single prefab.
```

---

### Using Resources.Load to Instantiate Prefabs at Scale

```csharp
// BAD — loading prefabs at runtime via Resources.Load for frequently spawned objects.
//
// void SpawnEnemy()
// {
//     GameObject prefab = Resources.Load<GameObject>("Prefabs/Enemy");
//     Instantiate(prefab, spawnPos, Quaternion.identity);
// }
//
// Problems:
// - Resources.Load is synchronous — hitches on first load.
// - No pooling — Instantiate/Destroy at high frequency creates GC pressure.
// - Resources folder bypasses Addressables build pipeline.
//
// Prefer: inject a prefab reference via SerialiseField, combine with an ObjectPool,
// or use Addressables.InstantiateAsync for asset-catalog-managed prefabs.
```

## Cross-References

- Architecture decisions before authoring: `hades:prefab-architecture`, `hades:unity-architect`
- Scene placement after prefab creation: `hades:scene-authoring`
- Hades MCP tools used in this skill: `find_prefabs_with_component`, `search_by_name`, `recall_memory`, `propose_memory_update`, `prefab_create`, `prefab_instantiate`, `prefab_apply_overrides`, `prefab_get_contents`, `prefab_edit_property`, `prefab_open_editing`, `prefab_save_editing`, `prefab_create_variant`, `component_add`, `component_set_property`
- Unity docs: [PrefabUtility](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PrefabUtility.html), [Prefab Variants](https://docs.unity3d.com/6000.0/Documentation/Manual/PrefabVariants.html), [Prefab Mode](https://docs.unity3d.com/6000.0/Documentation/Manual/EditingInPrefabMode.html), [SerializedObject](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SerializedObject.html)
