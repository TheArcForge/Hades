---
description: "Use when managing asset loading — Addressables vs Resources vs direct references, async loading patterns, memory management, group strategies, and content update workflows."
---

# Unity Addressables

Deep decision framework for asset loading and memory management in Unity. Covers when to adopt Addressables over Resources or direct references, group design, async loading with `AsyncOperationHandle`, memory lifecycle (load → use → release), ref-counting patterns, and content update workflows via CCD. Untracked handles are the most common source of memory leaks in Addressables projects.

## When to Apply

Activate when the conversation involves:
- Deciding whether to use Addressables, Resources, or direct serialized references for an asset
- Loading or instantiating an asset at runtime from a path or label
- Tracking and releasing `AsyncOperationHandle` instances to avoid memory leaks
- Designing Addressable groups (by scene, feature, update frequency)
- Preloading assets before a scene or gameplay segment begins
- Streaming downloadable content or supporting remote catalogs
- Diagnosing unexpectedly high memory from loaded-but-unreleased assets

Do NOT activate for in-scene prefab wire-up where the asset is always needed and always loaded — that belongs to `hades:prefab-architecture`. For questions about how scenes themselves are structured and loaded, see `hades:scene-architecture`.

## Project Context Check

Before making recommendations, gather project-specific context so advice is calibrated to the actual codebase.

1. **Check if Addressables is already in use:**
   - Call `search_by_name("*AddressableAsset*")` — finds AddressableAssetSettings, AddressableAssetGroup, and AddressableAssetData files. If none exist, the project has not adopted Addressables yet; adjust the scope of advice accordingly.
   - Call `find_components_using_pattern("AssetReference")` — locates MonoBehaviours with `AssetReference` fields, indicating Addressables is already wired into gameplay code.

2. **Assess project scale:**
   - Call `get_project_summary()` — reveals total asset count, scene count, and build targets. Addressables provide the most value in large projects (many assets) or projects with remote content. Small prototypes rarely justify the setup cost.

3. **Check documented asset strategy:**
   - Call `recall_memory("addressables asset loading memory")` — retrieves any documented group strategies, memory budgets, or loading decisions the team has already made.
   - If a new decision is reached, call `propose_memory_update` to persist the strategy for future reference.

4. **Adapt recommendations based on findings:**
   - No AddressableAssetSettings found → evaluate whether the project warrants adopting Addressables before advising on group design.
   - Small project (< 50 assets, single scene) → direct references or Resources may be the right call; skip Addressables.
   - Remote content or DLC in scope → Addressables are mandatory; apply CCD group strategy.
   - `AssetReference` already in use → extend existing patterns; do not introduce a second loading system.

## Decision Framework

### DECISION: Addressables vs Resources vs Direct Reference

```
Does the asset need to be loaded dynamically at runtime
(its identity or timing is not known at author time)?
├── No — the asset is always needed and always known
│   └── Direct serialized reference ([SerializeField] MyType _field)
│       ├── Unity loads it with the scene or prefab automatically
│       ├── Zero runtime loading code required
│       └── Use for: character models, level geometry, UI prefabs, audio clips
│           that are always active when their scene is active
│
└── Yes — the asset is loaded on demand
    ├── Is the project a prototype or the asset set is tiny (< 20 assets)?
    │   └── Resources.Load is acceptable for now
    │       ├── Simple; no setup required
    │       └── MIGRATE to Addressables before ship — Resources cannot be
    │           patched post-launch and bypasses build stripping
    │
    └── Full game / production / remote content
        └── Addressables
            ├── Large asset catalog (hundreds of assets across many scenes)
            ├── Need async streaming to avoid load-screen spikes
            ├── DLC / live content updates via remote catalog
            ├── Fine-grained memory control (load only what's needed now)
            └── Platform build size constraints (on-demand download)
```

**Why Resources is problematic at scale:**
- Everything in a `Resources/` folder is included in every build, regardless of whether the player ever loads it. Build size grows unbounded.
- `Resources.Load` is synchronous — it stalls the main thread.
- No content update path after ship. Patches that change a Resource asset require a full binary update.
- Unity's build stripping cannot remove assets inside `Resources/`.

