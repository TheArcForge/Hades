---
description: "Use when implementing AI and NPC behavior — state machines, behavior trees, GOAP, NavMesh navigation, decision-making patterns, and when to use each approach."
---

# Unity AI Behavior

Guidance for designing and implementing NPC and AI systems in Unity 6+: choosing the right architecture for the complexity of the agent, wiring NavMesh navigation, building detection and sensing pipelines, and avoiding common performance and design traps.

## When to Apply

Activate when the conversation involves:
- Choosing an AI architecture (state machine vs behavior tree vs GOAP vs utility AI)
- Implementing NPC patrol, chase, attack, or idle behaviors
- Setting up NavMeshAgent navigation, obstacles, or off-mesh links
- Building sensing systems (sight, hearing, trigger-based awareness)
- Designing steering or movement behaviors (seek, flee, flocking)
- Reviewing AI code that has grown large, tangled, or hard to extend
- Animating NPCs whose animation state is driven by AI state

Do NOT activate for purely cosmetic NPC animation, dialogue systems, or cutscene scripting — those go to `hades:animation-workflow` or the scene-authoring skill.

## Project Context Check

Before making recommendations:

1. **Check existing patterns in the graph:**
   - Call `find_components_using_pattern("NavMeshAgent")` — reveals whether NavMesh navigation is already used and how agents are configured
   - Call `search_by_name("*AI*")` or `search_by_name("*State*")` — discovers existing AI scripts and naming conventions
   - Call `find_components_using_pattern("Animator")` — AI and animation state are often coupled; understand the existing linkage before recommending a new architecture
   - Call `search_by_name("*BehaviorTree*")` or `search_by_name("*GOAP*")` — detects whether a third-party AI framework is already in use

2. **Check team decisions in memory:**
   - Call `recall_memory("AI behavior state machine")` to find documented AI patterns or previously chosen architecture
   - Call `recall_memory("NavMesh navigation patrol")` to find navigation conventions
   - Check validation status — if a recalled decision shows `warning`, surface the conflict before proceeding

3. **Adapt recommendations based on findings:**
   - If the project already has state machines → align with the existing pattern; do not introduce a behavior tree for one new NPC
   - If a third-party BT framework is in use → write leaf nodes for it rather than inventing a second tree system
   - After the user accepts an AI architecture decision not yet documented, call `propose_memory_update` to record it

## Decision Framework

### AI Architecture Selection

Answer these questions in order. The first branch that matches selects the architecture.

```
How many discrete states does the NPC have?
├── Fewer than 5, transitions are straightforward (e.g., Idle → Patrol → Chase)
│   └── Simple Enum State Machine
│       - Flat switch/enum; all logic in one class
│       - Fast to write, easy to debug, no overhead
│       - Limit: adding states requires touching the central switch
│
├── 5–15 states, some share sub-behaviors (e.g., guard has Alert/Investigate/Combat sub-tree)
│   └── Hierarchical State Machine (State Pattern)
│       - IState interface with Enter / Tick / Exit
│       - StateContext owns the active state; states swap each other out
│       - Parent states can contain child states for shared transitions
│       - Limit: complex cross-state communication can become tangled
│
├── Priority-based interruption required (e.g., "attack if close, else chase,
│   else patrol; abandon any of those the moment a higher priority is ready")
│   └── Behavior Tree
│       - Composite nodes: Selector (first success), Sequence (all must succeed)
│       - Leaf nodes: Action (does work), Condition (tests world state)
│       - Decorators: Repeat, Invert, Timeout
│       - Naturally handles interruption via re-evaluation at the root each tick
│       - Limit: stateless traversal requires a blackboard for shared data
│
├── Many possible actions, emergent behavior from preconditions/effects preferred
│   └── GOAP (Goal-Oriented Action Planning)
│       - Actions declare WorldState preconditions and effects
│       - Planner searches for cheapest action sequence to reach goal
│       - Emergent: the agent finds solutions you never scripted
│       - Limit: high complexity, expensive planning (run on a job or async)
│
└── Continuous scored decisions — "how much do I want to attack vs. flee vs. heal?"
    └── Utility AI
        - Each action has a scorer returning 0–1 (e.g., HealthRatio, DistanceToEnemy)
        - Agent picks the highest-scoring action each tick
        - Easily tunable by designers via curves
        - Limit: scores can conflict in unexpected ways; needs iteration time
```

