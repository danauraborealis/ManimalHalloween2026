using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace Manimal.Halloween2026.Server;

// an achievement unlocked by a quest has its rewards applied, but spt drops the items among them
// (RewardHelper.AddAchievementToProfile says so itself). ours carry the poster, so on a first unlock
// they are mailed here, the way spt mails the items of an achievement earned in raid.
[Injectable(InjectionType.Singleton)]
public sealed class HalloweenAchievementRewardPatch : AbstractPatch
{
    // retail's system message for achievement rewards, the one spt sends after a raid
    private const string MailTemplate = "670547bb5fa0b1a7c30d5836 0";

    private static readonly HashSet<MongoId> Ours = [];
    private static readonly MethodInfo? RewardItems = typeof(RewardHelper).GetMethod("GetRewardItems",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [typeof(IEnumerable<Reward>), typeof(string)], null);

    private static TemplateTable? _templates;
    private static MailSendService? _mail;
    private static TimeUtil? _time;
    private static ISptLogger<HalloweenAchievementRewardPatch>? _logger;

    public HalloweenAchievementRewardPatch(TemplateTable templates, MailSendService mail, TimeUtil time,
        ISptLogger<HalloweenAchievementRewardPatch> logger)
        : base($"{ModIdentity.Guid}.achievement-rewards")
    {
        _templates = templates;
        _mail = mail;
        _time = time;
        _logger = logger;
    }

    // only our own achievements: another mod's may be paid some other way
    internal bool Arm(IEnumerable<Achievement> achievements)
    {
        foreach (var achievement in achievements)
        {
            if (achievement.Rewards?.Any(reward => reward.Type == SPTarkov.Server.Core.Models.Enums.RewardType.Item) == true)
            {
                Ours.Add(achievement.Id);
            }
        }

        return Ours.Count > 0 && RewardItems is not null;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(RewardHelper).GetMethod(nameof(RewardHelper.AddAchievementToProfile),
                   BindingFlags.Instance | BindingFlags.Public, null, [typeof(SptProfile), typeof(MongoId)], null)
               ?? throw new MissingMethodException("[Halloween2026] RewardHelper.AddAchievementToProfile is unavailable.");
    }

    [PatchPrefix]
    private static void Prefix(SptProfile fullProfile, MongoId achievementId, out bool __state)
    {
        // held already: a repeat of this call must not pay twice
        __state = fullProfile.CharacterData?.PmcData?.Achievements?.ContainsKey(achievementId) == true;
    }

    [PatchPostfix]
    private static void Postfix(RewardHelper __instance, SptProfile fullProfile, MongoId achievementId, bool __state)
    {
        if (__state || !Ours.Contains(achievementId))
        {
            return;
        }

        try
        {
            var session = fullProfile.ProfileInfo?.ProfileId;
            var achievement = _templates?.Achievements.FirstOrDefault(entry => entry.Id == achievementId);
            var version = fullProfile.CharacterData?.PmcData?.Info?.GameVersion;
            if (session is null || achievement?.Rewards is null || version is null)
            {
                return;
            }

            var items = (RewardItems!.Invoke(__instance, [achievement.Rewards, version]) as IEnumerable<Item>)?.ToList();
            if (items is null || items.Count == 0)
            {
                return;
            }

            _mail!.SendLocalisedSystemMessageToPlayer(session.Value, MailTemplate, items, [], _time!.GetHoursAsSeconds(24 * 7));
            _logger?.Info($"[Halloween2026] achievement {achievementId} unlocked by a quest: its {items.Count} reward item(s) mailed");
        }
        catch (Exception e)
        {
            _logger?.Warning($"[Halloween2026] mailing the rewards of achievement {achievementId} failed: {e.Message}");
        }
    }
}
