using LivingRust.Core;
using UnityEngine;

namespace Carbon.Plugins;

[Info("Project", "LivingRust", "0.1.0")]
[Description("Persistent AI survivors for Rust.")]

public partial class LivingRust : CarbonPlugin
{
    private LivingRustEngine? _engine;

    // Console-spam fix (2026-09-21): Rust sends note.inv / note.craft_* to
    // every player on inventory/craft events; for our client-less survivors
    // the server tried to run them and logged "Command 'x' not found"
    // (~6,300 lines in one session). Registering them as no-ops swallows
    // the message without changing any behaviour.
    [ConsoleCommand("note.inv")]
    private void CmdNoteInv(ConsoleSystem.Arg arg) { }

    [ConsoleCommand("note.craft_start")]
    private void CmdNoteCraftStart(ConsoleSystem.Arg arg) { }

    [ConsoleCommand("note.craft_done")]
    private void CmdNoteCraftDone(ConsoleSystem.Arg arg) { }

    [ConsoleCommand("note.craft_add")]
    private void CmdNoteCraftAdd(ConsoleSystem.Arg arg) { }

    /// <summary>
    /// When true, the routine per-bot chatter (loot summaries, equip/
    /// organize confirmations, movement narration) logs via VerbosePuts.
    /// Defaulted to OFF earlier the same session (2026-08-15) to avoid
    /// drowning the console with 100-200 bots all narrating routine steps -
    /// flipped back to ON (Lucas's own later explicit request, same day)
    /// once the active work became live navmesh/movement debugging, where
    /// this narration is the main diagnostic signal and worth the noise.
    /// WARNING-level lines and one-time lifecycle events (spawn/despawn/
    /// crash) always log regardless of this setting.
    ///
    /// Real fix (2026-09-19, live report: rubber-banding "really bad,"
    /// asking for anything short of a full restart) - defaults back to OFF.
    /// Live measurement during this exact complaint, at a ~340-survivor
    /// population: 150-330 Puts() lines/sec sustained, almost entirely
    /// walk-diag/phase-diag/walk-progress-diag per-tick narration, each one
    /// a synchronous string format + file write on the main thread. That's
    /// a real, continuous, unconditional cost that scales directly with
    /// population - unlike a genuine restart-only leak (thread/memory
    /// growth was measured essentially flat over the same window), so
    /// turning this off is an actual fix, not a workaround. Toggle with
    /// /lr.debug.verbose on for a future focused debugging session where
    /// the narration is worth the volume again - it no longer silently
    /// reverts on every hot-reload deploy this way either, which is very
    /// likely why an earlier same-session "try turning it off" test never
    /// got a real chance: every subsequent code deploy reset this field
    /// straight back to whatever its hardcoded default was.
    /// </summary>
    private bool _verboseLootLogging = false;

    private void VerbosePuts(string message)
    {
        if (_verboseLootLogging)
        {
            Puts(message);
        }
    }

    private void Init()
    {
        Puts("================================");
        Puts("LivingRust v0.1.0");
        Puts("Initializing...");
        Puts("================================");

        _engine = new LivingRustEngine();
    }

