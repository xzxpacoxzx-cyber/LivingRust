using System;
using System.Collections.Generic;
using LivingRust.Core;
using LivingRust.Factories;
using LivingRust.Models;

namespace LivingRust.Managers
{
    /// <summary>
    /// Owns and manages every persistent Character in the LivingRust world.
    /// Characters are permanent and exist whether or not they are currently
    /// spawned into the Rust world.
    /// </summary>
    public class CharacterManager
    {
        private readonly Dictionary<Guid, Character> _characters = new();

        // Staying under 10,000,000 keeps BasePlayer.IsBot true for these
        // entities (Rust's own bot-ID convention), which is what makes
        // SupportsServerOcclusion() return false for them and avoids a
        // same-key crash in the server occlusion group bookkeeping for
        // connectionless players. Issued once per Character at creation and
        // never reused, so a respawned survivor's BasePlayer keeps the same
        // userID its whole life - see Character.BotId's doc comment for why
        // that matters.
        //
        // Starting value confirmed via decompiled Assembly-CSharp.dll:
        // BasePlayer.ServerInit() assigns every native NPC (scientists,
        // monument bots, etc.) a userID from a single process-wide
        // `botIdCounter` that starts at 1 and increments by 1 per spawn for
        // the whole server session - so a low starting value here (e.g.
        // 1000) can genuinely collide mid-session with Rust's own NPCs
        // (confirmed: a Bradley-spawned scientist's loot bag showed a
        // LivingRust bot's name). 5,000,000 leaves a gap that counter would
        // need 5 million native bot spawns in one session to ever reach -
        // not realistic - while staying safely under the 10,000,000 IsBot
        // ceiling. Restarts are self-correcting regardless (Rust's own
        // SaveRestore.WorldSetup calls BasePlayer.ReserveBotIds() on load,
        // scanning every saved BasePlayer's userID and reserving exactly
        // what's actually used), so this only needed to solve the live,
        // same-session race between the two independent counters.
        private ulong _nextBotId = 5000000;

        public CharacterManager()
        {
            Logger.Info("CharacterManager initialized.");
        }

        /// <summary>
        /// Creates the very first survivor character.
        /// </summary>
        public Character CreateInitialSurvivor()
        {
            Character survivor = CreateCharacter();

            Logger.Info($"Initial survivor created: {survivor.Alias}");

            return survivor;
        }

        /// <summary>
        /// Creates a new persistent character.
        /// </summary>
        public Character CreateCharacter()
        {
            Character character = CharacterFactory.Create();

            character.BotId = _nextBotId++;

            _characters.Add(character.Id, character);

            Logger.Info($"Created Character '{character.Alias}' ({character.Id}, bot ID {character.BotId})");

            return character;
        }

        /// <summary>
        /// Registers an existing character (used when loading saves). Also
        /// advances the bot-ID counter past anything already claimed by a
        /// loaded character, so a fresh CreateCharacter() call later in the
        /// same run can never collide with a userID restored from disk.
        /// </summary>
        public void RegisterCharacter(Character character)
        {
            _characters[character.Id] = character;

            if (character.BotId >= _nextBotId)
            {
                _nextBotId = character.BotId + 1;
            }

            Logger.Info($"Registered Character '{character.Alias}' ({character.Id})");
        }

        /// <summary>
        /// Removes a character permanently.
        /// </summary>
        public bool RemoveCharacter(Guid id)
        {
            return _characters.Remove(id);
        }

        /// <summary>
        /// Gets a character by ID.
        /// </summary>
        public Character GetCharacter(Guid id)
        {
            _characters.TryGetValue(id, out Character character);

            return character;
        }

        /// <summary>
        /// Returns every persistent character.
        /// </summary>
        public IReadOnlyCollection<Character> GetAllCharacters()
        {
            return _characters.Values;
        }

        /// <summary>
        /// Total number of persistent characters.
        /// </summary>
        public int Count => _characters.Count;
    }
}