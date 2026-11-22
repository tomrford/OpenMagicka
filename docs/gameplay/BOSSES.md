# Named bosses (reimplementation spec)

Source of behavior: `Magicka.exe` 1.10.4.2 (see [`ENGINE_ANALYSIS.md`](../ENGINE_ANALYSIS.md)).
Scope: the named bosses a script's `spawnBoss boss="…"` brings in (`Magicka.GameLogic.Entities.Bosses`), what they
share, and each one OpenMagicka has made. The fight itself (its bar, titles, `activateBoss`, `bossDead`) is in
[`LEVELS_TRIGGERS.md`](../formats/LEVELS_TRIGGERS.md) §9.6; the list of bosses and their states is in
[`ENTITIES_AI.md`](../engine/ENTITIES_AI.md) §10.

> **Clean-room.** Below are only rules, constants and identifiers in our own words. No original
> code is carried over. Code: `OpenMagicka.Gameplay.Bosses`, `OpenMagicka.Engine.Scripting.NamedBosses` (OM-42).

---

## 1. What every named boss is built from

### 1.1 In the fight

- A named boss is looked up by its script name, in any case. The ones made so far are listed in `NamedBosses`.
  A name not made yet gets the debugging stand-in when `OPENMAGICKA_SKIP_BOSSES=1` (it is dead once its fight
  starts), and otherwise nothing.
- **Each spawn makes it anew.** The original keeps one instance and sets it up again on every `spawnBoss`, and a
  checkpoint's restart replays that spawn. So a retry always meets a whole boss, asleep at its locator. The boss
  before it leaves the world at the end of the frame. A level with no room for the new one keeps the old one; with
  none yet, the boss waits for room.
- **It runs from its spawn.** The boss updates every frame on the world's time (a time warp slows it), whether or
  not its fight runs yet. After the scripts each frame, the fight tells it whether it runs. That is how a
  `delayed` boss wakes once `activateBoss` runs.
- **Dead** means what the boss says, usually once its death clip has played out, not at 0 hit points. `bossDead`
  holds once every boss of the fight is dead. The bar follows the summed hit points.
- **Only the host** makes a named boss; a client has none yet. Replicating the bosses is still to come. The
  original sends each boss's pose and hit points 30 times a second, its state changes, its target and its
  missiles.

### 1.2 The boss and its parts

- The boss is **one entity** that owns its parts. The parts are entities of their own: they come into the world
  with it and leave with it.
- A **damage zone** is a sphere that takes the boss's damage (Jörmungandr's head). Spells and blows find it as they
  find a character, and it hands each hit to its boss with its part number. While the boss is hidden, the zone
  waits at (0, −100, 0) and takes nothing. It pushes nothing.
- A **body** is a chain of immovable capsules laid from the boss's bones each frame (Jörmungandr's spine). It
  blocks what walks into it, takes no damage and is no target: shots and missiles pass it by. It is parked
  under the level while the boss is hidden.
- A **hit memory** spares each thing a part struck for a quarter of a second.
- The boss **flashes** white for 0.1 s after a blow lands (the shader's override colour, its alpha fading from 1
  to 0). Its wound map shows the share of its hit points gone. While frozen it is drawn icy.
- Its **damage numbers** float over it as a character's do: one blow's number follows it for a third of a second,
  and burning, poison and healing show once a second.

### 1.3 Hit points, resistances and statuses

The bosses have their own damage rules, not the characters'. One entry of damage:

1. Nothing happens to a boss that is dead.
2. **Resistances:** each element of the entry shifts the amount by its modifier (whole points; a pure Earth entry
   is kept at 0 or more). The magnitude is scaled by the **mean** multiplier of the entry's elements. With no
   magnitude left, the entry is deflected.
3. **Statuses:** a status attack sets the statuses of its elements (fire → burning, cold, water → wet,
   poison, life → healing, steam → wet), each only where the boss's multiplier for that element is above 0.
   Statuses meet as follows:
   - Fire puts out cold, wet and frozen, and takes no hold itself while any of them is on or the boss is drying.
   - Cold puts out fire (drying starts, 0.9 s) and is scaled by the cold multiplier. On the wet it freezes them
     (Frozen of its magnitude), and on the frozen it feeds the freeze instead.
   - Wet puts out fire (or is kept off while drying) and washes grease off.
   - Grease dries the wet.
   - Poison is scaled by the poison multiplier.
   - Healing cures poison.
   - What put a status out instead of taking is reported as removed.