### NavMesh Navigation

| Scenario | Approach |
|---|---|
| Simple point-to-point movement | `agent.SetDestination(target.position)` once on state enter, then poll `agent.remainingDistance` |
| Patrol between fixed waypoints | Waypoint Transform array on an SO or component; advance index on arrival |
| Dynamic obstacle avoidance | Add `NavMeshObstacle` (Carve mode) on moving blockers; keep agent `updatePosition = true` |
| Jump/climb between surfaces | Bake Off-Mesh Links at navmesh bake time, or add `OffMeshLink` component manually |
| Partial paths (unreachable target) | Check `agent.pathStatus == NavMeshPathStatus.PathPartial` after `SetDestination` |
| Warp without animation | `agent.Warp(position)` — teleports and snaps to navmesh without pathfinding |

**Rules:**
- Never call `SetDestination` every frame when the target is stationary — it rebuilds the path each call
- Cache the target position and only call `SetDestination` again when the target has moved by a meaningful threshold (e.g., `> 0.5f`)
- Set `agent.stoppingDistance` to match the NPC's attack or interaction range so it stops naturally
- Disable the agent component before manually moving the transform (e.g., during a knockback), then re-enable it

### Sensing Systems

**Trigger-based (coarse, cheap):** `OnTriggerEnter` / `OnTriggerStay` on a sphere collider sized to the detection radius. Reliable but has no line-of-sight or angle check.

**OverlapSphere + line-of-sight (medium fidelity):** Call `Physics.OverlapSphere` in a fixed coroutine (every 0.2 s) rather than `Update`. For each candidate, cast a ray to check occlusion. Angle-check against agent forward for a field-of-view cone.

**Full awareness tier (high fidelity):** Combine OverlapSphere, FOV angle check, and a Physics.Raycast visibility check. Use a noise/alert level float rather than a binary "seen" flag for gradual detection.

### Steering Behaviors

| Behavior | Description |
|---|---|
| Seek | Accelerate toward target position |
| Flee | Accelerate away from threat position |
| Arrive | Seek with a deceleration radius — slows as it approaches |
| Wander | Seek a point that orbits the agent's forward direction, randomly displaced each frame |
| Pursue | Seek the predicted future position of a moving target |
| Evade | Flee the predicted future position of a pursuer |
| Separation | Sum of repulsion vectors from nearby agents, weighted by inverse distance |
| Cohesion | Seek the centroid of nearby agents |
| Alignment | Steer toward the average heading of nearby agents |

For most Unity 6 projects, prefer NavMeshAgent for environmental navigation and apply steering behaviors as velocity offsets (via `agent.velocity`) for local social forces (separation, alignment).

## Code Examples

### Simple Enum State Machine

