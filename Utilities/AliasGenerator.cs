using System;
using System.Collections.Generic;

namespace LivingRust.Utilities
{
    /// <summary>
    /// Generates believable Steam-style aliases for Characters.
    /// </summary>
    public static class AliasGenerator
    {
        private static readonly Random Random = new();

        private static readonly List<string> Prefixes = new()
        {
            "Rusty",
            "Ghost",
            "Dirty",
            "Silent",
            "Crazy",
            "Broken",
            "Sneaky",
            "Mad",
            "Tiny",
            "Big",
            "Angry",
            "Lucky",
            "Lost",
            "Toxic",
            "Feral",
            "Dead",
            "Fast",
            "Cold",
            "Grim",
            "Salty",
            "Wild",
            "Shady",
            "Rabid",
            "Bloody",
            "Filthy",
            "Wasted",
            "Grungy",
            "Savage",
            "Ragged",
            "Weird",
            "Sly",
            "Twisted",
            "Rough",
            "Scrappy",
            "Jumpy",
            "Nervous",
            "Bold",
            "Reckless",
            "Quiet",
            "Loud",
            "Sharp",
            "Blunt",
            "Grubby",
            "Soggy",
            "Crusty",
            "Jittery",
            "Stray",
            "Numb",
            "Hazy",
            "Rowdy"
        };

        private static readonly List<string> Cores = new()
        {
            "Wolf",
            "Rat",
            "Bandit",
            "Bean",
            "Hunter",
            "Farmer",
            "Scav",
            "Reaper",
            "Goblin",
            "Builder",
            "Naked",
            "Grub",
            "Camper",
            "Miner",
            "Boomer",
            "AK",
            "Rock",
            "Torch",
            "Skinner",
            "Trapper",
            "Poacher",
            "Drifter",
            "Nomad",
            "Raider",
            "Prowler",
            "Stalker",
            "Scrapper",
            "Looter",
            "Wanderer",
            "Outlaw",
            "Renegade",
            "Vagrant",
            "Marauder",
            "Bruiser",
            "Grunt",
            "Sentinel",
            "Warden",
            "Ranger",
            "Trooper",
            "Runner",
            "Digger",
            "Diver",
            "Squatter",
            "Vulture",
            "Coyote",
            "Jackal",
            "Weasel",
            "Roach",
            "Buzzard",
            "Hermit"
        };

        /// <summary>
        /// A run of 1-4 digits, each 1-9 (never 0) - Lucas's own spec
        /// (2026-08-10, after repeatedly seeing duplicate/similar aliases
        /// at scale - the AliasGenerator predates this session's work).
        /// Placed before OR after the name with equal chance, rather than
        /// always appended, for more real-looking variety.
        /// </summary>
        private static string GenerateDigitRun()
        {
            int digitCount = Random.Next(1, 5);
            char[] digits = new char[digitCount];

            for (int i = 0; i < digitCount; i++)
            {
                digits[i] = (char)('1' + Random.Next(9));
            }

            return new string(digits);
        }

        public static string GenerateAlias()
        {
            string alias =
                Prefixes[Random.Next(Prefixes.Count)] +
                Cores[Random.Next(Cores.Count)];

            // Roughly half of aliases get a digit run - higher than the
            // old 40% flat-appended version, and now placed before or
            // after with equal chance instead of always trailing, both
            // deliberately to cut down on collisions at real spawnmany
            // scale (50 prefixes x 50 cores x ~50% plain still isn't a
            // huge pool on its own - the digit run is what actually
            // spreads things out).
            if (Random.NextDouble() < 0.5)
            {
                string digits = GenerateDigitRun();
                alias = Random.NextDouble() < 0.5 ? digits + alias : alias + digits;
            }

            return alias;
        }
    }
}