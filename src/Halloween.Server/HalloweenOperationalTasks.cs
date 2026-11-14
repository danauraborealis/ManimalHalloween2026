using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Repeatable;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

namespace Manimal.Halloween2026.Server;

// the event's operational tasks, as retail runs them: each trader has a six-hour set that pays experience,
// roubles and his event item. the kinds, their objectives and their text are retail's (the text is all
// upstream carries of them; the rest is from screenshots and players' reports). Therapist pays the vaccine
// prototype for getting out of an infected map, killing infected, or handing over tissue or blood samples.
// Skier pays infected tissue for supplies, medicine, weapons, dead Scavs or a dead infected boss, and
// always has one task that takes her prototype.
[Injectable(InjectionType.Singleton)]
public sealed class HalloweenOperationalTasks(
    HalloweenConfig config,
    QuestConfig questConfig,
    TemplateTable templateTable,
    LocationTable locationTable,
    RepeatableQuestController controller,
    ProfileHelper profileHelper,
    RandomUtil randomUtil,
    JsonUtil jsonUtil,
    ISptLogger<HalloweenOperationalTasks> logger)
{
    private static readonly MongoId Skier = new("58330581ace78e27b8b10cee");
    private static readonly MongoId Therapist = new("54cb57776803fa99248b456e");
    private static readonly MongoId Tissue = new("6acc4af5ada0d6309902b049");
    private static readonly MongoId Prototype = new("6acc4c757587fb38c508c5fa");
    private static readonly MongoId Blood = new("6acc4988059559342f0830d8");
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    // either side opens both sets: without the flea market each side needs the other trader's item
    private static readonly MongoId[] SideQuests = [new("6abb92e1505493a036006ee0"), new("6abf70a8e4732bc101051bf2")];

    // the client names the target after the first role
    private static readonly List<string> Infected = ["infectedAssault", "infectedCivil", "infectedLaborant", "infectedPmc", "infectedTagilla"];
    // any one of them, wherever he lives
    private static readonly string[] Bosses =
        ["bossBully", "bossGluhar", "bossKilla", "bossKojaniy", "bossSanitar", "bossTagilla", "bossBoar", "bossKolontay", "bossKnight"];
    // retail's event maps are their own locations; here each is a variant the client answers to by that name
    private static readonly (string Alias, string Location)[] InfectedMaps =
    [
        ("laboratory_event", "5b0fc42d86f7744a585f9105"),
        ("factory_event", "55f2d3fd4bdc2d5f408b4567"),
    ];

    // item classes Skier's lists are drawn from, with a ceiling on what one piece may be worth. only things
    // these maps' containers can hold are asked for: a list item has to be findable in raid.
    private static readonly string[] LootMaps = ["bigmap", "interchange", "shoreline", "rezervbase", "lighthouse", "woods", "tarkovstreets"];
    private static readonly MongoId[] Food = [new("5448e8d04bdc2ddf718b4569"), new("5448e8d64bdc2dce718b4568")];
    private static readonly MongoId[] Medicine = [new("5448f3ac4bdc2dce718b4569"), new("5448f39d4bdc2d0a728b4568"), new("5448f3a14bdc2d27728b4569")];
    private static readonly MongoId[] Weapons = [new("5422acb9af1c889c16000029")];
    private const double FoodCeiling = 30000;
    private const double MedicineCeiling = 40000;
    private const double WeaponCeiling = 60000;

    // three tasks, five at elite charisma: retail's count grows with the skill, and only its top is known
    private const int BaseTasks = 3;
    private const int EliteTasks = 5;
    private const double EliteCharisma = 5100;

    private enum Goal
    {
        Exit,
        KillInfected,
        KillScavs,
        KillBoss,
        HandItem,
        HandFood,
        HandMedicine,
        HandWeapon,
    }

    // a retail task: its template id and the id its text hangs from, "<template> description <trader> <variant>".
    // a handover of one fixed item names it, how many, and whether it must be found in raid.
    private sealed record TaskKind(Goal Goal, string Template, string Variant, MongoId? Item = null, int Min = 1, int Max = 1, bool FoundInRaid = false);

    // Always is the one task a set never goes without
    private sealed record TaskSet(string Name, MongoId Id, MongoId Trader, MongoId Pays, TaskKind[] Kinds, TaskKind? Always);

    private static readonly TaskSet[] Sets =
    [
        new("Halloween2026_Skier", new MongoId("6ae400000000000000da2001"), Skier, Tissue,
        [
            new TaskKind(Goal.HandFood, "6abb9e0496c356eaac01c10f", "6adec46298a366fd540311ec", null, 2, 2, true),
            new TaskKind(Goal.KillScavs, "6ad741d45b05fde5460c5793", "6adec4677aa8b462ac07c623", null, 10, 15),
            new TaskKind(Goal.KillBoss, "6adeadedc8ab7d23b00981b5", "6adec46bffeed6bf20016ec0"),
            new TaskKind(Goal.HandMedicine, "6adeae4d4285d31e390b6326", "6adec46f1fd1609eb901c25c", null, 2, 3, true),
            new TaskKind(Goal.HandWeapon, "6adeae5016c962099903ad10", "6adec4734285d31e390b6332", null, 1, 1, true),
        ],
        new TaskKind(Goal.HandItem, "6adeaf97300fa727b808c793", "6adec4815c23027bfa094865", Prototype)),
        new("Halloween2026_Therapist", new MongoId("6ae400000000000000da2002"), Therapist, Prototype,
        [
            new TaskKind(Goal.Exit, "6ad7444a476271984c00e21f", "6adec3b97aa8b462ac07c621"),
            new TaskKind(Goal.KillInfected, "6adeb28b3fea8e5d570184be", "6adec36aed428ae72606b3d6", null, 20, 40),
            new TaskKind(Goal.HandItem, "6adeb514402c23693c08e645", "6adec075ffeed6bf20016eba", Tissue, 1, 3),
            new TaskKind(Goal.HandItem, "6adeb41c402c23693c08e641", "6adec3fe402c23693c08e66a", Blood, 1, 3, true),
        ], null),
    ];

    private static readonly MethodInfo? GenerateQuestPool = typeof(RepeatableQuestController).GetMethod(
        "GenerateQuestPool", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RepeatableQuestConfig), typeof(int)], null);

    // which period of which profile has been filled, and whose guaranteed task was just swapped away
    private readonly Dictionary<string, long> _filled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _swappedAlways = new(StringComparer.Ordinal);
    private readonly Dictionary<Goal, List<MongoId>> _lists = [];
    private Dictionary<MongoId, double>? _prices;
    private HashSet<MongoId>? _findable;

    internal bool Register()
    {
        if (!config.Settings.OperationalTasksEnabled)
        {
            return false;
        }

        var daily = questConfig.RepeatableQuests.FirstOrDefault(set => set.Name == "Daily");
        if (daily is null || !templateTable.Items.ContainsKey(Tissue) || !templateTable.Items.ContainsKey(Prototype))
        {
            logger.Warning("[Halloween2026] no Daily task set or event items to build the event's operational tasks from");
            return false;
        }

        foreach (var set in Sets)
        {
            if (questConfig.RepeatableQuests.Any(existing => existing.Name == set.Name))
            {
                continue;
            }

            // a copy of Daily keeps its reward scaling and task templates. it starts each period as three
            // random tasks, and every one of them is then rewritten into one of the set's kinds.
            var copy = jsonUtil.Deserialize<RepeatableQuestConfig>(jsonUtil.Serialize(daily)!)!;
            var trader = copy.TraderWhitelist.FirstOrDefault(entry => entry.TraderId == set.Trader) ?? copy.TraderWhitelist.First();
            List<string> types = ["Elimination", "Completion", "Exploration"];
            trader.TraderId = set.Trader;
            trader.QuestTypes = [.. types];
            copy.TraderWhitelist = [trader];
            copy.Id = set.Id;
            copy.Name = set.Name;
            copy.Types = types;
            copy.NumQuests = BaseTasks;
            copy.ResetTime = 6 * 3600;
            copy.MinPlayerLevel = 1;
            questConfig.RepeatableQuests.Add(copy);
        }

        if (GenerateQuestPool is null)
        {
            logger.Warning("[Halloween2026] this SPT build has no GenerateQuestPool, the event sets stay ordinary operational tasks");
        }

        logger.Info("[Halloween2026] operational tasks: Skier pays infected tissue, Therapist pays the vaccine prototype, six-hour sets of retail's kinds");
        return true;
    }

    private static TaskSet? SetFor(string? name) => Sets.FirstOrDefault(set => set.Name == name);

    internal bool? CanAccess(RepeatableQuestConfig repeatable, PmcData pmc)
    {
        if (SetFor(repeatable.Name) is null)
        {
            return null;
        }

        return pmc.Quests?.Any(quest => quest.Status == QuestStatusEnum.Success && SideQuests.Contains(quest.QId)) == true;
    }

    internal void Decorate(MongoId sessionId, List<PmcDataRepeatableQuest> periods)
    {
        foreach (var period in periods)
        {
            var set = SetFor(period.Name);
            if (set is not null && period.ActiveQuests is not null)
            {
                Shape(sessionId, period, set);
            }
        }
    }

    // spt is about to swap a task out; the set's guaranteed one has to come back as itself
    internal void BeforeChange(PmcData pmc, MongoId sessionId, MongoId questId)
    {
        foreach (var period in pmc.RepeatableQuests ?? [])
        {
            var set = SetFor(period.Name);
            var quest = period.ActiveQuests?.FirstOrDefault(entry => entry.Id == questId);
            if (set?.Always is not null && quest is not null && IsKind(quest, set.Always))
            {
                _swappedAlways.Add($"{sessionId}:{set.Name}");
            }
        }
    }

    // the swapped-in task is a random one built by spt alone, and the copy the client is sent was cloned before we see it
    internal void AfterChange(PmcData pmc, MongoId sessionId, IEnumerable<PmcDataRepeatableQuest>? sent)
    {
        if (sent is null || pmc.RepeatableQuests is null)
        {
            return;
        }

        foreach (var copy in sent)
        {
            var set = SetFor(copy.Name);
            var period = pmc.RepeatableQuests.FirstOrDefault(entry => entry.Name == copy.Name);
            if (set is null || period?.ActiveQuests is null || copy.ActiveQuests is null)
            {
                continue;
            }

            Shape(sessionId, period, set);
            for (var i = 0; i < copy.ActiveQuests.Count; i++)
            {
                var live = period.ActiveQuests.FirstOrDefault(entry => entry.Id == copy.ActiveQuests[i].Id);
                if (live is not null)
                {
                    copy.ActiveQuests[i] = jsonUtil.Deserialize<RepeatableQuest>(jsonUtil.Serialize(live)!)!;
                }
            }
        }
    }

    private static List<Reward> Success(RepeatableQuest quest)
    {
        quest.Rewards ??= [];
        if (!quest.Rewards.TryGetValue("Success", out var rewards) || rewards is null)
        {
            quest.Rewards["Success"] = rewards = [];
        }

        return rewards;
    }

    private static void AddPayment(RepeatableQuest quest, MongoId item, int count)
    {
        var rewards = Success(quest);
        var root = new MongoId();
        rewards.Add(new Reward
        {
            Id = new MongoId(),
            Type = RewardType.Item,
            Index = rewards.Count,
            Target = root.ToString(),
            Value = count,
            FindInRaid = true,
            Items = [new Item { Id = root, Template = item, Upd = new Upd { StackObjectsCount = count } }],
        });
    }

    private static QuestCondition? Handover(RepeatableQuest quest)
    {
        return quest.Conditions.AvailableForFinish?.FirstOrDefault(condition => condition.ConditionType == "HandoverItem");
    }

    // a task carries its kind in its text key, which is in the profile and so outlives a restart
    private static bool IsKind(RepeatableQuest quest, TaskKind kind) => quest.Description?.StartsWith(kind.Template, StringComparison.Ordinal) == true;

    private static bool IsOurs(RepeatableQuest quest, TaskSet set)
    {
        return set.Kinds.Any(kind => IsKind(quest, kind)) || (set.Always is not null && IsKind(quest, set.Always));
    }

    private static string TypeOf(Goal goal) => goal switch
    {
        Goal.Exit => "Exploration",
        Goal.KillInfected or Goal.KillScavs or Goal.KillBoss => "Elimination",
        _ => "Completion",
    };

    private static int TasksFor(PmcData pmc)
    {
        var charisma = pmc.Skills?.Common?.FirstOrDefault(skill => skill.Id.ToString() == "Charisma")?.Progress ?? 0;
        return charisma >= EliteCharisma ? EliteTasks : BaseTasks;
    }

    private bool Possible(TaskKind kind)
    {
        return kind.Goal switch
        {
            Goal.HandItem => kind.Item is not null && templateTable.Items.ContainsKey(kind.Item.Value),
            Goal.HandFood or Goal.HandMedicine or Goal.HandWeapon => ListFor(kind.Goal).Count > 0,
            _ => true,
        };
    }

    // a kind the set does not hold yet, while there is one
    private TaskKind? Pick(TaskSet set, List<RepeatableQuest> active)
    {
        var kinds = set.Kinds.Where(Possible).ToList();
        if (kinds.Count == 0)
        {
            return null;
        }

        var missing = kinds.Where(kind => !active.Any(other => IsKind(other, kind))).ToList();
        var pool = missing.Count > 0 ? missing : kinds;
        return pool[randomUtil.GetInt(0, pool.Count - 1)];
    }

    // every task that is not yet one of the set's kinds becomes one. it keeps its id, so the slot spt made
    // for it (and the copy a swap already sent) still points at it. a new period is then filled up, once.
    private void Shape(MongoId sessionId, PmcDataRepeatableQuest period, TaskSet set)
    {
        var active = period.ActiveQuests!;
        var pmc = profileHelper.GetFullProfile(sessionId).CharacterData?.PmcData;
        if (pmc is null || GenerateQuestPool is null)
        {
            return;
        }

        var key = $"{sessionId}:{set.Name}";
        var ends = period.EndTime ?? 0;
        var fresh = !(_filled.TryGetValue(key, out var filledFor) && filledFor == ends);
        // owed on a new period, and again when the player swapped it away; not when he handed it in
        var owed = set.Always is not null && Possible(set.Always) && !active.Any(quest => IsKind(quest, set.Always))
            && (_swappedAlways.Remove(key) || (fresh && !active.Any(quest => IsOurs(quest, set))));

        for (var i = 0; i < active.Count; i++)
        {
            var quest = active[i];
            // one the player already took keeps its counters; rewriting it would strand them
            if (IsOurs(quest, set) || pmc.Quests?.Any(taken => taken.QId == quest.Id) == true)
            {
                continue;
            }

            var kind = owed ? set.Always : Pick(set, active);
            var made = kind is null ? null : Make(sessionId, pmc, set, kind);
            if (made is null)
            {
                logger.Warning($"[Halloween2026] could not build an event task ({kind?.Goal}) for {set.Name}, an ordinary one stands in until the next reset");
                continue;
            }

            owed = owed && kind != set.Always;
            made.Id = quest.Id;
            active[i] = made;
        }

        if (!fresh)
        {
            return;
        }

        _filled[key] = ends;
        for (var wanted = TasksFor(pmc); active.Count < wanted;)
        {
            var kind = Pick(set, active);
            var made = kind is null ? null : Make(sessionId, pmc, set, kind);
            if (made is null)
            {
                break;
            }

            active.Add(made);
            period.ChangeRequirement.TryAdd(made.Id, new ChangeRequirement { ChangeCost = made.ChangeCost, ChangeStandingCost = 0 });
        }
    }

    // one task of the given type, built by spt's own generator for this set
    private RepeatableQuest? Generate(MongoId sessionId, PmcData pmc, TaskSet set, string type)
    {
        var repeatable = questConfig.RepeatableQuests.FirstOrDefault(entry => entry.Name == set.Name);
        if (repeatable is null || pmc.Info is null || pmc.TradersInfo is null || GenerateQuestPool is null)
        {
            return null;
        }

        var level = pmc.Info.Level ?? 1;
        if (GenerateQuestPool.Invoke(controller, [repeatable, level]) is not QuestTypePool pool)
        {
            return null;
        }

        pool.Types = [type];
        return controller.PickAndGenerateRandomRepeatableQuest(sessionId, level, pmc.TradersInfo, pool, repeatable);
    }

    // spt builds a task of the right type with its rewards; the objective, the payout and the text are then ours
    private RepeatableQuest? Make(MongoId sessionId, PmcData pmc, TaskSet set, TaskKind kind)
    {
        var quest = Generate(sessionId, pmc, set, TypeOf(kind.Goal));
        if (quest?.Conditions?.AvailableForFinish is not { Count: > 0 } finish)
        {
            return null;
        }

        var count = randomUtil.GetInt(kind.Min, kind.Max);
        // Therapist: that many prototypes a sample or up to two more, 2-4 for getting out, 1-4 for kills.
        // Skier: 1-4 for anything, and one for the prototype he asks for.
        var paid = randomUtil.GetInt(1, 4);
        quest.Location = "any";
        if (TypeOf(kind.Goal) == "Completion")
        {
            var handover = Handover(quest);
            var item = kind.Goal == Goal.HandItem ? kind.Item : Draw(kind.Goal);
            if (handover is null || item is null)
            {
                return null;
            }

            handover.Target = new ListOrT<string>([item.Value.ToString()], null);
            handover.Value = count;
            handover.OnlyFoundInRaid = kind.FoundInRaid;
            // retail wants medicine nearly unused
            handover.MinDurability = kind.Goal == Goal.HandMedicine ? 90 : 0;
            handover.MaxDurability = 100;
            quest.Conditions.AvailableForFinish = [handover];
            if (kind == set.Always)
            {
                paid = 1;
            }
            else if (set.Trader == Therapist)
            {
                paid = count + randomUtil.GetInt(0, 2);
            }
        }
        else
        {
            var counter = finish[0];
            if (counter.Counter is null)
            {
                return null;
            }

            counter.Counter.Id = new MongoId();
            counter.Id = new MongoId();
            if (kind.Goal == Goal.Exit)
            {
                var maps = config.Settings.InfectedFactoryEnabled ? InfectedMaps : InfectedMaps[..1];
                var map = maps[randomUtil.GetInt(0, maps.Length - 1)];
                counter.Counter.Conditions =
                [
                    new QuestConditionCounterCondition { Id = new MongoId(), DynamicLocale = true, Status = ["Survived"], ConditionType = "ExitStatus" },
                    new QuestConditionCounterCondition
                    {
                        Id = new MongoId(),
                        DynamicLocale = true,
                        Target = new ListOrT<string>([map.Alias], null),
                        ConditionType = "Location",
                    },
                ];
                quest.Location = map.Location;
                paid = randomUtil.GetInt(2, 4);
            }
            else
            {
                var kills = new QuestConditionCounterCondition
                {
                    Id = new MongoId(),
                    DynamicLocale = true,
                    Target = new ListOrT<string>(null, "Savage"),
                    Value = 1,
                    ResetOnSessionEnd = false,
                    EnemyHealthEffects = [],
                    Daytime = new DaytimeCounter { From = 0, To = 0 },
                    ConditionType = "Kills",
                };
                if (kind.Goal != Goal.KillScavs)
                {
                    kills.SavageRole = kind.Goal == Goal.KillBoss ? [Bosses[randomUtil.GetInt(0, Bosses.Length - 1)]] : Infected;
                }

                counter.Counter.Conditions = [kills];
            }

            counter.Value = count;
            quest.Conditions.AvailableForFinish = [counter];
        }

        // retail pays experience, roubles and the event item, nothing else
        var rewards = Success(quest);
        rewards.RemoveAll(reward => reward.Type != RewardType.Experience
            && !(reward.Type == RewardType.Item && reward.Items?.Any(item => item.Template == Roubles) == true));
        for (var i = 0; i < rewards.Count; i++)
        {
            rewards[i].Index = i;
        }

        AddPayment(quest, set.Pays, paid);
        quest.Side = PlayerGroup.Pmc.ToString();
        quest.Description = $"{kind.Template} description {set.Trader} {kind.Variant}";
        quest.SuccessMessageText = $"{kind.Template} successMessageText {set.Trader} {kind.Variant}";
        quest.StartedMessageText = $"{kind.Template} startedMessageText {set.Trader} {kind.Variant}";
        quest.FailMessageText = $"{kind.Template} failMessageText {set.Trader} {kind.Variant}";
        quest.ChangeQuestMessageText = $"{kind.Template} changeQuestMessageText {set.Trader} {kind.Variant}";
        // a clone, so nothing of spt's generator state is shared with the profile
        return jsonUtil.Deserialize<RepeatableQuest>(jsonUtil.Serialize(quest)!);
    }

    private MongoId? Draw(Goal goal)
    {
        var list = ListFor(goal);
        return list.Count == 0 ? null : list[randomUtil.GetInt(0, list.Count - 1)];
    }

    // every ordinary item of the class that containers hold and the handbook prices under the ceiling
    private List<MongoId> ListFor(Goal goal)
    {
        if (_lists.TryGetValue(goal, out var known))
        {
            return known;
        }

        if (_findable is null)
        {
            _findable = [];
            foreach (var map in LootMaps)
            {
                var containers = locationTable.GetLocation(map)?.StaticLoot?.Value;
                if (containers is null)
                {
                    continue;
                }

                foreach (var container in containers.Values)
                {
                    foreach (var row in container.ItemDistribution ?? [])
                    {
                        _findable.Add(row.Tpl);
                    }
                }
            }
        }

        var (classes, ceiling) = goal switch
        {
            Goal.HandFood => (Food, FoodCeiling),
            Goal.HandMedicine => (Medicine, MedicineCeiling),
            _ => (Weapons, WeaponCeiling),
        };
        _prices ??= templateTable.Handbook.Items.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First().Price ?? 0);
        var list = new List<MongoId>();
        foreach (var (id, item) in templateTable.Items)
        {
            if (item.Type != "Item" || item.Properties?.QuestItem == true || !_findable.Contains(id)
                || !_prices.TryGetValue(id, out var price) || price <= 0 || price > ceiling)
            {
                continue;
            }

            for (var parent = item.Parent; !parent.IsEmpty && templateTable.Items.TryGetValue(parent, out var above); parent = above.Parent)
            {
                if (classes.Contains(parent))
                {
                    list.Add(id);
                    break;
                }
            }
        }

        _lists[goal] = list;
        return list;
    }
}

