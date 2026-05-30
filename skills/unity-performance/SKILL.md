---
description: "Use when analyzing or optimizing Unity performance — CPU/GPU/RAM profiling, object pooling, batching strategies, physics optimization, memory management, and platform-specific budgets."
---

# Unity Performance

Deep decision framework for Unity performance analysis and optimization. Profile first, identify the bottleneck, then apply the targeted fix. This skill provides CPU/GPU/RAM cost models, object pooling, batching, physics, and platform budget guidance.

## When to Apply

Activate when the conversation involves:
- Profiling or measuring performance in the Unity Editor or on-device
- Choosing between rendering strategies (static batching, GPU instancing, SRP Batcher, atlases)
- Deciding whether to pool objects, and how to implement the pool
- Physics performance (collision layers, collider types, timestep tuning)
- Memory management (texture compression, audio load types, heap allocations)
- Frame rate targets and platform budget planning
- Identifying hotspots in Update loops or high-frequency code paths

Do NOT activate for broad architectural questions about how to structure a system — those go to `hades:unity-architect` first.

## Project Context Check

Before making recommendations, gather project-specific context so advice is calibrated to the actual codebase.

1. **Understand the project scale and render pipeline:**
   - Call `get_project_summary()` — reveals render pipeline (URP/HDRP/Built-in), platform target, scene count, and asset volumes. A mobile URP project requires very different advice than a PC HDRP project.
   - Call `analyze_render_pipeline()` — shows current rendering setup, active features, SRP Batcher status, and any custom passes that affect draw call counts.

2. **Find hot-path candidates:**
   - Call `find_components_using_pattern("Update")` — lists MonoBehaviours with Update methods. Every entry is a hot-path candidate. Large counts on mobile are an immediate concern.
   - Call `find_components_using_pattern("FixedUpdate")` — physics-side hot paths.
   - Call `find_components_using_pattern("LateUpdate")` — camera/IK hot paths.

3. **Check documented performance targets:**
   - Call `recall_memory("performance optimization budget")` — retrieves any frame budgets, platform targets, or optimization decisions already recorded by the team.
   - If no memory exists on the topic, after the user accepts a recommendation, call `propose_memory_update` to record the decision.

4. **Adapt recommendations based on findings:**
   - If `get_project_summary()` shows mobile target → apply mobile budgets and pooling thresholds.
   - If `analyze_render_pipeline()` shows SRP Batcher already enabled → skip enabling advice, focus on shader compatibility instead.
   - If `find_components_using_pattern("Update")` returns > 50 components → staggered update pattern is mandatory, not optional.

## Decision Framework

### The Prime Directive: Profile First

Never optimize based on assumptions. The bottleneck is almost always somewhere unexpected.

**Profiling tools and when to use each:**

| Tool | What It Shows | When to Use |
|------|--------------|-------------|
| Unity Profiler (CPU module) | Frame time by thread, GC allocations, script/physics/rendering breakdown | Always — start here |
| Unity Profiler (GPU module) | GPU time per draw, shader cost | When CPU looks healthy but frame rate is still bad |
| Unity Profiler (Memory module) | Managed heap, native allocations, texture/mesh memory | When RAM is a concern or GC spikes appear |
| Frame Debugger | Per-draw-call breakdown, batching status, overdraw visualization | When draw calls are high or batching seems broken |
| Memory Profiler package | Full heap snapshots, retained object graphs | When a memory leak is suspected |

**Profiler workflow (step-by-step):**
1. Open the Profiler window (Window → Analysis → Profiler).
2. Enable **Deep Profile** only for targeted investigation — it adds significant overhead and changes timings.
3. Build a **Development Build** with **Autoconnect Profiler** enabled; profile on the target device, not in the Editor.
4. Capture 300+ frames during normal gameplay, not idle.
5. In the CPU module, sort by **Self Time** (descending) to isolate expensive individual functions.
6. Look for GC Alloc column — any non-zero value in hot paths is a problem.
7. Once you identify the top three hotspots, stop profiling and fix them before profiling again.