4. **Damage:** lightning hurts the wet twice. Life thins poison by its magnitude. A physical blow (earth, ice) on
   the frozen loses 200, then counts thrice, at a magnitude of at least 1. Otherwise a physical blow draws blood
   (`gore_splash_regular` at the point struck, when gore is on). The hit points drop by amount × magnitude and
   never rise above the most.
5. The rules look at the statuses as the **last frame** left them, and Wet counts only from a magnitude of 1.
   Statuses burn their damage straight off the hit points (fire twice on the greased), and a dead boss keeps none.

---

## 2. Jörmungandr

The serpent that ends chapter 2 (MountainDale, scene `md_s6BF`). It digs under the ground, then either bursts up
under a wizard to bite and spit, or leaps over one in an arc.

### 2.1 Constants

| | |
| --- | --- |
| Hit points | 10 000, one pool, no regeneration |
| Warning `W` | 0.75 + 1.75 · hp/10 000 s: 2.5 s whole, 0.75 s near death. It drives every telegraph and wait. |
| Stagger | one blow taking ≥ 250 |
| Ram | a shield or solid barrier rammed by the head costs it 750 and staggers it |
| Rise chance | 0.35 + 0.4 · hp/10 000 per surfacing (else it leaps) |
| Bites per surfacing | 2 or 3, even odds (spits do not count) |
| Resistances | Earth and Ice × 0.75; the rest neutral |

The model is `Models/Bosses/Jormungandr/Jormungandr`. Its clips and their lengths in seconds: `intro` 10,
`dive_attack` 2.667, `dive_recoil` 2.667, `emerge` 1.333, `submerge` 1.333, `idle` 1.333 (looped), `venom` 2.667,
`attack` 2.5, `hit` 1.333, `die` 5, `die_above` 3.542.

### 2.2 Parts

- **Head** (part 1): a sphere of radius 1.1 at bone `HeadBase`, 0.6 m along the root's facing. It is the only part
  that takes damage. It deals the contact damage of the move under way (§2.5).
- **Spine:** 25 capsules through the 26 bones `back11` … `back01`, `center`, `front01` … `front12`, `NeckBase`,
  `NeckMid`. Each capsule lies from one bone back towards the one before, at their bind-pose distance. Their radii
  grow from 0.333 at the tail by 0.2 a capsule, up to 1. The spine's place, for the AI, is where the capsule from
  `front06` starts, at a height of 1. The bones' world places are their posed places: the original turns each bind
  pose about Y before using it, which turns the frame and leaves the place where it is.

### 2.3 States

`W` is read as each test is made. One timer serves every state. A crossfade keeps the old clip current until it
ends, and the new clip then starts from 0. Animation runs at the state's speed, halved while cold and stopped while
frozen (frozen, it does not turn either). Before each state's frame, a stagger takes a leaping serpent to
HitLeap and a risen or biting one to HitRisen; in any other state it is shrugged off.

- **Sleep:** drawn at frame 0 of `intro`, still, at `spawn_jormungandr`. It takes blows. It wakes on the first
  frame its fight runs.
- **Intro:** `intro` from the start, `boss_jormungandr_wakeup` at the head. At 2.2 s, `boss_jormungandr_dive` at the
  head. From 2 s the front tremor shows. When the clip ends, it goes under.
- **Underground:** hidden; the splash and tremors stop; the timer, the bites, the stagger, both hit memories and
  **every status** are cleared. At 0 hit points it dies here (`die`). After `W` it picks a wizard at random, shakes
  the camera (0.5–1 for `W`), draws its bites and rolls:
  - **Rise:** its root goes to the wizard's place now, 1 m down, turned to a yaw of 2.5 rad. `emerge` starts.
  - **Leap:** it looks for a start (§2.4). With none found it tries again next frame.
