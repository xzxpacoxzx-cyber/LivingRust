using System.Collections.Generic;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

public partial class LivingRust
{
    // Spread window for RestoreSpawnedSurvivors' post-reload task-pipeline
    // resume (see its own doc comment on the relinked-survivor branch) -
    // large enough that a full ~300+ population doesn't all hit
    // StartLootForResourcesTask's own container/destination scans in the
    // same frame, small enough that nothing sits idle noticeably longer
    // than it would take the life-stall watchdog to notice anyway.
    private const float RestoreResumeStaggerWindowSeconds = 15f;

    /// <summary>
    /// Captures every currently-spawned survivor's live position, rotation,
    /// health, and full inventory into its persistent Character record, and
    /// corrects Spawned to match reality. Called from Unload right before
    /// SaveManager writes to disk, so what gets persisted is each
    /// survivor's true current state rather than whatever it was the last
    /// time something else happened to update it.
    ///
    /// This is what replaces the old DespawnAllBots-on-every-reload
    /// behaviour: instead of killing every bot so there's nothing stale to
    /// worry about, their real state gets saved so RestoreSpawnedSurvivors
    /// can bring them back afterward - a bot that owns a base or a
    /// sleeping bag no longer needs to vanish (and leave that base
    /// ownerless) just because the plugin reloaded or the server restarted.
    /// </summary>
    private void CaptureAllLiveState()
    {
        if (_engine == null)
        {
            return;
        }

        int captured = 0;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            BasePlayer npc = survivor.Player;
            Character character = survivor.Character;

            // Home-site/base-gather progress (2026-09-15) - captured
            // regardless of whether npc is still alive below, unlike every
            // other field in this loop. These three in-memory-only
            // dictionaries (LivingRust.HomeSiteStrategy.cs) would otherwise
            // be silently wiped by this very reload for every survivor,
            // spawned or not - see HasRolledHomeSiteStrategy's own doc
            // comment (Models/Character.cs) for the live bug this fixes.
            character.HasRolledHomeSiteStrategy = _hasRolledHomeSiteStrategy.Contains(character.Id);
            character.PursuingBaseGatherGoal = _pursuingBaseGatherGoal.Contains(character.Id);

            if (_rolledBaseDesign.TryGetValue(character.Id, out (string Tier, string DesignPath, Dictionary<string, int> Cost) rolled))
            {
                character.RolledBaseTier = rolled.Tier;
                character.RolledBaseDesignPath = rolled.DesignPath;
                character.RolledBaseCost = rolled.Cost;
            }

            if (npc == null || npc.IsDestroyed)
            {
                character.Spawned = false;
                continue;
            }

            character.Position = npc.transform.position;
            character.Rotation = npc.transform.rotation;
            character.Health = npc.health;
            character.Inventory = CaptureInventory(npc.inventory);
            character.Spawned = true;

            captured++;
        }

