---
description: "Use when adding multiplayer or networked features — Netcode for GameObjects vs Mirror vs Fishnet decisions, authority models, state synchronization, lobby systems, and network architecture patterns."
---

# Unity Networking

Guidance for introducing multiplayer into a Unity 6 project: choosing a networking framework, modeling authority, synchronizing state efficiently, and structuring lobby and matchmaking flows.

## When to Apply

Activate when the conversation involves:
- Choosing between Netcode for GameObjects, Mirror, or Fishnet
- Deciding which peer owns authority over a game object
- Synchronizing position, health, or other state across clients
- Implementing RPCs for ability activation, item pickup, or chat
- Setting up a lobby with player ready-up and host migration
- Spawning networked prefabs with proper ownership
- Designing a dedicated-server, listen-server, or relay topology
- Interest management or relevancy culling for large worlds

Do NOT activate for single-player AI behavior, local input handling, or physics-only questions — those go to `hades:unity-ai-behavior`, `hades:unity-input`, or `hades:unity-performance`.

## Project Context Check

Before making recommendations:

1. **Detect the networking framework already in use:**
   - Call `find_components_using_pattern("NetworkObject")` — presence indicates Netcode for GameObjects
   - Call `find_components_using_pattern("NetworkBehaviour")` — used by both NGO and Mirror; cross-reference with next check
   - Call `find_components_using_pattern("NetworkIdentity")` — Mirror-specific; confirms Mirror if found
   - Call `search_by_name("*Network*")` to surface any network-related scripts, managers, or config assets

2. **Check documented decisions in memory:**
   - Call `recall_memory("networking multiplayer authority")` to find any recorded framework choice or topology decision
   - If a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt recommendations based on findings:**
   - If NGO components already exist → default to NGO patterns for all new code
   - If Mirror components exist → use Mirror-flavored examples
   - If nothing is found and the project is greenfield → apply the Framework Selection decision tree below
   - After the user agrees on a framework or authority model not yet in memory, call `propose_memory_update` to record it

## Decision Framework

### Framework Selection

```
Is this a greenfield project or does the codebase already contain networking code?
│
├── Existing code detected
│   ├── NetworkIdentity found → Mirror (keep Mirror)
│   ├── NetworkObject found  → Netcode for GameObjects (keep NGO)
│   └── Other (MLAPI, old UNet) → evaluate migration cost before adding features
│
└── Greenfield
    │
    ├── Is the team on Unity 6 and targeting Steam/PC multiplayer with < 32 players?
    │   └── Netcode for GameObjects (NGO)
    │       + Official Unity package, first-party support
    │       + Integrates with Unity Gaming Services (Lobby, Relay, Matchmaker)
    │       + Best tooling for Unity 6 (Network Profiler, Multiplayer Play Mode)
    │       - Smaller community than Mirror; API still maturing
    │
    ├── Is the team experienced with UNet / needs a large ecosystem of addons?
    │   └── Mirror
    │       + Mature, MIT-licensed, large community
    │       + Drop-in UNet upgrade path
    │       + Many transports (KCP, Telepathy, WebGL, Steam)
    │       - Not first-party; must track upstream releases manually
    │
    └── Does the project need high-performance tick-based gameplay (e.g., fighting game, FPS)?
        └── Fishnet
            + Modern API with prediction/reconciliation built in
            + Excellent documentation and growing community
            + Good performance on dedicated server topology
            - Smaller ecosystem than Mirror; fewer transport options
```

### Authority Models

```
Who should own the canonical state for this object?
│
├── Server-authoritative (recommended for competitive or anti-cheat sensitive games)
│   - Server owns all mutable state; clients send inputs only
│   - Server validates inputs, applies results, and broadcasts deltas
│   - Prevents client-side cheating (speed hacks, teleportation, item duplication)
│   - Adds round-trip latency; requires client-side prediction for responsiveness
│
├── Host-mode (listen server) — acceptable for co-op or party games
│   - One player acts as both server and client
│   - Lower infrastructure cost; host has zero latency advantage
│   - Host migration is complex; host leaving ends the session unless handled
│
└── Client-authoritative — acceptable ONLY for cosmetics or non-competitive data
    - Client reports its own position; server trusts and replicates it
    - Fast and simple; trivially cheatable
    - Acceptable for: chat messages, emotes, non-scored cosmetic effects
    - NEVER use for health, currency, item ownership, or scored actions
```

