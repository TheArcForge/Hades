---
description: "Use when modeling game data — ScriptableObject patterns, runtime state management, serialization strategies, config vs instance data, and save/load architecture."
---

# Data Modeling

Guidance for structuring game data in Unity: choosing the right ScriptableObject pattern, separating config from runtime state, and designing a save system that survives versioning.

## When to Apply

Activate when the conversation involves:
- Deciding whether to use a ScriptableObject, a plain C# class, or MonoBehaviour fields to hold data
- Designing item stats, enemy configs, ability definitions, or other designer-facing data
- Building or extending a save/load system
- Choosing how to serialize data (JSON, binary, PlayerPrefs, SQLite)
- Managing a runtime registry of active entities (current enemies, items in world, etc.)
- Inspector annotation and editor-time data authoring conventions

Do NOT activate for pure component-structure questions (single MB vs. split) — those go to `hades:component-design`. Do NOT activate for prefab organization questions — those go to `hades:prefab-architecture`.

## Project Context Check

Before making recommendations:

1. **Check existing assets and types in the graph:**
   - Call `search_by_name("*.asset")` to discover existing ScriptableObject assets and understand naming conventions
   - Call `find_components_using_pattern("ScriptableObject")` to find SO-derived types already defined in the project
   - Call `get_project_summary()` to understand project scale — asset count and assembly structure inform whether a lightweight pattern or a formal save-system layer is appropriate

2. **Check team decisions in memory:**
   - Call `recall_memory("data modeling serialization save")` to find documented data and save conventions
   - Check validation status — if a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt recommendations based on findings:**
   - If the project already has a save system → extend it rather than introduce a parallel one
   - If the project has established SO naming conventions → follow them in new assets
   - After agreeing on a new pattern, call `propose_memory_update` to record the decision

## Decision Framework

### ScriptableObject Pattern Selection

```
What role does this data play?
│
├── Static config authored by designers, read at runtime, never written back
│   └── Data Container SO
│       - One asset = one config variant (ItemConfig, EnemyConfig, AbilityData)
│       - [CreateAssetMenu] lets designers create instances without code
│       - MonoBehaviour holds a [SerializeField] reference to the SO
│       - Never write to SO fields at runtime — it mutates the asset file on disk
│
├── Live registry of active runtime instances (enemies alive, items in world)
│   └── Runtime Set SO
│       - SO holds a List<T> that components register/unregister themselves into
│       - Register in OnEnable, unregister in OnDisable — handles scene loads cleanly
│       - Any system that needs "all current enemies" holds a reference to the SO
│       - Reset the list in OnDisable on the SO itself (or in a scene init call)
│
├── Broadcast communication between decoupled systems
│   └── Event Channel SO  (see hades:component-design for full implementation)
│       - Raised by sender; any listener SO receives the call
│       - Payload variants: GameEvent (void), GameEventInt, GameEventFloat, etc.
│
└── Named constants that need inspector drop-down ergonomics (like an enum, but extensible)
    └── SO Enum / Enum-Like Collection
        - Each value is a separate SO asset (DamageType_Fire.asset, DamageType_Ice.asset)
        - Reference by SO asset rather than int — serializes by GUID, not by order
        - New values added without recompile or enum renumbering
        - Best for: damage types, status effects, faction tags, audio categories
```

### Runtime State Management

```
Where should mutable instance state live?
│
├── State belongs to one specific GameObject instance
│   └── MonoBehaviour fields
│       - [SerializeField] private float _currentHealth;
│       - Owned and modified by the same component that uses it
│       - Appropriate for the vast majority of per-instance state
│
├── State is complex, involves business logic, or needs to be tested without Unity
│   └── Dedicated plain C# class (POCO / record)
│       - MonoBehaviour creates and owns the class instance
│       - All domain logic lives in the plain class; MB only wires it to Unity lifecycle
│       - Fully testable with EditMode tests (no scene required)
│       - Also the correct shape for save-data records
│
└── State is shared across all instances of a system and has a single owner
    └── Static field or singleton service (use sparingly)
        - Appropriate for game-wide singletons (AudioManager, SceneLoader)
        - Prefer an SO-based service (SO acts as a service locator) over a static class
        - Never use static for state that needs to reset between playtests in Editor
```

### Save System Architecture

**Structure first:**
```
SaveData (plain C# [Serializable] class)
├── version: int               — increment when the schema changes
├── playerData: PlayerSaveData
├── worldData: WorldSaveData
└── settingsData: SettingsSaveData
```

