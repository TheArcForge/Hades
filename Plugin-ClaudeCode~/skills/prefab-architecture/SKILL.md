---
description: "Use when designing prefab structure — base prefabs vs variants, nested prefabs, override strategies, prefab editing workflows, and when to use prefab variants vs ScriptableObject configs."
---

# Prefab Architecture

Provides decision frameworks and implementation patterns for structuring Unity prefabs — from simple base prefabs to variant hierarchies, nested prefab strategies, and ScriptableObject configs as a data-only alternative to deep variant chains.

## When to Apply

Activate when the conversation involves:
- Deciding whether to create a new base prefab, a prefab variant, or a ScriptableObject config
- Structuring nested prefabs — which child objects should be independent prefabs vs. baked in
- Managing prefab overrides: what to override, how many, and where to document them
- Choosing between a prefab variant family and a single prefab + multiple config SO assets
- Prefab editing workflows: when to open in Prefab Mode vs. edit in scene context
- Preventing override conflicts in deep variant chains
- Auditing orphaned or stale prefab references in the project

Do NOT activate for the physical act of placing and arranging objects inside a scene — that goes to `hades:scene-authoring`. Do NOT activate for animation state machine authoring — that goes to `hades:animation-workflow`.

## Project Context Check

Before making recommendations, gather current project state:

1. **Check existing prefab patterns:**
   - Call `find_prefabs_with_component("<relevant_component>")` to discover how similar objects are currently structured — e.g. `find_prefabs_with_component("EnemyController")` before designing a new enemy variant
   - Call `search_by_name("*.prefab")` for a broad inventory of prefab assets; look for naming conventions (Base_, Variant_, NPC_, etc.)
   - Call `trace_dependencies("<PrefabName>")` on a candidate base prefab to see what scenes and other prefabs reference it — changes to a heavily-referenced base have wide blast radius

2. **Check team decisions in memory:**
   - Call `recall_memory("prefab architecture variant")` to find documented prefab strategy
   - Call `recall_memory("scriptable object config pattern")` to find whether SO configs are already in use
   - If a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt based on findings:**
   - If graph shows an established `Base_` / `Variant_` naming pattern → align with it
   - If graph shows flat prefabs with no variants → confirm before introducing a variant hierarchy
   - If memory documents a prefab strategy → follow it; propose updates only when strongly justified

## Decision Framework

### Step 1 — Four Characterisation Questions

Answer these before choosing a pattern.

| # | Question | Why It Matters |
|---|----------|----------------|
| 1 | **Variation type** — do instances differ in data, components, or both? | Data only → SO config. Components differ → separate prefabs or variants |
| 2 | **Variation count** — how many distinct variants exist now, and will exist in 6 months? | < 10 → prefab variants manageable. ≥ 10 → SO config scales better |
| 3 | **Child reuse** — are child objects shared across multiple parent prefabs? | Yes → extract to independent nested prefab. No → keep baked in |
| 4 | **Override depth** — how many levels of variant-from-variant already exist? | 0–1 → variants are fine. 2+ → stop and switch to SO config or composition |

---

### DECISION: Prefab vs Variant vs SO Config

```
Do instances of this object differ from each other?
├── No — identical copies
│   └── BASE PREFAB — single .prefab asset, instantiate directly
│       Use for: obstacles, VFX emitters, UI widgets with no customisation
│
├── Yes — differ in data only (stats, audio clip, material, colour)
│   ├── < 10 variants AND team prefers inspector-visible differences
│   │   └── PREFAB VARIANTS — one base, N variant assets
│   │       Each variant asset stores only its overrides
│   │       Keep overrides < 5 per variant; document WHY each override exists
│   │
│   └── ≥ 10 variants OR data is tabular / lives in a spreadsheet
│       └── SO CONFIG PATTERN — one prefab, N ScriptableObject config assets
│           Prefab reads config at runtime; designers author config in inspector
│           Scales to hundreds of variants with zero new prefab assets
│
└── Yes — differ in components or child hierarchy
    ├── Variants differ by ≤ 1 level (base → child variant)
    │   └── PREFAB VARIANTS acceptable — limited override scope
    │
    └── Variants differ by components at multiple levels
        └── COMPONENT COMPOSITION — shared base prefab, optional components added
            Avoids overrides entirely; behaviour selected at instantiation or design time
```

---

### DECISION: Nested Prefab Strategy

