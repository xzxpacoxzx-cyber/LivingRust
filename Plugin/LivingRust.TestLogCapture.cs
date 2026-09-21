using System;
using System.IO;
using Oxide.Plugins;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins;

public partial class LivingRust
{
    /// <summary>
    /// Carbon.Core.log rotates unpredictably (size/time based, outside our
    /// control) and this has repeatedly eaten the middle of a spawnmany/
    /// tracemany test run - the archive cuts off before the test starts and
    /// the live log only picks up again near the end, losing everything in
    /// between. This hooks Carbon's static Logger callbacks (the same funnel
    /// every Puts() call goes through - see RustPlugin.Puts -> Logger.Log)
    /// and mirrors every "[Project] ..." line to our own append-only file
    /// that nothing else ever rotates or truncates, so a full test's worth
    /// of log lines always survives regardless of what Carbon's own log does
    /// around it.
    /// </summary>
    private const string TestLogDirectory = "LivingRust/testlogs";

    private StreamWriter? _testLogWriter;
    private readonly object _testLogLock = new();

    private void StartTestLogCapture(string reason)
    {
        lock (_testLogLock)
        {
            if (_testLogWriter != null)
            {
                _testLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] --- {reason} ---");
                _testLogWriter.Flush();
                return;
            }

            Directory.CreateDirectory(TestLogDirectory);

            string path = Path.Combine(TestLogDirectory, $"testlog_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            _testLogWriter = new StreamWriter(path, append: true) { AutoFlush = false };
            _testLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] --- capture started: {reason} ---");
            _testLogWriter.Flush();

            Carbon.Logger.OnNoticeCallback += OnTestLogNotice;
            Carbon.Logger.OnWarningCallback += OnTestLogWarning;
            Carbon.Logger.OnErrorCallback += OnTestLogError;

            Puts($"test log capture started - writing to {path}.");
        }
    }

    private void StopTestLogCapture()
    {
        lock (_testLogLock)
        {
            if (_testLogWriter == null)
            {
                return;
            }

            Carbon.Logger.OnNoticeCallback -= OnTestLogNotice;
            Carbon.Logger.OnWarningCallback -= OnTestLogWarning;
            Carbon.Logger.OnErrorCallback -= OnTestLogError;

            _testLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] --- capture stopped ---");
            _testLogWriter.Flush();
            _testLogWriter.Dispose();
            _testLogWriter = null;
        }
    }

    private void OnTestLogNotice(string text, int verbosity) => WriteTestLogLine(text);

    private void OnTestLogWarning(string text, int verbosity) => WriteTestLogLine(text);

    private void OnTestLogError(string text, Exception ex, int verbosity) => WriteTestLogLine(text);

    private void WriteTestLogLine(string text)
    {
        // Every plugin on the server routes through the same static Logger
        // callbacks - filter down to just our own lines (Puts prefixes
        // everything with "[Title]", and this plugin's Title is "Project"
        // per the Info attribute) so the file stays a clean per-test record
        // instead of a firehose of unrelated server/plugin noise.
        if (text == null || !text.StartsWith("[Project]", StringComparison.Ordinal))
        {
            return;
        }

        lock (_testLogLock)
        {
            if (_testLogWriter == null)
            {
                return;
            }

            _testLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
            _testLogWriter.Flush();
        }
    }

    /// <summary>
    /// Manual start/stop, independent of spawnmany/tracemany auto-capture -
    /// call once to start, call again to stop. Useful for bounding capture
    /// around any test (e.g. re-tracing already-spawned bots) without
    /// needing a fresh spawnmany call.
    /// </summary>
    [ChatCommand("lr.debug.testlog")]
    private void CmdDebugTestLog(BasePlayer player, string command, string[] args)
    {
        if (_testLogWriter != null)
        {
            StopTestLogCapture();
            player.ChatMessage("[LivingRust] Test log capture stopped.");
        }
        else
        {
            StartTestLogCapture("manual /lr.debug.testlog");
            player.ChatMessage($"[LivingRust] Test log capture started - writing under {TestLogDirectory}/.");
        }
    }

    [ConsoleCommand("lr.debug.testlog")]
    private void CmdDebugTestLogConsole(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();

        if (player != null)
        {
            CmdDebugTestLog(player, "lr.debug.testlog", Array.Empty<string>());
        }
    }
}
