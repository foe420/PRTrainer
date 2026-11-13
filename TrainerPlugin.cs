using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PocketRoguesTrainer
{
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    public sealed class TrainerPlugin : BaseUnityPlugin
    {
        public const string Guid = "local.pocketrogues.trainer";
        public const string PluginName = "Pocket Rogues Trainer";
        public const string PluginVersion = "1.2.0";

        internal static ManualLogSource Log;
        internal static TrainerCore Core;
        internal static bool Quitting;

        private static TrainerRunner _runner;
        private Harmony _harmony;
        private readonly List<string> _patchErrors = new List<string>();

        private void Awake()
        {
            Log = Logger;
            Core = new TrainerCore(Config);
            Core.WriteStateFile();

            _harmony = new Harmony(Guid);
            PatchSafely(typeof(Patch_CanReceiveDamage));
            PatchSafely(typeof(Patch_SetDamageRPC));
            PatchSafely(typeof(Patch_NormalizeEffects));
            PatchSafely(typeof(Patch_ChestEffectWindow));

            // Pocket Rogues destroys BepInEx's manager GameObject (and with it this
            // component) right after startup, before any scene loads. So the menu runs on
            // its own hidden object, created once scenes start loading and re-created if
            // anything ever removes it.
            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.quitting += () => Quitting = true;

            Log.LogInfo($"{PluginName} {PluginVersion} loaded - press {Core.ToggleKey} in game to open the menu." +
                        (_patchErrors.Count == 0 ? " All patches applied." : " Patch failures: " + string.Join("; ", _patchErrors.ToArray())));
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Core?.Diag($"scene loaded: '{scene.name}' ({mode})");
            EnsureRunner();
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return; // Unity's == also treats destroyed objects as null
            try
            {
                var host = new GameObject("PocketRoguesTrainer") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host);
                _runner = host.AddComponent<TrainerRunner>();
                Core?.Diag("runner object created");
            }
            catch (Exception e)
            {
                Log.LogError("Could not create the trainer's runner object: " + e);
            }
        }

        private void PatchSafely(Type patchClass)
        {
            try
            {
                _harmony.CreateClassProcessor(patchClass).Patch();
            }
            catch (Exception e)
            {
                _patchErrors.Add(patchClass.Name);
                Log.LogError($"Patch {patchClass.Name} failed (that cheat will not work): {e}");
            }
        }

        private void OnDestroy()
        {
            Core?.Diag("BepInEx manager object destroyed by the game (expected; the trainer runs on its own object).");
        }
    }

    /// <summary>Hidden, persistent host that forwards Unity's frame callbacks to the core.</summary>
    internal sealed class TrainerRunner : MonoBehaviour
    {
        private void Update() => TrainerPlugin.Core?.Tick();

        private void LateUpdate() => TrainerPlugin.Core?.LateTick();

        private void OnGUI() => TrainerPlugin.Core?.DrawGui();

        private void OnDestroy()
        {
            if (!TrainerPlugin.Quitting)
                TrainerPlugin.Log?.LogWarning("Trainer runner object was destroyed; it is re-created on the next scene load.");
            TrainerPlugin.Core?.OnRunnerDestroyed();
        }
    }
}