        Puts($"Captured live state for {captured} spawned survivor(s).");
    }

    private List<SavedItem> CaptureInventory(PlayerInventory inventory)
    {
        var saved = new List<SavedItem>();

        CaptureContainer(inventory.containerMain, InventorySlot.Main, saved);
        CaptureContainer(inventory.containerBelt, InventorySlot.Belt, saved);
        CaptureContainer(inventory.containerWear, InventorySlot.Wear, saved);

        return saved;
    }

    private void CaptureContainer(ItemContainer container, InventorySlot slot, List<SavedItem> into)
    {
        if (container == null)
        {
            return;
        }

        foreach (Item item in container.itemList)
        {
            into.Add(new SavedItem
            {
                ItemId = item.info.itemid,
                Amount = item.amount,
                Condition = item.condition,
                SkinId = item.skin,
                Position = item.position,
                Container = slot,
            });
        }
    }

    /// <summary>
    /// Brings back every survivor with a live BasePlayer still in the
    /// world, plus every survivor that was spawned the last time state was
    /// captured - called right after _engine.Start() loads the persistent
    /// roster. Three cases:
    ///
    /// - A live BasePlayer already exists for this character's userID - a
    ///   plugin hot-reload never touches already-spawned entities on its
    ///   own (only Unload's old DespawnAllBots call destroyed them, which
    ///   this feature removes), and a real restart's native world-load
    ///   recreates every saved BasePlayer too (ours included) - so most of
    ///   the time this is just re-linking Survivor.Player back to an
    ///   entity that was never actually gone, correcting Spawned/State to
    ///   match reality if either was stale.
    /// - No live BasePlayer, but Character.Spawned says it should have one
    ///   - a genuinely fresh restart with no native reload (e.g. no prior
    ///   Rust save existed yet) - spawns a fresh one at the saved position/
    ///   rotation, with the saved health and inventory restored instead of
    ///   the normal starting kit.
    /// - No live BasePlayer and Character.Spawned is false - this
    ///   character was never actually in the world (or was deliberately
    ///   left dead), so it's skipped entirely rather than resurrected.
    /// </summary>
    private void RestoreSpawnedSurvivors()
    {
        if (_engine == null)
        {
            return;
        }

        int relinked = 0;
        int respawned = 0;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            Character character = survivor.Character;

            // Re-seed home-site/base-gather progress (2026-09-15) - see
            // CaptureAllLiveState's own matching capture block and
            // HasRolledHomeSiteStrategy's own doc comment (Models/
            // Character.cs). Unconditional, before the live-entity
            // relink/respawn branching below - this needs to happen
            // regardless of whether the survivor turns out to still be
            // spawned, dead, or freshly respawned this restore pass.
            if (character.HasRolledHomeSiteStrategy)
            {
                _hasRolledHomeSiteStrategy.Add(character.Id);
            }

            if (character.PursuingBaseGatherGoal)
            {
                _pursuingBaseGatherGoal.Add(character.Id);
            }

            if (!string.IsNullOrEmpty(character.RolledBaseTier))
            {
                _rolledBaseDesign[character.Id] = (character.RolledBaseTier, character.RolledBaseDesignPath, character.RolledBaseCost ?? new Dictionary<string, int>());
            }

            // Checked before trusting the persisted Spawned flag, not
            // instead of it - Spawned can go stale in exactly the same way
            // State can (see the State fixup below): a character captured
            // mid-death (old BasePlayer already destroyed, RespawnSurvivor's
            // 5-second timer not yet fired) gets Spawned=false correctly at
            // that instant, but nothing ever revisits it afterward - every
            // later restart would skip it forever, even once a real,
            // sleeping, natively-reloaded entity for it exists in the world.
            // A live entity is authoritative over whatever got persisted.
            BasePlayer existing = FindExistingBotEntity(character.BotId);

            if (existing == null && !character.Spawned)
            {
                // Real fix (2026-09-21, live log audit: 18 bots sat Dead for
                // the entire uptime after a restart). A death's own
                // RespawnSurvivor is a 5s timer.Once, which never survives an
                // Unload/restart - a bot killed just before one stayed Dead
                // forever, and this used to skip it as "deliberately left
                // dead." Nothing else ever revives a Dead roster entry.
                if (character.State == CharacterState.Dead)
                {
                    float respawnDelay = UnityEngine.Random.Range(1f, RestoreResumeStaggerWindowSeconds);
                    timer.Once(respawnDelay, () =>
                    {
                        if (survivor.Player == null || survivor.Player.IsDestroyed)
                        {
                            RespawnSurvivor(survivor);
                        }
                    });
                }

                continue;
            }

            if (existing != null)
            {
                // A live, non-destroyed entity is direct proof this
                // character is alive right now, regardless of whatever
                // State got persisted - a character whose last death's
                // RespawnSurvivor timer got cut short by this very
                // reload/restart (timer.Once never survives an Unload)
                // would otherwise stay stuck at Dead forever, silently
                // disabling OnPlayerDeath for it on every future kill.
                character.State = CharacterState.Alive;

                // A real restart's native world-load runs BasePlayer.Load()
                // fromDisk for every saved player entity (ours included),
                // which unconditionally calls StartSleeping() - the same
                // thing that happens to a real player who was offline when
                // the server saved. A real player wakes back up via their
                // client's join/reconnect handshake; our bots have no such
                // handshake, so nothing would ever call the matching
                // EndSleeping() for them without this - they'd stay asleep
                // (inert ragdoll, not ticking) forever after a restart.
                if (existing.IsSleeping())
                {
                    existing.EndSleeping();

                    // EndSleeping() only flips server-side state - the
                    // client's sleeping-ragdoll visual comes from
                    // modelState.sleeping, which normally gets refreshed by
                    // CheckModelState() every tick for an actively-connected
                    // player. Our bots never run that per-tick path, so
                    // without an explicit push here an already-nearby client
                    // would keep rendering the stale asleep pose - same
                    // "no live refresh path" issue SpawnSurvivor already
                    // works around for displayName.
                    existing.SendNetworkUpdateImmediate();
                }

                survivor.Player = existing;
                survivor.Position = existing.transform.position;
                survivor.Spawned = true;
                character.Spawned = true;
                relinked++;

                // Real fix (2026-09-19) - root cause of '7672CrustyStalker'
                // sitting frozen inside its own base for over an hour after
                // a routine hot-reload, and very likely the primary driver
                // behind this whole session's chronic "bot stuck, watchdog
                // eventually rescues it" churn in general (see
                // RunLifeStallWatchdog's own fairness-fix doc comment,
                // LivingRust.Commands.cs). Re-linking Survivor.Player above
                // is NOT the same as resuming whatever this survivor was
                // doing - the entire task pipeline is pure callback chains
                // (StartWalking's onArrived leading into the next step, no
                // separate ticking loop anywhere - see
                // StartLootForResourcesTask's own doc comment), and every
                // C# timer/closure those chains depend on is unconditionally
                // destroyed by Unload, hot-reload included, regardless of
                // how intact the live entity itself still is. A "still-
                // existing" survivor was therefore always left exactly
                // where its capture-time callback happened to be paused,
                // with genuinely no way to resume on its own - only the
                // life-stall watchdog's own LifeStallTimeoutSeconds +
                // relocate-then-StartLootForResourcesTask rescue could ever
                // get it moving again, and with potentially hundreds of
                // survivors all going stale in the exact same instant on
                // every single reload, that 5-per-tick rescue budget could
                // take minutes to even reach a given survivor - exactly
                // what happened here. Explicitly re-entering the task
                // pipeline right here - staggered across
                // RestoreResumeStaggerWindowSeconds, not all 300+ at once in
                // the same frame, to avoid exactly the kind of synchronized
                // hitch this whole session's rubber-banding investigation
                // already traced to unbatched full-population scans - means
                // a relinked survivor resumes on its own within seconds of
                // a reload, the same way a freshly-respawned one already
                // does via SpawnSurvivor, instead of depending on the
                // watchdog as its only path back to life.
                if (character.State == CharacterState.Alive)
                {
                    float restartDelay = UnityEngine.Random.Range(0f, RestoreResumeStaggerWindowSeconds);

                    timer.Once(restartDelay, () =>
                    {
                        if (survivor.Player != null && !survivor.Player.IsDestroyed && survivor.Character.State == CharacterState.Alive)
                        {
                            StartLootForResourcesTask(survivor);
                        }
                    });
                }

                continue;
            }

            BasePlayer npc = SpawnSurvivor(survivor, character.Position, character.Rotation, character.Health, character.Inventory);

            if (npc != null)
            {
                respawned++;
            }
        }

        Puts($"Restored {relinked} still-existing and {respawned} freshly respawned survivor(s) from the persistent roster.");
    }

    /// <summary>
    /// Looks for a live BasePlayer already in the world with this specific
    /// character's userID - not a radius search (unlike FindSurvivorByPlayer,
    /// this runs at startup before any Survivor is linked to a Player yet,
    /// so there's no position to search near).
    ///
    /// ROOT CAUSE of the long-standing "stale-reference orphan" mystery,
    /// confirmed live 2026-08-13: the expanded despawnall diagnostic (added
    /// a prior session specifically to test this) showed every single
    /// "orphan" this run was a genuine vanilla Rust NPC - real
    /// scientistnpc_roam/patrol/junkpile_pistol variants, even an
    /// npc_underwaterdweller - all IsNpc:True, not a duplicate of one of
    /// our own bots at all. Real mechanism: vanilla Rust's own native-NPC
    /// userID counter and this plugin's independent BotId counter (both
    /// starting in/around the same numeric neighborhood, confirmed via
    /// CharacterManager's own doc comment on the 5,000,000 starting value)
    /// can drift into collision over repeated server sessions - Rust's own
    /// ReserveBotIds() on world load reserves whatever high IDs our
    /// PERSISTED bots already have, which can push vanilla's own counter up
    /// past our starting point, after which freshly-spawned vanilla NPCs
    /// (monument scientists respawning, junkpile guards, etc.) can end up
    /// issued a userID that numerically collides with one of ours. The old
    /// plain `userID == botId` match had no way to tell a coincidentally-
    /// numbered vanilla NPC from a genuine duplicate of our own bot -
    /// !player.IsNpc closes that gap directly (a real precedent already
    /// existed for this exact class of mistake: an earlier, now-removed
    /// broader sweep pass once accidentally killed real bandit camp
    /// guards for the identical reason). This also protects
    /// RestoreSpawnedSurvivors (this method's OTHER caller, at plugin
    /// load) from ever mistakenly linking a Character to a live vanilla
    /// NPC's entity instead of its own real body - a much bigger footgun
    /// than despawnall's diagnostic-only false positive.
    /// </summary>
    private BasePlayer FindExistingBotEntity(ulong botId)
    {
        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is BasePlayer player && player.userID == botId && !player.IsDestroyed && !player.IsNpc)
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>
    /// Recreates each saved item and puts it back in the same container/
    /// slot it was captured from - falling back to GiveItem's normal
    /// any-free-slot placement if the exact original slot isn't available
    /// (e.g. a wear-slot compatibility mismatch), and dropping the item
    /// entirely only if neither works.
    /// </summary>
    private void RestoreInventory(BasePlayer npc, List<SavedItem> items)
    {
        foreach (SavedItem saved in items)
        {
            Item item = ItemManager.CreateByItemID(saved.ItemId, saved.Amount, saved.SkinId);

            if (item == null)
            {
                Puts($"WARNING: couldn't restore item ID {saved.ItemId} (x{saved.Amount}) for '{npc.displayName}' - unknown item ID?");
                continue;
            }

            item.condition = saved.Condition;

            ItemContainer targetContainer = saved.Container switch
            {
                InventorySlot.Belt => npc.inventory.containerBelt,
                InventorySlot.Wear => npc.inventory.containerWear,
                _ => npc.inventory.containerMain,
            };

            if (!item.MoveToContainer(targetContainer, saved.Position) && !npc.inventory.GiveItem(item))
            {
                Puts($"WARNING: couldn't restore item ID {saved.ItemId} (x{saved.Amount}) for '{npc.displayName}' - no room in inventory?");
                item.Remove();
            }
        }
    }
}
