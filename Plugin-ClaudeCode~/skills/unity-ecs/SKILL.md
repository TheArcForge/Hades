---
description: "Use when considering ECS/DOTS architecture — when to use ECS vs MonoBehaviour, Burst compiler, Job System, hybrid approaches, and migration strategies from MonoBehaviour to ECS."
---

# Unity ECS / DOTS

Decision framework for adopting Unity's Data-Oriented Technology Stack. Covers when ECS, Jobs, and Burst deliver meaningful value vs. when they add cost without benefit, how to apply each layer independently, and a proven incremental migration path from MonoBehaviour to full ECS.

## When to Apply

Activate when the conversation involves:
- Evaluating whether to use ECS, Jobs, or Burst for a new system
- A MonoBehaviour simulation that is hitting CPU performance limits
- Questions about `IComponentData`, `SystemBase`, `ISystem`, `IJobParallelFor`, or `BurstCompile`
- Deciding on a hybrid approach (MonoBehaviour logic driving entity data via Baking)
- Migrating an existing MonoBehaviour-based system toward DOTS
- Any mention of entity counts in the thousands or tens of thousands
- NativeContainer allocation or disposal questions

Do NOT activate for general performance questions not involving entities — those go to `hades:unity-performance`. Do NOT activate for architecture questions about scene or prefab structure — those go to `hades:unity-architect`.

## Project Context Check

Before making ECS recommendations, gather context so advice matches the actual project state.

1. **Check for existing ECS usage:**
   - Call `find_components_using_pattern("IComponentData")` — reveals whether ECS components already exist. If they do, advice must be consistent with those patterns.
   - Call `find_components_using_pattern("SystemBase")` and `find_components_using_pattern("ISystem")` — identifies existing ECS systems. Presence of `ISystem` (unmanaged) suggests a performance-first stance; `SystemBase` suggests an earlier adoption or more managed-style code.

2. **Understand project scale and targets:**
   - Call `get_project_summary()` — reveals platform targets (mobile vs. PC/console), project age, and team size. ECS has a steep learning curve; on a solo mobile project with a 6-month timeline, it is rarely appropriate.

3. **Check team decisions in memory:**
   - Call `recall_memory("ECS DOTS performance architecture")` — retrieves any documented decisions about whether the team has committed to ECS, what systems already use it, and any known pain points or constraints.
   - If no memory exists on the topic, after a decision is made call `propose_memory_update` to record the rationale.

4. **Adapt recommendations based on findings:**
   - If no `IComponentData` exists → project is MonoBehaviour-first. Recommend Jobs/Burst on hot paths before committing to full ECS.
   - If `ISystem` is already used → team prefers unmanaged systems. Use `ISystem` + `ref SystemState` patterns in new code.
   - If `get_project_summary()` shows mobile target → Burst on hot paths is high-value; full ECS world setup overhead may not be.

## Decision Framework

### DECISION: ECS vs. MonoBehaviour vs. Hybrid

```
How many similar entities exist simultaneously at peak?
├── < 200 entities
│   └── MonoBehaviour — simpler, designer-friendly, full Unity tooling support.
│       ECS overhead exceeds any benefit at this scale.
│
├── 200 – 2,000 entities
│   └── CONSIDER Hybrid — keep game logic in MonoBehaviour.
│       Move the hot data path (position update, simulation step) to a Job.
│       Profile before committing. MonoBehaviour may still be fast enough.
│
├── 2,000 – 10,000 entities
│   └── Hybrid or Full ECS depending on team skill.
│       Job System + Burst on hot paths is mandatory.
│       Full ECS is justified if the system is isolated and team knows DOTS.
│
└── > 10,000 entities
    └── Full ECS — MonoBehaviour simply cannot sustain this entity count.
        Main-thread iteration overhead and cache misses prevent reaching 60 fps.
```

**Other signals that favor ECS regardless of count:**
- Data is structurally uniform (simulation, particles, pathfinding nodes, crowds)
- System is computation-heavy and embarrassingly parallel (all entities run the same logic)
- Team already uses ECS elsewhere in the project (consistency matters)
- The target platform has clear multi-core availability (PC, console)

