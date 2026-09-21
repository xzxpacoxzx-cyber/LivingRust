using System.Linq;
using LivingRust.AI;
using LivingRust.Core;
using LivingRust.Models;

namespace LivingRust.Managers
{
    /// <summary>
    /// Responsible for teaching Characters about the world.
    /// </summary>
    public class KnowledgeManager
    {
        public KnowledgeManager()
        {
            Logger.Info("KnowledgeManager initialized.");
        }

        public void DiscoverLocation(Character character, string locationName)
        {
            bool alreadyKnown =
                character.WorldKnowledge.KnownLocations
                    .Any(location => location.Name == locationName);

            if (alreadyKnown)
                return;

            character.WorldKnowledge.KnownLocations.Add(
                new KnownLocation
                {
                    Name = locationName,
                    Familiarity = 1
                });

            Logger.Info(
                $"{character.Alias} discovered {locationName}.");
        }
    }
}