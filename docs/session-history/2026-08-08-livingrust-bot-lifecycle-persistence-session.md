# LivingRust — Monument Route Wrap-up, Bot Lifecycle, and Persistence Session Summary

**Date:** 2026-08-08 (continues directly from `2026-08-07-livingrust-monument-routes-session.md` — read that one first; this session opened mid-way through extending the Powerline tower route past Ladder_2)
**Project:** `G:\Projects\TemplateProject` (Carbon plugin, live test server under `Rust\server\`, identity "LivingRust")
**Scope:** finished (then deliberately stopped short of) the Powerline tower monument route, then pivoted entirely to bot lifecycle — health/damage response, persistent death/respawn identity, starting kit, and full state persistence across plugin reload/server restart. Ended mid-investigation of a userID collision bug with Rust's own NPCs.

---

## Part 1: Monument route — Ladder_2 through Ladder_4 (then abandoned)

Continued extending `KnownMonumentRoutes["powerline_a (1)"]` in `Plugin\LivingRust.MonumentRoutes.cs` using **fresh, purpose-built `/lr.follow` traces** (the user's call, after twice guessing wrong from an older trace) rather than inferring waypoints from old data.

Recurring failure pattern this whole arc, fixed the same way every time: **a straight line between two real dwell points cuts across real multi-platform geometry** and the ground probe lands on the wrong (usually much lower, or much higher) surface. Fix each time was the same: insert the real intermediate dwell point(s) from the trace instead of guessing a shortcut. Happened for the second duck-climb (needed 2 extra points) and the post-Ladder_3 plank-crossing riser (needed 2 extra points).

**New mechanism added: `NoHeadroomTo` / `ClimbLadderIgnoringHeadroom`.** The plank-crossing zone near Ladder_4 sits directly under the tower's oversized frame collider at head height even after `NonSteppableColliderNames` excludes it from the ground probe — added an `ignoreHeadroom` param to `NavigationManager.TryGetNextStep` (skips only the headroom/duck raycast, ground collision stays fully active) threaded through `TryFindSidestep`/`TrySidestepAngle` and a new `MonumentWaypoint.NoHeadroomTo(...)`. Discovered the **ladder-approach step also needed the same bypass** (a separate code path, `AdvanceClimb`'s `TryGetNextStep` call, which doesn't get `ignoreHeadroom` from an ordinary walk waypoint) — added `ClimbState.IgnoreHeadroomOnApproach` / `MonumentWaypoint.ClimbLadderIgnoringHeadroom`, used only for this one ladder.

**Final, unresolved blocker:** at Ladder_4's exact mount point, three stacked platform meshes (`Lvl4PlatformRamp`/`Lvl5PlatformA`/`Lvl6PlatformA`) overlap in x/z with ~5m vertical gaps between them. The ladder-approach's naive probe (no sidestep fallback) kept landing on the wrong (much higher) one — `step too high onto 'Lvl6PlatformA' (3.96m)`. Pushed the authored walk-in progressively closer (down to the ladder's own trigger footprint, using the user's own confirmed-solid scanned position) but the **exact same failure reproduced identically** each time, because the walk was "arriving" within the 2m tolerance and handing off to the same broken approach step regardless of how close it got.

**User's call: stop here, pivot to a different feature area.** Ladders 1–3, both duck-climb sections, and the full plank-crossing zone are all solid and tested; only the final ~1.5m into Ladder_4 stays stuck. Documented as a known, deliberately-unfixed gap in the code comments rather than left silently broken.

---

## Part 2: Bot lifecycle — names, health, death, respawn, starting kit

User's framing for what "aspect of LivingRust" to work on next: **names, inventories, health, hunger management.** Investigated the existing (mostly unconnected) scaffold in `Managers/`, `AI/`, `Factories/`, `Inventory/` before touching anything:
- **Names**: already fully working — `AliasGenerator` → `CharacterFactory` → `npc.displayName`. Confirmed this is where every test bot's name this whole project has come from.
- **Health/needs/inventory**: mostly dead scaffold. `NeedManager.UpdateNeeds` is defined but never called anywhere. `InventoryManager` (a generic string→int dict) is never instantiated or attached to anything. Bots spawn with Rust's real inventory completely empty.

User picked **health & damage response** first.

### Death handling
Added `OnPlayerDeath(BasePlayer, HitInfo)` in the new-ish `LivingRust.Hooks.cs` (previously empty). Confirmed via reflection that `HitInfo.damageTypes.GetMajorityDamageType()` and `BasePlayer.Die(HitInfo)` are the right real APIs. Live-tested against headshots, body/limb-shot-then-downed, and incendiary rounds (`Heat` damage type correctly resolved, matched Rust's own native "killed by fireball_small" log line exactly). Confirmed via live testing that Rust's own combat/downed/bleedout/item-drop-on-death pipeline works completely unmodified on these fake `BasePlayer`s — nothing custom needed there, including the full-gib (explosive/fire) loot-bag-instead-of-corpse case.

Also confirmed (via reflection, not yet live-tested) that `BasePlayer` implements `IMedicalToolTarget` unconditionally with no `IsConnected`/`IsNpc` gate, so a real player's medical syringe should heal a bot exactly like a real player — relevant for future "self-administered healing" AI work once inventory exists.

### Persistent identity is the key design decision
User's explicit spec (paraphrased): a bot that dies should respawn as **the same character** — same name, same blueprints, same base ownership — losing only its held items (like a real player), not become a new random bot. This drove a real architecture change: **`Character.BotId`** (new field) is assigned once at creation by `CharacterManager` (seeded from the loaded save's max ID so a real restart never collides) and reused as `npc.userID` for every spawn *and* respawn of that character's whole life — never regenerated. Rust ties blueprint unlocks and building-privilege authorization to `userID`, so this one field is what makes progression/base ownership survive death.

### Death → respawn: two false starts, then working
1. **First attempt**: call `BasePlayer.Respawn()` (Rust's own real respawn entry point) directly on the dying entity after a delay. **Failed live** — every single test hit `"couldn't respawn - its BasePlayer was already cleaned up"`. Root cause: unlike a real connected player (whose object waits around for them to pick a respawn option), a disconnected fake `BasePlayer` gets destroyed almost immediately after `Die()`.
2. **Second attempt**: create a *fresh* `BasePlayer`, set `userID` first, call `ServerMgr.FindSpawnPoint(npc)` (assumed it internally checks owned sleeping bags by `userID`, same as a real respawn). **Failed live** — landed on a beach spawn (`isProcedualSpawn: True`) even with a bag explicitly assigned via `/lr.debug.claimbag`. That assumption was inference from the parameter name, never actually verified.
3. **Working fix**: built `TryFindOwnedBag` explicitly — scans every `SleepingBag` via `BaseNetworkable.serverEntities` (confirmed `IEnumerable<BaseNetworkable>`), checks `bag.OwnerID == botId` and `bag.ValidForPlayer(botId, false)` (both real, reflection-confirmed members), uses `bag.GetSpawnPos(out pos, out rot)` directly. `ServerMgr.FindSpawnPoint` is now only used as the plain-beach fallback when no owned bag exists. **Live-tested working**: log shows `respawned at ... (owned sleeping bag)` for multiple bots after this fix.

### `/lr.debug.claimbag` (new debug command)
Rust's own "assign to friend" UI only lists real Steam friends, so there was no way to give a bot ownership of a bag. Added `/lr.debug.claimbag [alias]` — aims like `/lr.debug.look` (raycast + small-radius fallback) to find a bag, assigns `OwnerID` to the nearest (or named) spawned survivor's `BotId`. **Bug found and fixed**: the command originally only sent a chat message, never `Puts()` — meaning it had zero server-log trace, which is why an earlier "no joy" report couldn't be diagnosed from the log at all. Now logs every outcome.

### Starting kit
`GiveStartingKit` (rock + torch, matching the user's explicit "fresh spawn" spec) was wired into the respawn path but initially missed from the *initial* `/lr.spawn` path — user caught this immediately after confirming respawn worked (self-corrected their own first report). One-line fix: `SpawnSurvivor` now also calls it.

### Also shown separately from the nametag (user request)
Spawn chat message now includes `(ID {BotId})` — deliberately not folded into `displayName` — since the bot's raw userID is needed for `/lr.debug.claimbag` and there's no other way to see it (Rust's friend-assignment UI can't target a fake player).

---

## Part 3: Full state persistence across plugin reload / server restart

User's framing, verbatim-adjacent: if the server restarts and bots that own bases/bags/gear just vanish and never come back, that's a real problem — orphaned bases with no owner. `Unload()` previously called `DespawnAllBots()` **unconditionally on every reload** (that function's own doc comment already flagged this as "testing-only... should be removed/gated" once real persistence existed — this session is that moment).

Checked whether Rust's own entity-save already covers disconnected `BasePlayer`s (would have simplified things a lot) — `BasePlayer.Save(SaveInfo)` exists with no obvious skip-gate visible via reflection, but **couldn't be confirmed without an actual server restart test**, which wasn't done (too disruptive to do unilaterally). Decided to build LivingRust's own explicit capture/restore instead of gambling on unverified native behavior — same "verify or build it yourself" pattern as the sleeping-bag lookup in Part 2.

**New models**: `Models/SavedItem.cs` (ItemId, Amount, Condition, SkinId, Position, Container), `Models/InventorySlot.cs` (Main/Belt/Wear enum). `Character` gained `Rotation`, `Health`, `Inventory` (`List<SavedItem>`); `Spawned`'s contract tightened to "recomputed from the live world every capture, not hand-maintained."

**New file `Plugin/LivingRust.Persistence.cs`**:
- `CaptureAllLiveState()` — walks every survivor; if its `BasePlayer` is alive, captures position/rotation/health/full 3-container inventory into `Character` and sets `Spawned = true`; otherwise sets `Spawned = false`. Called from `Unload()` right before `_engine.Stop()` (which saves).
- `RestoreSpawnedSurvivors()` — walks every loaded `Character` with `Spawned == true`; if a live `BasePlayer` with that `userID` already exists in the world (a plugin hot-reload never actually destroys entities on its own once `DespawnAllBots()` isn't called), just re-links `Survivor.Player` — free, nothing lost. Otherwise (a real restart, entity genuinely gone) spawns fresh at the saved position/rotation via an extended `SpawnSurvivor` overload (`health`, `restoreInventory` params — when inventory is given, restores it via `ItemManager.CreateByItemID` + `Item.MoveToContainer` instead of granting the starting kit). Called from `OnServerInitialized()` right after `_engine.Start()`.
- `DespawnAllBots()`/`/lr.debug.despawnall` kept as a manual-only clean-slate tool, no longer auto-invoked.

**Live-tested successfully on the very first deploy** — the log line `Restored 4 still-existing and 2 freshly respawned survivor(s) from the persistent roster` exercised *both* code paths in one shot (the 2 "freshly respawned" were bots killed by the *old* despawn-on-reload behavior on the previous deploy, so their restored inventory was correctly empty — that predates the feature, not a bug in it).

---

## Open / in-progress at session end: userID collision with Rust's own NPCs

User found a real bug testing something unrelated: killed a Bradley APC and the scientists it spawns, and their **loot bags showed LivingRust bot names**. Hypothesis (user's own, and it holds up): `CharacterManager`'s `BotId` range (sequential from 1000) might collide with whatever `userID` Rust assigns its own NPCs.

Confirmed via reflection: `ScientistNPC : HumanNPC : NPCPlayer : BasePlayer`, and `NPCPlayer` redeclares a real `userID` field — so a collision is structurally possible. **Could not confirm the exact value Rust assigns spawned scientists** (no IL disassembler available, method bodies aren't reflectable). Added `userID`/`displayName` to `/lr.debug.look`'s `ReportCollider` output as a diagnostic so the next live test can observe a real scientist's actual `userID` directly.

**Not yet implemented at cutoff**: had decided to move `CharacterManager._nextBotId`'s starting value from `1000` to something far larger but still safely under the hard `10,000,000` `IsBot` ceiling (was leaning toward ~5,000,000, reasoning: large enough gap from any plausible small/sequential NPC ID convention, still leaves massive headroom) — this edit was **not yet made** when the session's memory was checked. Existing already-created Characters (BotId in the 1000s from this session's testing) would keep their old, possibly-colliding IDs unless explicitly migrated (not planned — this is dev/test data).

---

## Next steps for when this resumes

1. **Finish the userID-collision fix**: change `CharacterManager._nextBotId`'s starting value to a safer range (stay under 10,000,000 — that ceiling is load-bearing, see `CharacterManager`'s own doc comment on `IsBot`/occlusion-crash-avoidance), rebuild, redeploy.
2. **Verify via the new diagnostic**: ask the user to aim `/lr.debug.look` at a live (not-yet-killed) Bradley-spawned or oilrig scientist and report the `userID` shown, to confirm/refute the collision theory with real data rather than the current reflection-only inference.
3. **Consider a live collision check** (mentioned but not built, given architecture-layering concerns — `CharacterManager` doesn't currently have Rust API access): when issuing a new `BotId`, could scan `BaseNetworkable.serverEntities` to confirm nothing already holds that exact `userID`, as defense-in-depth on top of the range separation.
4. **Full server-restart test still hasn't happened** — the persistence feature (Part 3) has only been validated across plugin hot-reloads so far. A genuine stop/restart would be the strongest test of the "respawn with restored inventory" path specifically, with real (non-empty) saved gear this time.
5. **Ladder_4's final approach** (Part 1) remains a known, deliberately-parked gap — three overlapping platform meshes at the mount point, naive no-sidestep approach step picks the wrong one. Not scheduled to resume unless the user brings it back up.
6. **User's original 4-part framing** ("names, inventories, health, hunger") — names and health/damage/respawn are now solid. Inventory *persistence* (this session's Part 3) is done; inventory *management* in the sense of autonomous pickup/looting AI is explicitly deferred to a future session (user's own scoping call). Hunger/needs (`NeedManager`/`NeedState`) hasn't been touched yet — still fully dead scaffold.

---

## Part 4 (same day, continued): userID collision fix, then a 5-bug persistence-hardening chain

Picked up exactly at the userID-collision open item above. Each fix in this chain was found by testing the previous one and hitting the next layer of the same underlying problem — persistence "worked" only in the narrow sense of surviving a restart; making it actually *usable* (bots wake up, resume dying/respawning, and stay in sync no matter how the user restarts) took five more rounds.

### 1. userID collision — confirmed and fixed with hard evidence, not just live-test inference
Decompiled `Assembly-CSharp.dll` with `ilspycmd` (already installed as a dotnet tool on this machine, `~/.dotnet/tools/ilspycmd` — no need to ask the user to install anything). Found the exact mechanism in `BasePlayer.ServerInit()`: every native NPC with `userID == 0` gets assigned one from a single process-wide `botIdCounter` that starts at `1` and increments by 1 per spawn for the whole server session — structurally identical to LivingRust's own `_nextBotId` counter starting at 1000, guaranteeing an eventual collision in any active session. Also checked `SaveRestore.WorldSetup`'s call to `BasePlayer.ReserveBotIds()` on load — confirmed restarts are self-correcting on their own (native load reserves whatever's actually saved), so the only real danger was the live, same-session race. Fix: `CharacterManager._nextBotId` now starts at 5,000,000 (safely under the 10,000,000 `IsBot` ceiling, effectively unreachable by Rust's own counter in one session). No extra runtime collision-scan needed given the mechanism is now fully understood. Shipped without waiting on the planned live `/lr.debug.look` verification — the decompiled evidence was already conclusive.

### 2. Bots stayed asleep after a real restart
User confirmed (with a screenshot) that a real full restart *does* persist bots — but they came back as inert, curled-up sleeping ragdolls instead of resuming activity. Decompiled `BasePlayer.Load(LoadInfo)`: when `info.fromDisk`, it unconditionally calls `StartSleeping()` and sets `Connected = false` — meaning Rust's own native world-load *does* cover disconnected fake `BasePlayer`s (resolving an explicit open question from Part 3 above, without ever needing the planned-but-undone native-save test). A real player wakes via their client's reconnect handshake; bots have none, so nothing was ever calling the matching `EndSleeping()`. Fix: call `existing.EndSleeping()` on relink in `RestoreSpawnedSurvivors` (`LivingRust.Persistence.cs`).

### 3. Sleep fix didn't show up visually
Second screenshot showed one relinked bot (`SilentGoblin`) standing/walking, but another still asleep — user attributed SilentGoblin's wakefulness to "being live from before," not to the fix, so this needed more than a visual read. Traced it: `EndSleeping()` only flips server-side flags; the client's sleeping-ragdoll-vs-standing visual is driven by `modelState.sleeping`, which for a real connected player gets refreshed every tick by `CheckModelState()` — a path gated on `ActivePlayerInd`/client input that disconnected bots never run. `BasePlayer.Save()` does refresh it unconditionally, though, and that's exactly what `SendNetworkUpdateImmediate()` triggers — the identical "no live refresh path" pattern already known from `displayName` (`SpawnSurvivor` already calls it for that reason). Fix: call it right after `EndSleeping()` too.

### 4. Killed bots weren't respawning at all
User restarted, killed 8 bots, reported: no respawn anywhere, not even near default shoreline spawns. Log showed the "Restored N still-existing" line as expected, but **zero** `"'X' died (...)"` lines despite 8 real kills — `OnPlayerDeath`'s very first guard (`if survivor.Character.State == CharacterState.Dead) return;`) was silently tripping every time. Pulled the actual saved roster (`Rust\server\LivingRust\world.json`) via PowerShell's `ConvertFrom-Json` (no Python available on this box) and found **every** 1000+-range character sitting at `State: 3` (Dead) in the file, regardless of `Spawned`. Root cause: `Character.State` is only ever written in 3 places (constructor default `Alive`, `OnPlayerDeath`→`Dead`, `RespawnSurvivor`→`Alive`) — and `RespawnSurvivor` runs 5 seconds after death via `timer.Once`, which does not survive a plugin `Unload`. Any death within 5 seconds of a reload/restart (extremely likely during iterative dev/test) leaves `State` stuck at `Dead` forever, since nothing else ever resets it — permanently disabling death handling for that character from then on. Fix: set `character.State = CharacterState.Alive` in both `SpawnSurvivor` (`LivingRust.Commands.cs`) and the relink branch of `RestoreSpawnedSurvivors` (a live, non-destroyed entity is itself proof of aliveness, regardless of what got persisted).

### 5. Some bots were invisible to the restore logic entirely
Same roster dump showed several characters (`BotId` 1000–1006) stuck at `Spawned: False` + `State: Dead` — the identical death-timing race as #4, but for the `Spawned` flag: captured correctly as `false` mid-death (old entity destroyed, new one 5s away), then never revisited. `RestoreSpawnedSurvivors` was skipping any character with `Spawned == false` *before* ever checking whether a live entity actually existed, permanently orphaning them. Fix: check for a live entity first (authoritative over whatever got persisted); only skip entirely if no entity exists *and* `Spawned` was false (this guard matters — without it, all ~350 never-spawned background/population characters in the roster would get auto-resurrected).

### 6. Roster was still stale after all of the above — the actual root cause of the remaining sleeping bots
User restarted again: one specific bot (`MadMiner`, misheard/typed as "Mandminer") was still asleep, then reported finding more sleeping bots around the world. Checked the log: **zero** `"Stopping LivingRust"` / `"Captured live state"` / `"unloaded"` lines anywhere — `Unload()` had never run at all before that restart. Asked the user directly how they restart: they run the in-game `save` console command, then close the server window directly — a hard kill, not a graceful Carbon shutdown, so `Unload()` (the only place `CaptureAllLiveState`/`SaveCharacters` ran) never gets a chance to fire. Rust's own native world-save runs completely independently (confirmed its entity count grew between restarts on its own schedule) and correctly captured the raw `BasePlayer` bodies regardless — so LivingRust's own `world.json` roster was simply stuck at whatever it was after the last clean Unload, missing everything spawned/killed since. Fix: added an `OnServerSave` hook (`LivingRust.Main.cs`) that also runs `CaptureAllLiveState()` + `SaveCharacters()`. Verified via decompiling `SaveRestore` that the manual `save` command and the periodic auto-save both funnel through the identical `DoAutomatedSave()`/`Save()` pipeline, so the hook covers the user's exact manual-save-then-hard-kill workflow, not just auto-save.

**User confirmed working** after this final fix, testing their actual real workflow end-to-end (manual `save`, close window, relaunch).

### Also flagged, not yet fixed
`LivingRustEngine.Start()` auto-creates new "population" characters via `PopulationManager.GetRequiredSurvivors` on every single boot if under some target count. The saved roster has ballooned to 356+ characters, the vast majority `BotId: 0` (predating the `BotId` system entirely), never spawned, pure dead weight — almost certainly accumulated across many restarts over the project's history. Not addressed this session; flagged to the user for later.

### Methodology note worth repeating
Every fix in this chain came from reading actual evidence — decompiling the real game assembly with `ilspycmd` for engine-internals questions (#1, #2, #3), and reading the actual server log + saved JSON roster with PowerShell for behavioral questions (#4, #5, #6) — never from reasoning about the C# source in isolation or guessing at Rust's internals. Worth defaulting to this same approach for the next persistence-adjacent bug, if one surfaces.

### Next steps for when this resumes
1. The population-bloat issue (356+ dead-weight characters) — not urgent, but will keep growing on every restart until addressed.
2. Hunger/needs (`NeedManager`/`NeedState`) — still fully dead scaffold, untouched.
3. Autonomous item pickup/looting AI — explicitly deferred (user's own scoping call).
4. Ladder_4's final approach (Part 1, prior session) — still a known, deliberately-parked gap.

---

## Part 5 (same day, continued): inventory persistence validated, new `/lr.debug.giveitem` command

User wanted to move on to looting/inventory-management AI, but first wanted to prove inventory persistence specifically (as opposed to just position/health/name) by manually adding items to a bot and restarting.

**Blocker**: user tried giving items to a bot via Rust's normal admin give-to-player console command (by both playerID and name) — got "couldn't find player" both ways. Diagnosed via decompiling `BasePlayer` (`ilspycmd`): the native `BasePlayer.Find(string)` helper (which player-targeting admin commands use) only searches `activePlayerList`/`activePlayerLookup`, populated exclusively by `PlayerInit(Network.Connection)` — the real client-connection handshake. Disconnected bots never go through it, so no native command can ever target them by name or ID, regardless of state — a structural gap, not a usage mistake. (Also checked `BasePlayer.FindBot()`/the `bots` static list as an alternative — inconsistent for our bots too, since it only gets populated either by native `userID==0` assignment, which we never trigger, or by `EndSleeping()`, so a freshly-`/lr.spawn`ed bot that never went through a sleep/wake cycle wouldn't be in it either.)

**Fix**: added `/lr.debug.giveitem <shortname> [amount] [alias]` (`Plugin/LivingRust.Debug.cs`), following the same pattern as `/lr.debug.claimbag` — resolves the target bot through LivingRust's own `Survivor`/`Character` roster (nearest-to-player by default, or by alias) instead of Rust's native lookup, then calls `ItemManager.CreateByName` + `inventory.GiveItem` + `SendNetworkUpdateImmediate()` directly. One build hiccup along the way: `ConsoleSystem.Arg.Args` is actually `Facepunch.StringView[]`, not `string[]` — needed an explicit `.Select(a => a.ToString())` (existing code elsewhere in the file only ever did `string.Join(" ", arg.Args)` or single-element `==` comparisons, which work with any element type, so this mismatch hadn't surfaced before).

**User confirmed working end-to-end**: gave an item via the new command, ran `save`, hard-killed the window, restarted, item was still in the bot's inventory, then killing the bot dropped it normally (Rust's own native death/loot-bag behavior, unmodified). Inventory persistence is now proven, not just assumed from the capture/restore code being present.

Next up per user: move into looting/inventory-management AI (previously deferred) now that the persistence foundation is validated.

---

## Part 6 (same day, continued): task-driven looting AI (Phase A: real interaction, no animation yet), a navmesh stuck-detection fix, and a despawnall rewrite

### Looting task design and Phase A scoping
User's ask: bots should treat "almost everything as worth picking up," with WHERE/WHEN driven by a task/need concept (worked example: low on scrap/components → go loot). Before building, surveyed the existing `AI/` scaffold (`CharacterBrain`/`NeedState`/`GoalType`) via a background Explore agent - confirmed it's fully dead (nothing decays `NeedState`, nothing consumes `CharacterBrain.Think()`'s `Decision`) and unrelated to what was needed here, so deliberately built a separate, minimal `TaskType` enum + `Character.CurrentTask` field rather than wiring through that dead system. Confirmed `ItemCategory.Component` is a real, built-in Rust category covering all the "components" items the user listed (road signs, pipes, fuses, sheet metal, etc.) - useful for a future need-driven trigger, though Phase A ended up not needing it (see loot-scope decision below).

Three design decisions confirmed via AskUserQuestion before building: (1) loot everything in a container once there, not just task-relevant items: (2) start with a manual `/lr.debug.settask` trigger, autonomous need-based triggering later; (3) go idle in place when a task completes, no "return home" behavior yet.

Then the user raised the bigger point: real Rust bots interacting with barrels/containers should involve an actual tool-swing animation (rock/pickaxe/hatchet), not a teleport-style instant loot. Researched this properly before writing anything: confirmed real melee combat is entirely **client-driven** (`BaseMelee`'s `PlayerAttack` RPC - the client decides when to swing and sends the hit to the server), so bots structurally can't use that path. Rust's own AI NPCs (scientists) *do* show real attack animations to bystanders without a client, but via a separate, purpose-built server-driven system (`IAIAttack`/`GetAttackEntity`/`TickAttack`) built into `NPCPlayer`/`HumanNPC` specifically - not something plain `BasePlayer` (what our bots are) inherits. Also confirmed: applying real damage is easy (already-proven `Hurt()` API from the death system); the swing animation itself is the genuinely hard, separate problem. Agreed to split into **Phase A** (real damage-based destruction, no animation) now, **Phase B** (the animation) later.

### Phase A implementation
New `Plugin/LivingRust.Looting.cs`: `/lr.debug.settask [alias]` scans a plain 50m radius around the bot's current position (deliberately NOT monument-seeking - user's own correction, since barrels/junkpiles spawn on roads too, not just monuments), finds the nearest un-visited `StorageContainer` (covers both world `LootContainer` barrels/crates and player storage - confirmed via decompiling `Assembly-CSharp.dll` that `LootContainer : StorageContainer`), walks to it, then repeatedly damages it via `Hurt(40f, DamageType.Blunt, npc, useProtection: false)` (~1 hit/sec, same proven API as the death system) until destroyed, transferring its inventory just BEFORE the finishing blow (avoids racing Rust's own `Die()`/loot-scatter handling), then moves to the next container. Loops until inventory full or nothing left nearby.

### Live-test bug chain (each found via the previous fix's own test)
1. **Bots tried to stand on/inside barrels.** `StartWalking` targeted `container.transform.position` - the exact center, inside the barrel's own solid collider, unreachable by definition. Produced silent infinite jitter (no `Stuck`/`NoPath` ever logged - confirmed via log that path-following itself was fine, only the last few centimeters never resolved). Fixed with `GetApproachPoint()`: closest point on the container's `WorldSpaceBounds()` to the bot, pulled back a standoff distance, with height from a real ground raycast (`TryFindGroundBelow`, not the coarse terrain heightmap - critical for containers on elevated monument platforms). Same root cause plausibly explained a `powerline_a` "won't move" report too.
2. **Standoff distance felt too far / a bot destroyed two barrels through a solid wall** (MadScav, user-confirmed via a nearby scan of the wall). Fixed standoff 1m→0.6m, and added a real line-of-sight check (`Physics.Linecast` from eyes to the container, must hit the container itself first) before every hit - if blocked, the bot gives up on that container (already in the visited set) and moves to the next, rather than hitting through the wall. Explicitly scoped as "stop the cheat," not "route around the wall" - that's real future work if it turns out to matter often.
3. **A bot (BigCamper) appeared stuck near a container, killed by the user before resolving.** Root cause: a **pre-existing, general navigation gap**, not a looting bug - `AdvanceAlongPath` can report `Progressing` every tick while real position barely changes (a short, technically-valid step on a fragmented navmesh island - Rust's baked navmesh splits into small disconnected islands around scattered dressing props). This exact phenomenon was already independently solved once before in this codebase, just for a different command (`/lr.climb.monument`'s own `ClimbMonumentMinProgressDistance`/`ClimbMonumentGiveUpTicks`) - `StartWalking` never got the same protection, only a blunt 200s timeout with zero diagnostic. Fixed by adding real per-tick displacement tracking to `StartWalking` itself (benefits every caller, not just looting) plus a new `onFailed` callback parameter, fired on every way a walk can fail (stuck-with-no-real-progress, genuine `Stuck`/`NoPath`, shoreline, or timeout) - wired into the loot task so it abandons an unreachable container instead of leaving the survivor's task silently dangling forever.

User confirmed this whole chain resolved cleanly on retest ("Sweet, that looked a lot better").

### `/lr.debug.despawnall` clean-slate rewrite (separate side-quest, user's own request)
User wanted despawnall to stop being a "kill and respawn" reset and become a true clean slate - erasing `Character` records entirely (`CharacterManager`, `SurvivorManager`, `world.json`), not just killing the `BasePlayer`. Also incidentally sweeps the still-unfixed population-bloat backlog (350+ dead-weight `BotId: 0` characters) as a side effect, since they're in the same roster. Deliberately does NOT reset the `BotId` counter back down - reusing freed IDs would reopen the exact same-session collision risk with Rust's native NPCs that the 5,000,000 starting value exists to avoid; confirmed with the user that the ~5M-ID headroom is nowhere close to a real concern even under heavy dev-testing churn.

Took three iterations to actually work, each confirmed/refuted via log evidence (`world.json` state + a proper `despawnall:` summary log line added along the way, since the command originally had zero diagnostic trace - same historical gap `claimbag` once had):
1. First version only cleared tracking + killed whatever `survivor.Player` pointed to - user reported bots still standing. Log showed a `despawnall: wiped 0 character(s)` summary, i.e. **nothing was even tracked** at the time - proved these were bots with no `Character` record at all (not a stale reference), almost certainly `BasePlayer` entities Rust's own independent native world-save kept alive from before some of today's persistence fixes existed.
2. Added a third pass sweeping the live world directly (`BaseNetworkable.serverEntities`) for any `BasePlayer` in LivingRust's own known `BotId` ranges (5,000,000+ current, plus the narrow legacy 1000-1099 pre-fix range) regardless of tracking - deliberately NOT a blanket sweep of every bot-tier NPC (that approach was abandoned once already, for catching unrelated native NPCs). This time bots actually died.
3. But then they visibly respawned once, and only a *second* kill stuck. Root cause: `Die()` was being called on still-tracked survivors, which fires Carbon's `OnPlayerDeath` hook exactly like a real combat death - the hook's `FindSurvivorByPlayer` lookup still matched (tracking wasn't removed until after killing), scheduling a completely unwanted `RespawnSurvivor` for a Character mid-deletion. Fixed by reordering: untrack every survivor from `CharacterManager`/`SurvivorManager` FIRST, kill everything second - so `OnPlayerDeath`'s lookup can never match a despawnall-triggered death. User confirmed this worked cleanly.

### Open at session end (superseded by Part 7 below - see that for the actual end-of-session state)
1. Phase B (the actual swing animation) - not started, real research needed (see Phase A scoping above).
2. Population-bloat backlog - now gets cleared by despawnall as a side effect, but the underlying auto-creation in `LivingRustEngine.Start()` is still unaddressed and will refill some empty characters on the next boot regardless.
3. Everything else from Part 5's list (hunger/needs dead scaffold, Ladder_4 parked gap) unchanged.

---

## Part 7 (same day, continued): belt capacity, junkpile_j avoidance, and a general "give up on this whole area" fix - session wrap-up

Three more live-test-driven fixes to the looting task, then a session close.

### Belt filling
Loot task was silently capped at main inventory only - `TransferAllItems`'s loop and `ContinueLootTask`'s "am I full?" check both only ever looked at `containerMain`, so the moment main filled up the whole loop bailed even though the belt still had room (Rust's own `GiveItem` fallback, already in the code, could have placed items there but never got the chance). Fixed by checking both containers together (`IsInventoryFull` = main AND belt both full) in both places. Deliberately left `containerWear` out of the fullness check - that's for worn clothing/armor slots specifically, not general carry capacity.

### junkpile_j avoidance - first pass
User reported junkpile_j specifically (the variant with a van) as a bot-trap, since its barrels/crates sit physically inside the van model rather than out in the open like other junkpile letters. Added `IsInJunkpileJVan` - walks up a candidate container's transform hierarchy looking for "junkpile_j" in any parent's name (a container's own `ShortPrefabName` is identical everywhere, so only the containing structure's name distinguishes this case), excluding matches from ever being selected as a target. The exact prefab name was a guess at the time (no live access to verify) - flagged as such and asked the user to confirm via `/lr.debug.look` if it didn't work.

### Trace review confirmed a lot working, plus a deeper issue
User ran a fresh trace/scan and reported back:
- **junkpile_j's exact prefab name confirmed correct** via their own `/lr.debug.look` scan: `entity: JunkPile ('junkpile_j') ... parent 'assets/prefabs/misc/junkpile/junkpile_j.prefab'` - the guess was right, no fix needed there.
- **Belt-filling fix confirmed working live**: one survivor (`FastGrub3042`) looted a long chain of containers including an actual airdrop (`supply_drop`, 13 item stacks) before correctly reporting "full up" - clearly past what main alone would hold.
- **A different survivor (`MadBean`) thrashed badly**: one successful loot, then a long cascade of line-of-sight blocks and walk failures against nearly every other nearby candidate before finally giving up cleanly (no infinite stall, thanks to Part 6's stuck-detection work - just very wasteful).
- **despawnall's two safety-net passes (added Part 6) both proved themselves for real** on this same trace: summary line showed `25 killed, 3 already gone, 5 stale-reference orphans, 1 fully untracked entity` - confirming those extra passes catch real leftovers, not just hypothetical ones (likely residue from the day's many hot-reloads).

Initially guessed `MadBean`'s thrashing was generic "cluttered building interior" - **user corrected this**: it was specifically camped near/around junkpile_j's own obstructive geometry (the van mesh, `cave_gravel_a/b` rock props, a `junkpile_base` terrain mesh, an `AILOSBlocker`) while trying to reach *other*, unrelated nearby containers - not trying to loot junkpile_j's own loot at all. Confirms `IsInJunkpileJVan` (which only excludes junkpile_j's own containers from being *picked*) does nothing for this case, since the survivor wasn't targeting junkpile_j's loot - it was just physically stuck near junkpile_j while pathing to something else.

### The actual fix: poison the whole area after repeated failures
User's own suggested shape of the fix: "if I can't loot this properly, move on to the next task/area." Implemented as: track consecutive loot failures (walk-unreachable via `StartWalking`'s `onFailed`, or no-line-of-sight via `StartAttackingContainer`) in a new per-run `LootTaskState` (replacing the bare `HashSet<NetworkableId> visited` threaded through the whole find-walk-loot chain). After 4 in a row, poison a 20m radius around the survivor's current position (sized off junkpile_j's own confirmed 10m "Prevent Building" trigger radius, plus margin) - future candidate searches this run skip anything inside any poisoned zone, forcing the next pick meaningfully further away rather than the next-nearest thing still tangled in the same obstruction. Resets to zero on any successful loot, so one unlucky pick doesn't trigger it. `StartAttackingContainer`'s callback was split into `onSuccess`/`onFailed` (was a single generic `onDone`) so the streak can be reset/incremented correctly depending on which one actually happened.

Not yet live-tested at session end - implemented and deployed in response to the user's diagnosis, but no confirmation trace yet.

### Session-end state
Persistence (position/health/inventory/wake-on-restore/death-respawn, all across the user's real save-then-hard-kill restart workflow), Phase A looting (task-driven, walk-to-container, real damage-based destruction, belt-aware, junkpile_j-aware, poisoned-area-aware), and a genuinely comprehensive `/lr.debug.despawnall` clean-slate are all implemented and mostly live-confirmed (poisoned-zone fix is the one exception - deployed, not yet tested).

### Next steps for when this resumes
1. **Confirm the poisoned-zone fix** actually stops `MadBean`-style thrashing near junkpile_j on a live retest.
2. **Phase B** - the actual tool-swing animation. Real research needed: Rust's melee combat is client-driven (`BaseMelee`'s `PlayerAttack` RPC), and the server-driven alternative Rust's own AI NPCs use (`IAIAttack`/`GetAttackEntity`/`TickAttack`) lives in `NPCPlayer`/`HumanNPC`, not plain `BasePlayer` (what these bots are) - no clean answer yet, just a scoped-out problem.
3. **Population-bloat root cause** (`PopulationManager.GetRequiredSurvivors` auto-creating characters every boot) - despawnall now clears the backlog as a side effect, but doesn't stop it refilling on the next boot.
4. Hunger/needs (`NeedManager`/`NeedState`) - still fully dead scaffold, untouched all session.
5. Ladder_4's final approach (from the very start of this session, Part 1) - still a known, deliberately-parked gap.
6. Autonomous need-based task triggering - `/lr.debug.settask` is still manual-only by design; wiring it to an actual need/inventory threshold is future work once Phase A/B are solid.