```csharp
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Simple four-state NPC: Idle, Patrol, Chase, Attack.
/// Suitable for enemies with fewer than five distinct behaviors.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class SimpleEnemyAI : MonoBehaviour
{
    public enum State { Idle, Patrol, Chase, Attack }

    [Header("Detection")]
    [SerializeField] private float _sightRange    = 12f;
    [SerializeField] private float _attackRange   = 2f;
    [SerializeField] private float _sightAngle    = 110f;
    [SerializeField] private LayerMask _playerMask;
    [SerializeField] private LayerMask _obstacleMask;

    [Header("Patrol")]
    [SerializeField] private Transform[] _waypoints;

    private NavMeshAgent _agent;
    private Transform    _player;
    private State        _state = State.Idle;
    private int          _waypointIndex;
    private float        _idleTimer;

    private const float IdleDuration  = 2f;
    private const float ScanInterval  = 0.2f;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
    }

    private void Start()
    {
        // Find player — cache the reference once
        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null) _player = playerObj.transform;

        InvokeRepeating(nameof(ScanForPlayer), 0f, ScanInterval);
    }

    private void Update()
    {
        switch (_state)
        {
            case State.Idle:   TickIdle();   break;
            case State.Patrol: TickPatrol(); break;
            case State.Chase:  TickChase();  break;
            case State.Attack: TickAttack(); break;
        }
    }

    // ── State ticks ──────────────────────────────────────────────────────────

    private void TickIdle()
    {
        _idleTimer += Time.deltaTime;
        if (_idleTimer >= IdleDuration)
        {
            _idleTimer = 0f;
            EnterPatrol();
        }
    }

    private void TickPatrol()
    {
        if (_agent.remainingDistance < 0.5f)
        {
            _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
            _agent.SetDestination(_waypoints[_waypointIndex].position);
        }
    }

    private void TickChase()
    {
        if (_player == null) { EnterPatrol(); return; }

        float dist = Vector3.Distance(transform.position, _player.position);
        if (dist <= _attackRange)
        {
            EnterAttack();
        }
        else if (dist > _sightRange * 1.5f)
        {
            // Lost the player — return to patrol
            EnterPatrol();
        }
        else
        {
            // Refresh destination only when player has moved meaningfully
            if (Vector3.Distance(_agent.destination, _player.position) > 0.5f)
                _agent.SetDestination(_player.position);
        }
    }

    private void TickAttack()
    {
        if (_player == null) { EnterPatrol(); return; }

        float dist = Vector3.Distance(transform.position, _player.position);
        if (dist > _attackRange)
        {
            EnterChase();
        }
        else
        {
            transform.LookAt(_player);
            // Trigger attack animation / hitbox here
        }
    }

    // ── State transitions ────────────────────────────────────────────────────

    private void EnterPatrol()
    {
        _state = State.Patrol;
        _agent.isStopped = false;
        if (_waypoints.Length > 0)
            _agent.SetDestination(_waypoints[_waypointIndex].position);
    }

    private void EnterChase()
    {
        _state = State.Chase;
        _agent.isStopped = false;
    }

    private void EnterAttack()
    {
        _state = State.Attack;
        _agent.isStopped = true;
    }

    // ── Sensing ──────────────────────────────────────────────────────────────

    private void ScanForPlayer()
    {
        if (_player == null || _state == State.Attack) return;

        if (CanSeePlayer())
        {
            if (_state != State.Chase && _state != State.Attack)
                EnterChase();
        }
    }

    private bool CanSeePlayer()
    {
        Vector3 toPlayer = _player.position - transform.position;
        float dist = toPlayer.magnitude;

        if (dist > _sightRange) return false;

        float angle = Vector3.Angle(transform.forward, toPlayer);
        if (angle > _sightAngle * 0.5f) return false;

        // Line-of-sight check
        if (Physics.Raycast(transform.position + Vector3.up, toPlayer.normalized,
                            dist, _obstacleMask))
            return false;

        return true;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 1f, 0f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, _sightRange);
        Gizmos.color = new Color(1f, 0f, 0f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, _attackRange);
    }
#endif
}
```

---

### Hierarchical State Machine (State Pattern)

