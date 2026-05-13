---
description: "Use when writing or structuring tests — EditMode vs PlayMode test decisions, test architecture, what to test in Unity, mocking strategies, and CI integration for Unity tests."
---

# Unity Testing

Decision framework for testing in Unity: when to use EditMode vs PlayMode tests, what is worth testing and what is not, how to structure test assemblies, mocking strategies without third-party frameworks, and how to integrate with CI.

## When to Apply

Activate when the conversation involves:
- Deciding whether a test belongs in EditMode or PlayMode
- Writing a new test class or test assembly
- Questions about `[Test]`, `[UnityTest]`, `[SetUp]`, `[TearDown]`, or `TestFixture`
- Mocking Unity components or isolating game logic from the MonoBehaviour lifecycle
- Setting up assembly definitions for test projects
- Running Unity tests in CI (headless, command-line)
- Deciding whether something is worth testing at all

Do NOT activate for debugging a specific runtime bug without a test reproduction case — that goes to general debugging. Do NOT activate for performance measurement — that goes to `hades:unity-performance`.

## Project Context Check

Before making testing recommendations, gather context to match advice to the actual project.

1. **Find existing tests:**
   - Call `search_by_name("*Test*")` — reveals all existing test files and their location. If the project has no tests yet, the first recommendation is always to establish the assembly structure before writing any test.
   - Call `search_by_name("*Tests.asmdef")` — finds test assembly definitions. If none exist, any tests are currently compiling into the main runtime assembly and shipping in builds — a critical issue to fix first.

2. **Understand project structure:**
   - Call `get_project_summary()` — reveals project scale, whether it is a package or application, and existing folder organization. Test placement conventions differ between a package (tests go in `Tests/` inside the package) and an application (tests go in a sibling `Tests/` folder).

3. **Check documented testing conventions:**
   - Call `recall_memory("testing strategy coverage")` — retrieves any recorded decisions about test coverage targets, fixture patterns, or CI configuration already in place.
   - After establishing a testing pattern for the first time, call `propose_memory_update` to record it so it is consistent across the codebase.

4. **Adapt recommendations based on findings:**
   - If `search_by_name("*Tests.asmdef")` returns nothing → fix assembly isolation before writing any new tests.
   - If existing tests are all PlayMode → consider whether some could be converted to EditMode (10× faster, no scene load overhead).
   - If `get_project_summary()` shows this is a package → test assembly must reference the package assembly, not a hardcoded application assembly.

## Decision Framework

### DECISION: EditMode vs PlayMode

```
Does the test require the full Unity player loop (Update, physics, rendering)?
├── No — pure logic: data transformation, state machines, calculations,
│   serialization, event firing
│   └── EditMode test
│       Fast (< 1 ms each), no scene load, runs without entering Play Mode.
│       Can test ScriptableObjects, plain C# classes, and MonoBehaviour
│       method bodies if instantiated manually with new GameObject().
│
└── Yes — requires coroutines, scene hierarchy, MonoBehaviour lifecycle
    (Awake/Start/Update), physics, animations, or renderer state
    └── PlayMode test
        Slower (~300 ms for scene entry + test execution).
        Use sparingly — only for things that genuinely cannot be tested
        without the player loop.
```

**Heuristic:** Write the test in EditMode first. If it requires `yield return null` to observe a state change, it belongs in PlayMode. If it does not, keep it in EditMode.

**Cost comparison (approximate):**

| Test Type | Per-test overhead | Scene load | Player loop |
|-----------|------------------|------------|-------------|
| EditMode | < 1 ms | No | No |
| PlayMode (no scene) | ~200 ms | No | Yes |
| PlayMode (load scene) | 300–500 ms | Yes | Yes |

---

### DECISION: What to Test