```
Should a child object be extracted to its own prefab?
├── Is it reused in more than one parent prefab or scene?
│   └── YES — extract to independent nested prefab
│       Changes propagate automatically to all parents
│       Example: HealthBar UI used by Player, Enemy, Boss
│
├── Is it an independently configurable sub-system?
│   └── YES — extract (even if used only in one parent now)
│       Example: WeaponSocket that will be swapped at runtime
│
└── Is it purely internal visual structure (pivot, offset container, etc.)?
    └── NO — keep baked in; extracting creates needless nesting overhead

Depth limit: never create a chain deeper than Base → Nested (2 levels).
Base → Nested → Sub-Nested = 3 levels → override propagation becomes unpredictable.
```

---

### DECISION: Override Strategy

When you do use prefab variants, keep overrides minimal and intentional.

```
Before applying an override, ask:
├── Can the difference be expressed as a data field on a SO config instead?
│   └── YES → use SO config, avoid the override entirely
│
├── Is this override specific to THIS variant and no other?
│   └── YES → apply and document in a comment on the prefab or in memory
│
└── Does this override duplicate a change already on the base?
    └── YES → do not override; update the base instead
```

Rules:
- Apply overrides immediately after deciding — do not leave "pending override" notes
- Keep override count per variant below 5; variants with more than 5 overrides are a signal to revisit the base prefab
- Never override the Transform root position — let the instantiating code handle placement
- Never override script component type — that is a structural change requiring a separate prefab

---

### When to Stop Using Variants

Stop using prefab variants and switch to SO config + single prefab when any of the following are true:
- Variant count exceeds 10 and is still growing
- The same override (e.g. a material) appears on every variant — it should be on the base
- A variant-of-a-variant chain already exists — flatten before adding more
- Designers need to create new entries without opening Unity (SO assets can be authored in JSON or Google Sheets and imported)

## Code Examples

### ScriptableObject Config Pattern — Single Prefab, Many Configs

Full implementation — one `EnemyController` prefab consumes an `EnemyConfig` SO. Adding a new enemy type means creating a new SO asset, not a new prefab.

```csharp
using UnityEngine;

/// <summary>
/// Defines all data-driven properties for an enemy type.
/// Create assets: Assets > Enemies > Enemy Config
/// Assign the asset to an EnemyController prefab's config field before instantiation,
/// or let EnemySpawner inject it via SetConfig at runtime.
/// </summary>
[CreateAssetMenu(menuName = "Enemies/Enemy Config", fileName = "NewEnemyConfig")]
public class EnemyConfig : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Display name shown in UI and debug logs.")]
    public string displayName = "Enemy";

    [Header("Combat")]
    [Tooltip("Maximum hit points.")]
    public float maxHealth = 100f;

    [Tooltip("Base damage per attack.")]
    public float attackDamage = 15f;

    [Tooltip("Seconds between attacks.")]
    public float attackCooldown = 1.2f;

    [Tooltip("World-space attack range.")]
    public float attackRange = 1.5f;

    [Header("Movement")]
    [Tooltip("Movement speed in units per second.")]
    public float moveSpeed = 3.5f;

    [Tooltip("Distance at which the enemy detects the player.")]
    public float detectionRadius = 8f;

    [Header("Visuals")]
    [Tooltip("Material applied to the enemy renderer on Awake.")]
    public Material bodyMaterial;

    [Header("Rewards")]
    [Tooltip("Experience awarded on death.")]
    public int experienceReward = 10;

    [Tooltip("Gold dropped on death.")]
    public int goldReward = 5;
}

/// <summary>
/// Reads from an EnemyConfig SO to initialise all stats at runtime.
/// Attach to the single shared enemy prefab.
/// Config is injected by EnemySpawner; do not hard-code a config reference in the prefab.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class EnemyController : MonoBehaviour
{
    // Not serialized — always injected at spawn time.
    private EnemyConfig _config;

    // Runtime state
    private float _currentHealth;
    private float _nextAttackTime;
    private Transform _target;
    private Renderer  _renderer;

    public bool IsAlive => _currentHealth > 0f;
    public EnemyConfig Config => _config;

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
    }

    /// <summary>
    /// Call immediately after Instantiate to wire up the config.
    /// Initialises all stats and applies visual overrides.
    /// </summary>
    public void SetConfig(EnemyConfig config)
    {
        _config = config != null ? config
            : throw new System.ArgumentNullException(nameof(config));

        _currentHealth = config.maxHealth;

        if (config.bodyMaterial != null)
            _renderer.material = config.bodyMaterial;
    }

    public void SetTarget(Transform target) => _target = target;

    public void TakeDamage(float amount)
    {
        if (!IsAlive) return;
        _currentHealth = Mathf.Max(0f, _currentHealth - amount);
        if (!IsAlive) OnDied();
    }

    private void Update()
    {
        if (!IsAlive || _target == null || _config == null) return;

        float dist = Vector3.Distance(transform.position, _target.position);

        if (dist > _config.detectionRadius) return;

        // Move toward target.
        if (dist > _config.attackRange)
        {
            Vector3 dir = (_target.position - transform.position).normalized;
            transform.position += dir * (_config.moveSpeed * Time.deltaTime);
        }
        else
        {
            TryAttack();
        }
    }

    private void TryAttack()
    {
        if (Time.time < _nextAttackTime) return;
        _nextAttackTime = Time.time + _config.attackCooldown;

        if (_target.TryGetComponent<Health>(out Health hp))
            hp.TakeDamage(_config.attackDamage);
    }

    private void OnDied()
    {
        // Raise event, award XP/gold, trigger VFX, return to pool, etc.
        Debug.Log($"[Enemy] {_config.displayName} died. " +
                  $"Reward: {_config.experienceReward} XP, {_config.goldReward} gold.");
        gameObject.SetActive(false);
    }
}
```

