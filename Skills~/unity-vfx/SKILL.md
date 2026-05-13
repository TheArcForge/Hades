---
description: "Use when implementing visual effects — VFX Graph vs legacy Particle System decisions, performance budgets for particles, LOD strategies for effects, and common VFX patterns."
---

# Unity VFX

Deep decision framework for visual effects in Unity. Covers VFX Graph vs legacy Particle System selection, per-effect performance budgets, LOD and culling strategies, common effect patterns, and pooling for frequent spawns. Profile overdraw first — transparent particles are the most common GPU bottleneck in Unity projects.

## When to Apply

Activate when the conversation involves:
- Choosing between VFX Graph and the legacy Particle System for a new effect
- Profiling or reducing GPU overdraw from particle effects
- Scaling particle counts or quality across device tiers
- Implementing LOD or culling for effects at distance
- Building reusable VFX patterns (impacts, trails, ambient, destruction)
- Pooling VFX instances to avoid Instantiate/Destroy cost
- Triggering effects from C# (SendEvent, parameter injection)

Do NOT activate for general rendering performance questions — those go to `hades:unity-performance`. For shader authoring inside VFX Graph, cross-reference `hades:unity-shaders-urp` or `hades:unity-shaders-hdrp`.

## Project Context Check

Before making recommendations, gather project-specific context so advice is calibrated to the actual codebase.

1. **Determine the render pipeline — this governs VFX Graph eligibility:**
   - Call `analyze_render_pipeline()` — VFX Graph requires URP or HDRP. If Built-in is returned, VFX Graph is not available and all advice must target the legacy Particle System. Do not suggest VFX Graph if this returns Built-in.

2. **Audit existing VFX usage:**
   - Call `find_components_using_pattern("ParticleSystem")` — inventories legacy particle systems in the scene. Large counts on mobile are an immediate concern for CPU emission cost.
   - Call `find_components_using_pattern("VisualEffect")` — inventories VFX Graph instances. Shows whether the team has already adopted VFX Graph.

3. **Check documented VFX decisions:**
   - Call `recall_memory("VFX particles effects")` — retrieves any particle budgets, platform limits, or effect decisions already recorded by the team.
   - If a new decision is reached, call `propose_memory_update` to record the budget or pattern for future reference.

4. **Adapt recommendations based on findings:**
   - Built-in pipeline → Particle System only; VFX Graph is off the table.
   - Large `ParticleSystem` count on mobile → immediate pooling and LOD audit required.
   - `VisualEffect` already in use → build on existing VFX Graph patterns rather than introducing a second system.

## Decision Framework

### DECISION: VFX Graph vs Particle System

```
What render pipeline does the project use?
├── Built-in (Legacy)
│   └── Particle System only — VFX Graph requires SRP
└── URP or HDRP
    ├── What is the target platform?
    │   ├── Mobile (iOS/Android)
    │   │   ├── Low/mid-tier → Particle System (CPU-simulated, well-optimized for mobile GPUs)
    │   │   └── High-end (recent iPhone Pro, flagship Android)
    │   │       └── VFX Graph may work — test GPU compute shader support first
    │   ├── Console / PC
    │   │   └── VFX Graph preferred for effects > 1,000 particles or GPU-driven needs
    │   └── WebGL
    │       └── Particle System only — VFX Graph requires compute shaders (not supported in WebGL)
    └── What is the particle count?
        ├── < 500 particles per effect → Particle System (simpler, lower overhead)
        ├── 500–5,000 → Either; VFX Graph starts winning here for GPU-driven effects
        └── > 5,000 → VFX Graph strongly preferred — CPU-simulated Particle System will stall
```

**VFX Graph strengths:**
- GPU-simulated: particle position, velocity, and lifetime computed entirely on the GPU. CPU cost is near-zero regardless of particle count.
- Rich visual controls via a node graph; no C# needed for the effect itself.
- Supports depth buffer collisions, shader graph integration, vector field sampling.
- Can render millions of particles at interactive frame rates on PC/console.

