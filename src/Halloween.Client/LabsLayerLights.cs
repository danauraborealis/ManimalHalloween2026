using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the event adds ~350 small realtime lights. retail fades each one out by distance through its
    // CullingLightObject, which the rip lost; this does the coarse version so they are not all live at once.
    internal sealed class LabsLayerLights : MonoBehaviour
    {
        private const float Range = 45f;
        private const float Interval = 0.5f;

        private readonly List<Light> _lights = new List<Light>();
        private float _next;

        internal static void Attach(Scene scene)
        {
            var host = new GameObject("Halloween2026_LayerLights");
            SceneManager.MoveGameObjectToScene(host, scene);
            var culler = host.AddComponent<LabsLayerLights>();
            var roots = scene.GetRootGameObjects();
            for (var r = 0; r < roots.Length; r++)
            {
                var lights = roots[r].GetComponentsInChildren<Light>(true);
                for (var i = 0; i < lights.Length; i++)
                {
                    // one left disabled in the scene was authored off
                    if (lights[i].enabled)
                    {
                        culler._lights.Add(lights[i]);
                    }
                }
            }

            Plugin.Log.LogInfo($"[Halloween2026] {culler._lights.Count} event lights under distance culling");
        }

        private void Update()
        {
            if (Time.unscaledTime < _next)
            {
                return;
            }

            _next = Time.unscaledTime + Interval;
            var camera = Camera.main;
            if (!camera)
            {
                return;
            }

            var eye = camera.transform.position;
            var limit = Range * Range;
            for (var i = 0; i < _lights.Count; i++)
            {
                var light = _lights[i];
                if (light)
                {
                    var near = (light.transform.position - eye).sqrMagnitude < limit;
                    if (light.enabled != near)
                    {
                        light.enabled = near;
                    }
                }
            }
        }
    }
}