---

### Prefab Spawner with SO Config Injection

Full implementation — spawner reads a list of spawn entries (prefab + config pairs) and injects config at spawn time.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Describes one type of spawnable enemy: the shared prefab and the config that defines it.
/// </summary>
[System.Serializable]
public class EnemySpawnEntry
{
    [Tooltip("Shared enemy prefab. Must have an EnemyController component.")]
    public EnemyController prefab;

    [Tooltip("Config SO that differentiates this enemy type.")]
    public EnemyConfig config;

    [Tooltip("Relative spawn weight (higher = more common).")]
    [Min(1)]
    public int weight = 1;
}

/// <summary>
/// Spawns enemies from a weighted list of SpawnEntries.
/// Place on a SpawnPoint GameObject; call Spawn() from a wave controller or trigger.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [SerializeField] private List<EnemySpawnEntry> _entries = new();
    [SerializeField] private Transform _playerTransform;
    [SerializeField] private int _maxAlive = 5;

    private readonly List<EnemyController> _aliveEnemies = new();
    private int _totalWeight;

    private void Awake()
    {
        foreach (EnemySpawnEntry entry in _entries)
            _totalWeight += entry.weight;
    }

    /// <summary>
    /// Spawn one enemy at this spawner's position.
    /// Returns the spawned controller, or null if the alive cap is reached.
    /// </summary>
    public EnemyController Spawn()
    {
        // Prune dead references.
        _aliveEnemies.RemoveAll(e => e == null || !e.IsAlive);

        if (_aliveEnemies.Count >= _maxAlive)
        {
            Debug.LogWarning($"[EnemySpawner] Alive cap ({_maxAlive}) reached.");
            return null;
        }

        EnemySpawnEntry entry = PickWeightedRandom();
        if (entry == null) return null;

        EnemyController instance = Instantiate(entry.prefab, transform.position, transform.rotation);
        instance.SetConfig(entry.config);
        instance.SetTarget(_playerTransform);

        _aliveEnemies.Add(instance);
        return instance;
    }

    private EnemySpawnEntry PickWeightedRandom()
    {
        if (_entries.Count == 0) return null;

        int roll = Random.Range(0, _totalWeight);
        int cumulative = 0;

        foreach (EnemySpawnEntry entry in _entries)
        {
            cumulative += entry.weight;
            if (roll < cumulative) return entry;
        }

        return _entries[_entries.Count - 1];  // fallback: last entry
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Gizmos.DrawSphere(transform.position, 0.4f);
    }
#endif
}
```

---

### Factory Pattern with Config Injection

Full implementation — a factory centralises creation logic and enforces config injection, making the spawning contract explicit.

```csharp
using UnityEngine;

/// <summary>
/// Central factory for instantiating prefabs that require a config SO.
/// Use instead of calling Instantiate directly when construction requires more than position/rotation.
/// </summary>
public class EnemyFactory : MonoBehaviour
{
    [Tooltip("The single shared enemy prefab. Never change at runtime.")]
    [SerializeField] private EnemyController _enemyPrefab;

    [Tooltip("Optional parent for spawned enemies. Keeps hierarchy clean.")]
    [SerializeField] private Transform _spawnContainer;

    private static EnemyFactory _instance;