```csharp
using UnityEngine;
using UnityEngine.AI;

// ── State interface ───────────────────────────────────────────────────────────

/// <summary>
/// Contract for all NPC states. Enter is called once on transition;
/// Tick is called every Update; Exit is called once before leaving.
/// </summary>
public interface IState
{
    void Enter();
    void Tick();
    void Exit();
}

// ── State context ─────────────────────────────────────────────────────────────

/// <summary>
/// Owns the active state and drives the state machine.
/// MonoBehaviours construct concrete state objects in Awake and hand them here.
/// </summary>
public class StateContext
{
    private IState _current;

    public void TransitionTo(IState next)
    {
        _current?.Exit();
        _current = next;
        _current?.Enter();
    }

    public void Tick() => _current?.Tick();
}

// ── Concrete states ───────────────────────────────────────────────────────────

public class IdleState : IState
{
    private readonly GuardAI _guard;
    private float _timer;
    private readonly float _duration;

    public IdleState(GuardAI guard, float duration) { _guard = guard; _duration = duration; }

    public void Enter()  { _guard.Agent.isStopped = true; _timer = 0f; }
    public void Exit()   { }

    public void Tick()
    {
        _timer += Time.deltaTime;
        if (_timer >= _duration)
            _guard.Context.TransitionTo(_guard.PatrolState);
    }
}

public class PatrolState : IState
{
    private readonly GuardAI    _guard;
    private readonly Transform[] _waypoints;
    private int _index;

    public PatrolState(GuardAI guard, Transform[] waypoints)
    {
        _guard     = guard;
        _waypoints = waypoints;
    }

    public void Enter()
    {
        _guard.Agent.isStopped = false;
        if (_waypoints.Length > 0)
            _guard.Agent.SetDestination(_waypoints[_index].position);
    }

    public void Exit() { }

    public void Tick()
    {
        if (_waypoints.Length == 0) return;

        if (_guard.Agent.remainingDistance < 0.4f)
        {
            _index = (_index + 1) % _waypoints.Length;
            _guard.Agent.SetDestination(_waypoints[_index].position);
        }

        if (_guard.CanSeePlayer())
            _guard.Context.TransitionTo(_guard.AlertState);
    }
}

public class AlertState : IState
{
    private readonly GuardAI _guard;
    private float _alertTimer;
    private const float AlertWindow = 1.5f;

    public AlertState(GuardAI guard) { _guard = guard; }

    public void Enter()
    {
        _guard.Agent.isStopped = true;
        _alertTimer = 0f;
        Debug.Log($"{_guard.name}: Entered alert!");
    }

    public void Exit() { _guard.Agent.isStopped = false; }

    public void Tick()
    {
        _alertTimer += Time.deltaTime;

        if (_guard.DistanceToPlayer() <= _guard.AttackRange)
        {
            _guard.Context.TransitionTo(_guard.AttackState);
            return;
        }

        if (_alertTimer >= AlertWindow)
            _guard.Context.TransitionTo(_guard.ChaseState);
    }
}

public class ChaseState : IState
{
    private readonly GuardAI _guard;

    public ChaseState(GuardAI guard) { _guard = guard; }

    public void Enter()  { _guard.Agent.isStopped = false; }
    public void Exit()   { }

    public void Tick()
    {
        if (_guard.Player == null) { _guard.Context.TransitionTo(_guard.PatrolState); return; }

        float dist = _guard.DistanceToPlayer();

        if (dist <= _guard.AttackRange)
        {
            _guard.Context.TransitionTo(_guard.AttackState);
            return;
        }

        if (dist > _guard.SightRange * 1.5f)
        {
            _guard.Context.TransitionTo(_guard.PatrolState);
            return;
        }

        if (Vector3.Distance(_guard.Agent.destination, _guard.Player.position) > 0.5f)
            _guard.Agent.SetDestination(_guard.Player.position);
    }
}

public class AttackState : IState
{
    private readonly GuardAI _guard;
    private float _cooldown;
    private const float AttackRate = 1.2f;

    public AttackState(GuardAI guard) { _guard = guard; }

    public void Enter()  { _guard.Agent.isStopped = true; }
    public void Exit()   { }

    public void Tick()
    {
        if (_guard.Player == null) { _guard.Context.TransitionTo(_guard.PatrolState); return; }

        float dist = _guard.DistanceToPlayer();

        if (dist > _guard.AttackRange)
        {
            _guard.Context.TransitionTo(_guard.ChaseState);
            return;
        }

        _guard.transform.LookAt(_guard.Player);

        _cooldown -= Time.deltaTime;
        if (_cooldown <= 0f)
        {
            _cooldown = AttackRate;
            // Trigger attack animation / deal damage
            Debug.Log($"{_guard.name}: Attack!");
        }
    }
}

// ── GuardAI coordinator ───────────────────────────────────────────────────────

/// <summary>
/// Hierarchical state machine for a guard NPC.
/// Owns all state objects; states navigate via public references.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class GuardAI : MonoBehaviour
{
    [Header("Detection")]
    [SerializeField] private float _sightRange  = 14f;
    [SerializeField] private float _attackRange = 2f;
    [SerializeField] private float _sightAngle  = 120f;
    [SerializeField] private LayerMask _obstacleMask;

    [Header("Patrol")]
    [SerializeField] private Transform[] _waypoints;

    // Public so states can read them
    public NavMeshAgent Agent     { get; private set; }
    public Transform    Player    { get; private set; }
    public StateContext Context   { get; private set; }
    public float SightRange       => _sightRange;
    public float AttackRange      => _attackRange;

    // State objects (public so states can reference siblings)
    public IState IdleState   { get; private set; }
    public IState PatrolState { get; private set; }
    public IState AlertState  { get; private set; }
    public IState ChaseState  { get; private set; }
    public IState AttackState { get; private set; }

    private void Awake()
    {
        Agent = GetComponent<NavMeshAgent>();

        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null) Player = playerObj.transform;

        Context = new StateContext();

        // Construct states once — they are plain C# objects, not MonoBehaviours
        IdleState   = new IdleState(this, 2f);
        PatrolState = new PatrolState(this, _waypoints);
        AlertState  = new AlertState(this);
        ChaseState  = new ChaseState(this);
        AttackState = new AttackState(this);
    }

    private void Start() => Context.TransitionTo(IdleState);

    private void Update() => Context.Tick();

    public float DistanceToPlayer() =>
        Player != null ? Vector3.Distance(transform.position, Player.position) : float.MaxValue;

    public bool CanSeePlayer()
    {
        if (Player == null) return false;

        Vector3 toPlayer = Player.position - transform.position;
        float dist = toPlayer.magnitude;
        if (dist > _sightRange) return false;

        float angle = Vector3.Angle(transform.forward, toPlayer);
        if (angle > _sightAngle * 0.5f) return false;

        if (Physics.Raycast(transform.position + Vector3.up * 1.5f,
                            toPlayer.normalized, dist, _obstacleMask))
            return false;

        return true;
    }
}
```

