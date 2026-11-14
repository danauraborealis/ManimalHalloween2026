namespace Manimal.Halloween2026.Client.Api
{
    // for other mods, by reflection after a soft dependency. the location id is "laboratory" on both
    // tiles, so this is the only way to tell an infected Labs raid from a normal one.
    public static class Labs
    {
        public const string LocationId = "laboratory";
        public const string ApiVersion = "1.0";

        public static bool IsHalloween => InfectedLabs.Active;
    }
}
