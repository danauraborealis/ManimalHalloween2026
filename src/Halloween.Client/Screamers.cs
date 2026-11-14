using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Halloween2026.Client
{
    // retail 1.3's screamer. its behaviour is client code this project cannot read, so this is ours:
    // on sighting an enemy it screams (the scream clips the 1.3 voice carries) and every infected in
    // earshot is called to that enemy at once, through the game's own zombie call.
    // the 4.1 client has no screamer bot type: the server marks an ordinary infected by its kill reward.
    internal static class Screamers
    {
        // must match the server's HalloweenZombiePatch.ScreamerExperience
        private const int MarkerExperience = 300;
        private const string Section = "Screamer";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _callRadius;
        private static ConfigEntry<float> _cooldown;
        private static BotSpawner _spawner;

        internal static float Cooldown => _cooldown.Value;

        internal static void Install(ConfigFile config)
        {
            _enabled = config.Bind(Section, "Enabled", true,
                "Screamer infected scream when they see an enemy and call the infected around them to it.");
            _callRadius = config.Bind(Section, "Call radius", 70f,
                new ConfigDescription("How far from the screamer, in metres, other infected hear the scream. The map's own zombie settings can cut this short.",
                    new AcceptableValueRange<float>(10f, 300f)));
            _cooldown = config.Bind(Section, "Cooldown", 25f,
                new ConfigDescription("Seconds before the same screamer can scream again.", new AcceptableValueRange<float>(5f, 300f)));
        }

        internal static void RaidStarted()
        {
            try
            {
                if (_spawner != null)
                {
                    _spawner.OnBotCreated -= Created;
                    _spawner = null;
                }

                if (!_enabled.Value || !Singleton<IBotGame>.Instantiated)
                {
                    return;
                }

                var controller = Singleton<IBotGame>.Instance.BotsController;
                if (controller == null || controller.BotSpawner == null)
                {
                    return;
                }

                _spawner = controller.BotSpawner;
                _spawner.OnBotCreated += Created;
                foreach (var bot in controller.Bots.BotOwners)
                {
                    Created(bot);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] screamers could not be set up for this raid: {e}");
            }
        }

        internal static bool IsInfected(BotOwner bot)
        {
            var settings = bot && bot.Profile != null && bot.Profile.Info != null ? bot.Profile.Info.Settings : null;
            return settings != null && settings.Role.ToString().StartsWith("infected", StringComparison.OrdinalIgnoreCase);
        }

        private static void Created(BotOwner bot)
        {
            try
            {
                if (!IsInfected(bot) || bot.Profile.Info.Settings.Experience != MarkerExperience || bot.GetComponent<Screamer>())
                {
                    return;
                }

                bot.gameObject.AddComponent<Screamer>().Owner = bot;
                Plugin.Log.LogInfo("[Halloween2026] a screamer is in this raid");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] marking a screamer failed: {e}");
            }
        }

        internal static void Scream(BotOwner owner, EnemyInfo enemy)
        {
            var target = enemy.Person;
            var at = enemy.CurrPosition;
            owner.GetPlayer.Say(EPhraseTrigger.LocateHostiles, true, 0f, (ETagStatus)0, 100, true);

            // the game's own pursuit, so the called keep following the target after this one call
            BotHalloweenEventPursuit pursuit = null;
            var zombies = owner.BotsController.EventsController != null ? owner.BotsController.EventsController.BotHalloweenWithZombies : null;
            if (zombies != null && zombies._isInited)
            {
                zombies.StartPursuit(owner, target);
                for (var i = 0; i < zombies._activePursuits.Count; i++)
                {
                    if (zombies._activePursuits[i].Target == target)
                    {
                        pursuit = zombies._activePursuits[i];
                    }
                }
            }

            var radius = _callRadius.Value;
            var called = 0;
            foreach (var other in owner.BotsController.Bots.BotOwners)
            {
                if (!other || other == owner || !IsInfected(other) || !other.GetPlayer || !other.GetPlayer.HealthController.IsAlive
                    || (other.Position - owner.Position).sqrMagnitude > radius * radius)
                {
                    continue;
                }

                // earshot was judged above, from the screamer; the game's own check is from the target
                other.GameEventsData.HalloweenGameEvent.Call(at, float.MaxValue, pursuit);
                called++;
            }

            Plugin.Log.LogInfo($"[Halloween2026] screamer {owner.ProfileId} screamed: {called} infected within {radius:0} m called");
        }
    }

    internal sealed class Screamer : MonoBehaviour
    {
        internal BotOwner Owner;

        private float _nextLook;
        private float _nextScream;

        private void Update()
        {
            if (Time.time < _nextLook)
            {
                return;
            }

            _nextLook = Time.time + 0.4f;
            try
            {
                var owner = Owner;
                if (!owner || !owner.GetPlayer || !owner.GetPlayer.HealthController.IsAlive)
                {
                    enabled = false;
                    return;
                }

                var enemy = owner.Memory != null ? owner.Memory.GoalEnemy : null;
                // only while it has eyes on the enemy, and never at another infected
                if (enemy == null || enemy.Person == null || Time.time - enemy.PersonalLastSeenTime > 1.5f || Time.time < _nextScream
                    || (enemy.Person.IsAI && Screamers.IsInfected(enemy.Person.AIData.BotOwner)))
                {
                    return;
                }

                _nextScream = Time.time + Screamers.Cooldown;
                Screamers.Scream(owner, enemy);
            }
            catch (Exception e)
            {
                enabled = false;
                Plugin.Log.LogError($"[Halloween2026] a screamer's scream failed, it stays quiet: {e}");
            }
        }
    }
}