---

### Behavior Tree Node Interface

```csharp
using System.Collections.Generic;
using UnityEngine;

// ── Result type ───────────────────────────────────────────────────────────────

public enum NodeResult { Success, Failure, Running }

// ── Base interface ────────────────────────────────────────────────────────────

public interface IBTNode
{
    NodeResult Evaluate(BTBlackboard board);
}

// ── Composites ────────────────────────────────────────────────────────────────

/// <summary>
/// Runs children left to right. Returns Success on the first child that succeeds.
/// Returns Failure only if all children fail.
/// </summary>
public class Selector : IBTNode
{
    private readonly List<IBTNode> _children;
    public Selector(params IBTNode[] children) => _children = new List<IBTNode>(children);

    public NodeResult Evaluate(BTBlackboard board)
    {
        foreach (IBTNode child in _children)
        {
            NodeResult result = child.Evaluate(board);
            if (result != NodeResult.Failure) return result;
        }
        return NodeResult.Failure;
    }
}

/// <summary>
/// Runs children left to right. Returns Failure on the first child that fails.
/// Returns Success only if all children succeed.
/// </summary>
public class Sequence : IBTNode
{
    private readonly List<IBTNode> _children;
    public Sequence(params IBTNode[] children) => _children = new List<IBTNode>(children);

    public NodeResult Evaluate(BTBlackboard board)
    {
        foreach (IBTNode child in _children)
        {
            NodeResult result = child.Evaluate(board);
            if (result != NodeResult.Success) return result;
        }
        return NodeResult.Success;
    }
}

// ── Decorator ─────────────────────────────────────────────────────────────────

/// <summary>Inverts the result of a child node. Running passes through unchanged.</summary>
public class Inverter : IBTNode
{
    private readonly IBTNode _child;
    public Inverter(IBTNode child) => _child = child;

    public NodeResult Evaluate(BTBlackboard board)
    {
        NodeResult result = _child.Evaluate(board);
        return result switch
        {
            NodeResult.Success => NodeResult.Failure,
            NodeResult.Failure => NodeResult.Success,
            _                  => NodeResult.Running
        };
    }
}

// ── Blackboard ────────────────────────────────────────────────────────────────

/// <summary>
/// Shared data store for all nodes in the tree.
/// Typed accessors avoid boxing for common value types.
/// </summary>
public class BTBlackboard
{
    private readonly Dictionary<string, object> _data = new();

    public void Set<T>(string key, T value) => _data[key] = value;

    public bool TryGet<T>(string key, out T value)
    {
        if (_data.TryGetValue(key, out object raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }
}

// ── Sample action leaf nodes ──────────────────────────────────────────────────

public class IsPlayerInRangeCondition : IBTNode
{
    private readonly Transform _self;
    private readonly float     _range;

    public IsPlayerInRangeCondition(Transform self, float range)
    {
        _self  = self;
        _range = range;
    }

    public NodeResult Evaluate(BTBlackboard board)
    {
        if (!board.TryGet<Transform>("player", out Transform player))
            return NodeResult.Failure;

        return Vector3.Distance(_self.position, player.position) <= _range
            ? NodeResult.Success
            : NodeResult.Failure;
    }
}

public class ChasePlayerAction : IBTNode
{
    private readonly UnityEngine.AI.NavMeshAgent _agent;
    private readonly float _stopDistance;

    public ChasePlayerAction(UnityEngine.AI.NavMeshAgent agent, float stopDistance)
    {
        _agent        = agent;
        _stopDistance = stopDistance;
    }

    public NodeResult Evaluate(BTBlackboard board)
    {
        if (!board.TryGet<Transform>("player", out Transform player))
            return NodeResult.Failure;

        _agent.stoppingDistance = _stopDistance;

        if (Vector3.Distance(_agent.destination, player.position) > 0.5f)
            _agent.SetDestination(player.position);

        return NodeResult.Running;
    }
}

// ── BT host MonoBehaviour ─────────────────────────────────────────────────────

/// <summary>
/// Builds and ticks a behavior tree each frame.
/// Tree is constructed once in Awake; the blackboard is updated each tick.
/// </summary>
[RequireComponent(typeof(UnityEngine.AI.NavMeshAgent))]
public class BehaviorTreeAI : MonoBehaviour
{
    [SerializeField] private float _sightRange  = 12f;
    [SerializeField] private float _attackRange = 2f;

    private IBTNode   _root;
    private BTBlackboard _board;
    private UnityEngine.AI.NavMeshAgent _agent;

    private void Awake()
    {
        _agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        _board = new BTBlackboard();

        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null)
            _board.Set("player", playerObj.transform);

        // Tree: try to attack; if not in attack range, chase; else wander
        _root = new Selector(
            new Sequence(
                new IsPlayerInRangeCondition(transform, _attackRange),
                new AttackAction(this)           // omitted for brevity
            ),
            new Sequence(
                new IsPlayerInRangeCondition(transform, _sightRange),
                new ChasePlayerAction(_agent, _attackRange)
            )
            // additional branches (patrol, wander) would follow here
        );
    }

    private void Update() => _root.Evaluate(_board);
}

// Stub so the tree compiles; replace with real attack implementation
public class AttackAction : IBTNode
{
    private readonly BehaviorTreeAI _ai;
    public AttackAction(BehaviorTreeAI ai) { _ai = ai; }
    public NodeResult Evaluate(BTBlackboard board) { return NodeResult.Running; }
}
```

