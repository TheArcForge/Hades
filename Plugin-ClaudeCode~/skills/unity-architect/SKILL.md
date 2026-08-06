---
description: "Use when making Unity architecture decisions — system design, scope analysis, feature planning. Covers component strategy, scene structure, data modeling, prefab architecture, and performance. Routes to specialized sub-skills for deep dives."
---

# Unity Architect

Top-level routing skill for Unity architecture decisions. Provides decision frameworks for component strategy, scene structure, data modeling, prefab architecture, and performance — then routes to specialized sub-skills for deeper implementation guidance.

## When to Apply

Activate when the conversation involves:
- Designing a new system or feature from scratch ("how should I structure X?")
- Evaluating trade-offs between architectural approaches
- Scoping a feature before implementation starts
- Asking whether to use MonoBehaviour vs ScriptableObject vs ECS
- Asking how to organize scenes, prefabs, or data
- Performance planning before any profiling has been done
- Cross-cutting concerns: networking, persistence, or testability

Do NOT activate for narrow implementation questions about a specific existing system — those go directly to the relevant sub-skill.

## Project Context Check

Before making recommendations:

1. **Check existing patterns in the graph (always use Hades tools first, not grep/find):**
   - Call `get_project_summary()` to understand project scale, render pipeline, assembly count, asset coverage, and asset volumes — a 200-prefab project warrants different advice than a 20-prefab one
   - Call `search_by_name("<keyword>", path_prefix="Assets/Scripts")` to find scripts in a specific directory, or use `match_mode="exact"` for precise lookups
   - Call `find_references_to("<script_path>")` to discover all dependents — returns both asset references (prefabs, scenes) and C# code references (fields, parameters, inheritance, constructors)
   - Call `find_components_using_pattern("Pool")` or `find_components_using_pattern("Factory")` to discover whether spawn infrastructure already exists
   - Call `find_components_using_pattern("GameEvent")` or `find_components_using_pattern("EventChannel")` to detect event architecture
   - Call `trace_dependencies("<CentralSystem>")` on any system the new feature depends on to surface tight coupling early

2. **Check team decisions in memory:**
   - Call `recall_memory("architecture decisions conventions")` to find documented conventions
   - Call `recall_memory("scene structure loading")` when scene architecture is in scope
   - Call `recall_memory("data modeling scriptable object")` when data architecture is in scope
   - Check validation status — if a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt recommendations based on findings:**
   - If graph shows existing pattern → align new systems with it; note the pattern name explicitly
   - If graph shows nothing related → introduce from scratch; ask if the team wants to document it
   - If memory documents a decision → follow it unless there is a strong technical reason not to
   - If memory is empty on a topic → after the user accepts a recommendation, call `propose_memory_update` to record it

## Decision Framework

### Step 1 — Six Analysis Questions

Answer these before choosing any pattern. Each answer narrows the decision tree.

| # | Question | Why It Matters |
|---|----------|----------------|
| 1 | **Scale** — how many instances exist at once? | 1 → simple; 10s → pooling optional; 100s+ → pooling required; 1000s → ECS |
| 2 | **Lifetime** — does it persist across scenes, or die with the scene? | Persistent → DontDestroyOnLoad or SO; Transient → normal hierarchy |
| 3 | **Variance** — do instances differ in data, behaviour, or both? | Data only → SO config + single prefab; Behaviour → variants or composition |
| 4 | **Ownership** — which system creates, owns, and destroys it? | Ambiguous ownership → pain later; name the owner explicitly |
| 5 | **Dependencies** — what does it need to function? | Long list → consider a mediator or event channel to decouple |
| 6 | **Performance budget** — CPU frame cost, GPU draw calls, RAM footprint? | Define budget before designing; profile after, not before |

---

### DECISION: Object Representation

