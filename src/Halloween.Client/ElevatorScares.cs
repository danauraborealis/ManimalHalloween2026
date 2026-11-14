using System;
using EFT;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Halloween2026.Client
{
    // the three rigged elevators. retail: the call button switches its lamp, a delay runs, then the
    // animator's one ending plays (a car and its zombie, a cabin that drops, a hand with a grenade),
    // and the grenade one blows 5 s after its doors open. the delays are retail's own.
    internal static class ElevatorScares
    {
        private const string RootName = "INTERACTIVE_Elevator_Zombie";

        internal static void Spawn()
        {
            try
            {
                var scene = SceneManager.GetSceneByName(LabsLayer.SceneName);
                if (!LabsLayer.Loaded || !scene.IsValid() || !scene.isLoaded)
                {
                    return;
                }

                var ready = 0;
                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var all = roots[r].GetComponentsInChildren<Transform>(true);
                    for (var i = 0; i < all.Length; i++)
                    {
                        if (all[i].name.StartsWith(RootName, StringComparison.Ordinal) && Arm(all[i]))
                        {
                            ready++;
                        }
                    }
                }

                Plugin.Log.LogInfo($"[Halloween2026] {ready} elevator scares ready");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Halloween2026] setting up the elevator scares failed: {e}");
            }
        }

        private static bool Arm(Transform piece)
        {
            var animator = piece.GetComponent<Animator>();
            if (!animator || piece.GetComponent<ElevatorScare>())
            {
                return false;
            }

            Collider button = null;
            Transform lamp = null, blast = null, flash = null;
            var parts = piece.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.name == "Switch" && !button)
                {
                    button = part.GetComponent<Collider>();
                }
                else if (part.name == "Light_Button" || (part.name == "Spot_Light_Button" && !lamp))
                {
                    lamp = part;
                }
                else if (part.name == "Explosion")
                {
                    blast = part;
                }
                else if (part.name == "Explosion_VFX")
                {
                    flash = part;
                }
            }

            if (!button)
            {
                Plugin.Log.LogWarning($"[Halloween2026] elevator '{piece.name}' has no call button collider, it stays shut");
                return false;
            }

            // the car's zombie has an animator of its own on the same cue.
            // their resting clips are what keep the scene behind the doors hidden until it plays
            var animators = piece.GetComponentsInChildren<Animator>(true);
            for (var i = 0; i < animators.Length; i++)
            {
                animators[i].enabled = true;
                animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            var scare = piece.gameObject.AddComponent<ElevatorScare>();
            scare.Animators = animators;
            scare.Button = button;
            scare.Lamp = lamp;
            scare.BlastPoint = blast;
            scare.Flash = flash;
            scare.OpenDelay = piece.name.IndexOf("_01_Car", StringComparison.Ordinal) >= 0 ? 10f : 4f;
            scare.BlastDelay = piece.name.IndexOf("Hand_Grenade", StringComparison.Ordinal) >= 0 ? 5f : -1f;
            EventHandles.Attach(button, "Call", scare.Press);
            return true;
        }
    }

    internal sealed class ElevatorScare : MonoBehaviour
    {
        internal Animator[] Animators;
        internal Collider Button;
        internal Transform Lamp;
        internal Transform BlastPoint;
        internal Transform Flash;
        internal float OpenDelay;
        internal float BlastDelay;

        private const string Cue = "IsOpen_01";

        private float _pressed = -1f;
        private bool _opened;
        private bool _blown;
        private Player _caller;

        private static bool HasCue(Animator animator)
        {
            var parameters = animator.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == Cue)
                {
                    return true;
                }
            }

            return false;
        }

        internal void Press(Player player)
        {
            if (_pressed >= 0f)
            {
                return;
            }

            _caller = player;
            _pressed = Time.time;
            Button.enabled = false;
            if (Lamp)
            {
                Lamp.gameObject.SetActive(!Lamp.gameObject.activeSelf);
            }

            LayerSounds.PlayStep(name, "Open_01", Button.transform.position);
            Plugin.Log.LogInfo($"[Halloween2026] elevator called: {name}");
        }

        private void Update()
        {
            if (_pressed < 0f || (_opened && (_blown || BlastDelay < 0f)))
            {
                return;
            }

            var elapsed = Time.time - _pressed;
            if (!_opened && elapsed >= OpenDelay)
            {
                _opened = true;
                for (var i = 0; i < Animators.Length; i++)
                {
                    if (Animators[i] && HasCue(Animators[i]))
                    {
                        Animators[i].SetBool(Cue, true);
                    }
                }

                LayerSounds.PlayStep(name, "Open_02", transform.position + Vector3.up);
            }

            if (_opened && !_blown && BlastDelay >= 0f && elapsed >= OpenDelay + BlastDelay)
            {
                _blown = true;
                var at = BlastPoint ? BlastPoint.position : transform.position + Vector3.up;
                if (Flash)
                {
                    Flash.gameObject.SetActive(true);
                    var particles = Flash.GetComponent<ParticleSystem>();
                    if (particles)
                    {
                        particles.Play(true);
                    }
                }

                LayerSounds.PlayStep(name, "Explode", at);
                EventHandles.Explode(at, _caller);
            }
        }
    }
}
