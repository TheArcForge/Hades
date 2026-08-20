---
description: "Use when designing scene structure — single vs additive loading, bootstrap patterns, scene transitions, persistent managers, DontDestroyOnLoad decisions, and streaming world setups."
---

# Scene Architecture

Provides decision frameworks and implementation patterns for structuring Unity scenes — from small single-scene games to large streaming worlds with persistent services. Covers bootstrap setup, scene transitions, additive loading, and persistent manager strategies.

## When to Apply

Activate when the conversation involves:
- Choosing between a monolithic scene, per-state scenes, or additive loading
- Deciding where persistent managers (audio, save, analytics) should live
- Designing scene transitions — hard cut, fade, or loading screen with progress
- Deciding whether to use `DontDestroyOnLoad` vs a dedicated bootstrap scene
- Setting up a streaming open-world or hub-and-spoke scene graph
- Questions about who owns a scene transition and how to trigger it
- Scene dependency: one scene needs data from a previously loaded scene

Do NOT activate for narrow scene-authoring tasks (placing objects, lighting, baking) — those go to `hades:scene-authoring`.

## Project Context Check

Before making recommendations, gather current project state:

1. **Check existing scene structure:**
   - Call `get_project_summary()` to see scene count, project scale, render pipeline, and asset volumes — a 3-scene mobile game warrants different advice than a 40-scene open-world PC title
   - Call `get_scene_summary("<main_scene>")` to understand what is already in the main or boot scene (existing managers, lighting, canvas hierarchy)
   - Call `search_by_name("*Scene*")` to discover scene-related assets and scripts (loaders, transition controllers, scene reference SOs)

2. **Check team decisions in memory:**
   - Call `recall_memory("scene loading architecture")` to find documented scene strategy
   - Call `recall_memory("persistent manager bootstrap")` to find existing manager lifetime decisions
   - If a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt based on findings:**
   - If graph shows an existing `BootstrapLoader` or `SceneTransitionManager` → align with it rather than introducing a parallel system
   - If no loading infrastructure exists → introduce from scratch and prompt the user to record the decision
   - If memory documents a scene strategy → follow it unless there is a strong technical reason to deviate

## Decision Framework

### Step 1 — Four Sizing Questions

Answer these before choosing a pattern.

| # | Question | Why It Matters |
|---|----------|----------------|
| 1 | **Scene count** — how many distinct screens or spaces exist? | 1–3 → monolithic or simple swap. 4–15 → per-state + transition. 16+ → additive streaming |
| 2 | **Persistent services** — does anything need to survive every scene change? | Audio, analytics, save system → bootstrap scene or DDOL. Nothing persistent → single-scene is fine |
| 3 | **World size** — can the full world fit in one scene, or must it stream? | Full fit → simple. Must stream → additive + Addressables |
| 4 | **Load budget** — how long can the player wait at each transition? | < 0.5 s → sync is fine. > 0.5 s → async + loading screen |

---

### DECISION: Scene Organisation Strategy

```
How many distinct screens / world areas exist?
├── 1–3, small project, no persistent services
│   └── MONOLITHIC — one scene per state, direct LoadScene calls
│       Pros: trivial to understand, zero additive overhead
│       Cons: all objects reload every transition
│
├── 4–15 screens with shared UI, audio, or save system
│   └── BOOTSTRAP + ADDITIVE — build index 0 is Bootstrap scene
│       Bootstrap holds persistent managers, loads content scenes additively
│       Pros: managers live once; clean state isolation per content scene
│       Cons: slightly more setup; all content scenes must not include duplicate managers
│
└── Large world / many rooms that cannot all load at once
    └── STREAMING — additive loading + Addressables
        Hub scene always loaded; area scenes load/unload by distance or trigger
        Pros: no loading screen mid-game; supports very large worlds
        Cons: significant complexity; requires Addressables setup and scene dependency graph
```

---

### DECISION: Persistent Manager Placement