```
Does it appear in the scene as a visible GameObject?
├── Yes
│   ├── Instances vary in data but not behaviour?
│   │   └── SO config + single prefab  (one prefab, swap SO at runtime)
│   ├── Instances vary in behaviour?
│   │   ├── < 3 levels of variation → Prefab Variants
│   │   └── >= 3 levels or combinatorial → Component Composition
│   └── Exact duplicate, no variance → Base Prefab
└── No (pure data / logic)
    ├── Needs inspector authoring by designers? → ScriptableObject
    ├── Runtime-only, lives in code?            → plain C# class
    └── Needs persistence?                      → plain C# class + JSON/SQLite
```

---

### DECISION: Spawning Strategy

```
How many instances per second?
├── Rare (< 1/s) or unique
│   └── Direct Instantiate — simple, no overhead
├── Frequent (1–30/s) or burst
│   └── Object Pool — pre-warm on scene load, cap max size
└── Complex construction (injected deps, async load)
    └── Factory — centralises creation logic, pool inside if needed
```

---

### DECISION: Scene Architecture

```
How many distinct "screens" or "spaces" exist?
├── 1–3 with full unloads between them
│   └── Single scene per state — simple, no additive overhead
├── Persistent UI / HUD across state changes
│   └── Additive loading — keep UI scene active, load/unload content scenes
└── Many rooms, levels, or areas
    └── Per-screen additive scenes + optional Addressables streaming
        → see hades:scene-architecture for LoadingManager scaffold
```

---

### DECISION: Component Design

```
Is the behaviour a single, cohesive responsibility?
├── Yes → Single MonoBehaviour
└── No — multiple independent concerns
    ├── Are concerns reusable across different prefabs?
    │   └── Composition: separate components (Health, Movement, Combat)
    │       → see hades:component-design for interface contracts
    └── Do concerns share a lot of mutable state?
        └── Controller + Data split:
            - Controller MB reads/writes a plain C# data class
            - Data class is testable without Unity
```

---

### DECISION: Data Architecture

```
Who authors the data and when?
├── Designers at edit-time, values never change at runtime
│   └── ScriptableObject  (drag into inspector, no code to load)
├── Designers at edit-time, but instances vary per actor
│   └── SO as config template + MonoBehaviour runtime state
├── Generated or loaded at runtime
│   ├── Small / always needed   → SerializeField + Resources (simple)
│   └── Large / optional        → JSON or SQLite via Hades bundled DLLs
└── Global constant shared across assemblies
    └── Static readonly or const  (no SO overhead for immutable values)
```

---

### DECISION: Performance Strategy

Establish the budget before designing, not after.

| Concern | Cheap | Expensive | Rule of Thumb |
|---------|-------|-----------|---------------|
| CPU — MonoBehaviour Update | Empty Update is free | Virtual dispatch at scale | Disable Update when idle; use event-driven where possible |
| CPU — Physics | Simple rigidbodies | Mesh colliders, continuous detection | Use primitive colliders; Fixed Timestep at 50 Hz default |
| GPU — Draw Calls | Static batching | Unique materials per object | Share materials; GPU instancing for > 20 identical objects |
| GPU — Fill Rate | Simple shaders, small screen coverage | Transparent overdraw | Profile with Frame Debugger before switching shaders |
| RAM — Textures | Compressed (DXT/ETC) | Uncompressed RGBA32 | Always compress; stream large atlases with Addressables |
| RAM — Audio | Compressed, loaded on demand | Decompress on load for long clips | Set Load Type = Compressed In Memory for music |

Do not apply any optimisation before running Unity Profiler and identifying the actual bottleneck.

---

### Cross-Cutting Concerns

**Networking**
- Decide on authority model (server-authoritative vs client-predicted) before writing any MonoBehaviour
- Keep network state in a dedicated data class; keep presentation in a separate MB that reads from it
- See `hades:unity-networking` for Netcode for GameObjects patterns

**Persistence**
- SO assets are not save data — never write to SO fields at runtime in a build
- Serialise save state as plain C# records → JSON or SQLite (Hades bundled DLLs)
- Separate save-data schema from domain model; add a version field from day one