    private void OnServerInitialized()
    {
        Puts("Server initialized.");
        Puts("Starting LivingRust Engine...");

        _engine?.Start();

        ValidateMeleeToolPriority();
        ValidateWeaponPriority();
        ValidateNeverLootShortnames();
        ValidateCombatFireProfiles();
        ValidateGatherToolPriorityLists();

        // Brings back every survivor that was actually alive/spawned the
        // last time state was captured - see RestoreSpawnedSurvivors's doc
        // comment (LivingRust.Persistence.cs) for the still-exists-vs-real-
        // restart distinction.
        RestoreSpawnedSurvivors();

        // On-sight combat detection - see OnSightDetectionRange's own doc
        // comment (LivingRust.Combat.cs).
        StartOnSightDetection();

        // Top-level "genuinely not going anywhere" safety net - see
        // LifeStallTimeoutSeconds's own doc comment (LivingRust.Commands.cs).
        StartLifeStallWatchdog();
        StartBaseReturnScheduler();

        // Real crafting queue driver - see StartCraftQueueDriver's own doc
        // comment (LivingRust.Crafting.cs) for why a disconnected
        // survivor's own ItemCrafter needs this manual push at all.
        StartCraftQueueDriver();

        // Monument loot zones (auto-detected, persisted across restarts) -
        // see LivingRust.MonumentLootZones.cs's own doc comment for why
        // these load fresh every boot rather than needing to be re-scanned:
        // stored in each monument's own local space, re-resolved to
        // wherever that monument type actually spawned on THIS map/wipe.
        LoadMonumentLootZones();

        // Full-map monument scan, once per boot (2026-08-15) - Lucas's own
        // explicit framing: "when the bots spawn in, they should do a full
        // map scan of all the monuments that exist for that specific wipe
        // - that way it replicates what a player does... there is no
        // advantage/disadvantage to knowing what monuments exist on the
        // map at all." Previously zones only ever got discovered lazily,
        // the first time some bot's own loot search happened to run dry
        // near that specific monument - this instead eagerly covers every
        // monument type present on THIS map right away, so the very first
        // survivor that ever wanders near any of them already benefits.
        ScanAllMonumentsOnBoot();

        // Monument cover points (2026-08-24) - same "load persisted cache,
        // then scan whatever's still missing" shape as the loot-zone pair
        // right above, see LivingRust.MonumentCoverPoints.cs's own top-of-
        // file doc comment for why this exists as a separate pre-scanned
        // cache rather than folding into the live per-fight cover search.
        LoadMonumentCoverPoints();
        ScanAllMonumentsForCoverPointsOnBoot();

        // Pre-confirmed avoid zone, Nuclear Missile Silo's first tunnel
        // stretch (2026-08-22) - normally a zone only becomes CONFIRMED
        // (see MonumentAvoidZones.cs's own AvoidZoneConfirmThreshold) after
        // two separate organic stuck reports there, but this exact spot
        // already has overwhelming manual evidence from two different
        // sessions: the ghost-route trace itself needed re-recording/
        // splicing through here (2026-08-21) because the ORIGINAL recorded
        // path hit blocked geometry at this same coordinate, and a live
        // combat death (2026-08-22, 'HazyRaider6') confirmed
        // TryGetNextStep reports "body would overlap solid geometry
        // ('Terrain')" here too - the real underlying map terrain mesh
        // intrudes into this tunnel stretch, not a movable prop. Seeding
        // it confirmed immediately (two calls, matching
        // AvoidZoneConfirmThreshold) means combat's own no-LOS pursuit
        // (see the new IsInMonumentAvoidZone check in Combat.cs) treats it
        // as known-bad from server start, instead of only after a bot
        // dies there again organically.
        Vector3 nuclearMissileSiloTunnelBadPocket = new Vector3(-706.75f, 10.65f, -1312.42f);
        RecordPotentialAvoidZone(nuclearMissileSiloTunnelBadPocket);
        RecordPotentialAvoidZone(nuclearMissileSiloTunnelBadPocket);

        Puts("LivingRust Engine started successfully.");
    }

    /// <summary>
    /// Rust's own native world-save runs on its own schedule (auto-save
    /// timer, plus on shutdown) completely independent of this plugin's
    /// lifecycle - it captures every live BasePlayer entity (bots
    /// included) regardless of whether Carbon ever gets a chance to call
    /// Unload first. Relying on Unload alone to persist LivingRust's own
    /// Character roster meant any restart that skipped a graceful Unload
    /// (process killed/restarted directly rather than told to shut down)
    /// left world.json stuck at whatever it was after the last clean
    /// Unload, even though the actual game entities kept moving on
    /// without it - characters spawned or killed in that gap became
    /// invisible to RestoreSpawnedSurvivors on the next boot, since it can
    /// only work from what's in the roster. Saving here too keeps the two
    /// independent persistence systems from drifting apart.
    /// </summary>
    private void OnServerSave()
    {
        CaptureAllLiveState();

        _engine?.SaveManager.SaveCharacters(_engine.CharacterManager.GetAllCharacters());
    }

    private void Unload()
    {
        Puts("Stopping LivingRust Engine...");

        StopAllTraces();
        StopTestLogCapture();
        StopOnSightDetection();
        StopLifeStallWatchdog();
        StopBaseReturnScheduler();
        StopCraftQueueDriver();

        // Captures every spawned survivor's live position/health/inventory
        // into its persistent Character record before SaveManager writes it
        // to disk (inside _engine.Stop()) - see CaptureAllLiveState's doc
        // comment (LivingRust.Persistence.cs).
        //
        // This replaces the old unconditional DespawnAllBots() wipe on
        // every reload/unload (kept as a manual "clean slate" op, still
        // callable via /lr.debug.despawnall) - now that live state is
        // actually captured and restored, killing every bot here would
        // defeat the entire point: a bot that owns a base or sleeping bag
        // would vanish and leave it ownerless just because the plugin
        // reloaded or the server restarted, which is exactly the outcome
        // this feature exists to prevent.
        CaptureAllLiveState();

        _engine?.Stop();

        Puts("LivingRust unloaded.");
    }
}