```
ALWAYS test:
  ✓ Pure game logic (damage formulas, score calculation, level progression rules)
  ✓ State machines (valid transitions, invalid transition guards, entry/exit callbacks)
  ✓ Data processing (save/load serialization round-trips, migration logic)
  ✓ Algorithms (pathfinding cost, inventory sorting, loot table distribution)
  ✓ ScriptableObject data validation (stat ranges, required references)
  ✓ Event/callback firing (does TakeDamage raise the OnDied event at 0 HP?)

CONSIDER testing (cost/benefit depends on project):
  ✓ Component interactions on a test GameObject (does HealthComponent notify UIComponent?)
  ✓ MonoBehaviour integration scenarios that are expensive to test manually
  ✓ Critical spawn/despawn sequences where bugs cause hard-to-reproduce defects

DO NOT test (low value or not testable reliably):
  ✗ Unity engine behavior (does AddForce move a Rigidbody? — Unity already tests this)
  ✗ Visual rendering output (pixel-perfect rendering tests require specialized tooling)
  ✗ Editor-only tools unless the tool has externally observable outputs
  ✗ Code paths with no branching or decision logic
  ✗ Every get/set property with no logic
```

---

### DECISION: Mocking Strategy

Unity has no built-in mocking framework. NSubstitute and Moq work but require .NET Standard 2.1 and DLL management. The Unity-native approach is interface-based test doubles.

```
Does the code under test have external dependencies (input, audio, network, time)?
├── No — test the class directly with known inputs.
│
└── Yes
    ├── Is the dependency behind an interface?
    │   ├── Yes → inject a test double (fake/stub class) in the test SetUp.
    │   └── No → extract an interface first (refactor), then inject.
    │
    └── Is the dependency on Unity systems (Physics, Time, SceneManager)?
        └── Wrap in a thin service interface (ITimeProvider, IPhysicsService).
            Inject the real implementation in production.
            Inject a fake that returns controlled values in tests.
```

**Why interfaces over Moq/NSubstitute in Unity:**
- No NuGet — package management in Unity is manual DLL drops or UPM packages.
- Reflection-based mocking frameworks can interfere with IL2CPP stripping.
- Handwritten test doubles are more readable and produce better error messages.
- The interface extraction forced by this approach improves production code design.

---

### DECISION: CI Integration

Unity supports headless test runs via the command-line `-runTests` flag. The standard output is a NUnit XML file compatible with all major CI systems (GitHub Actions, Jenkins, TeamCity, Buildkite).

```
Is this running on CI?
├── Use Unity's Test Runner CLI:
│   unity -batchmode -runTests -projectPath /path/to/project
│         -testResults TestResults.xml -testPlatform EditMode
│
├── For PlayMode on CI:
│   -testPlatform PlayMode
│   Note: PlayMode CI requires a GPU or a software renderer (Mesa/SwiftShader).
│   On headless Linux: use -nographics (limits but does not eliminate PlayMode tests).
│
└── Parse results:
    Standard NUnit XML output — attach to CI as test artifact.
    GitHub Actions: use dorny/test-reporter@v1 or EnricoMi/publish-unit-test-result-action.
```

## Code Examples

### EditMode Test for Pure Game Logic

Full test class for a health system. Covers normal damage, death threshold, healing, and invalid input.