---

### Platform Budget Targets

Define the budget before designing. Profile against these targets.

| Platform | Target FPS | Frame Budget | Draw Call Budget | RAM Budget |
|----------|-----------|--------------|-----------------|------------|
| Mobile (iOS/Android) | 30 fps | 33 ms | < 100 | < 1.5 GB |
| Mobile (high-end) | 60 fps | 16 ms | < 150 | < 2 GB |
| Console (PS5/XSX) | 60 fps | 16 ms | < 2,000 | < 8 GB |
| Console (target 120 fps) | 120 fps | 8 ms | < 1,500 | < 8 GB |
| PC (min spec) | 60 fps | 16 ms | < 3,000 | < 4 GB |
| PC (high end) | 120 fps | 8 ms | < 5,000 | < 8 GB |
| PC (uncapped) | 120+ fps | 4 ms | < 5,000 | application-defined |

These are starting targets. Profile on your minimum-spec hardware to calibrate.

---

### CPU Cost Model

**Expensive operations — avoid in hot paths (Update, FixedUpdate, LateUpdate):**

| Operation | Why Expensive | Fix |
|-----------|--------------|-----|
| `GetComponent<T>()` | Reflection-based lookup every call | Cache result in `Awake()` or `Start()` |
| `GameObject.Find()` / `FindObjectOfType()` | Linear scan of entire scene | Cache on init; use direct references or events |
| String concatenation (`+`, `$""`) | Allocates a new string object each time | Use `StringBuilder`; avoid in Update entirely |
| LINQ in Update | Allocates enumerators, closures, and intermediate lists | Replace with explicit loops and pre-allocated buffers |
| `Instantiate` / `Destroy` (frequent) | Full GameObject construction and GC pressure | Object pool — see pooling decision below |
| `Physics.Raycast` (many per frame) | Full broad-phase query | Use `RaycastNonAlloc`; limit ray length; reduce frequency |
| `SendMessage` / `BroadcastMessage` | Reflection-based; touches entire component list | Direct method calls, `UnityAction`, or `event` delegates |
| `Camera.main` | Property call scans scene for tagged camera | Cache `Camera.main` in `Awake()` |
| `Object.FindObjectsOfType<T>()` | Full scene scan, allocates result array | Cache list at initialization; maintain manually |

