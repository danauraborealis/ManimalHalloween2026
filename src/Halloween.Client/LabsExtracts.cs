using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CommonAssets.Scripts.Game;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the event's ways out of Labs. the server closes the normal exits and names two secret ones (car
    // keys for the parking, the collector map for the sewer); the 4.1 scenes hold neither object, so
    // both are made here, at retail's poses, before the game collects its exits. the game's own
    // secret-exit code does the rest: showing it once the item is picked up, the hand-over, the timer.
    internal static class LabsExtracts
    {
#pragma warning disable 0649 // filled by json
        private sealed class Secret
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("position")] public float[] Position;
            [JsonProperty("rotation")] public float[] Rotation;
            [JsonProperty("size")] public float[] Size;
            [JsonProperty("center")] public float[] Center;
        }

        private sealed class Moved
        {
            [JsonProperty("scene")] public string Scene;
            [JsonProperty("name")] public string Name;
            [JsonProperty("from")] public float[] From;
            [JsonProperty("to")] public float[] To;
        }

        private sealed class ExtractFile
        {
            [JsonProperty("secret")] public List<Secret> Secret;
            [JsonProperty("moved")] public List<Moved> Moved;
        }
#pragma warning restore 0649

        private static ExtractFile _file;
        private static bool _loaded;

        private static ExtractFile Load()
        {
            if (_loaded)
            {
                return _file;
            }

            _loaded = true;
            var path = Path.Combine(Plugin.PluginDir, "plugin-data", "labs", "extracts.json");
            if (File.Exists(path))
            {
                _file = JsonConvert.DeserializeObject<ExtractFile>(File.ReadAllText(path));
            }

            return _file;
        }

        // on the first native scene of a Halloween raid. the game finds exits through LocationScene,
        // filled at bake time, so the new ones get a LocationScene of their own that registers on wake
        internal static void Prepare(Scene scene)
        {
            var file = Load();
            if (file == null || file.Secret == null || file.Secret.Count == 0)
            {
                return;
            }

            var host = new GameObject("Halloween2026_LabsExits");
            host.SetActive(false);
            SceneManager.MoveGameObjectToScene(host, scene);
            var registry = host.AddComponent<LocationScene>();

            // its Awake registers every array and throws on a null one
            var fields = typeof(LocationScene).GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType.IsArray && fields[i].GetValue(registry) == null)
                {
                    fields[i].SetValue(registry, Array.CreateInstance(fields[i].FieldType.GetElementType(), 0));
                }
            }

            var points = new ExfiltrationPoint[file.Secret.Count];
            for (var i = 0; i < file.Secret.Count; i++)
            {
                var secret = file.Secret[i];
                var exit = new GameObject(secret.Name);
                exit.transform.SetParent(host.transform, false);
                exit.transform.position = new Vector3(secret.Position[0], secret.Position[1], secret.Position[2]);
                exit.transform.rotation = new Quaternion(secret.Rotation[0], secret.Rotation[1], secret.Rotation[2], secret.Rotation[3]);

                // the collider first: the exit caches it in Awake
                var box = exit.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(secret.Size[0], secret.Size[1], secret.Size[2]);
                box.center = new Vector3(secret.Center[0], secret.Center[1], secret.Center[2]);

                var point = exit.AddComponent<SecretExfiltrationPoint>();
                point.Settings.Name = secret.Name;
                points[i] = point;
            }

            registry.ExfiltrationPoints = points;
            host.SetActive(true);
            Plugin.Log.LogInfo($"[Halloween2026] {points.Length} event exits added to Labs");
        }

        // both gates stand open in the event and the normal exits' consoles sit out of reach
        internal static void MoveNatives()
        {
            var file = Load();
            if (file == null || file.Moved == null)
            {
                return;
            }

            // by name and place in any native scene: this client keeps them in another scene than retail
            var moved = 0;
            var done = new bool[file.Moved.Count];
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded || scene.name == LabsLayer.SceneName
                    || !scene.name.StartsWith(LabsLayer.NativePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var roots = scene.GetRootGameObjects();
                for (var m = 0; m < file.Moved.Count; m++)
                {
                    var row = file.Moved[m];
                    var from = new Vector3(row.From[0], row.From[1], row.From[2]);
                    for (var r = 0; !done[m] && r < roots.Length; r++)
                    {
                        var target = Find(roots[r].transform, row.Name, from);
                        if (target)
                        {
                            target.position = new Vector3(row.To[0], row.To[1], row.To[2]);
                            done[m] = true;
                            moved++;
                        }
                    }
                }
            }

            for (var m = 0; m < done.Length; m++)
            {
                if (!done[m])
                {
                    Plugin.Log.LogWarning($"[Halloween2026] '{file.Moved[m].Name}' was not found in the Labs scenes, it stays where it is");
                }
            }

            Plugin.Log.LogInfo($"[Halloween2026] moved {moved} of {file.Moved.Count} Labs gate and console objects to their event places");
            Report();
        }

        private static Transform Find(Transform root, string name, Vector3 at)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i].name == name && (all[i].position - at).sqrMagnitude < 0.25f)
                {
                    return all[i];
                }
            }

            return null;
        }

        private static void Report()
        {
            var controller = ExfiltrationController.Instance;
            var points = controller != null ? controller.SecretExfiltrationPoints : null;
            if (points == null || points.Length == 0)
            {
                Plugin.Log.LogWarning("[Halloween2026] the game holds no secret exits on this raid");
                return;
            }

            for (var i = 0; i < points.Length; i++)
            {
                if (points[i])
                {
                    Plugin.Log.LogInfo($"[Halloween2026] event exit '{points[i].Settings.Name}': {points[i].Status}");
                }
            }
        }
    }
}
