using LivingRust.Core;
using UnityEngine;

namespace Carbon.Plugins;

[Info("Project", "LivingRust", "0.1.0")]
[Description("Persistent AI survivors for Rust.")]

public partial class LivingRust : CarbonPlugin
{
    private LivingRustEngine? _engine;

    // Registers these as no-ops to swallow the "Command not found" log spam Rust
    // sends to client-less survivors on inventory/craft events.
    [ConsoleCommand("note.inv")]
    private void CmdNoteInv(ConsoleSystem.Arg arg) { }

    [ConsoleCommand("note.craft_start")]
    private void CmdNoteCraftStart(ConsoleSystem.Arg arg) { }

    [ConsoleCommand("note.craft_done")]
    private void CmdNoteCraftDone(ConsoleSystem.Arg arg) { }

    [ConsoleCommand("note.craft_add")]
    private void CmdNoteCraftAdd(ConsoleSystem.Arg arg) { }

    /// <summary>
    /// When true, routine per-bot chatter (loot summaries, equip/organize
    /// confirmations, movement narration) logs via VerbosePuts. Defaults to off;
    /// warnings and lifecycle events always log regardless.
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

        DetectFreshWipe();

        ValidateMeleeToolPriority();
        ValidateWeaponPriority();
        ValidateNeverLootShortnames();
        ValidateCombatFireProfiles();
        ValidateGatherToolPriorityLists();

        // Restores every survivor that was alive/spawned when state was last captured.
        RestoreSpawnedSurvivors();

        // Starts on-sight combat detection.
        StartOnSightDetection();

        // Safety net that catches a survivor that is stuck and not moving.
        StartLifeStallWatchdog();
        StartBaseReturnScheduler();

        // Closes any base door left standing open by an interrupted enter/exit route.
        StartDoorSweeper();

        // Drives the crafting queue, since a disconnected survivor's own
        // ItemCrafter needs this manual push.
        StartCraftQueueDriver();

        // Loads auto-detected monument loot zones persisted from previous boots,
        // re-resolved to this map's monument positions.
        LoadMonumentLootZones();

        // Scans the whole map once per boot so every monument type is discovered
        // up front instead of only lazily on first use.
        ScanAllMonumentsOnBoot();

        // Loads persisted monument cover points, then scans for any still missing.
        LoadMonumentCoverPoints();
        ScanAllMonumentsForCoverPointsOnBoot();

        // Pre-seeds a known-bad avoid zone for the Nuclear Missile Silo's first
        // tunnel stretch, where the terrain mesh intrudes into the tunnel.
        Vector3 nuclearMissileSiloTunnelBadPocket = new Vector3(-706.75f, 10.65f, -1312.42f);
        RecordPotentialAvoidZone(nuclearMissileSiloTunnelBadPocket);
        RecordPotentialAvoidZone(nuclearMissileSiloTunnelBadPocket);

        Puts("LivingRust Engine started successfully.");
    }

    /// <summary>
    /// Saves the Character roster alongside Rust's own world-save so the two
    /// persistence systems stay in sync even if Unload never runs cleanly.
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
        StopDoorSweeper();
        StopCraftQueueDriver();

        // Captures every spawned survivor's live position/health/inventory into its
        // persistent Character record before SaveManager writes it to disk, so bots
        // survive a reload instead of despawning.
        CaptureAllLiveState();

        _engine?.Stop();

        Puts("LivingRust unloaded.");
    }
}