**Signals that favor MonoBehaviour regardless of count:**
- Designers need to author and tweak this system in the Inspector frequently
- Entity logic involves complex branching, UnityEvents, or coroutines
- System integrates tightly with third-party MonoBehaviour-only SDKs
- Prototype / jam / short-timeline project — ECS ramp-up cost is not justified

---

### DECISION: Burst Compiler

```
Is this code on a hot path (called >1,000 times/frame)?
├── No
│   └── Don't add Burst — the annotation adds complexity with no measurable gain.
│
└── Yes
    ├── Can the code run in a Job (is it static, no managed references)?
    │   ├── No (uses MonoBehaviour, class instances, Unity Objects)
    │   │   └── Burst cannot compile this — refactor data to structs/NativeArrays first.
    │   │
    │   └── Yes (all structs, NativeContainers, math only)
    │       ├── Add [BurstCompile] to the IJob/IJobParallelFor struct
    │       └── Verify: open Burst Inspector (Jobs → Burst Inspector) to confirm compilation
    │           and read the generated assembly to understand what was vectorized.
```

**What Burst can compile:**
- `struct` value types (no classes, no interfaces unless blittable)
- `NativeArray<T>`, `NativeList<T>`, `NativeHashMap<K,V>` and other Unity.Collections types
- `Unity.Mathematics` types (`float3`, `quaternion`, `float4x4`, etc.)
- `Unsafe` pointer operations
- Static readonly constants

**What Burst cannot compile:**
- Managed class instances (`List<T>`, `Dictionary<K,V>`, `string`, any `class`)
- Virtual method calls or delegates (except `FunctionPointer<T>`)
- `try/catch/finally`
- Boxing operations
- `GameObject`, `Component`, `Transform`, and all `UnityEngine.Object` subtypes

---

### DECISION: Job System Without Full ECS

The Job System can be used entirely within MonoBehaviour code. This is the lowest-risk entry point into DOTS — no world setup, no baking, no system architecture change required.

```
Is the hot path in a MonoBehaviour (e.g., an Update loop processing a large array)?
└── Yes
    ├── Step 1: Extract the data into a NativeArray (allocated in Awake/Start, disposed in OnDestroy)
    ├── Step 2: Write an IJobParallelFor that operates on the NativeArray
    ├── Step 3: Schedule the job in Update, complete it before reading results (same frame or next)
    └── Step 4: Add [BurstCompile] to the job struct if all data is Burst-compatible
```

---

### DECISION: Migration Strategy (MonoBehaviour → ECS)

Migrate incrementally. Never rewrite an entire system at once.

```
Phase 1 — Identify the bottleneck (Profile first)
  Use Unity Profiler to find the single most expensive MonoBehaviour update.
  This is your migration target.

Phase 2 — Extract data to structs
  Move the hot data (positions, velocities, health, etc.) from MonoBehaviour
  fields into plain C# structs or NativeArrays.
  MonoBehaviour becomes a thin wrapper holding the arrays.

Phase 3 — Add a Job
  Write IJobParallelFor (or IJob for non-parallelizable work).
  Schedule from MonoBehaviour Update. Complete before reading results.
  Measure: this step alone often achieves 80% of the ECS performance gain.

Phase 4 — Add Burst
  Add [BurstCompile] to the job struct.
  Verify in Burst Inspector. Measure again.

Phase 5 (optional) — Full ECS conversion
  If Phases 3–4 are not enough, convert the data to IComponentData.
  Add a SubScene with Baking to create entities from authoring MonoBehaviours.
  Replace the MonoBehaviour update with an ISystem.
  Remove the MonoBehaviour entirely.
```

---

### Baking: The Hybrid Bridge

Baking is the official Unity 6 mechanism for authoring ECS data with MonoBehaviour-based Inspector tooling. The authoring component is a plain MonoBehaviour that lives only in the Editor; the Baker converts it to `IComponentData` at build time (or SubScene load time).

This is the recommended entry point for any new ECS work — it preserves the designer workflow while producing ECS runtime data.

## Code Examples