- **Leap:** pitched 125° about its own X, `dive_attack` loaded and held, hidden. On its first frame it locks its way
  towards the wizard and plays `boss_jormungandr_unburrow`. After `W` it shows, playing its clip, and turns back
  275° over the clip's length about the pivot. At `W` + 0.6 s it growls (`boss_jormungandr_bite`), at `W` + 0.9 s it
  is mid-leap (its dive back in splashes again), and at `W` + 1.9 s it plays `boss_jormungandr_whip`. At 55/62 of
  the clip its tail whips (§2.5). When the clip ends it goes under, its place now the head's.
- **Risen**, by its clip:
  - *emerge:* the tremor shows on the ground where it will come up. After `W` (or once its head has hit the ground)
    it shows, strikes with the emerge's contact and kills the shields and barriers within 2 m. At 12.5% it plays
    `boss_jormungandr_rise`. When the clip ends it blends to `idle` (0.5 s) and the timer starts over.
  - *idle:* at 0 hit points it dies (`die_above`). With no bites left it blends to `submerge` (0.5 s); while that
    blend runs it chooses nothing (ours: the original could still choose if a frozen blend outlasted the wait). Else it
    turns to its wizard (rate 2). After `W` + 0.8 s it chooses: a **spit** if the wizard is over 15 m away or a solid
    barrier stands within 2 m of the point 3 m ahead, unless it has just spat; else a **bite**.
  - *submerge:* it takes no damage. Once it plays `boss_jormungandr_dive`, shakes the camera (0.5–1 for 1 s) and
    kills the shields within 3 m. When the clip ends, it goes under.
  - On entering Risen, the wizard most nearly ahead becomes its target.
- **Spit:** at speed 1.333, it blends to `venom` (0.2 s), plays `boss_jormungandr_prespit` and picks a wizard at
  random. It turns to the wizard (rate 1). At 37.5% it sprays (`jormungandr_spit_spray` at the head,
  `boss_jormungandr_spit`). From 38%, it spits a gob (§2.5) whenever its spit timer is out, up to four, each adding
  0.5 s. The timer has run down since the clip began, so three go out on three frames and the fourth half a second
  of clip later (ours: the timer runs on the clip's time, so the volley keeps that shape at the spit's speed). When the clip ends it blends to `idle` and picks a wizard at random.
- **Bite:** it uses a bite, blends to `attack` (0.15 s), plays `boss_jormungandr_prebite` and strikes with the
  bite's contact. With no target it takes the wizard with the most elements queued. It turns to the wizard (rate
  1.5). At 40% it snaps (`boss_jormungandr_bite`) and speeds up to 1 + U[0, 0.6] × the share of hit points gone.
  When the clip ends it blends to `idle`.
- **HitRisen:** it blends to `hit` (0.5 s); if the old clip was more than 1.1 s in, the old clip holds while the
  blend runs. It plays `boss_jormungandr_pain`. When `hit` ends, it blends to `submerge` (0.6 s): the surfacing is
  over, whatever bites are left.
- **HitLeap:** it blends to `dive_recoil` (0.2 s) and plays `boss_jormungandr_pain`. Past 66.6% of the recoil it
  turns forward again at the leap's rate, running its timer back, and goes under once the timer is down to `W`.
- **Dead:** drawn, upright at its last place and facing, playing `die` (from under the ground) or `die_above`, with
  `boss_jormungandr_death`. It is dead once the clip ends. The corpse stays: it blocks and deals nothing, and takes
  no more damage.

### 2.4 Where a leap starts

Six places 60° apart, 14 m from the wizard, from a random angle. Each is moved onto the nav mesh. One moved more
than 2 m, or with a liquid within 10 m above it, will not do. The **last** that does is taken (`C`), with the way
`d` from it to the wizard `T`. The root's pivot is 6 m past `T` along `d`, 9.5 m under the ground found by dropping
from 14 m past `T`. The root faces back towards `C`. As the pivot turns, the clip throws the body out near `C`,
over `T` and back in past it.

### 2.5 Attacks

Each entry is (attack, element, amount, magnitude), through the victim's own resistances.

