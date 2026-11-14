using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

// what an event map changes about its normal map's bots, as tools/Import-Halloween.py reads it from upstream
internal sealed class LocationOverlay
{
    [JsonPropertyName("BossLocationSpawn")] public List<BossLocationSpawn> BossLocationSpawn { get; init; } = [];
    [JsonPropertyName("Replaces")] public List<string> Replaces { get; init; } = [];
    [JsonPropertyName("Waves")] public List<Wave>? Waves { get; init; }
    [JsonPropertyName("Hostility")] public List<AdditionalHostilitySettings> Hostility { get; init; } = [];
    [JsonPropertyName("ClosedExits")] public List<string> ClosedExits { get; init; } = [];
    [JsonPropertyName("SecretExits")] public List<Exit> SecretExits { get; init; } = [];
    [JsonPropertyName("BotMax")] public int BotMax { get; init; }
    [JsonPropertyName("EscapeTimeLimit")] public double? EscapeTimeLimit { get; init; }
    [JsonPropertyName("Events")] public LocationEvents? Events { get; init; }
}

// puts the infected into an event raid as the raid is generated. it cannot live in the location
// table: spawn mods rebuild every map's bosses and waves there after all other mods have loaded,
// and FactoryClassic's Halloween Factory carries the classic map's bots, not the event's.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 2)]
public sealed class HalloweenRaidSpawns(
    HalloweenConfig config,
    BotTable botTable,
    JsonUtil jsonUtil,
    MapVariantsBridge mapVariants,
    HalloweenRaidSpawnsPatch patch,
    ISptLogger<HalloweenRaidSpawns> logger) : IOnLoad
{
    private sealed record Rule(string Variant, LocationOverlay Overlay);

    private readonly Dictionary<string, Rule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (config.Settings.InfectedLabsEnabled)
        {
            Add("laboratory", "backport", "laboratory");
        }

        if (config.Settings.InfectedFactoryEnabled)
        {
            // FactoryClassic's own name for its Halloween tile
            Add("factory", "halloween2026", "factory4_day", "factory4_night");
        }

        if (_rules.Count == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            patch.Bind(this);
            patch.Enable();
            logger.Info($"[Halloween2026] infected spawns armed for {string.Join(", ", _rules.Keys)}");
        }
        catch (Exception e)
        {
            logger.Error($"[Halloween2026] could not hook raid generation, event raids get no infected: {e.Message}");
        }

        return Task.CompletedTask;
    }

    private void Add(string folder, string variant, params string[] locations)
    {
        var path = config.DbPath("locations", folder, "overlay.json");
        var overlay = File.Exists(path) ? jsonUtil.DeserializeFromFile<LocationOverlay>(path) : null;
        if (overlay is null || overlay.BossLocationSpawn.Count == 0)
        {
            logger.Warning($"[Halloween2026] no infected waves in {path}");
            return;
        }

        foreach (var location in locations)
        {
            _rules[location] = new Rule(variant, overlay);
        }
    }

    internal void Apply(string location, LocationBase raid)
    {
        if (!_rules.TryGetValue(location, out var rule)
            || !string.Equals(mapVariants.InstalledVariant(location), rule.Variant, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var overlay = rule.Overlay;
        var zones = (raid.SpawnPointParams ?? []).Select(point => point.BotZoneName).ToHashSet();
        var infected = Copy(overlay.BossLocationSpawn)
            .Where(wave => botTable.Types.ContainsKey((wave.BossName ?? "").ToLowerInvariant())
                && (wave.BossZone ?? "").Split(',').Any(zones.Contains))
            .ToList();

        raid.BossLocationSpawn ??= [];
        var removed = raid.BossLocationSpawn.RemoveAll(wave =>
            (wave.BossName ?? "").StartsWith("infected", StringComparison.OrdinalIgnoreCase)
            || overlay.Replaces.Contains(wave.BossName ?? "", StringComparer.OrdinalIgnoreCase));
        raid.BossLocationSpawn.AddRange(infected);

        if (overlay.Waves is not null)
        {
            raid.Waves = Copy(overlay.Waves);
        }

        raid.BotMax = Math.Max(raid.BotMax, overlay.BotMax);
        raid.Events = overlay.Events ?? raid.Events;
        Hostility(raid, overlay);
        logger.Info($"[Halloween2026] {location} ({rule.Variant}): {infected.Count} infected waves in, {removed} boss entries out, "
            + $"{raid.BossLocationSpawn.Count - infected.Count} other entries kept");
    }

    // the infected take upstream's own entries; everyone else only learns that the infected are enemies
    private void Hostility(LocationBase raid, LocationOverlay overlay)
    {
        if (overlay.Hostility.Count == 0 || raid.BotLocationModifier is null)
        {
            return;
        }

        var settings = (raid.BotLocationModifier.AdditionalHostilitySettings ?? []).ToList();
        foreach (var entry in Copy(overlay.Hostility))
        {
            var existing = settings.FindIndex(other => string.Equals(other.BotRole, entry.BotRole, StringComparison.OrdinalIgnoreCase));
            if ((entry.BotRole ?? "").StartsWith("infected", StringComparison.OrdinalIgnoreCase))
            {
                if (existing >= 0)
                {
                    settings[existing] = entry;
                }
                else
                {
                    settings.Add(entry);
                }
            }
            else if (existing < 0)
            {
                settings.Add(entry);
            }
            else
            {
                var enemies = settings[existing].AlwaysEnemies ??= [];
                foreach (var role in entry.AlwaysEnemies ?? [])
                {
                    if (role.StartsWith("infected", StringComparison.OrdinalIgnoreCase))
                    {
                        enemies.Add(role);
                    }
                }
            }
        }

        raid.BotLocationModifier.AdditionalHostilitySettings = settings;
    }

    // a fresh copy per raid: spt keeps adjusting the response after this
    private List<T> Copy<T>(List<T> source) => jsonUtil.Deserialize<List<T>>(jsonUtil.Serialize(source)!) ?? [];
}

[Injectable(InjectionType.Singleton)]
public sealed class HalloweenRaidSpawnsPatch : AbstractPatch
{
    private static HalloweenRaidSpawns? _spawns;
    private static ISptLogger<HalloweenRaidSpawnsPatch>? _logger;

    public HalloweenRaidSpawnsPatch(ISptLogger<HalloweenRaidSpawnsPatch> logger)
        : base($"{ModIdentity.Guid}.raid-spawns")
    {
        _logger = logger;
    }

    // set by its owner rather than injected: the two would otherwise need each other to construct
    internal void Bind(HalloweenRaidSpawns spawns) => _spawns = spawns;

    protected override MethodBase GetTargetMethod()
    {
        foreach (var method in typeof(LocationLifecycleService).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name == "GenerateLocationAndLoot" && method.ReturnType == typeof(LocationBase))
            {
                return method;
            }
        }

        throw new MissingMethodException("[Halloween2026] LocationLifecycleService.GenerateLocationAndLoot is unavailable.");
    }

    // by position: the location name is the only string argument, whatever this build calls it
    [PatchPostfix]
    private static void Postfix(object[] __args, LocationBase __result)
    {
        try
        {
            if (__result is null)
            {
                return;
            }

            foreach (var arg in __args)
            {
                if (arg is string location)
                {
                    _spawns?.Apply(location, __result);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            _logger?.Error($"[Halloween2026] adding the infected to this raid failed: {e}");
        }
    }
}