**Particle System strengths:**
- CPU-simulated: runs on every platform, including mobile and WebGL.
- Deep built-in module system (sub-emitters, trails, texture sheet animation) without custom shaders.
- Fine-grained per-particle C# access (`GetParticles` / `SetParticles`) for gameplay-driven effects.
- Lighter runtime overhead for small counts (< 200 particles).

---

### DECISION: Performance Budgets

Define budgets before building effects; profile against them on target hardware.

| Platform | Max particles per effect | Max concurrent effects | Overdraw budget |
|----------|--------------------------|------------------------|-----------------|
| Mobile (low-end) | 50 | 3 | Very low — prefer opaque or alpha-test |
| Mobile (high-end) | 200 | 6 | Low — limit transparent layer depth to 2 |
| Console / PC | 2,000 (PS/CPU) | 20 | Medium |
| PC (high-end, VFX Graph) | 100,000+ | depends on effect | Check fill rate in profiler |

**Overdraw is the primary mobile GPU killer:**
- Every transparent pixel drawn on top of another transparent pixel doubles the fragment shader cost for that screen area.
- Prefer alpha-test (clip-based cutout) over alpha-blend where visual quality allows — alpha-test participates in early-Z and avoids sort overhead.
- Reduce particle size rather than increasing count; smaller particles cover fewer pixels.
- Use the Scene view's Overdraw visualization mode (top-left shading mode dropdown) to spot hot spots.

**Soft particles:**
- Soft particles sample the depth buffer to fade where particles intersect geometry.
- Costs one additional texture sample per fragment.
- Disable on mobile by default; only enable if intersection artifacts are pronounced and the budget permits.

---

### DECISION: LOD for Effects

Effects seen from a distance do not need full fidelity. Distance-based scaling and culling avoid GPU work that is invisible to the player.

```
Is the effect visible at distance?
├── Always near-camera (muzzle flash, UI, hit confirm)
│   └── No LOD needed — always full quality
├── Mid-range (within 30 m typically visible)
│   └── LOD group with 2 tiers: full and half
│       ├── Full (0–15 m): normal emission rate, full particle lifetime
│       └── Half (15–30 m): 50% emission rate, shorter lifetime
└── Background / ambient (30 m+)
    └── LOD group with culling tier
        ├── Reduced (15–30 m): 25% emission rate
        └── Culled (> 30 m): effect disabled entirely
```

**Culling modes for Particle Systems:**
- `Automatic` — Unity pauses simulation when off-screen (default; prefer this).
- `AlwaysSimulate` — simulates off-screen; needed for effects that must be accurate when they re-enter the frustum (e.g., fire that catches something off-screen).
- `PauseAndCatchup` — pauses off-screen; on re-enter, fast-forwards to the current time. Expensive on re-entry.

**VFX Graph culling:**
- VFX Graph instances respect the `VisualEffect.cullingFlags` setting.
- Set `VFXCullingFlags.CullSimulation` for background effects that should pause off-screen.
- Set `VFXCullingFlags.RecomputeBoundsAfterCulling` for effects whose bounds change significantly (e.g., expanding explosion).

---

### Common Effect Patterns

**Impact / hit confirm:**
- One-shot burst; short lifetime (0.1–0.3 s); small particle count (10–30).
- Pool aggressively — impacts fire many times per second in combat-heavy games.
- Use `ParticleSystem.Play()` on a pooled instance rather than Instantiate.

**Trail / ribbon:**
- VFX Graph: output mesh strip or ribbon renderer.
- Particle System: Trail module with `Ribbon` mode.
- Cap max trail length or lifetime to avoid unbounded vertex growth.

**Ambient / idle (fire, smoke, dust):**
- Continuous emission; moderate count; looping.
- Apply LOD culling beyond 30 m.
- For smoke: use a flipbook texture atlas (4×4 or 8×8 frames) rather than many small particles — fewer draw calls, lower overdraw.

**Destruction / explosion:**
- Burst emission; multiple sub-effects (debris, smoke, flash, shockwave ring).
- Stagger sub-effect spawns by 50–100 ms to spread peak GPU cost across frames.
- Return to pool when all sub-effects have finished (`ParticleSystem.isStopped` / VFX Graph alive count == 0).

---

### Pooling VFX Instances

