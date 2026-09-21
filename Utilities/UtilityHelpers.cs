// This file contains helper functions and utilities used throughout the plugin for various tasks.

using System;

namespace LivingRust.Utilities
{
    public static class UtilityHelpers
    {
        // Example utility function to log messages
        public static void Log(string message)
        {
            Console.WriteLine($"[UtilityHelpers] {message}");
        }

        // Example utility function to calculate distance between two points
        public static double CalculateDistance(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }

        // Add more utility functions as needed
    }
}