---

### Detection System with Awareness Level

```csharp
using System.Collections;
using UnityEngine;

/// <summary>
/// Gradual detection model. The NPC has an awareness float (0 = unaware, 1 = fully detected).
/// Awareness rises while the player is visible, decays when not.
/// Subscribers respond to threshold crossings via C# events.
/// </summary>
public class NPCDetection : MonoBehaviour
{
    [Header("Sensing")]
    [SerializeField] private float _detectionRadius = 15f;
    [SerializeField] private float _fieldOfViewAngle = 120f;
    [SerializeField] private LayerMask _playerMask;
    [SerializeField] private LayerMask _obstacleMask;

    [Header("Awareness")]
    [SerializeField] private float _riseRate  = 1.5f;   // per second while visible
    [SerializeField] private float _decayRate = 0.8f;   // per second when not visible

    [Header("Thresholds")]
    [SerializeField] private float _alertThreshold   = 0.4f;
    [SerializeField] private float _detectedThreshold = 1.0f;

    public float Awareness { get; private set; }

    public event System.Action OnAlert;
    public event System.Action OnDetected;
    public event System.Action OnLost;

    private bool _wasAlert;
    private bool _wasDetected;

    private const float ScanTick = 0.15f;

    private void OnEnable()  => StartCoroutine(ScanCoroutine());
    private void OnDisable() => StopAllCoroutines();

    private IEnumerator ScanCoroutine()
    {
        var wait = new WaitForSeconds(ScanTick);

        while (true)
        {
            bool canSee = CheckLineOfSight();

            if (canSee)
                Awareness = Mathf.Min(1f, Awareness + _riseRate  * ScanTick);
            else
                Awareness = Mathf.Max(0f, Awareness - _decayRate * ScanTick);

            FireThresholdEvents();
            yield return wait;
        }
    }

    private bool CheckLineOfSight()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, _detectionRadius, _playerMask);
        if (hits.Length == 0) return false;

        foreach (Collider hit in hits)
        {
            Vector3 toTarget = hit.transform.position - transform.position;
            float angle = Vector3.Angle(transform.forward, toTarget);
            if (angle > _fieldOfViewAngle * 0.5f) continue;

            if (Physics.Raycast(transform.position + Vector3.up * 1.5f,
                                toTarget.normalized, toTarget.magnitude, _obstacleMask))
                continue;

            return true;
        }

        return false;
    }

    private void FireThresholdEvents()
    {
        bool isAlert    = Awareness >= _alertThreshold;
        bool isDetected = Awareness >= _detectedThreshold;

        if (isAlert    && !_wasAlert)    OnAlert?.Invoke();
        if (isDetected && !_wasDetected) OnDetected?.Invoke();

        // Lost: was detected, awareness has dropped back below alert
        if (_wasDetected && !isAlert)
        {
            OnLost?.Invoke();
            _wasDetected = false;
        }

        _wasAlert    = isAlert;
        _wasDetected = isDetected;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 0f, 0.15f);
        Gizmos.DrawWireSphere(transform.position, _detectionRadius);

        // Draw FOV arc indicator
        Vector3 fovLeft  = Quaternion.Euler(0f, -_fieldOfViewAngle * 0.5f, 0f) * transform.forward;
        Vector3 fovRight = Quaternion.Euler(0f,  _fieldOfViewAngle * 0.5f, 0f) * transform.forward;
        Gizmos.color = Color.green;
        Gizmos.DrawRay(transform.position, fovLeft  * _detectionRadius);
        Gizmos.DrawRay(transform.position, fovRight * _detectionRadius);
    }
#endif
}
```