### State Synchronization Strategy

| Data type | Frequency | NGO approach | Mirror approach |
|---|---|---|---|
| Position / rotation | Every physics tick | `NetworkTransform` (built-in) | `NetworkTransform` (Mirror component) |
| Scalar game state (HP, ammo) | On change | `NetworkVariable<T>` with `OnValueChanged` | `[SyncVar]` with hook |
| One-shot events (ability fire, death) | Once | `ServerRpc` → `ClientRpc` | `[Command]` → `[ClientRpc]` |
| Large infrequent blobs (inventory) | On change | Custom `INetworkSerializable` | Custom `SyncList` or manual serialize |

**Synchronization rules:**
- Do NOT use RPCs for data that changes every frame — use `NetworkVariable` / `SyncVar` and let the framework delta-compress
- Do NOT synchronize every `Transform` every frame manually — use `NetworkTransform` with interpolation enabled
- Batch multiple small state changes into one `NetworkVariable` or struct update rather than firing multiple RPCs
- Use `NetworkVariable` with `NetworkVariableWritePermission.Owner` for objects the owning client controls (e.g., player cosmetics)

### Lobby and Matchmaking Patterns

1. **Host creates a lobby** → advertises via relay/matchmaker → generates a join code
2. **Client enters join code** → connects to relay → joins lobby session
3. **All players in lobby** → host sets ready countdown → on all-ready, host loads game scene
4. **Late join** → supported only if game design allows mid-session entry; otherwise lock lobby on scene load

Unity Gaming Services (UGS) Lobby + Relay is the recommended path for NGO projects (no dedicated server needed for small player counts, NAT traversal handled automatically).

### Network Topology

| Topology | Use when | Notes |
|---|---|---|
| Dedicated server | Competitive, anti-cheat required, > 16 players | Highest cost; no host advantage |
| Listen server (host-mode) | Co-op, party games, small player count | Host leaves = session ends unless migrated |
| Relay (UGS / Steam) | P2P games that need NAT traversal | No cost for server infra; latency depends on relay region |
| Peer-to-peer (direct) | LAN or trusted environments only | No relay cost; fails behind strict NAT |

### Interest Management

For worlds with > 32 simultaneous players or large open maps:
- Enable NGO's `NetworkObject` relevancy API — objects outside a player's area of interest stop receiving updates
- Define interest zones with `NetworkObjectRelevancy` overrides per object type
- Mirror offers `NetworkProximityChecker` component for the same effect
- Never replicate every object to every client at scale — CPU and bandwidth cost grows O(n²)

## Code Examples

### NetworkBehaviour with Synchronized Health (Netcode for GameObjects)

Full component demonstrating `NetworkVariable`, `OnValueChanged`, and server-authoritative damage.

```csharp
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Server-authoritative health component for any networked actor.
/// Attach alongside a NetworkObject component.
/// </summary>
public class NetworkHealth : NetworkBehaviour
{
    [SerializeField] private float _maxHealth = 100f;

    // NetworkVariable is server-owned by default (WritePerm = Server).
    // All clients receive the new value automatically when it changes.
    private NetworkVariable<float> _current = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // Raised on every client (including server) when HP changes.
    public event UnityAction<float, float> OnHealthChanged; // (current, max)
    public event UnityAction OnDied;

    public float Current => _current.Value;
    public float Max => _maxHealth;
    public bool IsAlive => _current.Value > 0f;

    public override void OnNetworkSpawn()
    {
        // Subscribe to NetworkVariable changes on all clients.
        _current.OnValueChanged += HandleHealthChanged;

        // Server initialises the value on spawn.
        if (IsServer)
            _current.Value = _maxHealth;
    }

    public override void OnNetworkDespawn()
    {
        _current.OnValueChanged -= HandleHealthChanged;
    }

    /// <summary>
    /// Call only on the server. Clients request damage via TakeDamageServerRpc.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void TakeDamageServerRpc(float amount, ServerRpcParams rpcParams = default)
    {
        if (!IsAlive) return;

        _current.Value = Mathf.Max(0f, _current.Value - amount);

        if (!IsAlive)
            DiedClientRpc();
    }

    [ClientRpc]
    private void DiedClientRpc()
    {
        OnDied?.Invoke();
    }

    private void HandleHealthChanged(float previous, float next)
    {
        OnHealthChanged?.Invoke(next, _maxHealth);

        if (next <= 0f && previous > 0f)
            OnDied?.Invoke();
    }

    /// <summary>
    /// Server-only: restore health (e.g., from a pickup).
    /// </summary>
    public void Heal(float amount)
    {
        if (!IsServer) return;
        _current.Value = Mathf.Min(_maxHealth, _current.Value + amount);
    }
}
```

