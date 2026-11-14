using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

// the trader offers the event quests unlock. PostLoad, so offers other mods add at load are already in:
// ManimalCSGas sells the two vaccine grenades under retail's own offer ids, and those get the quest lock
// here instead of a second copy.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public sealed class HalloweenAssorts(
    HalloweenConfig config,
    JsonUtil jsonUtil,
    TemplateTable templateTable,
    TradersTable tradersTable,
    ISptLogger<HalloweenAssorts> logger) : IOnLoad
{
    // upstream prices the grenade trades in retail's cs gas grenade; here that grenade is ManimalCSGas's
    private static readonly MongoId RetailCsGas = new("69f07893e761ac1c200ae15b");
    private static readonly MongoId InstalledCsGas = new("6a5d6a5f4ed8c025a0a2cff0");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (!config.Settings.QuestsEnabled)
        {
            return Task.CompletedTask;
        }

        var path = config.DbPath("content", "assorts.json");
        var offers = jsonUtil.DeserializeFromFile<List<Offer>>(path) ?? [];
        int added = 0, locked = 0, skipped = 0;
        foreach (var offer in offers)
        {
            if (offer.Items.Count == 0 || !tradersTable.TryGetValue(offer.Trader, out var trader) || trader?.Assort is null
                || !templateTable.Quests.ContainsKey(offer.Quest))
            {
                skipped++;
                continue;
            }

            foreach (var cost in offer.Barter)
            {
                if (cost.Template == RetailCsGas)
                {
                    cost.Template = InstalledCsGas;
                }
            }

            // an offer is only real if the thing sold, its parts and its price all exist in this install
            if (offer.Items.Any(item => !templateTable.Items.ContainsKey(item.Template))
                || offer.Barter.Any(cost => !templateTable.Items.ContainsKey(cost.Template)))
            {
                skipped++;
                continue;
            }

            var id = offer.Items[0].Id;
            if (trader.Assort.Items.All(existing => existing.Id != id))
            {
                trader.Assort.Items.AddRange(offer.Items);
                trader.Assort.BarterScheme[id] = [offer.Barter];
                trader.Assort.LoyalLevelItems[id] = offer.Loyalty;
                added++;
            }

            // 4.1 indexes these by lowercase status
            var status = offer.Status.ToLowerInvariant();
            if (!trader.QuestAssort.TryGetValue(status, out var links))
            {
                trader.QuestAssort[status] = links = [];
            }

            links[id] = offer.Quest;
            locked++;
        }

        logger.Info($"[Halloween2026] trader offers: {added} added, {locked} locked behind event quests, {skipped} skipped (their items are not installed)");
        return Task.CompletedTask;
    }

    private sealed class Offer
    {
        [JsonPropertyName("trader")] public MongoId Trader { get; init; }
        [JsonPropertyName("quest")] public MongoId Quest { get; init; }
        [JsonPropertyName("status")] public string Status { get; init; } = "success";
        [JsonPropertyName("loyalty")] public int Loyalty { get; init; } = 1;
        [JsonPropertyName("items")] public List<Item> Items { get; init; } = [];
        [JsonPropertyName("barter")] public List<BarterScheme> Barter { get; init; } = [];
    }
}