**Storage decision:**
```
How large and complex is the save data?
│
├── Small config-style (volume, key bindings, resolution)
│   └── PlayerPrefs — acceptable; never use for gameplay state
│
├── Moderate structured data (player stats, inventory, world flags)
│   └── JSON via JsonUtility or Newtonsoft.Json
│       - Application.persistentDataPath + filename
│       - Human-readable; easy to debug
│       - Add a version field from day one
│
└── Large relational data (quest logs, procedural worlds, analytics)
    └── SQLite via Hades bundled Mono.Data.Sqlite DLLs
        - Structured queries; handles large datasets efficiently
        - See project SQLite dependency notes in memory
```

**Versioning rule:** always include a `version` integer in the save root. On load, compare `SaveData.version` to the current expected version and migrate or warn if they differ.

### Inspector Exposure Rules

| Attribute | Use For |
|-----------|---------|
| `[SerializeField]` | Any field that should appear in the Inspector but remain private in code |
| `[Header("Section Name")]` | Group related fields visually with a bold label |
| `[Tooltip("...")]` | Document what a field controls; write for a designer, not a programmer |
| `[Range(min, max)]` | Numeric fields with known valid bounds (damage, speed, volume) |
| `[TextArea(minLines, maxLines)]` | Multi-line string fields (dialogue, descriptions) |
| `[HideInInspector]` | Public field that should not be editable in the Inspector |
| `[Space]` | Add vertical breathing room between groups |

Never expose fields with `public` just to see them in the Inspector — always use `[SerializeField] private`.

## Code Examples

### ScriptableObject Config Pattern (Full Implementation)

Designer-authored item stats. One asset per item type, no code changes to add new items.

```csharp
using UnityEngine;

/// <summary>
/// Static configuration for an item type. Create one asset per item.
/// Assets > Items > Item Config
/// </summary>
[CreateAssetMenu(menuName = "Items/Item Config", fileName = "New ItemConfig")]
public class ItemConfig : ScriptableObject
{
    [Header("Identity")]
    [SerializeField] private string _displayName;
    [SerializeField] [TextArea(2, 4)] private string _description;
    [SerializeField] private Sprite _icon;

    [Header("Stats")]
    [SerializeField] [Range(1, 999)] private int _maxStackSize = 1;
    [SerializeField] [Range(0f, 9999f)] private float _baseValue = 10f;
    [SerializeField] private ItemRarity _rarity = ItemRarity.Common;

    [Header("Behaviour")]
    [SerializeField] private bool _isConsumable;
    [SerializeField] private float _consumeHealthRestore;

    // Read-only public surface
    public string DisplayName     => _displayName;
    public string Description     => _description;
    public Sprite Icon            => _icon;
    public int    MaxStackSize    => _maxStackSize;
    public float  BaseValue       => _baseValue;
    public ItemRarity Rarity      => _rarity;
    public bool   IsConsumable    => _isConsumable;
    public float  ConsumeHealthRestore => _consumeHealthRestore;
}

public enum ItemRarity { Common, Uncommon, Rare, Epic, Legendary }
```

---

### SO Runtime Set (Full Implementation)

Tracks all currently active enemies without any scene coupling.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A runtime registry of active MonoBehaviours of type T.
/// Components register themselves in OnEnable and unregister in OnDisable,
/// so the list stays accurate across scene loads and pooled objects.
/// Create one asset per tracked type: Assets > Runtime Sets > Enemy Runtime Set
/// </summary>
[CreateAssetMenu(menuName = "Runtime Sets/Enemy Runtime Set", fileName = "EnemyRuntimeSet")]
public class EnemyRuntimeSet : ScriptableObject
{
    [System.NonSerialized]
    private readonly List<EnemyController> _items = new();

    public IReadOnlyList<EnemyController> Items => _items;
    public int Count => _items.Count;

    public void Register(EnemyController enemy)
    {
        if (!_items.Contains(enemy))
            _items.Add(enemy);
    }

    public void Unregister(EnemyController enemy) =>
        _items.Remove(enemy);

    /// <summary>Called from a scene init script to clear stale entries between playtests.</summary>
    public void Reset() => _items.Clear();
}

/// <summary>
/// Add to every enemy prefab. Wires registration automatically.
/// </summary>
public class EnemyController : MonoBehaviour
{
    [SerializeField] private EnemyRuntimeSet _runtimeSet;

