using System.Reflection;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace Manimal.Halloween2026.Server;

// the "Zombies" block of config.json. retail publishes no numbers for its black division
// infected ("armored, much tougher"), so these defaults are ours
public sealed class ZombieSettings
{
    // black division zombies on/off
    public bool BlackDivisionEnabled { get; set; } = true;
    // share of the listed roles that spawn as a black division zombie, 0..1
    public double BlackDivisionChance { get; set; } = 0.15;
    public List<string> BlackDivisionRoles { get; set; } = ["infectedPmc", "infectedAssault"];
    public double BlackDivisionHealthMultiplier { get; set; } = 2.5;
    public double BlackDivisionHeadHealthMultiplier { get; set; } = 6;
    // head health for every other zombie of the listed roles; 1 leaves them stock.
    // both head multipliers are relative to stock, they dont stack
    public double HeadHealthMultiplier { get; set; } = 4;
    public List<string> HeadHealthRoles { get; set; } = ["infectedPmc", "infectedAssault", "infectedCivil", "infectedLaborant"];
    // share of zombies that rolled the stock Zombie_Fast voice and get the retail 1.3 recording instead, 0..1
    public double RetailFastVoiceChance { get; set; } = 0.5;
    // black division zombies always use the retail 1.3 fast voice
    public bool BlackDivisionRetailVoice { get; set; } = true;
    // upstream's black division zombies carry only the "Infectious powerful strike" (WTT-ContentBackport's item)
    public bool BlackDivisionPowerfulStrike { get; set; } = true;
    // share of the listed roles that spawn as a screamer, 0..1. the 4.1 client has no screamer bot
    // type, so one is an ordinary infected the client plugin recognises and gives the behaviour
    public double ScreamerChance { get; set; } = 0.08;
    public List<string> ScreamerRoles { get; set; } = ["infectedAssault"];
    // upstream's screamer has head 60 where the others have 10
    public double ScreamerHeadHealthMultiplier { get; set; } = 6;
}

// one black division look: the retail prefabs are authored as matching sets
public sealed record BlackDivisionOutfit(MongoId Head, MongoId Body, MongoId Feet);

[Injectable(InjectionType.Singleton)]
public sealed class HalloweenZombieState
{
    public List<BlackDivisionOutfit> Outfits { get; } = [];
    public HashSet<string> BlackDivisionRoles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HeadRoles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool BlackDivisionActive { get; set; }
    public HashSet<string> ScreamerRoles { get; } = new(StringComparer.OrdinalIgnoreCase);
    // set when the item is installed
    public MongoId? PowerfulStrike { get; set; }
    public MongoId OrdinaryStrike { get; } = new("670ad7f1ad195290cd00da7a");
    // set once the retail voice is registered and its bundle is present
    public MongoId? RetailFastVoice { get; set; }
    public MongoId StockFastVoice { get; } = new("67199b72b79a024c140a17b7");
}

