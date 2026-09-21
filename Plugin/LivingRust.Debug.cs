using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingRust.Models;
using LivingRust.Navigation;
using Oxide.Plugins;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins;

public partial class LivingRust
{
    private const string TraceDirectory = "LivingRust/traces";

    // Real curated base-design pool (2026-08-29, Lucas's own explicit ask -
    // "have it so the bots roll between choosing 1 of the 3 base choices
    // for tier0"). Deliberately separate from TraceDirectory - that folder
    // fills up with every raw /lr.debug.tracebuild recording (including
    // aborted/incomplete ones, confirmed live this session - several got
    // cut off partway and had to be manually cleaned up), so picking
    // randomly among ALL files in there would happily hand the bot a
    // broken half-finished trace. This folder only ever contains designs
    // deliberately promoted in via /lr.debug.savebasedesign, one
    // subfolder per tier - matching the tiered blueprint library Lucas
    // described wanting eventually (3-4 tier1, 2-3 tier2, 2-3 tier3).
    private const string BaseDesignsDirectory = "LivingRust/base_designs";

    private readonly Dictionary<Guid, Timer> _activeTraces = new();
    private readonly Dictionary<Guid, StreamWriter> _traceWriters = new();

    // Keyed by the real scientist2 entity's own network ID, not a Guid -
    // it's not one of our own Characters, so there's no Survivor/Character
    // roster entry to key off.
    private readonly Dictionary<ulong, Timer> _activeNpcTraces = new();
    private readonly Dictionary<ulong, StreamWriter> _npcTraceWriters = new();

    // Traces a real, connected admin player's own movement (2026-08-16,
    // Lucas's own explicit request) - same registry shape as
    // _activeNpcTraces/_npcTraceWriters, keyed by userID instead of a
    // Character Guid since a real player isn't a Survivor at all. Built to
    // get a real "known good" ground-truth path through a trouble spot
    // (the Abandoned Supermarket doorway/barricade/office room) directly
    // from how a genuine player actually moves through it - a real player
    // never touches TryGetNextStep/IsBodyOverlapping/CalculatePath at all,
    // so this can't diagnose OUR code, but it's exactly the kind of data
    // an authored hardcoded-waypoint route (mirroring LivingRust.MonumentRoutes.cs's
    // existing Powerline tower pattern) would need to be built from.
    private readonly Dictionary<ulong, Timer> _activePlayerTraces = new();
    private readonly Dictionary<ulong, StreamWriter> _playerTraceWriters = new();

    /// <summary>
    /// Toggles recording the nearest survivor's exact position (plus
    /// onLadder/sprinting/ducked state) every tick to a CSV file under
    /// LivingRust/traces - a precise, plottable record of a whole
    /// walk/follow/climb run (e.g. "Point A below the ramp" to "Point B at
    /// the top") for pinpointing exactly where something goes right or
    /// wrong, instead of inferring it from scattered Puts() log lines.
    /// Call once to start, call again on the same survivor to stop.
    /// </summary>
    [ChatCommand("lr.debug.trace")]
    private void CmdDebugTrace(BasePlayer player, string command, string[] args)
    {
        RunDebugTrace(player);
    }

    /// <summary>
    /// Toggles bot invincibility - added 2026-08-13 for live spread-
    /// calibration testing (Lucas's own request: fire many rounds at a
    /// stationary bot from set distances without it dying/respawning
    /// mid-test, which would reset position and break the "same bot, same
    /// spot, known distance" setup those numbers depend on). Restores full
    /// health and clears wounded state on every hit a survivor takes while
    /// this is on, rather than fighting with DamageTypeList internals to
    /// cancel the hit at its source - simpler, and works regardless of
    /// whichever exact point OnEntityTakeDamage fires relative to the real
    /// damage application. Off by default; explicitly NOT persisted
    /// anywhere - a fresh plugin load always starts with real, killable
    /// survivors, this is a manual per-session testing toggle only.
    /// </summary>
    private bool _botsInvincible = false;

    /// <summary>
    /// Toggles bots fighting real players entirely (both the reactive
    /// OnEntityTakeDamage path and on-sight detection) - added 2026-08-14
    /// per explicit request while live-testing animal interaction: with
    /// only one real player on the server, every bot defaults to shooting
    /// that player back the instant it's hit or spotted, making it
    /// impossible to isolate and observe bot-vs-animal behavior in
    /// peace. Bot-vs-bot and bot-vs-animal combat are both completely
    /// unaffected - this only ever suppresses a REAL BasePlayer as a
    /// combat target. Off by default; explicitly NOT persisted anywhere,
    /// same as _botsInvincible - a fresh plugin load always starts with
    /// normal player-combat enabled.
    /// </summary>
    private bool _disablePlayerCombat = false;

    /// <summary>
    /// Lets the real BotVsBotAggressionGracePeriodSeconds window (LivingRust.
    /// Combat.cs) be bypassed for testing - off by default (same pattern as
    /// _botsInvincible/_disablePlayerCombat), since the real 15-minute
    /// window ties to this process's own real uptime and would otherwise
    /// require either restarting the whole Rust server or waiting out a
    /// genuine 15 real minutes to ever observe in a dev session.
    /// </summary>
    private bool _skipBotVsBotGracePeriod = false;

    [ChatCommand("lr.debug.skipbotgraceperiod")]
    private void CmdDebugSkipBotGracePeriod(BasePlayer player, string command, string[] args)
    {
        RunDebugSkipBotGracePeriod(player, args);
    }

    [ConsoleCommand("lr.debug.skipbotgraceperiod")]
    private void CmdDebugSkipBotGracePeriodConsole(ConsoleSystem.Arg arg)
    {
        RunDebugSkipBotGracePeriod(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugSkipBotGracePeriod(BasePlayer? player, string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _skipBotVsBotGracePeriod = true;
            }
            else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _skipBotVsBotGracePeriod = false;
            }
        }
        else
        {
            _skipBotVsBotGracePeriod = !_skipBotVsBotGracePeriod;
        }

        string state = _skipBotVsBotGracePeriod ? "ON (bot-vs-bot grace period bypassed - proactive aggression allowed immediately)" : "OFF (real 15-minute grace period applies)";
        string message = $"[LivingRust] Skip-bot-grace-period is now {state}.";
        if (player != null)
        {
            player.ChatMessage(message);
        }