```csharp
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// EditMode tests for HealthSystem — pure C# logic, no scene required.
/// Place this file in a folder with a .asmdef that references Editor and TestRunner.
/// </summary>
[TestFixture]
public class HealthSystemTests
{
    private HealthSystem _health;

    [SetUp]
    public void SetUp()
    {
        // Create a fresh instance before each test.
        // No scene, no MonoBehaviour — just the class under test.
        _health = new HealthSystem(maxHealth: 100f);
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up any state if HealthSystem held external resources.
        // For a plain C# class, this is often empty but keeps the pattern explicit.
        _health = null;
    }

    [Test]
    public void TakeDamage_ReducesCurrentHealth()
    {
        _health.TakeDamage(30f);
        Assert.AreEqual(70f, _health.CurrentHealth, delta: 0.001f);
    }

    [Test]
    public void TakeDamage_ToZero_SetsIsDead()
    {
        _health.TakeDamage(100f);
        Assert.IsTrue(_health.IsDead);
    }

    [Test]
    public void TakeDamage_BeyondMax_ClampsToZero()
    {
        _health.TakeDamage(9999f);
        Assert.AreEqual(0f, _health.CurrentHealth, delta: 0.001f);
        Assert.IsTrue(_health.IsDead);
    }

    [Test]
    public void Heal_IncreasesCurrentHealth()
    {
        _health.TakeDamage(50f);
        _health.Heal(20f);
        Assert.AreEqual(70f, _health.CurrentHealth, delta: 0.001f);
    }

    [Test]
    public void Heal_DoesNotExceedMaxHealth()
    {
        _health.Heal(9999f);
        Assert.AreEqual(100f, _health.CurrentHealth, delta: 0.001f);
    }

    [Test]
    public void TakeDamage_FiresOnDiedEvent_WhenHealthReachesZero()
    {
        bool firedDeath = false;
        _health.OnDied += () => firedDeath = true;

        _health.TakeDamage(100f);

        Assert.IsTrue(firedDeath, "OnDied event was not fired.");
    }

    [Test]
    public void TakeDamage_DoesNotFireOnDied_WhenHealthRemainsAboveZero()
    {
        bool firedDeath = false;
        _health.OnDied += () => firedDeath = true;

        _health.TakeDamage(50f);

        Assert.IsFalse(firedDeath, "OnDied fired prematurely.");
    }

    [Test]
    public void TakeDamage_NegativeAmount_ThrowsArgumentException()
    {
        // Verify that invalid inputs are rejected rather than silently corrupting state.
        Assert.Throws<System.ArgumentException>(() => _health.TakeDamage(-10f));
    }

    // ── Parameterized test — avoids copy-pasting test methods for boundary values ──
    [TestCase(0f,   100f)] // no damage
    [TestCase(1f,    99f)] // minimum damage
    [TestCase(99f,    1f)] // one point remaining
    [TestCase(100f,   0f)] // exact lethal
    public void TakeDamage_VariousAmounts_ProducesExpectedHealth(float damage, float expectedHealth)
    {
        _health.TakeDamage(damage);
        Assert.AreEqual(expectedHealth, _health.CurrentHealth, delta: 0.001f);
    }
}

// ─── Production class under test (in separate non-test assembly) ─────────────

/// <summary>
/// Plain C# class — no MonoBehaviour, no Unity dependency.
/// Testable without instantiating a GameObject.
/// </summary>
public class HealthSystem
{
    public float MaxHealth { get; }
    public float CurrentHealth { get; private set; }
    public bool IsDead => CurrentHealth <= 0f;

    public event System.Action OnDied;

    public HealthSystem(float maxHealth)
    {
        MaxHealth     = maxHealth;
        CurrentHealth = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        if (amount < 0f)
            throw new System.ArgumentException("Damage amount cannot be negative.", nameof(amount));

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);

        if (IsDead)
            OnDied?.Invoke();
    }

    public void Heal(float amount)
    {
        CurrentHealth = Mathf.Min(MaxHealth, CurrentHealth + amount);
    }
}
```

---

### PlayMode Test with Scene Loading and MonoBehaviour Verification

