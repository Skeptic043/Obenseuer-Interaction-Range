using System;
using BepInEx;
using HarmonyLib;

namespace InteractionRange
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.skeptic043.obenseuer.interactionrange";
        public const string Name = "Interaction Range";
        public const string Version = "0.2.1";
        private Harmony harmony;
        private RangeSettings settings;
        private bool quitting;

        private void Awake()
        {
            RangePatches.Log = Logger;
            // Only an unload before quit (another mod or a scene cleanup destroying the manager) is reported.
            try { UnityEngine.Application.quitting += OnQuitting; }
            catch { }
            try
            {
                string problem = RangePatches.Resolve();
                if (problem != null)
                {
                    Logger.LogWarning($"Interaction Range {Version} inactive: {problem} (game update?). Normal reach stays vanilla.");
                    return;
                }
                if (RangePatches.Game.Uses.Length == 0)
                    Logger.LogWarning("InteractObjects.UseInteractable is missing; a target selected at extended reach may stay usable until the next targeting update.");
                settings = new RangeSettings(Config);
                RangePatches.Settings = settings;
                harmony = new Harmony(Guid);
                RangePatches.Install(harmony);
                RangePatches.Enabled = true;
                string others = string.Join(", ", RangePatches.OtherOwners(harmony));
                if (others.Length != 0) Logger.LogInfo("Other Harmony patches on interaction targeting: " + others + ".");
                Logger.LogInfo($"Interaction Range {Version}: normal reach {settings.Multiplier.Value:0.#}x, patch " +
                    (RangePatches.Inactive ? "inactive (vanilla reach)" : "active") + ". Build/cleaning mode, melee and item placement stay native.");
            }
            catch (Exception error)
            {
                RangePatches.Enabled = false;
                settings?.Dispose();
                RangePatches.Settings = null;
                Exception cleanupError = null;
                try { if (harmony != null) RangePatches.RemoveOwnedPatches(harmony); }
                catch (Exception cleanup) { cleanupError = cleanup; }
                Logger.LogError($"Interaction Range {Version} could not install its patches; normal reach stays vanilla. " +
                    (cleanupError == null ? "" : "Patch cleanup also failed; any remaining range helpers are disabled. " + cleanupError + " ") + error);
            }
        }

        private void OnQuitting() => quitting = true;

        private void OnDestroy()
        {
            RangePatches.Enabled = false;
            try { UnityEngine.Application.quitting -= OnQuitting; }
            catch { }
            try
            {
                settings?.Dispose();
                RangePatches.Settings = null;
                try { if (harmony != null) RangePatches.RemoveOwnedPatches(harmony); }
                catch (Exception error) { Logger.LogWarning("Could not remove all Interaction Range patches; its helpers are disabled. " + error); }
                if (!quitting && harmony != null) Logger.LogWarning($"Interaction Range {Version} was unloaded; normal reach is now vanilla.");
            }
            catch { }
        }
    }
}