### IJobParallelFor for Batch Position Update

A MonoBehaviour-owned job. No ECS world required. Run from `Update()`.

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// MonoBehaviour that updates a large array of positions using a parallel job.
/// No ECS world required — this is the Job-only hybrid pattern.
/// Owns the NativeArrays and is responsible for disposal.
/// </summary>
public class BatchPositionUpdater : MonoBehaviour
{
    [SerializeField] private int _entityCount = 5000;

    private NativeArray<float3> _positions;
    private NativeArray<float3> _velocities;
    private JobHandle _pendingJob;

    private void Awake()
    {
        // Allocate persistent NativeArrays once. Persistent = survives frame boundaries.
        _positions  = new NativeArray<float3>(_entityCount, Allocator.Persistent);
        _velocities = new NativeArray<float3>(_entityCount, Allocator.Persistent);

        // Initialize with test data.
        for (int i = 0; i < _entityCount; i++)
        {
            _positions[i]  = new float3(i * 0.1f, 0f, 0f);
            _velocities[i] = new float3(0f, 0f, 1f);
        }
    }

    private void Update()
    {
        // Always complete any job from last frame before scheduling a new one.
        // Failing to do this causes race conditions if you read the arrays.
        _pendingJob.Complete();

        var job = new MovePositionsJob
        {
            Positions  = _positions,
            Velocities = _velocities,
            DeltaTime  = Time.deltaTime
        };

        // Schedule: innerloopBatchCount controls how many iterations each worker thread
        // processes in one batch. 64 is a reasonable default; tune with profiler.
        _pendingJob = job.Schedule(_entityCount, 64);
    }

    private void LateUpdate()
    {
        // Complete before any system reads the results (e.g., rendering sync).
        _pendingJob.Complete();
    }

    private void OnDestroy()
    {
        // Always dispose NativeContainers. Leaking them causes a hard error in Unity 6.
        _pendingJob.Complete(); // must complete before disposing
        _positions.Dispose();
        _velocities.Dispose();
    }
}

/// <summary>
/// The job itself must be a struct. Each Execute(i) call runs independently
/// on a worker thread. No shared mutable state is allowed.
/// </summary>
[BurstCompile]
public struct MovePositionsJob : IJobParallelFor
{
    // NativeArray is a reference type visible to Burst. Mark [ReadOnly] on inputs
    // to allow multiple reader threads simultaneously; omit [ReadOnly] on outputs.
    [ReadOnly] public NativeArray<float3> Velocities;
    public NativeArray<float3> Positions;
    public float DeltaTime;

    public void Execute(int i)
    {
        // All math here uses Unity.Mathematics types — Burst vectorizes these automatically.
        Positions[i] += Velocities[i] * DeltaTime;
    }
}
```

---

### Burst-Compiled Job for Expensive Computation

Demonstrates a single-threaded Burst job for a computation that is not parallelizable but is still Burst-compilable.

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Example of a single-threaded Burst job for pathfinding cost estimation
/// across a large node grid. The result is written to a single-element NativeArray
/// so the MonoBehaviour can read it after completion.
/// </summary>
public class PathCostEstimator : MonoBehaviour
{
    [SerializeField] private int _gridSize = 1024;

    private NativeArray<float> _nodeCosts;
    private NativeArray<float> _result;

    private void Awake()
    {
        _nodeCosts = new NativeArray<float>(_gridSize, Allocator.Persistent);
        _result    = new NativeArray<float>(1, Allocator.Persistent);

        // Populate grid with test data.
        var rng = new Unity.Mathematics.Random(42);
        for (int i = 0; i < _gridSize; i++)
            _nodeCosts[i] = rng.NextFloat(1f, 10f);
    }

    private void Update()
    {
        // Schedule and immediately complete — acceptable when the job is very fast
        // and its result is needed in the same frame. For longer jobs, keep the
        // JobHandle alive and complete next frame.
        new EstimatePathCostJob
        {
            NodeCosts = _nodeCosts,
            Result    = _result
        }
        .Schedule()
        .Complete();

        // Safe to read after Complete().
        float estimatedCost = _result[0];
        Debug.Log($"Estimated path cost: {estimatedCost:F2}");
    }

    private void OnDestroy()
    {
        _nodeCosts.Dispose();
        _result.Dispose();
    }
}

[BurstCompile]
public struct EstimatePathCostJob : IJob
{
    [ReadOnly] public NativeArray<float> NodeCosts;
    public NativeArray<float> Result;

    public void Execute()
    {
        float total = 0f;
        for (int i = 0; i < NodeCosts.Length; i++)
            total += math.log(NodeCosts[i] + 1f); // log is Burst-vectorized via Unity.Mathematics
        Result[0] = total;
    }
}
```

