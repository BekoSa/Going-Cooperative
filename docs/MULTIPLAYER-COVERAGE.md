# Multiplayer Coverage — Going Cooperative 0.4.0

Target: Going Medieval 1.1.19, two-player host-authoritative multiplayer.

This document is a development coverage map, not a claim that every Going
Medieval interaction is multiplayer-safe. A feature is only **Supported** when
the current code has an explicit capture/authority/apply path. **Partial**
means a useful path exists but one or more native variants or lifecycle cases
are intentionally disabled or can fail closed.

## Architecture contract

- The host owns simulation and the authoritative save.
- Client gameplay UI must become a validated network intent.
- Client presentation-only state (cursor, selection markers) never mutates host
  gameplay state.
- High-frequency absolute state is newest-wins and must not build a queue.
- Durable gameplay mutations use FIFO delivery plus acknowledgement/retry where
  required.
- Unknown command actions fail closed and are logged.
- The handshake includes an action-registry fingerprint so peers with different
  client-intent contracts cannot silently connect.

## Player actions

| Area | Status | Current path / limitation |
| --- | --- | --- |
| Pause / game speed | Supported | Client speed intent, host-authoritative speed state. |
| Digging | Supported | Single/range dig through GroundManager capture/apply. |
| Chop / harvest vegetation | Supported | Selection/resource order capture and plant authority. |
| Fishing orders | Supported | Region/resource order lane. |
| Allow / forbid | Supported | Region and contextual object orders. |
| Urgent haul / cancel / deconstruct | Supported | Selection/contextual region order paths for recognized targets. |
| Standard building placement | Supported | Transactional build batches with host result/repair flow. |
| Roof placement | Partial | Exact committed topology is supported, but malformed/unreadable topology fails closed; bounded to 512 cells. |
| Beam placement | Partial | Semantic beam topology lane exists and is enabled; capture failure is blocked rather than guessed. |
| Wall/socket placement | Partial | Semantic socket lane exists and is enabled; capture failure is blocked rather than guessed. |
| Stockpile create / resize | Supported | Stockpile region order and policy lanes. |
| Cropfield create / resize | Supported | Spatial cropfield lane enabled. |
| Cropfield policy / deconstruct | Supported | Policy and contextual action lanes. |
| Equipment equip order | Supported | Inventory AddEquipOrder capture/apply. |
| Full equipment/inventory UI parity | Partial | Equip order is explicit; every possible inventory UI operation has not been proven as a player-intent path. |
| Research activation | Supported | Explicit ResearchActivate custom intent. |
| Production queue | Supported | V1/V2 production queue actions are explicit. |
| Every production UI operation | Partial | Unsupported production-operation fallbacks still exist. |
| Management policies | Partial | Many worker/animal/rally/self-tend/training methods are captured; unsupported policy/UI fallbacks remain. |
| Worker manage preset | Supported | Explicit WorkerManagePreset intent. |
| Storage policy | Partial | Storage V4 and shelf manifest enabled; unsupported target/change-kind fallbacks remain. |
| Prioritised object work | Supported | Explicit PrioritisedObjectWorkV1 request/result. |
| Draft / undraft | Supported | Custom DraftState intent. |
| Drafted movement | Supported | Custom DraftMove intent. |
| Combat attack / cancel | Supported | Explicit attack/cancel intents with host authority. |
| Projectile presentation/replication | Not enabled | combatProjectileReplication=false. |
| External hostile-agent lifecycle | Not enabled | combatExternalAgentLifecycle=false. |
| Medical treatment orders | Supported | Medical V1 treatment request/state path. |
| Client wound-tick suppression | Not enabled | medicalClientWoundTickSuppressionV1=false. |
| Trader open / basket / commit | Supported | Synchronized trader interaction actions. |
| Event dialog choice | Supported | Explicit GameEventOptionChosen intent. |
| Trader event authority | Supported/experimental | Enabled. |
| Recruitment event authority | Supported/experimental | Enabled for the exact NewWorkerEvent pilot. |
| Runaway event authority | Supported/experimental | Enabled for the exact runaway pilot. |
| General event scheduler authority | Not enabled | eventSchedulerAuthority=false. |
| Event warning / notice replication | Not enabled | Both gates are false. |
| Event external-agent lifecycle | Not enabled | eventExternalAgentLifecycle=false. |
| Event environment mutations | Not enabled | eventEnvironmentMutationReplication=false. |
| Player-triggered events | Not enabled | playerTriggeredEventReplication=false. |
| Weather state | Supported/experimental | Weather replication and temperature replication enabled. |
| Weather scheduler authority | Not enabled | weatherSchedulerAuthority=false. |
| Remote cursor | Presentation | World-space presence, newest-wins. |
| Remote selected settlers | Presentation | Stable-ID markers only; does not replace the other player's local selection. |
| Ping marker | Presentation event | F9 test hotkey; event is not coalesced. |

## Protocol action registry

Every public string constant whose name ends in `Action` in
`LockstepCommandPayloads` is classified by `MultiplayerActionRegistry` as
either:

1. a player-originated payload action, or
2. a host-only state/presentation payload action.

`tests/CorePolicyTests.cs` reflects over those constants and fails if a new
action is added without classification.

The client-action fingerprint is included in the replication build capability
string. A mismatch is rejected during Hello with
`client-action-capability-mismatch`.

This prevents action-schema drift between the two computers, but it does not by
itself prove that every Going Medieval UI interaction has a capture hook. New
game UI surfaces still need explicit audit and an integration test.

## Known fail-closed paths to close

The source currently contains deliberate unsupported/fail-closed branches for:

- unknown region-order variants;
- unknown custom command payload actions;
- unsupported management policies;
- unsupported production operation variants;
- unsupported combat target/delta/state variants;
- unsupported event state/delta variants;
- unsupported storage target/change kinds;
- building semantic/topology capture that cannot be represented safely.

These paths should remain fail-closed until an exact Going Medieval 1.1.19
native API and deterministic host-authoritative representation are verified.

## Performance architecture in 0.4.0

Direct/IP hot-path changes:

- UDP receive, authentication, UTF-8 envelope decoding and chunk reassembly run
  outside Unity's main thread.
- Envelope encoding, chunking, HMAC and UDP send run outside Unity's main
  thread.
- Transform snapshots, cursor and selection use newest-wins slots in both
  directions rather than unbounded FIFO queues.
- Gameplay intents, acknowledgements, ping events and durable state remain FIFO.
- The HMAC implementation hashes buffers incrementally instead of allocating a
  MemoryStream + ToArray copy for every packet.
- Moving agents use frequent sparse transform rows; unchanged agents use a
  low-rate heartbeat.
- Client presentation tracks stop doing interpolation/animator writes every
  render frame once an agent is idle.

The target is negligible multiplayer frame-time overhead, not literally zero:
the host still has to inspect changed game state and the client still has to
present actively moving entities.

## Next coverage priorities

1. Prove/build-test the async transport and idle presentation changes on both
   peers.
2. Close any management/production/storage actions seen as unsupported during a
   normal colony session.
3. Enable and validate projectile combat replication.
4. Complete hostile/external-agent lifecycle for raids and combat.
5. Expand event authority beyond the current trader/recruitment/runaway pilots.
6. Add subsystem-level desync fingerprints and targeted resync before widening
   more experimental event lanes.