Particle effects that fire frequently (impacts, projectile trails) must be pooled. `Instantiate`/`Destroy` per impact causes visible frame spikes on mobile.

```
Does this effect fire more than once every 2 seconds?
├── No → Instantiate/Destroy is acceptable
└── Yes → Pool the effect instance
    ├── Pre-warm during load (not Awake of the first scene)
    ├── On Get: call Play() after positioning; reset all modules
    └── On Return: wait for isStopped / alive count == 0, then disable
```

## Code Examples

### Particle System Configuration via Script

Complete component that configures emission, shape, and lifetime at runtime — useful for procedurally tuned effects.

```csharp
using UnityEngine;

/// <summary>
/// Configures a Particle System's key modules at runtime.
/// Attach alongside a ParticleSystem component.
/// Call Configure() from a spawner or pool Get() callback to tune the
/// effect to the context (e.g., scale damage-based explosion size).
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class RuntimeParticleConfigurator : MonoBehaviour
{
    private ParticleSystem _ps;
    private ParticleSystem.MainModule _main;
    private ParticleSystem.EmissionModule _emission;
    private ParticleSystem.ShapeModule _shape;

    private void Awake()
    {
        _ps       = GetComponent<ParticleSystem>();
        _main     = _ps.main;
        _emission = _ps.emission;
        _shape    = _ps.shape;
    }

    /// <summary>
    /// Apply runtime configuration to this effect.
    /// Call before Play() to override authored values.
    /// </summary>
    /// <param name="burstCount">Particles in the initial burst.</param>
    /// <param name="lifetimeSeconds">How long each particle lives.</param>
    /// <param name="radius">Sphere emission radius in world units.</param>
    /// <param name="startSpeed">Initial speed multiplier.</param>
    public void Configure(int burstCount, float lifetimeSeconds, float radius, float startSpeed)
    {
        // Main module: lifetime and speed
        _main.startLifetime  = lifetimeSeconds;
        _main.startSpeed     = startSpeed;
        _main.stopAction     = ParticleSystemStopAction.Callback; // fires OnParticleSystemStopped

        // Emission module: replace continuous rate with a one-shot burst
        _emission.rateOverTime = 0f; // disable continuous emission
        _emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(0f, (short)burstCount)
        });

        // Shape module: sphere around impact point
        _shape.shapeType = ParticleSystemShapeType.Sphere;
        _shape.radius    = radius;
    }

    /// <summary>
    /// Play the effect. Call after Configure().
    /// </summary>
    public void Play()
    {
        _ps.Play();
    }

    /// <summary>
    /// Called by Unity when all particles have died and stopAction == Callback.
    /// Override or subscribe to this to return the instance to a pool.
    /// </summary>
    private void OnParticleSystemStopped()
    {
        // Notify a pool manager that this instance is ready for return.
        // Replace with your pool return call:
        // VFXPoolManager.Instance.Return(this);
        gameObject.SetActive(false);
    }
}
```

---

### VFX Graph Event Triggering from C#

Complete component that triggers a VFX Graph effect via named events and injects float and Vector3 parameters.