    private void OnEnable()  => _runtimeSet.Register(this);
    private void OnDisable() => _runtimeSet.Unregister(this);
}

/// <summary>
/// Example consumer: a wave manager that checks how many enemies remain.
/// No scene coupling — it just holds a reference to the SO asset.
/// </summary>
public class WaveManager : MonoBehaviour
{
    [SerializeField] private EnemyRuntimeSet _enemies;

    private void Update()
    {
        if (_enemies.Count == 0)
            StartNextWave();
    }

    private void StartNextWave() { /* ... */ }
}
```

---

### SO Event Channel (Full Implementation)

Typed event channel for broadcasting a float payload — extend for any type.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Event channel that carries a float payload (e.g. health changed, score delta).
/// Create via Assets > Events > Float Event Channel.
/// </summary>
[CreateAssetMenu(menuName = "Events/Float Event Channel", fileName = "NewFloatEventChannel")]
public class FloatEventChannel : ScriptableObject
{
    private readonly List<FloatEventListener> _listeners = new();

    public void Raise(float value)
    {
        for (int i = _listeners.Count - 1; i >= 0; i--)
            _listeners[i].OnEventRaised(value);
    }

    public void Register(FloatEventListener listener)
    {
        if (!_listeners.Contains(listener))
            _listeners.Add(listener);
    }

    public void Unregister(FloatEventListener listener) =>
        _listeners.Remove(listener);
}

/// <summary>
/// Drop on any GameObject to respond to a FloatEventChannel asset.
/// </summary>
public class FloatEventListener : MonoBehaviour
{
    [SerializeField] private FloatEventChannel _channel;
    [SerializeField] private UnityEngine.Events.UnityEvent<float> _response;

    private void OnEnable()  => _channel.Register(this);
    private void OnDisable() => _channel.Unregister(this);

    public void OnEventRaised(float value) => _response?.Invoke(value);
}
```

---

### Simple JSON Save System (Full Implementation)

Minimal save/load with versioning using `JsonUtility` and `Application.persistentDataPath`.

```csharp
using System.IO;
using UnityEngine;

/// <summary>
/// Root save container. Increment CurrentVersion whenever the schema changes.
/// </summary>
[System.Serializable]
public class SaveData
{
    public const int CurrentVersion = 1;
    public int version = CurrentVersion;

    public PlayerSaveData player = new();
    public SettingsSaveData settings = new();
}

[System.Serializable]
public class PlayerSaveData
{
    public float health = 100f;
    public int   coins  = 0;
    public float posX, posY, posZ;
}

[System.Serializable]
public class SettingsSaveData
{
    public float masterVolume = 1f;
    public bool  subtitlesOn  = false;
}

/// <summary>
/// Static save manager. Call from any MonoBehaviour — no scene dependency.
/// </summary>
public static class SaveSystem
{
    private static readonly string SavePath =
        Path.Combine(Application.persistentDataPath, "save.json");

    public static void Save(SaveData data)
    {
        data.version = SaveData.CurrentVersion;
        string json = JsonUtility.ToJson(data, prettyPrint: true);
        File.WriteAllText(SavePath, json);
        Debug.Log($"[SaveSystem] Saved to {SavePath}");
    }

    public static SaveData Load()
    {
        if (!File.Exists(SavePath))
        {
            Debug.Log("[SaveSystem] No save file found — returning defaults.");
            return new SaveData();
        }

        string json = File.ReadAllText(SavePath);
        SaveData data = JsonUtility.FromJson<SaveData>(json);

        if (data.version != SaveData.CurrentVersion)
        {
            Debug.LogWarning($"[SaveSystem] Save version mismatch: " +
                             $"file={data.version}, current={SaveData.CurrentVersion}. " +
                             $"Migration may be required.");
            data = Migrate(data);
        }

        return data;
    }

    public static void Delete()
    {
        if (File.Exists(SavePath))
            File.Delete(SavePath);
    }

    /// <summary>Apply schema migrations here as versions increment.</summary>
    private static SaveData Migrate(SaveData old)
    {
        // Example: v0 → v1 added subtitlesOn; keep other fields, default new ones.
        if (old.version < 1)
        {
            old.settings.subtitlesOn = false;
            old.version = 1;
        }
        return old;
    }
}
```

Usage:
```csharp
// Save
var data = new SaveData();
data.player.health = playerHealth.Current;
data.player.coins  = inventory.Coins;
SaveSystem.Save(data);

// Load
SaveData loaded = SaveSystem.Load();
playerHealth.SetHealth(loaded.player.health);
```

