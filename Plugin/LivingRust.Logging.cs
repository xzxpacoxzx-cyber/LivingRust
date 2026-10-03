using System;
using System.Collections.Generic;
using System.Linq;
using LivingRust.Models;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Console noise control (2026-10-04, Lucas: "10+ messages a second in the server terminal window").
/// Every Puts in this plugin goes through the filter below: unless verbose logging is on
/// (<c>lr.debug.verbose on</c>), only a short list of important lines is printed (errors, lifecycle,
/// base completions, rewards, ghost routes, deaths zones, debug-command output ...). Everything else is
/// counted, and a one-line summary is printed every minute so it's still visible that the bots are busy.
/// Verbose mode prints everything exactly as before.
/// </summary>
public partial class LivingRust
{
    // A message is always printed if it starts with one of these.
    private static readonly string[] AlwaysShownPrefixes =
    {
        "WARNING", "ERROR", "[LivingRust]", "=====", "LivingRust", "Initializing", "Server initialized", "Starting LivingRust",
        "Stopping LivingRust", "Captured live state", "Restored ", "Loaded monument", "monument-loot-zones:", "fresh-wipe:",
        "ghost-route-only:", "sleeping-bags:", "death-zone:", "death-loop:", "ghostroute:", "card-puzzle:", "early-kit:",
        "monument-rush:", "airdrop", "assess:", "upgrade:", "tier-upgrade:", "wipe-goal:", "base-return:", "windfall:",
        "respawn:", "log-summary:", "debug-", "despawnall:", "giveitem:", "tracebuild:", "claimbag:", "spawncardtest:",
        "spawnmany:", "checkupgrades:", "test", "kill-loot:", "cooking:", "outfit:", "fuel-hunt:", "home-trip:",
    };

    private readonly Dictionary<string, int> _suppressedLogCounts = new();
    private int _suppressedLogTotal;
    private int _suppressedDeaths;
    private Timer _logSummaryTimer;
    private const float LogSummaryIntervalSeconds = 60f;
    private const int MaxTrackedLogKeys = 300;

    /// <summary>
    /// Hides Plugin.Puts for this class so every existing call site is filtered without being touched.
    /// </summary>
    private new void Puts(string message)
    {
        if (_verboseLootLogging || ShouldAlwaysShow(message))
        {
            base.Puts(message);
            return;
        }

        CountSuppressedLog(message);
    }

    private static bool ShouldAlwaysShow(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (string prefix in AlwaysShownPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // A few specific lines from otherwise-quiet categories.
        return message.Contains("finished building")
            || message.Contains("placed a sleeping bag at its base")
            || (message.StartsWith("research:", StringComparison.Ordinal) && message.Contains("free research #"))
            || message.Contains("WARNING")
            || message.Contains("Exception");
    }

    private void CountSuppressedLog(string message)
    {
        _suppressedLogTotal++;

        if (message.Contains("' died ("))
        {
            _suppressedDeaths++;
        }

        string key = LogCategoryKey(message);

        if (_suppressedLogCounts.TryGetValue(key, out int count))
        {
            _suppressedLogCounts[key] = count + 1;
        }
        else if (_suppressedLogCounts.Count < MaxTrackedLogKeys)
        {
            _suppressedLogCounts[key] = 1;
        }
    }

    /// <summary>
    /// "prefix: ..." messages group under their prefix; "'Alias' did something" messages group under the
    /// first few words after the alias, so the summary reads "bot died, bot respawned away ...".
    /// </summary>
    private static string LogCategoryKey(string message)
    {
        if (message.Length > 0 && message[0] == '\'')
        {
            int close = message.IndexOf('\'', 1);
            string rest = close > 0 && close + 1 < message.Length ? message.Substring(close + 1).Trim() : message;
            return "bot " + string.Join(" ", rest.Split(' ').Take(3));
        }

        int colon = message.IndexOf(':');

        if (colon > 0 && colon <= 28)
        {
            return message.Substring(0, colon);
        }

        return string.Join(" ", message.Split(' ').Take(3));
    }

    private void StartLogSummary()
    {
        _logSummaryTimer?.Destroy();
        _logSummaryTimer = timer.Every(LogSummaryIntervalSeconds, PrintLogSummary);
    }

    private void StopLogSummary()
    {
        _logSummaryTimer?.Destroy();
        _logSummaryTimer = null;
    }

    private void PrintLogSummary()
    {
        if (_verboseLootLogging || _suppressedLogTotal == 0)
        {
            ResetSuppressedLogCounts();
            return;
        }

        string top = string.Join(", ", _suppressedLogCounts.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}"));
        int alive = 0;
        int withBase = 0;

        if (_engine != null)
        {
            foreach (Survivor survivor in _engine.SurvivorManager.GetAll())
            {
                if (survivor.Player != null && !survivor.Player.IsDestroyed)
                {
                    alive++;
                }

                if (survivor.Character.Home != null)
                {
                    withBase++;
                }
            }
        }

        base.Puts($"log-summary: last {LogSummaryIntervalSeconds:F0}s - {_suppressedDeaths} deaths, {_suppressedLogTotal} routine lines hidden ({top}). {alive} bots alive, {withBase} with a base. 'lr.debug.verbose on' shows everything.");
        ResetSuppressedLogCounts();
    }

    private void ResetSuppressedLogCounts()
    {
        _suppressedLogCounts.Clear();
        _suppressedLogTotal = 0;
        _suppressedDeaths = 0;
    }
}
