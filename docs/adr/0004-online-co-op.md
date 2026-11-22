# ADR-0004. Online co-op: the host simulates, clients send commands and show the host's world

- **Status:** proposed
- **Date:** 2026-11-22
- **Tasks:** OM-58, OM-59, OM-30
- **Supersedes** in [ADR-0002](0002-network.md): the client deciding its own wizard's movement and hits (Model, points 2 and 4),
  and the UDP transport as the first one. The rest of ADR-0002 stands: our own protocol, host authority, star topology,
  explicit `NetId`s, two channels, join in progress, version and content hash.

## Context

The goal is online co-op for up to 4 players who all run this OpenMagicka, typically one host on macOS and friends on
Windows over Tailscale (low loss, 10–100 ms round trips). Compatibility with the original's protocol is out of scope.

What the code has today (all in one process, one player):

- **Threads.** The render thread polls input into a `LatestValue<InputState>` and draws the newest `PlayFrame` from a
  `FrameExchange`. The logic thread (`LogicLoop`) runs `ISimulation.Tick(dt)` at a fixed 60 Hz, up to 5 steps per frame,
  dropping the backlog after a stall, then writes one frame. `Tick` is not given the tick number.
- **Players.** `GameSession` has up to 4 `Player`s, but `LevelPlay` makes exactly one, with one `KeyboardMouseControl`
  and one `GamePadControl` (only `PlayerIndex.One` is polled). `PlayedLevel.PlacePlayers`, the camera and the scripts
  already handle several players. A player's avatar is *replaced* on checkpoint respawn and on Revive.
- **Input.** The controls read FNA keyboard, mouse and pad states and call the avatar directly (`Steer`, `Drive`,
  `PressCast`, `Conjure`, `Attack`, `ControlTargets.Use`...). They keep state of their own outside the simulation (element
  cooldowns, gesture combos, the held interaction target as an object reference). The mouse is turned into a world
  point through the local camera and screen size.
- **World.** `EntityManager` gives every entity an `Id` and has an unused `NetId` path (`Add(entity, NetId)`,
  `AddLocal`, `RemovedEntity` for "the removal broadcast"). Entities are mutable classes; a character's state includes
  its FSM state, its `CharacterAnimator` (whose actions open damage windows and grips: animation is simulation, not
  decoration), statuses, spell queue and spell. Physics is Jitter2 in floats, not deterministic
  ([ADR-0003](0003-physics.md)).
- **Scripts.** Triggers, AI, spawns, boss and magick actions are already gated on `IsHost`; `ScriptScene.TriggerFired`
  exists for a broadcast nobody listens to. Checkpoints store script state and an *action log* that is replayed on
  restart, not a world snapshot.
- **Presentation.** The simulation raises presentation through events: about 140 `EntityManager.Cue` sites
  (`SoundCue`, `EffectCue`, `GibsCue`, `BeamCue`...) and script `ScriptRequest`s (sounds, music, dialogs, HUD), consumed
  on the logic thread by `EffectDirector`, `SoundDirector` and `SpellVisuals`. Many cues hold live object references.
- **Randomness.** Per-scene `Random(seed)` for scripts, per-character `Random` for AI and magicks, but the player wizard,
  revived wizards and unseeded spawns fall back to `Random.Shared`.
- **Network.** `INetTransport` with a loopback and a direct-IP TCP transport (star, up to 3 clients, unreliable
  messages coalesce to the newest per peer). No messages, codec, handshake or session.

## Decision

### 1. One simulation, on the host

The host runs the whole game, **including every wizard**. A client sends its players' commands and shows what the host
sends back; it runs no gameplay: no AI, scripts, damage, statuses or gameplay physics.

- There is nothing to reconcile and no hit to trust: one simulation means no divergence, no `DamageRequest`, and a
  client cannot cheat. This matters more than in the original, because our physics and AI are not the original's and
  two simulations of them would drift apart quickly.
- The cost is latency on one's own wizard: a press shows after the round trip plus the interpolation delay
  (§4), about 50–150 ms over Tailscale. Magicka is a slow game of queued elements and channelled spells, and the element
  queue itself can show at once (it is local input). If moving feels late in playtests, phase 6 adds **prediction of
  one's own wizard's movement only**: the client steers a local copy of its avatar with the same `CharacterBody` and
  eases it to the host's position. Spells, damage and everyone else stay host-only.
