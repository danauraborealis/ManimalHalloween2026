using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the TerraGroup cameras of Halloween: Blind the Enemy, 14 across Customs, Woods, Shoreline, Reserve
    // and Lighthouse. retail's ShootableQuestLocationObject does not exist in 4.1, so each one is the
    // retail prop with a ballistic collider that reports its own id as a visited place when shot.
    internal static class Cameras
    {
        private const string BundleFile = "manimal_halloween2026_props.bundle";
        private const string SceneName = "Halloween2026_Props";
        private const string TemplateName = "CameraTemplate";

#pragma warning disable 0649 // filled by json
        private sealed class Placement
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("raid")] public string Raid;
            [JsonProperty("position")] public float[] Position;
            [JsonProperty("rotation")] public float[] Rotation;
            [JsonProperty("scale")] public float[] Scale;
        }

        private sealed class CameraFile
        {
            [JsonProperty("cameras")] public List<Placement> Cameras;
        }
#pragma warning restore 0649

        private static List<Placement> _all;
        private static AssetBundle _bundle;
        private static bool _bundleTried;

        internal static void Spawn(GameWorld world, string locationId)
        {
            var mine = new List<Placement>();
            var all = Load();
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Raid, locationId, StringComparison.OrdinalIgnoreCase))
                {
                    mine.Add(all[i]);
                }
            }

            if (mine.Count == 0 || !EnsureBundle())
            {
                return;
            }

            // never unloaded from here: at raid end the game lists every loaded scene and unloads each by
            // name, and one that is already gone by then throws and leaves the player on the loading screen
            var load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            load.completed += _ =>
            {
                try
                {
                    Place(mine);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Halloween2026] placing the quest cameras failed: {e}");
                }
            };
        }

        private static void Place(List<Placement> placements)
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            GameObject template = null;
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == TemplateName)
                {
                    template = roots[i];
                }
            }

            if (!template)
            {
                Plugin.Log.LogError("[Halloween2026] the props bundle has no camera template");
                return;
            }

            RebindShaders(template);
            for (var i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                var camera = UnityEngine.Object.Instantiate(template,
                    new Vector3(p.Position[0], p.Position[1], p.Position[2]),
                    new Quaternion(p.Rotation[0], p.Rotation[1], p.Rotation[2], p.Rotation[3]));
                SceneManager.MoveGameObjectToScene(camera, scene);
                camera.name = "Halloween2026_" + p.Id;
                camera.transform.localScale = new Vector3(p.Scale[0], p.Scale[1], p.Scale[2]);
                camera.SetActive(true);
                camera.AddComponent<ShootableCamera>().Id = p.Id;
            }

            Plugin.Log.LogInfo($"[Halloween2026] placed {placements.Count} quest cameras");
        }

        // the bundle's shaders are stand-ins under the game's names
        private static void RebindShaders(GameObject template)
        {
            var renderers = template.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var materials = renderers[i].sharedMaterials;
                for (var m = 0; m < materials.Length; m++)
                {
                    var material = materials[m];
                    if (!material || !material.shader)
                    {
                        continue;
                    }

                    var real = Shader.Find(material.shader.name);
                    if (real && real != material.shader)
                    {
                        var queue = material.renderQueue;
                        material.shader = real;
                        material.renderQueue = queue;
                    }
                }
            }
        }

        private static List<Placement> Load()
        {
            if (_all != null)
            {
                return _all;
            }

            _all = new List<Placement>();
            var path = Path.Combine(Plugin.PluginDir, "plugin-data", "cameras.json");
            if (File.Exists(path))
            {
                var file = JsonConvert.DeserializeObject<CameraFile>(File.ReadAllText(path));
                if (file != null && file.Cameras != null)
                {
                    _all = file.Cameras;
                }
            }

            return _all;
        }

        private static bool EnsureBundle()
        {
            if (_bundle)
            {
                return true;
            }

            if (_bundleTried)
            {
                return false;
            }

            _bundleTried = true;
            var path = Path.Combine(Plugin.PluginDir, "plugin-data", "props", BundleFile);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[Halloween2026] {path} is missing, Blind the Enemy has no cameras to shoot");
                return false;
            }

            _bundle = AssetBundle.LoadFromFile(path);
            return _bundle;
        }

    }

    internal sealed class ShootableCamera : MonoBehaviour
    {
        internal string Id;

        private GameObject _whole;
        private GameObject _fragments;
        private bool _broken;

        private void Start()
        {
            var children = GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (child.name.EndsWith("_whole", StringComparison.OrdinalIgnoreCase))
                {
                    _whole = child.gameObject;
                }
                else if (child.name.EndsWith("_fragments", StringComparison.OrdinalIgnoreCase))
                {
                    _fragments = child.gameObject;
                }
                else if (child.name.IndexOf("BALLISTIC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // bullets only answer to colliders that carry this component
                    var ballistic = child.gameObject.AddComponent<BallisticCollider>();
                    ballistic._typeOfMaterial = MaterialType.Plastic;
                    ballistic.OnHitAction += OnHit;
                }
            }
        }

        // "customs_destroy_camera_02" -> "customs_destroy_camera"
        internal static string Place(string id)
        {
            var cut = id.LastIndexOf('_');
            if (cut <= 0 || cut == id.Length - 1)
            {
                return id;
            }

            for (var i = cut + 1; i < id.Length; i++)
            {
                if (!char.IsDigit(id[i]))
                {
                    return id;
                }
            }

            return id.Substring(0, cut);
        }

        private void OnHit(DamageInfo damage)
        {
            if (_broken)
            {
                return;
            }

            _broken = true;
            if (_whole)
            {
                _whole.SetActive(false);
            }

            if (_fragments)
            {
                _fragments.SetActive(true);
            }

            // the quest keeps one visit counter a map, on the id without its number: each camera adds one
            var world = Singleton<GameWorld>.Instance;
            var player = world ? world.MainPlayer : null;
            if (player)
            {
                player.SpecialPlaceVisited(Place(Id), 0);
            }

            Plugin.Log.LogInfo($"[Halloween2026] camera {Id} destroyed");
        }
    }
}
