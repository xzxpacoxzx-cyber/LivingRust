using System;
using System.Collections.Generic;
using LivingRust.Core;
using LivingRust.Models;

namespace LivingRust.Managers
{
    /// <summary>
    /// Owns every runtime survivor currently being simulated.
    /// </summary>
    public class SurvivorManager
    {
        private readonly Dictionary<Guid, Survivor> _survivors = new();

        public SurvivorManager()
        {
            Logger.Info("SurvivorManager initialized.");
        }

        /// <summary>
        /// Creates a runtime survivor for a character.
        /// </summary>
        public Survivor Create(Character character)
        {
            Survivor survivor = new Survivor(character);

            _survivors.Add(character.Id, survivor);

            Logger.Info($"Created Survivor for '{character.Alias}'");

            return survivor;
        }

        public Survivor Get(Guid id)
        {
            _survivors.TryGetValue(id, out Survivor survivor);

            return survivor;
        }

        public IReadOnlyCollection<Survivor> GetAll()
        {
            return _survivors.Values;
        }

        public bool Remove(Guid id)
        {
            return _survivors.Remove(id);
        }

        public int Count => _survivors.Count;
    }
}