// the event sets are open by quest progress, not by level
[Injectable(InjectionType.Singleton)]
public sealed class HalloweenTaskAccessPatch : AbstractPatch
{
    private static HalloweenOperationalTasks? _tasks;

    public HalloweenTaskAccessPatch(HalloweenOperationalTasks tasks)
        : base($"{ModIdentity.Guid}.task-access")
    {
        _tasks = tasks;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(RepeatableQuestController).GetMethod("CanProfileAccessRepeatableQuests",
                   BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                   [typeof(RepeatableQuestConfig), typeof(PmcData)], null)
               ?? throw new MissingMethodException("[Halloween2026] RepeatableQuestController.CanProfileAccessRepeatableQuests is unavailable.");
    }

    [PatchPostfix]
    private static void Postfix(RepeatableQuestConfig repeatableConfig, PmcData pmcData, ref bool __result)
    {
        var answer = _tasks?.CanAccess(repeatableConfig, pmcData);
        if (answer.HasValue)
        {
            __result = answer.Value;
        }
    }
}

// runs on every fetch, not only on a reset: that is when a new period's random tasks become the event's
[Injectable(InjectionType.Singleton)]
public sealed class HalloweenTaskRewardPatch : AbstractPatch
{
    private static HalloweenOperationalTasks? _tasks;
    private static ISptLogger<HalloweenTaskRewardPatch>? _logger;