**Why direct references are underused:**
- For assets that are always loaded with a scene, a `[SerializeField]` reference is zero-cost after the scene loads. No handle tracking, no release calls, no async state machine.
- Only reach for Addressables when you genuinely need the extra control.

---

### DECISION: Group Strategy

Addressable groups map to asset bundles. Group design determines what loads together, how much memory is used at once, and which assets can be updated independently.

```
How is this asset used?
├── Always needed for a specific scene (level art, terrain, NPCs)
│   └── Per-scene group — one group per major scene
│       Loading the scene label loads exactly what that scene needs.
│
├── Shared across multiple scenes (UI atlas, common SFX, shared materials)
│   └── Shared-content group — one group for cross-scene assets
│       Separate from scene groups to avoid duplicating them into every bundle.
│
├── Loaded based on player action (shop items, cosmetics, streamed levels)
│   └── Feature group — one group per feature or content category
│       (e.g., "Cosmetics", "Shop", "AltLevel_Forest")
│
└── Updated post-launch (live event content, DLC)
    └── Remote group — hosted on CDD or custom CDN
        Mark the group's Build & Load Path as Remote.
        Keep stable core content in local groups; only volatile content goes remote.
```

**Bundle granularity rules:**
- One asset per group = maximum flexibility, maximum bundle overhead. Use only for very large standalone assets (a full cinematic video, a large music track).
- 10–50 related assets per group = good balance. Related assets load together, bundle count stays manageable.
- Hundreds of unrelated assets in one group = too coarse. One changed asset invalidates the entire bundle download.
- Duplicate assets (same asset referenced from two groups without being extracted to a shared group) bloat build size. Run the Analyze tool in the Addressables window before every release build.

---

### DECISION: Memory Management and Handle Lifecycle

Every `AsyncOperationHandle` that loads an asset must be released. Forgetting to release is the primary cause of memory leaks in Addressables projects.

```
Are you loading an asset?
├── Store the handle — you need it to release later.
│   AsyncOperationHandle<T> handle = Addressables.LoadAssetAsync<T>(key);
│
├── Awaiting the load?
│   └── await handle.Task — or use a coroutine with yield return handle
│
├── Done with the asset?
│   └── Addressables.Release(handle) — decrements internal ref count
│       ├── If ref count reaches 0 → asset is unloaded from memory
│       └── If you lost the handle reference → asset leaks until scene unload
│
└── Instantiating a prefab?
    └── Use Addressables.InstantiateAsync — NOT Addressables.LoadAssetAsync + Instantiate
        ├── InstantiateAsync tracks the instance's lifetime
        └── Release with Addressables.ReleaseInstance(gameObject) to unload correctly
```

**Ref-counting model:**
- Each `LoadAssetAsync` call for the same key increments a counter.
- Each `Release` decrements it. The asset unloads only when the count reaches zero.
- Loading the same asset from 10 places without releasing 9 of them keeps it in memory permanently.
- Implication: every load site must own its handle and release it when the owning object is destroyed.

---

### Content Update Workflow (CCD)

For live games that need post-launch asset patches:

1. Tag assets that may change as **Remote** group members (remote Build & Load paths).
2. On release build, run `Build → New Build → Default Build Script` — produces catalog and bundles.
3. To update: change assets, run `Build → Update a Previous Build`, point at the previous content state binary.
4. Upload the resulting bundles and updated catalog to your CDN (Unity CCD or custom).
5. Clients download the new catalog on startup and load updated bundles from remote on demand.

**What cannot be patched via content updates:**
- C# code changes — those require a binary update.
- Scene structural changes (if the scene itself is not in an Addressable group).
- Changes to assets in Local groups — those are baked into the binary.

## Code Examples

### Addressable Asset Loading with Handle Tracking and Release

Full component that loads an asset async, uses it, and releases it correctly on destroy.

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Loads a single Addressable asset by key (address or label), uses it,
/// and releases the handle when the component is destroyed.
///
/// Pattern: one component owns one handle. Never share handles across owners.
/// </summary>
public class AddressableAssetLoader : MonoBehaviour
{
    [Tooltip("Addressable address or label string for the asset to load.")]
    [SerializeField] private string _assetKey;

