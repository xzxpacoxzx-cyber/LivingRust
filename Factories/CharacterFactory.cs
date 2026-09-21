using LivingRust.Models;
using LivingRust.Utilities;

namespace LivingRust.Factories
{
    /// <summary>
    /// Responsible for creating fully initialized Characters.
    /// </summary>
    public static class CharacterFactory
    {
        /// <summary>
        /// Creates a brand new Character with a generated alias
        /// and behavioural profile.
        /// </summary>
        public static Character Create()
        {
            Character character = new Character();

            character.Alias = AliasGenerator.GenerateAlias();
            character.Profile = CharacterProfileFactory.Create();

            return character;
        }
    }
}