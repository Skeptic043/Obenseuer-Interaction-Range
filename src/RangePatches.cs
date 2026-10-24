using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace InteractionRange
{
    internal sealed class GameMembers
    {
        internal FieldInfo UseDistance, Interactable, CanUse, MenuInstance, MenuIsOpen;
        internal MethodInfo[] Reach, Uses;
        // Built once from MenuInstance/MenuIsOpen so the per-frame menu check neither reflects nor boxes.
        internal AccessTools.FieldRef<UnityEngine.Object> Menu;
        internal AccessTools.FieldRef<UnityEngine.Object, bool> MenuOpen;
        internal IEnumerable<MethodInfo> Targets => Reach.Concat(Uses);
    }

    internal static class RangePatches
    {
        internal static RangeSettings Settings;
        internal static bool Enabled;
        internal static GameMembers Game;
        internal static ManualLogSource Log;
        internal static float LastApplied = 1f;
        // Unity's own liveness test, as the game's (bool)instance check; replaceable only for desktop tests.
        internal static Func<UnityEngine.Object, bool> IsAlive = obj => obj != null;
        private static readonly HashSet<MethodBase> unchanged = new HashSet<MethodBase>();
        private static readonly MethodInfo ReadHelper = AccessTools.Method(typeof(RangePatches), nameof(ReadUseDistance));
        private static bool failureReported;

        internal static bool Inactive => unchanged.Count != 0;

        // Game members are looked up by name once, so a game update can only make the mod inactive.
        internal static string Resolve()
        {
            Game = null;
            try
            {
                string problem = Find(Type.GetType("InteractObjects, Assembly-CSharp", false),
                    Type.GetType("BuildingSystemMenu, Assembly-CSharp", false), out var found);
                Game = found;
                return problem;
            }
            catch (Exception error) { return "member lookup failed: " + error.Message; }
        }

        internal static string Find(Type target, Type menu, out GameMembers found)
        {
            found = null;
            if (target == null) return "type InteractObjects is missing";
            if (menu == null || !typeof(UnityEngine.Object).IsAssignableFrom(menu)) return "type BuildingSystemMenu is missing or changed";
            var members = new GameMembers
            {
                UseDistance = Field(target, "useDistance"),
                Interactable = Field(target, "interactable"),
                CanUse = Field(target, "canUse"),
                MenuInstance = Field(menu, "instance"),
                MenuIsOpen = Field(menu, "menuIsOpen"),
                // GetUseDistance feeds first-person targeting (RaycastInteract, which also gates quick planting) and plant harvest reach (IsWithinUseDistance).
                Reach = Methods(target, "GetUseDistance").ToArray(),
                Uses = Methods(target, "UseInteractable").Where(m => !m.IsStatic).ToArray(),
            };
            string problem = CheckField(members.UseDistance, "InteractObjects.useDistance", typeof(float), false)
                ?? CheckField(members.Interactable, "InteractObjects.interactable", null, false)
                ?? CheckField(members.MenuInstance, "BuildingSystemMenu.instance", menu, true)
                ?? CheckField(members.MenuIsOpen, "BuildingSystemMenu.menuIsOpen", typeof(bool), false)
                ?? CheckMethods(members.Reach, "InteractObjects.GetUseDistance")
                ?? BuildMenuReaders(members);
            if (problem != null) return problem;
            if (CheckField(members.CanUse, "InteractObjects.canUse", typeof(bool), false) != null) members.CanUse = null;
            found = members;
            return null;
        }

        internal static string CheckField(FieldInfo field, string name, Type type, bool isStatic)
        {
            if (field == null) return name + " is missing";
            return field.IsStatic != isStatic || (type != null && field.FieldType != type) ? name + " has changed" : null;
        }

        internal static string BuildMenuReaders(GameMembers members)
        {
            try
            {
                members.Menu = AccessTools.StaticFieldRefAccess<UnityEngine.Object>(members.MenuInstance);
                members.MenuOpen = AccessTools.FieldRefAccess<UnityEngine.Object, bool>(members.MenuIsOpen);
                return members.Menu == null || members.MenuOpen == null ? "BuildingSystemMenu readers could not be built" : null;
            }
            catch (Exception error) { return "BuildingSystemMenu readers could not be built: " + error.Message; }
        }

        internal static string CheckMethods(ICollection<MethodInfo> methods, string name)
            => methods == null || methods.Count == 0 ? name + " is missing" : null;

        private static FieldInfo Field(Type type, string name)
            => AccessTools.FindIncludingBaseTypes(type, t => t.GetField(name, AccessTools.allDeclared));

        private static IEnumerable<MethodInfo> Methods(Type type, string name)
            => type.GetMethods(AccessTools.allDeclared).Where(m => m.Name == name && m.GetMethodBody() != null);

        internal static float CurrentMultiplier()
        {
            if (!Enabled || Settings == null) return 1f;
            // Same test as the game: a destroyed menu counts as closed.
            var menu = Game.Menu();
            if (IsAlive(menu) && Game.MenuOpen(menu)) return 1f;
            return Settings.Multiplier.Value;
        }

        internal static float ReadUseDistance(float nativeDistance)
        {
            try
            {
                float multiplier = CurrentMultiplier();
                LastApplied = multiplier;
                return RangePolicy.UseDistance(nativeDistance, multiplier);
            }
            catch (Exception error)
            {
                Fail(error);
                return nativeDistance;
            }
        }

        // UseInteractable can run before the next targeting update after the menu opens or the setting drops.
        internal static void BeforeUse(object __instance)
        {
            try
            {
                if (!Enabled) return;
                float current = CurrentMultiplier();
                if (current >= LastApplied) return;
                // Saved/forced and last-used interactables belong to native action/hold handling.
                Game.Interactable.SetValue(__instance, null);
                Game.CanUse?.SetValue(__instance, false);
                LastApplied = current;
            }
            catch (Exception error) { Fail(error); }
        }

        private static void Fail(Exception error)
        {
            Enabled = false;
            if (failureReported) return;
            failureReported = true;
            try { Log?.LogError("Interaction Range disabled itself after an unexpected error; normal reach is vanilla. " + error); }
            catch { }
        }

        // Runs again whenever any mod patches GetUseDistance, so it must never throw.
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var original = new List<CodeInstruction>(instructions);
            try
            {
                var result = Rewrite(original, Game?.UseDistance, ReadHelper, out int reads);
                if (reads != 0)
                {
                    unchanged.Remove(__originalMethod);
                    return result;
                }
                if (unchanged.Add(__originalMethod))
                    Log?.LogWarning("GetUseDistance no longer reads useDistance (game update or another mod changed it); normal reach stays vanilla.");
            }
            catch (Exception error)
            {
                unchanged.Add(__originalMethod);
                Log?.LogError("Could not rewrite GetUseDistance; normal reach stays vanilla. " + error);
            }
            return original;
        }

        internal static List<CodeInstruction> Rewrite(List<CodeInstruction> original, FieldInfo useField, MethodInfo helper, out int reads)
        {
            reads = 0;
            var result = new List<CodeInstruction>(original.Count + 1);
            foreach (var instruction in original)
            {
                result.Add(instruction);
                if (useField == null || instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, useField)) continue;
                result.Add(new CodeInstruction(OpCodes.Call, helper));
                reads++;
            }
            return result;
        }

        internal static void Install(Harmony harmony)
        {
            unchanged.Clear();
            LastApplied = 1f;
            var transpiler = new HarmonyMethod(typeof(RangePatches), nameof(Transpile));
            var prefix = new HarmonyMethod(typeof(RangePatches), nameof(BeforeUse));
            try
            {
                foreach (var method in Game.Reach) harmony.Patch(method, transpiler: transpiler);
                foreach (var method in Game.Uses) harmony.Patch(method, prefix: prefix);
            }
            catch (Exception error)
            {
                try { RemoveOwnedPatches(harmony); }
                catch (Exception cleanup) { throw new AggregateException("Range patch install failed and cleanup was incomplete.", error, cleanup); }
                throw;
            }
        }

        internal static void RemoveOwnedPatches(Harmony harmony)
        {
            // HarmonyX can retain patch records for a wrapper that failed to generate;
            // explicitly visit the known targets instead of its successful-method inventory.
            if (Game == null) return;
            var errors = new List<Exception>();
            foreach (var method in Game.Targets)
            {
                try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count != 0) throw new AggregateException("Could not remove all range patch records.", errors);
        }

        internal static string[] OtherOwners(Harmony harmony)
            => Game.Targets.SelectMany(m => (IEnumerable<string>)Harmony.GetPatchInfo(m)?.Owners ?? Enumerable.Empty<string>())
                .Where(owner => owner != harmony.Id).Distinct().OrderBy(owner => owner, StringComparer.Ordinal).ToArray();
    }
}