---

### Basic ECS Component and System (Unity 6)

Minimal full-ECS example: `IComponentData` component, an `ISystem` that processes it, and a Baking authoring component.

```csharp
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ─── Component ───────────────────────────────────────────────────────────────

/// <summary>
/// IComponentData must be an unmanaged struct. No classes, no strings, no arrays.
/// Store only value-type data. Reference types require IComponentData managed variant
/// (but managed components cannot be Burst-compiled).
/// </summary>
public struct VelocityComponent : IComponentData
{
    public float3 Value;
}

// ─── System ──────────────────────────────────────────────────────────────────

/// <summary>
/// ISystem is the Unity 6 preferred system type — fully unmanaged, Burst-compilable.
/// Prefer ISystem over SystemBase for any new system. Use SystemBase only when you
/// need managed references (coroutines, events, MonoBehaviour interop).
/// </summary>
[BurstCompile]
public partial struct MoveSystem : ISystem
{
    // OnCreate runs once when the system is created. Store cached data here.
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // Require that at least one entity with VelocityComponent exists before running.
        // This avoids scheduling the system every frame when no entities match.
        state.RequireForUpdate<VelocityComponent>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float deltaTime = SystemAPI.Time.DeltaTime;

        // SystemAPI.Query iterates all entities that match the component signature.
        // RefRW = read/write access. RefRO = read-only (allows parallel scheduling).
        foreach (var (transform, velocity) in
            SystemAPI.Query<RefRW<LocalTransform>, RefRO<VelocityComponent>>())
        {
            transform.ValueRW.Position += velocity.ValueRO.Value * deltaTime;
        }
    }

    public void OnDestroy(ref SystemState state) { }
}

// ─── Authoring (Baking) ───────────────────────────────────────────────────────

/// <summary>
/// The authoring component lives only in the Editor / SubScene.
/// It is stripped at bake time and replaced with ECS components.
/// Designers see and edit this in the Inspector; the runtime never sees it.
/// </summary>
public class VelocityAuthoring : MonoBehaviour
{
    public Vector3 Velocity = new Vector3(0f, 0f, 5f);
}

/// <summary>
/// The Baker converts authoring data to IComponentData.
/// One Baker per authoring MonoBehaviour.
/// </summary>
public class VelocityBaker : Baker<VelocityAuthoring>
{
    public override void Bake(VelocityAuthoring authoring)
    {
        Entity entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent(entity, new VelocityComponent
        {
            Value = authoring.Velocity
        });
    }
}
```

---

### Hybrid: MonoBehaviour Spawns Entities via Baking

The MonoBehaviour controls game logic (spawn timing, player input) while ECS handles the high-count simulation.