        Puts(message);
    }

    [ChatCommand("lr.debug.noplayercombat")]
    private void CmdDebugNoPlayerCombat(BasePlayer player, string command, string[] args)
    {
        RunDebugNoPlayerCombat(player, args);
    }

    [ConsoleCommand("lr.debug.noplayercombat")]
    private void CmdDebugNoPlayerCombatConsole(ConsoleSystem.Arg arg)
    {
        RunDebugNoPlayerCombat(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugNoPlayerCombat(BasePlayer? player, string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _disablePlayerCombat = true;
            }
            else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _disablePlayerCombat = false;
            }
        }
        else
        {
            _disablePlayerCombat = !_disablePlayerCombat;
        }

        string state = _disablePlayerCombat ? "ON (bots will NOT fight real players)" : "OFF (normal player combat)";
        string message = $"[LivingRust] Disable-player-combat is now {state}.";
        if (player != null)
        {
            player.ChatMessage(message);
        }
        else
        {
            Puts(message);
        }
    }

    /// <summary>
    /// Global, temporary test-only toggle (2026-08-23) - Lucas's own
    /// explicit request while debugging the self-heal animation: the "too
    /// low on health" combat disengage (CombatFleeHealthThreshold, 25)
    /// runs every combat tick unconditionally, even mid-heal, and was
    /// confirmed as the real trigger behind a live-caught race (health
    /// crashing through 25 mid-animation tore the fight down and orphaned
    /// an in-flight heal chain, which then collided with a fresh one from
    /// the next re-engagement). That race has its own real fix now (a
    /// generation token on the heal chain itself), but disabling the
    /// disengage entirely is still useful to isolate heal-animation
    /// testing from combat ending underneath it while iterating. Same
    /// pattern as _botsInvincible/_disablePlayerCombat - off by default,
    /// never persisted, a fresh plugin load always starts with normal
    /// low-health disengage behavior.
    /// </summary>
    private bool _disableLowHealthDisengage = false;

    [ChatCommand("lr.debug.nolowhealthdisengage")]
    private void CmdDebugNoLowHealthDisengage(BasePlayer player, string command, string[] args)
    {
        RunDebugNoLowHealthDisengage(player, args);
    }

    [ConsoleCommand("lr.debug.nolowhealthdisengage")]
    private void CmdDebugNoLowHealthDisengageConsole(ConsoleSystem.Arg arg)
    {
        RunDebugNoLowHealthDisengage(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugNoLowHealthDisengage(BasePlayer? player, string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _disableLowHealthDisengage = true;
            }
            else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _disableLowHealthDisengage = false;
            }
        }
        else
        {
            _disableLowHealthDisengage = !_disableLowHealthDisengage;
        }

        string lowHealthState = _disableLowHealthDisengage ? "ON (bots will NOT disengage at low health)" : "OFF (normal low-health disengage)";
        string lowHealthMessage = $"[LivingRust] Disable-low-health-disengage is now {lowHealthState}.";
        if (player != null)
        {
            player.ChatMessage(lowHealthMessage);
        }
        else
        {
            Puts(lowHealthMessage);
        }
    }

    /// <summary>
    /// Global, temporary test-only toggle (2026-08-15) - Lucas's own
    /// request while chasing the "invisible door" doorway freeze at
    /// Abandoned Supermarket: force every survivor to fly straight at its
    /// destination in 3D, completely bypassing TryGetNextStep/
    /// IsBodyOverlapping/native NavMeshAgent pathing, to isolate whether a
    /// stuck bot is a real physical-collision problem or a pathing/logic
    /// one. Same pattern as _botsInvincible/_disablePlayerCombat - off by
    /// default, never persisted, a fresh plugin load always starts clean.
    /// NOT meant to ship as a real gameplay feature.
    /// </summary>
    private bool _noclipEnabled = false;

    [ChatCommand("lr.debug.noclip")]
    private void CmdDebugNoclip(BasePlayer player, string command, string[] args)
    {
        RunDebugNoclip(player, args);
    }

    [ConsoleCommand("lr.debug.noclip")]
    private void CmdDebugNoclipConsole(ConsoleSystem.Arg arg)
    {
        RunDebugNoclip(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugNoclip(BasePlayer? player, string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _noclipEnabled = true;
            }
            else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _noclipEnabled = false;
            }
        }
        else
        {
            _noclipEnabled = !_noclipEnabled;
        }

        string state = _noclipEnabled ? "ON (every survivor flies straight through walls/geometry toward its destination)" : "OFF (normal collision-aware movement)";
        string message = $"[LivingRust] Debug noclip is now {state}.";

        // Always to the server console too (2026-08-15), not just chat -
        // a live test where "/lr.debug.noclip on" was sent (confirmed via
        // command_history) produced zero visible effect, and there was no
        // way to tell from the server log alone whether the toggle itself
        // ever actually ran, since ChatMessage never reaches Carbon.Core.log.
        // This closes that blind spot for next time.
        Puts(message);

        if (player != null)
        {
            player.ChatMessage(message);
        }
    }

    [ChatCommand("lr.debug.godmode")]
    private void CmdDebugGodmode(BasePlayer player, string command, string[] args)
    {
        RunDebugGodmode(player, args);
    }

    [ConsoleCommand("lr.debug.godmode")]
    private void CmdDebugGodmodeConsole(ConsoleSystem.Arg arg)
    {
        RunDebugGodmode(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugGodmode(BasePlayer? player, string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _botsInvincible = true;
            }
            else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _botsInvincible = false;
            }
        }
        else
        {
            _botsInvincible = !_botsInvincible;
        }

        string state = _botsInvincible ? "ON" : "OFF";
        string message = $"[LivingRust] Bot invincibility is now {state}.";
        if (player != null)
        {
            player.ChatMessage(message);
        }
        else
        {
            Puts(message);
        }
    }

    /// <summary>
    /// Toggles the routine per-bot console chatter (loot summaries, equip
    /// confirmations, movement narration) on/off. Off by default is NOT
    /// the setting - defaults to on so existing debugging habits keep
    /// working; flip it off when running a large bot population and the
    /// console is being drowned out. WARNING lines and one-time lifecycle
    /// events (spawn/despawn/crash) always log either way.
    /// </summary>
    [ChatCommand("lr.debug.verbose")]
    private void CmdDebugVerbose(BasePlayer player, string command, string[] args)
    {
        RunDebugVerbose(player, args);
    }

    [ConsoleCommand("lr.debug.verbose")]
    private void CmdDebugVerboseConsole(ConsoleSystem.Arg arg)
    {
        RunDebugVerbose(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugVerbose(BasePlayer? player, string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _verboseLootLogging = true;
            }
            else if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _verboseLootLogging = false;
            }
        }
        else
        {
            _verboseLootLogging = !_verboseLootLogging;
        }

        string state = _verboseLootLogging ? "ON" : "OFF";
        string message = $"[LivingRust] Verbose per-bot console logging is now {state}.";
        if (player != null)
        {
            player.ChatMessage(message);
        }
        else
        {
            Puts(message);
        }
    }

    [ConsoleCommand("lr.debug.trace")]
    private void CmdDebugTraceConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugTrace(player);
        }
    }

    private void RunDebugTrace(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        Guid characterId = survivor.Character.Id;

        if (_activeTraces.TryGetValue(characterId, out Timer existingTraceTimer))
        {
            existingTraceTimer.Destroy();
            _activeTraces.Remove(characterId);

            if (_traceWriters.TryGetValue(characterId, out StreamWriter existingWriter))
            {
                existingWriter.Flush();
                existingWriter.Dispose();
                _traceWriters.Remove(characterId);
            }

            player.ChatMessage($"[LivingRust] Stopped tracing '{survivor.Character.Alias}'.");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            Directory.CreateDirectory(TraceDirectory);
        }

        string fileName = StartSurvivorTrace(survivor, DateTime.Now.ToString("yyyyMMdd_HHmmss"), Time.realtimeSinceStartup);

        player.ChatMessage($"[LivingRust] Tracing '{survivor.Character.Alias}' - run /lr.debug.trace again to stop. Saved to {fileName}");
    }

    [ChatCommand("lr.debug.tracemany")]
    private void CmdDebugTraceMany(BasePlayer player, string command, string[] args)
    {
        RunDebugTraceMany(player);
    }

    [ConsoleCommand("lr.debug.tracemany")]
    private void CmdDebugTraceManyConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugTraceMany(player);
        }
    }

    /// <summary>
    /// Same per-bot CSV format/tick cadence as /lr.debug.trace, but starts
    /// one trace per currently-spawned survivor at once instead of just
    /// the nearest one - built for watching a whole /lr.debug.spawnmany
    /// batch move and loot together (e.g. the bot-phasing-through-each-
    /// other investigation), where a single-bot trace can't show cross-bot
    /// interaction at all. All traces from one call share the same
    /// filename timestamp and start clock, so their elapsed_s columns
    /// line up for direct cross-bot comparison in the same time window.
    /// Reuses the same _activeTraces/_traceWriters maps as /lr.debug.trace
    /// (keyed by character ID) - toggling tracemany again stops every
    /// currently active survivor trace, whichever command started it.
    /// </summary>
    private void RunDebugTraceMany(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (_activeTraces.Count > 0)
        {
            int stopped = _activeTraces.Count;

            foreach (Timer traceTimer in _activeTraces.Values)
            {
                traceTimer.Destroy();
            }

            _activeTraces.Clear();

            foreach (StreamWriter writer in _traceWriters.Values)
            {
                writer.Flush();
                writer.Dispose();
            }

            _traceWriters.Clear();

            player.ChatMessage($"[LivingRust] Stopped tracing {stopped} survivor(s).");
            return;
        }

        List<Survivor> spawned = _engine.SurvivorManager.GetAll()
            .Where(s => s.Player != null && !s.Player.IsDestroyed)
            .ToList();

        if (spawned.Count == 0)
        {
            player.ChatMessage("[LivingRust] No spawned survivors to trace. Use /lr.spawn or /lr.debug.spawnmany first.");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            Directory.CreateDirectory(TraceDirectory);
        }

        string batchStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        float startTime = Time.realtimeSinceStartup;

        int started = 0;

        foreach (Survivor survivor in spawned)
        {
            // Isolated per-survivor - two different Characters can share
            // the same generated Alias (confirmed live: a real file-
            // sharing-violation crash on "TinyMiner" killed this whole
            // loop partway through, silently leaving every survivor after
            // the colliding one untraced). The real fix is the now-unique
            // filename in StartSurvivorTrace (includes BotId, not just
            // Alias), but this try/catch stays as a safety net so any
            // future unexpected IO failure for one survivor can't abort
            // tracing for the rest of the batch.
            try
            {
                StartSurvivorTrace(survivor, batchStamp, startTime);
                started++;
            }
            catch (Exception exception)
            {
                Puts($"WARNING: tracemany - failed to start a trace for '{survivor.Character.Alias}': {exception.Message}");
            }
        }

        player.ChatMessage($"[LivingRust] Tracing {started}/{spawned.Count} survivor(s) - run /lr.debug.tracemany again to stop all. Saved under {TraceDirectory}/.");
    }

    /// <summary>
    /// Shared per-survivor trace setup used by both /lr.debug.trace (one
    /// bot, its own timestamp/clock) and /lr.debug.tracemany (every
    /// spawned bot, one shared timestamp/clock so elapsed_s lines up
    /// across files). No-ops if this survivor is already being traced
    /// (e.g. tracemany running over one that's individually traced too).
    /// Filename includes BotId (the real, always-unique userID), not just
    /// Alias - confirmed live that two different Characters can share the
    /// same generated Alias, which previously produced an identical
    /// filename for both and crashed with a file-sharing-violation
    /// exception the moment the second one's StreamWriter tried to open
    /// the first one's still-open file.
    /// </summary>
    private string StartSurvivorTrace(Survivor survivor, string fileStamp, float startTime)
    {
        Guid characterId = survivor.Character.Id;
        string fileName = $"{TraceDirectory}/trace_{survivor.Character.Alias}_{survivor.Character.BotId}_{fileStamp}.csv";

        if (_activeTraces.ContainsKey(characterId))
        {
            return fileName;
        }

        StreamWriter writer = new StreamWriter(fileName, append: false);

        // target_* columns added 2026-08-13 - purely diagnostic, lets a
        // trace directly answer "was this bot still closing initial
        // distance / chasing a fast-moving real target / or something
        // else" instead of only ever being able to guess from the bot's
        // own position+facing alone. Blank whenever _activeCombatTarget has
        // no entry for this survivor (not currently in combat) rather than
        // a misleading 0/placeholder value.
        writer.WriteLine("elapsed_s,x,y,z,facing_deg,on_ladder,sprinting,ducked,in_combat,target_x,target_y,target_z,target_distance");

        Timer traceTimer = null;

        traceTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            // Survives death/respawn now (2026-08-15, Lucas's own explicit
            // request - "keep the trace across deaths so we can target any
            // bugs it produces") - previously this stopped the trace
            // outright the instant the dying BasePlayer got destroyed,
            // closing the file for good even though RespawnSurvivor
            // (LivingRust.Hooks.cs) assigns a brand new BasePlayer to this
            // exact same survivor a few seconds later. Now it just skips
            // writing for whichever ticks land in that dead/respawning gap
            // (a real, honest gap in the CSV - no fabricated "dead" rows)
            // and keeps the timer alive, so npc naturally becomes valid
            // again on its own once RespawnSurvivor runs, with zero extra
            // wiring needed here. Only the explicit stop paths
            // (/lr.debug.trace off, StopAllTraces) end a trace now - it no
            // longer ends itself just because the survivor is currently
            // between lives.
            if (npc == null || npc.IsDestroyed)
            {
                return;
            }

            Vector3 pos = npc.transform.position;
            float facing = npc.transform.rotation.eulerAngles.y;
            float elapsed = Time.realtimeSinceStartup - startTime;

            string targetColumns = ",,,,";

            if (_activeCombatTarget.TryGetValue(characterId, out BaseCombatEntity target) && target != null && !target.IsDestroyed)
            {
                Vector3 targetPos = target.transform.position;
                float targetDistance = Vector3.Distance(pos, targetPos);
                targetColumns = $"True,{targetPos.x:F2},{targetPos.y:F2},{targetPos.z:F2},{targetDistance:F2}";
            }
            else
            {
                targetColumns = "False,,,,";
            }

            writer.WriteLine($"{elapsed:F2},{pos.x:F2},{pos.y:F2},{pos.z:F2},{facing:F1},{npc.modelState.onLadder},{npc.modelState.sprinting},{npc.modelState.ducked},{targetColumns}");
            writer.Flush();
        });

        _activeTraces[characterId] = traceTimer;
        _traceWriters[characterId] = writer;

        return fileName;
    }

    [ChatCommand("lr.debug.tracenpc")]
    private void CmdDebugTraceNpc(BasePlayer player, string command, string[] args)
    {
        RunDebugTraceNpc(player);
    }

    [ConsoleCommand("lr.debug.tracenpc")]
    private void CmdDebugTraceNpcConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugTraceNpc(player);
        }
    }

    /// <summary>
    /// Same idea as /lr.debug.trace, but for the nearest REAL scientist2
    /// (ScientistNPC2, not one of our own survivors) - user-requested, to
    /// get a real "known good" baseline of how native RustNavMeshAgent
    /// movement actually behaves around the same trouble spots (tent
    /// doorways, awning canopies, sandbags) our own bots have struggled
    /// with. Won't reveal anything about our OWN TryGetNextStep bug (the
    /// consensus-probe fix) - that's exclusively our hand-built code, a
    /// real scientist never runs it - but it will show whether native
    /// pathing itself cleanly handles these exact spots, which is direct
    /// evidence for how much we should keep leaning on native movement.
    /// Logs position/facing plus the scientist's own RustNavMeshAgent
    /// state (hasPath/remainingDistance/pathPending/isOnOffMeshLink/
    /// velocity) every tick, not just position - ModelState fields
    /// (onLadder/sprinting/ducked) don't exist on ScientistNPC2 at all
    /// (it's not a BasePlayer), so the CSV schema is deliberately
    /// different from the survivor trace's.
    /// </summary>
    private void RunDebugTraceNpc(BasePlayer player)
    {
        ScientistNPC2 nearest = null;
        float nearestDistSqr = float.MaxValue;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not ScientistNPC2 scientist || scientist.IsDestroyed)
            {
                continue;
            }

            float distSqr = (player.transform.position - scientist.transform.position).sqrMagnitude;

            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearest = scientist;
            }
        }

        if (nearest == null)
        {
            player.ChatMessage("[LivingRust] No live scientist2 found anywhere on the map.");
            return;
        }

        ulong npcId = nearest.net.ID.Value;

        if (_activeNpcTraces.TryGetValue(npcId, out Timer existingTraceTimer))
        {
            existingTraceTimer.Destroy();
            _activeNpcTraces.Remove(npcId);

            if (_npcTraceWriters.TryGetValue(npcId, out StreamWriter existingWriter))
            {
                existingWriter.Flush();
                existingWriter.Dispose();
                _npcTraceWriters.Remove(npcId);
            }

            player.ChatMessage($"[LivingRust] Stopped tracing scientist2 [{npcId}].");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            Directory.CreateDirectory(TraceDirectory);
        }

        string fileName = $"{TraceDirectory}/trace_scientist2_{npcId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        StreamWriter writer = new StreamWriter(fileName, append: false);
        writer.WriteLine("elapsed_s,x,y,z,facing_deg,hasPath,remainingDistance,pathPending,isOnOffMeshLink,velocity_x,velocity_y,velocity_z");

        float startTime = Time.realtimeSinceStartup;
        RustNavMeshAgent agent = nearest.GetComponent<RustNavMeshAgent>();

        Timer traceTimer = null;

        traceTimer = timer.Every(WalkTickInterval, () =>
        {
            if (nearest == null || nearest.IsDestroyed)
            {
                traceTimer.Destroy();
                _activeNpcTraces.Remove(npcId);

                if (_npcTraceWriters.TryGetValue(npcId, out StreamWriter deadWriter))
                {
                    deadWriter.Flush();
                    deadWriter.Dispose();
                    _npcTraceWriters.Remove(npcId);
                }

                return;
            }

            Vector3 pos = nearest.transform.position;
            float facing = nearest.transform.rotation.eulerAngles.y;
            float elapsed = Time.realtimeSinceStartup - startTime;

            bool hasPath = agent != null && agent.hasPath;
            float remainingDistance = agent != null ? agent.remainingDistance : 0f;
            bool pathPending = agent != null && agent.pathPending;
            bool isOnOffMeshLink = agent != null && agent.isOnOffMeshLink;
            Vector3 velocity = agent != null ? (Vector3)agent.velocity : Vector3.zero;

            writer.WriteLine($"{elapsed:F2},{pos.x:F2},{pos.y:F2},{pos.z:F2},{facing:F1},{hasPath},{remainingDistance:F2},{pathPending},{isOnOffMeshLink},{velocity.x:F2},{velocity.y:F2},{velocity.z:F2}");
            writer.Flush();
        });

        _activeNpcTraces[npcId] = traceTimer;
        _npcTraceWriters[npcId] = writer;

        player.ChatMessage($"[LivingRust] Tracing scientist2 [{npcId}] at {nearest.transform.position} - run /lr.debug.tracenpc again to stop. Saved to {fileName}");
    }

    /// <summary>
    /// Traces the CALLING admin's own real, connected BasePlayer - see
    /// _activePlayerTraces's own doc comment for why (real ground-truth
    /// movement data through a trouble spot, to build an authored
    /// waypoint route from). Toggle, same as every other trace command
    /// here - run again to stop.
    /// </summary>
    [ChatCommand("lr.debug.traceme")]
    private void CmdDebugTraceMe(BasePlayer player, string command, string[] args)
    {
        RunDebugTraceMe(player);
    }

    private void RunDebugTraceMe(BasePlayer player)
    {
        ulong playerId = player.userID.Get();

        if (_activePlayerTraces.TryGetValue(playerId, out Timer existingTraceTimer))
        {
            existingTraceTimer.Destroy();
            _activePlayerTraces.Remove(playerId);

            if (_playerTraceWriters.TryGetValue(playerId, out StreamWriter existingWriter))
            {
                existingWriter.Flush();
                existingWriter.Dispose();
                _playerTraceWriters.Remove(playerId);
            }

            player.ChatMessage("[LivingRust] Stopped tracing your own movement.");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            Directory.CreateDirectory(TraceDirectory);
        }

        string fileName = $"{TraceDirectory}/trace_player_{playerId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        StreamWriter writer = new StreamWriter(fileName, append: false);
        writer.WriteLine("elapsed_s,x,y,z,facing_deg,ducked,sprinting,swimming");

        float startTime = Time.realtimeSinceStartup;

        Timer traceTimer = null;

        traceTimer = timer.Every(WalkTickInterval, () =>
        {
            if (player == null || player.IsDestroyed || !player.IsConnected)
            {
                traceTimer.Destroy();
                _activePlayerTraces.Remove(playerId);

                if (_playerTraceWriters.TryGetValue(playerId, out StreamWriter deadWriter))
                {
                    deadWriter.Flush();
                    deadWriter.Dispose();
                    _playerTraceWriters.Remove(playerId);
                }

                return;
            }

            Vector3 pos = player.transform.position;
            float facing = player.transform.rotation.eulerAngles.y;
            float elapsed = Time.realtimeSinceStartup - startTime;

            writer.WriteLine($"{elapsed:F2},{pos.x:F2},{pos.y:F2},{pos.z:F2},{facing:F1},{player.modelState.ducked},{player.modelState.sprinting},{player.IsSwimming()}");
            writer.Flush();
        });

        _activePlayerTraces[playerId] = traceTimer;
        _playerTraceWriters[playerId] = writer;

        player.ChatMessage($"[LivingRust] Tracing your own movement at {player.transform.position} - walk through the trouble spot, then run /lr.debug.traceme again to stop. Saved to {fileName}");
    }

    // Real per-player build-trace registry (2026-08-28, Lucas's own
    // explicit request: "trace my movement + placement of walls, floors
    // and ceilings + doorframes") - same shape as _activePlayerTraces/
    // _playerTraceWriters above, but event-driven off the real
    // OnEntityBuilt hook (confirmed patched into this Carbon build via
    // Carbon.Hooks.Oxide.dll) rather than a periodic timer tick, since a
    // construction placement is a discrete moment, not a continuous
    // stream of positions the way movement is.
    private readonly Dictionary<ulong, StreamWriter> _playerBuildTraceWriters = new();

    [ChatCommand("lr.debug.tracebuild")]
    private void CmdDebugTraceBuild(BasePlayer player, string command, string[] args)
    {
        RunDebugTraceBuild(player);
    }

    private void RunDebugTraceBuild(BasePlayer player)
    {
        ulong playerId = player.userID.Get();

        if (_playerBuildTraceWriters.TryGetValue(playerId, out StreamWriter existingWriter))
        {
            existingWriter.Flush();
            existingWriter.Dispose();
            _playerBuildTraceWriters.Remove(playerId);
            player.ChatMessage("[LivingRust] Stopped tracing your building placements.");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            Directory.CreateDirectory(TraceDirectory);
        }

        string fileName = $"{TraceDirectory}/tracebuild_player_{playerId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        StreamWriter writer = new StreamWriter(fileName, append: false);
        writer.WriteLine("elapsed_s,event_type,shortname,x,y,z,rot_x,rot_y,rot_z,grade,parent_entity_id,parent_shortname,wood_after,stones_after,metal_fragments_after");
        writer.Flush();

        _playerBuildTraceWriters[playerId] = writer;

        player.ChatMessage($"[LivingRust] Tracing your building placements - place foundations/walls/ceilings/doorframes/doors as normal, then run /lr.debug.tracebuild again to stop. Saved to {fileName}");
    }

    private readonly float _buildTraceStartTime = Time.realtimeSinceStartup;

    /// <summary>
    /// Real Oxide/Carbon hook (confirmed patched into Carbon.Hooks.Oxide.dll -
    /// same "grep the DLL for the hook name" confirmation this project
    /// already used for OnDispenserGathered/OnEntityDeath) - fires for
    /// EVERY real construction placement server-wide (a foundation, wall,
    /// ceiling, doorway frame, stairs, ...), the exact same real flow the
    /// hammer+building-plan combo drives. go is the newly placed
    /// GameObject; plan.GetOwnerPlayer() (confirmed via decompile - same
    /// real HeldEntity API Deployer.GetOwnerPlayer already used for the
    /// sleeping bag work) identifies who placed it, so this only logs for
    /// whichever real player currently has a trace active rather than
    /// recording every bot/player's construction server-wide. Grade
    /// (twig/wood/stone/metal/armored) is logged when the placed entity is
    /// a real BuildingBlock - doorframes/stairs/foundations all are;
    /// deployables placed into a building socket (a door into its frame)
    /// go through Deployer.DoDeploy_Slot instead (already decompiled for
    /// the sleeping bag placement work) and won't show up here - a
    /// separate concern from Lucas's own explicit ask, which named
    /// "walls, floors, ceilings, doorframes" specifically (all real
    /// Construction/BuildingBlock pieces), not the door itself.
    /// </summary>
    private void OnEntityBuilt(Planner plan, GameObject go)
    {
        if (_playerBuildTraceWriters.Count == 0)
        {
            return;
        }

        BasePlayer ownerPlayer = plan.GetOwnerPlayer();

        if (ownerPlayer == null || !_playerBuildTraceWriters.TryGetValue(ownerPlayer.userID.Get(), out StreamWriter writer))
        {
            return;
        }

        BaseEntity builtEntity = go.ToBaseEntity();

        if (builtEntity == null)
        {
            return;
        }

        float elapsed = Time.realtimeSinceStartup - _buildTraceStartTime;
        Vector3 pos = builtEntity.transform.position;
        Vector3 rot = builtEntity.transform.rotation.eulerAngles;
        string grade = builtEntity is BuildingBlock block ? block.grade.ToString() : string.Empty;
        BaseEntity parent = builtEntity.GetParentEntity();
        string parentId = parent != null ? parent.net.ID.ToString() : string.Empty;
        string parentShortname = parent != null ? parent.ShortPrefabName : string.Empty;

        WriteBuildTraceRow(writer, elapsed, "build", builtEntity.ShortPrefabName, pos, rot, grade, parentId, parentShortname, ownerPlayer);

        Puts($"tracebuild: '{ownerPlayer.displayName}' placed '{builtEntity.ShortPrefabName}' at {pos} (grade={grade}, parent='{parentShortname}').");
    }

    /// <summary>
    /// Real Oxide/Carbon hook (confirmed patched into Carbon.Hooks.Oxide.dll,
    /// same grep-the-DLL confirmation as OnEntityBuilt above) - fires after
    /// an EXISTING BuildingBlock (a wall/foundation/floor already placed)
    /// gets upgraded to a higher real grade (Twigs -> Wood -> Stone ->
    /// Metal -> Armored). 2026-08-28, Lucas's own live report: this was the
    /// real, previously-untraced ~5600 stone/~1000 wood drop between the
    /// cupboard and door rows in the first capture - confirmed his own
    /// framing exactly ("I also lost 1000 wood and 1000 stone... upgrade
    /// walls" was the missing piece). Logged as its own "upgrade" event
    /// type in the same CSV/row shape OnEntityBuilt already writes, so a
    /// trace reader doesn't need two different files to reconstruct the
    /// full real sequence.
    /// </summary>
    private void OnStructureUpgraded(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
    {
        if (_playerBuildTraceWriters.Count == 0 || player == null || block == null)
        {
            return;
        }

        if (!_playerBuildTraceWriters.TryGetValue(player.userID.Get(), out StreamWriter writer))
        {
            return;
        }

        float elapsed = Time.realtimeSinceStartup - _buildTraceStartTime;
        Vector3 pos = block.transform.position;
        Vector3 rot = block.transform.rotation.eulerAngles;
        BaseEntity parent = block.GetParentEntity();
        string parentId = parent != null ? parent.net.ID.ToString() : string.Empty;
        string parentShortname = parent != null ? parent.ShortPrefabName : string.Empty;

        WriteBuildTraceRow(writer, elapsed, "upgrade", block.ShortPrefabName, pos, rot, grade.ToString(), parentId, parentShortname, player);

        Puts($"tracebuild: '{player.displayName}' upgraded '{block.ShortPrefabName}' to {grade} at {pos}.");
    }

    /// <summary>
    /// Real Oxide/Carbon hook, confirmed patched into Carbon.Hooks.Oxide.dll
    /// off Deployer.DoDeploy_Slot (2026-08-29, Lucas's own live report: a
    /// code lock placed during a real trace never showed up in the replay
    /// at all - the CSV simply had no lock.code row). Root cause: a code
    /// lock deploys through Deployer, a completely separate HeldEntity from
    /// Planner - OnEntityBuilt above is typed specifically for Planner and
    /// never fires for it (locks are a "slot" deploy: attaches onto an
    /// EXISTING entity's Slot.Lock anchor, not a free-standing placement).
    /// Confirmed via decompile that the [Slot] patch variant passes two
    /// BaseEntity params in this declared order - the deploy TARGET (the
    /// door), then the freshly created entity - but this checks by type
    /// rather than trusting that order, since Carbon's own patch metadata
    /// shows the [Regular] variant (free-standing deploys, already covered
    /// by OnEntityBuilt/Planner) passes a completely different second
    /// parameter type (ItemModDeployable) under the same hook name, and
    /// getting the order backwards here would silently record the wrong
    /// entity as the lock.
    /// </summary>
    private void OnItemDeployed(Deployer deployer, BaseEntity entityA, BaseEntity entityB)
    {
        if (_playerBuildTraceWriters.Count == 0 || deployer == null)
        {
            return;
        }

        BaseLock deployedLock = entityA as BaseLock ?? entityB as BaseLock;

        if (deployedLock == null)
        {
            return;
        }

        BaseEntity target = ReferenceEquals(deployedLock, entityA) ? entityB : entityA;
        BasePlayer ownerPlayer = deployer.GetOwnerPlayer();

        if (ownerPlayer == null || !_playerBuildTraceWriters.TryGetValue(ownerPlayer.userID.Get(), out StreamWriter writer))
        {
            return;
        }

        float elapsed = Time.realtimeSinceStartup - _buildTraceStartTime;
        Vector3 pos = deployedLock.transform.position;
        Vector3 rot = deployedLock.transform.rotation.eulerAngles;
        string parentId = target != null ? target.net.ID.ToString() : string.Empty;
        string parentShortname = target != null ? target.ShortPrefabName : string.Empty;

        WriteBuildTraceRow(writer, elapsed, "build", deployedLock.ShortPrefabName, pos, rot, string.Empty, parentId, parentShortname, ownerPlayer);

        Puts($"tracebuild: '{ownerPlayer.displayName}' placed '{deployedLock.ShortPrefabName}' at {pos} (parent='{parentShortname}').");
    }

    /// <summary>
    /// Shared row-writer for both OnEntityBuilt and OnStructureUpgraded -
    /// same real material-count snapshot (2026-08-28, Lucas's own explicit
    /// ask: confirm items are "being removed from inventory to place
    /// foundations / upgrade walls") for both event types, since both
    /// hooks fire AFTER Construction's own real cost deduction already
    /// ran - comparing one row's counts against the previous row's is a
    /// direct, real before/after view of consumption, nothing simulated.
    /// </summary>
    private void WriteBuildTraceRow(StreamWriter writer, float elapsed, string eventType, string shortname, Vector3 pos, Vector3 rot, string grade, string parentId, string parentShortname, BasePlayer ownerPlayer)
    {
        int woodAfter = ownerPlayer.inventory.GetAmount(ItemManager.FindItemDefinition(WoodShortname)?.itemid ?? 0);
        int stonesAfter = ownerPlayer.inventory.GetAmount(ItemManager.FindItemDefinition(StoneShortname)?.itemid ?? 0);
        int fragmentsAfter = ownerPlayer.inventory.GetAmount(ItemManager.FindItemDefinition("metal.fragments")?.itemid ?? 0);

        writer.WriteLine($"{elapsed:F2},{eventType},{shortname},{pos.x:F2},{pos.y:F2},{pos.z:F2},{rot.x:F1},{rot.y:F1},{rot.z:F1},{grade},{parentId},{parentShortname},{woodAfter},{stonesAfter},{fragmentsAfter}");
        writer.Flush();
    }

    /// <summary>
    /// Direct test trigger for the base-building replay (2026-08-28,
    /// LivingRust.BaseBuilding.cs) - finds the nearest survivor to the
    /// caller (same pattern every other /lr.debug.* force command already
    /// uses) and the caller's own most recently saved /lr.debug.tracebuild
    /// CSV, then replays it with the survivor's own current position as
    /// the new origin.
    /// </summary>
    /// <summary>
    /// No explicit tier arg (2026-08-29, Lucas's own explicit ask: "figure
    /// out how they decide what tier of base they want to build") now
    /// means "decide for yourself" rather than always defaulting to
    /// tier0 - resolved to the AutoBaseDesignTier sentinel here, checked
    /// in RunDebugReplayBuild, which runs TryChooseAffordableBaseDesign
    /// (LivingRust.BaseBuilding.cs) instead of rolling within one fixed
    /// tier. Passing an explicit tier (e.g. "/lr.debug.replaybuild tier2")
    /// still works exactly as before, for testing one tier specifically.
    /// </summary>
    /// <summary>
    /// Real isolated ghost-crossing test command (2026-09-01) - triggers
    /// GhostEnterHomeForDeposit directly on the nearest spawned survivor
    /// (or by alias) so Lucas can verify the fallback movement in
    /// isolation, without needing a real task to happen to route through
    /// it first.
    /// </summary>
    [ChatCommand("lr.debug.ghostenter")]
    private void CmdDebugGhostEnter(BasePlayer player, string command, string[] args)
    {
        RunDebugGhostEnter(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    private void RunDebugGhostEnter(BasePlayer player, string aliasFilter)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
            ? FindNearestSpawnedSurvivor(player.transform.position)
            : FindSpawnedSurvivorByAlias(aliasFilter);

        if (survivor == null)
        {
            string message = string.IsNullOrWhiteSpace(aliasFilter)
                ? "No spawned survivor nearby. Use /lr.spawn first."
                : $"No spawned survivor named '{aliasFilter}'.";

            player.ChatMessage($"[LivingRust] {message}");
            return;
        }

        if (survivor.Character.Home == null || survivor.Character.Home.DoorRoutes.Count == 0)
        {
            player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' has no home with recorded door routes.");
            return;
        }

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' ghosting in to its cupboard...");

        GhostEnterHomeForDeposit(survivor, () =>
        {
            player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' ghost-entry complete.");
        });
    }

    [ChatCommand("lr.debug.forcereturnhome")]
    private void CmdDebugForceReturnHome(BasePlayer player, string command, string[] args)
    {
        RunDebugForceReturnHome(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    /// <summary>
    /// On-demand trigger for the real production "recycle then go home and
    /// deposit" trip (2026-09-01) - naturally only fires today once a
    /// survivor finishes a genuine recycling trip (see FinishRecycling,
    /// LivingRust.Recycling.cs), which is slow to wait for live. Runs
    /// GhostReturnHomeAndDeposit directly on an already-spawned survivor
    /// so the deposit/furnace-fill behaviour can be verified without
    /// waiting for a real full-inventory-then-recycle cycle first. Same
    /// alias/nearest lookup as /lr.debug.ghostenter.
    /// </summary>
    private void RunDebugForceReturnHome(BasePlayer player, string aliasFilter)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
            ? FindNearestSpawnedSurvivor(player.transform.position)
            : FindSpawnedSurvivorByAlias(aliasFilter);

        if (survivor == null)
        {
            string message = string.IsNullOrWhiteSpace(aliasFilter)
                ? "No spawned survivor nearby. Use /lr.spawn first."
                : $"No spawned survivor named '{aliasFilter}'.";

            player.ChatMessage($"[LivingRust] {message}");
            return;
        }

        if (survivor.Character.Home == null || survivor.Character.Home.DoorRoutes.Count == 0)
        {
            player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' has no home with recorded door routes.");
            return;
        }

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' heading home to deposit...");

        GhostReturnHomeAndDeposit(survivor, () =>
        {
            player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' finished its return-home trip.");
        });
    }

    [ChatCommand("lr.debug.replaybuild")]
    private void CmdDebugReplayBuild(BasePlayer player, string command, string[] args)
    {
        // Real recording-workflow support (2026-08-29, seventh round -
        // Lucas's own explicit ask: "spawns the base build type from
        // each tier, then I can trace it (without codelocked doors) to
        // walk through it"). "nolock" anywhere in the args (order-
        // independent, so "/lr.debug.replaybuild tier0 base3 nolock"
        // reads naturally) skips every code-lock row in the replay
        // entirely, and a second plain arg picks an EXACT design by name
        // (e.g. "base3") instead of rolling randomly within the tier -
        // both only matter for this deliberate recording use case; a
        // real bot's own build always rolls normally with locks intact.
        bool skipCodeLocks = args.Any(a => a.Equals("nolock", StringComparison.OrdinalIgnoreCase));
        string[] positionalArgs = args.Where(a => !a.Equals("nolock", StringComparison.OrdinalIgnoreCase)).ToArray();

        RunDebugReplayBuild(
            player,
            positionalArgs.Length > 0 ? positionalArgs[0] : AutoBaseDesignTier,
            positionalArgs.Length > 1 ? positionalArgs[1] : null,
            skipCodeLocks);
    }

    // Which tier /lr.debug.replaybuild rolls against when a specific tier
    // is explicitly requested but that tier's pool doesn't exist/is empty
    // yet, and the final fallback TryChooseAffordableBaseDesign itself
    // uses when nothing anywhere is currently affordable.
    private const string DefaultBaseDesignTier = "tier0";

    // Sentinel tier value meaning "let the bot decide" rather than a real
    // folder name - never matches a real BaseDesignsDirectory subfolder.
    private const string AutoBaseDesignTier = "auto";

    /// <summary>
    /// Real curated-pool promotion (2026-08-29) - copies the caller's own
    /// most recent raw /lr.debug.tracebuild recording into
    /// BaseDesignsDirectory/{tier}/, so it joins the roll pool
    /// /lr.debug.replaybuild picks from. A deliberate, separate step
    /// rather than auto-promoting every trace - several recordings this
    /// session got cut off partway (a road/slope refusal, chat freezing
    /// aim mid-capture) and needed to be thrown away, so only a design
    /// Lucas has actually confirmed looks right in-game should ever join
    /// the pool a bot might build from later.
    /// </summary>
    [ChatCommand("lr.debug.savebasedesign")]
    private void CmdDebugSaveBaseDesign(BasePlayer player, string command, string[] args)
    {
        RunDebugSaveBaseDesign(player, args.Length > 0 ? args[0] : DefaultBaseDesignTier);
    }

    private void RunDebugSaveBaseDesign(BasePlayer? player, string tier)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.savebasedesign requires a real player context (run from in-game chat/console).");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            player.ChatMessage("[LivingRust] No trace directory found - run /lr.debug.tracebuild and place a structure first.");
            return;
        }

        string playerId = player.userID.Get().ToString();
        string latestCsv = Directory.GetFiles(TraceDirectory, $"tracebuild_player_{playerId}_*.csv")
            .OrderByDescending(path => path)
            .FirstOrDefault();

        if (latestCsv == null)
        {
            player.ChatMessage("[LivingRust] No raw trace found for you - run /lr.debug.tracebuild and place a structure first.");
            return;
        }

        string tierDirectory = $"{BaseDesignsDirectory}/{tier}";

        if (!Directory.Exists(tierDirectory))
        {
            Directory.CreateDirectory(tierDirectory);
        }

        int nextIndex = Directory.GetFiles(tierDirectory, "base*.csv").Length + 1;
        string destinationPath = $"{tierDirectory}/base{nextIndex}.csv";

        File.Copy(latestCsv, destinationPath, overwrite: false);

        player.ChatMessage($"[LivingRust] Saved '{Path.GetFileName(latestCsv)}' as '{tier}/base{nextIndex}.csv' - {nextIndex} design(s) now in the '{tier}' pool.");
        Puts($"basebuild-replay: promoted '{Path.GetFileName(latestCsv)}' to '{destinationPath}'.");
    }

    /// <summary>
    /// Real hardcoded door-route recorder (2026-08-29, seventh round -
    /// Lucas's own explicit fallback after repeated live door/physics
    /// failures: "if we can't fix this in due time, we resort to
    /// hardcoded ghostroutes for entering the bases"). Workflow: stand at
    /// a real live instance of the design you want to record for, run
    /// /lr.debug.traceme, walk through the door exactly like a real
    /// player would (open it, walk through, close it behind you), run
    /// /lr.debug.traceme again to stop recording, then run this. Converts
    /// your just-recorded trace into an offset relative to that build's
    /// own origin, and saves it into that design's own "_doors" folder -
    /// LoadHomeDoorRoutes (BaseBuilding.cs) picks up every file in there
    /// automatically the next time ANY survivor finishes building that
    /// same design. Can be run once per door on a design (front, back,
    /// etc) - each recording becomes its own file, auto-numbered.
    ///
    /// Optional /lr.debug.savedoorroute &lt;tier&gt; &lt;baseN&gt; args
    /// (2026-08-29, real live bug - Lucas's own report: "I just killed
    /// the bot cause they were standing in the way... that doesn't make
    /// sense" after a second recording for base7 silently landed under
    /// base6 instead). Root cause: the original no-args version only ever
    /// searched currently-SPAWNED survivors for one with a home, which
    /// dropped the intended base7 bot from consideration the instant it
    /// died, silently falling through to whatever OTHER nearby bot
    /// happened to have a home (a base6 one) instead - no error, just the
    /// wrong design's own coordinate anchor used for the whole recording.
    /// Passing the design explicitly searches every real Character this
    /// engine knows about (CharacterManager.GetAllCharacters(), dead or
    /// alive - Home persists on the Character regardless of whether its
    /// BasePlayer currently exists) for whichever one's own Home.
    /// SourceDesignPath matches, nearest by Home.Position - the bot being
    /// dead or despawned no longer matters at all. The no-args fallback
    /// below is kept for a quick one-off test where only one home exists
    /// nearby, but naming the design explicitly is the reliable way now.
    /// </summary>
    [ChatCommand("lr.debug.savedoorroute")]
    private void CmdDebugSaveDoorRoute(BasePlayer player, string command, string[] args)
    {
        RunDebugSaveDoorRoute(player, args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null);
    }

    private void RunDebugSaveDoorRoute(BasePlayer? player, string tier = null, string baseName = null)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.savedoorroute requires a real player context (run from in-game chat/console).");
            return;
        }

        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (!Directory.Exists(TraceDirectory))
        {
            player.ChatMessage("[LivingRust] No trace directory found - run /lr.debug.traceme and walk through the door first.");
            return;
        }

        string playerId = player.userID.Get().ToString();
        string latestCsv = Directory.GetFiles(TraceDirectory, $"trace_player_{playerId}_*.csv")
            .OrderByDescending(path => path)
            .FirstOrDefault();

        if (latestCsv == null)
        {
            player.ChatMessage("[LivingRust] No raw movement trace found for you - run /lr.debug.traceme, walk through the door, then run /lr.debug.traceme again to stop.");
            return;
        }

        string[] lines = File.ReadAllLines(latestCsv);

        // Real live bug fix (2026-08-29, eighth round - Lucas's own
        // report: two separate door recordings for the SAME design came
        // out with implausible, inconsistent relative offsets). Root
        // cause: matching "nearest home" against the CALLING PLAYER'S
        // current position is simply the wrong reference point - by the
        // time a player finishes a trace and types this command, they
        // could be standing anywhere, including right next to a
        // DIFFERENT bot that happens to have built the exact same design
        // elsewhere (base7 rolled more than once across a long session is
        // completely normal). The only position that's actually
        // meaningful here is where the RECORDING ITSELF started - matched
        // against that instead, regardless of where the player wandered
        // off to afterward.
        Vector3? traceStartPosition = null;

        for (int i = 1; i < lines.Length && traceStartPosition == null; i++)
        {
            string[] probeColumns = lines[i].Split(',');

            if (probeColumns.Length >= 4
                && float.TryParse(probeColumns[1], out float px)
                && float.TryParse(probeColumns[2], out float py)
                && float.TryParse(probeColumns[3], out float pz))
            {
                traceStartPosition = new Vector3(px, py, pz);
            }
        }

        if (traceStartPosition == null)
        {
            player.ChatMessage("[LivingRust] That trace has no real movement in it - nothing to save.");
            return;
        }

        HomeBase home = null;
        string matchedAlias = null;

        if (tier != null && baseName != null)
        {
            string requiredDesignPath = $"{tier}/{baseName}.csv";
            float nearestDistSqr = float.MaxValue;

            // Every real Character this engine knows about, dead or
            // alive - see this command's own doc comment for why this
            // replaced a live-survivor-only search.
            foreach (Character candidate in _engine.CharacterManager.GetAllCharacters())
            {
                if (candidate.Home == null || !requiredDesignPath.Equals(candidate.Home.SourceDesignPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                float distanceSqr = (candidate.Home.Position - traceStartPosition.Value).sqrMagnitude;

                if (distanceSqr < nearestDistSqr)
                {
                    nearestDistSqr = distanceSqr;
                    home = candidate.Home;
                    matchedAlias = candidate.Alias;
                }
            }

            if (home == null)
            {
                player.ChatMessage($"[LivingRust] No recorded home base found for design '{requiredDesignPath}' - build one first with /lr.debug.replaybuild {tier} {baseName} nolock.");
                return;
            }
        }
        else
        {
            HomeBase nearestHome = null;
            float nearestDistSqr = float.MaxValue;

            foreach (Character candidate in _engine.CharacterManager.GetAllCharacters())
            {
                if (candidate.Home == null || string.IsNullOrEmpty(candidate.Home.SourceDesignPath))
                {
                    continue;
                }

                float distanceSqr = (candidate.Home.Position - traceStartPosition.Value).sqrMagnitude;

                if (distanceSqr < nearestDistSqr)
                {
                    nearestDistSqr = distanceSqr;
                    nearestHome = candidate.Home;
                    matchedAlias = candidate.Alias;
                }
            }

            if (nearestHome == null)
            {
                player.ChatMessage("[LivingRust] No recorded home base found near where your trace started - build one first, or name the design explicitly: /lr.debug.savedoorroute <tier> <baseN>.");
                return;
            }

            home = nearestHome;
        }

        List<string> relativeLines = new() { lines[0] };

        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = lines[i].Split(',');

            if (columns.Length < 4
                || !float.TryParse(columns[1], out float x)
                || !float.TryParse(columns[2], out float y)
                || !float.TryParse(columns[3], out float z))
            {
                continue;
            }

            Vector3 relative = new Vector3(x, y, z) - home.BuildOriginPosition;
            columns[1] = relative.x.ToString("F2");
            columns[2] = relative.y.ToString("F2");
            columns[3] = relative.z.ToString("F2");
            relativeLines.Add(string.Join(",", columns));
        }

        if (relativeLines.Count <= 1)
        {
            player.ChatMessage("[LivingRust] That trace has no real movement in it - nothing to save.");
            return;
        }

        string designDirectory = Path.GetDirectoryName(home.SourceDesignPath)?.Replace('\\', '/') ?? string.Empty;
        string designName = Path.GetFileNameWithoutExtension(home.SourceDesignPath);
        string doorsFolder = $"{BaseDesignsDirectory}/{designDirectory}/{designName}_doors";

        if (!Directory.Exists(doorsFolder))
        {
            Directory.CreateDirectory(doorsFolder);
        }

        int nextIndex = Directory.GetFiles(doorsFolder, "door*.csv").Length + 1;
        string destinationPath = $"{doorsFolder}/door{nextIndex}.csv";

        File.WriteAllLines(destinationPath, relativeLines);

        player.ChatMessage($"[LivingRust] Saved door route as '{destinationPath}' for design '{home.SourceDesignPath}' (recorded against '{matchedAlias}''s own home) - it'll be picked up by every future build of this same design.");
        Puts($"basebuild-replay: saved door route '{destinationPath}' for design '{home.SourceDesignPath}'.");
    }

    /// <summary>
    /// Real bill-of-materials inspector (2026-08-29) - reports
    /// CalculateTraceResourceRequirements's own real total for a saved
    /// design, so the underlying numbers can be checked directly against
    /// what the trace actually contains before any decision logic gets
    /// built on top of them. Usage: /lr.debug.basecost [tier] [baseN] -
    /// tier defaults to DefaultBaseDesignTier, base index defaults to the
    /// first design in that tier's pool.
    /// </summary>
    [ChatCommand("lr.debug.basecost")]
    private void CmdDebugBaseCost(BasePlayer player, string command, string[] args)
    {
        RunDebugBaseCost(player, args);
    }

    private void RunDebugBaseCost(BasePlayer? player, string[] args)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.basecost requires a real player context (run from in-game chat/console).");
            return;
        }

        string tier = args.Length > 0 ? args[0] : DefaultBaseDesignTier;
        string tierDirectory = $"{BaseDesignsDirectory}/{tier}";

        if (!Directory.Exists(tierDirectory))
        {
            player.ChatMessage($"[LivingRust] No '{tier}' base design pool found.");
            return;
        }

        string[] designs = Directory.GetFiles(tierDirectory, "*.csv").OrderBy(path => path).ToArray();

        if (designs.Length == 0)
        {
            player.ChatMessage($"[LivingRust] '{tier}' has no saved designs.");
            return;
        }

        string designPath = args.Length > 1
            ? designs.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(args[1], StringComparison.OrdinalIgnoreCase))
            : designs[0];

        if (designPath == null)
        {
            player.ChatMessage($"[LivingRust] No design named '{args[1]}' in '{tier}' - available: {string.Join(", ", designs.Select(Path.GetFileNameWithoutExtension))}.");
            return;
        }

        Dictionary<string, int> totals = CalculateTraceResourceRequirements(designPath);
        string breakdown = string.Join(", ", totals.OrderByDescending(kvp => kvp.Value).Select(kvp => $"{kvp.Value}x {kvp.Key}"));
        string message = $"'{tier}/{Path.GetFileNameWithoutExtension(designPath)}' real total cost: {breakdown}";
        player.ChatMessage($"[LivingRust] {message}");
        Puts($"basebuild-cost: {message}");
    }

    private void RunDebugReplayBuild(BasePlayer? player, string tier = DefaultBaseDesignTier, string designName = null, bool skipCodeLocks = false)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.replaybuild requires a real player context (run from in-game chat/console).");
            return;
        }

        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor nearestSurvivor = null;
        float nearestDistSqr = float.MaxValue;

        foreach (Survivor candidate in _engine.SurvivorManager.GetAll())
        {
            if (candidate.Player == null || candidate.Player.IsDestroyed)
            {
                continue;
            }

            float distSqr = (candidate.Player.transform.position - player.transform.position).sqrMagnitude;

            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearestSurvivor = candidate;
            }
        }

        if (nearestSurvivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn.basebuilder first.");
            return;
        }

        // Real tier-decision entry point (2026-08-29, Lucas's own explicit
        // ask: "figure out how they decide what tier of base they want to
        // build") - TryChooseAffordableBaseDesign (LivingRust.BaseBuilding.cs)
        // picks the richest tier the survivor's own current inventory can
        // genuinely afford outright, falling back to a random tier0
        // design if nothing anywhere is affordable yet (commit and farm
        // the shortfall, per this project's own already-recorded design
        // call, rather than endlessly searching for something cheap).
        string latestCsv = null;

        if (tier == AutoBaseDesignTier)
        {
            if (TryChooseAffordableBaseDesign(nearestSurvivor, out string autoTier, out string autoDesignPath, out Dictionary<string, int> autoCost))
            {
                tier = autoTier;
                latestCsv = autoDesignPath;
                string costBreakdown = string.Join(", ", autoCost.OrderByDescending(kvp => kvp.Value).Select(kvp => $"{kvp.Value}x {kvp.Key}"));
                Puts($"basebuild-replay: '{nearestSurvivor.Character.Alias}' decided on '{autoTier}/{Path.GetFileNameWithoutExtension(autoDesignPath)}' (real cost: {costBreakdown}).");
            }
        }
        else
        {
            // Real curated-pool roll (2026-08-29, Lucas's own explicit ask -
            // "have it so the bots roll between choosing 1 of the 3 base
            // choices for tier0"). Only ever picks from designs deliberately
            // promoted via /lr.debug.savebasedesign (BaseDesignsDirectory's
            // own doc comment has the full reasoning on why this is separate
            // from the raw TraceDirectory dump) - falls back to the old
            // "newest raw trace for this player" behaviour only if that tier's
            // pool doesn't exist yet or is empty, so this stays backward
            // compatible with testing a trace that hasn't been promoted yet.
            string tierDirectory = $"{BaseDesignsDirectory}/{tier}";

            if (Directory.Exists(tierDirectory))
            {
                string[] tierDesigns = Directory.GetFiles(tierDirectory, "*.csv");

                // Real deterministic pick (2026-08-29, seventh round -
                // Lucas's own explicit ask for the door-route recording
                // workflow: needs to build the SAME exact design on
                // demand, not whatever the dice happens to roll, so a
                // route can be recorded against it). Matches by filename
                // without its extension (e.g. "base3"), case-insensitive.
                if (designName != null)
                {
                    latestCsv = tierDesigns.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(designName, StringComparison.OrdinalIgnoreCase));

                    if (latestCsv == null)
                    {
                        player.ChatMessage($"[LivingRust] No design named '{designName}' in '{tier}' - available: {string.Join(", ", tierDesigns.Select(Path.GetFileNameWithoutExtension))}.");
                        return;
                    }
                }
                else if (tierDesigns.Length > 0)
                {
                    latestCsv = tierDesigns[UnityEngine.Random.Range(0, tierDesigns.Length)];
                    Puts($"basebuild-replay: rolled '{Path.GetFileName(latestCsv)}' out of {tierDesigns.Length} '{tier}' design(s).");
                }
            }
        }

        if (latestCsv == null)
        {
            if (!Directory.Exists(TraceDirectory))
            {
                player.ChatMessage("[LivingRust] No trace directory found - run /lr.debug.tracebuild and place a structure first.");
                return;
            }

            string playerId = player.userID.Get().ToString();
            latestCsv = Directory.GetFiles(TraceDirectory, $"tracebuild_player_{playerId}_*.csv")
                .OrderByDescending(path => path)
                .FirstOrDefault();

            if (latestCsv == null)
            {
                player.ChatMessage($"[LivingRust] No '{tier}' base design(s) saved and no raw trace found for you - run /lr.debug.tracebuild and place a structure first, then /lr.debug.savebasedesign {tier}.");
                return;
            }
        }

        BasePlayer npc = nearestSurvivor.Player;
        string csvFileName = Path.GetFileName(latestCsv);

        // Real live bug (2026-08-29, Lucas's own report: a real trace
        // recording near a road stopped after only 6 rows - the walls
        // never finished) - confirmed via decompile:
        // Construction.TestPlacingCloseToRoad is a REAL placement check
        // (ConstructionErrors.TooCloseToRoad, "Placing too close to
        // road"), real Rust genuinely refuses to place construction within
        // a real topology-based road buffer. Our replay bypasses
        // CanBuild/TestPlacingCloseToRoad entirely (raw CreateEntity, not
        // the real Planner.DoBuild RPC), so without this check the bot
        // would happily "succeed" at building somewhere a real player
        // never could.
        //
        // Folded together with the slope check (a foundation ending up
        // half-swallowed by the ground, IsTooSlopedToBuild's own doc
        // comment) and, as of 2026-08-29 second round, Lucas's own
        // explicit site-selection ask: avoid building inside a real
        // tree/ore node, avoid a large static obstacle (a rock formation,
        // a powerline), and avoid a spot hemmed in by nearby LOS-breaking
        // cover - stepping BuildSiteRelocateDistance further out and
        // re-running the FULL set again if any single check fails, rather
        // than just refusing outright the way the road/slope checks used
        // to on their own (TryFindClearBuildOrigin's own doc comment has
        // the full breakdown).
        if (!TryFindClearBuildOrigin(npc.transform.position, out Vector3 clearOrigin, out float expectedGroundHeight, out string siteFailureReason))
        {
            string siteFailureMessage = $"'{npc.displayName}' couldn't find a clear spot to build within {BuildSiteRelocateMaxAttempts} attempts (last reason: {siteFailureReason}) - move it somewhere more open and try again.";
            player.ChatMessage($"[LivingRust] {siteFailureMessage}");
            Puts($"basebuild-replay: {siteFailureMessage}");
            return;
        }

        // Same "seize control before forcing this" pattern every other
        // debug force command already uses (RunDebugGatherResource/
        // RunDebugCraft's own doc comments) - without this, the survivor's
        // own autonomous loop can silently redirect it mid-replay.
        CancelActiveMovement(nearestSurvivor);
        CancelActiveAttack(nearestSurvivor.Character.Id);
        CancelActiveRecycling(nearestSurvivor.Character.Id);

        float relocatedDistance = (clearOrigin - npc.transform.position).magnitude;
        string relocatedNote = relocatedDistance > 0.5f ? $" (site scan moved the build origin {relocatedDistance:F0}m from where it's standing to clear a road/slope/obstacle)" : string.Empty;
        player.ChatMessage($"[LivingRust] '{npc.displayName}' starting to replay '{csvFileName}'{relocatedNote} - it'll clear any trees/ore within {TreeOreClearCheckRadius:F0}m first, watch the log for 'basebuild-replay' lines.");

        Action<int, int> onReplayComplete = (placed, failed) =>
        {
            string message = $"'{npc.displayName}' finished replaying '{csvFileName}' - {placed} piece(s) placed, {failed} failed.";
            player.ChatMessage($"[LivingRust] {message}");
            Puts($"basebuild-replay: {message}");
        };

        // Real live bug (2026-08-29, Lucas's own explicit ask: "if the bot
        // decides this area isn't good, have it run to the new
        // destination... no phase through walls at all. Only once it
        // starts the build itself"). Site relocation used to just change
        // where the build MATH starts from - the bot never actually
        // walked there, it just phased straight to the first piece the
        // instant building began, silently covering however far the site
        // scan had moved it. A real normal walk makes that leg visible and
        // legible instead - phasing stays reserved for what it was
        // actually built for, moving between pieces of a site the bot is
        // already actively building. Skipped entirely when the original
        // spot was already clear (relocatedDistance basically zero) - no
        // relocation happened, so there's nothing to walk to first.
        //
        // WalkToBuildSiteWithRecovery, not the shared StartWalkingWithRecovery
        // (2026-08-29, second round - Lucas's own report: "bots still be
        // phasing through the floor... ended up in the air" even after the
        // fix above). The shared ladder's own final tier phases through
        // everything unconditionally once wiggle/nudge/emergency-teleport
        // all fail - switching to it only moved WHERE phasing could still
        // happen from, it never actually removed it. This bounded version
        // (LivingRust.BaseBuilding.cs) uses the same real recovery tiers
        // but gives up cleanly instead of ever phasing.
        if (relocatedDistance > 0.5f)
        {
            WalkToBuildSiteWithRecovery(
                nearestSurvivor,
                clearOrigin,
                onArrived: () => ClearBuildSiteThenReplay(nearestSurvivor, clearOrigin, expectedGroundHeight, latestCsv, BuildSiteMaxClearingActions, onReplayComplete, skipCodeLocks),
                onFailed: () =>
                {
                    string failMessage = $"'{npc.displayName}' couldn't walk to the relocated build site - staying put instead of building somewhere it couldn't actually reach.";
                    player.ChatMessage($"[LivingRust] {failMessage}");
                    Puts($"basebuild-replay: {failMessage}");
                });
            return;
        }

        ClearBuildSiteThenReplay(nearestSurvivor, clearOrigin, expectedGroundHeight, latestCsv, BuildSiteMaxClearingActions, onReplayComplete, skipCodeLocks);
    }

    /// <summary>
    /// Sends the nearest spawned survivor along a real recorded
    /// /lr.debug.traceme route (2026-08-16, Lucas's own explicit request -
    /// "I want the bot to take this EXACT path... to noclip from start to
    /// finish to avoid jittering, oscillating etc") - phases straight
    /// through Abandoned Supermarket's keycard room using the real path an
    /// admin already proved works, looting along the way, then hands
    /// control back to normal autonomous behaviour. No filename argument
    /// defaults to the specific trace that recorded doorway -> crate ->
    /// keycard cleanly.
    /// </summary>
    [ChatCommand("lr.debug.ghostroute")]
    private void CmdDebugGhostRoute(BasePlayer player, string command, string[] args)
    {
        RunDebugGhostRoute(player, args.Length > 0 ? args[0] : null);
    }

    [ConsoleCommand("lr.debug.ghostroute")]
    private void CmdDebugGhostRouteConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugGhostRoute(player, arg.HasArgs() ? arg.Args[0].ToString() : null);
        }
    }

    private const string DefaultGhostRouteTraceFile = $"{TraceDirectory}/Supermarket_A/Supermarket_A_Path1.csv";

    private void RunDebugGhostRoute(BasePlayer player, string? traceFileName)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        // With no explicit filename, find the CLOSEST monument that
        // actually has a registered route - not just the closest monument
        // of any type (2026-08-16, Lucas's own explicit fix: "make
        // ghostroute specifically use the closest monument and then roll
        // the dice to whatever path it should take at that monument").
        // TryGetGhostRouteForNearestMonument already searches specifically
        // among registered monuments, so standing near an unregistered
        // monument that's slightly closer than a registered one no longer
        // falls all the way back to the hardcoded default - it still finds
        // the nearer REGISTERED one. resolvedMonument is the exact live
        // instance the roll was made against, reused directly below for
        // projection instead of searching for it a second time.
        string filePath;
        MonumentInfo resolvedMonument = null;

        if (!string.IsNullOrWhiteSpace(traceFileName))
        {
            filePath = $"{TraceDirectory}/{traceFileName}";
        }
        else if (TryGetGhostRouteForNearestMonument(player.transform.position, GhostRouteRecordingMonumentSearchRadius, out string rolledFilePath, out MonumentInfo nearestGhostRouteMonument))
        {
            filePath = rolledFilePath;
            resolvedMonument = nearestGhostRouteMonument;
        }
        else
        {
            filePath = DefaultGhostRouteTraceFile;
        }

        if (!TryLoadTraceWaypoints(filePath, out List<GhostRouteWaypoint> localWaypoints, out MonumentInfo recordedAtMonument, resolvedMonument?.name))
        {
            player.ChatMessage($"[LivingRust] Couldn't load a real waypoint route from '{filePath}' - does that trace file exist?");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        // Re-project onto the real monument instance this route is actually
        // for (2026-08-16 - see TryLoadTraceWaypoints' own doc comment).
        // resolvedMonument (from the dice roll above) is preferred when
        // available - it's the exact instance the file was chosen for, no
        // need to search again. An explicit traceFileName arg has no
        // resolvedMonument, so that path falls back to searching for the
        // nearest instance matching whatever monument the trace was
        // recorded at, and finally to the raw recorded world coordinates
        // unchanged if the trace never localized to a monument at all.
        List<GhostRouteWaypoint> waypoints = localWaypoints;

        if (resolvedMonument != null)
        {
            waypoints = ProjectGhostRouteToMonument(localWaypoints, resolvedMonument);
        }
        else if (recordedAtMonument != null)
        {
            MonumentInfo targetMonument = TryGetNearestMonumentByExactName(player.transform.position, recordedAtMonument.name, GhostRouteRecordingMonumentSearchRadius, out MonumentInfo nearestMatch)
                ? nearestMatch
                : recordedAtMonument;

            waypoints = ProjectGhostRouteToMonument(localWaypoints, targetMonument);
        }

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' running to a {waypoints.Count}-waypoint real recorded route's start from '{filePath}'.");
        Puts($"ghostroute: '{survivor.Character.Alias}' walking to the start of a {waypoints.Count}-waypoint ghost route from '{filePath}'.");

        survivor.Character.CurrentTask = TaskType.LootForResources;

        // Real walk to the route's own first waypoint before phasing
        // through it (2026-08-16 - see EscalateSearchToMonumentZone's
        // identical fix/doc comment: Lucas's own explicit correction, "it
        // actually has to run to the location and then starts the
        // hardcoded path"). This manual debug command shares the exact
        // same StartGhostRoute the real autonomous trigger uses, so it
        // needs the same approach-walk in front of it to actually test
        // the same thing admins will see bots do on their own.
        Action onGhostRouteComplete = () =>
        {
            LootTaskState state = new()
            {
                CommittedMonumentName = null,
            };

            ContinueLootTask(survivor, state);
        };

        // Same pre-registration/loot-scan-start pair EscalateSearchToMonumentZone
        // uses for the real autonomous trigger - see its own doc comments.
        _pendingGhostRouteToResume[survivor.Character.Id] = (waypoints, 0, onGhostRouteComplete);
        StartGhostRouteLootScan(survivor, onGhostRouteComplete);

        StartLongDistanceWalk(
            survivor,
            waypoints[0].Position,
            "ghost route start",
            onArrived: () => StartGhostRoute(survivor, waypoints, 0, onGhostRouteComplete),
            onFailed: () =>
            {
                _pendingGhostRouteToResume.Remove(survivor.Character.Id);
                StopGhostRouteLootScan(survivor.Character.Id);
                player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' couldn't walk to the ghost route's start.");
            });
    }

    /// <summary>
    /// Stops every active trace and closes its file - called from Unload
    /// so a reload mid-trace doesn't leak an open file handle (writes are
    /// already flushed every tick, so this is about the handle, not data
    /// loss).
    /// </summary>
    private void StopAllTraces()
    {
        foreach (Timer traceTimer in _activeTraces.Values)
        {
            traceTimer.Destroy();
        }

        _activeTraces.Clear();

        foreach (StreamWriter writer in _traceWriters.Values)
        {
            writer.Flush();
            writer.Dispose();
        }

        _traceWriters.Clear();

        foreach (Timer traceTimer in _activeNpcTraces.Values)
        {
            traceTimer.Destroy();
        }

        _activeNpcTraces.Clear();

        foreach (StreamWriter writer in _npcTraceWriters.Values)
        {
            writer.Flush();
            writer.Dispose();
        }

        _npcTraceWriters.Clear();

        foreach (Timer traceTimer in _activePlayerTraces.Values)
        {
            traceTimer.Destroy();
        }

        _activePlayerTraces.Clear();

        foreach (StreamWriter writer in _playerTraceWriters.Values)
        {
            writer.Flush();
            writer.Dispose();
        }

        _playerTraceWriters.Clear();
    }

    // Real bots wouldn't all start their loot task in the exact same
    // frame, and a scale test shouldn't manufacture a "thundering herd"
    // (every new bot's first movement/loot-search tick landing together)
    // that wouldn't happen organically - each spawned bot's task start is
    // staggered by a random delay somewhere in this window instead.
    private const float SpawnManyStaggerWindowSeconds = 5f;

    // How long to wait before the NEXT bot spawns, once this one's real
    // beach spawn point turns out to be within SpawnManyContentionRadius
    // of the previous bot's - Lucas's own refinement (2026-08-10) on the
    // original flat 2-3s stagger: only slow down when spawns are actually
    // landing close together (real players connecting near each other
    // would naturally space out a bit more), not uniformly for every
    // spawn regardless of location.
    // Dropped to a flat 0.1s (2026-09-01, Lucas's own explicit ask -
    // "purely for testing") from the old 2-3s window. Worth knowing this
    // reverses a deliberate earlier widening: the original flat fast
    // cadence caused a real burst-spawn freeze bug at 200 bots
    // (near-lockstep spawns landing in the same 1-2s window), later
    // believed fixed via randomized NavMeshAgent.avoidancePriority rather
    // than by spacing spawns out - if that congestion resurfaces at scale
    // with this faster rate, the avoidance-priority fix (not this delay)
    // is the thing to revisit.
    private const float SpawnManyContentionDelayMin = 0.1f;
    private const float SpawnManyContentionDelayMax = 0.1f;

    // How far apart two consecutive spawn points need to be to count as
    // "clear" (Lucas's own range, "3-5 metres" - using the upper bound so
    // contention triggers a little more readily, erring toward realism
    // over speed).
    private const float SpawnManyContentionRadius = 5f;

    // Stagger used when this bot's spawn point ISN'T contested - was a
    // flat 0.5f (2026-08-10, dropped from 1.3f purely to speed up testing
    // iteration), widened to a variable 1-5s range (2026-08-15, Lucas's
    // own explicit request) to test whether a flat, fast, effectively-
    // synchronized spawn cadence was itself a factor in the burst-spawn
    // freeze bug found live this session (a batch of bots landing in the
    // same 1-2s window at 200 bots, not reproduced at 75) - "hopefully
    // alleviate larger congestion." Each bot's own delay is independently
    // rolled, so the whole batch naturally desynchronizes over time
    // instead of marching in lockstep at a fixed interval.
    // Narrowed from 1-5f to 1-3f (2026-08-15, Lucas's own follow-up
    // request) - the crowd-avoidance-deadlock root cause found the same
    // session (randomized NavMeshAgent.avoidancePriority) doesn't actually
    // depend on spawn cadence at all, so there's no need to spread spawns
    // out as wide as 5s; this just keeps a faster test-iteration pace
    // while still avoiding the old flat 0.5s near-lockstep cadence.
    // Also dropped to a flat 0.1s (2026-09-01, same "purely for testing"
    // ask) - see SpawnManyContentionDelayMin's own doc comment for the
    // congestion-bug history this reverses.
    private const float SpawnManyClearDelayMin = 0.1f;
    private const float SpawnManyClearDelayMax = 0.1f;

    /// <summary>
    /// Percent chance (0-100) each /lr.debug.spawnmany survivor spawns at a
    /// real, validated random inland point instead of a real beach spawn -
    /// see the inland-spawn roll's own doc comment at its call site for the
    /// full spec/reasoning.
    /// </summary>
    private const int SpawnManyInlandFraction = 20;

    /// <summary>
    /// Scale-testing tool: spawns N survivors, SpawnManyInlandFraction% of
    /// them at a real, validated random inland point (TryFindRandomInlandSite,
    /// LivingRust.HomeSiteStrategy.cs - not underwater, not inside a
    /// monument's no-build zone) and the rest at real Rust dedicated-spawn
    /// beach locations (ServerMgr.FindSpawnPoint, via SpawnSurvivor's
    /// useBeachSpawnPoint - the same procedural system every real player
    /// actually starts from, not a fixed/static list) - see the inland roll's
    /// own doc comment at its call site (2026-09-01, Lucas's own explicit
    /// ask: hardcode a fraction inland "to maybe aid in them progressing,"
    /// skipping the walk-there step the normal home-site roll would
    /// otherwise need). Every survivor, wherever it lands, goes on a real
    /// LootForResources task immediately (staggered - see
    /// SpawnManyStaggerWindowSeconds), rather than being left idle.
    /// Deliberately does NOT scatter the beach-spawn majority around the
    /// caller the way an earlier version did - Lucas's explicit correction:
    /// for bulk scale testing, bots should start exactly where the server's
    /// own spawn system would actually put a fresh player, to keep the test
    /// semi-realistic.
    /// /lr.spawn itself is untouched (still aimed wherever the caller's
    /// looking) - this only applies to spawnmany. An idle BasePlayer
    /// count alone says nothing about how the server holds up under real
    /// load - the actual cost is in movement ticks, loot-search radius
    /// scans, and the inventory-management passes, so this exists to
    /// reproduce that load on demand instead of manually running
    /// /lr.spawn N times and hand-assigning each one.
    /// </summary>
    [ChatCommand("lr.debug.spawnmany")]
    private void CmdDebugSpawnMany(BasePlayer player, string command, string[] args)
    {
        RunDebugSpawnMany(player, args);
    }

    [ConsoleCommand("lr.debug.spawnmany")]
    private void CmdDebugSpawnManyConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugSpawnMany(player, arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
        }
    }

    private void RunDebugSpawnMany(BasePlayer player, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (args.Length == 0 || !int.TryParse(args[0], out int count) || count <= 0)
        {
            player.ChatMessage("[LivingRust] Usage: /lr.debug.spawnmany <count>");
            return;
        }

        player.ChatMessage($"[LivingRust] Spawning {count} survivor(s) ({SpawnManyInlandFraction}% at random inland sites, the rest at real beach spawn points) - each starts looting shortly after it spawns.");

        StartTestLogCapture($"spawnmany {count}");

        Puts($"spawnmany: spawning {count} survivor(s) ({SpawnManyInlandFraction}% at random inland sites, the rest at real beach spawn points).");

        SpawnManySequential(count, count, null);
    }

    /// <summary>
    /// Spawns one bot, then decides the delay before the NEXT one based on
    /// whether THIS bot's real beach spawn point landed within
    /// SpawnManyContentionRadius of the previous one - contested spawns
    /// get the slower SpawnManyContentionDelayMin-Max window, clear ones
    /// get the fast SpawnManyClearDelaySeconds. Structured as a self-
    /// scheduling chain rather than a flat loop of independent timer.Once
    /// calls, since each delay genuinely depends on a real spawn result
    /// (ServerMgr.FindSpawnPoint) that's only known once the previous bot
    /// has actually spawned - there's no way to precompute the whole
    /// sequence's delays upfront the way the old uniform 2-3s stagger
    /// could.
    /// </summary>
    private void SpawnManySequential(int remaining, int total, Vector3? previousSpawnPosition)
    {
        if (remaining <= 0 || _engine == null)
        {
            return;
        }

        int indexForLog = total - remaining + 1;

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        // Real "hardcode-spawn a fraction inland" ask (2026-09-01, Lucas's
        // own explicit spec: "20% of the bots... instantly spawn somewhere
        // not on 'real' spawn locations, genuinely just hardcode spawn them
        // in random inland locations (not inside monuments)... to maybe aid
        // in them progressing"). Reuses TryFindRandomInlandSite directly
        // (LivingRust.HomeSiteStrategy.cs) - the same real "not underwater,
        // not inside a monument's no-build zone" validated roll the normal
        // home-site strategy already uses for an inland pick, just applied
        // at spawn time instead of as a post-spawn walk target. Falls back
        // to the normal beach spawn if the roll fails to find a valid site
        // at all (matches TryFindRandomInlandSite's own existing caller,
        // which does the same rather than leaving a survivor unplaced).
        Vector3 inlandSite = Vector3.zero;
        bool spawnInland = UnityEngine.Random.Range(0, 100) < SpawnManyInlandFraction && TryFindRandomInlandSite(out inlandSite);

        BasePlayer npc = spawnInland
            ? SpawnSurvivor(survivor, inlandSite, Quaternion.identity, useBeachSpawnPoint: false)
            : SpawnSurvivor(survivor, Vector3.zero, Quaternion.identity, useBeachSpawnPoint: true);

        if (npc == null)
        {
            Puts($"ERROR: spawnmany - failed to spawn survivor '{character.Alias}' ({indexForLog}/{total}).");
            // No valid position to compare against - same variable delay
            // as an ordinary clear spawn, rather than stalling the whole
            // batch on one failure.
            timer.Once(UnityEngine.Random.Range(SpawnManyClearDelayMin, SpawnManyClearDelayMax), () => SpawnManySequential(remaining - 1, total, previousSpawnPosition));
            return;
        }

        Vector3 thisSpawnPosition = npc.transform.position;

        Survivor capturedSurvivor = survivor;
        float taskDelay = UnityEngine.Random.Range(0f, SpawnManyStaggerWindowSeconds);

        timer.Once(taskDelay, () =>
        {
            if (capturedSurvivor.Player != null && !capturedSurvivor.Player.IsDestroyed)
            {
                StartLootForResourcesTask(capturedSurvivor);
            }
        });

        bool contested = previousSpawnPosition.HasValue
            && Vector3.Distance(previousSpawnPosition.Value, thisSpawnPosition) <= SpawnManyContentionRadius;

        float nextDelay = contested
            ? UnityEngine.Random.Range(SpawnManyContentionDelayMin, SpawnManyContentionDelayMax)
            : UnityEngine.Random.Range(SpawnManyClearDelayMin, SpawnManyClearDelayMax);

        timer.Once(nextDelay, () => SpawnManySequential(remaining - 1, total, thisSpawnPosition));
    }

    /// <summary>
    /// Manual full clean-slate wipe - see DespawnAllBots's own doc comment
    /// for exactly what gets erased (every Character record, not just the
    /// live BasePlayer) and what deliberately doesn't change (BotId
    /// numbering never resets back down).
    /// </summary>
    [ChatCommand("lr.debug.despawnall")]
    private void CmdDebugDespawnAll(BasePlayer player, string command, string[] args)
    {
        int count = DespawnAllBots();

        StopTestLogCapture();

        player.ChatMessage($"[LivingRust] Wiped {count} bot(s) - clean slate, nothing left in the roster.");
    }

    [ConsoleCommand("lr.debug.despawnall")]
    private void CmdDebugDespawnAllConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            int count = DespawnAllBots();

            StopTestLogCapture();

            player.ChatMessage($"[LivingRust] Wiped {count} bot(s) - clean slate, nothing left in the roster.");
        }
    }

    /// <summary>
    /// Lists every monument/POI's name, to see what's actually available
    /// to send a survivor to with /lr.walk.monument.
    /// </summary>
    [ChatCommand("lr.monuments")]
    private void CmdListMonuments(BasePlayer player, string command, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        List<string> names = _engine.NavigationManager.GetMonumentNames();

        player.ChatMessage($"[LivingRust] {names.Count} monument(s): {string.Join(", ", names)}");
        Puts($"{names.Count} monument(s): {string.Join(", ", names)}");
    }

    /// <summary>
    /// Reports every monument matching name with its exact coordinates,
    /// ordered nearest-to-you first - useful when a name matches several
    /// instances (e.g. 4 "Substation"s) and it's unclear which one
    /// /lr.walk.monument would actually target.
    /// </summary>
    [ChatCommand("lr.monument.where")]
    private void CmdMonumentWhere(BasePlayer player, string command, string[] args)
    {
        RunMonumentWhere(player, string.Join(" ", args));
    }

    [ConsoleCommand("lr.monument.where")]
    private void CmdMonumentWhereConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunMonumentWhere(player, arg.HasArgs() ? string.Join(" ", arg.Args) : string.Empty);
        }
    }

    private void RunMonumentWhere(BasePlayer player, string name)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            player.ChatMessage("[LivingRust] Usage: /lr.monument.where <name>");
            return;
        }

        List<(string Name, Vector3 Position, float Distance)> matches = _engine.NavigationManager.FindAllMonumentMatches(player.transform.position, name);

        if (matches.Count == 0)
        {
            player.ChatMessage($"[LivingRust] No monument matching '{name}' found.");
            return;
        }

        player.ChatMessage($"[LivingRust] {matches.Count} match(es) for '{name}', nearest first:");

        foreach (var match in matches)
        {
            string message = $"{match.Name} at {match.Position} | {match.Distance:F0}m from you";
            player.ChatMessage(message);
            Puts(message);
        }
    }

    /// <summary>
    /// Teleports the caller directly to whichever bot is currently stuck
    /// in recovery (EscalateStuckRecovery, LivingRust.Looting.cs) - the
    /// live counterpart to eyeballing 200 bots on the map looking for one
    /// that's visibly bugged out. No arg jumps to the worst offender
    /// (longest continuously stuck); an optional 1-based index (from the
    /// list this prints) jumps to a specific one instead when several are
    /// stuck at once. _stuckSince only tracks survivors currently mid-
    /// recovery - once a bot wiggles free, reaches its destination, or
    /// dies, it drops out of this list on its own (see MarkStuck/
    /// ClearStuck's own doc comment).
    /// </summary>
    [ChatCommand("lr.tp.stuck")]
    private void CmdTpStuck(BasePlayer player, string command, string[] args)
    {
        RunTpStuck(player, args.Length > 0 ? args[0] : null);
    }

    [ConsoleCommand("lr.tp.stuck")]
    private void CmdTpStuckConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunTpStuck(player, arg.HasArgs() ? arg.Args[0].ToString() : null);
        }
    }

    private void RunTpStuck(BasePlayer player, string indexArg)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        List<(Survivor Survivor, float StuckSeconds)> stuck = new();

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (_stuckSince.TryGetValue(survivor.Character.Id, out float since)
                && survivor.Player != null && !survivor.Player.IsDestroyed)
            {
                stuck.Add((survivor, Time.realtimeSinceStartup - since));
            }
        }

        if (stuck.Count == 0)
        {
            player.ChatMessage("[LivingRust] No bots currently stuck - nothing to teleport to.");
            return;
        }

        stuck.Sort((a, b) => b.StuckSeconds.CompareTo(a.StuckSeconds));

        int index = 1;

        if (!string.IsNullOrWhiteSpace(indexArg) && !int.TryParse(indexArg, out index))
        {
            index = 1;
        }

        if (index < 1 || index > stuck.Count)
        {
            player.ChatMessage($"[LivingRust] Only {stuck.Count} bot(s) currently stuck - index {index} is out of range.");
            return;
        }

        (Survivor target, float stuckSeconds) = stuck[index - 1];
        Vector3 position = target.Player.transform.position;

        player.Teleport(position);

        string monumentLabel = TryGetNearestMonument(position, AvoidZoneMonumentSearchRadius, out MonumentInfo monument)
            ? monument.name
            : "no monument nearby";

        player.ChatMessage($"[LivingRust] Teleported to '{target.Character.Alias}' - stuck for {stuckSeconds:F0}s near {monumentLabel}.");

        if (stuck.Count > 1)
        {
            List<string> others = new();

            for (int i = 0; i < stuck.Count; i++)
            {
                if (i == index - 1)
                {
                    continue;
                }

                others.Add($"{i + 1}: {stuck[i].Survivor.Character.Alias} ({stuck[i].StuckSeconds:F0}s)");
            }

            player.ChatMessage($"[LivingRust] {stuck.Count - 1} more currently stuck - /lr.tp.stuck <index>: {string.Join(", ", others)}");
        }
    }

    /// <summary>
    /// Real "jump to a specific bot by name" tool (2026-09-07, Lucas's own
    /// explicit ask - a sibling to /lr.tp.stuck for when the bot in
    /// question isn't currently flagged stuck at all, e.g. following one
    /// he's watching in chat/logs). Case-insensitive substring match
    /// against Character.Alias, same "don't require the caller to get
    /// capitalization/the full generated name exactly right" reasoning
    /// most name-driven debug tools in this project already use. If more
    /// than one survivor matches, teleports to the first and lists the
    /// rest so the caller can narrow it down, rather than silently picking
    /// one with no way to know others existed.
    /// </summary>
    [ChatCommand("lr.tp.bot")]
    private void CmdTpBot(BasePlayer player, string command, string[] args)
    {
        RunTpBot(player, string.Join(" ", args));
    }

    [ConsoleCommand("lr.tp.bot")]
    private void CmdTpBotConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunTpBot(player, arg.HasArgs() ? string.Join(" ", arg.Args) : string.Empty);
        }
    }

    private void RunTpBot(BasePlayer player, string nameQuery)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (string.IsNullOrWhiteSpace(nameQuery))
        {
            player.ChatMessage("[LivingRust] Usage: /lr.tp.bot <name or partial name>");
            return;
        }

        List<Survivor> matches = _engine.SurvivorManager.GetAll()
            .Where(survivor => survivor.Player != null
                && !survivor.Player.IsDestroyed
                && survivor.Character.Alias.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (matches.Count == 0)
        {
            player.ChatMessage($"[LivingRust] No live bot matching '{nameQuery}' found.");
            return;
        }

        Survivor target = matches[0];
        player.Teleport(target.Player.transform.position);

        string monumentLabel = TryGetNearestMonument(target.Player.transform.position, AvoidZoneMonumentSearchRadius, out MonumentInfo monument)
            ? monument.name
            : "no monument nearby";

        player.ChatMessage($"[LivingRust] Teleported to '{target.Character.Alias}' near {monumentLabel}.");

        if (matches.Count > 1)
        {
            string others = string.Join(", ", matches.Skip(1).Select(s => s.Character.Alias));
            player.ChatMessage($"[LivingRust] {matches.Count - 1} more match(es) - refine your search to reach them: {others}");
        }
    }

    /// <summary>
    /// Real recipe lookup (2026-08-28) - blueprint ingredient amounts are
    /// baked prefab data, not present anywhere in the decompiled source,
    /// so the only reliable way to get exact numbers for the crafting
    /// system is to ask the live game itself (same "confirm via real data"
    /// approach every other system in this project already follows).
    /// Prints the real ItemBlueprint.GetIngredients() for the given
    /// shortname straight from ItemManager.
    /// </summary>
    /// <summary>
    /// Real leftover-twig scan (2026-09-01, Lucas's own explicit ask: "have
    /// the bots verify somehow if there are any twig walls / foundations
    /// or ceilings still left un-upgraded... I think I missed a few during
    /// the tracebuilds" - a real, confirmed live failure mode this same
    /// session: "couldn't find the piece this upgrade row belongs to -
    /// skipping it" happens whenever a recorded upgrade row's target piece
    /// was destroyed/missing at replay time (Lucas's own explanation: "I
    /// accidentally destroyed a wall that was twig" during one nolock
    /// recording), silently leaving that one piece at Twig grade forever.
    /// Scans every real BuildingBlock this survivor owns (OwnerID match,
    /// same real ownership check /lr.debug.wipeall already uses) within a
    /// generous radius of its home cupboard, reporting any still at
    /// BuildingGrade.Enum.Twigs by real position and piece type so they
    /// can be found and fixed by hand - doesn't touch/upgrade anything
    /// itself, purely diagnostic.
    /// </summary>
    private const float CheckUpgradesSearchRadius = 40f;

    [ChatCommand("lr.debug.checkupgrades")]
    private void CmdDebugCheckUpgrades(BasePlayer player, string command, string[] args)
    {
        RunDebugCheckUpgrades(player, args);
    }

    [ConsoleCommand("lr.debug.checkupgrades")]
    private void CmdDebugCheckUpgradesConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugCheckUpgrades(player, arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
        }
    }

    private void RunDebugCheckUpgrades(BasePlayer player, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        // Real "fix" mode (2026-09-01, Lucas's own follow-up: "is there a
        // way to have it upgraded without redoing the whole tracebuild?").
        // "fix" anywhere in the args (order-independent, same pattern
        // /lr.debug.replaybuild's own "nolock" flag already uses) upgrades
        // every found Twig piece directly via the exact real
        // BuildingBlock.ChangeGrade+SetHealthToMax pair AdvanceBuildReplay's
        // own PlaceUpgradeReplayRow already uses for a normal upgrade row -
        // just aimed at a piece the ORIGINAL build already should have
        // upgraded (a real live bug: "couldn't find the piece this upgrade
        // row belongs to - skipping it"), not a fresh design choice, so no
        // resource cost is deducted here - the survivor already "paid" for
        // this upgrade in spirit the first time, this just finishes it.
        bool fix = args.Any(a => a.Equals("fix", StringComparison.OrdinalIgnoreCase));
        string aliasFilter = args.Where(a => !a.Equals("fix", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

        bool checkAll = string.Equals(aliasFilter, "all", StringComparison.OrdinalIgnoreCase);

        List<Character> targets;

        if (checkAll)
        {
            targets = _engine.CharacterManager.GetAllCharacters().Where(c => c.Home != null).ToList();
        }
        else
        {
            Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
                ? FindNearestSpawnedSurvivor(player.transform.position)
                : FindSpawnedSurvivorByAlias(aliasFilter);

            if (survivor?.Character.Home == null)
            {
                player.ChatMessage("[LivingRust] No survivor with a home base found (nearby, by that alias, or use 'all').");
                return;
            }

            targets = new List<Character> { survivor.Character };
        }

        int totalTwigs = 0;
        int totalFixed = 0;

        foreach (Character character in targets)
        {
            List<BuildingBlock> twigBlocks = new();
            Dictionary<BuildingGrade.Enum, int> gradeCounts = new();

            foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
            {
                if (entity is not BuildingBlock block || block.IsDestroyed || block.OwnerID != character.BotId)
                {
                    continue;
                }

                if ((block.transform.position - character.Home.Position).sqrMagnitude > CheckUpgradesSearchRadius * CheckUpgradesSearchRadius)
                {
                    continue;
                }

                if (block.grade == BuildingGrade.Enum.Twigs)
                {
                    twigBlocks.Add(block);
                }
                else
                {
                    gradeCounts[block.grade] = gradeCounts.GetValueOrDefault(block.grade) + 1;
                }
            }

            if (twigBlocks.Count == 0)
            {
                continue;
            }

            totalTwigs += twigBlocks.Count;

            if (!fix)
            {
                string found = string.Join("; ", twigBlocks.Select(b => $"{b.ShortPrefabName} at {b.transform.position}"));
                player.ChatMessage($"[LivingRust] '{character.Alias}' has {twigBlocks.Count} un-upgraded Twig piece(s): {found}");
                Puts($"checkupgrades: '{character.Alias}' has {twigBlocks.Count} Twig piece(s): {found}");
                continue;
            }

            // Target grade is whatever grade the rest of THIS survivor's
            // own base is mostly built from - the real majority, not a
            // hardcoded assumption, since different tiers/designs upgrade
            // to different top grades.
            BuildingGrade.Enum targetGrade = gradeCounts.Count > 0
                ? gradeCounts.OrderByDescending(kv => kv.Value).First().Key
                : BuildingGrade.Enum.Wood;

            foreach (BuildingBlock block in twigBlocks)
            {
                block.ChangeGrade(targetGrade, playEffect: true);
                block.SetHealthToMax();
                totalFixed++;
            }

            player.ChatMessage($"[LivingRust] '{character.Alias}': upgraded {twigBlocks.Count} leftover Twig piece(s) to {targetGrade} (no cost deducted - this finishes an upgrade the original build already should have done).");
            Puts($"checkupgrades: '{character.Alias}' - fixed {twigBlocks.Count} Twig piece(s) to {targetGrade}.");
        }

        if (totalTwigs == 0)
        {
            player.ChatMessage($"[LivingRust] No leftover Twig pieces found{(checkAll ? " across any surveyed home" : "")}.");
        }
        else if (!fix)
        {
            player.ChatMessage($"[LivingRust] Run '/lr.debug.checkupgrades fix{(checkAll ? " all" : "")}' to upgrade {totalTwigs} found piece(s) directly.");
        }
    }

    [ChatCommand("lr.debug.recipe")]
    private void CmdDebugRecipe(BasePlayer player, string command, string[] args)
    {
        RunDebugRecipe(player, args);
    }

    [ConsoleCommand("lr.debug.recipe")]
    private void CmdDebugRecipeConsole(ConsoleSystem.Arg arg)
    {
        RunDebugRecipe(arg.Player(), arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
    }

    private void RunDebugRecipe(BasePlayer? player, string[] args)
    {
        if (args.Length < 1)
        {
            player?.ChatMessage("[LivingRust] usage: /lr.debug.recipe <shortname>");
            Puts("[LivingRust] usage: lr.debug.recipe <shortname>");
            return;
        }

        ItemDefinition def = ItemManager.FindItemDefinition(args[0]);

        if (def == null)
        {
            string notFound = $"[LivingRust] no item definition found for '{args[0]}'.";
            player?.ChatMessage(notFound);
            Puts(notFound);
            return;
        }

        ItemBlueprint bp = def.Blueprint;

        if (bp == null)
        {
            string noBp = $"[LivingRust] '{args[0]}' has no ItemBlueprint (not craftable).";
            player?.ChatMessage(noBp);
            Puts(noBp);
            return;
        }

        string ingredients = string.Join(", ", bp.GetIngredients().Select(i => $"{i.amount:F0}x {i.itemDef.shortname}"));
        string message = $"[LivingRust] recipe for '{args[0]}': {ingredients} -> {bp.amountToCreate}x {args[0]} (workbench tier {bp.workbenchLevelRequired}, {bp.time:F1}s).";
        player?.ChatMessage(message);
        Puts(message);
    }

    /// <summary>
    /// Real bulk recipe dump (2026-09-01, Lucas's own explicit ask: "find
    /// and resolve all the recipes for basically every craftable item in
    /// the game... this way we don't need to recipefind every item... way
    /// too mandrolic"). Same real ItemBlueprint API RunDebugRecipe already
    /// uses for one item at a time, just iterated across the whole real
    /// ItemManager.itemList - the actual live game data, not a hand-typed
    /// table that could drift out of date or simply be wrong for some
    /// obscure item. Written as CSV (one row per craftable item) to
    /// LivingRust/crafting_recipes.csv, right alongside base_designs/
    /// traces - a permanent, easy-to-read reference for both future dev
    /// work and any future in-code "can I craft this" decision logic.
    /// </summary>
    [ChatCommand("lr.debug.dumprecipes")]
    private void CmdDebugDumpRecipes(BasePlayer player, string command, string[] args)
    {
        RunDebugDumpRecipes(player);
    }

    [ConsoleCommand("lr.debug.dumprecipes")]
    private void CmdDebugDumpRecipesConsole(ConsoleSystem.Arg arg)
    {
        RunDebugDumpRecipes(arg.Player());
    }

    private void RunDebugDumpRecipes(BasePlayer? player)
    {
        const string outputPath = "LivingRust/crafting_recipes.csv";
        Directory.CreateDirectory("LivingRust");

        List<ItemDefinition> allItems = ItemManager.itemList;
        int written = 0;
        int skippedNoBlueprint = 0;

        using (StreamWriter writer = new(outputPath, append: false))
        {
            writer.WriteLine("shortname,displayName,category,workbenchTier,amountToCreate,craftSeconds,userCraftable,unlockedByDefault,ingredients");

            foreach (ItemDefinition def in allItems.OrderBy(d => d.shortname, StringComparer.Ordinal))
            {
                ItemBlueprint bp = def.Blueprint;

                if (bp == null)
                {
                    skippedNoBlueprint++;
                    continue;
                }

                string ingredients = string.Join(";", bp.GetIngredients().Select(i => $"{i.amount:F0}x{i.itemDef.shortname}"));

                writer.WriteLine(string.Join(",",
                    def.shortname,
                    $"\"{def.displayName.english}\"",
                    def.category,
                    bp.workbenchLevelRequired,
                    bp.amountToCreate,
                    bp.time.ToString("F1"),
                    bp.userCraftable,
                    bp.defaultBlueprint,
                    $"\"{ingredients}\""));

                written++;
            }
        }

        string summary = $"[LivingRust] dumped {written} real craftable recipe(s) ({skippedNoBlueprint} item(s) have no blueprint/aren't craftable) to {outputPath}.";
        player?.ChatMessage(summary);
        Puts(summary);
    }

    [ChatCommand("lr.debug.scan")]
    private void CmdDebugScan(BasePlayer player, string command, string[] args)
    {
        RunDebugScan(player, args.Length > 0 && args[0] == "all");
    }

    [ConsoleCommand("lr.debug.scan")]
    private void CmdDebugScanConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugScan(player, arg.HasArgs() && arg.Args[0] == "all");
        }
    }

    /// <summary>
    /// Consolidates every movement/geometry diagnostic into one command -
    /// user-requested, after separately running /lr.debug.look,
    /// /lr.debug.nearby, and /lr.debug.navmeshcheck one at a time to
    /// investigate the same spot got tedious. Runs, in order: a raycast
    /// look (what's directly ahead - "all" dumps every hit along the ray,
    /// not just the nearest solid one), every unique collider within 5m,
    /// navmesh coverage sampling via a nearby survivor's real agent, and a
    /// full replay of TryGetNextStep's own 5-point ground probe one metre
    /// ahead of wherever the player is facing (the actual root-cause tool
    /// built for a live report of widespread false "step too high" blocks
    /// across many different monument dressing props - reports each of
    /// the 5 probe points individually, not just TryGetNextStep's single
    /// winning result, since the working theory is one bad outlier point
    /// silently overriding 4 otherwise-correct ground readings). Replaces
    /// the three standalone commands entirely - use this instead of them
    /// going forward.
    /// </summary>
    private void RunDebugScan(BasePlayer player, bool reportAll)
    {
        Puts($"debug-scan: ==== scan at {player.transform.position}, facing {player.eyes.HeadForward()} ====");

        RunDebugLook(player, reportAll);
        RunDebugNearby(player);
        RunDebugNavMeshCheck(player);
        RunDebugStepProbe(player);

        player.ChatMessage("[LivingRust] Full movement scan complete - check LivingRust_log.txt for everything (look/nearby/navmesh/ground-probe).");
    }

    /// <summary>
    /// The ground-probe piece of /lr.debug.scan - replays
    /// NavigationManager.TryGetNextStep's own 5-point ground-surface probe
    /// one metre ahead of wherever the player is facing, reporting every
    /// individual raycast (hit or not, collider name, height, surface
    /// angle, whether it's in NonSteppableColliderNames) instead of only
    /// the single winning result TryGetNextStep itself would return.
    /// Diagnostic only - probes from the player's own position/facing, not
    /// a bot's, since the raw ground-surface geometry involved doesn't
    /// depend on which BasePlayer is doing the probing.
    /// </summary>
    private void RunDebugStepProbe(BasePlayer player)
    {
        if (_engine == null)
        {
            Puts("debug-scan: engine not running, skipping ground-probe.");
            return;
        }

        Vector3 current = player.transform.position;
        Vector3 forward = player.eyes.BodyForward();
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 target = current + forward;

        List<NavigationManager.GroundProbeResult> probes = _engine.NavigationManager.DebugProbeGroundSurface(current, target);

        Puts($"debug-scan: ground-probe from {current} toward {target} (facing {forward}):");

        foreach (NavigationManager.GroundProbeResult probe in probes)
        {
            if (probe.Hit)
            {
                string excludedNote = probe.ExcludedByName ? ", EXCLUDED by name (NonSteppableColliderNames)" : "";
                Puts($"debug-scan:   offset {probe.Offset} -> HIT '{probe.ColliderName}' at {probe.Point} (height {probe.Point.y:F2}, surface angle {probe.SurfaceAngle:F0} deg{excludedNote}).");
            }
            else
            {
                Puts($"debug-scan:   offset {probe.Offset} -> no hit.");
            }
        }

        StepResult verdict = _engine.NavigationManager.TryGetNextStep(current, target, 1f, out Vector3 nextStep, out string blockReason, ignoreHeadroom: false);
        Puts($"debug-scan: TryGetNextStep verdict={verdict}, nextStep={nextStep}, blockReason={blockReason ?? "none"}.");
    }

    /// <summary>
    /// Reports every unique collider within a radius of the player,
    /// regardless of look direction - sidesteps the whole aim/angle
    /// problem the raycast-based debug.look has, at the cost of not
    /// telling you exactly which specific object you were looking at.
    /// </summary>
    // No longer directly bound to a command - folded into /lr.debug.scan
    // (see above), called from there alongside look/navmeshcheck/the
    // ground-probe diagnostic so a single command captures everything at
    // once instead of running each separately.
    private void RunDebugNearby(BasePlayer player)
    {
        const float radius = 5f;

        Collider[] colliders = Physics.OverlapSphere(player.transform.position, radius, ~0, QueryTriggerInteraction.Collide);

        if (colliders.Length == 0)
        {
            player.ChatMessage($"[LivingRust] Nothing found within {radius}m.");
            return;
        }

        var seenKeys = new HashSet<string>();
        var unique = new List<Collider>();

        foreach (Collider col in colliders)
        {
            string key = $"{col.gameObject.name}|{col.gameObject.layer}";

            if (seenKeys.Add(key))
            {
                unique.Add(col);
            }
        }

        player.ChatMessage($"[LivingRust] {colliders.Length} collider(s) ({unique.Count} unique) within {radius}m:");

        foreach (Collider col in unique)
        {
            // bounds.ClosestPoint (an AABB) rather than transform.position -
            // for a huge collider like the whole terrain mesh or a large
            // trigger zone, transform.position can be its arbitrary local
            // pivot far from the player, making "distance" meaningless.
            Vector3 nearPoint = col.bounds.ClosestPoint(player.transform.position);
            float dist = Vector3.Distance(player.transform.position, nearPoint);
            ReportCollider(player, col, nearPoint, $"trigger: {col.isTrigger} | dist {dist:F1}m");
        }
    }

    [ChatCommand("lr.debug.scientistnav")]
    private void CmdDebugScientistNav(BasePlayer player, string command, string[] args)
    {
        RunDebugScientistNav(player);
    }

    [ConsoleCommand("lr.debug.scientistnav")]
    private void CmdDebugScientistNavConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugScientistNav(player);
        }
    }

    /// <summary>
    /// Reverse-engineers the real, prefab-baked NavMeshAgent/RustNavMeshAgent
    /// parameters a live scientist actually uses - these are Unity-serialized
    /// prefab inspector values (radius, height, baseOffset, agentTypeID,
    /// etc.), not decompilable C# source, so the only way to know them is to
    /// read them off a real instance at runtime. Prefers the general-purpose
    /// "scientist2" archetype specifically (the one that actually roams
    /// in/out of buildings and monuments, not the stationary junkpile/tunnel
    /// guard variants that only ever stand on flat ground) over any other
    /// scientist match, and any scientist over any other NPCPlayer - a
    /// naive "nearest scientist-ish thing" first pass grabbed a
    /// scientistnpc_junkpile_pistol (junkpile-only, never navigates
    /// buildings) instead. Doesn't spawn a temporary one itself, relying on
    /// whatever's already alive on the map (via `spawn scientist2` or a
    /// natural monument spawn), avoiding any guesswork about the correct
    /// prefab path or brain-init side effects a fresh spawn might trigger.
    /// First step toward attaching the same navigation stack to our own
    /// bots instead of the hand-built NavigationManager/local-stepping
    /// system - see the "scientist NPC movement" investigation this
    /// followed from.
    /// </summary>
    private void RunDebugScientistNav(BasePlayer player)
    {
        NPCPlayer best = null;
        int bestRank = int.MaxValue;
        float bestDist = float.MaxValue;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not NPCPlayer npc || npc.IsDestroyed)
            {
                continue;
            }

            float dist = Vector3.Distance(player.transform.position, npc.transform.position);
            int rank = RankScientistPrefab(npc.ShortPrefabName);

            if (rank < bestRank || (rank == bestRank && dist < bestDist))
            {
                bestRank = rank;
                bestDist = dist;
                best = npc;
            }
        }

        NPCPlayer nearest = best;
        float nearestDist = bestDist;

        if (nearest == null)
        {
            player.ChatMessage("[LivingRust] No live NPCPlayer (scientist) found anywhere on the map.");
            return;
        }

        Puts($"debug-scientistnav: nearest NPCPlayer is '{nearest.ShortPrefabName}' ({nearest.name}) at {nearest.transform.position}, {nearestDist:F0}m from '{player.displayName}'.");

        NavMeshAgent unityAgent = nearest.GetComponent<NavMeshAgent>();

        if (unityAgent != null)
        {
            Puts($"debug-scientistnav: NavMeshAgent - agentTypeID={unityAgent.agentTypeID}, radius={unityAgent.radius:F3}, height={unityAgent.height:F3}, baseOffset={unityAgent.baseOffset:F3}, speed={unityAgent.speed:F3}, angularSpeed={unityAgent.angularSpeed:F1}, acceleration={unityAgent.acceleration:F2}, stoppingDistance={unityAgent.stoppingDistance:F2}, autoBraking={unityAgent.autoBraking}, obstacleAvoidanceType={unityAgent.obstacleAvoidanceType}, avoidancePriority={unityAgent.avoidancePriority}, areaMask={unityAgent.areaMask}, autoTraverseOffMeshLink={unityAgent.autoTraverseOffMeshLink}, autoRepath={unityAgent.autoRepath}.");
        }
        else
        {
            Puts("debug-scientistnav: no Unity NavMeshAgent component found on this NPCPlayer.");
        }

        RustNavMeshAgent rustAgent = nearest.GetComponent<RustNavMeshAgent>();

        if (rustAgent != null)
        {
            Puts($"debug-scientistnav: RustNavMeshAgent - agentTypeID={rustAgent.agentTypeID}, areaMask={rustAgent.areaMask}, speed={rustAgent.speed:F3}, angularSpeed={rustAgent.angularSpeed:F1}, acceleration={rustAgent.acceleration:F2}, updatePosition={rustAgent.updatePosition}, updateRotation={rustAgent.updateRotation}, walkSpeed={rustAgent.walkSpeed:F2}, jogSpeed={rustAgent.jogSpeed:F2}, runSpeed={rustAgent.runSpeed:F2}, sprintSpeed={rustAgent.sprintSpeed:F2}, fullSprintSpeed={rustAgent.fullSprintSpeed:F2}, canSteer={rustAgent.canSteer}, maxTurnRadius={rustAgent.maxTurnRadius:F2}, canSwim={rustAgent.canSwim}, swimSpeed={rustAgent.swimSpeed:F2}, preferedTopology={rustAgent.preferedTopology}, preferedBiome={rustAgent.preferedBiome}.");
        }
        else
        {
            Puts("debug-scientistnav: no RustNavMeshAgent component found on this NPCPlayer.");
        }

        BaseNavigator navigator = nearest.GetComponent<BaseNavigator>();

        if (navigator != null)
        {
            Puts($"debug-scientistnav: BaseNavigator type={navigator.GetType().Name}, Moving={navigator.Moving}, HasPath={navigator.HasPath}.");
        }
        else
        {
            Puts("debug-scientistnav: no BaseNavigator component found on this NPCPlayer.");
        }

        player.ChatMessage($"[LivingRust] Logged NavMeshAgent/RustNavMeshAgent/BaseNavigator params for nearest scientist ({nearestDist:F0}m away) - check LivingRust_log.txt.");
    }

    /// <summary>
    /// Lower is better. "scientist2" specifically (the general-purpose
    /// roamer that actually pathfinds in/out of buildings and monuments,
    /// unlike the junkpile/tunnel guard variants that only ever stand on
    /// open ground) beats any other scientist match, which beats any other
    /// NPCPlayer at all.
    /// </summary>
    private int RankScientistPrefab(string shortPrefabName)
    {
        if (shortPrefabName.Contains("scientist2", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (shortPrefabName.Contains("scientist", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    [ChatCommand("lr.debug.avoidzone")]
    private void CmdDebugAvoidZone(BasePlayer player, string command, string[] args)
    {
        RunDebugAvoidZone(player);
    }

    [ConsoleCommand("lr.debug.avoidzone")]
    private void CmdDebugAvoidZoneConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugAvoidZone(player);
        }
    }

    /// <summary>
    /// Manually feeds one "stuck here" report into the monument avoid-zone
    /// system (LivingRust.MonumentAvoidZones.cs) at the caller's own
    /// position - the exact same call PoisonAreaNow makes after a real
    /// full recovery-escalation exhaustion. Lets a specific known-bad
    /// pocket (e.g. inside desert_military_base_d) be confirmed
    /// deterministically by standing on it and running this command twice
    /// (AvoidZoneConfirmThreshold), instead of needing to organically
    /// reproduce a real stuck loot task at that exact spot.
    /// </summary>
    private void RunDebugAvoidZone(BasePlayer player)
    {
        RecordPotentialAvoidZone(player.transform.position);

        if (!TryGetNearestMonument(player.transform.position, AvoidZoneMonumentSearchRadius, out MonumentInfo monument))
        {
            player.ChatMessage($"[LivingRust] No monument within {AvoidZoneMonumentSearchRadius:F0}m of you - nothing recorded.");
            return;
        }

        bool inZone = IsInMonumentAvoidZone(player.transform.position);

        player.ChatMessage($"[LivingRust] Reported a stuck point near '{monument.name}'. Currently standing inside a CONFIRMED avoid zone: {inZone}.");

        if (_monumentAvoidZones.TryGetValue(monument.name, out List<MonumentAvoidZone> zones))
        {
            foreach (MonumentAvoidZone zone in zones)
            {
                string status = zone.ConfirmCount >= AvoidZoneConfirmThreshold ? "CONFIRMED" : "pending";
                Puts($"debug-avoidzone: '{monument.name}' zone at local offset {zone.LocalOffset}, radius {zone.Radius:F0}m, confirmCount={zone.ConfirmCount} ({status}).");
            }
        }
    }

    /// <summary>
    /// No longer directly bound to a command - folded into /lr.debug.scan
    /// (see below).
    ///
    /// Diagnoses why native movement keeps failing to even get a usable
    /// path toward loot-approach destinations, even after snapping them
    /// onto the navmesh (StartWalking's own WalkNativeApproachSnapDistance
    /// fix) - a live test showed that fix made no real difference (71
    /// instant-fail events, same as before). Checks two distinct
    /// possibilities: (1) the live value of AI.useUnityNavmesh - our own
    /// added components assume Unity's classic baked navmesh (the C#
    /// default), but if this server is actually running the newer
    /// runtime/recast independent navmesh instead (what real scientist2
    /// NPCs might really be using), our agents would be looking for
    /// coverage on the wrong system entirely; (2) whether Unity's baked
    /// navmesh has ANY coverage at all near the caller's position for our
    /// specific agentTypeID, at several growing radii - distinguishing
    /// "just needs a bigger snap distance" from "no coverage anywhere
    /// nearby, this whole area was never baked for this agent shape."
    /// </summary>
    private void RunDebugNavMeshCheck(BasePlayer player)
    {
        Puts($"debug-navmeshcheck: AI.useUnityNavmesh={ConVar.AI.useUnityNavmesh}, AI.move={ConVar.AI.move}, AI.logIssues={ConVar.AI.logIssues}.");

        // Only bots get our native components attached - checking the
        // calling player's own GameObject would always come up empty. Uses
        // the nearest spawned survivor's real (or newly-attached) agent
        // instead, so this samples with the exact same agentTypeID/
        // areaMask our production movement actually uses.
        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null || survivor.Player == null || survivor.Player.IsDestroyed)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby to test with - convar values above still logged though.");
            return;
        }

        BasePlayer npc = survivor.Player;
        EnsureNativeNavAgent(npc, out RustNavMeshAgent agent, out NavMeshAgent unityAgent);

        Vector3 origin = player.transform.position;
        float[] radii = { 3f, 6f, 10f, 20f, 40f };

        foreach (float radius in radii)
        {
            bool found = agent.SamplePosition(origin, out NavMeshHit hit, radius);
            Puts($"debug-navmeshcheck: SamplePosition radius {radius:F0}m from {origin} - found={found}" + (found ? $", nearest point {hit.position} ({Vector3.Distance(origin, hit.position):F1}m away)." : "."));
        }

        player.ChatMessage($"[LivingRust] Navmesh coverage check (using '{survivor.Character.Alias}') logged to LivingRust_log.txt.");
    }

    /// <summary>
    /// No longer directly bound to a command - folded into /lr.debug.scan
    /// (see below). Remember: Rust freezes your view/aim angle the instant
    /// chat opens, so /lr.debug.scan run from chat tests whichever
    /// direction you were facing when you pressed Enter/T, not whatever
    /// you turned to look at afterward - use the console version (bindable
    /// to a key, e.g. "bind y lr.debug.scan") for close-range/small
    /// targets, where that distinction actually matters.
    ///
    /// By default reports only the nearest solid (non-trigger) collider
    /// along the view ray - trigger volumes like a vehicle's no-build
    /// zone or a horse's feed trigger get skipped so the real body
    /// collider isn't buried under them. Pass "all" to instead dump every
    /// collider along the ray (trigger and solid alike), for cases where
    /// you need to see the full stack. Falls back to scanning a small
    /// radius around the aim point if the ray hits nothing at all, to
    /// distinguish "wrong layer" from "no collider exists here".
    /// </summary>
    private void RunDebugLook(BasePlayer player, bool reportAll)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, 15f, ~0, QueryTriggerInteraction.Collide);

        if (hits.Length > 0)
        {
            RaycastHit[] ordered = hits.OrderBy(h => h.distance).ToArray();

            if (!reportAll)
            {
                RaycastHit? nearestSolid = ordered.Where(h => !h.collider.isTrigger).Cast<RaycastHit?>().FirstOrDefault();
                RaycastHit chosen = nearestSolid ?? ordered[0];

                ReportCollider(player, chosen.collider, chosen.point, $"trigger: {chosen.collider.isTrigger} | dist {chosen.distance:F1}m");
                return;
            }

            player.ChatMessage($"[LivingRust] {ordered.Length} collider(s) along your view:");

            foreach (RaycastHit hit in ordered)
            {
                ReportCollider(player, hit.collider, hit.point, $"trigger: {hit.collider.isTrigger} | dist {hit.distance:F1}m");
            }

            return;
        }

        Vector3 probeCenter = origin + direction * 2f;
        Collider[] nearby = Physics.OverlapSphere(probeCenter, 1.5f, ~0, QueryTriggerInteraction.Collide);

        if (nearby.Length == 0)
        {
            player.ChatMessage("[LivingRust] Raycast missed, and no colliders found within 1.5m of where you're looking either - that object likely has no collider at all (pure decoration).");
            return;
        }

        player.ChatMessage($"[LivingRust] Raycast missed, but found {nearby.Length} collider(s) nearby:");

        foreach (Collider col in nearby)
        {
            ReportCollider(player, col, col.transform.position, $"trigger: {col.isTrigger}");
        }
    }

    /// <summary>
    /// Reassigns a sleeping bag's ownership to one of our survivors. Rust's
    /// own in-game "assign to friend" UI only lists real Steam friends,
    /// which a disconnected bot's fake userID can never appear in, so
    /// there's no vanilla way to give a bot a bag it can respawn at (see
    /// RespawnSurvivor's doc comment) - this is that missing piece,
    /// exposed as a debug command rather than autonomous behaviour since a
    /// bot can't yet deploy/claim its own bag yet either.
    ///
    /// Aims the same way /lr.debug.look does (raycast from view, small-
    /// radius fallback if it misses) to find the bag; targets the nearest
    /// spawned survivor by default, or a specific one by alias if given
    /// (e.g. "/lr.debug.claimbag AngryBoomer") - useful once several bots
    /// are nearby and "nearest to me" is ambiguous.
    /// </summary>
    [ChatCommand("lr.debug.claimbag")]
    private void CmdDebugClaimBag(BasePlayer player, string command, string[] args)
    {
        RunDebugClaimBag(player, args.Length > 0 ? string.Join(" ", args) : null);
    }

    [ConsoleCommand("lr.debug.claimbag")]
    private void CmdDebugClaimBagConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugClaimBag(player, arg.HasArgs() ? string.Join(" ", arg.Args) : null);
        }
    }

    private void RunDebugClaimBag(BasePlayer player, string aliasFilter)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
            ? FindNearestSpawnedSurvivor(player.transform.position)
            : FindSpawnedSurvivorByAlias(aliasFilter);

        if (survivor == null)
        {
            string message = string.IsNullOrWhiteSpace(aliasFilter)
                ? "No spawned survivor nearby. Use /lr.spawn first."
                : $"No spawned survivor named '{aliasFilter}'.";

            // Puts() too, not just ChatMessage - this command previously had
            // zero server-side trace, making a failed/never-run claim
            // indistinguishable from a working one after the fact.
            player.ChatMessage($"[LivingRust] {message}");
            Puts($"claimbag: {message}");
            return;
        }

        SleepingBag bag = FindLookedAtSleepingBag(player);

        if (bag == null)
        {
            string message = "No sleeping bag found where you're looking (or within 1.5m of it).";
            player.ChatMessage($"[LivingRust] {message}");
            Puts($"claimbag: {message}");
            return;
        }

        bag.OwnerID = survivor.Character.BotId;
        bag.SendNetworkUpdate();

        string confirm = $"Sleeping bag ('{bag.ShortPrefabName}' at {bag.transform.position}) now owned by '{survivor.Character.Alias}' (ID {survivor.Character.BotId}).";
        player.ChatMessage($"[LivingRust] {confirm}");
        Puts($"claimbag: {confirm}");
    }

    /// <summary>
    /// Puts an item directly into a survivor's inventory. Rust's own admin
    /// give-to-player commands (e.g. inventory.giveto) resolve their target
    /// through BasePlayer.Find(), which only searches activePlayerList -
    /// populated exclusively by PlayerInit(Network.Connection), the real
    /// client-connection handshake our disconnected bots never go through.
    /// So no console command targeting a bot by name or userID can ever
    /// find it, regardless of name/ID accuracy or the bot's sleep state -
    /// same "not a real connected player" gap /lr.debug.claimbag already
    /// works around for sleeping bags, via our own Survivor/Character
    /// lookup instead of Rust's.
    /// </summary>
    /// <summary>
    /// Real research-skip test rig (2026-09-01, Lucas's own explicit ask:
    /// "spawn a bot with the items required to craft 3 locked items -
    /// rifle.ak, medical syringe and incendiary 5.56 ammo"). All three are
    /// unlockedByDefault=False (confirmed via /lr.debug.dumprecipes' own
    /// real crafting_recipes.csv) - real Rust ItemCrafter.CanCraft() would
    /// refuse every one of them for a bot that never researched anything,
    /// which is every bot, always. Ingredients here are the exact real
    /// ItemBlueprint.GetIngredients() totals for all three (summed where
    /// they share an ingredient, e.g. metal.fragments in both the syringe
    /// and the incendiary ammo), spawned directly rather than gathered -
    /// this rig exists to test the upcoming "skip CanCraft, verify
    /// workbench+ingredients ourselves" ghost-craft logic in isolation,
    /// not to simulate a real farming path.
    /// </summary>
    private static readonly (string Shortname, int Amount)[] CraftTestIngredients =
    {
        ("metal.refined", 50),
        ("wood", 200),
        ("riflebody", 1),
        ("metalspring", 4),
        ("cloth", 15),
        ("metal.fragments", 20),
        ("lowgradefuel", 10),
        ("gunpowder", 10),
        ("sulfur", 5),
    };

    [ChatCommand("lr.debug.spawncrafttest")]
    private void CmdDebugSpawnCraftTest(BasePlayer player, string command, string[] args)
    {
        RunDebugSpawnCraftTest(player);
    }

    [ConsoleCommand("lr.debug.spawncrafttest")]
    private void CmdDebugSpawnCraftTestConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugSpawnCraftTest(player);
        }
    }

    private void RunDebugSpawnCraftTest(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn survivor '{character.Alias}' for craft test.");
            return;
        }

        List<string> failures = new();

        foreach ((string shortname, int amount) in CraftTestIngredients)
        {
            Item item = ItemManager.CreateByName(shortname, amount);

            if (item == null)
            {
                failures.Add($"unknown item '{shortname}'");
                continue;
            }

            if (!npc.inventory.GiveItem(item))
            {
                item.Remove();
                failures.Add($"no room for {amount}x {shortname}");
            }
        }

        // Real tier3 workbench, deployed right at the survivor's own feet -
        // guarantees the workbench-tier condition is met for all three
        // test items (rifle.ak/incendiary ammo need tier3, the syringe
        // only needs tier2) without this test rig depending on any real
        // Workbench component's own level field, which nothing else in
        // this codebase currently reads either (every existing tier check
        // elsewhere goes by the deployed prefab's own shortname, e.g.
        // "workbench3.deployed" - same approach used here).
        BaseEntity testWorkbench = GameManager.server.CreateEntity("assets/prefabs/deployable/tier 3 workbench/workbench3.deployed.prefab", npc.transform.position, npc.transform.rotation) as BaseEntity;
        testWorkbench?.Spawn();

        npc.SendNetworkUpdateImmediate();

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        string failureNote = failures.Count > 0 ? $" (ingredient issues: {string.Join("; ", failures)})" : "";

        player.ChatMessage($"[LivingRust] Spawned craft-test survivor '{character.Alias}' {where} (ID {character.BotId}).{failureNote} Queuing crafts - watch chat for each one landing.");

        // Real queue (2026-09-01, live report: "it does craft the items,
        // but instantly. That is not good. What happened to craft timers
        // and queues" - fair catch, the first version skipped bp.time
        // entirely). Chained one craft at a time, same as a real crafting
        // queue only ever processing one slot - NOT all three firing in
        // parallel. Ingredients are deducted the moment a craft is
        // accepted (matching real Rust: they leave your inventory when you
        // QUEUE, not when the item finishes), only the crafted item itself
        // is delayed by the recipe's own real bp.time.
        Queue<string> craftQueue = new(new[] { "rifle.ak", "syringe.medical", "ammo.rifle.incendiary" });

        void ProcessNextCraft()
        {
            if (craftQueue.Count == 0)
            {
                return;
            }

            string shortname = craftQueue.Dequeue();

            GhostCraftQueued(npc, shortname, (success, reason) =>
            {
                string result = success ? "crafted" : $"FAILED ({reason})";
                player.ChatMessage($"[LivingRust] craft-test: '{character.Alias}' - {shortname} {result}.");
                Puts($"[LivingRust] craft-test: '{character.Alias}' - {shortname} {result}.");
                ProcessNextCraft();
            });
        }

        ProcessNextCraft();
    }

    /// <summary>
    /// Real "skip Rust's own research-gated CanCraft, but keep the real
    /// craft timer" craft (2026-09-01, live report: instant crafting
    /// "is not good" - fair, real crafting always takes real time even
    /// when nothing's blocking it). Validates the real ItemBlueprint
    /// requirements directly (workbench tier via nearby deployed prefab
    /// shortname, ingredients via real inventory amounts) rather than
    /// calling ItemCrafter.CanCraft(), which would refuse every one of
    /// these since no bot ever goes through Rust's own research flow.
    /// Ingredients deduct immediately on acceptance (matching real Rust
    /// queueing behaviour), then the crafted item itself only appears
    /// after the recipe's own real bp.time via timer.Once - no instant
    /// pop, no fake queue depth beyond what the caller itself chains.
    /// </summary>
    private void GhostCraftQueued(BasePlayer npc, string shortname, Action<bool, string> onComplete)
    {
        ItemDefinition def = ItemManager.FindItemDefinition(shortname);
        ItemBlueprint bp = def?.Blueprint;

        if (bp == null)
        {
            onComplete(false, "no blueprint / not craftable");
            return;
        }

        bool hasWorkbench = Physics.OverlapSphere(npc.transform.position, 10f)
            .Any(hit =>
            {
                BaseEntity entity = hit.GetComponentInParent<BaseEntity>();
                string prefab = entity?.ShortPrefabName ?? string.Empty;

                if (!prefab.StartsWith("workbench") || !prefab.EndsWith(".deployed"))
                {
                    return false;
                }

                return int.TryParse(prefab.Substring("workbench".Length, 1), out int level) && level >= bp.workbenchLevelRequired;
            });

        if (!hasWorkbench)
        {
            onComplete(false, $"no tier{bp.workbenchLevelRequired}+ workbench nearby");
            return;
        }

        List<ItemAmount> ingredients = bp.GetIngredients();

        foreach (ItemAmount ingredient in ingredients)
        {
            if (npc.inventory.GetAmount(ingredient.itemid) < ingredient.amount)
            {
                onComplete(false, $"missing {ingredient.amount:F0}x {ingredient.itemDef.shortname}");
                return;
            }
        }

        foreach (ItemAmount ingredient in ingredients)
        {
            List<Item> collected = new();
            npc.inventory.Take(collected, ingredient.itemid, (int)ingredient.amount);

            foreach (Item taken in collected)
            {
                taken.Remove();
            }
        }

        timer.Once(bp.time, () =>
        {
            if (npc == null || npc.IsDestroyed)
            {
                onComplete(false, "survivor no longer exists when the craft finished");
                return;
            }

            Item crafted = ItemManager.CreateByName(shortname, (int)bp.amountToCreate);

            if (crafted == null || !npc.inventory.GiveItem(crafted))
            {
                crafted?.Remove();
                onComplete(false, "ingredients deducted but couldn't create/give the crafted item");
                return;
            }

            // Real fix for a visual glitch (2026-09-01, live report: "it
            // just instantly reloaded the weapon and swapped it at the
            // same time which caused the invisible weapon bug again") -
            // forcing an IMMEDIATE network update at the exact same tick
            // the bot's own reload/weapon-swap logic reacts to new ammo
            // landing is what collides. Letting the give itself network
            // normally (same as any other inventory change) avoids that
            // same-tick collision.
            onComplete(true, null);
        });
    }

    [ChatCommand("lr.debug.spawndeposittest")]
    private void CmdDebugSpawnDepositTest(BasePlayer player, string command, string[] args)
    {
        RunDebugSpawnDepositTest(player);
    }

    [ConsoleCommand("lr.debug.spawndeposittest")]
    private void CmdDebugSpawnDepositTestConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugSpawnDepositTest(player);
        }
    }

    private void RunDebugSpawnDepositTest(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn survivor '{character.Alias}' for deposit test.");
            return;
        }

        Item rifle = ItemManager.CreateByName("rifle.ak", 1);

        if (rifle == null || !npc.inventory.GiveItem(rifle))
        {
            rifle?.Remove();
            player.ChatMessage("[LivingRust] Failed to give rifle.ak to the deposit-test survivor.");
        }

        // Same known-good prefab path as the deployable placement code
        // (KnownDeployablePrefabPaths in LivingRust.BaseBuilding.cs) -
        // deployed right at the survivor's own feet, same pattern as the
        // craft-test's own test workbench.
        BaseEntity box = GameManager.server.CreateEntity("assets/prefabs/deployable/large wood storage/box.wooden.large.prefab", npc.transform.position, npc.transform.rotation) as BaseEntity;
        box?.Spawn();

        StorageContainer storage = box as StorageContainer;

        if (storage == null || storage.inventory == null)
        {
            player.ChatMessage("[LivingRust] Failed to deploy a large wood box for the deposit test.");
            Puts($"ERROR: box.wooden.large did not spawn as a usable StorageContainer for '{character.Alias}'.");
            return;
        }

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned deposit-test survivor '{character.Alias}' {where} (ID {character.BotId}) with a rifle.ak and a large wood box. Depositing now.");

        int moved = DepositAllItems(npc, storage.inventory);

        player.ChatMessage($"[LivingRust] deposit-test: '{character.Alias}' moved {moved} item stack(s) into the box.");
        Puts($"[LivingRust] deposit-test: '{character.Alias}' moved {moved} item stack(s) into the box.");
    }

    /// <summary>
    /// Simple starting point for real deposit logic (2026-09-01, Lucas's
    /// own explicit ask: "start off simple - have a spawn.ak bot deposit
    /// its items into a large wood box"). Moves every item out of both
    /// main and belt (not wear - a survivor shouldn't strip its own worn
    /// clothing/armor to deposit loot) into the given container. Now just
    /// DepositFilteredItems (LivingRust.Looting.cs, built for the real
    /// production "go home and deposit" trip) with an always-true filter,
    /// so this test rig and real production logic share one transfer loop.
    /// </summary>
    private int DepositAllItems(BasePlayer npc, ItemContainer target)
    {
        return DepositFilteredItems(npc, target, _ => true);
    }

    [ChatCommand("lr.debug.walkdeposittest")]
    private void CmdDebugWalkDepositTest(BasePlayer player, string command, string[] args)
    {
        RunDebugWalkDepositTest(player);
    }

    [ConsoleCommand("lr.debug.walkdeposittest")]
    private void CmdDebugWalkDepositTestConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugWalkDepositTest(player);
        }
    }

    // How far from the player to search for an already-placed box.wooden.
    // large - Lucas's own ask: "can I place a wood box and the bot walks
    // over to it," so this looks for one HE placed rather than spawning
    // its own the way lr.debug.spawndeposittest does.
    private const float WalkDepositTestBoxSearchRadius = 50f;

    private void RunDebugWalkDepositTest(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        StorageContainer nearestBox = null;
        float nearestDist = float.MaxValue;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not StorageContainer container || container.IsDestroyed || container.ShortPrefabName != "box.wooden.large")
            {
                continue;
            }

            float dist = Vector3.Distance(player.transform.position, container.transform.position);

            if (dist <= WalkDepositTestBoxSearchRadius && dist < nearestDist)
            {
                nearestDist = dist;
                nearestBox = container;
            }
        }

        if (nearestBox == null)
        {
            player.ChatMessage($"[LivingRust] No large wood box found within {WalkDepositTestBoxSearchRadius:F0}m - place one nearby first.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn survivor '{character.Alias}' for walk-deposit test.");
            return;
        }

        // Real full kit (2026-09-01, Lucas's own ask: "give the bot a full
        // metal ak kit (same as spawn.ak)") - ApplyKit is the exact same
        // armor+weapon+ammo+magazine logic /lr.spawn.ak itself runs, just
        // applied to this test's own already-created survivor instead of a
        // fresh spawn, so this can't silently drift from the real kit.
        ApplyKit(npc, survivor, "ak");

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned walk-deposit-test survivor '{character.Alias}' {where} (ID {character.BotId}) with a full metal AK kit. Walking {nearestDist:F0}m to the box.");

        // Real stand-off (2026-09-01, Lucas's own ask: "1 metre around the
        // crate to avoid any phasing through the box itself"). Walking
        // straight to the box's own transform.position - a solid object -
        // would end with the survivor overlapping its collider. Approaching
        // from its own current (spawn) direction, same pattern already used
        // for the toolcupboard approach in GhostEnterHomeForDeposit, keeps a
        // real gap without needing to know which side the box faces.
        const float BoxStandOffDistance = 1f;
        Vector3 approachDirection = spawnPosition - nearestBox.transform.position;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude < 0.01f)
        {
            approachDirection = nearestBox.transform.forward;
        }

        Vector3 approachPoint = nearestBox.transform.position + approachDirection.normalized * BoxStandOffDistance;
        approachPoint.y = nearestBox.transform.position.y;

        StartWalkingWithRecovery(survivor, approachPoint, onArrived: () =>
        {
            BasePlayer arrivedNpc = survivor.Player;

            if (arrivedNpc == null || arrivedNpc.IsDestroyed || nearestBox == null || nearestBox.IsDestroyed || nearestBox.inventory == null)
            {
                player.ChatMessage($"[LivingRust] walk-deposit-test: '{character.Alias}' arrived but the survivor or box no longer exists.");
                return;
            }

            int moved = DepositAllItems(arrivedNpc, nearestBox.inventory);
            player.ChatMessage($"[LivingRust] walk-deposit-test: '{character.Alias}' arrived and moved {moved} item stack(s) into the box.");
            Puts($"[LivingRust] walk-deposit-test: '{character.Alias}' arrived and moved {moved} item stack(s) into the box.");
        },
        onFailed: () =>
        {
            player.ChatMessage($"[LivingRust] walk-deposit-test: '{character.Alias}' failed to reach the box.");
        });
    }

    // 1000x each (2026-09-01, Lucas's own explicit ask) - real stack sizes
    // on this server comfortably hold this (the existing recipe dump
    // already confirmed 1000x wood alone for cupboard.tool, so wood's own
    // max stack is at least that high; metal.ore/sulfur.ore use the same
    // server-wide stack multiplier).
    private static readonly (string Shortname, int Amount)[] SmeltTestIngredients =
    {
        ("wood", 1000),
        ("metal.ore", 1000),
        ("sulfur.ore", 1000),
    };

    [ChatCommand("lr.debug.walksmelttest")]
    private void CmdDebugWalkSmeltTest(BasePlayer player, string command, string[] args)
    {
        RunDebugWalkSmeltTest(player);
    }

    [ConsoleCommand("lr.debug.walksmelttest")]
    private void CmdDebugWalkSmeltTestConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugWalkSmeltTest(player);
        }
    }

    // Same reasoning as WalkDepositTestBoxSearchRadius (LivingRust.Debug.cs) -
    // looks for a furnace Lucas already placed rather than spawning its own.
    private const float WalkSmeltTestFurnaceSearchRadius = 50f;

    // Same 1m stand-off as the box walk-deposit test (2026-09-01, Lucas's
    // own ask: "give it a 1m stand off similar to the chest deposit test") -
    // avoids phasing/spawning inside the furnace's own collider.
    private const float FurnaceStandOffDistance = 1f;

    /// <summary>
    /// First real smelting test (2026-09-01, Lucas's own explicit ask:
    /// "deposit wood, metal ore and sulfur ore into a furnace... and
    /// ignite the furnace to initiate the smelting process") - this
    /// project had NO smelting system at all before this (see
    /// LivingRust.Crafting.cs's own doc comment on metal.fragments having
    /// "no active gathering source... no furnace/smelting system exists").
    /// Confirmed via decompiling BaseOven (the small furnace prefab's real
    /// component, "Furnace" isn't a distinct class) that StartCooking() is
    /// the real, direct, player-RPC-free ignition call Rust's own SVSwitch
    /// RPC uses internally - it just needs FindBurnable() to find real
    /// fuel (wood) already sitting in the oven's inventory first, which is
    /// why items are deposited BEFORE StartCooking() runs, not after.
    /// Reuses DepositAllItems (already generic over any ItemContainer, not
    /// box-specific despite being built for the box test) rather than a
    /// second hand-rolled transfer loop. Walks to an already-placed furnace
    /// instead of spawning its own at its feet (2026-09-01, Lucas's own
    /// follow-up: "have it be a walk deposit test, so the bot doesn't spawn
    /// inside the furnace and appear stuck") - same
    /// find-nearest-then-StartWalkingWithRecovery pattern as
    /// RunDebugWalkDepositTest.
    /// </summary>
    private void RunDebugWalkSmeltTest(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        BaseOven nearestFurnace = null;
        float nearestDist = float.MaxValue;

        foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
        {
            if (entity is not BaseOven oven || oven.IsDestroyed || oven.ShortPrefabName != "furnace")
            {
                continue;
            }

            float dist = Vector3.Distance(player.transform.position, oven.transform.position);

            if (dist <= WalkSmeltTestFurnaceSearchRadius && dist < nearestDist)
            {
                nearestDist = dist;
                nearestFurnace = oven;
            }
        }

        if (nearestFurnace == null)
        {
            player.ChatMessage($"[LivingRust] No furnace found within {WalkSmeltTestFurnaceSearchRadius:F0}m - place one nearby first.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn survivor '{character.Alias}' for smelt test.");
            return;
        }

        List<string> failures = new();

        foreach ((string shortname, int amount) in SmeltTestIngredients)
        {
            Item item = ItemManager.CreateByName(shortname, amount);

            if (item == null || !npc.inventory.GiveItem(item))
            {
                item?.Remove();
                failures.Add($"{amount}x {shortname}");
            }
        }

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        string failureNote = failures.Count > 0 ? $" (failed to give: {string.Join(", ", failures)})" : "";
        player.ChatMessage($"[LivingRust] Spawned smelt-test survivor '{character.Alias}' {where} (ID {character.BotId}) with 1000x wood/metal.ore/sulfur.ore.{failureNote} Walking {nearestDist:F0}m to the furnace.");

        // Same offset-approach pattern as the box walk-deposit test - walks
        // to a point 1m out from the furnace along its own spawn direction,
        // never straight to the furnace's own transform.position.
        Vector3 approachDirection = spawnPosition - nearestFurnace.transform.position;
        approachDirection.y = 0f;

        if (approachDirection.sqrMagnitude < 0.01f)
        {
            approachDirection = nearestFurnace.transform.forward;
        }

        Vector3 approachPoint = nearestFurnace.transform.position + approachDirection.normalized * FurnaceStandOffDistance;
        approachPoint.y = nearestFurnace.transform.position.y;

        StartWalkingWithRecovery(survivor, approachPoint, onArrived: () =>
        {
            BasePlayer arrivedNpc = survivor.Player;

            if (arrivedNpc == null || arrivedNpc.IsDestroyed || nearestFurnace == null || nearestFurnace.IsDestroyed || nearestFurnace.inventory == null)
            {
                player.ChatMessage($"[LivingRust] smelt-test: '{character.Alias}' arrived but the survivor or furnace no longer exists.");
                return;
            }

            int moved = DepositAllItems(arrivedNpc, nearestFurnace.inventory);
            nearestFurnace.StartCooking();

            bool isOn = nearestFurnace.IsOn();
            string ignitionResult = isOn ? "successfully ignited it" : "FAILED to ignite it (no burnable fuel found in the oven?)";

            player.ChatMessage($"[LivingRust] smelt-test: '{character.Alias}' arrived, moved {moved} item stack(s) into the furnace and {ignitionResult}.");
            Puts($"[LivingRust] smelt-test: '{character.Alias}' arrived, moved {moved} item stack(s) into the furnace, IsOn={isOn}.");
        },
        onFailed: () =>
        {
            player.ChatMessage($"[LivingRust] smelt-test: '{character.Alias}' failed to reach the furnace.");
        });
    }

    [ChatCommand("lr.debug.giveitem")]
    private void CmdDebugGiveItem(BasePlayer player, string command, string[] args)
    {
        RunDebugGiveItem(player, args);
    }

    [ConsoleCommand("lr.debug.giveitem")]
    private void CmdDebugGiveItemConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player == null)
        {
            return;
        }

        string[] args = arg.HasArgs()
            ? arg.Args.Select(a => a.ToString()).ToArray()
            : new string[0];

        RunDebugGiveItem(player, args);
    }

    /// <summary>
    /// Args: &lt;shortname&gt; [amount] [alias...] - amount defaults to 1
    /// and is only consumed from args[1] if it actually parses as a
    /// number, so "/lr.debug.giveitem torch AngryBoomer" (no amount, just
    /// an alias) still works without requiring a redundant "1".
    /// </summary>
    private void RunDebugGiveItem(BasePlayer player, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (args.Length == 0)
        {
            player.ChatMessage("[LivingRust] Usage: /lr.debug.giveitem <shortname> [amount] [alias]");
            return;
        }

        string shortname = args[0];
        int amount = 1;
        int aliasStartIndex = 1;

        if (args.Length > 1 && int.TryParse(args[1], out int parsedAmount))
        {
            amount = Mathf.Max(1, parsedAmount);
            aliasStartIndex = 2;
        }

        string aliasFilter = args.Length > aliasStartIndex
            ? string.Join(" ", args.Skip(aliasStartIndex))
            : null;

        Survivor survivor = string.IsNullOrWhiteSpace(aliasFilter)
            ? FindNearestSpawnedSurvivor(player.transform.position)
            : FindSpawnedSurvivorByAlias(aliasFilter);

        if (survivor == null)
        {
            string message = string.IsNullOrWhiteSpace(aliasFilter)
                ? "No spawned survivor nearby. Use /lr.spawn first."
                : $"No spawned survivor named '{aliasFilter}'.";

            player.ChatMessage($"[LivingRust] {message}");
            Puts($"giveitem: {message}");
            return;
        }

        Item item = ItemManager.CreateByName(shortname, amount);

        if (item == null)
        {
            string message = $"Unknown item shortname '{shortname}'.";
            player.ChatMessage($"[LivingRust] {message}");
            Puts($"giveitem: {message}");
            return;
        }

        BasePlayer npc = survivor.Player;

        if (!npc.inventory.GiveItem(item))
        {
            item.Remove();

            string failMessage = $"'{survivor.Character.Alias}' has no room for '{shortname}'.";
            player.ChatMessage($"[LivingRust] {failMessage}");
            Puts($"giveitem: {failMessage}");
            return;
        }

        // Same "no live refresh path" reason SpawnSurvivor/RestoreSpawnedSurvivors
        // already push an immediate update for - without this an
        // already-nearby client wouldn't see the new item show up until
        // something else happened to trigger a network update.
        npc.SendNetworkUpdateImmediate();

        string confirm = $"Gave {amount}x '{shortname}' to '{survivor.Character.Alias}' (ID {survivor.Character.BotId}).";
        player.ChatMessage($"[LivingRust] {confirm}");
        Puts($"giveitem: {confirm}");
    }

    [ChatCommand("lr.debug.spawncardtest")]
    private void CmdDebugSpawnCardTest(BasePlayer player, string command, string[] args)
    {
        RunDebugSpawnCardTest(player);
    }

    [ConsoleCommand("lr.debug.spawncardtest")]
    private void CmdDebugSpawnCardTestConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugSpawnCardTest(player);
        }
    }

    /// <summary>
    /// Spawns a fresh survivor already carrying everything a card-puzzle
    /// detour needs (see LivingRust.CardPuzzles.cs) - one fuse and one of
    /// each keycard tier - so testing TryStartCardPuzzleDetour doesn't
    /// require looting a real fuse/card off a corpse first every time
    /// (2026-08-18, Lucas's own explicit request: "will save me having the
    /// bot looting it along the way"). Same spawn-at-aim-point placement as
    /// /lr.spawn, just also gives items immediately after.
    ///
    /// Also kitted with the same AK loadout /lr.spawn.ak gives (2026-08-21,
    /// Lucas's own explicit request) - full metal armor, an AK + 2 spare
    /// mags of ammo.rifle, medical supplies, reloaded and equipped for
    /// display - same SpawnKits["ak"] entry and the same
    /// EquipKitArmor/GiveItem sequence SpawnKitAt uses, just applied to
    /// this already-spawned survivor instead of spawning a second one.
    /// Keeps this bot armed and armored for a real end-to-end elevator/
    /// puzzle test without a separate manual kit-up step.
    /// </summary>
    private void RunDebugSpawnCardTest(BasePlayer player)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);

        BasePlayer npc = SpawnSurvivor(survivor, spawnPosition, player.transform.rotation);

        if (npc == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn survivor.");
            Puts($"ERROR: Failed to spawn survivor '{character.Alias}' for spawncardtest.");
            return;
        }

        npc.InitializeHealth(npc.MaxHealth(), npc.MaxHealth());

        if (npc.IsWounded())
        {
            npc.StopWounded();
        }

        EquipKitArmor(npc, KitHoodieShortname);
        EquipKitArmor(npc, KitPantsShortname);
        EquipKitArmor(npc, KitBootsShortname);
        EquipKitArmor(npc, KitFacemaskShortname);
        EquipKitArmor(npc, KitChestplateShortname);
        EquipKitArmor(npc, KitRoadsignKiltShortname);

        WeaponKit akKit = SpawnKits["ak"];

        GiveItem(npc, akKit.WeaponShortname, 1);

        ItemDefinition ammoDefinition = ItemManager.FindItemDefinition(akKit.AmmoShortname);
        int ammoStackSize = ammoDefinition != null ? ammoDefinition.stackable : 128;
        GiveItem(npc, akKit.AmmoShortname, ammoStackSize);
        GiveItem(npc, akKit.AmmoShortname, ammoStackSize);

        GiveItem(npc, KitSyringeShortname, KitSyringeCount);
        GiveItem(npc, KitBandageShortname, KitBandageCount);

        EquipBestWeaponForDisplay(survivor);

        if (npc.GetHeldEntity() is BaseProjectile spawnedWeapon)
        {
            spawnedWeapon.ServerTryReload(npc.inventory);
        }

        string[] shortnames = { "fuse", "keycard_green", "keycard_blue", "keycard_red" };

        foreach (string shortname in shortnames)
        {
            Item item = ItemManager.CreateByName(shortname, 1);

            if (item == null || !npc.inventory.GiveItem(item))
            {
                item?.Remove();
                Puts($"spawncardtest: couldn't give '{shortname}' to '{character.Alias}' - no room or unknown shortname.");
            }
        }

        npc.SendNetworkUpdateImmediate();

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned survivor '{character.Alias}' {where} with an AK kit, a fuse, and one of each keycard. (ID {character.BotId})");
        Puts($"spawncardtest: spawned '{character.Alias}' with an AK kit, a fuse, and one of each keycard.");
    }

    [ChatCommand("lr.debug.gotomonument")]
    private void CmdDebugGotoMonument(BasePlayer player, string command, string[] args)
    {
        RunDebugGotoMonument(player, string.Join(" ", args));
    }

    [ConsoleCommand("lr.debug.gotomonument")]
    private void CmdDebugGotoMonumentConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugGotoMonument(player, arg.HasArgs() ? string.Join(" ", arg.Args) : string.Empty);
        }
    }

    /// <summary>
    /// Unlike /lr.walk.monument (a raw StartWalking with no onArrived at
    /// all - it just walks there and stops), this actually hands the
    /// survivor off to the real loot-task escalation ladder
    /// (EscalateSearchToMonumentZone) once it arrives - the exact same
    /// function TryStartCardPuzzleDetour is checked from
    /// (LivingRust.CardPuzzles.cs). 2026-08-18, Lucas's own explicit
    /// problem: /lr.debug.settask rolls a random gear-weighted destination
    /// that has no idea a specific survivor is carrying a fuse+card for a
    /// specific monument's puzzle, so testing it meant hoping the dice
    /// roll happened to send the bot toward harbor_2 at all. This forces
    /// the destination directly, then lets the real decision logic
    /// (including the card-puzzle check) run exactly as it would if the
    /// survivor had wandered there on its own.
    /// </summary>
    private void RunDebugGotoMonument(BasePlayer player, string name)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            player.ChatMessage("[LivingRust] Usage: /lr.debug.gotomonument <name> - see /lr.monuments for the list.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = survivor.Player;

        MonumentInfo monument = null;
        float bestDistanceSqr = float.MaxValue;

        foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
        {
            string displayName = candidate.displayPhrase.IsValid() ? candidate.displayPhrase.english : null;

            bool matches = (displayName != null && displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                || candidate.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;

            if (!matches)
            {
                continue;
            }

            float distanceSqr = (candidate.transform.position - npc.transform.position).sqrMagnitude;

            if (distanceSqr < bestDistanceSqr)
            {
                bestDistanceSqr = distanceSqr;
                monument = candidate;
            }
        }

        if (monument == null)
        {
            player.ChatMessage($"[LivingRust] No monument matching '{name}' found. See /lr.monuments for the list.");
            return;
        }

        Vector3 destination = monument.ClosestPointOnBounds(npc.transform.position);
        destination.y = TerrainMeta.HeightMap.GetHeight(destination);

        survivor.Character.CurrentTask = TaskType.LootForResources;

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' is heading to '{monument.name}' - will run the real loot-task decision logic on arrival.");

        StartWalkingWithRecovery(
            survivor,
            destination,
            onArrived: () => EscalateSearchToMonumentZone(survivor, new LootTaskState()),
            onFailed: () => player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' couldn't reach '{monument.name}'."));
    }

    [ChatCommand("lr.debug.testcardpuzzle")]
    private void CmdDebugTestCardPuzzle(BasePlayer player, string command, string[] args)
    {
        RunDebugTestCardPuzzle(player, string.Join(" ", args));
    }

    [ConsoleCommand("lr.debug.testcardpuzzle")]
    private void CmdDebugTestCardPuzzleConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugTestCardPuzzle(player, arg.HasArgs() ? string.Join(" ", arg.Args) : string.Empty);
        }
    }

    /// <summary>
    /// Teleports the nearest spawned survivor straight to a registered
    /// card-puzzle route's own first waypoint and runs it immediately - see
    /// TryTeleportToCardPuzzle's own doc comment (LivingRust.CardPuzzles.cs)
    /// for why this skips the real approach walk entirely. name, if given,
    /// substring-matches a specific monument the same way /lr.walk.monument
    /// does; omitted (2026-08-18, real live bug fix - this used to
    /// hardcode "harbor_2" as the default, a leftover from when that was
    /// the only registered puzzle, so it kept firing on Harbor2 regardless
    /// of where the admin actually was) finds whichever REGISTERED puzzle
    /// monument (any key in CardPuzzleRouteFolders) is nearest the caller,
    /// same "closest registered one, not just closest of any type" pattern
    /// TryGetGhostRouteForNearestMonument already uses for ghost routes.
    /// Combine with /lr.debug.spawncardtest first if the survivor isn't
    /// already carrying a fuse + the right keycard(s).
    /// </summary>
    private void RunDebugTestCardPuzzle(BasePlayer player, string name)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        Survivor survivor = FindNearestSpawnedSurvivor(player.transform.position);

        if (survivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.debug.spawncardtest first.");
            return;
        }

        BasePlayer npc = survivor.Player;
        MonumentInfo monument = null;
        float bestDistanceSqr = float.MaxValue;

        if (string.IsNullOrWhiteSpace(name))
        {
            foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
            {
                if (!CardPuzzleRouteFolders.Keys.Any(key => candidate.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - npc.transform.position).sqrMagnitude;

                if (distanceSqr < bestDistanceSqr)
                {
                    bestDistanceSqr = distanceSqr;
                    monument = candidate;
                }
            }

            if (monument == null)
            {
                player.ChatMessage("[LivingRust] No registered card-puzzle monument found anywhere on the map.");
                return;
            }
        }
        else
        {
            foreach (MonumentInfo candidate in TerrainMeta.Path.Monuments)
            {
                if (candidate.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                float distanceSqr = (candidate.transform.position - npc.transform.position).sqrMagnitude;

                if (distanceSqr < bestDistanceSqr)
                {
                    bestDistanceSqr = distanceSqr;
                    monument = candidate;
                }
            }

            if (monument == null)
            {
                player.ChatMessage($"[LivingRust] No monument matching '{name}' found.");
                return;
            }
        }

        if (!TryTeleportToCardPuzzle(survivor, monument, out string failureReason))
        {
            player.ChatMessage($"[LivingRust] Couldn't start the card puzzle at '{monument.name}': {failureReason}");
            return;
        }

        player.ChatMessage($"[LivingRust] '{survivor.Character.Alias}' teleported to '{monument.name}''s card puzzle and is running it now.");
    }

    private SleepingBag FindLookedAtSleepingBag(BasePlayer player)
    {
        Vector3 origin = player.eyes.position;
        Vector3 direction = player.eyes.HeadForward();

        RaycastHit[] hits = Physics.RaycastAll(origin, direction, 15f, ~0, QueryTriggerInteraction.Collide);

        foreach (RaycastHit hit in hits.OrderBy(h => h.distance))
        {
            SleepingBag hitBag = hit.collider.GetComponentInParent<SleepingBag>();

            if (hitBag != null)
            {
                return hitBag;
            }
        }

        Vector3 probeCenter = origin + direction * 2f;
        Collider[] nearby = Physics.OverlapSphere(probeCenter, 1.5f, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider col in nearby)
        {
            SleepingBag nearbyBag = col.GetComponentInParent<SleepingBag>();

            if (nearbyBag != null)
            {
                return nearbyBag;
            }
        }

        return null;
    }

    private Survivor FindSpawnedSurvivorByAlias(string alias)
    {
        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (survivor.Spawned && survivor.Player != null && !survivor.Player.IsDestroyed
                && string.Equals(survivor.Character.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return survivor;
            }
        }

        return null;
    }

    private void ReportCollider(BasePlayer player, Collider col, Vector3 pos, string extra)
    {
        GameObject go = col.gameObject;
        BaseEntity entity = col.GetComponentInParent<BaseEntity>();

        string layerName = LayerMask.LayerToName(go.layer);
        string entityInfo = (entity != null) ? $"{entity.GetType().Name} ('{entity.ShortPrefabName}')" : "no BaseEntity";

        // userID shown for anything BasePlayer-derived - covers real
        // players, our own bots, AND Rust's own NPCPlayer-family NPCs
        // (ScientistNPC, HumanNPC, etc. all derive from BasePlayer and
        // carry a real userID field, confirmed via reflection) - added to
        // check whether our BotId range could ever collide with whatever
        // ID a vanilla scientist NPC gets, after a report of a Bradley-
        // spawned scientist's loot bag showing a LivingRust bot's name.
        if (entity is BasePlayer entityPlayer)
        {
            entityInfo += $" | userID {entityPlayer.userID} | displayName '{entityPlayer.displayName}'";
        }

        // Real dropped-item shortname (2026-08-29) - a DroppedItem's own
        // ShortPrefabName is a generic "generic_world" wrapper prefab
        // regardless of what's actually inside it (confirmed live -
        // Lucas scanned two visually different items that both reported
        // 'generic_world'), so the ACTUAL real item identity only shows
        // up via WorldItem.item.info.shortname, the real Item instance
        // it's carrying.
        if (entity is WorldItem worldItem && worldItem.item != null)
        {
            entityInfo += $" | item.shortname '{worldItem.item.info.shortname}'";
        }

        // Bounds/components are the two things we actually need to figure out
        // ladder climbing: whether Rust already exposes real mount/top/bottom
        // points via some component on the trigger (rather than us deriving a
        // climb line from raw collider geometry), and what the true world-space
        // extent of the ladder collider is if we end up doing it ourselves.
        Bounds bounds = col.bounds;
        string boundsInfo = $"bounds min {bounds.min} max {bounds.max} size {bounds.size}";

        string ownComponents = string.Join(", ", go.GetComponents<Component>().Select(c => c.GetType().Name));
        string parentComponents = go.transform.parent != null
            ? string.Join(", ", go.transform.parent.gameObject.GetComponents<Component>().Select(c => c.GetType().Name))
            : "no parent";

        string message = $"Hit '{go.name}' | layer {go.layer} ({layerName}) | entity: {entityInfo} | pos {pos} | {extra} | {boundsInfo} | components: [{ownComponents}] | parent '{go.transform.parent?.name}' components: [{parentComponents}]";

        player.ChatMessage($"[LivingRust] {message}");

        // Puts() is Carbon's own logging call - proven to actually reach the
        // server console log, unlike our custom Logger.Info's plain
        // Console.WriteLine, which turned out not to be captured at all.
        Puts(message);
    }

    /// <summary>
    /// Verifies TryFindCoverPoint (LivingRust.Combat.cs) against real map
    /// terrain before anything else depends on it - treats the calling
    /// admin as the "attacker" and the nearest spawned survivor as the one
    /// looking for cover, exactly the same real geometry-driven search a
    /// live combat decision would run.
    /// </summary>
    [ChatCommand("lr.debug.findcover")]
    private void CmdDebugFindCover(BasePlayer player, string command, string[] args)
    {
        RunDebugFindCover(player);
    }

    [ConsoleCommand("lr.debug.findcover")]
    private void CmdDebugFindCoverConsole(ConsoleSystem.Arg arg)
    {
        RunDebugFindCover(arg.Player());
    }

    private void RunDebugFindCover(BasePlayer? player)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.findcover requires a real player context (run from in-game chat/console).");
            return;
        }

        BasePlayer nearestBotPlayer = null;
        float nearestDistSqr = float.MaxValue;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (survivor.Player == null || survivor.Player.IsDestroyed)
            {
                continue;
            }

            float distSqr = (survivor.Player.transform.position - player.transform.position).sqrMagnitude;

            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearestBotPlayer = survivor.Player;
            }
        }

        if (nearestBotPlayer == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor found nearby.");
            return;
        }

        float botToAdminDistance = Vector3.Distance(nearestBotPlayer.transform.position, player.transform.position);

        if (TryFindCoverPoint(nearestBotPlayer, player, out Vector3 coverPoint))
        {
            float coverDistanceFromBot = Vector3.Distance(nearestBotPlayer.transform.position, coverPoint);
            string message = $"[LivingRust] '{nearestBotPlayer.displayName}' ({botToAdminDistance:F1}m from you) found cover at {coverPoint} ({coverDistanceFromBot:F1}m away, LOS to you blocked from there).";
            player.ChatMessage(message);
            Puts($"findcover-diag: {message}");
        }
        else
        {
            string message = $"[LivingRust] '{nearestBotPlayer.displayName}' ({botToAdminDistance:F1}m from you) found NO cover within search range.";
            player.ChatMessage(message);
            Puts($"findcover-diag: {message}");
        }
    }

    /// <summary>
    /// Direct test trigger for active resource gathering (2026-08-25,
    /// LivingRust.ResourceGathering.cs) - forces the nearest survivor to
    /// walk to and gather from the nearest live tree/ore node right now,
    /// bypassing ContinueLootTask's own fallback-of-last-resort gating
    /// (TryStartResourceGatheringFallback only fires once every other loot
    /// source nearby comes up empty) so the core swing/gather mechanic can
    /// be validated in isolation without needing to first clear an entire
    /// area of loot.
    /// </summary>
    [ChatCommand("lr.debug.gathertree")]
    private void CmdDebugGatherTree(BasePlayer player, string command, string[] args)
    {
        RunDebugGatherResource(player, isTree: true);
    }

    [ConsoleCommand("lr.debug.gathertree")]
    private void CmdDebugGatherTreeConsole(ConsoleSystem.Arg arg)
    {
        RunDebugGatherResource(arg.Player(), isTree: true);
    }

    [ChatCommand("lr.debug.gatherore")]
    private void CmdDebugGatherOre(BasePlayer player, string command, string[] args)
    {
        RunDebugGatherResource(player, isTree: false);
    }

    [ConsoleCommand("lr.debug.gatherore")]
    private void CmdDebugGatherOreConsole(ConsoleSystem.Arg arg)
    {
        RunDebugGatherResource(arg.Player(), isTree: false);
    }

    private void RunDebugGatherResource(BasePlayer? player, bool isTree)
    {
        if (player == null)
        {
            Puts("[LivingRust] this gather debug command requires a real player context (run from in-game chat/console).");
            return;
        }

        Survivor nearestSurvivor = null;
        float nearestDistSqr = float.MaxValue;

        foreach (Survivor candidate in _engine.SurvivorManager.GetAll())
        {
            if (candidate.Player == null || candidate.Player.IsDestroyed)
            {
                continue;
            }

            float distSqr = (candidate.Player.transform.position - player.transform.position).sqrMagnitude;

            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearestSurvivor = candidate;
            }
        }

        if (nearestSurvivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = nearestSurvivor.Player;

        // Seize control before forcing this - same "cancel whatever's
        // already running" pattern StartCombat itself uses before taking
        // over (LivingRust.Combat.cs). Without this, a survivor's own
        // already-in-flight natural task chain (a pending ContinueLootTask
        // callback, a recycler trip, etc) can fire moments later and
        // silently redirect the bot elsewhere - confirmed live, 2026-08-25:
        // "it just overrode the gather ore command to loot bodies about
        // 30-40 metres away." Both StartWalking-family calls just replace
        // whichever movement timer is currently registered, so whichever
        // call happens LAST wins - this debug command needs to be that
        // last call, not race the bot's own autonomous loop for it.
        CancelActiveMovement(nearestSurvivor);
        CancelActiveAttack(nearestSurvivor.Character.Id);
        CancelActiveRecycling(nearestSurvivor.Character.Id);

        if (isTree)
        {
            if (!_engine.NavigationManager.TryFindNearestTreeEntity(npc.transform.position, ResourceNodeSearchRadius, out TreeEntity tree))
            {
                player.ChatMessage($"[LivingRust] No live tree found within {ResourceNodeSearchRadius:F0}m of '{npc.displayName}'.");
                return;
            }

            player.ChatMessage($"[LivingRust] '{npc.displayName}' heading to gather wood from '{tree.ShortPrefabName}' ({Vector3.Distance(npc.transform.position, tree.transform.position):F0}m away).");
            _resourceGatherTypeLock[nearestSurvivor.Character.Id] = true;
            GatherTreeAndContinue(nearestSurvivor, tree, new LootTaskState());
        }
        else
        {
            if (!_engine.NavigationManager.TryFindNearestOreResourceEntity(npc.transform.position, ResourceNodeSearchRadius, out OreResourceEntity ore))
            {
                player.ChatMessage($"[LivingRust] No live ore node found within {ResourceNodeSearchRadius:F0}m of '{npc.displayName}'.");
                return;
            }

            player.ChatMessage($"[LivingRust] '{npc.displayName}' heading to mine '{ore.ShortPrefabName}' ({Vector3.Distance(npc.transform.position, ore.transform.position):F0}m away).");
            _resourceGatherTypeLock[nearestSurvivor.Character.Id] = false;
            GatherOreAndContinue(nearestSurvivor, ore, new LootTaskState());
        }
    }

    /// <summary>
    /// Direct test trigger for the crafting system (2026-08-28,
    /// LivingRust.Crafting.cs) - forces the nearest survivor into
    /// TryStartCraftingFallback right now, bypassing ContinueLootTask's
    /// own "nothing left to loot nearby" gate the same way
    /// /lr.debug.gathertree/gatherore already bypass the resource-
    /// gathering fallback's own gate. One call is enough to kick off the
    /// WHOLE chain Lucas asked to test (hemp -> cloth -> sleeping bag ->
    /// place it -> then wood/stone -> 15 stacks of arrows) - every step
    /// after this one is already self-continuing via each Gather*AndContinue
    /// wrapper's own onSuccess/onFailed resuming ContinueLootTask, which
    /// re-enters this exact same crafting fallback on its next "nothing
    /// left nearby" cycle.
    /// </summary>
    [ChatCommand("lr.debug.craft")]
    private void CmdDebugCraft(BasePlayer player, string command, string[] args)
    {
        RunDebugCraft(player);
    }

    [ConsoleCommand("lr.debug.craft")]
    private void CmdDebugCraftConsole(ConsoleSystem.Arg arg)
    {
        RunDebugCraft(arg.Player());
    }

    private void RunDebugCraft(BasePlayer? player)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.craft requires a real player context (run from in-game chat/console).");
            return;
        }

        Survivor nearestSurvivor = null;
        float nearestDistSqr = float.MaxValue;

        foreach (Survivor candidate in _engine.SurvivorManager.GetAll())
        {
            if (candidate.Player == null || candidate.Player.IsDestroyed)
            {
                continue;
            }

            float distSqr = (candidate.Player.transform.position - player.transform.position).sqrMagnitude;

            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearestSurvivor = candidate;
            }
        }

        if (nearestSurvivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor nearby. Use /lr.spawn first.");
            return;
        }

        BasePlayer npc = nearestSurvivor.Player;

        // Same "seize control" pattern RunDebugGatherResource uses above -
        // see its own doc comment for why this needs to be the last call
        // rather than racing the bot's own autonomous loop.
        CancelActiveMovement(nearestSurvivor);
        CancelActiveAttack(nearestSurvivor.Character.Id);
        CancelActiveRecycling(nearestSurvivor.Character.Id);

        if (!TryStartCraftingFallback(nearestSurvivor, npc, new LootTaskState()))
        {
            player.ChatMessage($"[LivingRust] '{npc.displayName}' has nothing to craft right now (already has a bag placed and full arrows, or no craftable goal applies).");
            return;
        }

        player.ChatMessage($"[LivingRust] '{npc.displayName}' started pursuing its next crafting goal - watch the log for 'craft-task' lines.");
    }

    /// <summary>
    /// Phase 2 of the findcover debug flow (2026-08-23, Lucas's own
    /// explicit request - "that way I can verify it and see if it would
    /// make sense") - runs the exact same TryFindCoverPoint search, but
    /// actually sends the bot walking there via the real StartWalking
    /// movement engine, so the result can be judged visually rather than
    /// just read off a coordinate in chat. Same "nearest survivor to the
    /// caller, caller treated as the attacker" setup as /lr.debug.findcover.
    /// </summary>
    [ChatCommand("lr.debug.gotocover")]
    private void CmdDebugGotoCover(BasePlayer player, string command, string[] args)
    {
        RunDebugGotoCover(player);
    }

    [ConsoleCommand("lr.debug.gotocover")]
    private void CmdDebugGotoCoverConsole(ConsoleSystem.Arg arg)
    {
        RunDebugGotoCover(arg.Player());
    }

    private void RunDebugGotoCover(BasePlayer? player)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.gotocover requires a real player context (run from in-game chat/console).");
            return;
        }

        Survivor nearestSurvivor = null;
        float nearestDistSqr = float.MaxValue;

        foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
        {
            if (survivor.Player == null || survivor.Player.IsDestroyed)
            {
                continue;
            }

            float distSqr = (survivor.Player.transform.position - player.transform.position).sqrMagnitude;

            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearestSurvivor = survivor;
            }
        }

        if (nearestSurvivor == null)
        {
            player.ChatMessage("[LivingRust] No spawned survivor found nearby.");
            return;
        }

        BasePlayer botPlayer = nearestSurvivor.Player;

        if (!TryFindCoverPoint(botPlayer, player, out Vector3 coverPoint))
        {
            player.ChatMessage($"[LivingRust] '{botPlayer.displayName}' found NO cover within search range - nothing to walk to.");
            return;
        }

        float coverDistanceFromBot = Vector3.Distance(botPlayer.transform.position, coverPoint);
        player.ChatMessage($"[LivingRust] '{botPlayer.displayName}' found cover {coverDistanceFromBot:F1}m away - walking there now.");
        Puts($"gotocover-diag: '{nearestSurvivor.Character.Alias}' walking to found cover point {coverPoint} ({coverDistanceFromBot:F1}m away).");

        StartWalking(
            nearestSurvivor,
            coverPoint,
            onArrived: () => player.ChatMessage($"[LivingRust] '{botPlayer.displayName}' reached the cover point - go check whether it actually blocks your view of it."),
            onFailed: () => player.ChatMessage($"[LivingRust] '{botPlayer.displayName}' couldn't actually walk to the cover point (blocked/stuck) - the spot passed the LOS/ground checks but isn't reliably reachable."));
    }

    /// <summary>
    /// Forces a fresh ScanMonumentForCoverPoints pass on whichever monument
    /// is nearest the caller, overwriting any cached result for that
    /// monument TYPE - lets a tuning change (CoverPointMinStructureSize,
    /// CoverPointStandoffFromStructure, etc) be tested immediately without
    /// a full server restart, same convenience this project's other tuned-
    /// constant debug tools already provide.
    /// </summary>
    [ChatCommand("lr.debug.scanmonumentcover")]
    private void CmdDebugScanMonumentCover(BasePlayer player, string command, string[] args)
    {
        RunDebugScanMonumentCover(player);
    }

    [ConsoleCommand("lr.debug.scanmonumentcover")]
    private void CmdDebugScanMonumentCoverConsole(ConsoleSystem.Arg arg)
    {
        RunDebugScanMonumentCover(arg.Player());
    }

    private void RunDebugScanMonumentCover(BasePlayer? player)
    {
        if (player == null)
        {
            Puts("[LivingRust] lr.debug.scanmonumentcover requires a real player context (run from in-game chat/console).");
            return;
        }

        if (!TryGetNearestMonumentForCover(player.transform.position, out MonumentInfo monument))
        {
            string message = "[LivingRust] No monument within range of you (checked against each monument's own real size).";
            player.ChatMessage(message);
            Puts($"scanmonumentcover-diag: {message}");
            return;
        }

        List<MonumentCoverPoint> points = ScanMonumentForCoverPoints(monument);
        player.ChatMessage($"[LivingRust] Re-scanned '{monument.name}' - found {points.Count} cover point(s). See console/findcover-diag for details.");
    }

    // Real WEAPONS only (2026-09-01, Lucas's own explicit ask: "a random
    // melee weapon (not a tool)") - a genuine subset of MeleeToolPriority
    // (LivingRust.Looting.cs), deliberately excluding every gather tool
    // (pickaxe/hatchet families) and the starting rock. Pitchfork kept in
    // (a real Rust melee weapon in its own right, not just a farming
    // tool).
    private static readonly string[] MeleeWeaponOnlyShortnames =
    {
        "salvaged.sword",
        "longsword",
        "salvaged.cleaver",
        "machete",
        "mace",
        "mace.baseballbat",
        "knife.combat",
        "spear.stone",
        "spear.wooden",
        "spear.cny",
        "knife.butcher",
        "pitchfork",
        "bone.club",
        "knife.bone",
        "knife.bone.obsidian",
        "knife.skinning",
        "candycaneclub",
        "vampire.stake",
        "sunken.knife",
        "boomerang",
        "paddle",
    };

    [ChatCommand("lr.debug.spawnmeleekit")]
    private void CmdDebugSpawnMeleeKit(BasePlayer player, string command, string[] args)
    {
        RunDebugSpawnMeleeKit(player, args);
    }

    // Console variant (2026-09-01, Lucas's own explicit ask: "make it
    // bindable") - Rust's own real `bind <key> "lr.debug.spawnmeleekit"`
    // client command only ever fires a CONSOLE command, not a chat one,
    // same reason every other debug command in this project already
    // registers both.
    [ConsoleCommand("lr.debug.spawnmeleekit")]
    private void CmdDebugSpawnMeleeKitConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugSpawnMeleeKit(player, arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
        }
    }

    /// <summary>
    /// Real melee-combat test rig (2026-09-01, Lucas's own explicit ask -
    /// see MeleeWeaponOnlyShortnames' own doc comment for the weapon
    /// pool). No args: spawns one survivor near where the caller is
    /// looking, gives it a single random real melee weapon, and forces it
    /// straight into StartMeleeCombat (LivingRust.MeleeCombat.cs) against
    /// the CALLING PLAYER directly - bypasses the normal on-sight
    /// detection roll entirely, since this command's whole point is an
    /// immediate, deterministic fight for testing, not waiting on a
    /// probability check. Real damage - the caller's own character can
    /// genuinely take damage and die from this, same as any other real
    /// Rust melee hit. "vs" arg: spawns TWO survivors near each other
    /// instead, each independently rolling its own random weapon, and
    /// forces them into melee combat against EACH OTHER instead of the
    /// caller - Lucas's own "even better" framing.
    /// </summary>
    private void RunDebugSpawnMeleeKit(BasePlayer player, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        bool versusMode = args.Any(a => a.Equals("vs", StringComparison.OrdinalIgnoreCase));

        if (!versusMode)
        {
            Vector3 spawnPosition = FindSpawnAimPoint(player, out bool aimedSpawn);
            Survivor survivor = SpawnMeleeKitSurvivor(player, spawnPosition, player.transform.rotation, out string weaponShortname);

            if (survivor == null)
            {
                player.ChatMessage("[LivingRust] Failed to spawn survivor.");
                return;
            }

            string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
            player.ChatMessage($"[LivingRust] Spawned melee-kit survivor '{survivor.Character.Alias}' {where} with a {weaponShortname} - it's coming for you.");

            StartMeleeCombat(survivor, player);
            return;
        }

        // 15m apart (2026-09-01, Lucas's own explicit ask) - centered on
        // either side of the aim point rather than one spawn offset from
        // the other, so they're genuinely 15m apart from EACH OTHER (not
        // 15m from wherever the caller happened to be looking), and have
        // to actually close real distance before the fight starts instead
        // of spawning already in swinging range.
        const float VersusSpawnSeparation = 15f;
        Vector3 versusCenter = FindSpawnAimPoint(player, out bool aimed);
        Vector3 firstPosition = versusCenter - player.transform.right * (VersusSpawnSeparation / 2f);
        Vector3 secondPosition = versusCenter + player.transform.right * (VersusSpawnSeparation / 2f);

        Survivor first = SpawnMeleeKitSurvivor(player, firstPosition, player.transform.rotation, out string firstWeapon);
        Survivor second = SpawnMeleeKitSurvivor(player, secondPosition, player.transform.rotation, out string secondWeapon);

        if (first == null || second == null)
        {
            player.ChatMessage("[LivingRust] Failed to spawn one or both melee-kit survivors.");
            return;
        }

        player.ChatMessage($"[LivingRust] Spawned '{first.Character.Alias}' ({firstWeapon}) vs '{second.Character.Alias}' ({secondWeapon}) - let them fight.");

        StartMeleeCombat(first, second.Player);
        StartMeleeCombat(second, first.Player);
    }

    private Survivor SpawnMeleeKitSurvivor(BasePlayer player, Vector3 position, Quaternion rotation, out string weaponShortname)
    {
        Character character = _engine.CharacterManager.CreateInitialSurvivor();
        Survivor survivor = _engine.SurvivorManager.Create(character);

        BasePlayer npc = SpawnSurvivor(survivor, position, rotation);

        if (npc == null)
        {
            Puts($"ERROR: Failed to spawn melee-kit survivor '{character.Alias}'.");
            weaponShortname = null;
            return null;
        }

        npc.InitializeHealth(npc.MaxHealth(), npc.MaxHealth());

        if (npc.IsWounded())
        {
            npc.StopWounded();
        }

        weaponShortname = MeleeWeaponOnlyShortnames[UnityEngine.Random.Range(0, MeleeWeaponOnlyShortnames.Length)];
        GiveItem(npc, weaponShortname, 1);

        return survivor;
    }
}