    // Store the handle as a field — it must outlive the await so we can release it.
    private AsyncOperationHandle<GameObject> _handle;
    private GameObject _loadedInstance;

    private async void Start()
    {
        await LoadAndSpawnAsync();
    }

    private async Task LoadAndSpawnAsync()
    {
        _handle = Addressables.LoadAssetAsync<GameObject>(_assetKey);

        // Await the operation. Does not block the main thread.
        await _handle.Task;

        if (_handle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"[AddressableAssetLoader] Failed to load '{_assetKey}': {_handle.OperationException}");
            // Release even on failure — Addressables tracks failed ops too.
            Addressables.Release(_handle);
            return;
        }

        // Instantiate via normal Unity path after the asset is loaded.
        // Note: to release correctly, use Addressables.ReleaseInstance instead if you
        // used InstantiateAsync (see the InstantiateAsync example below).
        _loadedInstance = Instantiate(_handle.Result, transform.position, Quaternion.identity);
    }

    private void OnDestroy()
    {
        // Release the asset reference. If this was the last owner, the asset is unloaded.
        // NEVER skip this — it is the primary cause of Addressable memory leaks.
        if (_handle.IsValid())
            Addressables.Release(_handle);
    }
}
```

---

### AssetReference Field with Async Instantiation

`AssetReference` fields let designers assign Addressable assets directly in the Inspector without hardcoding keys.

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Demonstrates AssetReference — the designer-friendly way to reference an
/// Addressable asset from the Inspector. No address string required.
///
/// Assign the prefab in the Inspector by dragging an Addressable asset onto
/// the _enemyPrefabRef field.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [Tooltip("Assign an Addressable prefab asset here — drag from the Project window.")]
    [SerializeField] private AssetReferenceGameObject _enemyPrefabRef;

    [SerializeField] private int _spawnCount = 5;
    [SerializeField] private float _spawnRadius = 10f;

    // Track instances so we can release them individually.
    private readonly System.Collections.Generic.List<GameObject> _spawnedInstances = new();

    public async Task SpawnEnemiesAsync()
    {
        if (!_enemyPrefabRef.RuntimeKeyIsValid())
        {
            Debug.LogError("[EnemySpawner] AssetReference is not set or invalid.");
            return;
        }

        for (int i = 0; i < _spawnCount; i++)
        {
            Vector3 spawnPos = transform.position + Random.insideUnitSphere * _spawnRadius;
            spawnPos.y = transform.position.y; // keep on the ground plane

            // InstantiateAsync loads the asset AND creates a new instance.
            // Each call creates one tracked instance. Release with ReleaseInstance.
            AsyncOperationHandle<GameObject> handle =
                Addressables.InstantiateAsync(_enemyPrefabRef, spawnPos, Quaternion.identity);

            await handle.Task;

            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                _spawnedInstances.Add(handle.Result);
            }
            else
            {
                Debug.LogError($"[EnemySpawner] Spawn {i} failed: {handle.OperationException}");
                if (handle.IsValid()) Addressables.Release(handle);
            }
        }
    }

    /// <summary>
    /// Destroy all spawned instances and release their Addressable references.
    /// Call this when the combat encounter ends and enemies are no longer needed.
    /// </summary>
    public void DespawnAll()
    {
        foreach (GameObject instance in _spawnedInstances)
        {
            if (instance != null)
                // ReleaseInstance destroys the GameObject AND decrements the ref count.
                // Do NOT use Destroy() — it skips the ref-count decrement.
                Addressables.ReleaseInstance(instance);
        }
        _spawnedInstances.Clear();
    }

    private void OnDestroy() => DespawnAll();
}
```

---

### Asset Preloader (Load Group Before Scene Entry)

Load an entire Addressable label before the player enters a scene to avoid in-gameplay streaming hitches.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

/// <summary>
/// Preloads all assets with a given Addressable label before scene entry.
/// Attach to a loading screen manager. Call PreloadAsync() with a progress
/// callback to drive a loading bar.
///
/// Example: label "Level_Forest" loads all meshes, textures, and audio
/// required by the Forest level before transitioning.
/// </summary>
public class AddressablePreloader : MonoBehaviour
{
    // Track all open handles so we can release them when the scene unloads.
    private readonly List<AsyncOperationHandle> _activeHandles = new();