| Attack | Damage |
| --- | --- |
| Leap contact | (Damage, Earth, 150, 1) · (Status, Poison, 30, 1) · (Pushed, Earth, 70, 2) |
| Emerge contact, and after it while risen | (Knockback, Earth, 150, 2) · (Damage, Earth, 300, 2) |
| Bite contact | (Damage, Earth, 325, 1) · (Status, Poison, 30, 2) · (Knockback, Earth, 70, 2) |
| Tail whip, radius 3 at bone `back10` | (Knockback, Earth, 200, 4) · (Damage, Earth, 300, 1), weaker towards the edge |
| Spit gob, on a hit | (Status, Poison, 75, 4) |
| Spit gob, on the ground, radius 2 | (Status, Poison, 75, 4) · (Pushed, Earth, 70, 1) |
| Body, on a character | (Knockback, Earth, 60, 1) |
| Body, on a shield or barrier | (Knockback, Earth, 750, 1) |

- The head and the body strike each thing they touch, again only after a quarter of a second. What the head's
  strike kills is torn apart. A sphere or disc shield is touched by its shell (its rim too); one going down is not
  touched.
- A shield or **solid** barrier the head touches takes (Damage, Earth, ¾ of its most, 1) instead. The serpent
  plays `boss_jormungandr_hitobject`, loses 750 and is staggered. A barrier that is not solid is passed.
- A spit gob (radius 0.25) leaves 0.5 m ahead of the head towards the wizard, launched flat so that it drops onto
  where the wizard is now. It trails `jormungandr_spit_projectile`, and each impact plays
  `boss_jormungandr_spit` and `jormungandr_spit_splash`.

### 2.6 The ground

- The head first touching the ground since it was last allowed to (as the serpent comes up, leaps, or is mid-leap):
  a splash of dirt (`jormungandr_dirt_splash_tremor`) on the ground under it, a camera shake of 1 for 0.7 s, and
  during a leap `boss_jormungandr_leap`, or `boss_jormungandr_leapdive` once mid-leap. Asleep, waking and diving
  under, there is none. Biting, it sprays `jormungandr_dirt_splash` where its neck meets the ground instead, once a
  bite.
- Wherever the spine crosses the ground, from `center` towards the tail and towards the neck, a tremor
  (`jormungandr_dirt_tremor`) follows the crossing. There is none under the ground, asleep, or while hidden.
- `jormungandr_dirt_tremor` also marks where it will rise.

### 2.7 The scene

`md_s6BF` spawns it delayed (bar width 0.8) as the level starts. `Start Move` runs `activateBoss` 2 s after Vlad's
dialog, so the serpent wakes during the cutscene. `Start Fight` takes a checkpoint, so a retry replays the spawn and
the activation: the serpent comes back whole and plays its intro again. `End bossfight` waits for `bossDead`. The
serpent heeds no `bossMessage`.

### 2.8 Where OpenMagicka differs from the original

The original has a few slips. OpenMagicka keeps those that shape the fight and fixes those that are plainly
broken:

| Original | OpenMagicka |
| --- | --- |
| The spit's speed (1.333) and the bite's speed-up are overwritten before the clip runs | **They take effect.** |
| The leap's status entry has no element, so the leap never poisons | **It poisons** (30 × 1). |
| The emerge's contact goes on while it idles after | Kept. |
| A "destroy the shield outright" case for the ram can never run | Kept (not made). |
| The leap's start is the last good place, but its way is the last place tried's | The last good place, **with its own way**. |
| The ground shakes again each frame no leap start is found | **Once** a time under the ground. |
| The ground's shake rumbles the pads from the world's origin | No pad rumble yet. |
| Three gobs on three frames, the fourth half a second later | Kept (at our fixed step). |
| A wizard above the head makes the gob's aim fail | **Aimed at in 0.5 s.** |
| Hidden mid-leap, the tremors show where the body last was | **None while hidden.** |
| The leap's turn and its timers (shown, and played back in the recoil) run on real time, whatever the clip's speed (frozen, the root still turns) | **On the clip's time.** |
| The spit timer runs on real time (at the fixed spit speed the volley would spread) | **On the clip's time.** |
| Blending into its dive after a flinch, it can still choose a bite | **It chooses nothing while diving.** |
| A spray's check against the spine never matches | Kept: the spray passes the body. |
| A stagger during Risen can end in `die` after the dive under | Kept. |