**Testability**
- Pure C# classes for domain logic; MonoBehaviours for Unity lifecycle only
- Program to interfaces so test doubles can replace Unity dependencies
- See `hades:unity-testing` for EditMode and PlayMode test patterns

## Code Examples

### ScriptableObject Event Channel

Full implementation — eliminates tight coupling between systems without a service locator.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A broadcast event channel with no payload.
/// Create assets: Assets > Events > Game Event
/// </summary>
[CreateAssetMenu(menuName = "Events/Game Event", fileName = "NewGameEvent")]
public class GameEvent : ScriptableObject
{
    private readonly List<GameEventListener> _listeners = new();

    public void Raise()
    {
        // Iterate backwards so listeners can safely unregister during callback.
        for (int i = _listeners.Count - 1; i >= 0; i--)
            _listeners[i].OnEventRaised();
    }

    public void RegisterListener(GameEventListener listener)
    {
        if (!_listeners.Contains(listener))
            _listeners.Add(listener);
    }

    public void UnregisterListener(GameEventListener listener) =>
        _listeners.Remove(listener);
}

/// <summary>
/// Drop on any GameObject to respond to a GameEvent asset.
/// Wire the SO asset to Event and add UnityEvents in the inspector.
/// </summary>
public class GameEventListener : MonoBehaviour
{
    [Tooltip("The GameEvent SO this listener responds to.")]
    [SerializeField] private GameEvent _event;

    [Tooltip("Callbacks invoked when the event is raised.")]
    [SerializeField] private UnityEngine.Events.UnityEvent _response;

    private void OnEnable()  => _event.RegisterListener(this);
    private void OnDisable() => _event.UnregisterListener(this);

    public void OnEventRaised() => _response?.Invoke();
}
```

---

### Generic Object Pool

Full implementation — pre-warm on scene load, hard cap on live instances.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generic MonoBehaviour-based pool. Attach to a manager GameObject.
/// T must be a Component that implements IPoolable (optional but recommended).
/// </summary>
public class ObjectPool<T> : MonoBehaviour where T : Component
{
    [SerializeField] private T _prefab;
    [SerializeField] private int _initialSize = 10;
    [SerializeField] private int _maxSize = 50;

    private readonly Queue<T> _available = new();
    private int _liveCount;

    private void Awake() => PreWarm();

    public void PreWarm()
    {
        for (int i = 0; i < _initialSize; i++)
            _available.Enqueue(CreateInstance());
    }

    /// <summary>Returns a pooled instance, or null if the cap is reached.</summary>
    public T Get(Vector3 position, Quaternion rotation)
    {
        if (_available.Count == 0)
        {
            if (_liveCount >= _maxSize)
            {
                Debug.LogWarning($"[Pool:{typeof(T).Name}] Cap of {_maxSize} reached.");
                return null;
            }
            _available.Enqueue(CreateInstance());
        }

        T instance = _available.Dequeue();
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.gameObject.SetActive(true);
        _liveCount++;

        if (instance is IPoolable poolable)
            poolable.OnGetFromPool();

        return instance;
    }

    /// <summary>Returns an instance to the pool.</summary>
    public void Return(T instance)
    {
        if (instance == null) return;

        if (instance is IPoolable poolable)
            poolable.OnReturnToPool();

        instance.gameObject.SetActive(false);
        instance.transform.SetParent(transform);
        _available.Enqueue(instance);
        _liveCount--;
    }

    private T CreateInstance()
    {
        T obj = Instantiate(_prefab, transform);
        obj.gameObject.SetActive(false);
        return obj;
    }
}

/// <summary>Optional interface for pool-aware components.</summary>
public interface IPoolable
{
    void OnGetFromPool();
    void OnReturnToPool();
}
```

---

### Bootstrap Scene Loader

Full implementation — loads a persistent bootstrap scene first, then additively loads the initial game scene. Keeps global services alive across all scene transitions.

