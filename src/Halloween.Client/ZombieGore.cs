#nullable enable
using System;
using System.Reflection;
using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using HarmonyLib;

namespace Manimal.Halloween2026.Client
{
    // retail 1.3's zombie head trauma: settings, the pack, and the hook that feeds it head hits
    internal static class ZombieGore
    {
        private const string Section = "Zombie gore";

        private static ZombieGoreRuntime? _runtime;
        private static bool _failureLogged;

        internal static void Install(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Section, "Enabled", true,
                "Head hits on infected can open the skull (retail 1.3 gore heads) with a blood burst. Applies live.");
            var headshotChance = config.Bind(Section, "Headshot chance", 0.35f,
                new ConfigDescription("Chance that a head hit the zombie survives opens the skull. Applies live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            var killingChance = config.Bind(Section, "Killing headshot chance", 0.75f,
                new ConfigDescription("Chance that a killing head hit opens the skull. Applies live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            var bloodBurst = config.Bind(Section, "Blood burst", true,
                "Extra blood particles and environment splatter when a skull opens. Applies live.");
            var burstSound = config.Bind(Section, "Burst sound", true,
                "Play the retail wet head-impact sound when a skull opens. Applies live.");
            var meleeHits = config.Bind(Section, "Melee hits", false,
                "Let melee head hits open the skull too, not just bullets, fragments and explosions. Applies live.");

            try
            {
                var runtime = new ZombieGoreRuntime(Plugin.Log);
                void Apply(object? sender = null, EventArgs? args = null)
                {
                    runtime.Current.Enabled = enabled.Value;
                    runtime.Current.HeadshotChance = headshotChance.Value;
                    runtime.Current.KillingHeadshotChance = killingChance.Value;
                    runtime.Current.BloodBurst = bloodBurst.Value;
                    runtime.Current.BurstSound = burstSound.Value;
                    runtime.Current.MeleeHits = meleeHits.Value;
                }

                Apply();
                if (!runtime.LoadPack())
                {
                    return;
                }

                // by parameter types so an inherited or renamed overload cant be picked up. offline bots are
                // plain Players; ClientPlayer overrides this and is left alone
                var target = typeof(Player).GetMethod(nameof(Player.ApplyShot),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null,
                    new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(EArmorPlateCollider), typeof(ShotId) }, null);
                if (target == null)
                {
                    Plugin.Log.LogError("[Halloween2026] Player.ApplyShot was not found, zombie gore is off");
                    runtime.Shutdown();
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(typeof(ZombieGore).GetMethod(nameof(ShotApplied), BindingFlags.Static | BindingFlags.NonPublic)));
                enabled.SettingChanged += Apply;
                headshotChance.SettingChanged += Apply;
                killingChance.SettingChanged += Apply;
                bloodBurst.SettingChanged += Apply;
                burstSound.SettingChanged += Apply;
                meleeHits.SettingChanged += Apply;
                _runtime = runtime;
                Plugin.Log.LogInfo("[Halloween2026] zombie head gore armed");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] zombie head gore could not be set up, everything else is unaffected: {e}");
                _runtime?.Shutdown();
                _runtime = null;
            }
        }

        internal static void Shutdown()
        {
            _runtime?.Shutdown();
            _runtime = null;
        }

        private static void ShotApplied(Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType, object __result)
        {
            // null result = the shot was rejected (already dead, friendly-fire rules)
            var runtime = _runtime;
            if (__result == null || runtime == null)
            {
                return;
            }

            try
            {
                runtime.OnShotApplied(__instance, damageInfo, bodyPartType);
            }
            catch (Exception e)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    Plugin.Log.LogError($"[Halloween2026] zombie gore failed, the hit itself is unaffected: {e}");
                }
            }
        }
    }

    internal static class ZombieVoices
    {
        // the server hands some infected this voice name and the game resolves names through this table.
        // name and path must match the server's HalloweenZombies
        internal static void Register()
        {
            const string voice = "Zombie_Fast_Retail";
            const string bundle = "assets/content/audio/phrases/zombie_fast_retail.bundle";
            try
            {
                if (!InGameBundles._phrasesPaths.ContainsKey(voice))
                {
                    InGameBundles._phrasesPaths[voice] = bundle;
                }

                Plugin.Log.LogInfo("[Halloween2026] zombie voice registered: " + voice);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] the 1.3 zombie voice could not be registered, infected given it by the server will be silent: {e}");
            }
        }
    }
}