- Rejected: the original's split (each client moves its own avatar and reports its hits). It needs two simulations of
  every wizard that agree, hit validation, and corrections when they do not.
- Rejected: lockstep. Floats, Jitter2 and a clock that drops its backlog rule it out.
- Rejected: streaming render frames (`PlayFrame`). Skinned models' bone palettes make that far too large (hundreds of
  kilobytes per frame), and frames cannot be interpolated.

### 2. Commands instead of controls calling the avatar

A **`PlayerCommand`** is one player's intent for one tick, plain data:

| Field | Encoding |
|---|---|
| Tick it is for | `uint32` |
| Move direction (world X/Z, length = speed) | 2 × `int16` |
| Aim point (world), and the entity under the cursor | 3 × `float16`, `NetId` |
| Held buttons: attack, cast (force / area / self / magick), block, boost, walk | bit mask |
| Elements pressed since the last command, in order | count + up to 8 × `byte` |
| Edge actions: interact (with target `NetId`), select magick, skip dialog | small optional list |

- The controls (`KeyboardMouseControl`, `GamePadControl`) **produce** commands; they no longer touch the avatar. The
  cursor pick through the local camera stays client-side and is sent as a world point and a `NetId`.
- An **`AvatarDriver`** per player, on the host, applies commands to that player's current avatar. The state the controls
  keep today (element cooldowns, combos, the held target, gesture locks) moves into it, so it is simulation state and
  follows the avatar across respawns.
- Local players go through the same path. **Local co-op (several pads, or keyboard plus pads, on one machine) falls out
  of this** and is the first phase: it exercises several players in one simulation before any networking.
- Presses between ticks are kept: commands carry edges (elements pressed, interact) as lists, not just the latest state.

### 3. Messages and channels

All messages are little-endian, one byte of type then the body, and every message from the host carries the tick it
belongs to. Commands go host-ward; everything else comes from the host.

| Message | Channel | Contents |
|---|---|---|
| `Hello` / `Welcome` / `Reject` | reliable | handshake (§6) |
| `Ready` | reliable | the client has loaded the level and scene it was told to |
| `Commands` | reliable | the client's commands for its local players, one per tick |
| `ChangeScene` | reliable | level, scene, spawn point; clients load, then send `Ready` |
| `Spawn` / `Despawn` | reliable | `NetId`, kind, template, initial state / `NetId` |
| `Events` | reliable | presentation events (§5) of one tick |
| `Baseline` | reliable | the whole world, for a client that is ready (§7) |
| `Snapshot` | unreliable | the state of every live entity and of the level, newest wins |
| `Players` | reliable | slots, names, colours, each player's avatar `NetId`, unlocked magicks |
| `Ping` / `Pong` | reliable | once a second: round trip, and a dead link after 10 s |

**Spawns and despawns are reliable and ordered, snapshots are not.** A snapshot names entities only by `NetId`; one for
an entity the client has not been told about is ignored.

### 4. Snapshots, interpolation and the client's world