```csharp
using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// Drives a VFX Graph VisualEffect component from C#.
/// Attach alongside a VisualEffect component.
///
/// In your VFX Graph, expose:
///   - An Event named "OnImpact" (or whatever ImpactEventName is set to)
///   - A float property named "ImpactStrength"
///   - A Vector3 property named "ImpactNormal"
///
/// Then call TriggerImpact() from a collision handler or damage system.
/// </summary>
[RequireComponent(typeof(VisualEffect))]
public class VFXGraphImpactController : MonoBehaviour
{
    [Tooltip("Must match the Event name defined in the VFX Graph asset.")]
    [SerializeField] private string _impactEventName = "OnImpact";

    [Tooltip("VFX Graph float property name for impact strength (0–1).")]
    [SerializeField] private string _strengthPropertyName = "ImpactStrength";

    [Tooltip("VFX Graph Vector3 property name for surface normal direction.")]
    [SerializeField] private string _normalPropertyName = "ImpactNormal";

    private VisualEffect _vfx;

    // Cache the event name as an integer ID — cheaper than passing a string each frame.
    private int _impactEventId;

    private void Awake()
    {
        _vfx = GetComponent<VisualEffect>();
        _impactEventId = Shader.PropertyToID(_impactEventName);
    }

    /// <summary>
    /// Trigger the impact effect at the given world position with a surface normal.
    /// </summary>
    /// <param name="worldPosition">Where to move the VFX before firing.</param>
    /// <param name="surfaceNormal">Surface normal of the hit — drives spark direction.</param>
    /// <param name="strength">Normalized damage or impact magnitude (0–1).</param>
    public void TriggerImpact(Vector3 worldPosition, Vector3 surfaceNormal, float strength)
    {
        transform.position = worldPosition;

        // Inject parameters before sending the event so the graph reads them
        // on the same frame the event fires.
        _vfx.SetFloat(_strengthPropertyName, Mathf.Clamp01(strength));
        _vfx.SetVector3(_normalPropertyName, surfaceNormal.normalized);

        // SendEvent fires the named event into the VFX Graph.
        // The graph's Initialize context must listen for this event.
        _vfx.SendEvent(_impactEventId);
    }

    /// <summary>
    /// Reset the effect, clearing all alive particles immediately.
    /// Call this when returning the instance to a pool.
    /// </summary>
    public void ResetEffect()
    {
        _vfx.Reinit();
    }
}
```

---

### Effect Pool for Frequent VFX

Generic pool sized for high-frequency VFX. Returns instances automatically when the effect finishes.

```csharp
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pool for Particle System-based one-shot effects (impacts, sparks, etc.).
/// Pre-warms on Awake; auto-returns instances when their ParticleSystem stops.
///
/// Usage:
///   var instance = ImpactEffectPool.Instance.Get(hitPoint, hitNormal);
///   // No manual return needed — the pool detects when the PS stops.
/// </summary>
public class ImpactEffectPool : MonoBehaviour
{
    public static ImpactEffectPool Instance { get; private set; }

    [SerializeField] private PooledImpactEffect _prefab;
    [SerializeField] private int _initialSize   = 20;
    [SerializeField] private int _maxSize        = 40;

    private readonly Stack<PooledImpactEffect> _available = new();
    private int _liveCount;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        PreWarm();
    }

    private void PreWarm()
    {
        for (int i = 0; i < _initialSize; i++)
            _available.Push(CreateInstance());
    }

    /// <summary>
    /// Get an impact effect at the given world position, oriented to the surface normal.
    /// Returns null if the pool cap is reached.
    /// </summary>
    public PooledImpactEffect Get(Vector3 position, Vector3 normal)
    {
        if (_available.Count == 0)
        {
            if (_liveCount >= _maxSize)
            {
                Debug.LogWarning("[ImpactEffectPool] Cap reached; dropping impact VFX.");
                return null;
            }
            _available.Push(CreateInstance());
        }

        PooledImpactEffect instance = _available.Pop();
        instance.transform.SetPositionAndRotation(position, Quaternion.LookRotation(normal));
        instance.gameObject.SetActive(true);
        instance.Play();
        _liveCount++;
        return instance;
    }

    internal void Return(PooledImpactEffect instance)
    {
        instance.gameObject.SetActive(false);
        instance.transform.SetParent(transform);
        _available.Push(instance);
        _liveCount--;
    }

    private PooledImpactEffect CreateInstance()
    {
        var obj = Instantiate(_prefab, transform);
        obj.gameObject.SetActive(false);
        obj.Initialize(this);
        return obj;
    }
}

/// <summary>
/// Attach this alongside a ParticleSystem on a pooled effect prefab.
/// Automatically returns to the pool when the Particle System stops.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class PooledImpactEffect : MonoBehaviour
{
    private ParticleSystem _ps;
    private ImpactEffectPool _pool;

    public void Initialize(ImpactEffectPool pool)
    {
        _ps   = GetComponent<ParticleSystem>();
        _pool = pool;

        // Configure stop action so Unity calls OnParticleSystemStopped automatically.
        var main      = _ps.main;
        main.stopAction = ParticleSystemStopAction.Callback;
    }

    public void Play()
    {
        _ps.Clear();
        _ps.Play();
    }

    // Unity calls this via SendMessage when stop action == Callback.
    private void OnParticleSystemStopped()
    {
        _pool?.Return(this);
    }
}
```