When you need to verify MonoBehaviour lifecycle behavior, use PlayMode with `[UnityTest]`.

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// <summary>
/// PlayMode test for PlayerController — requires the player loop (Awake, Start, Update).
/// This test loads a minimal test scene that contains only the components under test.
/// Placing it in a PlayMode test assembly (see assembly definition example below).
/// </summary>
[TestFixture]
public class PlayerControllerPlayModeTests
{
    private const string TestSceneName = "TestScenes/PlayerControllerTestScene";

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // Load the test scene asynchronously. yield return waits for completion.
        // The test scene should be minimal: only the objects needed for this test.
        AsyncOperation load = SceneManager.LoadSceneAsync(TestSceneName, LoadSceneMode.Additive);
        while (!load.isDone)
            yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        // Unload the test scene after each test to avoid state leaking between tests.
        AsyncOperation unload = SceneManager.UnloadSceneAsync(TestSceneName);
        while (!unload.isDone)
            yield return null;
    }

    [UnityTest]
    public IEnumerator PlayerController_ReceivesInput_MovesForward()
    {
        // Find the component under test in the loaded scene.
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        Assert.IsNotNull(player, "PlayerController not found in test scene.");

        Vector3 initialPosition = player.transform.position;

        // Simulate one second of forward input by injecting through the interface.
        player.SetTestInput(new Vector3(0f, 0f, 1f));

        // Wait two frames so Awake → Start → Update executes.
        yield return null;
        yield return null;

        Assert.Greater(
            player.transform.position.z,
            initialPosition.z,
            "Player did not move forward after receiving forward input."
        );
    }

    [UnityTest]
    public IEnumerator PlayerController_WhenDead_StopsMoving()
    {
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        Assert.IsNotNull(player);

        Vector3 startPosition = player.transform.position;

        // Kill the player.
        player.GetComponent<HealthSystem>()?.TakeDamage(9999f);

        // Apply input — a dead player should not respond to it.
        player.SetTestInput(new Vector3(0f, 0f, 1f));

        yield return null;
        yield return null;

        Assert.AreEqual(
            startPosition,
            player.transform.position,
            "Dead player moved when it should not have."
        );
    }
}
```

---

### Test Fixture Setup with Shared State

When multiple test methods share expensive setup (e.g., loading a large ScriptableObject), use `[OneTimeSetUp]` to run it once per fixture rather than per test.

```csharp
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Demonstrates [OneTimeSetUp] / [OneTimeTearDown] for expensive fixtures
/// vs [SetUp] / [TearDown] for per-test state.
/// </summary>
[TestFixture]
public class LootTableTests
{
    // Shared across all tests in this fixture — created once.
    private LootTable _lootTable;

    // Per-test state — reset before each test.
    private System.Random _rng;

    [OneTimeSetUp]
    public void FixtureSetUp()
    {
        // Load the ScriptableObject asset from the project.
        // This is slow (file I/O) — run once, not before every test.
        _lootTable = Resources.Load<LootTable>("TestData/StandardLootTable");
        Assert.IsNotNull(_lootTable, "TestData/StandardLootTable not found. " +
            "Ensure the asset exists at Assets/Resources/TestData/.");
    }

    [OneTimeTearDown]
    public void FixtureTearDown()
    {
        Resources.UnloadAsset(_lootTable);
        _lootTable = null;
    }

    [SetUp]
    public void TestSetUp()
    {
        // Reset the RNG seed before each test for deterministic results.
        _rng = new System.Random(seed: 42);
    }

    [Test]
    public void Roll_Always_ReturnsItemFromTable()
    {
        LootItem item = _lootTable.Roll(_rng);
        Assert.IsNotNull(item, "Roll returned null — table may be empty.");
        Assert.IsTrue(_lootTable.Contains(item), "Rolled item is not in the loot table.");
    }

    [Test]
    public void Roll_HighRarityItems_AppearLessFrequentlyThanCommon()
    {
        int commonCount = 0;
        int rareCount   = 0;
        const int trials = 10_000;

        for (int i = 0; i < trials; i++)
        {
            LootItem item = _lootTable.Roll(_rng);
            if (item.Rarity == Rarity.Common) commonCount++;
            if (item.Rarity == Rarity.Rare)   rareCount++;
        }

        Assert.Greater(commonCount, rareCount,
            $"Common ({commonCount}) should appear more often than Rare ({rareCount}).");
    }
}
```

---

### Interface-Based Mocking (IInputProvider)

Extract input behind an interface so tests can inject deterministic input without `Input.GetAxis`.

```csharp
using NUnit.Framework;
using UnityEngine;