- **The host assigns a `NetId` to every entity it adds** (`EntityManager.Add` gives one; the client adds with the
  host's, `Add(entity, NetId)`). Things that exist only on one machine (gibs, purely visual effects) use `AddLocal`,
  which today nothing calls: `EffectDirector.Shatter` must switch to it.
- **Snapshots go out at 30 Hz** (every second tick), full state, quantized after the original's `EntityFeatures`
  ([Entities](../engine/ENTITIES_AI.md) §12.3): position `float32`×3, yaw `uint16`, velocity `half`×3, HP, statuses as
  a mask with magnitudes, FSM state, animation clip and time, the spell being cast, self shield. Props add orientation;
  missiles their position and velocity. Level state goes in the same snapshot: animated level part poses, liquids'
  freeze values, the script stage (camera, fade, input locks, cutscene), the boss health bar.
  - Full state is a few kilobytes at 30 Hz per client, well within a Tailscale link. Since TCP coalesces unreliable
    messages, a delta encoding would have to be against a snapshot the client has acknowledged; that is left for when
    measurements ask for it.
- **The client keeps a world of puppets.** It loads the same level content as the host (the content hash guarantees it
  is the same) and creates each spawned entity from its template, but never runs gameplay on them: each tick it sets
  their state from the snapshots, **interpolated about 100 ms behind the newest one**, the delay adapting to jitter.
  Animations advance locally from the replicated clip and time and are corrected by each snapshot.
- **The presentation code then runs unchanged on the client**: `PlaySimulation.WriteFrame`, the camera
  (`StepCamera` frames every playing avatar), `EffectDirector`, `SoundDirector`, `SpellVisuals` and the HUD read the
  puppets exactly as they read the host's entities. That is why replication is at the entity level and not of render
  frames.
- Each client draws its own HUD for its own players, and the same shared camera as local co-op (all players framed).

### 5. Presentation events

Cues and script requests are raised inside the host's simulation and do not follow from state. The host collects those
of each tick and sends them as one `Events` message; the client plays them when its interpolated time reaches that tick.

- Each event is encoded with `NetId`s in place of object references (an `Item`, a `Beam`, the `AnimationAction` that
  raised it). An event whose entity the client does not have is dropped.
- Not every cue needs sending: what the client can derive from state (a character's animation, a spell's spray and
  beam, which `SpellVisuals` draws from the caster's spell) is not sent. The rest (impacts, gibs, blasts, sounds,
  music, dialogs, hints, camera shakes) is.

### 6. Handshake: same build, same game data

`Hello` carries:

- the protocol version (a constant, raised with any message change);
- the build: assembly informational version and git commit;
- the content fingerprint: SHA-256 over a sorted manifest of the content files the game reads (path, size, own hash),
  computed once and cached by size and modification time, so a modded or different-language install is refused;
- the player's name and colour, and how many local players the client brings.

The host answers `Welcome` (the client's player slots, the level and scene now playing) or `Reject` with a reason the
client shows: another version, other game data, the session is full. Nothing else is accepted before `Welcome`.

### 7. Joining, leaving, scene changes and checkpoints

- **Joining (at the start or mid-game) is the same path:** `Hello` → `Welcome` → the client loads the level and scene →
  `Ready` → the host sends `Baseline` (every entity's spawn and state, the level state, `Players`), adds the player and
  spawns its wizard as Revive does, next to a living player. Scripts never run on the client, so unlike a checkpoint it
  needs no action log or replay.
- **A client leaving** (closed link, or no message for 10 s): the host kills its avatar and frees its slot, as the
  original's `Leave` does. The same name reconnecting gets its slot back.
- **The host leaving ends the session.** Clients return to the menu with a message. There is no host migration.
- **Scene changes** are a sync point, as in the original: the host sends `ChangeScene`, loads, and waits for every
  client's `Ready` (at most 60 s, after which a late client gets a `Baseline` when it is ready) before the new scene's
  first tick, so that its opening cutscene plays for everyone.
- **Checkpoints stay on the host.** Taking one changes nothing for clients. Restarting at one rebuilds the host's world,
  after which the host sends every client a new `Baseline`.

### 8. Determinism, randomness and scripts

- **Determinism is not required**: the host's state is the truth and the client shows it.
- **Randomness is the host's.** The client draws no gameplay random numbers, since it runs no gameplay. Visual randomness
  (particles, effect placement) stays local and need not match. The `Random.Shared` fallbacks for wizards and unseeded
  spawns are replaced by randoms seeded from the session, so that a checkpoint replay and the tests are reproducible.
- **Scripts run only on the host** (`IsHost`, as today). What they change reaches clients as state (entities, level
  parts, stage) or as events (sounds, dialogs, HUD).

### 9. Threads

- **The session runs on the logic thread**, inside a network `ISimulation` that wraps `ShellSimulation`. Each tick, the
  host drains the transport's inbox, queues commands for their ticks (a buffer of one or two ticks absorbs jitter; a
  missing command repeats the last held buttons without edges), ticks, then every second tick writes a snapshot and
  sends. The client drains, applies spawns, events and snapshots to its puppets, then writes the frame.
- `LogicLoop` passes the tick number to `ISimulation.Tick`.
- The transport's socket loops already run on the thread pool and hand over through concurrent queues, so the render
  thread is untouched. Encoding a snapshot is plain copying on the logic thread, measured in phase 3.
- Single player does not go through the network code. Running it over loopback, as ADR-0002 wanted, is possible later
  and not needed now: the in-process host and client of the tests exercise the same code.

### 10. Transport

Keep the **TCP transport** for now. It already works over Tailscale and LAN, needs no NAT work (Tailscale gives every
machine an address), and its coalescing of unreliable messages means snapshots never queue behind a slow link.
`INetTransport` gains connect and disconnect events and the round trip. Hosting and joining are menu items (an address
and port to type, the port 27016 by default), plus `--host [port]` and `--join <address>` options for testing. A UDP
transport stays possible behind the same interface if loss on real links ever stalls TCP.

### 11. Bosses

Bosses are being made now, so they must follow the same rules:

1. A boss's state lives in its entities' fields: no closures or iterators holding its progress.
2. Its random numbers come from its entity's seeded `Random`.
3. A boss of several parts (Jörmungandr's segments, several heads) is either several entities, or one entity that
   writes and reads a small state block of its parts for the snapshot.
4. It raises presentation through `EntityManager.Cue` with data, not by calling the presentation code.
5. It decides only where `IsHost` (AI, attacks, phase changes).

## Phases

Windows CI stays green at every step. Each phase ends with tests, and the ones marked *playtest* with a game played.

| Phase | Builds | Tests |
|---|---|---|
| 0. Groundwork | this ADR; the TCP transport test made patient | the transport tests |
| 1. Commands and local co-op | `PlayerCommand`, the controls producing commands, `AvatarDriver`; all four pads polled; a pad joins the game by its start button | the control tests moved onto commands; two players' commands move and cast with their own wizards only; *playtest* with a pad |
| 2. Protocol and session | message codec; handshake with version, build and content fingerprint; host and client session states over `INetTransport`; the tick number in `Tick`; `--host` / `--join` | codec round trips; handshake accepted, and refused for another version, other content and a full session, over loopback and TCP |
| 3. Replication | `NetId`s for every entity; spawns, despawns and snapshots of characters, props, items and missiles; the client's puppet world and interpolation | a host world and its client's puppets agree within the quantization after spawns, moves and removals (in-process); with the game files, a level's first scene replicates |
| 4. Remote players | client commands applied on the host; events; dialogs, cutscenes, camera and HUD on the client | an in-process client's command moves its wizard on the host; a cue on the host plays on the client; *playtest* on two machines (Mac host, Windows client over Tailscale) |
| 5. Joining, leaving and scenes | `Baseline`; joining mid-game; disconnects and rejoining; the scene change sync point; a new baseline after a checkpoint restart | a client joining mid-level matches the host; a disconnect removes the avatar and frees the slot; a scene change brings every client along; *playtest* |
| 6. Coverage and feel | every entity kind and magick; bosses as they land; own-movement prediction if playtests ask for it; bandwidth measured, deltas if needed | per entity kind replication tests; *playtest* through a full chapter |

## Risks

| Risk | Mitigation |
|---|---|
| Presentation code reads more of an entity than the snapshot carries, so a puppet looks wrong (a missing aura, a spell drawn idle) | puppets are real entities from the same templates; replication tests compare what `WriteFrame` writes on host and client, not just fields |
| ~30 entity kinds and ~140 cue sites to cover | entity kinds and cues are added one by one behind a test; an unreplicated kind is logged, not silently wrong |
| Own wizard feels late | measure on the real link in phase 4; movement prediction in phase 6 |
| Cues with object references, raised mid-physics | encoded after the tick, from the collected list, with `NetId`s |
| Players' avatars change entity on respawn and revive | `Players` names each player's current avatar; the `AvatarDriver` follows the player, not the entity |
| Scene loads take different times on each machine | the sync point, with a timeout and a baseline for late clients |
| TCP stalls behind a lost packet | Tailscale links lose little; snapshots coalesce; UDP is possible behind the interface |
| Bosses built without these rules | §11, given to the boss work now |

## Consequences

- ADR-0002's client-decided movement and hit requests are dropped; its Steam and relay plans are unaffected and not
  needed for Tailscale play.
- The controls stop driving characters directly. `AvatarDriver` becomes the only way into a wizard, for local and remote
  players alike.
- Every entity kind gains a snapshot encoding, kept beside it; new kinds (bosses) come with theirs.
- `EffectDirector` stops adding authoritative entities.