---

### SO Enum Pattern (Full Implementation)

Named constants that are extensible without recompile and safe against enum reordering.

```csharp
using UnityEngine;

/// <summary>
/// A single damage type value. Create one asset per type:
///   DamageType_Physical.asset, DamageType_Fire.asset, etc.
/// Assets > Combat > Damage Type
/// References serialize by GUID, not by int — adding new types never breaks existing assets.
/// </summary>
[CreateAssetMenu(menuName = "Combat/Damage Type", fileName = "New DamageType")]
public class DamageType : ScriptableObject
{
    [SerializeField] [Tooltip("Display name shown in debug logs and UI.")]
    private string _displayName;

    [SerializeField] [Range(0f, 2f)]
    [Tooltip("Multiplier applied to all damage of this type (1 = normal, 0.5 = resistant, 2 = vulnerable).")]
    private float _defaultMultiplier = 1f;

    public string DisplayName        => _displayName;
    public float  DefaultMultiplier  => _defaultMultiplier;
}

/// <summary>
/// Example usage — a hit event carries a DamageType SO reference.
/// </summary>
[System.Serializable]
public struct HitInfo
{
    public float      Amount;
    public DamageType Type;      // drag asset in inspector; no enum cast needed
    public GameObject Source;
}

/// <summary>
/// An armor component that has per-type resistance overrides.
/// </summary>
public class Armor : MonoBehaviour
{
    [System.Serializable]
    private struct Resistance
    {
        public DamageType Type;
        [Range(0f, 2f)] public float Multiplier;
    }

    [SerializeField] private Resistance[] _resistances;

    public float GetMultiplier(DamageType type)
    {
        foreach (Resistance r in _resistances)
        {
            if (r.Type == type)
                return r.Multiplier;
        }
        return type != null ? type.DefaultMultiplier : 1f;
    }
}
```

## Anti-Examples

### Storing Mutable State on a SO Asset

```csharp
// BAD — writing to a SO field at runtime mutates the asset on disk in Editor
// and causes stale state between playtests.
[CreateAssetMenu]
public class PlayerStatsSO : ScriptableObject
{
    public float currentHealth = 100f; // written at runtime — DO NOT DO THIS
}

// GOOD — SO holds only config; runtime state lives in a MonoBehaviour
[CreateAssetMenu]
public class PlayerStatsSO : ScriptableObject
{
    [SerializeField] private float _maxHealth = 100f;
    public float MaxHealth => _maxHealth; // read-only
}

public class PlayerHealth : MonoBehaviour
{
    [SerializeField] private PlayerStatsSO _config;
    private float _currentHealth; // mutable state lives here, not on the SO
    private void Awake() => _currentHealth = _config.MaxHealth;
}
```

---

### Using PlayerPrefs for Complex Save Data

```csharp
// BAD — PlayerPrefs is a flat key-value store. Complex structures require
// manual key management, no versioning, and no migration path.
PlayerPrefs.SetFloat("health", health);
PlayerPrefs.SetInt("coins", coins);
PlayerPrefs.SetFloat("posX", pos.x);
// ... 50 more keys, all without version control

// GOOD — use a SaveData class + JSON to get structure and versioning.
var data = new SaveData();
data.player.health = health;
data.player.coins  = coins;
SaveSystem.Save(data);
```

---

### Serializing MonoBehaviours Directly

```csharp
// BAD — MonoBehaviours contain Unity-internal state that JsonUtility cannot
// serialize correctly. The output is incomplete and may throw on load.
string json = JsonUtility.ToJson(this); // 'this' is a MonoBehaviour — unreliable

// GOOD — extract the data you need into a plain [Serializable] class first.
[System.Serializable]
public class PlayerSaveData { public float health; public int coins; }

// In MonoBehaviour:
public PlayerSaveData ExtractSaveData() =>
    new PlayerSaveData { health = _health.Current, coins = _inventory.Coins };
```

## Cross-References

- Related skills: `hades:component-design`, `hades:unity-architect`, `hades:prefab-architecture`
- Hades MCP tools: `search_by_name`, `find_components_using_pattern`, `get_project_summary`, `recall_memory`, `propose_memory_update`
- Unity docs: [ScriptableObject](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ScriptableObject.html), [JsonUtility](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/JsonUtility.html), [Application.persistentDataPath](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-persistentDataPath.html), [CreateAssetMenu](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/CreateAssetMenuAttribute.html)