---

### LOD Component for Particle Effects

Distance-based emission rate scaling using a simple MonoBehaviour — no LODGroup required.

```csharp
using UnityEngine;

/// <summary>
/// Scales a Particle System's emission rate based on camera distance.
/// Attach alongside a ParticleSystem that is ambient or background (fire, smoke, etc.).
///
/// Tier configuration:
///   Full quality:    distance &lt;= FullDistance
///   Half quality:    distance &lt;= HalfDistance
///   Quarter quality: distance &lt;= CullDistance
///   Culled:          distance &gt;  CullDistance
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class ParticleSystemLOD : MonoBehaviour
{
    [Header("Distance Thresholds (world units)")]
    [SerializeField] private float _fullDistance    = 15f;
    [SerializeField] private float _halfDistance    = 30f;
    [SerializeField] private float _cullDistance    = 60f;

    [Header("Check Interval")]
    [Tooltip("How often (seconds) to re-evaluate LOD. 0.1 is fine for most effects.")]
    [SerializeField] private float _checkInterval   = 0.1f;

    private ParticleSystem _ps;
    private ParticleSystem.EmissionModule _emission;
    private float _baseRateOverTime;
    private Transform _cameraTransform;
    private float _nextCheckTime;
    private int _currentTier = -1; // -1 forces evaluation on first tick

    private void Awake()
    {
        _ps              = GetComponent<ParticleSystem>();
        _emission        = _ps.emission;
        _baseRateOverTime = _emission.rateOverTime.constant;
    }

    private void Start()
    {
        if (Camera.main != null)
            _cameraTransform = Camera.main.transform;
    }

    private void Update()
    {
        if (Time.time < _nextCheckTime) return;
        _nextCheckTime = Time.time + _checkInterval;

        if (_cameraTransform == null) return;

        float distance = Vector3.Distance(transform.position, _cameraTransform.position);
        int tier = DistanceToTier(distance);

        if (tier == _currentTier) return; // no change — skip
        _currentTier = tier;
        ApplyTier(tier);
    }

    private int DistanceToTier(float distance)
    {
        if (distance <= _fullDistance)    return 0; // full
        if (distance <= _halfDistance)    return 1; // half
        if (distance <= _cullDistance)    return 2; // quarter
        return 3;                                    // culled
    }

    private void ApplyTier(int tier)
    {
        switch (tier)
        {
            case 0: // full quality
                _emission.rateOverTime = _baseRateOverTime;
                if (!_ps.isPlaying) _ps.Play();
                break;

            case 1: // half quality
                _emission.rateOverTime = _baseRateOverTime * 0.5f;
                if (!_ps.isPlaying) _ps.Play();
                break;

            case 2: // quarter quality
                _emission.rateOverTime = _baseRateOverTime * 0.25f;
                if (!_ps.isPlaying) _ps.Play();
                break;

            case 3: // culled
                _ps.Stop(withChildren: true, stopBehavior: ParticleSystemStopBehavior.StopEmitting);
                break;
        }
    }
}
```

## Anti-Examples

### VFX Graph on Mobile without Compute Shader Check

```csharp
// BAD — VFX Graph silently falls back or fails on devices that don't support
// compute shaders. Never assume mobile supports VFX Graph.

// On an Adreno 530 or lower, SystemInfo.supportsComputeShaders returns false.
// The VisualEffect component will produce nothing with no editor warning.

[SerializeField] private GameObject _vfxGraphObject; // contains VisualEffect component

private void SpawnImpact(Vector3 pos)
{
    Instantiate(_vfxGraphObject, pos, Quaternion.identity); // may render nothing on device
}

// GOOD — check support at startup and fall back to a Particle System prefab.
[SerializeField] private GameObject _vfxGraphPrefab;
[SerializeField] private GameObject _particleSystemFallbackPrefab;

private GameObject _effectPrefab;

private void Awake()
{
    _effectPrefab = SystemInfo.supportsComputeShaders
        ? _vfxGraphPrefab
        : _particleSystemFallbackPrefab;
}

private void SpawnImpact(Vector3 pos)
{
    // Now pool this prefab via a pool manager — never Instantiate per impact.
    ImpactEffectPool.Instance.Get(pos, Vector3.up);
}
```