```
Where should persistent managers (audio, analytics, save, settings) live?
├── Does the project use Bootstrap + Additive or Streaming?
│   └── BOOTSTRAP SCENE (recommended)
│       - Build index 0 scene contains only manager GameObjects
│       - Never unloaded; content scenes are loaded additively over it
│       - No DDOL needed; managers are always in an active scene
│
├── Single-scene project adding one or two persistent objects
│   └── DontDestroyOnLoad (acceptable, limited use)
│       - Use only when a bootstrap scene is overkill
│       - Must guard against duplicate instances (Awake singleton pattern)
│       - Never use DDOL on objects that hold scene-specific references
│
└── Pure data, no Unity lifecycle needed
    └── STATIC CLASS or SERVICE LOCATOR
        - No MonoBehaviour overhead; no scene dependency
        - Appropriate for config registries, game settings, math utilities
        - Do not store mutable GameObject references here
```

---

### DECISION: Scene Transitions

```
How long does the transition take and what should the player see?
├── Instant (same-frame or nearly instant)
│   └── HARD CUT — SceneManager.LoadSceneAsync, no interstitial
│       Use for: menu → menu, fast dev builds, unit tests
│
├── < 2 seconds, needs visual polish
│   └── FADE TRANSITION — black canvas overlay tweens opacity
│       Fade out → LoadSceneAsync → Fade in
│       Use for: chapter transitions, room-to-room, short loads
│
└── > 2 seconds or unpredictable load time
    └── LOADING SCREEN WITH PROGRESS — dedicated Loading scene
        Load Loading scene additively → unload old scene →
        load target scene (track AsyncOperation.progress) → unload Loading scene
        Use for: large levels, first boot, platform cert requirements
```

---

### DECISION: Scene Reference Management

```
How should scene names / paths be stored?
├── < 5 scenes, team of 1–2
│   └── String constants in a static class (GameScenes.cs)
│       Simple but string typos cause silent runtime errors
│
├── Any project with designers or > 5 scenes
│   └── ScriptableObject scene reference assets
│       - Hold build index + display name
│       - Drag into inspector; refactor-safe
│       - Build validation script can verify all references are in Build Settings
│
└── Streaming / Addressables project
    └── AssetReference<SceneAsset> in SO
        - Fully ref-counted loading; compatible with Addressables Groups
```

---

### Scene Dependency Management

Avoid implicit dependencies between scenes:
- A content scene must never reference objects from another content scene directly — use events or SOs
- If Scene A needs data from Scene B, store it in a SO or a static data class before unloading B
- Loading order dependencies should be encoded in the bootstrap loader, not assumed

## Code Examples

### Bootstrap Scene Loader

Full implementation — loads the Bootstrap scene (build index 0) first, then additively loads the initial game scene. All subsequent transitions go through `SceneLoader.TransitionTo`.

```csharp
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Attach to a single GameObject in the Bootstrap scene (build index 0).
/// The Bootstrap scene is never unloaded. All content scenes are loaded additively.
/// </summary>
public class BootstrapSceneLoader : MonoBehaviour
{
    [Tooltip("Name of the scene to load after bootstrap services initialise.")]
    [SerializeField] private string _initialSceneName = "MainMenu";

    [Tooltip("Name of the loading screen scene shown during transitions. Leave blank to skip.")]
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
        DontDestroyOnLoad(gameObject);  // keeps the loader itself persistent
    }

    private IEnumerator Start()
    {
        yield return InitialiseServicesAsync();
        yield return LoadContentSceneAsync(_initialSceneName);
    }

    /// <summary>
    /// Transition from the current content scene to <paramref name="sceneName"/>.
    /// Optionally shows a loading screen scene in between.
    /// </summary>
    public static void TransitionTo(string sceneName)
    {
        if (_instance == null)
        {
            Debug.LogError("[Bootstrap] No BootstrapSceneLoader instance found. " +
                           "Ensure Bootstrap is build index 0.");
            return;
        }
        _instance.StartCoroutine(_instance.DoTransition(sceneName));
    }

    private IEnumerator DoTransition(string targetScene)
    {
        // Show loading screen if configured.
        if (!string.IsNullOrEmpty(_loadingSceneName))
            yield return SceneManager.LoadSceneAsync(_loadingSceneName, LoadSceneMode.Additive);

        // Unload all content scenes except Bootstrap and Loading.
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (s.name == gameObject.scene.name) continue;    // skip Bootstrap
            if (s.name == _loadingSceneName)     continue;    // skip Loading
            yield return SceneManager.UnloadSceneAsync(s);
        }

        yield return LoadContentSceneAsync(targetScene);

        // Hide loading screen.
        if (!string.IsNullOrEmpty(_loadingSceneName))
            yield return SceneManager.UnloadSceneAsync(_loadingSceneName);
    }

    private IEnumerator LoadContentSceneAsync(string sceneName)
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        while (!op.isDone)
            yield return null;

        SceneManager.SetActiveScene(SceneManager.GetSceneByName(sceneName));
    }

    /// <summary>
    /// Replace with real async service initialisation (audio, save, analytics, etc.).
    /// </summary>
    private IEnumerator InitialiseServicesAsync()
    {
        yield return null;
    }
}
```