```csharp
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Place in a Bootstrap scene (build index 0).
/// Loads the InitialScene additively after all persistent services initialise.
/// Every subsequent scene transition goes through SceneLoader, not LoadScene directly.
/// </summary>
public class BootstrapSceneLoader : MonoBehaviour
{
    [Tooltip("The scene to load after bootstrap completes.")]
    [SerializeField] private string _initialSceneName = "MainMenu";

    [Tooltip("Optional loading screen scene shown during transitions.")]
    [SerializeField] private string _loadingSceneName = "Loading";

    private static BootstrapSceneLoader _instance;

    private void Awake()
    {
        if (_instance != null)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private IEnumerator Start()
    {
        // Initialise persistent services here (audio, analytics, save system…)
        yield return InitialiseServicesAsync();

        yield return LoadSceneAsync(_initialSceneName);
    }

    public static void TransitionTo(string sceneName)
    {
        if (_instance == null)
        {
            Debug.LogError("[Bootstrap] No BootstrapSceneLoader instance found.");
            return;
        }
        _instance.StartCoroutine(_instance.DoTransition(sceneName));
    }

    private IEnumerator DoTransition(string sceneName)
    {
        if (!string.IsNullOrEmpty(_loadingSceneName))
            yield return SceneManager.LoadSceneAsync(_loadingSceneName, LoadSceneMode.Additive);

        // Unload all content scenes (not bootstrap, not loading).
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (s.name == gameObject.scene.name) continue;
            if (s.name == _loadingSceneName) continue;
            yield return SceneManager.UnloadSceneAsync(s);
        }

        yield return LoadSceneAsync(sceneName);

        if (!string.IsNullOrEmpty(_loadingSceneName))
            yield return SceneManager.UnloadSceneAsync(_loadingSceneName);
    }

    private IEnumerator LoadSceneAsync(string sceneName)
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        while (!op.isDone)
            yield return null;

        SceneManager.SetActiveScene(SceneManager.GetSceneByName(sceneName));
    }

    private IEnumerator InitialiseServicesAsync()
    {
        // Replace with real async service init as needed.
        yield return null;
    }
}
```

---

### Component Composition Example

Separating Health, Movement, and Combat into independent components instead of one god class.

```csharp
using UnityEngine;
using UnityEngine.Events;

// ── Health ────────────────────────────────────────────────────────────────────

public class Health : MonoBehaviour
{
    [SerializeField] private float _maxHealth = 100f;

    public float Current { get; private set; }
    public float Max => _maxHealth;
    public bool IsDead => Current <= 0f;

    public UnityEvent<float> OnHealthChanged;  // passes new value
    public UnityEvent OnDied;

    private void Awake() => Current = _maxHealth;

    public void TakeDamage(float amount)
    {
        if (IsDead) return;
        Current = Mathf.Max(0f, Current - amount);
        OnHealthChanged?.Invoke(Current);
        if (IsDead) OnDied?.Invoke();
    }

    public void Heal(float amount)
    {
        if (IsDead) return;
        Current = Mathf.Min(_maxHealth, Current + amount);
        OnHealthChanged?.Invoke(Current);
    }
}

// ── Movement ──────────────────────────────────────────────────────────────────

[RequireComponent(typeof(Rigidbody))]
public class Movement : MonoBehaviour
{
    [SerializeField] private float _speed = 5f;

    private Rigidbody _rb;
    private Vector3 _desiredVelocity;

    private void Awake() => _rb = GetComponent<Rigidbody>();

    /// <summary>Call from input handler or AI. Direction is world-space, magnitude 0–1.</summary>
    public void SetMoveDirection(Vector3 direction)
    {
        _desiredVelocity = Vector3.ClampMagnitude(direction, 1f) * _speed;
    }

    private void FixedUpdate()
    {
        _rb.linearVelocity = new Vector3(_desiredVelocity.x, _rb.linearVelocity.y, _desiredVelocity.z);
    }
}

// ── Combat ────────────────────────────────────────────────────────────────────

public class Combat : MonoBehaviour
{
    [SerializeField] private float _attackDamage = 25f;
    [SerializeField] private float _attackRange  = 1.5f;
    [SerializeField] private float _attackCooldown = 0.8f;
    [SerializeField] private LayerMask _targetMask;

    private float _nextAttackTime;

    public bool TryAttack()
    {
        if (Time.time < _nextAttackTime) return false;

        Collider[] hits = Physics.OverlapSphere(transform.position, _attackRange, _targetMask);
        foreach (Collider hit in hits)
        {
            if (hit.TryGetComponent<Health>(out Health hp))
                hp.TakeDamage(_attackDamage);
        }

        _nextAttackTime = Time.time + _attackCooldown;
        return true;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _attackRange);
    }
#endif
}
```