```csharp
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// A MonoBehaviour that acts as the bridge between the Unity object world
/// and the ECS entity world. It holds a reference to the ECS World and uses
/// EntityManager to spawn entities when game events occur.
///
/// This is the correct hybrid pattern — not having MonoBehaviours update
/// entity data every frame (that defeats the purpose of ECS).
/// </summary>
public class EntitySpawner : MonoBehaviour
{
    [SerializeField] private GameObject _entityPrefab; // must have VelocityAuthoring
    [SerializeField] private int _spawnCount = 100;
    [SerializeField] private float _spawnInterval = 1f;

    // Cached reference to the ECS EntityManager.
    private EntityManager _entityManager;
    // Cached entity produced by baking the prefab.
    private Entity _bakedPrefab;
    private float _timer;

    private void Start()
    {
        // In Unity 6, World.DefaultGameObjectInjectionWorld is the primary ECS world.
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        // BakingUtility converts a prefab GameObject to an entity at runtime.
        // The result is a template entity — not yet live in the simulation.
        _bakedPrefab = GameObjectConversionUtility_Unity6(_entityPrefab);
    }

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < _spawnInterval) return;
        _timer = 0f;

        SpawnBatch();
    }

    private void SpawnBatch()
    {
        for (int i = 0; i < _spawnCount; i++)
        {
            // Instantiate creates a live entity from the baked template.
            Entity entity = _entityManager.Instantiate(_bakedPrefab);

            // Set initial position. LocalTransform replaces the old Translation component.
            _entityManager.SetComponentData(entity, LocalTransform.FromPosition(
                new float3(
                    UnityEngine.Random.Range(-10f, 10f),
                    0f,
                    UnityEngine.Random.Range(-10f, 10f)
                )
            ));
        }
    }

    // Placeholder: in Unity 6, use the BakingSystem or SubScenes for prefab baking.
    // This method represents the concept — actual API depends on Entities package version.
    private Entity GameObjectConversionUtility_Unity6(GameObject prefab)
    {
        // Actual Unity 6 pattern:
        // var settings = GameObjectConversionSettings.FromWorld(
        //     World.DefaultGameObjectInjectionWorld, null);
        // return GameObjectConversionUtility.ConvertGameObjectHierarchy(prefab, settings);
        //
        // Or use a subscene with Baking and load it at runtime.
        // This stub returns Entity.Null to keep the example compilable without Entities package.
        return Entity.Null;
    }
}
```

---

### NativeArray Usage with Proper Disposal

Demonstrates the three allocator types and the `using` pattern for Temp allocations.

```csharp
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Demonstrates NativeArray allocator choices and disposal patterns.
/// Incorrect allocation/disposal causes hard errors in Unity 6 (not silent leaks).
/// </summary>
public class NativeArrayLifetimeExample : MonoBehaviour
{
    // ── Persistent: survives frame boundaries, must be manually disposed ──────
    private NativeArray<float3> _persistentData;

    private void Awake()
    {
        // Allocator.Persistent for data that lives across frames.
        _persistentData = new NativeArray<float3>(1000, Allocator.Persistent);
    }

    private void Update()
    {
        // ── TempJob: valid for up to 4 frames, intended for job lifetime ─────
        // Must be disposed within 4 frames. Use for per-frame job data that
        // doesn't need to persist.
        NativeArray<float> tempJobArray = new(100, Allocator.TempJob);

        var jobHandle = new SumJob { Data = tempJobArray, Result = _persistentData }.Schedule();

        // You cannot dispose TempJob arrays before the job completes.
        // Store the handle, complete next frame, then dispose.
        jobHandle.Complete();
        tempJobArray.Dispose(); // safe after Complete()

        // ── Temp: valid for current frame only ────────────────────────────────
        // Cannot be passed to a job. Fastest allocator.
        // Use the `using` statement to guarantee disposal on scope exit.
        using NativeArray<int> frameOnly = new(50, Allocator.Temp);
        for (int i = 0; i < frameOnly.Length; i++)
            frameOnly[i] = i * 2;
        // frameOnly.Dispose() called automatically by `using` at end of block.
    }

    private void OnDestroy()
    {
        // Persistent arrays must be explicitly disposed.
        // This is enforced: Unity 6 throws a hard error on leak detection.
        if (_persistentData.IsCreated)
            _persistentData.Dispose();
    }
}

// Minimal job used in the example above.
[Unity.Burst.BurstCompile]
public struct SumJob : IJob
{
    [Unity.Collections.ReadOnly] public NativeArray<float> Data;
    public NativeArray<float3> Result;

    public void Execute()
    {
        float sum = 0f;
        for (int i = 0; i < Data.Length; i++) sum += Data[i];
        // Write sum into first element of result as a demonstration.
        if (Result.Length > 0)
            Result[0] = new float3(sum, 0f, 0f);
    }
}
```

## Anti-Examples