---

### Scene Transition Manager with Loading Screen and Progress

Full implementation — tracks `AsyncOperation.progress`, drives a UI progress bar, and supports fade in/out.

```csharp
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Manages scene transitions with a loading screen that reports progress.
/// Place in the Bootstrap scene alongside other persistent managers.
/// The loading screen canvas is enabled/disabled by this manager — it should
/// start disabled in the Bootstrap scene hierarchy.
/// </summary>
public class SceneTransitionManager : MonoBehaviour
{
    [Header("Loading Screen")]
    [Tooltip("Root canvas of the loading screen UI. Starts disabled.")]
    [SerializeField] private CanvasGroup _loadingCanvasGroup;

    [Tooltip("Slider or Image fill used as a progress bar (0–1 fill amount).")]
    [SerializeField] private Slider _progressBar;

    [Tooltip("Seconds to fade the loading canvas in or out.")]
    [SerializeField] private float _fadeDuration = 0.3f;

    [Header("Transition")]
    [Tooltip("Minimum time to show the loading screen even if load finishes fast.")]
    [SerializeField] private float _minimumLoadTime = 0.5f;

    private static SceneTransitionManager _instance;

    public static SceneTransitionManager Instance => _instance;

    /// <summary>Raised when a transition completes. Parameter is the new scene name.</summary>
    public static event Action<string> OnTransitionComplete;

    private void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        // Ensure loading screen is hidden at startup.
        if (_loadingCanvasGroup != null)
        {
            _loadingCanvasGroup.alpha = 0f;
            _loadingCanvasGroup.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Begin transitioning to <paramref name="sceneName"/> with a loading screen.
    /// <paramref name="onProgress"/> receives normalised progress 0–1 each frame if non-null.
    /// </summary>
    public void LoadScene(string sceneName, Action<float> onProgress = null)
    {
        StartCoroutine(TransitionCoroutine(sceneName, onProgress));
    }

    private IEnumerator TransitionCoroutine(string targetScene, Action<float> onProgress)
    {
        // Fade in loading screen.
        yield return SetLoadingScreenVisible(true);

        float startTime = Time.realtimeSinceStartup;

        // Begin async load but do not activate scene yet.
        AsyncOperation op = SceneManager.LoadSceneAsync(targetScene, LoadSceneMode.Single);
        op.allowSceneActivation = false;

        while (op.progress < 0.9f)  // Unity caps at 0.9 before activation
        {
            float progress = Mathf.Clamp01(op.progress / 0.9f);
            UpdateProgress(progress * 0.9f);  // reserve last 10% for minimum wait
            onProgress?.Invoke(progress);
            yield return null;
        }

        // Enforce minimum display time.
        float elapsed = Time.realtimeSinceStartup - startTime;
        if (elapsed < _minimumLoadTime)
            yield return new WaitForSecondsRealtime(_minimumLoadTime - elapsed);

        UpdateProgress(1f);
        yield return null;  // one frame for UI update

        // Activate new scene.
        op.allowSceneActivation = true;
        while (!op.isDone)
            yield return null;

        // Fade out loading screen.
        yield return SetLoadingScreenVisible(false);

        OnTransitionComplete?.Invoke(targetScene);
    }

    private void UpdateProgress(float value)
    {
        if (_progressBar != null)
            _progressBar.value = value;
    }

    private IEnumerator SetLoadingScreenVisible(bool visible)
    {
        if (_loadingCanvasGroup == null) yield break;

        _loadingCanvasGroup.gameObject.SetActive(true);

        float start  = visible ? 0f : 1f;
        float target = visible ? 1f : 0f;
        float elapsed = 0f;

        while (elapsed < _fadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            _loadingCanvasGroup.alpha = Mathf.Lerp(start, target, elapsed / _fadeDuration);
            yield return null;
        }

        _loadingCanvasGroup.alpha = target;

        if (!visible)
            _loadingCanvasGroup.gameObject.SetActive(false);
    }
}
```

