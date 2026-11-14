using System;
using System.Reflection;
using HarmonyLib;

namespace Manimal.Halloween2026.Client
{
    // MapVariants' public api by reflection: a compile-time reference would load a second copy of
    // its shared assembly and turn a version mismatch into a load failure.
    internal static class MapVariantsApi
    {
        private const string TypeName = "MapVariants.Client.Api.Maps";

        private static Type _type;
        private static MethodInfo _register;
        private static PropertyInfo _currentVariant, _currentLocationId;
        private static EventInfo _variantResolved;
        private static bool _looked;

        private static bool Look()
        {
            if (_looked)
            {
                return _type != null;
            }

            _looked = true;
            _type = AccessTools.TypeByName(TypeName);
            if (_type == null)
            {
                Plugin.Log.LogError($"[Halloween2026] {TypeName} was not found");
                return false;
            }

            // by parameter count: a name-only lookup throws once a second overload exists
            foreach (var method in _type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "Register" && method.GetParameters().Length == 8)
                {
                    _register = method;
                }
            }

            _currentVariant = AccessTools.Property(_type, "CurrentVariant");
            _currentLocationId = AccessTools.Property(_type, "CurrentLocationId");
            _variantResolved = _type.GetEvent("VariantResolved", BindingFlags.Public | BindingFlags.Static);
            return true;
        }

        internal static bool Register(string[] locationIds, string displayName, string variantName, string originalName,
            string variantTile, string originalTile, Action<string> onVariant, Func<string[]> warnings)
        {
            if (!Look() || _register == null)
            {
                return false;
            }

            var result = _register.Invoke(null, new object[]
            {
                locationIds, displayName, variantName, originalName, variantTile, originalTile, onVariant, warnings,
            });
            return result is bool ok && ok;
        }

        // their wire values: "original", "backport" (ours), or null while undecided
        internal static string CurrentVariant => Read(_currentVariant);
        internal static string CurrentLocationId => Read(_currentLocationId);

        internal static void Subscribe(Action<string> handler)
        {
            if (Look() && _variantResolved != null)
            {
                _variantResolved.GetAddMethod(true)?.Invoke(null, new object[] { handler });
            }
        }

        private static string Read(PropertyInfo property)
        {
            if (!Look() || property == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
