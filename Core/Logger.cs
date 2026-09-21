using System;

namespace LivingRust.Core
{
    /// <summary>
    /// Simple logging utility used throughout LivingRust.
    /// </summary>
    public static class Logger
    {
        public static bool DebugEnabled { get; set; } = true;

        public static void Info(string message)
        {
            Console.WriteLine($"[LivingRust] {message}");
        }

        public static void Warning(string message)
        {
            Console.WriteLine($"[LivingRust][Warning] {message}");
        }

        public static void Error(string message)
        {
            Console.WriteLine($"[LivingRust][Error] {message}");
        }

        public static void Debug(string message)
        {
            if (!DebugEnabled)
                return;

            Console.WriteLine($"[LivingRust][Debug] {message}");
        }
    }
}