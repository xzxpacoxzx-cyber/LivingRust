using LivingRust.Core;
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

    // Directory of curated base designs, separate from raw trace recordings, organized by tier.
    private const string BaseDesignsDirectory = "LivingRust/base_designs";

    private readonly Dictionary<Guid, Timer> _activeTraces = new();
    private readonly Dictionary<Guid, StreamWriter> _traceWriters = new();

    // Keyed by the scientist2 entity's network ID since it has no Survivor/Character roster entry.
    private readonly Dictionary<ulong, Timer> _activeNpcTraces = new();
    private readonly Dictionary<ulong, StreamWriter> _npcTraceWriters = new();

    // Traces a connected player's own movement, keyed by userID, to record a ground-truth path for building routes.
    private readonly Dictionary<ulong, Timer> _activePlayerTraces = new();
    private readonly Dictionary<ulong, StreamWriter> _playerTraceWriters = new();

    /// <summary>
    /// Toggles recording the nearest survivor's position and movement state each tick to a CSV trace file.
    /// Call again on the same survivor to stop recording.
    /// </summary>
    [ChatCommand("lr.debug.trace")]
    private void CmdDebugTrace(BasePlayer player, string command, string[] args)
    {
        RunDebugTrace(player);
    }

    /// <summary>
    /// Toggles bot invincibility for testing by restoring health and clearing wounded state on every hit.
    /// Off by default and not persisted between plugin loads.
    /// </summary>
    private bool _botsInvincible = false;

    /// <summary>
    /// Toggles whether bots will fight real players, covering both reactive damage and on-sight detection.
    /// Bot-vs-bot and bot-vs-animal combat are unaffected. Off by default and not persisted between plugin loads.
    /// </summary>
    private bool _disablePlayerCombat = false;

    /// <summary>
    /// Lets the bot-vs-bot aggression grace period (LivingRust.Combat.cs) be bypassed for testing.
    /// Off by default and not persisted between plugin loads.
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
    /// Toggles the low-health combat disengage off, useful for isolating heal-animation testing from combat ending.
    /// Off by default and not persisted between plugin loads.
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
    /// Forces every survivor to move straight to its destination, bypassing normal pathing, to help isolate
    /// collision issues from pathing/logic issues. Debug-only; off by default and not persisted between plugin loads.
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

        // Also log to the server console, since ChatMessage output does not reach it.
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
    /// Toggles routine per-bot console logging (loot summaries, equip confirmations, movement narration).
    /// On by default; warnings and lifecycle events always log regardless.
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
    /// Starts a CSV trace for every currently-spawned survivor at once, sharing one timestamp so elapsed_s
    /// columns line up across files. Running the command again stops all active survivor traces.
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
            // Catches per-survivor IO failures so one bad trace can't abort the rest of the batch.
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
    /// Shared per-survivor trace setup used by both /lr.debug.trace and /lr.debug.tracemany. No-ops if the
    /// survivor is already being traced. Filename includes the unique BotId to avoid collisions between survivors.
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

        // target_* columns record combat target position/distance, left blank when not in combat.
        writer.WriteLine("elapsed_s,x,y,z,facing_deg,on_ladder,sprinting,ducked,in_combat,target_x,target_y,target_z,target_distance");

        Timer traceTimer = null;

        traceTimer = timer.Every(WalkTickInterval, () =>
        {
            BasePlayer npc = survivor.Player;

            // Skips writing during a death/respawn gap instead of stopping the trace, so it resumes automatically
            // once RespawnSurvivor assigns a new BasePlayer.
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
    /// Traces the nearest native ScientistNPC2 as a baseline for how native NavMeshAgent movement behaves.
    /// Logs position/facing plus native agent state; the CSV schema differs from the survivor trace since
    /// ScientistNPC2 is not a BasePlayer.
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
    /// Traces the calling admin's own connected player movement for building an authored waypoint route.
    /// Toggle; run again to stop.
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

    // Per-player build-trace registry, same shape as _activePlayerTraces/_playerTraceWriters above, but
    // event-driven off the OnEntityBuilt hook rather than a periodic timer tick, since a construction
    // placement is a discrete moment rather than a continuous stream of positions.
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
    /// Oxide/Carbon hook that fires for every construction placement server-wide. Logs only for a player
    /// with an active build trace. Grade is logged for BuildingBlock entities; door deploys into a building
    /// socket go through Deployer.DoDeploy_Slot instead and are not covered here.
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
    /// Oxide/Carbon hook that fires when an existing BuildingBlock is upgraded to a higher grade.
    /// Logged as an "upgrade" event in the same CSV row shape as OnEntityBuilt.
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
    /// Oxide/Carbon hook off Deployer.DoDeploy_Slot, covering slot deploys like code locks that
    /// OnEntityBuilt/Planner does not catch. Identifies the lock and its target entity by type rather
    /// than by parameter order, since that order differs between deploy variants.
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
    /// Shared row-writer for OnEntityBuilt and OnStructureUpgraded. Records a material-count snapshot after
    /// each event so comparing rows shows material consumption.
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
    /// Test trigger for the base-building replay: finds the nearest survivor and the caller's most recently
    /// saved tracebuild CSV, then replays it using the survivor's current position as the new origin.
    /// </summary>
    /// <summary>
    /// Omitting a tier argument lets the bot choose one via TryChooseAffordableBaseDesign instead of
    /// defaulting to tier0. An explicit tier argument still forces that specific tier.
    /// </summary>
    /// <summary>
    /// Test command that triggers GhostEnterHomeForDeposit directly on a survivor, to verify the fallback
    /// movement in isolation without needing a real task to route through it.
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
    /// On-demand trigger for the "recycle then go home and deposit" trip. Runs GhostReturnHomeAndDeposit
    /// directly on a spawned survivor to verify deposit/furnace-fill behavior without waiting for a full
    /// recycle cycle. Uses the same alias/nearest lookup as /lr.debug.ghostenter.
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
        // "nolock" anywhere in the args skips code-lock rows in the replay; a second plain arg picks an
        // exact design by name instead of rolling randomly within the tier.
        bool skipCodeLocks = args.Any(a => a.Equals("nolock", StringComparison.OrdinalIgnoreCase));
        string[] positionalArgs = args.Where(a => !a.Equals("nolock", StringComparison.OrdinalIgnoreCase)).ToArray();

        RunDebugReplayBuild(
            player,
            positionalArgs.Length > 0 ? positionalArgs[0] : AutoBaseDesignTier,
            positionalArgs.Length > 1 ? positionalArgs[1] : null,
            skipCodeLocks);
    }

    // Fallback tier used when a requested tier's pool is empty, and by TryChooseAffordableBaseDesign
    // when nothing is currently affordable.
    private const string DefaultBaseDesignTier = "tier0";

    // Sentinel tier value meaning "let the bot decide" - never matches a real directory.
    private const string AutoBaseDesignTier = "auto";

    /// <summary>
    /// Copies the caller's most recent raw tracebuild recording into BaseDesignsDirectory/{tier}/ so it
    /// joins the pool /lr.debug.replaybuild picks from. A deliberate step rather than auto-promoting every
    /// trace, so only confirmed-good designs join the pool.
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

        // Scans upward for the first free index rather than assuming a dense, gapless filename sequence.
        int nextIndex = 1;

        while (File.Exists($"{tierDirectory}/base{nextIndex}.csv"))
        {
            nextIndex++;
        }

        string destinationPath = $"{tierDirectory}/base{nextIndex}.csv";

        File.Copy(latestCsv, destinationPath, overwrite: false);

        // Uses the actual file count rather than nextIndex, since nextIndex can land on a gap.
        int totalInPool = Directory.GetFiles(tierDirectory, "base*.csv").Length;

        player.ChatMessage($"[LivingRust] Saved '{Path.GetFileName(latestCsv)}' as '{tier}/base{nextIndex}.csv' - {totalInPool} design(s) now in the '{tier}' pool.");
        Puts($"basebuild-replay: promoted '{Path.GetFileName(latestCsv)}' to '{destinationPath}'.");
    }

    /// <summary>
    /// Converts a recorded /lr.debug.traceme trace into an offset from a build's origin and saves it as a
    /// door route for that design. Optional tier/baseN args target a specific design; otherwise it uses the
    /// nearest spawned survivor's home.
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

        // Matches against where the recording itself started, not the calling player's current position,
        // since the player may have moved elsewhere by the time this command runs.
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

            // Searches every character this engine knows about, dead or alive.
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
    /// Reports CalculateTraceResourceRequirements's total material cost for a saved design.
    /// Usage: /lr.debug.basecost [tier] [baseN]; tier and design default to the first in the pool.
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

        // TryChooseAffordableBaseDesign picks the richest tier the survivor's inventory can afford, falling
        // back to a random tier0 design if nothing is affordable yet.
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
            // Picks from designs promoted via /lr.debug.savebasedesign, falling back to the newest raw trace
            // for this player if that tier's pool doesn't exist or is empty.
            string tierDirectory = $"{BaseDesignsDirectory}/{tier}";

            if (Directory.Exists(tierDirectory))
            {
                string[] tierDesigns = Directory.GetFiles(tierDirectory, "*.csv");

                // Matches a specific design by filename (without extension), case-insensitive, for
                // deterministic builds needed by the door-route recording workflow.
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

        // Checks for road proximity, slope, tree/ore/obstacle overlap, and nearby LOS-breaking cover, since
        // the replay bypasses Rust's own CanBuild placement checks. Steps further out and re-checks if any
        // check fails.
        if (!TryFindClearBuildOrigin(npc.transform.position, out Vector3 clearOrigin, out float expectedGroundHeight, out string siteFailureReason))
        {
            string siteFailureMessage = $"'{npc.displayName}' couldn't find a clear spot to build within {BuildSiteRelocateMaxAttempts} attempts (last reason: {siteFailureReason}) - move it somewhere more open and try again.";
            player.ChatMessage($"[LivingRust] {siteFailureMessage}");
            Puts($"basebuild-replay: {siteFailureMessage}");
            return;
        }

        // Cancels active behavior so the survivor's autonomous loop can't redirect it mid-replay.
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

        // Walks the bot to a relocated build site instead of phasing there, so the movement stays visible.
        // Skipped when the site was already clear. Uses WalkToBuildSiteWithRecovery, a bounded recovery
        // variant that gives up cleanly instead of phasing through geometry.
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
    /// Sends the nearest spawned survivor along a recorded /lr.debug.traceme route, phasing through
    /// obstacles using a known-good path, then hands control back to normal autonomous behavior.
    /// No filename argument defaults to a specific known-good trace.
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

        // With no explicit filename, finds the closest monument that has a registered route.
        // resolvedMonument is the exact instance the roll was made against, reused below for projection.
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

        // Re-projects onto the monument instance this route is for. Prefers resolvedMonument when available;
        // otherwise searches for the nearest matching monument, falling back to raw recorded coordinates.
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

        // Walks to the route's first waypoint before phasing through it, matching the same approach used
        // by the autonomous trigger via StartGhostRoute.
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

    // Staggers each spawned bot's task start by a random delay within this window to avoid a
    // thundering-herd effect where every bot's first tick lands in the same frame.
    private const float SpawnManyStaggerWindowSeconds = 5f;

    // Delay before the next bot spawns when this one's spawn point lands within
    // SpawnManyContentionRadius of the previous one. Currently a flat 0.1s for testing.
    private const float SpawnManyContentionDelayMin = 0.1f;
    private const float SpawnManyContentionDelayMax = 0.1f;

    // Distance between two consecutive spawn points to count as contested.
    private const float SpawnManyContentionRadius = 5f;

    // Stagger used when a bot's spawn point is not contested. Currently a flat 0.1s for testing.
    private const float SpawnManyClearDelayMin = 0.1f;
    private const float SpawnManyClearDelayMax = 0.1f;

    /// <summary>
    /// Percent chance (0-100) each /lr.debug.spawnmany survivor spawns at a validated random inland point
    /// instead of a beach spawn.
    /// </summary>
    private const int SpawnManyInlandFraction = 20;

    /// <summary>
    /// Scale-testing tool: spawns N survivors, SpawnManyInlandFraction% at a validated random inland point
    /// and the rest at real beach spawn locations, each starting a staggered LootForResources task.
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
    /// Spawns one bot, then schedules the next spawn with a delay that depends on whether this bot's
    /// spawn point landed within SpawnManyContentionRadius of the previous one. Runs as a self-scheduling
    /// chain since each delay depends on a spawn result only known after the previous bot spawns.
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

        // Reuses TryFindRandomInlandSite (the same validated roll the normal home-site strategy uses) to
        // spawn a fraction of bots inland instead of walking there afterward. Falls back to a beach spawn
        // if no valid inland site is found.
        Vector3 inlandSite = Vector3.zero;
        bool spawnInland = UnityEngine.Random.Range(0, 100) < SpawnManyInlandFraction && TryFindRandomInlandSite(out inlandSite);

        BasePlayer npc = spawnInland
            ? SpawnSurvivor(survivor, inlandSite, Quaternion.identity, useBeachSpawnPoint: false)
            : SpawnSurvivor(survivor, Vector3.zero, Quaternion.identity, useBeachSpawnPoint: true);

        if (npc == null)
        {
            Puts($"ERROR: spawnmany - failed to spawn survivor '{character.Alias}' ({indexForLog}/{total}).");
            // Uses the ordinary clear-spawn delay rather than stalling the batch on one failure.
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
    /// Manual full clean-slate wipe of every Character record; see DespawnAllBots for what is erased.
    /// BotId numbering never resets.
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
    /// Teleports the caller to a bot currently stuck in recovery (EscalateStuckRecovery, LivingRust.Looting.cs).
    /// No arg jumps to the longest-stuck bot; an optional 1-based index picks a specific one from the printed list.
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
    /// Teleports to a bot by case-insensitive substring match against Character.Alias, for when the bot
    /// isn't currently flagged stuck. If multiple survivors match, teleports to the first and lists the rest.
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
    /// Prints ItemBlueprint.GetIngredients() for the given shortname, since blueprint amounts are baked
    /// prefab data and must be queried from the live game.
    /// </summary>
    /// <summary>
    /// Scans every BuildingBlock a survivor owns near its home for pieces still at Twig grade, reporting
    /// position and type. Diagnostic only; does not upgrade anything itself.
    /// </summary>
    private const float CheckUpgradesSearchRadius = 40f;

    /// <summary>
    /// Reports every live survivor's Character.Home state: counts with/without a base, a tier breakdown,
    /// and (with the "list" arg) each survivor's alias/tier/position.
    /// </summary>
    /// <summary>
    /// Reports who is actually connected right now, distinguishing real human connections from bots
    /// (bots are always IsNpc:True and IsConnected:False). Admin-gated since a full player roster is
    /// server-operator information.
    /// </summary>
    [ChatCommand("lr.show.players")]
    private void CmdShowPlayers(BasePlayer player, string command, string[] args)
    {
        if (!player.IsAdmin)
        {
            player.ChatMessage("[LivingRust] You do not have permission to use this command.");
            return;
        }

        RunShowPlayers(player);
    }

    [ConsoleCommand("lr.show.players")]
    private void CmdShowPlayersConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        // A null player means the server console (or RCON) called this, which is always trusted.
        if (player != null && !player.IsAdmin)
        {
            player.ChatMessage("[LivingRust] You do not have permission to use this command.");
            return;
        }

        RunShowPlayers(player);
    }

    private void RunShowPlayers(BasePlayer requestingPlayer)
    {
        List<BasePlayer> realPlayers = BasePlayer.activePlayerList
            .Where(p => p != null && !p.IsNpc && p.IsConnected)
            .OrderBy(p => p.displayName)
            .ToList();

        void Report(string message)
        {
            Puts(message);
            requestingPlayer?.ChatMessage(message);
        }

        Report($"[LivingRust] {realPlayers.Count} real player(s) currently connected:");

        foreach (BasePlayer p in realPlayers)
        {
            Report($"[LivingRust]   '{p.displayName}' (SteamID {p.UserIDString}) at {p.transform.position}.");
        }

        if (realPlayers.Count == 0)
        {
            Report("[LivingRust]   (none - every current BasePlayer is this project's own AI, or nobody is connected at all)");
        }
    }

    [ChatCommand("lr.debug.bases")]
    private void CmdDebugBases(BasePlayer player, string command, string[] args)
    {
        RunDebugBases(player, args);
    }

    [ConsoleCommand("lr.debug.bases")]
    private void CmdDebugBasesConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            RunDebugBases(player, arg.HasArgs() ? arg.Args.Select(a => a.ToString()).ToArray() : Array.Empty<string>());
        }
    }

    private void RunDebugBases(BasePlayer player, string[] args)
    {
        if (_engine == null)
        {
            player.ChatMessage("[LivingRust] Engine is not running.");
            return;
        }

        bool listIndividual = args.Any(a => a.Equals("list", StringComparison.OrdinalIgnoreCase));

        List<Survivor> all = _engine.SurvivorManager.GetAll().ToList();
        List<Survivor> withBase = all.Where(s => s.Character.Home != null).ToList();
        List<Survivor> withoutBase = all.Except(withBase).ToList();

        player.ChatMessage($"[LivingRust] {all.Count} survivor(s) total - {withBase.Count} with a base, {withoutBase.Count} without.");

        if (withBase.Count > 0)
        {
            IEnumerable<IGrouping<int, Survivor>> byTier = withBase.GroupBy(s => s.Character.Home.TierRank).OrderBy(g => g.Key);

            foreach (IGrouping<int, Survivor> group in byTier)
            {
                player.ChatMessage($"[LivingRust]   tier{group.Key}: {group.Count()}");
            }
        }

        if (!listIndividual)
        {
            player.ChatMessage("[LivingRust] Add 'list' to see each survivor individually.");
            return;
        }

        foreach (Survivor survivor in withBase)
        {
            HomeBase home = survivor.Character.Home;
            player.ChatMessage($"[LivingRust]   '{survivor.Character.Alias}' - tier{home.TierRank} at {home.Position} ({Path.GetFileNameWithoutExtension(home.SourceDesignPath)}).");
        }

        foreach (Survivor survivor in withoutBase)
        {
            string status = survivor.Character.PursuingBaseGatherGoal ? "gathering for one" : "no base yet";
            player.ChatMessage($"[LivingRust]   '{survivor.Character.Alias}' - {status}.");
        }
    }

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

        // "fix" mode upgrades every found Twig piece directly via BuildingBlock.ChangeGrade+SetHealthToMax,
        // with no resource cost deducted since the survivor already paid for the original upgrade.
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

            // Target grade is whichever grade the rest of this survivor's base is mostly built from.
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
    /// Dumps recipes for every craftable item in ItemManager.itemList (same ItemBlueprint API as
    /// RunDebugRecipe) as CSV to LivingRust/crafting_recipes.csv, as a reference for future dev work.
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
    /// Consolidates every movement/geometry diagnostic into one command. Runs, in order: a raycast look,
    /// every unique collider within 5m, navmesh coverage sampling, and a full replay of TryGetNextStep's
    /// 5-point ground probe. Replaces the three standalone diagnostic commands.
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
    /// The ground-probe piece of /lr.debug.scan: replays NavigationManager.TryGetNextStep's 5-point
    /// ground-surface probe one meter ahead of the player, reporting every individual raycast rather than
    /// just the single winning result.
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
    /// Reports every unique collider within a radius of the player, regardless of look direction.
    /// </summary>
    // No longer directly bound to a command - folded into /lr.debug.scan.
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
            // Uses bounds.ClosestPoint rather than transform.position, since a large collider's transform
            // can be far from the player, making a raw distance meaningless.
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
    /// Reads the prefab-baked NavMeshAgent/RustNavMeshAgent parameters a live scientist uses, since these
    /// are Unity-serialized inspector values not present in decompiled source. Prefers the general-purpose
    /// "scientist2" archetype over stationary variants, and any scientist over any other NPCPlayer.
    /// Relies on an already-spawned instance rather than spawning one itself.
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
    /// Lower is better. "scientist2" (the general-purpose roamer) beats any other scientist match, which
    /// beats any other NPCPlayer.
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
    /// Manually feeds one "stuck here" report into the monument avoid-zone system (LivingRust.MonumentAvoidZones.cs)
    /// at the caller's position, letting a known-bad spot be confirmed deterministically without needing to
    /// reproduce a real stuck loot task there.
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
    /// No longer directly bound to a command - folded into /lr.debug.scan.
    ///
    /// Diagnoses why native movement fails to get a usable path toward loot-approach destinations. Checks
    /// whether AI.useUnityNavmesh matches the navmesh system actually in use, and whether Unity's baked
    /// navmesh has any coverage near the caller's position for the agent type, at several growing radii.
    /// </summary>
    private void RunDebugNavMeshCheck(BasePlayer player)
    {
        Puts($"debug-navmeshcheck: AI.useUnityNavmesh={ConVar.AI.useUnityNavmesh}, AI.move={ConVar.AI.move}, AI.logIssues={ConVar.AI.logIssues}.");

        // Only bots get native components attached, so this uses the nearest spawned survivor's agent
        // to sample with the same agentTypeID/areaMask production movement uses.
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
    /// No longer directly bound to a command - folded into /lr.debug.scan. Note that Rust freezes the
    /// view/aim angle once chat opens, so running from chat tests the facing direction at that moment;
    /// use the console version for close-range/small targets where that distinction matters.
    ///
    /// By default reports only the nearest solid (non-trigger) collider along the view ray. Pass "all" to
    /// dump every collider along the ray. Falls back to scanning a small radius around the aim point if
    /// the ray hits nothing.
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
    /// Reassigns a sleeping bag's ownership to one of our survivors, since Rust's own "assign to friend" UI
    /// cannot target a bot's fake userID. Aims the same way /lr.debug.look does; targets the nearest spawned
    /// survivor by default, or a specific one by alias.
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

            // Also logs via Puts() so a failed run has a server-side trace.
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
    /// Puts an item directly into a survivor's inventory. Rust's own admin give-to-player commands can't
    /// target a bot since they resolve through BasePlayer.Find(), which only searches connected players.
    /// Uses the Survivor/Character lookup instead, same as /lr.debug.claimbag.
    /// </summary>
    /// <summary>
    /// Test rig that spawns a bot with the ingredients to craft three research-locked items. Since these
    /// require research a bot never does, ingredients are spawned directly to test ghost-craft logic that
    /// bypasses CanCraft while still verifying workbench and ingredients.
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

        // Deploys a tier3 workbench at the survivor's feet, satisfying the workbench-tier requirement for
        // all three test items.
        BaseEntity testWorkbench = GameManager.server.CreateEntity("assets/prefabs/deployable/tier 3 workbench/workbench3.deployed.prefab", npc.transform.position, npc.transform.rotation) as BaseEntity;
        testWorkbench?.Spawn();

        npc.SendNetworkUpdateImmediate();

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        string failureNote = failures.Count > 0 ? $" (ingredient issues: {string.Join("; ", failures)})" : "";

        player.ChatMessage($"[LivingRust] Spawned craft-test survivor '{character.Alias}' {where} (ID {character.BotId}).{failureNote} Queuing crafts - watch chat for each one landing.");

        // Chains crafts one at a time rather than firing all three in parallel, matching a real crafting
        // queue. Ingredients deduct on acceptance; the crafted item is delayed by bp.time.
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
    /// Crafts an item while skipping Rust's research-gated CanCraft check, but keeps the real craft timer.
    /// Validates workbench tier and ingredients directly instead. Ingredients deduct on acceptance; the
    /// crafted item appears after the recipe's bp.time.
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

            // Lets the item give network normally instead of forcing an immediate update, avoiding a
            // same-tick collision with reload/weapon-swap logic.
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

        // Uses the same known-good prefab path as the deployable placement code (KnownDeployablePrefabPaths
        // in LivingRust.BaseBuilding.cs), deployed at the survivor's feet.
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
    /// Moves every item out of a survivor's main and belt inventory (not worn gear) into the given container.
    /// Wraps DepositFilteredItems with an always-true filter, sharing the transfer loop with production logic.
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

    // Search radius for an already-placed box.wooden.large near the player, rather than spawning a new one.
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

        // Gives the survivor a full metal AK kit using the same ApplyKit logic /lr.spawn.ak runs, so this
        // test can't silently drift from the real kit.
        ApplyKit(npc, survivor, "ak");

        string where = aimedSpawn ? "where you're looking" : "near you (nothing solid in view)";
        player.ChatMessage($"[LivingRust] Spawned walk-deposit-test survivor '{character.Alias}' {where} (ID {character.BotId}) with a full metal AK kit. Walking {nearestDist:F0}m to the box.");

        // Approaches from a 1m stand-off rather than the box's own transform.position, to avoid overlapping
        // its collider, using the same approach-direction pattern as the toolcupboard approach in
        // GhostEnterHomeForDeposit.
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

    // 1000x each - within this server's stack size limits for these items.
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

    // Looks for a furnace already placed nearby, same as WalkDepositTestBoxSearchRadius.
    private const float WalkSmeltTestFurnaceSearchRadius = 50f;

    // Stand-off distance to avoid spawning/phasing inside the furnace's collider.
    private const float FurnaceStandOffDistance = 1f;

    /// <summary>
    /// Tests smelting by depositing wood, metal ore, and sulfur ore into a furnace and igniting it via
    /// BaseOven.StartCooking(), which requires fuel to already be present. Walks to an already-placed
    /// furnace using the same pattern as RunDebugWalkDepositTest, reusing DepositAllItems for the transfer.
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

        // Walks to a point offset from the furnace rather than straight to its transform.position.
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
    /// Args: &lt;shortname&gt; [amount] [alias...]. Amount defaults to 1 and is only consumed from args[1]
    /// if it parses as a number, so an alias-only call works without a redundant amount.
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

        // Forces an immediate network update so a nearby client sees the new item right away.
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
    /// Spawns a survivor carrying everything a card-puzzle detour needs (one fuse and each keycard tier,
    /// see LivingRust.CardPuzzles.cs), so testing TryStartCardPuzzleDetour doesn't require looting first.
    /// Also kitted with the same AK loadout as /lr.spawn.ak for a full end-to-end elevator/puzzle test.
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
    /// Unlike /lr.walk.monument, which just walks there and stops, this hands the survivor off to the real
    /// loot-task escalation ladder (EscalateSearchToMonumentZone) on arrival, forcing a specific destination
    /// so the card-puzzle check runs deterministically instead of relying on a random destination roll.
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

        foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
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
    /// Teleports the nearest spawned survivor to a registered card-puzzle route's first waypoint and runs
    /// it immediately, skipping the approach walk. name, if given, substring-matches a specific monument;
    /// omitted, it finds the nearest registered puzzle monument. Combine with /lr.debug.spawncardtest first
    /// if the survivor isn't already carrying a fuse and the right keycard(s).
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
            foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
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
            foreach (MonumentInfo candidate in MonumentAccess.GetAllMonuments())
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

        // userID is shown for anything BasePlayer-derived, covering real players, our own bots, and Rust's
        // own NPCPlayer-family NPCs, to check whether our BotId range could ever collide with a vanilla NPC's ID.
        if (entity is BasePlayer entityPlayer)
        {
            entityInfo += $" | userID {entityPlayer.userID} | displayName '{entityPlayer.displayName}'";
        }

        // A DroppedItem's own ShortPrefabName is a generic wrapper prefab regardless of what's inside it,
        // so the actual item identity comes from WorldItem.item.info.shortname instead.
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

        // Puts() is Carbon's own logging call, which reliably reaches the server console log.
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
            VerbosePuts($"findcover-diag: {message}");
        }
        else
        {
            string message = $"[LivingRust] '{nearestBotPlayer.displayName}' ({botToAdminDistance:F1}m from you) found NO cover within search range.";
            player.ChatMessage(message);
            VerbosePuts($"findcover-diag: {message}");
        }
    }

    /// <summary>
    /// Direct test trigger for resource gathering: forces the nearest survivor to walk to and gather from
    /// the nearest live tree/ore node, bypassing ContinueLootTask's normal fallback-of-last-resort gating.
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

        // Cancels whatever's already running, same pattern StartCombat uses before taking over. Without
        // this, a survivor's own in-flight task chain can fire moments later and silently redirect the bot,
        // since whichever StartWalking-family call happens last wins.
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
    /// Direct test trigger for the crafting system: forces the nearest survivor into TryStartCraftingFallback,
    /// bypassing ContinueLootTask's "nothing left to loot nearby" gate. Each subsequent step self-continues
    /// via the Gather*AndContinue callbacks re-entering this same fallback.
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
    /// Phase 2 of the findcover debug flow: runs the same TryFindCoverPoint search, but sends the bot
    /// walking there via StartWalking so the result can be judged visually instead of read as a coordinate.
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
        VerbosePuts($"gotocover-diag: '{nearestSurvivor.Character.Alias}' walking to found cover point {coverPoint} ({coverDistanceFromBot:F1}m away).");

        StartWalking(
            nearestSurvivor,
            coverPoint,
            onArrived: () => player.ChatMessage($"[LivingRust] '{botPlayer.displayName}' reached the cover point - go check whether it actually blocks your view of it."),
            onFailed: () => player.ChatMessage($"[LivingRust] '{botPlayer.displayName}' couldn't actually walk to the cover point (blocked/stuck) - the spot passed the LOS/ground checks but isn't reliably reachable."));
    }

    /// <summary>
    /// Forces a fresh ScanMonumentForCoverPoints pass on the nearest monument, overwriting any cached result
    /// for that monument type, so a tuning change can be tested without a full server restart.
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
            VerbosePuts($"scanmonumentcover-diag: {message}");
            return;
        }

        List<MonumentCoverPoint> points = ScanMonumentForCoverPoints(monument);
        player.ChatMessage($"[LivingRust] Re-scanned '{monument.name}' - found {points.Count} cover point(s). See console/findcover-diag for details.");
    }

    // Weapons-only subset of MeleeToolPriority, excluding gather tools (pickaxe/hatchet families) and the
    // starting rock. Pitchfork is kept in as a real melee weapon rather than just a farming tool.
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

    // Console variant so this command can be bound to a key, since Rust's `bind` only fires console commands.
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
    /// Melee-combat test rig. With no args, spawns one survivor with a random melee weapon and forces it
    /// straight into combat against the caller, bypassing the normal on-sight detection roll; damage is
    /// real. The "vs" arg spawns two survivors instead and forces them into combat against each other.
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

        // Spawns the two survivors 15m apart, centered on the aim point, so they have to close real
        // distance before the fight starts instead of spawning already in swinging range.
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
