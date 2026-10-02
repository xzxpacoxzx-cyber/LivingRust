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
    /// Mirrors this plugin's log lines to a dedicated append-only file via Carbon's
    /// static Logger callbacks, independent of Carbon's own log rotation.
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
        // Filters the shared Logger stream down to this plugin's own lines only.
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
    /// Toggles manual test log capture on or off, independent of auto-capture.
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
