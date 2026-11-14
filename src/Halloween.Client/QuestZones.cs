using System;
using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.Halloween2026.Client
{
    // the event's quest triggers, rebuilt from retail boxes. neither the 4.1 scenes nor
    // FactoryClassic's Halloween bundle carry them.
    internal static class QuestZones
    {
        // a zone belongs to an event raid (by alias) or to a plain map (by location id)
        internal static void Spawn(string locationId, string alias)
        {
            var zones = EventData.Zones;
            var existing = new HashSet<string>(StringComparer.Ordinal);
            var present = UnityEngine.Object.FindObjectsOfType<TriggerWithId>();
            for (var i = 0; i < present.Length; i++)
            {
                if (present[i] && present[i].Id != null)
                {
                    existing.Add(present[i].Id);
                }
            }

            var made = 0;
            for (var i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                var mine = string.Equals(zone.Raid, alias, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(zone.Raid, locationId, StringComparison.OrdinalIgnoreCase);
                // several boxes share one id on purpose, so only a scene-provided one stands us down
                if (!mine || existing.Contains(zone.Id))
                {
                    continue;
                }

                try
                {
                    Create(zone, made);
                    made++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Halloween2026] quest zone '{zone.Id}' could not be created: {e}");
                }
            }

            if (made > 0)
            {
                Plugin.Log.LogInfo($"[Halloween2026] added {made} quest zone(s) to this raid");
            }
        }

        private static void Create(EventData.Zone zone, int index)
        {
            var go = new GameObject($"Halloween2026_{zone.Id}_{index}");
            go.layer = LayerMask.NameToLayer("Triggers");
            go.transform.SetPositionAndRotation(
                new Vector3(zone.Position[0], zone.Position[1], zone.Position[2]),
                new Quaternion(zone.Rotation[0], zone.Rotation[1], zone.Rotation[2], zone.Rotation[3]));
            go.transform.localScale = new Vector3(zone.Scale[0], zone.Scale[1], zone.Scale[2]);

            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(zone.Center[0], zone.Center[1], zone.Center[2]);
            box.size = new Vector3(zone.Size[0], zone.Size[1], zone.Size[2]);

            TriggerWithId trigger = zone.Kind == "place"
                ? (TriggerWithId)go.AddComponent<PlaceItemTrigger>()
                : go.AddComponent<ExperienceTrigger>();
            trigger.SetId(zone.Id);
        }
    }
}
