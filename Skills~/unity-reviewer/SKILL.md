---
description: "Use when reviewing Unity C# code changes — checks for runtime-breaking bugs, performance anti-patterns, and maintainability issues. Provides severity-tiered review organized as Critical, Performance, and Style."
---

# Unity Code Reviewer

Run through these checks after modifying or creating C# scripts. Organized by severity: Critical first, then Performance, then Style. Fix Critical issues immediately — they will break the game at runtime. Batch Performance and Style issues where convenient.

## When to Apply

- After implementing any new MonoBehaviour or editor script
- When `superpowers:requesting-code-review` is active and the changes involve Unity C#
- When a bug report implicates event handling, threading, or object lifetime
- Before marking any Unity feature task complete

## Project Context Check

Before running the checklist, gather impact context using Hades Graph and memory:

```
find_references_to("<changed_script_path>")   — returns BOTH asset references (prefabs, scenes) AND C# code references (fields, parameters, constructors, casts, inheritance)
trace_dependencies("<changed_script_path>")   — what the changed script depends on
recall_memory("code review conventions style") — documented team conventions and past decisions
```

Use these results to:
- Flag ripple risks (e.g., a change to a base class affects many dependents — `find_references_to` shows both prefab dependents and C# code dependents)
- Adjust severity: a performance issue in a script referenced by 40 prefabs is higher priority than one referenced by 1
- Verify the change aligns with team conventions before marking Style issues as acceptable
- Do NOT use `grep` to find references — the graph is authoritative for both asset and code-level dependencies

## Decision Framework

### Critical (Will Break at Runtime)

#### CHECK: Missing Event Unsubscription

**Wrong (common default):**
```csharp
void OnEnable() {
    GameEvents.OnPlayerDied += HandlePlayerDied;
}
// No OnDisable — listener persists after destroy, causes NullReferenceException
```

**Right:**
```csharp
void OnEnable() {
    GameEvents.OnPlayerDied += HandlePlayerDied;
}
void OnDisable() {
    GameEvents.OnPlayerDied -= HandlePlayerDied;
}
```

**Why it matters:** Leaked subscriptions cause NullReferenceExceptions when the destroyed object's method is called. One of the most common Unity bugs. If `find_references_to` shows many dependents, audit all of them for the same pattern.

---

#### CHECK: Unity Fake Null

**Wrong (common default):**
```csharp
var go = GetComponent<SomeComponent>();
var result = go?.DoThing(); // C# null check — DOES NOT catch Unity destroyed objects
```

**Right:**
```csharp
var go = GetComponent<SomeComponent>();
if (go != null) go.DoThing(); // Unity's == override catches destroyed objects
```

**Why it matters:** Unity overrides `==` to return true for destroyed objects, but C#'s `?.` and `??` bypass this override. The object appears non-null to C# but is destroyed. This produces silent data corruption or deferred crashes that are hard to trace.

---

#### CHECK: Accessing Unity API from Background Thread

**Wrong:**
```csharp
async Task LoadDataAsync() {
    var data = await FetchFromServer();
    transform.position = data.position; // CRASH: not on main thread
}
```

**Right:**
```csharp
async Task LoadDataAsync() {
    var data = await FetchFromServer();
    await Awaitable.MainThreadAsync(); // Switch back to main thread (Unity 6+)
    transform.position = data.position;
}
```

**Why it matters:** Unity's API is not thread-safe. Accessing transforms, GameObjects, or components from a background thread causes crashes or silent corruption. Always marshal back to the main thread before touching any Unity object.

---

#### CHECK: Destroy vs DestroyImmediate

**Wrong:**
```csharp
void CleanUp() {
    DestroyImmediate(gameObject); // Dangerous outside editor scripts
}
```

**Right:**
```csharp
void CleanUp() {
    Destroy(gameObject); // Deferred to end of frame, safe at runtime
}
```

**Why it matters:** `DestroyImmediate` removes the object mid-frame, breaking iteration and invalidating references held by other components in the same frame. It is only valid in editor scripts (`#if UNITY_EDITOR` blocks). In runtime code, always use `Destroy`.

---

### Performance (Tanks Framerate)

#### CHECK: GetComponent in Update

**Wrong (common default):**
```csharp
void Update() {
    GetComponent<Rigidbody>().AddForce(Vector3.up); // Lookup every frame
}
```

**Right:**
```csharp
Rigidbody _rb;
void Awake() { _rb = GetComponent<Rigidbody>(); }
void Update() { _rb.AddForce(Vector3.up); }
```

**Why it matters:** `GetComponent` uses reflection-based lookup. In a hot loop with many objects, this adds up to milliseconds per frame. Cache all component references in `Awake` or `Start`. If `find_references_to` shows this script is attached to pooled or frequently spawned objects, the impact is multiplied.

---

#### CHECK: GameObject.Find in Update

**Wrong:**
```csharp
void Update() {
    var player = GameObject.Find("Player"); // Linear scan every frame
    transform.LookAt(player.transform);
}
```

**Right:**
```csharp
[SerializeField] Transform _playerTransform; // Set via Inspector or dependency injection
void Update() { transform.LookAt(_playerTransform); }
```

**Why it matters:** `Find` scans the entire scene hierarchy every call — O(n) per frame. Use serialized references set at edit time, or cache the result in `Awake`/`Start` if dynamic. `GameObject.FindWithTag` is faster but still O(n) and still should not run every frame.

---

#### CHECK: Allocations in Hot Paths

**Wrong (common default):**
```csharp
void Update() {
    var hits = Physics.RaycastAll(transform.position, transform.forward); // Allocates array every frame
    var name = "Player_" + id.ToString();                                 // String allocation
    var filtered = enemies.Where(e => e.IsAlive).ToList();                // LINQ allocates
}
```

**Right:**
```csharp
RaycastHit[] _hits = new RaycastHit[32]; // Pre-allocated buffer, reused every frame

void Update() {
    int count = Physics.RaycastNonAlloc(transform.position, transform.forward, _hits);
    // Process _hits[0..count-1] — no allocation
}
```

**Why it matters:** Every allocation in `Update` contributes to GC pressure. When GC runs, it causes frame spikes (10–50ms stutters on mobile, visible on desktop). Prefer `NonAlloc` physics queries, cached string builders, and pre-allocated collections for hot paths.

---

#### CHECK: Inappropriate Update vs FixedUpdate

**Wrong:**
```csharp
void Update() {
    _rb.MovePosition(transform.position + dir * speed * Time.deltaTime); // Physics in Update
}
void FixedUpdate() {
    if (Input.GetKeyDown(KeyCode.Space)) Jump(); // Input captured in FixedUpdate
}
```

**Right:**
```csharp
bool _shouldJump;

void Update() {
    if (Input.GetKeyDown(KeyCode.Space)) _shouldJump = true; // Capture input in Update
}
void FixedUpdate() {
    _rb.MovePosition(transform.position + dir * speed * Time.fixedDeltaTime); // Physics in FixedUpdate
    if (_shouldJump) { Jump(); _shouldJump = false; }
}
```

**Why it matters:** Physics operations in `Update` cause jitter because `Update` runs at frame rate, not physics rate. Input captured in `FixedUpdate` misses key presses because `FixedUpdate` does not run every rendered frame. Keep physics in `FixedUpdate`, input in `Update`.

---

#### CHECK: String Operations in Hot Paths

**Wrong:**
```csharp
void Update() {
    _label.text = "Score: " + score.ToString(); // Allocates a new string every frame
}
```

**Right:**
```csharp
int _lastScore = -1;

void Update() {
    if (score != _lastScore) {
        _label.text = $"Score: {score}"; // Only rebuild string when value changes
        _lastScore = score;
    }
}
```

**Why it matters:** String concatenation allocates. If the value hasn't changed, rebuilding the string every frame wastes memory and triggers GC. Track previous values and only update UI strings when the data actually changes.

---

### Style (Maintainability Issues)

#### CHECK: Public Fields for Inspector

**Wrong (common default):**
```csharp
public float speed = 5f;
public int maxHealth = 100;
```

**Right:**
```csharp
[SerializeField] float _speed = 5f;
[SerializeField] int _maxHealth = 100;
```

**Why it matters:** Public fields expose implementation details — any script can read or write them, making bugs hard to trace. `[SerializeField]` gives Inspector access without public exposure, enforcing encapsulation while preserving designer configurability.

---

#### CHECK: MonoBehaviour Lifecycle Order

**Wrong:**
```csharp
public class Enemy : MonoBehaviour {
    void Update() { ... }
    void Start() { ... }
    void Awake() { ... }
    void OnDestroy() { ... }
    void OnEnable() { ... }
}
```

**Right:**
```csharp
public class Enemy : MonoBehaviour {
    // Initialization
    void Awake() { ... }
    void OnEnable() { ... }
    void Start() { ... }

    // Per-frame
    void Update() { ... }

    // Teardown
    void OnDisable() { ... }
    void OnDestroy() { ... }
}
```

**Why it matters:** Ordering lifecycle methods to match Unity's execution order makes the lifetime of a component immediately readable. Consistent ordering across the codebase reduces cognitive load when debugging.

---

#### CHECK: Magic Numbers

**Wrong:**
```csharp
if (health < 20) ShowWarning();       // What is 20?
rb.AddForce(Vector3.up * 9.81f);     // Why this value?
```

**Right:**
```csharp
[SerializeField] float _lowHealthThreshold = 20f;
[SerializeField] float _jumpForce = 9.81f;

if (health < _lowHealthThreshold) ShowWarning();
rb.AddForce(Vector3.up * _jumpForce);
```

**Why it matters:** Magic numbers are untunable by designers, undocumented, and scattered. Serialized fields are named, visible in the Inspector, and adjustable without code changes. When using `recall_memory`, check whether the project has a convention for where tuning constants should live (e.g., a ScriptableObject config asset vs. inline serialized fields).

---

## Graph-Backed Impact Analysis

After running the checklist, use the Hades Graph to understand blast radius:

```
find_references_to("<changed_script_path>")
```

- If the changed script is a base class or shared utility, every dependent is potentially affected by the bug or change.
- Flag high-reference scripts as requiring broader testing — the review should note this explicitly.

```
trace_dependencies("<changed_script_path>")
```

- Identify what the changed script depends on. If a dependency changed recently too, check for interaction bugs.
- Use this to catch cases where a "safe" local change breaks an upstream assumption.

## Memory-Backed Convention Checking

```
recall_memory("code review conventions style")
```

Before filing Style findings, verify they are consistent with documented team conventions. If the codebase has documented exceptions (e.g., "we allow public fields on data classes"), don't flag them as violations.

If the review surfaces a new convention (e.g., a pattern the team agrees on during the review), record it:

```
propose_memory_update("code review conventions", "<new convention description>")
```

## Review Process

1. Pull context: run `find_references_to`, `trace_dependencies`, `recall_memory` before scanning code.
2. Scan for **Critical** checks first — these are bugs that will crash or corrupt at runtime.
3. Scan for **Performance** checks — these cause visible degradation, especially on mobile or with many instances.
4. Scan for **Style** checks — these accumulate maintainability debt.
5. Report findings grouped by severity tier.
6. Fix Critical issues immediately. Batch Performance and Style issues.
7. After review, if new conventions emerged, call `propose_memory_update`.

## Anti-Examples

Every "Wrong" code block above is an anti-example. The most common violations to watch for:

- Event subscription without matching unsubscription (Critical)
- Using `?.` or `??` on Unity objects (Critical)
- Any `GetComponent`, `Find`, or `FindWithTag` inside `Update` (Performance)
- Allocating in hot paths without pre-allocation (Performance)
- Public fields where `[SerializeField]` should be used (Style)

## Cross-References

**Skills:** `hades:unity-performance` — deep performance analysis beyond code review. `hades:component-design` — proper component structure and lifecycle design.

**Hades MCP Tools:** `find_references_to`, `trace_dependencies`, `recall_memory`, `propose_memory_update`.