**Cheap operations — fine in hot paths:**
- `transform.position`, `transform.rotation` — cached by the engine
- `CompareTag("Player")` — hash comparison; faster than `gameObject.tag == "Player"` (which allocates)
- Null checks on Unity objects via `== null` — use this, not `?.` or `??` (those bypass Unity's null check override)
- Static method calls
- Struct operations (Vector3 math, Quaternion operations)
- Fixed-size arrays with index access
- `TryGetComponent<T>()` — preferred over `GetComponent<T>()` when result may be null (avoids logged warning)

---

### GPU Cost Model

**Draw calls (CPU→GPU overhead per rendered batch):**

Each unique mesh+material combination is at minimum one draw call. The CPU must submit each separately unless batching reduces them.

| Batching Type | Requirements | Best For |
|--------------|-------------|---------|
| Static batching | Objects marked Batching Static; do not move | Environment, props, decorations |
| Dynamic batching | < 300 vertices per mesh; same material | Small moving objects (particles, small props) |
| GPU instancing | Same mesh + same material; enable on material | Many identical objects (trees, grass, enemies) |
| SRP Batcher | URP/HDRP only; objects share the same shader (different materials OK) | Diverse scenes with many different materials |

**Fill rate (per-pixel cost):**
- Transparent objects disable early-Z rejection — they are drawn back-to-front, and every overlapping pixel is shaded multiple times (overdraw).
- Reduce overdraw: use opaque materials where transparency is optional; shrink particle sizes; use LOD to reduce coverage at distance.
- Check overdraw with Frame Debugger's overdraw visualization mode.

**Shader complexity:**
- Per-vertex work scales with polygon count.
- Per-pixel (fragment) work scales with screen resolution and coverage — far more expensive on mobile.
- Each texture sample costs bandwidth; combine maps (e.g., a single RGBA texture carrying roughness/metallic/AO in separate channels).
- Avoid `discard`/`clip` in fragment shaders on mobile — some hardware cannot early-out on these.

---

### Memory Cost Model

**Textures (typically the largest memory category):**

| Resolution | Format | VRAM Usage |
|------------|--------|-----------|
| 2048×2048 | RGBA32 (uncompressed) | 16 MB |
| 2048×2048 | DXT5/BC3 (PC) | 4 MB |
| 2048×2048 | ASTC 4×4 (mobile) | ~4 MB |
| 2048×2048 | ETC2 RGBA8 (Android) | ~4 MB |
| + Mipmaps | any | +33% memory |

Rules:
- Always compress textures. Uncompressed RGBA32 is only appropriate for render targets.
- Use the smallest mipmap level needed for the farthest viewing distance.
- Stream large atlas textures via Addressables to avoid loading everything at once.

**Meshes:**
- 10K vertices ≈ 1 MB (with normals, UVs, tangents, bone weights).
- Share meshes across instances via GPU instancing — one copy in memory, many transforms in a single draw call.
- Use `Mesh.Optimize()` on import for better GPU vertex cache utilization.

**Audio:**

| Source | 1 min stereo | Notes |
|--------|-------------|-------|
| WAV (uncompressed) | ~10 MB | Instant playback, large |
| Vorbis compressed | ~1 MB | Good trade-off for music |
| ADPCM | ~2.5 MB | Low decode cost, good for SFX |

Load type guidance:
- `Decompress On Load` — uses decompressed size in RAM; fastest playback. Use for short SFX played frequently.
- `Compressed In Memory` — uses compressed size in RAM; tiny cost at playback. Use for music and infrequent SFX.
- `Streaming` — reads from disk at playback. Use for long music tracks (> 5 seconds) on platforms with fast storage.

---

### DECISION: Object Pooling

```
How often is this object spawned and destroyed?
├── Rarely (< 1/s) and it's a singleton or manager
│   └── Direct Instantiate/Destroy — simpler, correct
├── Occasionally (1–5/s) on PC/console
│   └── Instantiate/Destroy is usually fine — profile to confirm
├── Frequently (> 5/s) OR on mobile at any rate
│   └── Pool — see implementation below
└── Burst: many spawned at scene start, few during play
    └── Pre-warm during loading screen; pool with small cap
```

**Pool implementation checklist:**
1. Pre-warm during loading (not during gameplay or Awake of a live scene).
2. `Get()` → `SetActive(true)` → reset ALL state (position, rotation, velocity, health, timers, particle systems).
3. `Return()` → notify the component → `SetActive(false)` → re-parent under the pool.
4. Cap the live count to prevent unbounded growth; log a warning when the cap is hit.
5. Use `Stack<T>` (LIFO) — objects returned and immediately re-gotten stay cache-warm.

---

### DECISION: Rendering Optimization

```
Are objects static (do not move)?
├── Yes → Enable Batching Static flag → static batching applies automatically
└── No (dynamic objects)
    ├── Many identical copies (trees, grass, enemies, projectiles)?
    │   └── GPU Instancing — enable on material; can handle different transforms
    ├── Many different materials, same shader family? (URP/HDRP)
    │   └── SRP Batcher — already on by default; ensure shaders are SRP-compatible
    ├── Small mesh (< 300 verts), same material?
    │   └── Dynamic batching — Unity applies automatically
    └── Sprites from same atlas?
        └── Ensure atlas assignment; draw order must be contiguous for batching
```

**Sprite atlases — separate by usage frequency:**
- Do not put gameplay sprites and UI sprites in the same atlas; they load together.
- Separate atlases by scene: loading screen art should not load main-menu sprites.
- One atlas = one material = all sprites batchable in a single draw call.

---

### DECISION: Physics Optimization

```
Is the physics step expensive?
├── Check Fixed Timestep (Edit → Project Settings → Physics)
│   ├── Default 0.02 (50 Hz) is appropriate for most games
│   └── Reducing to 0.03 (33 Hz) saves CPU; test gameplay feel carefully
├── Check Solver Iterations
│   ├── Default is 6; reduce to 4 for less accuracy, lower CPU
│   └── Increase only for stacking puzzles or precise joints
└── Check collision pairs (Physics matrix)
    └── Disable collisions between layers that never need to interact
```

**Collider choice (cheapest to most expensive):**
1. `SphereCollider` — single point + radius, fastest broad and narrow phase
2. `CapsuleCollider` — two spheres + cylinder, good for characters
3. `BoxCollider` — 6 planes, good for rooms and crates
4. `MeshCollider (convex)` — approximated convex hull, moderate cost
5. `MeshCollider (non-convex)` — exact mesh, very expensive; never use on moving/kinematic rigidbodies

**Physics best practices:**
- Never use `MeshCollider` on a `Rigidbody` that moves or rotates. Use a compound of primitives instead.
- Set `Rigidbody.collisionDetectionMode = Discrete` (default) for objects that are not fast-moving. Use `Continuous` only for projectiles.
- `Rigidbody.Sleep()` when an object is stationary — Unity does this automatically but you can force it.
- `Physics.IgnoreLayerCollision()` at runtime to disable specific pairs that become irrelevant.

---

### DECISION: Update Optimization

```
Does this MonoBehaviour need to run every frame?
├── No — driven by events (state machine, input, trigger)
│   └── Disable the component or remove Update entirely; use events
├── Yes but only when something changes
│   └── Dirty flag pattern — skip the work when nothing changed
├── Yes but can tolerate latency
│   └── Staggered update coroutine — spread N instances across M frames
└── Yes, every frame, many instances (> 50 MonoBehaviours)
    └── Consider a manager that iterates a list manually (avoids per-MB overhead)
```

## Code Examples

### Generic Object Pool

Full class with `IPoolable` interface, pre-warm, `Get`, `Return`, cap, and `Stack`-based storage.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generic MonoBehaviour-based pool. Attach to a manager GameObject.
/// T must be a Component. Implementing IPoolable is optional but recommended.
/// </summary>
public class ObjectPool<T> : MonoBehaviour where T : Component
{
    [SerializeField] private T _prefab;
    [SerializeField] private int _initialSize = 10;
    [SerializeField] private int _maxSize = 50;

    private readonly Stack<T> _available = new();
    private int _liveCount;

    private void Awake() => PreWarm();

    /// <summary>
    /// Call during a loading screen to populate the pool before gameplay begins.
    /// Calling during active gameplay causes visible hitches.
    /// </summary>
    public void PreWarm()
    {
        for (int i = 0; i < _initialSize; i++)
            _available.Push(CreateInstance());
    }

    /// <summary>
    /// Get an instance from the pool, positioned and activated.
    /// Returns null if the live cap has been reached — callers must handle null.
    /// </summary>
    public T Get(Vector3 position, Quaternion rotation)
    {
        if (_available.Count == 0)
        {
            if (_liveCount >= _maxSize)
            {
                Debug.LogWarning($"[Pool<{typeof(T).Name}>] Hard cap of {_maxSize} reached. Returning null.");
                return null;
            }
            _available.Push(CreateInstance());
        }

        T instance = _available.Pop();
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.gameObject.SetActive(true);
        _liveCount++;

        if (instance is IPoolable poolable)
            poolable.OnGetFromPool();

        return instance;
    }

    /// <summary>
    /// Return an instance to the pool. Callers must stop all references after calling this.
    /// </summary>
    public void Return(T instance)
    {
        if (instance == null) return;

        if (instance is IPoolable poolable)
            poolable.OnReturnToPool();

        instance.gameObject.SetActive(false);
        instance.transform.SetParent(transform);
        _available.Push(instance);
        _liveCount--;
    }

    private T CreateInstance()
    {
        T obj = Instantiate(_prefab, transform);
        obj.gameObject.SetActive(false);
        return obj;
    }
}

/// <summary>
/// Optional interface for components that need to reset state when pooled.
/// Implement this on any component that stores runtime state (velocity, timers, etc.).
/// </summary>
public interface IPoolable
{
    /// <summary>Called immediately after the object is activated by the pool.</summary>
    void OnGetFromPool();

    /// <summary>Called immediately before the object is deactivated by the pool.</summary>
    void OnReturnToPool();
}
```

---

### Non-Allocating Raycast Pattern

Pre-allocated buffer with `RaycastNonAlloc` — zero GC allocations per call.

```csharp
using UnityEngine;

/// <summary>
/// Example component demonstrating non-allocating raycasts.
/// Use this pattern anywhere Physics.Raycast or Physics.RaycastAll would be called
/// in Update, FixedUpdate, or any high-frequency path.
/// </summary>
public class NonAllocRaycastExample : MonoBehaviour
{
    [SerializeField] private float _maxDistance = 20f;
    [SerializeField] private LayerMask _hitMask;

    // Pre-allocate once. Size this to the maximum number of hits you care about.
    // Hits beyond this count are silently discarded — size conservatively.
    private readonly RaycastHit[] _hitBuffer = new RaycastHit[8];

    private void Update()
    {
        int hitCount = Physics.RaycastNonAlloc(
            origin:    transform.position,
            direction: transform.forward,
            results:   _hitBuffer,
            maxDistance: _maxDistance,
            layerMask: _hitMask
        );

        // Only iterate up to hitCount — the rest of _hitBuffer is stale data.
        for (int i = 0; i < hitCount; i++)
        {
            ref RaycastHit hit = ref _hitBuffer[i]; // ref avoids struct copy
            ProcessHit(hit.collider, hit.point, hit.normal);
        }
    }

    private void ProcessHit(Collider col, Vector3 point, Vector3 normal)
    {
        // Your hit logic here — keep this cheap in hot paths.
        // Avoid GetComponent, string operations, or allocations.
        if (col.TryGetComponent<IDamageable>(out IDamageable target))
            target.TakeDamage(10f, point, normal);
    }
}

/// <summary>Interface for anything that can receive damage.</summary>
public interface IDamageable
{
    void TakeDamage(float amount, Vector3 point, Vector3 normal);
}
```

---

### Dirty-Flag Update Optimization

Skip expensive per-frame work when nothing has changed. Reduces CPU time for MonoBehaviours that react to infrequent state changes.

```csharp
using UnityEngine;

/// <summary>
/// Demonstrates the dirty-flag pattern for Update optimization.
/// The expensive recalculation runs only when the data actually changes,
/// not every single frame.
/// </summary>
public class DirtyFlagExample : MonoBehaviour
{
    [SerializeField] private float _value;

    private float _cachedValue;
    private float _cachedResult;
    private bool _isDirty = true;

    /// <summary>
    /// Call this from external systems that modify the value.
    /// Setting the flag is O(1) — the work is deferred to the next Update.
    /// </summary>
    public void SetValue(float newValue)
    {
        if (Mathf.Approximately(_value, newValue)) return; // no change, skip
        _value = newValue;
        _isDirty = true;
    }

    private void Update()
    {
        if (!_isDirty) return; // nothing changed — skip all work this frame

        _cachedResult = RunExpensiveCalculation(_value);
        _cachedValue  = _value;
        _isDirty      = false;

        ApplyResult(_cachedResult);
    }

    // Simulate an expensive calculation (e.g., pathfinding cost estimate,
    // mesh rebuild, shader property update).
    private float RunExpensiveCalculation(float input)
    {
        float result = 0f;
        for (int i = 0; i < 100; i++)
            result += Mathf.Sin(input * i);
        return result;
    }

    private void ApplyResult(float result)
    {
        // Apply to renderer, UI, or physics — whatever reacts to the value.
        transform.localScale = Vector3.one * (1f + result * 0.01f);
    }
}
```

---

### Staggered Update — Spread Work Across Frames

When many objects perform independent work that doesn't need to run every frame, distribute them across a time window to flatten CPU spikes.

```csharp
using System.Collections;
using UnityEngine;

/// <summary>
/// A manager that staggers updates across multiple frames to avoid
/// all N objects running expensive logic in the same frame.
///
/// Example: 60 AI agents, each doing a path evaluation.
/// Instead of all 60 running in frame 1, run 10 per frame across 6 frames.
/// Latency per agent increases from 0 to ~6 frames — acceptable for AI.
/// </summary>
public class StaggeredUpdateManager : MonoBehaviour
{
    [Tooltip("How many frames to spread the full update cycle across.")]
    [SerializeField] private int _framesPerCycle = 6;

    private IStaggeredUpdatable[] _registeredObjects;
    private int _currentBatchIndex;

    private void Start()
    {
        // Gather all registered objects once.
        _registeredObjects = GetComponentsInChildren<IStaggeredUpdatable>();
        StartCoroutine(RunStaggeredLoop());
    }

    private IEnumerator RunStaggeredLoop()
    {
        while (true)
        {
            if (_registeredObjects == null || _registeredObjects.Length == 0)
            {
                yield return null;
                continue;
            }

            int batchSize = Mathf.CeilToInt((float)_registeredObjects.Length / _framesPerCycle);
            int start = _currentBatchIndex * batchSize;
            int end   = Mathf.Min(start + batchSize, _registeredObjects.Length);

            for (int i = start; i < end; i++)
                _registeredObjects[i].StaggeredUpdate();

            _currentBatchIndex = (_currentBatchIndex + 1) % _framesPerCycle;

            yield return null; // wait one frame before processing the next batch
        }
    }

    /// <summary>Call when a new object should join the staggered update cycle.</summary>
    public void Register(IStaggeredUpdatable obj)
    {
        // Rebuild array — call this only on spawn, not every frame.
        var list = new System.Collections.Generic.List<IStaggeredUpdatable>(_registeredObjects ?? System.Array.Empty<IStaggeredUpdatable>());
        list.Add(obj);
        _registeredObjects = list.ToArray();
    }
}

/// <summary>
/// Implement this on any MonoBehaviour that wants to participate in staggered updates.
/// Do NOT implement Unity's Update() on the same component — remove it.
/// </summary>
public interface IStaggeredUpdatable
{
    void StaggeredUpdate();
}
```

## Anti-Examples

### LINQ in Update

```csharp
// BAD — every frame allocates an enumerator, a closure, and an intermediate list.
// On mobile, this causes GC spikes visible in the Profiler as GC.Collect frames.
private List<Enemy> _enemies;

private void Update()
{
    var closeEnemies = _enemies.Where(e => e.Distance < 10f).ToList(); // allocates!
    foreach (var enemy in closeEnemies)
        enemy.Alert();
}

// GOOD — zero allocations, explicit loop, same logic.
private void Update()
{
    for (int i = 0; i < _enemies.Count; i++)
    {
        if (_enemies[i].Distance < 10f)
            _enemies[i].Alert();
    }
}
```

---

### GetComponent in Update

```csharp
// BAD — GetComponent is a reflection-based lookup performed every frame.
// Multiplied across many MonoBehaviours, this adds up quickly.
private void Update()
{
    Rigidbody rb = GetComponent<Rigidbody>(); // O(n) lookup every frame!
    rb.AddForce(Vector3.up);
}

// GOOD — cache in Awake, use cached reference every frame.
private Rigidbody _rb;

private void Awake()
{
    _rb = GetComponent<Rigidbody>(); // once
}

private void Update()
{
    _rb.AddForce(Vector3.up); // cheap field access
}
```

---

### Premature Optimization Without Profiler Data

```csharp
// BAD — replacing a simple loop with Burst/Jobs before profiling shows any issue.
// Adds maintenance cost, complexity, and scheduling overhead.
// The actual bottleneck might be a single GetComponent call elsewhere.
[BurstCompile]
struct EnemyPositionSyncJob : IJobParallelFor
{
    public NativeArray<float3> Positions;
    public void Execute(int i) { /* 40 lines of NativeContainer boilerplate */ }
}

// GOOD — run the profiler first. If the simple loop is not in the top 10
// hotspots, it is not worth optimizing. If it is, then consider Jobs.
private void Update()
{
    for (int i = 0; i < _enemies.Count; i++)
        _enemies[i].SyncPosition(); // profile this first
}
```

---

### Physics.RaycastAll in Hot Path

```csharp
// BAD — RaycastAll allocates a new RaycastHit[] every call.
// Called in Update across many objects, this generates significant GC pressure.
private void Update()
{
    RaycastHit[] hits = Physics.RaycastAll(transform.position, transform.forward, 20f);
    foreach (var hit in hits)
        ProcessHit(hit);
}

// GOOD — pre-allocate once, use RaycastNonAlloc.
private readonly RaycastHit[] _buffer = new RaycastHit[8];

private void Update()
{
    int count = Physics.RaycastNonAlloc(transform.position, transform.forward, _buffer, 20f);
    for (int i = 0; i < count; i++)
        ProcessHit(_buffer[i]);
}
```

---

### Mesh Collider on a Moving Kinematic Rigidbody

```csharp
// BAD — MeshCollider (non-convex) on a moving/kinematic rigidbody.
// Unity must re-compute the collision shape every physics step. Very expensive.
[RequireComponent(typeof(Rigidbody), typeof(MeshCollider))]
public class MovingPlatform : MonoBehaviour
{
    private MeshCollider _col;
    private void Awake()
    {
        _col = GetComponent<MeshCollider>();
        _col.convex = false; // non-convex + kinematic = very expensive
        GetComponent<Rigidbody>().isKinematic = true;
    }
}

// GOOD — use a compound of primitive colliders instead.
// Same coverage, fraction of the cost.
// Add BoxCollider/CapsuleCollider/SphereCollider children to approximate the shape.
```

---

### String Concatenation in Hot Path

```csharp
// BAD — each + creates a new string object on the managed heap.
// In Update across many objects, this triggers frequent GC passes.
private void Update()
{
    Debug.Log("Enemy " + _id + " at position: " + transform.position); // 3 allocs
}

// GOOD (option 1) — remove debug logs from Update entirely (shipping code).
// GOOD (option 2) — use a cached string format for editor-only debug display.
#if UNITY_EDITOR
private void Update()
{
    // Still allocates, but stripped from non-editor builds.
}
#endif

// GOOD (option 3) — if you must log, use string.Format with a pre-built format string.
// For truly zero-alloc, use a FixedString from Unity.Collections (Burst-compatible).
```

## Cross-References

- Related skills: `hades:unity-architect` (condensed performance section, architecture trade-offs), `hades:unity-reviewer` (catches performance anti-patterns during code review)
- Hades MCP tools used in this skill:
  - `get_project_summary` — render pipeline, platform, asset scale
  - `analyze_render_pipeline` — active rendering features, SRP Batcher status
  - `find_components_using_pattern` — hot-path MonoBehaviour discovery
  - `recall_memory` — documented performance targets and decisions
  - `propose_memory_update` — record new performance decisions for the team
- Unity docs: [Unity Profiler](https://docs.unity3d.com/6000.0/Documentation/Manual/Profiler.html), [Frame Debugger](https://docs.unity3d.com/6000.0/Documentation/Manual/frame-debugger-window.html), [Memory Profiler](https://docs.unity3d.com/Packages/com.unity.memoryprofiler@latest), [SRP Batcher](https://docs.unity3d.com/6000.0/Documentation/Manual/SRPBatcher.html), [GPU Instancing](https://docs.unity3d.com/6000.0/Documentation/Manual/GPUInstancing.html), [Physics.RaycastNonAlloc](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.RaycastNonAlloc.html)