    /// <summary>
    /// Preload all assets for the given label. Reports progress 0→1 via onProgress.
    /// Throws if any asset fails to load.
    /// </summary>
    public async Task PreloadAsync(string label, Action<float> onProgress = null)
    {
        // Step 1: Resolve the locations (the list of asset keys under this label).
        AsyncOperationHandle<IList<IResourceLocation>> locationHandle =
            Addressables.LoadResourceLocationsAsync(label);

        await locationHandle.Task;

        if (locationHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Addressables.Release(locationHandle);
            throw new Exception($"[AddressablePreloader] Failed to load locations for label '{label}'.");
        }

        IList<IResourceLocation> locations = locationHandle.Result;
        int total   = locations.Count;
        int loaded  = 0;

        // Step 2: Load each asset, tracking progress.
        var loadTasks = new List<Task>(total);

        foreach (IResourceLocation location in locations)
        {
            AsyncOperationHandle<object> assetHandle =
                Addressables.LoadAssetAsync<object>(location);

            _activeHandles.Add(assetHandle);

            Task trackingTask = assetHandle.Task.ContinueWith(_ =>
            {
                loaded++;
                onProgress?.Invoke((float)loaded / total);
            }, TaskScheduler.FromCurrentSynchronizationContext());

            loadTasks.Add(trackingTask);
        }

        await Task.WhenAll(loadTasks);

        // Release the location handle — we no longer need the location list.
        Addressables.Release(locationHandle);

        Debug.Log($"[AddressablePreloader] Preloaded {loaded}/{total} assets for label '{label}'.");
    }

    /// <summary>
    /// Release all handles acquired during preload.
    /// Call this when the scene that consumed the preloaded assets is unloaded.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (AsyncOperationHandle handle in _activeHandles)
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
        _activeHandles.Clear();
    }

    private void OnDestroy() => ReleaseAll();
}
```

---

### Memory-Safe Loading with Ref Counting

Pattern for shared assets loaded by multiple owners, using manual ref-counting to ensure the asset unloads exactly once when all owners release it.

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Shared Addressable asset cache with explicit ref-counting.
/// Multiple callers can request the same asset; the underlying handle is shared.
/// The asset is released only when all callers have decremented their ref.
///
/// Usage:
///   var tex = await SharedAssetCache.Instance.AcquireAsync&lt;Texture2D&gt;("UI/PlayerIcon");
///   // ...use tex...
///   SharedAssetCache.Instance.Release("UI/PlayerIcon");
/// </summary>
public class SharedAssetCache : MonoBehaviour
{
    public static SharedAssetCache Instance { get; private set; }

    private readonly Dictionary<string, AsyncOperationHandle> _handles  = new();
    private readonly Dictionary<string, int>                  _refCounts = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>
    /// Acquire an asset. Increments the ref count. Caller MUST call Release() when done.
    /// Returns null if the load fails.
    /// </summary>
    public async Task<T> AcquireAsync<T>(string key) where T : UnityEngine.Object
    {
        if (_handles.TryGetValue(key, out AsyncOperationHandle existing))
        {
            _refCounts[key]++;
            // Await in case a previous caller is mid-load.
            await existing.Task;
            return existing.Result as T;
        }

        // First request — load it.
        AsyncOperationHandle<T> handle = Addressables.LoadAssetAsync<T>(key);
        _handles[key]   = handle;
        _refCounts[key] = 1;

        await handle.Task;

        if (handle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"[SharedAssetCache] Failed to load '{key}': {handle.OperationException}");
            _handles.Remove(key);
            _refCounts.Remove(key);
            if (handle.IsValid()) Addressables.Release(handle);
            return null;
        }

        return handle.Result;
    }

    /// <summary>
    /// Decrement the ref count for this key. Releases the handle when count reaches zero.
    /// Every AcquireAsync call must have exactly one matching Release call.
    /// </summary>
    public void Release(string key)
    {
        if (!_refCounts.TryGetValue(key, out int count))
        {
            Debug.LogWarning($"[SharedAssetCache] Release called for unknown key '{key}'.");
            return;
        }

        count--;

        if (count <= 0)
        {
            if (_handles.TryGetValue(key, out AsyncOperationHandle handle) && handle.IsValid())
                Addressables.Release(handle);

            _handles.Remove(key);
            _refCounts.Remove(key);
        }
        else
        {
            _refCounts[key] = count;
        }
    }

    private void OnDestroy()
    {
        foreach (KeyValuePair<string, AsyncOperationHandle> kvp in _handles)
        {
            if (kvp.Value.IsValid())
                Addressables.Release(kvp.Value);
        }
        _handles.Clear();
        _refCounts.Clear();
    }
}
```