---

### Persistent Managers via Additive Scene

Full implementation — a dedicated `PersistentManagers` scene holds all global services and is always loaded additively. Use instead of sprinkling `DontDestroyOnLoad` across multiple prefabs.

```csharp
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Entry point for the Bootstrap scene.
/// Loads the PersistentManagers scene additively before the initial content scene.
/// </summary>
public class AppEntryPoint : MonoBehaviour
{
    [SerializeField] private string _persistentManagersScene = "PersistentManagers";
    [SerializeField] private string _initialContentScene     = "MainMenu";

    private IEnumerator Start()
    {
        // Guard: only load PersistentManagers if not already loaded (e.g. in Editor).
        if (!SceneManager.GetSceneByName(_persistentManagersScene).isLoaded)
        {
            AsyncOperation load =
                SceneManager.LoadSceneAsync(_persistentManagersScene, LoadSceneMode.Additive);
            while (!load.isDone) yield return null;
        }

        AsyncOperation content =
            SceneManager.LoadSceneAsync(_initialContentScene, LoadSceneMode.Additive);
        while (!content.isDone) yield return null;

        SceneManager.SetActiveScene(
            SceneManager.GetSceneByName(_initialContentScene));
    }
}

/// <summary>
/// Place once in the PersistentManagers scene.
/// Provides a central registration point for manager discovery.
/// Managers in the same scene call ServiceRegistry.Register in their Awake.
/// </summary>
public static class ServiceRegistry
{
    private static readonly System.Collections.Generic.Dictionary<System.Type, object>
        _services = new();

    public static void Register<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    public static T Get<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out object s))
            return s as T;

        Debug.LogError($"[ServiceRegistry] No service registered for {typeof(T).Name}.");
        return null;
    }

    public static void Clear() => _services.Clear();
}

/// <summary>
/// Example persistent manager. Place in the PersistentManagers scene.
/// Registers itself with ServiceRegistry so other systems can discover it
/// without a direct scene reference.
/// </summary>
public class AudioManager : MonoBehaviour
{
    [SerializeField] private AudioSource _musicSource;
    [SerializeField] private AudioSource _sfxSource;

    private void Awake()
    {
        ServiceRegistry.Register<AudioManager>(this);
    }

    public void PlayMusic(AudioClip clip, float volume = 1f)
    {
        _musicSource.clip   = clip;
        _musicSource.volume = volume;
        _musicSource.Play();
    }

    public void PlaySfx(AudioClip clip, float volume = 1f)
    {
        _sfxSource.PlayOneShot(clip, volume);
    }

    public void StopMusic() => _musicSource.Stop();
}
```

---

### Scene Reference ScriptableObject

Full implementation — avoids magic strings by storing scene names and build indices in inspector-assigned assets.

```csharp
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// ScriptableObject that holds a safe reference to a scene by build index and name.
/// Create assets: Assets > Scenes > Scene Reference
/// Assign to any field that currently uses a string scene name.
/// </summary>
[CreateAssetMenu(menuName = "Scenes/Scene Reference", fileName = "NewSceneRef")]
public class SceneReference : ScriptableObject
{
    [Tooltip("Human-readable display name shown in loading UI.")]
    [SerializeField] private string _displayName;

    [Tooltip("Scene name as it appears in Build Settings.")]
    [SerializeField] private string _sceneName;

    [Tooltip("Build index from Build Settings.")]
    [SerializeField] private int _buildIndex = -1;

    public string DisplayName => string.IsNullOrEmpty(_displayName) ? _sceneName : _displayName;
    public string SceneName   => _sceneName;
    public int    BuildIndex  => _buildIndex;

    public bool IsValid => _buildIndex >= 0
                        && _buildIndex < SceneManager.sceneCountInBuildSettings;

    /// <summary>Loads this scene asynchronously (Single mode by default).</summary>
    public AsyncOperation LoadAsync(LoadSceneMode mode = LoadSceneMode.Single)
    {
        if (!IsValid)
        {
            Debug.LogError($"[SceneReference] '{_sceneName}' has invalid build index {_buildIndex}.");
            return null;
        }
        return SceneManager.LoadSceneAsync(_buildIndex, mode);
    }

#if UNITY_EDITOR
    /// <summary>Editor-only validation to keep build index in sync with Build Settings.</summary>
    public void ValidateInEditor()
    {
        if (string.IsNullOrEmpty(_sceneName)) return;

        for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
        {
            string path = EditorBuildSettings.scenes[i].path;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name == _sceneName)
            {
                _buildIndex = i;
                EditorUtility.SetDirty(this);
                return;
            }
        }
        Debug.LogWarning($"[SceneReference] '{_sceneName}' not found in Build Settings.");
        _buildIndex = -1;
    }
#endif
}
```