    public HalloweenTaskRewardPatch(HalloweenOperationalTasks tasks, ISptLogger<HalloweenTaskRewardPatch> logger)
        : base($"{ModIdentity.Guid}.task-reward")
    {
        _tasks = tasks;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(RepeatableQuestController).GetMethod(nameof(RepeatableQuestController.GetClientRepeatableQuests),
                   BindingFlags.Instance | BindingFlags.Public, null, [typeof(MongoId)], null)
               ?? throw new MissingMethodException("[Halloween2026] RepeatableQuestController.GetClientRepeatableQuests is unavailable.");
    }

    [PatchPostfix]
    private static void Postfix(MongoId sessionID, List<PmcDataRepeatableQuest> __result)
    {
        try
        {
            _tasks?.Decorate(sessionID, __result);
        }
        catch (Exception e)
        {
            _logger?.Warning($"[Halloween2026] shaping the event's operational tasks failed: {e.Message}");
        }
    }
}

[Injectable(InjectionType.Singleton)]
public sealed class HalloweenTaskChangePatch : AbstractPatch
{
    private static HalloweenOperationalTasks? _tasks;
    private static ISptLogger<HalloweenTaskChangePatch>? _logger;

    public HalloweenTaskChangePatch(HalloweenOperationalTasks tasks, ISptLogger<HalloweenTaskChangePatch> logger)
        : base($"{ModIdentity.Guid}.task-change")
    {
        _tasks = tasks;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(RepeatableQuestController).GetMethod(nameof(RepeatableQuestController.ChangeRepeatableQuest),
                   BindingFlags.Instance | BindingFlags.Public, null,
                   [typeof(PmcData), typeof(RepeatableQuestChangeRequest), typeof(MongoId)], null)
               ?? throw new MissingMethodException("[Halloween2026] RepeatableQuestController.ChangeRepeatableQuest is unavailable.");
    }

    [PatchPrefix]
    private static void Prefix(PmcData pmcData, RepeatableQuestChangeRequest changeRequest, MongoId sessionID)
    {
        try
        {
            _tasks?.BeforeChange(pmcData, sessionID, changeRequest.QuestId);
        }
        catch (Exception e)
        {
            _logger?.Warning($"[Halloween2026] noting a swapped operational task failed: {e.Message}");
        }
    }

    [PatchPostfix]
    private static void Postfix(PmcData pmcData, MongoId sessionID, ItemEventRouterResponse __result)
    {
        try
        {
            if (__result?.ProfileChanges is not null && __result.ProfileChanges.TryGetValue(sessionID, out var changes))
            {
                _tasks?.AfterChange(pmcData, sessionID, changes?.RepeatableQuests);
            }
        }
        catch (Exception e)
        {
            _logger?.Warning($"[Halloween2026] shaping a swapped operational task failed: {e.Message}");
        }
    }
}