---

### ServerRpc + ClientRpc for Ability Activation

Pattern for a player-owned ability that executes on the server and broadcasts visual effects to all clients.

```csharp
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Player-owned ability component.
/// Owner client calls ActivateServerRpc → server validates → ClientRpc broadcasts VFX.
/// </summary>
public class NetworkAbility : NetworkBehaviour
{
    [SerializeField] private float _cooldown    = 2f;
    [SerializeField] private float _damage      = 30f;
    [SerializeField] private float _range       = 5f;
    [SerializeField] private LayerMask _hitMask;
    [SerializeField] private GameObject _vfxPrefab;

    private float _nextUseTime;

    // Called from the owning client's input handler.
    public void TryActivate()
    {
        if (!IsOwner) return;
        ActivateServerRpc(transform.position, transform.forward);
    }

    /// <summary>
    /// Runs on the server. Validates cooldown, applies damage, triggers ClientRpc.
    /// </summary>
    [ServerRpc]
    private void ActivateServerRpc(Vector3 origin, Vector3 direction)
    {
        if (Time.time < _nextUseTime) return;
        _nextUseTime = Time.time + _cooldown;

        // Server-side hit detection — never trust client positions for damage.
        Collider[] hits = Physics.OverlapSphere(origin, _range, _hitMask);
        foreach (Collider hit in hits)
        {
            if (hit.TryGetComponent<NetworkHealth>(out NetworkHealth health))
                health.TakeDamageServerRpc(_damage);
        }

        // Broadcast visual feedback to all clients.
        PlayVFXClientRpc(origin, direction);
    }

    /// <summary>
    /// Runs on every client. Spawns local VFX only — no game-state changes here.
    /// </summary>
    [ClientRpc]
    private void PlayVFXClientRpc(Vector3 origin, Vector3 direction)
    {
        if (_vfxPrefab == null) return;
        GameObject vfx = Instantiate(_vfxPrefab, origin, Quaternion.LookRotation(direction));
        Destroy(vfx, 3f); // local non-networked VFX, clean up manually
    }
}
```

---

### NetworkVariable with OnValueChanged Callback

Compact example for synchronized score or resource with reactive UI.

```csharp
using Unity.Netcode;
using UnityEngine;
using TMPro;

/// <summary>
/// Tracks a player's score on the server and reflects it on every client's HUD.
/// Attach to the player NetworkObject. Wire _scoreLabel in the inspector of the HUD prefab.
/// </summary>
public class NetworkScore : NetworkBehaviour
{
    [SerializeField] private TMP_Text _scoreLabel;

    private NetworkVariable<int> _score = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public int Score => _score.Value;

    public override void OnNetworkSpawn()
    {
        // Subscribe on all clients so UI stays in sync.
        _score.OnValueChanged += OnScoreChanged;
        // Initialise UI with current value (may already be non-zero on late join).
        RefreshLabel(_score.Value);
    }

    public override void OnNetworkDespawn()
    {
        _score.OnValueChanged -= OnScoreChanged;
    }

    /// <summary>Server-only. Call from game logic (e.g., kill confirmed).</summary>
    public void AddScore(int points)
    {
        if (!IsServer) return;
        _score.Value += points;
    }

    private void OnScoreChanged(int previous, int next) => RefreshLabel(next);

    private void RefreshLabel(int value)
    {
        if (_scoreLabel != null)
            _scoreLabel.text = $"Score: {value}";
    }
}
```

---

### Simple Lobby Manager with Player Ready-Up

Manages a pre-game lobby: tracks connected players, surfaces ready state, and triggers scene load when all are ready.