---

### NavMeshAgent Patrol with State Transitions

```csharp
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Minimal patrol NPC. Loops through a set of Transform waypoints.
/// Transitions to a chase mode when the player enters detection range.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class PatrolAgent : MonoBehaviour
{
    [SerializeField] private Transform[] _waypoints;
    [SerializeField] private float _detectionRange = 8f;
    [SerializeField] private LayerMask _playerMask;

    private NavMeshAgent _agent;
    private Transform    _player;
    private int          _waypointIndex;
    private bool         _chasing;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        GameObject p = GameObject.FindWithTag("Player");
        if (p != null) _player = p.transform;
    }

    private void Start()
    {
        if (_waypoints.Length > 0)
            _agent.SetDestination(_waypoints[0].position);
    }

    private void Update()
    {
        if (_player == null) return;

        bool playerNearby = Vector3.Distance(transform.position, _player.position) <= _detectionRange;

        if (playerNearby && !_chasing)
        {
            _chasing = true;
        }
        else if (!playerNearby && _chasing)
        {
            _chasing = false;
            _agent.SetDestination(_waypoints[_waypointIndex].position);
        }

        if (_chasing)
        {
            // Only update destination when the player has moved meaningfully
            if (Vector3.Distance(_agent.destination, _player.position) > 0.5f)
                _agent.SetDestination(_player.position);
        }
        else
        {
            // Advance to next waypoint on arrival
            if (!_agent.pathPending && _agent.remainingDistance < 0.4f)
            {
                _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
                _agent.SetDestination(_waypoints[_waypointIndex].position);
            }
        }
    }
}
```