// ─── Interface (in production assembly) ──────────────────────────────────────

/// <summary>
/// Abstracts Unity's input system. Inject in production code.
/// Enables testing input-dependent logic without the player loop.
/// </summary>
public interface IInputProvider
{
    Vector2 MovementInput { get; }
    bool    JumpPressed   { get; }
    bool    AttackPressed { get; }
}

// ─── Real implementation (production) ────────────────────────────────────────

/// <summary>
/// Reads from Unity's legacy Input system. Registered with DI container on startup.
/// </summary>
public class UnityInputProvider : IInputProvider
{
    public Vector2 MovementInput => new(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    public bool    JumpPressed   => Input.GetButtonDown("Jump");
    public bool    AttackPressed => Input.GetButtonDown("Fire1");
}

// ─── Test double (test assembly only) ────────────────────────────────────────

/// <summary>
/// Fake input provider for tests. Set properties directly to simulate any input state.
/// </summary>
public class FakeInputProvider : IInputProvider
{
    public Vector2 MovementInput { get; set; } = Vector2.zero;
    public bool    JumpPressed   { get; set; } = false;
    public bool    AttackPressed { get; set; } = false;
}

// ─── Production component using the interface ─────────────────────────────────

/// <summary>
/// MonoBehaviour that accepts an IInputProvider for testability.
/// In production, inject via Awake from a service locator or DI container.
/// </summary>
public class PlayerController : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 5f;

    private IInputProvider _input;
    private bool _isDead;

    public void SetInputProvider(IInputProvider provider) => _input = provider;

    /// <summary>Test-only convenience: sets a one-shot fixed input vector.</summary>
    public void SetTestInput(Vector3 direction)
    {
        _input = new FakeInputProvider { MovementInput = new Vector2(direction.x, direction.z) };
    }

    private void Awake()
    {
        _input ??= new UnityInputProvider(); // default to real input if not injected
    }

    private void Update()
    {
        if (_isDead || _input == null) return;

        Vector2 move = _input.MovementInput;
        transform.Translate(new Vector3(move.x, 0f, move.y) * (_moveSpeed * Time.deltaTime));
    }

    public void Die() => _isDead = true;
}

// ─── EditMode test using the fake ─────────────────────────────────────────────

[TestFixture]
public class PlayerControllerInputTests
{
    private GameObject        _go;
    private PlayerController  _player;
    private FakeInputProvider _fakeInput;