```csharp
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Attach to a persistent NetworkObject in the lobby scene.
/// Server manages ready state; clients send ReadyServerRpc.
/// </summary>
public class NetworkLobbyManager : NetworkBehaviour
{
    [SerializeField] private string _gameSceneName = "GameScene";
    [SerializeField] private int    _minPlayersToStart = 2;

    // Keyed by client ID. True = player is ready.
    private readonly Dictionary<ulong, bool> _readyState = new();

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback    += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback   += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback    -= OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback   -= OnClientDisconnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        _readyState[clientId] = false;
        BroadcastLobbyStateClientRpc(BuildStatePayload());
    }

    private void OnClientDisconnected(ulong clientId)
    {
        _readyState.Remove(clientId);
        BroadcastLobbyStateClientRpc(BuildStatePayload());
    }

    /// <summary>Called from the owning client's Ready button.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void SetReadyServerRpc(bool ready, ServerRpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        if (!_readyState.ContainsKey(senderId)) return;

        _readyState[senderId] = ready;
        BroadcastLobbyStateClientRpc(BuildStatePayload());

        if (AllReady())
            StartGame();
    }

    [ClientRpc]
    private void BroadcastLobbyStateClientRpc(LobbyStatePayload payload)
    {
        // UI layer listens to this — update player list display here.
        Debug.Log($"[Lobby] {payload.ReadyCount}/{payload.TotalCount} players ready.");
    }

    private bool AllReady()
    {
        if (_readyState.Count < _minPlayersToStart) return false;
        foreach (bool ready in _readyState.Values)
            if (!ready) return false;
        return true;
    }

    private void StartGame()
    {
        // Lock the lobby so no new joins arrive mid-transition.
        NetworkManager.Singleton.SceneManager.LoadScene(
            _gameSceneName, LoadSceneMode.Single);
    }

    private LobbyStatePayload BuildStatePayload()
    {
        int readyCount = 0;
        foreach (bool r in _readyState.Values)
            if (r) readyCount++;
        return new LobbyStatePayload
        {
            TotalCount = _readyState.Count,
            ReadyCount = readyCount
        };
    }

    // Lightweight struct; must be serializable by NGO.
    public struct LobbyStatePayload : INetworkSerializable
    {
        public int TotalCount;
        public int ReadyCount;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref TotalCount);
            serializer.SerializeValue(ref ReadyCount);
        }
    }
}
```

---

### Network Object Spawning with Ownership

Spawning a networked prefab on the server and assigning ownership to a specific client.

```csharp
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-side factory. Spawns a networked projectile and assigns ownership
/// to the requesting client so that client can author its movement via Owner RPCs.
/// </summary>
public class NetworkProjectileSpawner : NetworkBehaviour
{
    [SerializeField] private NetworkObject _projectilePrefab;
    [SerializeField] private Transform     _muzzlePoint;

    /// <summary>Client calls this to request a projectile from the server.</summary>
    [ServerRpc]
    public void FireServerRpc(Vector3 direction, ServerRpcParams rpcParams = default)
    {
        ulong ownerId = rpcParams.Receive.SenderClientId;

        // Instantiate locally on server — do NOT spawn a non-networked GameObject
        // and then try to attach a NetworkObject later.
        NetworkObject instance = Instantiate(
            _projectilePrefab,
            _muzzlePoint.position,
            Quaternion.LookRotation(direction)
        );

        // SpawnWithOwnership replicates the object to all clients and grants
        // the requesting client write authority over Owner-writable NetworkVariables.
        instance.SpawnWithOwnership(ownerId);

        // Initialise velocity via a method on the spawned component.
        if (instance.TryGetComponent<NetworkProjectile>(out var proj))
            proj.Launch(direction);
    }
}

/// <summary>
/// Moves a spawned projectile on the server.
/// Uses NetworkTransform for automatic client-side replication.
/// </summary>
[RequireComponent(typeof(NetworkTransform))]
public class NetworkProjectile : NetworkBehaviour
{
    [SerializeField] private float _speed    = 20f;
    [SerializeField] private float _lifetime = 5f;

    private Vector3 _velocity;
    private float   _spawnTime;

    public void Launch(Vector3 direction)
    {
        // Called on server immediately after spawn.
        _velocity  = direction.normalized * _speed;
        _spawnTime = Time.time;
    }

    private void Update()
    {
        if (!IsServer) return;

        transform.position += _velocity * Time.deltaTime;

        if (Time.time - _spawnTime > _lifetime)
            GetComponent<NetworkObject>().Despawn();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        if (other.TryGetComponent<NetworkHealth>(out NetworkHealth health))
            health.TakeDamageServerRpc(25f);

        GetComponent<NetworkObject>().Despawn();
    }
}
```