// adds the retail 1.3 black division zombie parts and the 1.3 fast voice as customization items and
// switches on the bot-generation patch that hands them out and toughens zombie heads
[Injectable(TypePriority = OnLoadOrder.Preload + 5)]
public sealed class HalloweenZombies(
    ISptLogger<HalloweenZombies> logger,
    HalloweenConfig config,
    TemplateTable templates,
    ICloner cloner,
    HalloweenZombieState state,
    HalloweenZombiePatch patch) : IOnLoad
{
    private const string PrefabDirectory = "assets/content/characters/wip/zombies/zombiesbody/prefabs/";

    // existing zombie parts used as templates for the new entries
    private static readonly MongoId HeadTemplate = new("670d1843186ee334e70ee700");
    private static readonly MongoId BodyTemplate = new("670d197632cadb3aad0a49a5");
    private static readonly MongoId FeetTemplate = new("670fb76578245f17bb00ad85");

    private static readonly MongoId HeadUsec6 = new("5b696c0b5edc1047a8970bd6");
    private static readonly MongoId HeadEric = new("7ab60fd473f22c7f57a50793");
    private static readonly MongoId TopBd1 = new("50fe5d76a2a7eb74bddfdc4f");
    private static readonly MongoId TopBd2 = new("33d1b952695372e6699d4412");
    private static readonly MongoId PantsBd2 = new("2e2aebddc99f56d59b418476");

    // must match the client's voice table entry and tools/zombies/rebuild_zombie_voice.py
    private const string RetailFastVoiceName = "Zombie_Fast_Retail";
    private const string RetailFastVoiceBundle = "assets/content/audio/phrases/zombie_fast_retail.bundle";
    private static readonly MongoId RetailFastVoiceId = new("88ff442e0c71fce3b921cf27");
    private static readonly MongoId PowerfulStrikeId = new("6ad23efa87391cd7c704fbbd");

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var settings = config.Settings.Zombies;
        var headMultiplier = settings.HeadHealthMultiplier;
        if (double.IsFinite(headMultiplier) && headMultiplier > 0 && headMultiplier != 1)
        {
            foreach (var role in settings.HeadHealthRoles)
            {
                state.HeadRoles.Add(role);
            }
        }

        if (!settings.BlackDivisionEnabled)
        {
            logger.Info("[Halloween2026] Black Division zombies are disabled in config.json");
        }
        else
        {
            state.BlackDivisionActive = RegisterBlackDivision(settings);
        }

        // looked for when a bot is made, not here: the item's own mod has not loaded it yet
        if (state.BlackDivisionActive && settings.BlackDivisionPowerfulStrike)
        {
            state.PowerfulStrike = PowerfulStrikeId;
        }

        var wantScreamers = settings.ScreamerChance > 0 && settings.ScreamerRoles.Count > 0;
        if (settings.RetailFastVoiceChance > 0 || (settings.BlackDivisionRetailVoice && state.BlackDivisionActive) || wantScreamers)
        {
            RegisterRetailVoice();
        }

        // the scream is a phrase only the 1.3 voice has, so no voice means no screamers
        if (wantScreamers && state.RetailFastVoice != null)
        {
            foreach (var role in settings.ScreamerRoles)
            {
                state.ScreamerRoles.Add(role);
            }
        }

        if (!state.BlackDivisionActive && state.HeadRoles.Count == 0 && state.RetailFastVoice == null)
        {
            return Task.CompletedTask;
        }

        try
        {
            patch.Enable();
        }
        catch (Exception e)
        {
            logger.Error($"[Halloween2026] could not hook bot generation, zombies stay stock: {e.Message}");
            return Task.CompletedTask;
        }

        if (state.BlackDivisionActive)
        {
            logger.Info($"[Halloween2026] Black Division zombies: {settings.BlackDivisionChance:P0} of {string.Join(", ", state.BlackDivisionRoles)}");
        }

        if (state.HeadRoles.Count > 0)
        {
            logger.Info($"[Halloween2026] zombie head health x{headMultiplier:0.##} for {string.Join(", ", state.HeadRoles)}");
        }

        if (state.RetailFastVoice != null)
        {
            logger.Info($"[Halloween2026] 1.3 zombie voice: {settings.RetailFastVoiceChance:P0} of Zombie_Fast speakers"
                + (settings.BlackDivisionRetailVoice && state.BlackDivisionActive ? ", every Black Division zombie" : ""));
        }

        if (state.ScreamerRoles.Count > 0)
        {
            logger.Info($"[Halloween2026] screamers: {settings.ScreamerChance:P0} of {string.Join(", ", state.ScreamerRoles)}");
        }
        else if (wantScreamers)
        {
            logger.Warning("[Halloween2026] screamers are off: they need the 1.3 zombie voice");
        }

        return Task.CompletedTask;
    }

    private string BundleFile(string key) => Path.Combine(config.ModPath, "bundles", key.Replace('/', Path.DirectorySeparatorChar));

    // the voice name is resolved to a bundle by the client plugin, which ships with this mod
    private void RegisterRetailVoice()
    {
        var bundle = BundleFile(RetailFastVoiceBundle);
        if (!File.Exists(bundle))
        {
            logger.Warning($"[Halloween2026] the 1.3 zombie voice is off: {bundle} is missing");
            return;
        }

        if (!templates.Customization.TryGetValue(state.StockFastVoice, out var stock))
        {
            logger.Warning("[Halloween2026] the 1.3 zombie voice is off: the stock Zombie_Fast voice is missing");
            return;
        }

        var item = cloner.Clone(stock)!;
        item.Id = RetailFastVoiceId;
        item.Name = RetailFastVoiceName;
        item.Properties.Name = RetailFastVoiceName;
        item.Properties.ShortName = RetailFastVoiceName;
        item.Properties.Description = RetailFastVoiceName;
        // voices carry the voice name here, not a prefab path
        item.Properties.Prefab = RetailFastVoiceName;
        templates.Customization[RetailFastVoiceId] = item;
        state.RetailFastVoice = RetailFastVoiceId;
    }

    private bool RegisterBlackDivision(ZombieSettings settings)
    {
        (MongoId Id, MongoId Template, string Name)[] parts =
        [
            (HeadUsec6, HeadTemplate, "zombie_head_bd_usec_6"),
            (HeadEric, HeadTemplate, "zombie_head_bd_usec_eric"),
            (TopBd1, BodyTemplate, "zombie_top_operator_bd1"),
            (TopBd2, BodyTemplate, "zombie_top_operator_bd2"),
            (PantsBd2, FeetTemplate, "zombie_pants_operator_bd2"),
        ];

        // a customization entry without its bundle gives the client a zombie it cant build
        foreach (var part in parts)
        {
            var bundle = BundleFile(PrefabDirectory + part.Name + ".bundle");
            if (!File.Exists(bundle))
            {
                logger.Warning($"[Halloween2026] Black Division zombies are off: {bundle} is missing");
                return false;
            }

            if (!templates.Customization.ContainsKey(part.Template))
            {
                logger.Warning($"[Halloween2026] Black Division zombies are off: zombie customization template {part.Template} is missing");
                return false;
            }
        }

        foreach (var part in parts)
        {
            var item = cloner.Clone(templates.Customization[part.Template])!;
            item.Id = part.Id;
            item.Name = part.Name;
            item.Properties.Name = part.Name;
            item.Properties.ShortName = part.Name;
            item.Properties.Description = part.Name;
            item.Properties.Prefab = new Prefab { Path = PrefabDirectory + part.Name + ".bundle", Rcid = string.Empty };
            templates.Customization[part.Id] = item;
        }

        // retail ships two tops and one pair of trousers
        state.Outfits.Add(new BlackDivisionOutfit(HeadUsec6, TopBd1, PantsBd2));
        state.Outfits.Add(new BlackDivisionOutfit(HeadUsec6, TopBd2, PantsBd2));
        state.Outfits.Add(new BlackDivisionOutfit(HeadEric, TopBd1, PantsBd2));
        state.Outfits.Add(new BlackDivisionOutfit(HeadEric, TopBd2, PantsBd2));
        foreach (var role in settings.BlackDivisionRoles)
        {
            state.BlackDivisionRoles.Add(role);
        }

        return true;
    }
}