---

### Thousands of Overlapping Transparent Particles

```csharp
// BAD — a smoke effect with 5,000 large, overlapping, alpha-blended quads.
// Each screen pixel in the smoke cloud is shaded 20-40 times (extreme overdraw).
// On mobile, this alone can drop frames to single digits.
var main = smokePs.main;
main.maxParticles = 5000;    // way too many for large quads
var shape = smokePs.shape;
shape.radius = 0.5f;         // tight spawn = maximum overlap = maximum overdraw

// GOOD — use fewer, larger flipbook particles with alpha-test cutout shader.
// 30 flipbook particles cover the same visual area as 300 small alpha-blend quads
// with a fraction of the overdraw cost.
main.maxParticles = 30;
// Assign a material using a cutout/alpha-test shader:
// Shader Graph: set Surface Type to "Opaque", use Alpha Clip threshold.
// Flipbook module: 4x4 texture sheet, random start frame, smooth blending OFF.
```

---

### Instantiate/Destroy for Every Impact

```csharp
// BAD — spawning and destroying a particle system object for each bullet impact.
// In a scene with 20 shooting enemies, this is 20+ Instantiate+Destroy calls/s.
// Each Instantiate stalls the main thread while Unity clones the hierarchy.
private void OnCollisionEnter(Collision col)
{
    GameObject fx = Instantiate(_impactPrefab, col.contacts[0].point, Quaternion.identity);
    Destroy(fx, 2f); // GC timer; still blocks GC
}

// GOOD — pool the effect; return it when the ParticleSystem stops.
private void OnCollisionEnter(Collision col)
{
    ContactPoint contact = col.contacts[0];
    ImpactEffectPool.Instance.Get(contact.point, contact.normal);
    // No Destroy — the PooledImpactEffect returns itself automatically.
}
```

---

### Setting Particle Properties via GetComponent Every Frame

```csharp
// BAD — querying the ParticleSystem module struct and modifying it inside Update.
// ParticleSystem module accessors allocate a new struct copy each access when
// called repeatedly without caching the module reference.
private void Update()
{
    // Each line below fetches a fresh module struct — O(n) property lookup.
    GetComponent<ParticleSystem>().main.startSpeed    = _currentWindSpeed;   // stale struct
    GetComponent<ParticleSystem>().emission.rateOverTime = _emissionRate;    // stale struct
}

// GOOD — cache modules in Awake(); modify the cached struct (it's a handle, not a copy).
private ParticleSystem.MainModule _main;
private ParticleSystem.EmissionModule _emission;

private void Awake()
{
    var ps     = GetComponent<ParticleSystem>();
    _main      = ps.main;      // cached once — this IS the live module
    _emission  = ps.emission;  // cached once
}

private void Update()
{
    _main.startSpeed          = _currentWindSpeed; // modifies the live module in-place
    _emission.rateOverTime    = _emissionRate;
}
```

## Cross-References

- Related skills: `hades:unity-performance` (overdraw, draw call budgets, GPU cost model), `hades:unity-shaders-urp` (URP Shader Graph for VFX materials), `hades:unity-shaders-hdrp` (HDRP Shader Graph for VFX materials)
- Hades MCP tools used in this skill:
  - `analyze_render_pipeline` — confirm URP/HDRP before recommending VFX Graph
  - `find_components_using_pattern` — audit existing ParticleSystem and VisualEffect usage
  - `recall_memory` — documented VFX budgets and decisions
  - `propose_memory_update` — record new VFX decisions for the team
- Unity docs: [VFX Graph](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@latest), [Particle System](https://docs.unity3d.com/6000.0/Documentation/Manual/ParticleSystems.html), [VisualEffect API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/VFX.VisualEffect.html), [ParticleSystem.EmissionModule](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ParticleSystem.EmissionModule.html), [VFXCullingFlags](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@latest/api/UnityEngine.VFX.VFXCullingFlags.html)