## Anti-Examples

### God MonoBehaviour

```csharp
// BAD — one class owns input, movement, health, UI, audio, save state, and AI.
// Adding a feature requires understanding all other features first.
// Testability is near zero; every change risks regressions.
public class PlayerController : MonoBehaviour
{
    public float health;
    public float speed;
    public AudioSource footstepAudio;
    public Text healthLabel;

    void Update()
    {
        // Input
        float h = Input.GetAxis("Horizontal");
        // Movement
        transform.Translate(h * speed * Time.deltaTime, 0, 0);
        // UI
        healthLabel.text = health.ToString();
        // Audio
        if (Mathf.Abs(h) > 0.1f) footstepAudio.Play();
        // ... 300 more lines
    }
}
```

Prefer: separate `Movement`, `Health`, `AudioTrigger`, and `HudView` components wired via events.

---

### Premature Optimisation

```csharp
// BAD — switching to a job-based spatial hash before profiling shows any issue.
// Adds maintenance cost, obscures intent, and may not address the actual bottleneck.
[BurstCompile]
struct EnemyPositionHashJob : IJobParallelFor { /* 80 lines */ }
```

Always profile first with the Unity Profiler. Optimise only the identified hotspot with the simplest change that fixes the budget breach.

---

### Deep Prefab Variant Chains

```
// BAD — variant of a variant of a variant
BaseSword.prefab
  └── IronSword.prefab          (variant)
        └── SharpIronSword.prefab     (variant of variant)
              └── EnchantedSharpIronSword.prefab  (variant of variant of variant)
```

Variant chains deeper than two levels cause cascading propagation issues — a change to `BaseSword` triggers unpredictable overrides at every level. Prefer a single prefab + ScriptableObject config asset for data variance, and composition for behaviour variance.

## After Designing

Once an architectural direction is agreed:
- Use `hades:prefab-workflow` to author the prefab hierarchy
- Use `hades:scene-authoring` to set up scene structure and additive loading
- Use `hades:animation-workflow` for Animator Controller and state machine integration
- Use `hades:unity-reviewer` for code review before merging

Record the decision in memory:
```
propose_memory_update("architecture", "<system name>: <pattern chosen> because <reason>")
```

## Cross-References

- Related skills: `hades:prefab-architecture`, `hades:scene-architecture`, `hades:component-design`, `hades:data-modeling`, `hades:unity-performance`
- Workflow skills: `hades:prefab-workflow`, `hades:scene-authoring`, `hades:animation-workflow`, `hades:unity-reviewer`
- Hades MCP tools: `get_project_summary`, `find_components_using_pattern`, `recall_memory`, `propose_memory_update`, `search_by_name`, `trace_dependencies`
- Unity docs: [ScriptableObject](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ScriptableObject.html), [Prefab Variants](https://docs.unity3d.com/6000.0/Documentation/Manual/PrefabVariants.html), [Additive Scene Loading](https://docs.unity3d.com/6000.0/Documentation/Manual/MultiSceneEditing.html), [Unity Profiler](https://docs.unity3d.com/6000.0/Documentation/Manual/Profiler.html)
