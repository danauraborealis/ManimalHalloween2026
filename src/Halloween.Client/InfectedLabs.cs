using System;

namespace Manimal.Halloween2026.Client
{
    // which Labs this raid is. the scene layer (event props, lights, quest zones) will hang off Changed.
    internal static class InfectedLabs
    {
        internal const string VariantId = "backport";

        internal static bool Active { get; private set; }
        internal static event Action<bool> Changed;

        // MapVariants calls this for our map before the preset loads, and again if the player changes their mind
        internal static void OnVariant(string variant) => Set(string.Equals(variant, VariantId, StringComparison.OrdinalIgnoreCase));

        // raised for every managed map, so anything that is not Labs clears us
        internal static void OnResolved(string variant)
        {
            var labs = string.Equals(MapVariantsApi.CurrentLocationId, Api.Labs.LocationId, StringComparison.OrdinalIgnoreCase);
            Set(labs && string.Equals(variant, VariantId, StringComparison.OrdinalIgnoreCase));
        }

        private static void Set(bool active)
        {
            if (Active == active)
            {
                return;
            }

            Active = active;
            Plugin.Log.LogInfo(active ? "[Halloween2026] this Labs raid is Halloween 2026" : "[Halloween2026] Halloween 2026 Labs is off");
            try
            {
                Changed?.Invoke(active);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] a Changed handler threw: {e}");
            }
        }
    }
}