## Anti-Examples

### Giant Switch Statement for AI States

```csharp
// BAD — as states grow, this class becomes unmanageable.
// Every new state touches the same file; the Tick method handles all concerns.
// Impossible to unit-test individual states.
void Update()
{
    switch (state)
    {
        case "idle":
            idleTimer += Time.deltaTime;
            if (idleTimer > 2f) { state = "patrol"; idleTimer = 0f; }
            break;
        case "patrol":
            // 40 lines of patrol logic
            break;
        case "chase":
            // 60 lines of chase logic
            break;
        // ... grows indefinitely
    }
}
```

Prefer: state pattern (IState / StateContext) or enum state machine with separate methods.

---

### SetDestination Every Frame

```csharp
// BAD — NavMesh recalculates the path on every SetDestination call.
// At 60 fps with 20 agents this rebuilds 1,200 paths per second.
void Update()
{
    agent.SetDestination(player.position);
}

// GOOD — only update when the target has moved by a meaningful threshold
private Vector3 _lastKnownPosition;

void Update()
{
    if (Vector3.Distance(_lastKnownPosition, player.position) > 0.5f)
    {
        _lastKnownPosition = player.position;
        agent.SetDestination(player.position);
    }
}
```

---

### Checking AI Logic Every Frame When Events Suffice

```csharp
// BAD — scanning 30 enemies for a player every frame burns CPU.
// OverlapSphere runs a physics query each call.
void Update()
{
    foreach (Enemy e in allEnemies)
        e.ScanForPlayer(); // each does Physics.OverlapSphere
}

// GOOD — drive sensing from a coroutine on each agent at a relaxed interval
private IEnumerator SenseLoop()
{
    var wait = new WaitForSeconds(0.2f);
    while (true)
    {
        ScanForPlayer();
        yield return wait;
    }
}
```

---

### Hardcoded Waypoint Positions

```csharp
// BAD — positions are not visible in the scene, break on level changes,
// and cannot be reused across prefab instances.
void Start()
{
    waypoints = new Vector3[]
    {
        new Vector3(10f, 0f, 5f),
        new Vector3(-3f, 0f, 12f),
        new Vector3(0f,  0f, 0f),
    };
}

// GOOD — serialized Transform array; drag scene objects in inspector.
// Works on any instance, visible in the scene, repositioned with gizmos.
[SerializeField] private Transform[] _waypoints;
```

---

### Choosing Behavior Trees for Simple Enemies

```csharp
// BAD — three-state guard implemented as a full behavior tree.
// The overhead of composites, leaf nodes, and a blackboard is not justified.
// Overkill adds complexity without benefit for <5 states.
_root = new Selector(
    new Sequence(new CheckAlertCondition(), new AlertAction()),
    new Sequence(new CheckIdleCondition(), new IdleAction()),
    new PatrolAction()
);

// GOOD — simple enum state machine; readable, fast, no infrastructure required.
switch (_state)
{
    case State.Idle:   TickIdle();   break;
    case State.Patrol: TickPatrol(); break;
    case State.Alert:  TickAlert();  break;
}
```

## Cross-References

- Related skills: `hades:component-design`, `hades:unity-performance`, `hades:unity-architect`
- Review skill: `hades:unity-reviewer`
- Hades MCP tools: `find_components_using_pattern`, `search_by_name`, `recall_memory`, `propose_memory_update`
- Unity docs: [NavMeshAgent](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AI.NavMeshAgent.html), [NavMesh baking](https://docs.unity3d.com/6000.0/Documentation/Manual/nav-BuildingNavMesh.html), [Physics.OverlapSphere](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.OverlapSphere.html), [Off-Mesh Links](https://docs.unity3d.com/6000.0/Documentation/Manual/nav-CreateOffMeshLink.html)