---

### Addressable Scene Loading

Load and unload a scene via Addressables — required when the scene's assets are in Addressable groups.

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

/// <summary>
/// Loads and unloads scenes via Addressables.
/// Use this instead of SceneManager.LoadSceneAsync when the scene or its
/// dependencies are in Addressable groups (e.g., the scene is a DLC level).
///
/// Scenes loaded via Addressables must be unloaded via Addressables.UnloadSceneAsync —
/// NOT SceneManager.UnloadSceneAsync — or the bundle stays in memory.
/// </summary>
public class AddressableSceneLoader : MonoBehaviour
{
    [SerializeField] private AssetReference _sceneReference;

    private AsyncOperationHandle<SceneInstance> _loadHandle;
    private bool _isLoaded;

    /// <summary>
    /// Load the scene additively. Reports progress 0→1 during load.
    /// The scene is added on top of the current scene (LoadSceneMode.Additive).
    /// </summary>
    public async Task LoadAsync()
    {
        if (_isLoaded)
        {
            Debug.LogWarning("[AddressableSceneLoader] Scene is already loaded.");
            return;
        }

        _loadHandle = Addressables.LoadSceneAsync(
            key:       _sceneReference,
            loadMode:  LoadSceneMode.Additive,
            activateOnLoad: true
        );

        while (!_loadHandle.IsDone)
        {
            Debug.Log($"[AddressableSceneLoader] Loading... {_loadHandle.PercentComplete:P0}");
            await Task.Yield();
        }

        if (_loadHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError($"[AddressableSceneLoader] Scene load failed: {_loadHandle.OperationException}");
            if (_loadHandle.IsValid()) Addressables.Release(_loadHandle);
            return;
        }

        _isLoaded = true;
        Debug.Log("[AddressableSceneLoader] Scene loaded successfully.");
    }

    /// <summary>
    /// Unload the scene and release the bundle reference.
    /// MUST be called via this method — do not use SceneManager.UnloadSceneAsync.
    /// </summary>
    public async Task UnloadAsync()
    {
        if (!_isLoaded || !_loadHandle.IsValid()) return;

        AsyncOperationHandle<SceneInstance> unloadHandle =
            Addressables.UnloadSceneAsync(_loadHandle, UnloadSceneOptions.UnloadAllEmbeddedSceneObjects);

        await unloadHandle.Task;

        if (unloadHandle.Status != AsyncOperationStatus.Succeeded)
            Debug.LogError($"[AddressableSceneLoader] Scene unload failed: {unloadHandle.OperationException}");

        _isLoaded = false;
    }

    private async void OnDestroy()
    {
        if (_isLoaded)
            await UnloadAsync();
    }
}
```

## Anti-Examples

### Resources.Load Beyond Prototyping

```csharp
// BAD — Resources.Load in a shipped title.
// Everything in Resources/ is included in the build regardless of use.
// This single call forces Unity to keep the entire Resources directory in the binary.
// Synchronous — stalls the main thread for the duration of the load.
private void SpawnEnemy(string type)
{
    GameObject prefab = Resources.Load<GameObject>($"Enemies/{type}"); // blocks main thread
    Instantiate(prefab, transform.position, Quaternion.identity);
}

// GOOD — async Addressable load by key; only loads what's needed when it's needed.
[SerializeField] private string _enemyAddressableKey;

