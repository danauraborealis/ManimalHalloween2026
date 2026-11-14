using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using Location = SPTarkov.Server.Core.Models.Eft.Common.Location;

namespace Manimal.Halloween2026.Server;

// MapVariants' server half by reflection, the way FactoryClassic reaches it: no compile-time
// reference, so a different MapVariants build cannot stop this mod loading.
[Injectable(InjectionType.Singleton)]
public sealed class MapVariantsBridge(IServiceProvider provider, ISptLogger<MapVariantsBridge> logger)
{
    private const string ProvidersTypeName = "MapVariants.Server.Variants.Providers";

    private object? _providers;
    private MethodInfo? _register;
    private MethodInfo? _installedVariant;
    private bool _bound;

    private bool Bind()
    {
        if (_bound)
        {
            return _providers is not null;
        }

        _bound = true;
        Type? type = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(ProvidersTypeName, false);
            if (type is not null)
            {
                break;
            }
        }

        if (type is null)
        {
            logger.Error($"[Halloween2026] {ProvidersTypeName} is not loaded, Labs gets no Halloween tile");
            return false;
        }

        _providers = provider.GetService(type);
        _register = type.GetMethod("Register", [typeof(string), typeof(Location), typeof(Location)]);
        _installedVariant = type.GetMethod("InstalledVariant", [typeof(string)]);
        if (_providers is null || _register is null || _installedVariant is null)
        {
            logger.Error("[Halloween2026] MapVariants' provider surface is not the expected layout, Labs gets no Halloween tile");
            _providers = null;
        }

        return _providers is not null;
    }

    public bool Register(string locationId, Location original, Location variant)
    {
        if (!Bind())
        {
            return false;
        }

        try
        {
            _register!.Invoke(_providers, [locationId, original, variant]);
            return true;
        }
        catch (Exception e)
        {
            logger.Error($"[Halloween2026] handing '{locationId}' to MapVariants threw: {e}");
            return false;
        }
    }

    // "original", "backport" (ours) or null when unavailable. changes with each player's pick.
    public string? InstalledVariant(string locationId)
    {
        if (!Bind())
        {
            return null;
        }

        try
        {
            return _installedVariant!.Invoke(_providers, [locationId]) as string;
        }
        catch
        {
            return null;
        }
    }
}
