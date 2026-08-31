# Multiplayer Roadmap

Target game: Going Medieval 1.1.19.

## Capacity target

Going Medieval does not expose a native multiplayer slot limit to the mod.
Going Cooperative owns the networking/session layer, so capacity is limited by
our transport, save-transfer, replication fan-out and performance.

- Current runtime: 2 players (host + 1 client).
- Supported target: 4 players (host + 3 clients).
- Experimental ceiling: 8 players (host + 7 clients), enabled only after the
  four-player path passes load, reconnect, save/load and desync tests.

The UI must never advertise a player count that the active transport cannot
actually service.

## Multi-peer implementation stages

### MP4-1 Peer identity and roster

- Replace the single conceptual CLIENT with unique peer IDs.
- Host owns a roster with capacity, connection state and compatibility state.
- Every intent, presence message, ACK and resync request is attributable to one
  peer.
- Duplicate/replayed peer identity is rejected.

### MP4-2 Direct transport fan-out

Current Direct UDP uses one remote endpoint. Replace host-side point-to-point
state with a per-peer connection table:

- endpoint;
- authentication nonces/session ID;
- receive replay window;
- send sequence;
- binary framing state;
- compatibility state;
- queue/backpressure counters.

Host uses one UDP socket and routes datagrams to peer sessions by authenticated
session identity. Client remains single-host.

High-frequency host state is encoded once where possible and fan-out is done
without rebuilding Unity state per client.

### MP4-3 Save transfer / join pipeline

Each joining client gets its own transfer state:

- checkpoint ID;
- transfer progress;
- verification hash;
- load readiness;
- resync state.

One slow/new client must not pause already-playing clients except during an
explicit authoritative checkpoint boundary where required.

### MP4-4 Replication fan-out

Classify outbound messages:

- broadcast authoritative state;
- broadcast presentation state;
- origin-excluding relay (remote cursor/selection/ping);
- peer-specific ACK/error/resync/save transfer.

Do not duplicate host world collection per peer.

### MP4-5 Presence/UI

Menu/session status displays up to four players initially:

- name/peer ID;
- Host / Client;
- Connecting / Loading / Ready / Playing / Reconnecting;
- ping/transport health;
- compatibility status.

Remote cursor/selection/pings carry sender peer ID and use stable per-peer
presentation styling.

### MP4-6 Four-player release gate

A four-player session is supported only after:

1. four peers can join the same save;
2. all four can issue simultaneous orders;
3. disconnect/reconnect of any client works;
4. late join works;
5. full resync can target one client;
6. host save/load restores the session;
7. 30+ minute colony test shows no accumulating queue/backlog;
8. no peer can mutate another peer's local-only UI state;
9. command ordering remains host authoritative.

### MP8 Experimental gate

Raise the menu ceiling from 4 to 8 only after the same test matrix passes with
eight processes and host frame-time/network fan-out remains acceptable.

## Performance priorities

1. Keep UDP receive/auth/decode/reassembly off Unity main thread.
2. Keep envelope encode/chunk/HMAC/send off Unity main thread.
3. Coalesce high-frequency absolute state in both directions.
4. Do not scan or serialize unchanged agents.
5. Skip per-render presentation work for idle agents.
6. Encode authoritative host snapshots once, then fan-out.
7. Introduce subsystem dirty sets instead of repeated broad scene scans.
8. Keep durable gameplay commands FIFO and bounded; never silently coalesce
   gameplay events.

## Gameplay coverage priorities

P1:
- management/production/storage unsupported paths observed during real use;
- building topology edge cases;
- inventory/equipment UI parity.

P2:
- projectile authority/presentation;
- hostile/external-agent lifecycle and raids;
- complete combat lifecycle;
- general event scheduler authority;
- warning/notice/environment event mutations;
- player-triggered events.

P3:
- subsystem desync fingerprints;
- targeted resync for inventory/building/settler/job/production;
- late join without full-session interruption;
- Steam multi-peer UX after Direct is stable.
