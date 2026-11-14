using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using Location = SPTarkov.Server.Core.Models.Eft.Common.Location;

namespace Manimal.Halloween2026.Server;

// builds the infected Labs beside the live one and hands the pair to MapVariants, which owns the
// choice and the swap from then on. PostLoad so every other mod's Labs edits are in the copy.
// the location id stays "laboratory": that is what keeps keycards, quests and stats working.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad)]
public sealed class InfectedLabs(
    HalloweenConfig config,
    LocationTable locationTable,
    BotTable botTable,
    JsonUtil jsonUtil,
    HalloweenQuestLoot questLoot,
    MapVariantsBridge mapVariants,
    ISptLogger<InfectedLabs> logger) : IOnLoad
{
    public const string LocationId = "laboratory";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (!config.Settings.InfectedLabsEnabled)
        {
            logger.Info("[Halloween2026] infected Labs disabled by config");
            return Task.CompletedTask;
        }

        var live = locationTable.GetLocation(LocationId);
        if (live?.Base is null)
        {
            logger.Error("[Halloween2026] no Labs location in the table, the Halloween tile is not offered");
            return Task.CompletedTask;
        }

        Location infected;
        try
        {
            infected = Build(live);
        }
        catch (Exception e)
        {
            logger.Error($"[Halloween2026] could not build infected Labs, the Halloween tile is not offered: {e}");
            return Task.CompletedTask;
        }

        if (mapVariants.Register(LocationId, live, infected))
        {
            logger.Info($"[Halloween2026] infected Labs handed to MapVariants: {infected.Base.BossLocationSpawn.Count} waves, "
                + $"{infected.Base.BotMax} bots, {infected.Base.EscapeTimeLimit} minutes");
        }

        return Task.CompletedTask;
    }

    private Location Build(Location live)
    {
        var overlayPath = config.DbPath("locations", LocationId, "overlay.json");
        var overlay = jsonUtil.DeserializeFromFile<LocationOverlay>(overlayPath)
            ?? throw new InvalidDataException($"overlay is missing or empty: {overlayPath}");
        if (overlay.BossLocationSpawn.Count == 0)
        {
            throw new InvalidDataException($"overlay holds no waves: {overlayPath}");
        }

        // json round trip keeps every key of the live base without naming them
        var infected = jsonUtil.Deserialize<LocationBase>(jsonUtil.Serialize(live.Base)!)!;

        var zones = (live.Base.SpawnPointParams ?? []).Select(point => point.BotZoneName).ToHashSet();
        infected.BossLocationSpawn = overlay.BossLocationSpawn
            .Where(wave => botTable.Types.ContainsKey((wave.BossName ?? "").ToLowerInvariant())
                && (wave.BossZone ?? "").Split(',').Any(zones.Contains))
            .ToList();
        if (infected.BossLocationSpawn.Count == 0)
        {
            throw new InvalidDataException("none of the infected waves fit this install's bots and Labs zones");
        }

        infected.BotMax = overlay.BotMax;
        infected.EscapeTimeLimit = overlay.EscapeTimeLimit;
        infected.Events = overlay.Events ?? infected.Events;

        // retail's event Labs has no normal way out: only the two exits that each need an item found in the raid
        if (config.Settings.LabsEventExtracts && overlay.SecretExits.Count > 0)
        {
            foreach (var exit in infected.Exits ?? [])
            {
                if (overlay.ClosedExits.Contains(exit.Name ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    exit.Chance = 0;
                    exit.ChancePVE = 0;
                }
            }

            infected.SecretExits = overlay.SecretExits;
        }

        // a fresh LazyLoad over the live one: the variant's extra points must never reach a normal raid.
        // held in a local because MapVariants reassigns live.LooseLoot to ours on every swap.
        var extra = questLoot.LabsVariant;
        var liveLoot = live.LooseLoot;
        return new Location
        {
            Base = infected,
            LooseLoot = liveLoot is null ? null : new LazyLoad<LooseLoot>(() => WithVariantPoints(liveLoot.Value, extra), false),
            StaticLoot = live.StaticLoot,
            StaticContainers = live.StaticContainers,
            StaticAmmo = live.StaticAmmo,
            Statics = live.Statics,
            AllExtracts = live.AllExtracts,
        };
    }

    private static LooseLoot WithVariantPoints(LooseLoot? live, QuestLootSet? extra)
    {
        var loot = new LooseLoot
        {
            SpawnpointCount = live?.SpawnpointCount,
            SpawnpointsForced = live?.SpawnpointsForced,
            Spawnpoints = live?.Spawnpoints,
        };
        return extra is null ? loot : HalloweenQuestLoot.Add(loot, extra);
    }
}