## Anti-Examples

### Client-Authoritative Movement in a Competitive Game

```csharp
// BAD — client reports its own position directly.
// Any client can set transform.position to any value, teleporting through walls,
// moving at any speed, or occupying other players' positions.
[ServerRpc]
private void UpdatePositionServerRpc(Vector3 clientPosition)
{
    transform.position = clientPosition; // trusts the client completely
}

// GOOD — client sends input direction; server applies movement and validates.
[ServerRpc]
private void SendInputServerRpc(Vector2 inputDir)
{
    Vector3 move = new Vector3(inputDir.x, 0f, inputDir.y) * _speed * Time.deltaTime;
    // Server moves the character; NetworkTransform replicates the result.
    _characterController.Move(move);
}
```

---

### Synchronizing Transform Every Frame Manually

```csharp
// BAD — manually writing position to a NetworkVariable every Update frame.
// Generates one dirty-bit check and potential bandwidth cost at tick rate for every object,
// even when the object has not moved.
private NetworkVariable<Vector3> _position = new NetworkVariable<Vector3>();

private void Update()
{
    if (IsServer)
        _position.Value = transform.position; // unnecessary if object is stationary
}

// GOOD — add a NetworkTransform component and enable interpolation.
// It handles delta compression, interpolation, and extrapolation automatically.
// Only fires updates when the transform actually changes beyond the threshold.
```

---

### RPCs for Frequent State Updates

```csharp
// BAD — firing a ClientRpc every frame to broadcast health.
// Each RPC invocation has message overhead; at 60 fps this floods the channel.
private void Update()
{
    if (IsServer)
        UpdateHealthClientRpc(_health);
}

[ClientRpc]
private void UpdateHealthClientRpc(float health) { /* ... */ }

// GOOD — use a NetworkVariable. The framework only sends when the value changes,
// applies delta compression, and batches updates at the network tick rate.
private NetworkVariable<float> _health = new NetworkVariable<float>();
```

---

### Trusting Client Input Without Server Validation

```csharp
// BAD — server accepts whatever damage value the client claims.
// A client can send arbitrarily large damage values to one-shot any target.
[ServerRpc(RequireOwnership = false)]
private void DealDamageServerRpc(float damage)
{
    _health.Value -= damage; // client-supplied, unvalidated
}

// GOOD — server computes damage from authoritative weapon stats.
// The client only identifies the weapon; the server looks up its actual values.
[ServerRpc(RequireOwnership = false)]
private void RequestDamageServerRpc(int weaponId, ServerRpcParams rpcParams = default)
{
    float damage = WeaponDatabase.GetDamage(weaponId); // server-side lookup
    _health.Value = Mathf.Max(0f, _health.Value - damage);
}
```

---

### Spawning a GameObject and Then Adding a NetworkObject Later

```csharp
// BAD — cannot attach a NetworkObject to an already-spawned non-networked GameObject.
// This will throw an InvalidOperationException at runtime.
GameObject go = new GameObject("Projectile");
go.AddComponent<NetworkObject>().Spawn(); // fails

// GOOD — always spawn from a prefab that already has a NetworkObject component.
// Register the prefab in NetworkManager's Network Prefabs list first.
NetworkObject instance = Instantiate(_projectilePrefab, spawnPos, spawnRot);
instance.Spawn();
```

## Cross-References

- Related skills: `hades:unity-architect`, `hades:component-design`, `hades:unity-performance`
- Review skill: `hades:unity-reviewer`
- Hades MCP tools: `find_components_using_pattern`, `search_by_name`, `recall_memory`, `propose_memory_update`
- Unity docs: [Netcode for GameObjects](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.0/manual/index.html), [NetworkVariable](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.0/api/Unity.Netcode.NetworkVariable-1.html), [Unity Gaming Services Lobby](https://docs.unity.com/lobby/en-us/manual/unity-lobby-service)