## Anti-Examples

### DontDestroyOnLoad on Every Persistent Object

```csharp
// BAD — every persistent object calls DDOL independently.
// Re-entering a scene creates duplicate AudioManager, SaveManager, UIManager, etc.
// Duplicates are nearly impossible to detect without logging every Awake.
public class AudioManager : MonoBehaviour
{
    private void Awake()
    {
        DontDestroyOnLoad(gameObject);  // no duplicate guard — second instance coexists silently
    }
}

public class SaveManager : MonoBehaviour
{
    private void Awake()
    {
        DontDestroyOnLoad(gameObject);  // same problem — both listen to the same events
    }
}
```

Prefer: a single Bootstrap or PersistentManagers scene that holds all persistent objects. They exist because the scene exists — no DDOL needed.

---

### Synchronous Scene Loading on Main Thread

```csharp
// BAD — blocks the main thread, causes a visible freeze of ≥ one frame.
// On large scenes this can stall for seconds without any user feedback.
void LoadNextLevel()
{
    SceneManager.LoadScene("Level_02");  // synchronous, blocks everything
    // Any code here runs AFTER the frame stall — misleading about timing
    InitialiseHUD();
}
```

Use `SceneManager.LoadSceneAsync` with a coroutine. Set `allowSceneActivation = false` to pre-load while still showing the current scene, then activate only when ready.

---

### Hard-Coded Scene Name Strings Scattered Across Codebase

```csharp
// BAD — magic strings in multiple files.
// Rename the scene in Build Settings → everything silently breaks at runtime.
SceneManager.LoadSceneAsync("Level_03_Forest");   // GameManager.cs line 88
SceneManager.LoadSceneAsync("Level_03_Forest");   // TeleportTrigger.cs line 42
SceneManager.LoadSceneAsync("Level_03_Forest");   // DeathHandler.cs line 17
```

Use `SceneReference` SOs or at minimum a single `static class GameScenes { public const string ForestLevel = "Level_03_Forest"; }` so renames propagate from one place.

---

### Loading Scene in Start Without Guard (Editor Regression)

```csharp
// BAD — works in a build but explodes in Editor Play mode when you open a content
// scene directly. The Bootstrap scene is not loaded, so SceneManager.GetSceneByName
// returns an invalid scene and SetActiveScene throws.
private IEnumerator Start()
{
    yield return SceneManager.LoadSceneAsync("GameplayHUD", LoadSceneMode.Additive);
    SceneManager.SetActiveScene(SceneManager.GetSceneByName("Gameplay"));  // null ref if opened directly
}
```

Always check `SceneManager.GetSceneByName().isLoaded` before calling `SetActiveScene`, and add an Editor-only guard that skips bootstrap logic when already in the target scene.

## Cross-References

- Related skills: `hades:unity-architect`, `hades:scene-authoring`
- Hades MCP tools used in this skill:
  - `get_project_summary` — project scale, scene count
  - `get_scene_summary` — inspect scene hierarchy before recommending changes
  - `search_by_name` — discover existing scene loaders and transition scripts
  - `trace_dependencies` — check what depends on a scene being loaded first
  - `recall_memory` — retrieve documented scene strategy and manager decisions
  - `propose_memory_update` — record new scene architecture decisions for the team