// no new WildSpawnType: ordinary infected are adjusted after generation, so the client needs
// no enum prepatch and they keep the stock infected brain
[Injectable(InjectionType.Singleton)]
public sealed class HalloweenZombiePatch : AbstractPatch
{
    private const string Head = "Head";
    // upstream's reward for a screamer, and the mark the client plugin (Screamers.cs) knows one by
    private const int ScreamerExperience = 300;
    private static HalloweenZombiePatch? _instance;

    private readonly HalloweenZombieState _state;
    private readonly HalloweenConfig _config;
    private readonly ISptLogger<HalloweenZombiePatch> _logger;
    private readonly TemplateTable _templates;
    private bool _failureLogged;
    private bool _strikeMissingLogged;

    public HalloweenZombiePatch(HalloweenZombieState state, HalloweenConfig config, TemplateTable templates, ISptLogger<HalloweenZombiePatch> logger)
        : base($"{ModIdentity.Guid}.zombie-bots")
    {
        _state = state;
        _config = config;
        _templates = templates;
        _logger = logger;
        _instance = this;
    }

    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(BotGenerator), nameof(BotGenerator.PrepareAndGenerateBot),
            [typeof(MongoId), typeof(BotGenerationDetails)]);

    [PatchPostfix]
    private static void Postfix(BotGenerationDetails botGenerationDetails, BotBase __result)
    {
        var self = _instance;
        if (self == null || __result == null || botGenerationDetails == null)
        {
            return;
        }

        try
        {
            self.Apply(__result, botGenerationDetails.Role);
        }
        catch (Exception e)
        {
            if (!self._failureLogged)
            {
                self._failureLogged = true;
                self._logger.Error($"[Halloween2026] zombie adjustment failed, the bot is left as generated: {e}");
            }
        }
    }

    private void Apply(BotBase bot, string? role)
    {
        var parts = bot.Health?.BodyParts;
        if (role == null || parts == null)
        {
            return;
        }

        var settings = _config.Settings.Zombies;
        if (_state.BlackDivisionActive && _state.BlackDivisionRoles.Contains(role) && _state.Outfits.Count > 0
            && bot.Customization != null && Random.Shared.NextDouble() < settings.BlackDivisionChance)
        {
            var outfit = _state.Outfits[Random.Shared.Next(_state.Outfits.Count)];
            bot.Customization.Head = outfit.Head;
            bot.Customization.Body = outfit.Body;
            bot.Customization.Feet = outfit.Feet;
            foreach (var (part, health) in parts)
            {
                Scale(health, part == Head ? settings.BlackDivisionHeadHealthMultiplier : settings.BlackDivisionHealthMultiplier);
            }

            if (settings.BlackDivisionRetailVoice && _state.RetailFastVoice != null)
            {
                bot.Customization.Voice = _state.RetailFastVoice;
            }

            if (_state.PowerfulStrike != null)
            {
                if (_templates.Items.ContainsKey(_state.PowerfulStrike.Value))
                {
                    SetMelee(bot, _state.PowerfulStrike.Value);
                }
                else if (!_strikeMissingLogged)
                {
                    _strikeMissingLogged = true;
                    _logger.Warning("[Halloween2026] the Infectious powerful strike is not installed (WTT-ContentBackport has it), Black Division zombies keep their rolled melee");
                }
            }

            return;
        }

        // the kill reward is what the client plugin knows a screamer by, so it is set even when it equals stock
        if (_state.ScreamerRoles.Contains(role) && _state.RetailFastVoice != null && bot.Customization != null
            && bot.Info?.Settings != null && Random.Shared.NextDouble() < settings.ScreamerChance)
        {
            bot.Info.Settings.Experience = ScreamerExperience;
            bot.Customization.Voice = _state.RetailFastVoice;
            if (parts.TryGetValue(Head, out var screamerHead))
            {
                Scale(screamerHead, settings.ScreamerHeadHealthMultiplier);
            }

            // upstream's screamer has bare hands only
            SetMelee(bot, _state.OrdinaryStrike);
            return;
        }

        if (_state.HeadRoles.Contains(role) && parts.TryGetValue(Head, out var head))
        {
            Scale(head, settings.HeadHealthMultiplier);
        }

        // the retail bundle is a variant of the fast voice, so only bots that rolled that voice are swapped
        if (_state.RetailFastVoice != null && bot.Customization != null && bot.Customization.Voice == _state.StockFastVoice
            && Random.Shared.NextDouble() < settings.RetailFastVoiceChance)
        {
            bot.Customization.Voice = _state.RetailFastVoice;
        }
    }

    // the melee item keeps its id and slot, only what it is changes
    private static void SetMelee(BotBase bot, MongoId template)
    {
        var items = bot.Inventory?.Items;
        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.SlotId == "Scabbard")
            {
                item.Template = template;
                return;
            }
        }
    }

    private static void Scale(BodyPartHealth? part, double multiplier)
    {
        if (part?.Health == null || multiplier <= 0 || !double.IsFinite(multiplier))
        {
            return;
        }

        var maximum = Math.Round((part.Health.Maximum ?? 0) * multiplier);
        if (maximum < 1)
        {
            return;
        }

        part.Health.Maximum = maximum;
        part.Health.Current = maximum;
    }
}