    [SetUp]
    public void SetUp()
    {
        _go     = new GameObject("TestPlayer");
        _player = _go.AddComponent<PlayerController>();

        _fakeInput = new FakeInputProvider();
        _player.SetInputProvider(_fakeInput);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_go);
    }

    [Test]
    public void Movement_WithRightInput_MovesOnXAxis()
    {
        _fakeInput.MovementInput = Vector2.right;

        Vector3 before = _player.transform.position;

        // Manually invoke Update — safe in EditMode tests via reflection or
        // a public ManualUpdate() method on the component.
        _player.SendMessage("Update"); // acceptable in tests; avoid in production

        Assert.Greater(_player.transform.position.x, before.x,
            "Player should move right when right input is applied.");
    }

    [Test]
    public void Movement_WhenDead_DoesNotMove()
    {
        _fakeInput.MovementInput = Vector2.right;
        _player.Die();

        Vector3 before = _player.transform.position;
        _player.SendMessage("Update");

        Assert.AreEqual(before, _player.transform.position,
            "Dead player should not move regardless of input.");
    }
}
```

---

### Assembly Definition for Test Assemblies

Tests must be in their own assembly definitions to prevent them from shipping in builds. This is the standard layout.

```json
// File: Assets/Tests/EditMode/MyProject.Tests.EditMode.asmdef
{
    "name": "MyProject.Tests.EditMode",
    "rootNamespace": "MyProject.Tests",
    "references": [
        "MyProject.Runtime",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

```json
// File: Assets/Tests/PlayMode/MyProject.Tests.PlayMode.asmdef
{
    "name": "MyProject.Tests.PlayMode",
    "rootNamespace": "MyProject.Tests.PlayMode",
    "references": [
        "MyProject.Runtime",
        "UnityEngine.TestRunner"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

Key fields:
- `autoReferenced: false` — prevents Unity from auto-including this assembly in non-test builds.
- `overrideReferences: true` + explicit `precompiledReferences` — ensures NUnit is referenced correctly.
- EditMode assembly references both `UnityEngine.TestRunner` and `UnityEditor.TestRunner`.
- PlayMode assembly references only `UnityEngine.TestRunner` (no Editor reference).

---

### UnityTest Coroutine-Based Test

For PlayMode tests that need to wait for async operations (animation, tween, AI tick).

```csharp
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

[TestFixture]
public class EnemyAIPlayModeTests
{
    private GameObject _enemyGO;
    private EnemyAI    _enemy;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _enemyGO = new GameObject("TestEnemy");
        _enemy   = _enemyGO.AddComponent<EnemyAI>();

        // Wait one frame for Awake and Start to complete.
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Object.Destroy(_enemyGO);
        // Yield one frame to let Destroy process before the next test begins.
        yield return null;
    }

    [UnityTest]
    public IEnumerator EnemyAI_AfterAlert_EntersChaseState()
    {
        Assert.AreEqual(EnemyState.Patrol, _enemy.CurrentState,
            "Enemy should start in Patrol state.");

        _enemy.Alert(alertPosition: Vector3.zero);

        // Wait for the state machine to process the alert on the next tick.
        // Yield for a fixed amount of real time rather than a fixed frame count
        // when the behavior depends on timing rather than frame count.
        yield return new WaitForSeconds(0.1f);

        Assert.AreEqual(EnemyState.Chase, _enemy.CurrentState,
            "Enemy should enter Chase state after being alerted.");
    }

    [UnityTest]
    public IEnumerator EnemyAI_AfterLoseTarget_ReturnsToPatrol()
    {
        _enemy.Alert(alertPosition: Vector3.zero);
        yield return new WaitForSeconds(0.1f);

        // Simulate losing the target.
        _enemy.LoseTarget();
        yield return new WaitForSeconds(_enemy.ChaseTimeoutSeconds + 0.1f);

        Assert.AreEqual(EnemyState.Patrol, _enemy.CurrentState,
            "Enemy should return to Patrol after chase timeout.");
    }
}
```

## Anti-Examples

### Using PlayMode When EditMode Suffices

```csharp
// BAD — PlayMode test for pure math. This adds ~300 ms overhead per test
// and requires the player loop when the code under test has no Unity dependencies.
[UnityTest]
public IEnumerator DamageFormula_WithCriticalHit_DoublesBaseDamage()
{
    float result = DamageCalculator.Calculate(baseDamage: 50f, isCritical: true);
    Assert.AreEqual(100f, result, delta: 0.001f);
    yield return null; // pointless — nothing async happens here
}

// GOOD — EditMode test. 300× faster. Same assertion, same coverage.
[Test]
public void DamageFormula_WithCriticalHit_DoublesBaseDamage()
{
    float result = DamageCalculator.Calculate(baseDamage: 50f, isCritical: true);
    Assert.AreEqual(100f, result, delta: 0.001f);
}
```

---

### Testing Unity Engine Behavior

```csharp
// BAD — tests that Rigidbody responds to AddForce.
// This is Unity's responsibility, not yours. If this breaks, it is a Unity bug.
// Writing this test adds maintenance cost with zero defect-detection value.
[UnityTest]
public IEnumerator Rigidbody_AfterAddForce_MovesInExpectedDirection()
{
    var go = new GameObject();
    var rb = go.AddComponent<Rigidbody>();
    rb.AddForce(Vector3.forward * 10f, ForceMode.Impulse);
    yield return new WaitForFixedUpdate();
    Assert.Greater(go.transform.position.z, 0f); // testing Unity's physics engine
}

// GOOD — test your game logic, not Unity's.
// If EnemyShip.FireProjectile() calls AddForce with the correct parameters,
// test that it called the interface with the right values using a fake physics service.
[Test]
public void EnemyShip_FireProjectile_AppliesCorrectForwardImpulse()
{
    var fakePhysics = new FakePhysicsService();
    var ship = new EnemyShip(fakePhysics);

    ship.FireProjectile();

    Assert.AreEqual(Vector3.forward * 10f, fakePhysics.LastAppliedForce);
}
```

---

### Tests That Depend on Frame Timing

```csharp
// BAD — assumes the test runs at exactly 60 fps. Fails on slow CI machines
// where Time.deltaTime is larger, causing more movement per frame.
[UnityTest]
public IEnumerator Player_After10Frames_IsAtExpectedPosition()
{
    for (int i = 0; i < 10; i++)
        yield return null;

    // This breaks at 30 fps — 10 frames = 333 ms, not 166 ms.
    Assert.AreEqual(new Vector3(0f, 0f, 8.3f), _player.transform.position);
}

// GOOD — test based on elapsed time, not frame count, for time-dependent behavior.
// Or better: test the logic unit directly with a known deltaTime, no frame dependency.
[Test]
public void Player_MovesCorrectDistanceForGivenDeltaTime()
{
    float moveSpeed = 5f;
    float deltaTime = 0.016f; // 60 fps — fixed, controlled value
    float expectedDistance = moveSpeed * deltaTime;

    Vector3 result = PlayerMovement.CalculateMovement(
        direction: Vector3.forward,
        speed:     moveSpeed,
        deltaTime: deltaTime
    );

    Assert.AreEqual(expectedDistance, result.magnitude, delta: 0.001f);
}
```

---

### No Test Assembly Definition

```csharp
// BAD — test file placed anywhere under Assets/ without a .asmdef, or with
// an .asmdef that has autoReferenced:true.
// Result: test code compiles into the runtime assembly and ships in the build.
// [Assembly: MyProject.Runtime.dll contains test code]

// GOOD — tests in a dedicated .asmdef with autoReferenced:false.
// Unity's build process strips assemblies that are not referenced by the final build.
// See the "Assembly Definition for Test Assemblies" example above.
```

## Cross-References

- Related skills: `hades:component-design` (interface extraction for testability, single-responsibility components make tests easier), `hades:data-modeling` (data structures that are testable without scene context)
- Hades MCP tools used in this skill:
  - `search_by_name` — find existing test files and assembly definitions
  - `get_project_summary` — project structure, assembly layout, package vs application
  - `recall_memory` — documented testing conventions and coverage targets
  - `propose_memory_update` — record new testing patterns for consistency
- Unity docs: [Unity Test Framework](https://docs.unity3d.com/Packages/com.unity.test-framework@latest), [EditMode tests](https://docs.unity3d.com/Packages/com.unity.test-framework@latest/manual/edit-mode-vs-play-mode-tests.html), [UnityTest attribute](https://docs.unity3d.com/Packages/com.unity.test-framework@latest/api/UnityEngine.TestTools.UnityTestAttribute.html), [Assembly definitions](https://docs.unity3d.com/6000.0/Documentation/Manual/ScriptCompilationAssemblyDefinitionFiles.html), [Running tests from command line](https://docs.unity3d.com/Packages/com.unity.test-framework@latest/manual/reference-command-line.html)
