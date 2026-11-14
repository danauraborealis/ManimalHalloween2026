using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Halloween2026.Client
{
    // the scientist bodies on the event maps. retail gates each one behind a GameTriggers graph
    // (switch -> medical tools check -> 10s plant action -> lootable) that the 4.1 client cannot run,
    // so the same steps are done here: a sealed body offers "Open the body" instead of Search.
    internal static class ScientistBodies
    {
        private const string MedicalTools = "619cc01e0a7c3a1a2731940c";
        private const string BloodSample = "6acc4988059559342f0830d8";
        private const string DeadCivilian = "658420d8085fea07e674cdb6";
        private const float OpenSeconds = 10f;

        private sealed class Sealed
        {
            public int Samples;
            public bool Busy;
        }

        private static readonly Dictionary<LootableContainer, Sealed> Bodies = new Dictionary<LootableContainer, Sealed>();

        internal static void Install(Harmony harmony)
        {
            try
            {
                // the container overload itself: the IInteractive dispatcher just forwards to it
                var target = AccessTools.Method(typeof(InteractionContextHelper), "GetAvailableActions",
                    new[] { typeof(GamePlayerOwner), typeof(LootableContainer) });
                if (target == null)
                {
                    Plugin.Log.LogError("[Halloween2026] GetAvailableActions(LootableContainer) was not found, scientist bodies stay unsealed");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(typeof(ScientistBodies).GetMethod(nameof(Actions), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] patching GetAvailableActions failed: {e}");
            }
        }

        internal static void Spawn(GameWorld world, string alias)
        {
            Bodies.Clear();
            if (alias == null)
            {
                return;
            }

            var containers = UnityEngine.Object.FindObjectsOfType<LootableContainer>(true);
            var found = 0;
            var cloned = 0;
            var bodies = EventData.Bodies;
            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (!string.Equals(body.Raid, alias, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // factory: FactoryClassic's bundle carries these bodies under retail's own ids
                    var container = ById(containers, body.Container);
                    if (container)
                    {
                        // it arrives with dead-civilian loot; a scientist body holds samples and nothing else
                        Empty(container);
                        found++;
                    }
                    else
                    {
                        // labs: no event scenes yet, so a native corpse stands in at the retail spot
                        container = Clone(world, containers, body);
                        if (!container)
                        {
                            continue;
                        }

                        cloned++;
                    }

                    Uncover(container);
                    Bodies[container] = new Sealed { Samples = Roll(body.Loot) };
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Halloween2026] scientist body '{body.Container}' could not be set up: {e}");
                }
            }

            Plugin.Log.LogInfo($"[Halloween2026] {Bodies.Count} scientist bodies sealed ({found} in the scene, {cloned} placed)");
        }

        // retail wraps the body in a switch whose collider sits over the container's and takes the
        // look-at ray; ripped, that switch offers nothing, so the body shows no prompt at all.
        // the same goes for interactive-layer colliders the rip left with no script behind them.
        private static void Uncover(LootableContainer container)
        {
            var silenced = 0;
            var own = container.GetComponents<Collider>();
            for (var i = 0; i < own.Length; i++)
            {
                own[i].enabled = true;
            }

            var inside = container.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < inside.Length; i++)
            {
                if (inside[i].gameObject != container.gameObject && inside[i].enabled)
                {
                    inside[i].enabled = false;
                    silenced++;
                }
            }

            var near = Physics.OverlapSphere(container.transform.position, 1.5f, 1 << container.gameObject.layer, QueryTriggerInteraction.Collide);
            for (var i = 0; i < near.Length; i++)
            {
                if (near[i] && near[i].enabled && !near[i].GetComponentInParent<InteractableObject>())
                {
                    near[i].enabled = false;
                    silenced++;
                }
            }

            if (silenced > 0)
            {
                Plugin.Log.LogInfo($"[Halloween2026] scientist body '{container.Id}': {silenced} collider(s) that hid its prompt switched off");
            }
        }

        private static void Empty(LootableContainer container)
        {
            var root = container.ItemOwner != null ? container.ItemOwner.RootItem as CompoundItem : null;
            if (root == null || root.Grids == null)
            {
                return;
            }

            for (var i = 0; i < root.Grids.Length; i++)
            {
                root.Grids[i].RemoveAll();
            }
        }

        private static LootableContainer ById(LootableContainer[] containers, string id)
        {
            for (var i = 0; i < containers.Length; i++)
            {
                if (containers[i] && containers[i].Id == id)
                {
                    return containers[i];
                }
            }

            return null;
        }

        private static LootableContainer Clone(GameWorld world, LootableContainer[] containers, EventData.Body body)
        {
            LootableContainer source = null;
            for (var i = 0; i < containers.Length && !source; i++)
            {
                var candidate = containers[i];
                if (!candidate)
                {
                    continue;
                }

                var name = PropRoot(candidate.transform).name;
                if (name.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("dead", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    source = candidate;
                }
            }

            if (!source)
            {
                Plugin.Log.LogWarning($"[Halloween2026] no corpse on this map to stand in for scientist body '{body.Container}'");
                return null;
            }

            var root = PropRoot(source.transform);
            // retail's rotation belongs to retail's mesh: keep the stand-in's own lie, take only the heading
            var retail = new Quaternion(body.Rotation[0], body.Rotation[1], body.Rotation[2], body.Rotation[3]);
            var own = root.rotation.eulerAngles;
            var copy = UnityEngine.Object.Instantiate(root.gameObject,
                new Vector3(body.Position[0], body.Position[1], body.Position[2]),
                Quaternion.Euler(own.x, retail.eulerAngles.y, own.z), root.parent);
            copy.name = "Halloween2026_" + body.Container;
            if (!copy.activeSelf)
            {
                copy.SetActive(true);
            }

            // with the event layer in, the real scientist mesh lies here already: the copy is only the container
            var renderers = copy.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = true;
                renderers[i].forceRenderingOff = LabsLayer.Loaded;
            }

            var container = copy.GetComponentInChildren<LootableContainer>(true);
            if (!container)
            {
                UnityEngine.Object.Destroy(copy);
                return null;
            }

            container.enabled = true;
            container.Id = body.Container;
            if (world.World != null)
            {
                world.RegisterWorldInteractionObject(container);
            }

            var template = DeadCivilian;
            var sourceItem = source.ItemOwner != null ? source.ItemOwner.RootItem : null;
            if (sourceItem != null)
            {
                template = sourceItem.StringTemplateId;
            }

            var factory = Singleton<ItemFactory>.Instance;
            var item = factory.CreateItem(factory.NextId, template, null);
            if (item == null)
            {
                UnityEngine.Object.Destroy(copy);
                return null;
            }

            LootItem.CreateLootContainer(container, item, item.ShortName.Localized(null), world, null);
            return container;
        }

        // the nearest LODGroup is the whole prop; a bare container is its own root
        private static Transform PropRoot(Transform from)
        {
            for (var current = from; current; current = current.parent)
            {
                if (current.GetComponent<LODGroup>())
                {
                    return current;
                }
            }

            return from;
        }

        private static int Roll(int[][] loot)
        {
            if (loot == null || loot.Length == 0)
            {
                return 2;
            }

            var total = 0;
            for (var i = 0; i < loot.Length; i++)
            {
                total += loot[i][1];
            }

            var roll = UnityEngine.Random.Range(0, total);
            for (var i = 0; i < loot.Length; i++)
            {
                roll -= loot[i][1];
                if (roll < 0)
                {
                    return loot[i][0];
                }
            }

            return loot[0][0];
        }

        // replaces Search on a sealed body. a postfix, so another mod's prefix cannot skip it.
        private static void Actions(ref AvailableInteractionState __result, GamePlayerOwner owner, LootableContainer container)
        {
            if (!container || !Bodies.TryGetValue(container, out var body))
            {
                return;
            }

            try
            {
                var action = new InteractionAction();
                if (body.Busy || FindMedicalTools(owner.Player) == null)
                {
                    action.Name = body.Busy ? "Interactive/HazardSuitCut".Localized(null) : "Interactive/MedTools".Localized(null);
                    action.Disabled = true;
                }
                else
                {
                    action.Name = "Interactive/HazardSuitBlood".Localized(null);
                    action.Action = () => Begin(owner, container, body);
                }

                __result = new AvailableInteractionState { Actions = new List<InteractionAction> { action } };
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] scientist body actions failed: {e}");
                __result = new AvailableInteractionState { Actions = new List<InteractionAction>() };
            }
        }

        private static Item FindMedicalTools(Player player)
        {
            if (!player || player.Profile == null || player.Profile.Inventory == null)
            {
                return null;
            }

            foreach (var item in player.Profile.Inventory.GetPlayerItems(EPlayerItems.InRaidItems))
            {
                if (item != null && item.StringTemplateId == MedicalTools)
                {
                    return item;
                }
            }

            return null;
        }

        // one set of tools per body, removed the way the game removes a planted quest item
        private static void Consume(Player player)
        {
            try
            {
                var tools = FindMedicalTools(player);
                if (tools == null)
                {
                    return;
                }

                var controller = player.InventoryController;
                var removal = ItemManipulator.Remove(tools, controller, true);
                if (removal.Failed)
                {
                    Plugin.Log.LogWarning($"[Halloween2026] the medical tools could not be used up: {removal.Error}");
                    return;
                }

                controller.TryRunNetworkTransaction(removal, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] using up the medical tools failed: {e}");
            }
        }

        // the game's own plant state: its hold, its animation, its cancel rules
        private static void Begin(GamePlayerOwner owner, LootableContainer container, Sealed body)
        {
            if (!(owner.Player.CurrentState is IdlePlayerState))
            {
                owner.DisplayPreloaderUiNotification("You can't plant quest item while moving".Localized(null));
                return;
            }

            body.Busy = true;
            owner.ShowObjectivesPanel("Interactive/HazardSuitCut".Localized(null) + " {0:F1}", OpenSeconds);
            owner.Player.CurrentManagedState.Plant(true, false, OpenSeconds, success =>
            {
                body.Busy = false;
                owner.CloseObjectivesPanel();
                if (success)
                {
                    Open(container, body);
                    Consume(owner.Player);
                }

                // the prompt still shows the old action until the state is rebuilt
                owner.ClearInteractionState();
            });
        }

        private static void Open(LootableContainer container, Sealed body)
        {
            try
            {
                Bodies.Remove(container);
                var root = container && container.ItemOwner != null ? container.ItemOwner.RootItem as CompoundItem : null;
                if (root == null || root.Grids == null || root.Grids.Length == 0)
                {
                    Plugin.Log.LogWarning("[Halloween2026] opened a scientist body that holds no container item, no blood sample added");
                    return;
                }

                var factory = Singleton<ItemFactory>.Instance;
                var added = 0;
                for (var i = 0; i < body.Samples; i++)
                {
                    var sample = factory.CreateItem(factory.NextId, BloodSample, null);
                    if (sample != null && root.Grids[0].AddAnywhere(sample, EErrorHandlingType.Ignore).Succeeded)
                    {
                        added++;
                    }
                }

                Plugin.Log.LogInfo($"[Halloween2026] scientist body opened, {added} blood sample(s) inside");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] opening a scientist body failed: {e}");
            }
        }
    }
}
