using System;
using System.Reflection;
using EFT;
using EFT.Quests;
using HarmonyLib;

namespace Manimal.Halloween2026.Client
{
    // retail's event maps are their own locations (laboratory_event, factory_event). here they are
    // variants of laboratory / factory4_*, so the quests keep the retail names and this answers to
    // them only while a Halloween variant is the raid. a vanilla Labs or Factory raid never counts.
    internal static class EventRaid
    {
        internal const string LabsEvent = "laboratory_event";
        internal const string FactoryEvent = "factory_event";

        // latched at raid start and held past raid end: exit-status objectives are judged after
        // MapVariants has already forgotten the choice
        internal static string LocationId { get; private set; }
        internal static string Alias { get; private set; }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(GameWorld), nameof(GameWorld.OnGameStarted), Type.EmptyTypes, null, nameof(GameStarted));
            Patch(harmony, typeof(ConditionLocationProgressChecker), nameof(ConditionLocationProgressChecker.Test),
                new[] { typeof(object) }, null, nameof(LocationTested));
        }

        // one patch failing must not take the other with it
        private static void Patch(Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, name, args);
                if (target == null)
                {
                    Plugin.Log.LogError($"[Halloween2026] {type.Name}.{name} was not found, that patch is skipped");
                    return;
                }

                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(EventRaid).GetMethod(prefix, flags)),
                    postfix == null ? null : new HarmonyMethod(typeof(EventRaid).GetMethod(postfix, flags)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] patching {type.Name}.{name} failed: {e}");
            }
        }

        private static void GameStarted(GameWorld __instance)
        {
            try
            {
                LocationId = __instance.LocationId;
                Alias = AliasFor(LocationId, MapVariantsApi.CurrentLocationId, MapVariantsApi.CurrentVariant);
                Plugin.Log.LogInfo(Alias == null
                    ? $"[Halloween2026] raid on '{LocationId}', not a Halloween variant"
                    : $"[Halloween2026] raid on '{LocationId}' is Halloween 2026, counting as '{Alias}'");
                QuestZones.Spawn(LocationId, Alias);
                if (Alias == LabsEvent)
                {
                    LabsLayer.HideRemovedNatives();
                    LabsExtracts.MoveNatives();
                    ZombieArmDrawers.Spawn();
                    ElevatorScares.Spawn();
                    var ambience = LabsLayer.Loaded && Plugin.LabsEventAmbience.Value
                        ? LabsAmbience.Start(UnityEngine.SceneManagement.SceneManager.GetSceneByName(LabsLayer.SceneName), "labs")
                        : 0;
                    LabsBlackout.Apply();
                    LabsPowerDown.Apply(ambience > 0);
                }

                if (string.Equals(LocationId, "lighthouse", StringComparison.OrdinalIgnoreCase))
                {
                    LighthouseRoom.RaidStarted(__instance);
                }

                Screamers.RaidStarted();
                ScientistBodies.Spawn(__instance, Alias);
                Cameras.Spawn(__instance, LocationId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] raid start handling failed: {e}");
            }
        }

        // labs is ours ("backport" is the Halloween tile); factory's is FactoryClassic's declared variant
        internal static string AliasFor(string raidLocation, string managedLocation, string variant)
        {
            if (string.IsNullOrEmpty(raidLocation) || variant == null
                || !string.Equals(raidLocation, managedLocation, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Same(raidLocation, Api.Labs.LocationId) && Same(variant, InfectedLabs.VariantId))
            {
                return LabsEvent;
            }

            if (raidLocation.StartsWith("factory4", StringComparison.OrdinalIgnoreCase) && Same(variant, "halloween2026"))
            {
                return FactoryEvent;
            }

            return null;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static void LocationTested(ConditionLocationProgressChecker __instance, object testValue, ref bool __result)
        {
            if (__result || Alias == null || !(testValue is string))
            {
                return;
            }

            var targets = __instance.Condition?.target;
            if (targets == null)
            {
                return;
            }

            for (var i = 0; i < targets.Length; i++)
            {
                if (Same(targets[i], Alias))
                {
                    __result = true;
                    return;
                }
            }
        }
    }
}
