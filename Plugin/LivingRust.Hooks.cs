using System;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using Rust;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Rough real-Rust death-screen pause before respawning - not a
    // verified constant, just a reasonable feel to match.
    private const float RespawnDelaySeconds = 5f;

    /// <summary>
    /// Survivors skip Rust's real wounded/crawl state entirely - the moment
    /// one would be wounded, this forces it straight to a real death
    /// instead. Explicit design decision (Lucas, 2026-08-11): a real player
    /// who downs a bot needs a guaranteed real, lootable corpse - "the
    /// inventory loot/items the bot has ... is the reward" - so leaving a
    /// bot in a genuine wounded/bleedout state isn't acceptable even if it
    /// could be made to work correctly, since Rust's own wounded pipeline
    /// (WoundingTick, an Invoke()-driven self-rescheduling timer that only
    /// finally calls the real Die() once a wound-duration timer expires AND
    /// a recovery-chance roll fails) was found via decompile to plausibly
    /// never resolve correctly for a disconnected fake BasePlayer -
    /// confirmed live: Lucas emptied ~90 rifle bullets into a downed bot
    /// with zero effect, and it eventually vanished with no corpse at all
    /// once wounded. Real per-hit damage/death itself was confirmed NOT
    /// connection-gated (BasePlayer.Hurt/Die never check IsConnected) - the
    /// wounding STATE MACHINE is the actual problem, not raw damage
    /// application, so removing that state machine from the equation
    /// entirely for our own bots is the fix, rather than trying to make a
    /// fragile Invoke()-based timer chain work reliably for an entity that
    /// never went through a real client-connection lifecycle.
    ///
    /// OnPlayerWound (real Carbon hook, confirmed via decompiling
    /// Carbon.Hooks.Oxide.dll - a transpiler patch injected into
    /// BasePlayer.BecomeWounded right after its own "already wounded?"
    /// guard) fires synchronously, deep inside the original Die() call's
    /// own stack, before the Wounded flag is actually set on the player.
    /// Calling player.Die() again SYNCHRONOUSLY from here would re-run
    /// EligibleForWounding while IsWounded() still reads false, wounding
    /// AGAIN instead of dying - straight into unbounded recursion. Deferring
    /// via timer.Once(0f, ...) is the standard safe pattern: by the time it
    /// fires (next tick), BecomeWounded has already set the Wounded flag,
    /// so EligibleForWounding correctly refuses to wound an already-wounded
    /// player and this second Die() call goes straight to a real death -
    /// same as a real player being finished off while already downed does
    /// in vanilla Rust. That real Die() then flows into the existing
    /// OnPlayerDeath hook below completely unchanged (real corpse via
    /// Rust's own CreateCorpse/DropCorpse, real "died"/respawn handling) -
    /// this only ever shortens the wounded window to effectively zero, it
    /// doesn't touch anything downstream of it.
    /// </summary>
    private void OnPlayerWound(BasePlayer player, HitInfo info)
    {
        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null)
        {
            return;
        }

        // Real fix (2026-09-07, live report: 90%+ of all deaths logged as
        // "UNRECORDED damage/no Initiator" despite tracing several of them
        // back to a real, attributable combat kill seconds earlier - see
        // OnPlayerDeath's own UNRECORDED-damage doc comment). Root cause:
        // HitInfo is one of Rust's own pooled objects (confirmed via
        // decompile - this project's own SwingGatherTool/StartHarvestingCorpse
        // already Pool.Get/Pool.Free one every real swing). The
        // timer.Once(0f, ...) below is a NEXT-TICK deferred call (needed so
        // EligibleForWounding sees the real Wounded flag - see this
        // function's own doc comment above), but by the time it actually
        // runs, the ORIGINAL synchronous call stack that invoked
        // OnPlayerWound has almost certainly already returned this same
        // info object to the pool for reuse elsewhere, clearing its fields
        // - so the deferred Die(info) call was handed a stale, recycled
        // object, not the real hit data, explaining exactly the observed
        // symptom (empty damageTypes, null Initiator) on an otherwise
        // completely real, attributable kill. Capturing the real
        // Initiator/position/damage data SYNCHRONOUSLY here (before
        // returning control to Rust's own pipeline, and before anything
        // has a chance to free info) and building a genuinely independent
        // HitInfo - a plain `new HitInfo()`, deliberately NOT pooled, so
        // there's no lifecycle to fight - for the deferred Die() call is
        // what actually survives to OnPlayerDeath.
        BaseEntity initiator = info?.Initiator;
        Vector3 hitPosition = info?.HitPositionWorld ?? player.transform.position;
        DamageTypeList clonedDamageTypes = info?.damageTypes?.Clone();

        timer.Once(0f, () =>
        {
            if (player != null && !player.IsDestroyed)
            {
                HitInfo freshInfo = new HitInfo
                {
                    Initiator = initiator,
                    HitPositionWorld = hitPosition,
                };

                if (clonedDamageTypes != null)
                {
                    freshInfo.damageTypes = clonedDamageTypes;
                }

                player.Die(freshInfo);
            }
        });
    }

    /// <summary>
    /// Reacts to a survivor dying. Rust's own combat pipeline already
    /// handles the actual damage/downed/bleedout mechanics for these bots
    /// unmodified - they're real BasePlayer entities (InitializeHealth in
    /// SpawnSurvivor sets up a genuine health pool), confirmed via live
    /// testing showing normal crawl/downed behaviour on limb/body hits and
    /// instant death from some weapons, same as a real player. Item drop-
    /// on-death is likewise Rust's own native behaviour, not something
    /// this needs to implement.
    ///
    /// What Rust's pipeline knows nothing about is LivingRust's own
    /// bookkeeping: stopping any in-progress movement (a timer left running
    /// against a corpse would keep calling ApplyMovementStep/AdvanceClimb
    /// on a destroyed or ragdolled entity every tick), and bringing the
    /// same persistent Character back via RespawnSurvivor rather than
    /// leaving it dead forever or replacing it with a new identity.
    ///
    /// FindSurvivorByPlayer returning null - a real connected player, a
    /// vanilla NPC, anything not wrapped in a Survivor - is the normal,
    /// expected case for most deaths on the server and is not logged.
    /// </summary>
    private void OnPlayerDeath(BasePlayer player, HitInfo info)
    {
        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null || survivor.Character.State == CharacterState.Dead)
        {
            return;
        }

        CancelActiveMovement(survivor);
        CancelActiveAttack(survivor.Character.Id);
        CancelActiveCombat(survivor.Character.Id);
        CancelActiveFlee(survivor.Character.Id);
        CancelActiveRecycling(survivor.Character.Id);
        _pendingRecyclerToResume.Remove(survivor.Character.Id);
        ReleaseMonumentOccupancy(survivor.Character.Id);
        OnAirdropRunnerLost(survivor.Character.Id);
        _buildSiteReservations.Remove(survivor.Character.Id);
        _pendingGhostRouteToResume.Remove(survivor.Character.Id);
        _ghostRouteDetourAttempts.Remove(survivor.Character.Id);
        _pendingGhostRouteLosRecheck.Remove(survivor.Character.Id);
        StopGhostRouteLootScan(survivor.Character.Id);
        _reloadExposureWindowUntil.Remove(survivor.Character.Id);
        _combatRetreatUntilTime.Remove(survivor.Character.Id);
        _lastCombatEndTime.Remove(survivor.Character.Id);
        _lastSelfHealTime.Remove(survivor.Character.Id);
        _healingUntilTime.Remove(survivor.Character.Id);
        ClearCombatDecisionTracking(survivor.Character.Id);
        ClearTacticalDecisionState(survivor.Character.Id);
        StopHuntSearch(survivor.Character.Id);
        _activeCardPuzzleSurvivors.Remove(survivor.Character.Id);
        _handledReadersThisPuzzle.Remove(survivor.Character.Id);
        _recentOwnKillPositions.Remove(survivor.Character.Id);
        StopExtendedOreSearch(survivor.Character.Id);
        _totalStoneGathered.Remove(survivor.Character.Id);
        _totalMetalOreGathered.Remove(survivor.Character.Id);
        _totalSulfurOreGathered.Remove(survivor.Character.Id);
        _totalWoodGathered.Remove(survivor.Character.Id);
        _resourceGatherTypeLock.Remove(survivor.Character.Id);
        _hasPlacedSleepingBag.Remove(survivor.Character.Id);

        if (_pendingSleepingBagDeployTimers.TryGetValue(survivor.Character.Id, out Timer pendingBagDeployTimer))
        {
            pendingBagDeployTimer.Destroy();
            _pendingSleepingBagDeployTimers.Remove(survivor.Character.Id);
        }

        if (_pendingArrowCraftTimers.TryGetValue(survivor.Character.Id, out Timer pendingArrowCraftTimer))
        {
            pendingArrowCraftTimer.Destroy();
            _pendingArrowCraftTimers.Remove(survivor.Character.Id);
        }

        if (_pendingBowCraftTimers.TryGetValue(survivor.Character.Id, out Timer pendingBowCraftTimer))
        {
            pendingBowCraftTimer.Destroy();
            _pendingBowCraftTimers.Remove(survivor.Character.Id);
        }

        if (_pendingPostCombatResumeTimers.TryGetValue(survivor.Character.Id, out Timer pendingPostCombatResumeTimer))
        {
            pendingPostCombatResumeTimer.Destroy();
            _pendingPostCombatResumeTimers.Remove(survivor.Character.Id);
        }

        _hasRolledPrimitiveGoal.Remove(survivor.Character.Id);

        // Real "don't strand a base-less bot on the shoreline forever"
        // cleanup (2026-09-01, Lucas's own explicit call after asking
        // whether respawned bots still roll inland: "depends if the bot
        // has a base or not... if it does, it shouldn't 'start from
        // scratch' every time it dies otherwise there will be no bots
        // progressing at all"). Deliberately conditional on Home == null -
        // none of this per-life bookkeeping (home-site roll, base-gather
        // goal, tier-upgrade design roll, coastal-stall snapshot,
        // checklist retry timer, ingredient-search cooldown) is ever
        // consulted again once a survivor has a real base (ShouldSkipPrimitiveChecklist/
        // TryPursueTierUpgrade both branch on Character.Home directly, not
        // on any of these flags), so a based survivor's progress is
        // completely untouched by this block either way - this only
        // matters for the survivor that died BEFORE ever finishing its
        // home-site walk or establishing a base, who would otherwise carry
        // "already rolled" forward via Character.Id (which survives
        // respawn) and permanently skip ever re-rolling inland again,
        // stuck wherever it happened to spawn near the shore for the rest
        // of the session.
        if (survivor.Character.Home == null)
        {
            _hasRolledHomeSiteStrategy.Remove(survivor.Character.Id);
            _homeSiteTarget.Remove(survivor.Character.Id);

            // _pursuingBaseGatherGoal/_rolledBaseDesign deliberately NOT
            // cleared here (2026-09-15, Lucas's own explicit ask: "have it
            // hold that base design it rolled for through deaths") -
            // previously wiped identically to every other per-life flag in
            // this block, which meant a survivor that had already
            // committed to gathering toward a real design (e.g. 'tier1/
            // base3') lost that commitment on every death and re-rolled a
            // BRAND NEW random design on respawn, live evidence 2026-09-15:
            // 'AngrySkinner61' rolled tier1/base3, then minutes later
            // tier0/base7 - never actually working toward the same target
            // twice in a row. This is the exact same "don't start from
            // scratch" reasoning this whole block already applies to a
            // FINISHED base (Character.Home != null skips this block
            // entirely) - a survivor mid-gather toward a design deserves
            // the same consistency, not just one that's already built.
            // Real gathered resources still don't survive death (normal
            // Rust inventory-on-death, unrelated to this flag) - this only
            // keeps the CHOICE of which design to gather toward stable
            // across a fresh life, so every death isn't also a fresh coin
            // flip on the goal itself.
            _primitiveGoalRetryTime.Remove(survivor.Character.Id);
            _coastalProgressSnapshot.Remove(survivor.Character.Id);

            // Same base-less-only reasoning as the rest of this block - a
            // fresh life gets a fresh AnimalHuntQuota/AnimalHuntPhaseWindowSeconds
            // budget rather than carrying a spent one forward forever.
            _animalHuntKillCount.Remove(survivor.Character.Id);
            _animalHuntPhaseStartTime.Remove(survivor.Character.Id);
            _activeAnimalHunt.Remove(survivor.Character.Id);

            foreach ((Guid CharacterId, string Ingredient) key in _ingredientSearchCooldownUntil.Keys.Where(k => k.CharacterId == survivor.Character.Id).ToList())
            {
                _ingredientSearchCooldownUntil.Remove(key);
            }
        }

        // Real cleanup (2026-08-29) - a survivor that dies mid-crossing
        // would otherwise stay permanently flagged "busy" for
        // TryGetHomeDoorCrossing's own guard, silently disabling its own
        // door-crossing route for the rest of its next life.
        _activeHomeDoorCrossings.Remove(survivor.Character.Id);
        _pursuingPrimitiveGoals.Remove(survivor.Character.Id);

        foreach ((Guid CharacterId, string Shortname) key in _pendingOneOffCraftTimers.Keys.Where(k => k.CharacterId == survivor.Character.Id).ToList())
        {
            _pendingOneOffCraftTimers[key].Destroy();
            _pendingOneOffCraftTimers.Remove(key);
        }

        // A dead bot has nothing to resume - see _pendingResumeCombatTarget's
        // own doc comment (LivingRust.Combat.cs). Not cleared inside
        // CancelActiveCombat itself since StartCombat's animal-interrupt
        // retarget also calls that method WHILE deliberately setting this
        // same entry the line before - clearing it there would erase what
        // the interrupt just recorded.
        CancelPendingResumeCombatTarget(survivor.Character.Id);

        // Real diagnostic fix (2026-09-07, live report: 90-97% of all
        // deaths across two separate 200+ bot traces logged as "Cannon"
        // with ZERO position clustering anywhere on the map - one single
        // survivor died "Cannon" three times in 5 real minutes at
        // locations 1000+ units apart, which no physical Bradley could
        // ever do). Root-caused via ilspycmd decompile of the real
        // DamageTypeList.GetMajorityDamageType() this line calls:
        //
        //   int result = 0; float num = 0f;
        //   for (int i = 0; i < types.Length; i++) {
        //       float num2 = types[i];
        //       if (!IsNaN(num2) && !IsInfinity(num2) && !(num2 < num)) {
        //           result = i; num = num2;
        //       }
        //   }
        //   return (DamageType)result;
        //
        // When EVERY entry is exactly 0 (no real damage was ever actually
        // recorded against this death at all), "!(0 < 0)" is true on every
        // single iteration, so result keeps marching forward through the
        // whole array and lands on the LAST valid index - which happens to
        // be Cannon, the second-to-last DamageType enum entry (right
        // before the LAST sentinel). It's a mechanical artifact of an
        // all-zero list, completely unrelated to Bradley or any real
        // cannon hit. Checking Total() > 0f first (the same real check
        // OnEntityTakeDamage's own shot-spread-diag already uses) catches
        // this before ever calling the misleading method, and logs the
        // real Initiator entity instead - whatever's actually killing
        // these survivors with no recorded damage is still unknown, but
        // this is what will actually reveal it on the next batch.
        string cause;

        if (info?.damageTypes == null)
        {
            cause = "Unknown (no HitInfo)";
        }
        else if (info.damageTypes.Total() <= 0f)
        {
            string initiatorDesc = info.Initiator != null
                ? $"{info.Initiator.GetType().Name} ('{info.Initiator.ShortPrefabName}')"
                : "no Initiator entity";

            cause = $"UNRECORDED damage (real GetMajorityDamageType() would misreport this as 'Cannon' - see this line's own doc comment; Initiator: {initiatorDesc})";
        }
        else
        {
            cause = info.damageTypes.GetMajorityDamageType().ToString();
        }

        Puts($"'{survivor.Character.Alias}' died ({cause}) at {player.transform.position}.");

        survivor.Character.State = CharacterState.Dead;

        timer.Once(RespawnDelaySeconds, () => RespawnSurvivor(survivor));
    }

    /// <summary>
    /// Real "I know exactly where that body fell" memory (2026-08-25,
    /// Lucas's own explicit correction: a bot loot-searching for corpses
    /// at absurd distances - "it overrode the gather ore command to loot
    /// bodies about 30-40 metres away" - "should only be applicable to
    /// post combat, because the bot would know where the player died if
    /// it won the fight"). OnEntityDeath is a real, general Oxide/Carbon
    /// hook firing for ANY BaseCombatEntity's death (players, NPCs,
    /// animals) - checked here for whether the KILLER (info.InitiatorPlayer)
    /// was one of our own survivors, not the victim. See
    /// IsRememberedOwnKill's own doc comment (LivingRust.Looting.cs) for
    /// how this position gets used - a survivor's own genuine kill stays
    /// reachable for real corpse-looting well beyond the tightened ambient
    /// awareness radius, everything else doesn't.
    /// </summary>
    private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
    {
        if (entity == null || info?.InitiatorPlayer == null || _engine == null)
        {
            return;
        }

        Survivor killer = FindSurvivorByPlayer(info.InitiatorPlayer);

        if (killer == null)
        {
            return;
        }

        RememberOwnKill(killer.Character.Id, entity.transform.position);
        NotePotentialPriorityKill(killer, entity);
    }

    /// <summary>
    /// Diagnostic-only, added 2026-08-11 to chase the stale-reference
    /// orphan bug - a tracked survivor's BasePlayer sometimes ends up
    /// destroyed with NO "'X' died" line ever logged (confirmed live,
    /// FastAK5283: a completely normal life - spawn, loot, walk - then a
    /// despawnall diagnostic later found its own survivor.Player already
    /// destroyed, with zero death/respawn activity in between). Since
    /// OnPlayerDeath only ever fires from a real Die()/wounding-bypass
    /// completing, something destroying the entity through any OTHER path
    /// (a direct Kill() call somewhere, engine-level cleanup, anything not
    /// going through Die()) would be invisible to it entirely.
    /// OnEntityKill is Rust/Carbon's real, general "this entity is being
    /// destroyed" hook - fires for every destruction path, not just Die().
    /// Logging here, cross-referenced against whether CharacterState was
    /// already Dead at the moment of destruction, directly answers the
    /// open question: if State is NOT already Dead here, this destruction
    /// bypassed OnPlayerDeath entirely - the actual smoking gun this
    /// investigation needs. Read-only - doesn't touch RespawnSurvivor or
    /// any other state, purely observational.
    /// </summary>
    private void OnEntityKill(BaseNetworkable entity)
    {
        if (entity is SupplyDrop killedDrop)
        {
            OnAirdropEntityKilled(killedDrop);
            return;
        }

        if (entity is not BasePlayer player)
        {
            return;
        }

        Survivor survivor = FindSurvivorByPlayer(player);

        if (survivor == null)
        {
            return;
        }

        Puts($"diagnostic: '{survivor.Character.Alias}' (userID {survivor.Character.BotId}, instanceID {player.GetInstanceID()}) BasePlayer is being destroyed via OnEntityKill - CharacterState was already {survivor.Character.State} at this moment (Dead = normal Die() path already handled it; anything else = this destruction bypassed OnPlayerDeath entirely).");
    }

    /// <summary>
    /// Brings a dead survivor back - same persistent Character (same name
    /// and same userID, so Rust's own userID-keyed blueprint unlocks and
    /// building privilege authorization carry over untouched), but a brand
    /// new BasePlayer entity rather than reusing the one that died.
    ///
    /// Originally tried calling BasePlayer.Respawn() directly on the dead
    /// entity (Rust's own real respawn entry point, the same one a real
    /// player's in-game "Respawn" button drives) - a live test showed every
    /// single case failing with "its BasePlayer was already cleaned up" by
    /// the time this timer fired, confirming a disconnected/fake BasePlayer
    /// gets destroyed on death far faster than a real connected player's
    /// does (which sticks around waiting for them to choose where to
    /// respawn).
    ///
    /// Also originally tried ServerMgr.FindSpawnPoint(npc) alone, assuming
    /// it would check the given BasePlayer's userID for an owned sleeping
    /// bag the same way a real respawn does - a live test with a bag
    /// explicitly assigned via /lr.debug.claimbag still landed on a beach
    /// spawn both times (isProcedualSpawn: True), so that assumption about
    /// FindSpawnPoint's internal bag-matching wasn't actually confirmed by
    /// anything, just inferred from the parameter name. TryFindOwnedBag
    /// below checks bag ownership directly instead (SleepingBag.OwnerID/
    /// ValidForPlayer/GetSpawnPos are real, verified members - confirmed
    /// via reflection over Assembly-CSharp.dll), and FindSpawnPoint is now
    /// only relied on for the plain beach-spawn fallback, which doesn't
    /// depend on any per-player matching working correctly.
    /// </summary>
    private void RespawnSurvivor(Survivor survivor)
    {
        Character character = survivor.Character;

        BasePlayer npc = GameManager.server.CreateEntity(
            "assets/prefabs/player/player.prefab",
            Vector3.zero,
            Quaternion.identity) as BasePlayer;

        if (npc == null)
        {
            Puts($"'{character.Alias}' failed to respawn - couldn't create a new BasePlayer.");
            return;
        }

        // userID set before Spawn() (see SpawnSurvivor's doc comment on
        // displayName/userID timing - same rule applies here).
        npc.userID = character.BotId;
        npc.displayName = character.Alias;

        bool atOwnedBag = TryFindOwnedBag(character.BotId, out Vector3 spawnPos, out Quaternion spawnRot);

        if (!atOwnedBag)
        {
            BasePlayer.SpawnPoint spawnPoint = ServerMgr.FindSpawnPoint(npc);
            spawnPos = spawnPoint.pos;
            spawnRot = spawnPoint.rot;
        }

        npc.transform.position = spawnPos;
        npc.transform.rotation = spawnRot;

        npc.Spawn();
        npc.InitializeHealth(100f, 100f);
        npc.SendNetworkUpdateImmediate();

        GiveStartingKit(npc);

        survivor.Player = npc;
        survivor.Position = spawnPos;
        character.Position = spawnPos;
        character.State = CharacterState.Alive;
        character.Spawned = true;
        survivor.Spawned = true;

        Puts($"'{character.Alias}' respawned at {spawnPos} ({(atOwnedBag ? "owned sleeping bag" : "beach")}).");

        // Resumes autonomous behaviour after respawn (2026-08-15, live
        // report: a respawned survivor just stood there doing nothing at
        // all until manually re-triggered) - this was a real gap, not
        // deliberate: nothing anywhere else in the codebase called
        // StartLootForResourcesTask (or anything else) from here, so death
        // was a genuine dead end for a bot's own decision loop, even
        // though everything reactive (combat, on-sight, fleeing) still
        // worked fine on top of it. Goes through the exact same entry
        // point every other "resume looting" call site already uses
        // (EndCombat/EndFlee), which itself runs the gear-weighted
        // destination roll (LivingRust.GearScore.cs) fresh - a respawned
        // survivor starts with nothing (rock + torch only), so it'll
        // almost always roll Local/Tier1 the same way any other fresh
        // spawn does, exactly as intended.
        StartLootForResourcesTask(survivor);
    }

    /// <summary>
    /// Searches every SleepingBag entity on the map (there's no radius to
    /// search within - a bag could be anywhere) for one owned by botId and
    /// currently usable, in preference order: not on its post-use/deploy
    /// cooldown first, falling back to a bag ignoring that cooldown if
    /// that's genuinely the only one owned - matches how the "wake up"
    /// screen greys out a locked bag but still lets it be the last resort.
    /// </summary>
    private bool TryFindOwnedBag(ulong botId, out Vector3 position, out Quaternion rotation)
    {
        SleepingBag fallbackBag = null;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not SleepingBag bag || bag.OwnerID != botId || bag.IsDestroyed)
            {
                continue;
            }

            if (bag.ValidForPlayer(botId, false))
            {
                bag.GetSpawnPos(out position, out rotation);
                return true;
            }

            fallbackBag ??= bag;
        }

        if (fallbackBag != null)
        {
            fallbackBag.GetSpawnPos(out position, out rotation);
            return true;
        }

        position = default;
        rotation = default;
        return false;
    }

    /// <summary>
    /// Every fresh life starts with exactly what a brand new Rust character
    /// gets - a rock and a torch - regardless of what the survivor was
    /// carrying when it died (everything else drops with the corpse via
    /// Rust's own death handling, same as a real player).
    /// </summary>
    private void GiveStartingKit(BasePlayer npc)
    {
        GiveItem(npc, "rock", 1);
        GiveItem(npc, "torch", 1);
    }

    /// <summary>
    /// A bot doesn't actually need a light source to see anything - it's
    /// not rendering the world, so a torch is pure dead weight/clutter
    /// once real work starts. Lucas's own framing: still spawn with one
    /// and carry it around while just walking/idle (so it doesn't look
    /// wrong at a glance), but drop it for real - Item.Drop, a genuine
    /// world entity a real player could walk up and pick up - the instant
    /// a task actually begins, rather than deleting it outright. Called
    /// from every task-start entry point (currently just
    /// StartLootForResourcesTask - see [[project-livingrust-roadmap]]'s
    /// Current-task entry for why there's only one right now); naturally
    /// idempotent since it just no-ops if no torch is found, so calling it
    /// again on a later task start (a torch somehow re-looted since) is
    /// harmless.
    /// </summary>
    private void DropUnneededLightSource(Survivor survivor, BasePlayer npc)
    {
        Item torch = npc.inventory.FindItemByItemName("torch");

        if (torch == null)
        {
            return;
        }

        Vector3 dropPosition = npc.transform.position + Vector3.up * 1f + npc.eyes.BodyForward() * 0.5f;
        Vector3 dropVelocity = npc.eyes.BodyForward() * 0.5f + Vector3.up * 0.5f;

        torch.Drop(dropPosition, dropVelocity);

        Puts($"'{survivor.Character.Alias}' dropped its torch before starting work - doesn't need it to see.");
    }

    private void GiveItem(BasePlayer npc, string shortname, int amount)
    {
        Item item = ItemManager.CreateByName(shortname, amount);

        if (item == null)
        {
            Puts($"WARNING: couldn't create starting-kit item '{shortname}' - unknown shortname?");
            return;
        }

        if (!npc.inventory.GiveItem(item))
        {
            item.Remove();
        }
    }
}