    public static EnemyFactory Instance
    {
        get
        {
            if (_instance == null)
                Debug.LogError("[EnemyFactory] No instance found in scene.");
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
    }

    /// <summary>
    /// Create an enemy at <paramref name="position"/> configured by <paramref name="config"/>.
    /// Returns null if config is null or prefab is missing.
    /// </summary>
    public EnemyController Create(EnemyConfig config, Vector3 position, Quaternion rotation)
    {
        if (config == null)
        {
            Debug.LogError("[EnemyFactory] Cannot create enemy with null config.");
            return null;
        }
        if (_enemyPrefab == null)
        {
            Debug.LogError("[EnemyFactory] Enemy prefab is not assigned.");
            return null;
        }

        EnemyController instance = Instantiate(
            _enemyPrefab,
            position,
            rotation,
            _spawnContainer);

        instance.SetConfig(config);

        return instance;
    }

    /// <summary>
    /// Convenience overload: creates at the factory GameObject's position.
    /// </summary>
    public EnemyController Create(EnemyConfig config) =>
        Create(config, transform.position, Quaternion.identity);
}
```

## Anti-Examples

### Deep Variant Chains

```
// BAD — overrides propagate unpredictably beyond 2 levels.
// Changing a material on BaseSword unexpectedly appears on SharpIronSword
// but not on EnchantedSharpIronSword because of an intermediate override.
BaseSword.prefab
  └── IronSword.prefab          (variant — acceptable)
        └── SharpIronSword.prefab     (variant of variant — danger zone)
              └── EnchantedSharpIronSword.prefab  (3 levels deep — do not do this)
```

Flatten to: one `BaseSword.prefab` + a `SwordConfig` SO per sword type. Enchantment is a runtime component, not a variant.

---

### Modifying Prefab Instances in Scene Without Applying

```csharp
// BAD — editing an instance's serialized field in the Inspector without
// clicking "Apply to Prefab" leaves the prefab in an inconsistent state.
// Other instances do not receive the change; the prefab asset is stale.

// In the Inspector, you changed EnemyController._config on an instance
// but never applied → the prefab still has the old config.
// Next time you reset the instance, the change is lost silently.
```

Rule: either apply overrides immediately or accept them as intentional per-instance customisation and document why. Never leave accidental per-instance overrides on scene objects.

---

### Using Prefab Variants for Data-Only Differences at Scale

```
// BAD — 40 weapon prefabs, all identical except for a stats SO reference.
// Adding a new weapon requires duplicating the whole prefab asset.
// Stats changes require opening each prefab individually.
GreenSword.prefab    (variant — only _config field differs)
BlueSword.prefab     (variant — only _config field differs)
RedSword.prefab      (variant — only _config field differs)
// ... 37 more ...
```

Use one `Sword.prefab` + 40 `SwordConfig.asset` files. The prefab is the single source of truth for structure and behaviour; the SO is the single source of truth for data.

---

### Nested Prefabs Deeper Than 2 Levels

```
// BAD — 3-level nesting causes cascading override propagation issues.
// Editing the innermost nested prefab triggers a chain of re-serialisation
// across all parent prefabs, and overrides at level 2 can silently mask
// changes at level 1.
Vehicle.prefab
  └── Chassis.prefab       (nested — acceptable)
        └── Wheel.prefab         (nested nested — acceptable)
              └── Lug.prefab           (nested × 3 — do not do this)
```

Flatten: `Lug` is baked into `Wheel`. `Wheel` is a standalone prefab. `Chassis` nests `Wheel`. `Vehicle` nests `Chassis`. Depth = 2, not 3.

---

### Overriding the Root Transform Position on a Variant

```
// BAD — overriding root Transform position on a variant means every instance
// spawns at a fixed world position unless overridden again at instantiation time.
// Breaks pooling, procedural spawning, and scene placement.
EnemyVariant.prefab:
  Overrides:
    Transform.position = (10, 0, 5)   ← never override root position on a variant
```

Root position should always be `(0, 0, 0)` on prefab assets. Position is set by the code that instantiates or places the object.

## Cross-References

- Related skills: `hades:unity-architect`, `hades:prefab-workflow`, `hades:data-modeling`
- Hades MCP tools used in this skill:
  - `find_prefabs_with_component` — discover existing prefab patterns before introducing new ones
  - `search_by_name` — inventory prefab assets and naming conventions
  - `trace_dependencies` — understand blast radius before modifying a base prefab
  - `recall_memory` — retrieve documented prefab strategy and SO config decisions
  - `propose_memory_update` — record new prefab architecture decisions for the team