private async void SpawnEnemyAsync(string key)
{
    var handle = Addressables.InstantiateAsync(key, transform.position, Quaternion.identity);
    await handle.Task;

    if (handle.Status != AsyncOperationStatus.Succeeded)
        Debug.LogError($"Failed to load enemy '{key}'");
    // The spawner or a manager tracks this handle for later release.
}
```

---

### Forgetting to Release Handles (Memory Leak)

```csharp
// BAD — handle stored locally in a method, handle reference lost after method returns.
// The asset stays in memory forever because its ref count never reaches zero.
// On a level with 50 such calls, 50 assets accumulate across play sessions.
private async void LoadIcon(string key)
{
    var handle = Addressables.LoadAssetAsync<Sprite>(key);
    await handle.Task;
    _iconImage.sprite = handle.Result; // display it

    // handle goes out of scope here — asset is now permanently retained.
    // NO Addressables.Release(handle) call!
}

// GOOD — store the handle as a field; release it in OnDestroy.
private AsyncOperationHandle<Sprite> _iconHandle;

private async void LoadIcon(string key)
{
    _iconHandle = Addressables.LoadAssetAsync<Sprite>(key);
    await _iconHandle.Task;

    if (_iconHandle.Status == AsyncOperationStatus.Succeeded)
        _iconImage.sprite = _iconHandle.Result;
}

private void OnDestroy()
{
    if (_iconHandle.IsValid())
        Addressables.Release(_iconHandle); // ref count decremented; asset may unload
}
```

---

### Synchronous Load of Large Assets

```csharp
// BAD — WaitForCompletion() forces the async operation to complete synchronously.
// On a large texture or audio clip, this freezes the game for several hundred
// milliseconds on target hardware — a visible hitch.
private void LoadBackground()
{
    var handle = Addressables.LoadAssetAsync<Texture2D>("Backgrounds/ForestBG");
    Texture2D tex = handle.WaitForCompletion(); // STALLS main thread
    _background.texture = tex;
}

// GOOD — keep the load async; show a loading indicator or transition.
private async void LoadBackgroundAsync()
{
    _loadingIndicator.SetActive(true);

    var handle = Addressables.LoadAssetAsync<Texture2D>("Backgrounds/ForestBG");
    await handle.Task;

    _loadingIndicator.SetActive(false);

    if (handle.Status == AsyncOperationStatus.Succeeded)
        _background.texture = handle.Result;
}
```

---

### Addressables for Tiny Always-Needed Assets

```csharp
// BAD — using Addressables for a tiny sprite that is always visible and
// always needed (e.g., the player's health bar icon).
// Adds async lifecycle management overhead (handle, await, release) where
// a simple [SerializeField] would load it for free with the scene.

[SerializeField] private string _healthIconKey; // Addressable key

private async void Start()
{
    var handle = Addressables.LoadAssetAsync<Sprite>(_healthIconKey);
    await handle.Task;
    _healthIcon.sprite = handle.Result;
    // Now must also track and release handle on destroy... for a 2 KB sprite.
}

// GOOD — direct serialized reference. Unity loads it with the HUD prefab automatically.
// No async code, no handle, no release. Exactly right for always-needed small assets.
[SerializeField] private Sprite _healthIconSprite;

private void Start()
{
    _healthIcon.sprite = _healthIconSprite; // zero overhead
}
```

## Cross-References

- Related skills: `hades:unity-performance` (memory budgets, asset memory cost), `hades:scene-architecture` (scene-based loading strategies, additive scene patterns), `hades:data-modeling` (data-driven asset key strategies, ScriptableObject catalogs)
- Hades MCP tools used in this skill:
  - `search_by_name` — confirm whether Addressables is already set up in the project
  - `find_components_using_pattern` — locate existing AssetReference usage
  - `get_project_summary` — assess project scale to determine if Addressables is warranted
  - `recall_memory` — documented group strategies and memory decisions
  - `propose_memory_update` — record new loading and memory decisions for the team
- Unity docs: [Addressables Overview](https://docs.unity3d.com/Packages/com.unity.addressables@latest), [AsyncOperationHandle](https://docs.unity3d.com/Packages/com.unity.addressables@latest/api/UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle-1.html), [AssetReference](https://docs.unity3d.com/Packages/com.unity.addressables@latest/api/UnityEngine.AddressableAssets.AssetReference.html), [Content Update Workflow](https://docs.unity3d.com/Packages/com.unity.addressables@latest/manual/ContentUpdateWorkflow.html), [Memory Management](https://docs.unity3d.com/Packages/com.unity.addressables@latest/manual/MemoryManagement.html)