### Full ECS for a Small Project

```csharp
// BAD — Full ECS world setup for 50 enemies on a mobile game.
// Cost: weeks of ramp-up, no Inspector authoring, debugging complexity,
// compatibility issues with third-party SDKs. Zero measurable runtime benefit.
public struct EnemyHealth : IComponentData { public float Value; }
public struct EnemyPosition : IComponentData { public float3 Value; }
[BurstCompile]
public partial struct EnemyUpdateSystem : ISystem { /* ... */ }

// GOOD — MonoBehaviour for 50 enemies is faster to build, easier to maintain,
// and runs at 60 fps with overhead to spare.
public class Enemy : MonoBehaviour
{
    [SerializeField] private float _health = 100f;
    private void Update() { /* standard logic */ }
}
```

---

### Adding Burst Without Measuring

```csharp
// BAD — Burst annotation added to a method called once per second.
// Burst has a JIT warm-up cost on first call. The method runs too infrequently
// to benefit. Adds complexity and Burst Inspector noise.
[BurstCompile]
public struct SpawnOneEnemyJob : IJob
{
    public void Execute() { /* called once per second */ }
}

// GOOD — Reserve Burst for hot paths called hundreds or thousands of times per frame.
// Verify benefit by comparing Profiler captures before and after adding [BurstCompile].
```

---

### Forgetting to Dispose NativeContainers

```csharp
// BAD — NativeArray allocated in a method, never disposed.
// In Unity 6, this is a hard error (not a warning): "A Native Collection has not
// been disposed, resulting in a memory leak."
private void SpawnEnemies()
{
    NativeArray<float3> positions = new(100, Allocator.Persistent);
    // ... use positions ...
    // Missing: positions.Dispose();
}

// GOOD — always pair allocation with disposal; use `using` for Temp scope.
private void SpawnEnemies()
{
    using NativeArray<float3> positions = new(100, Allocator.Temp);
    // positions.Dispose() called automatically at end of using block.
}

// GOOD — for Persistent arrays, dispose in OnDestroy.
private NativeArray<float3> _positions;
private void Awake()  => _positions = new NativeArray<float3>(100, Allocator.Persistent);
private void OnDestroy() { if (_positions.IsCreated) _positions.Dispose(); }
```

---

### Using ECS for UI or Complex Branching Logic

```csharp
// BAD — encoding UI state as IComponentData.
// ECS provides no benefit here: entity counts are tiny (one per UI element),
// there is no parallelism, and the code becomes far harder to read and debug.
public struct ButtonStateComponent : IComponentData
{
    public bool IsHovered;
    public bool IsPressed;
    public float4 CurrentColor;
}

// GOOD — UI belongs in MonoBehaviour / UI Toolkit.
// ECS shines for data-uniform, high-count, computation-heavy simulations.
// Menus, inventory screens, and settings panels are none of those things.
public class ButtonController : MonoBehaviour
{
    private bool _isHovered;
    private bool _isPressed;
}
```

## Cross-References

- Related skills: `hades:unity-performance` (CPU/GPU cost model, profiling workflow — profile before adopting ECS), `hades:unity-architect` (architecture trade-offs, when ECS fits the overall system design)
- Hades MCP tools used in this skill:
  - `find_components_using_pattern` — detect existing ECS components and systems
  - `get_project_summary` — project scale, platform target, team context
  - `search_by_name` — find existing Job or system files by naming convention
  - `recall_memory` — retrieve documented ECS/DOTS decisions
  - `propose_memory_update` — record new ECS architectural decisions
- Unity docs: [ECS overview](https://docs.unity3d.com/Packages/com.unity.entities@latest), [ISystem](https://docs.unity3d.com/Packages/com.unity.entities@latest/api/Unity.Entities.ISystem.html), [IJobParallelFor](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Unity.Jobs.IJobParallelFor.html), [Burst compiler](https://docs.unity3d.com/Packages/com.unity.burst@latest), [NativeArray](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Unity.Collections.NativeArray_1.html), [Baking](https://docs.unity3d.com/Packages/com.unity.entities@latest/manual/baking.html)
