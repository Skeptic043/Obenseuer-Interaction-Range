using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using InteractionRange;
using UnityEngine;

internal static class Tests
{
    private static int passed;
    private static RangeSettings settings;
    private static string working;
    private static GameMembers game;
    private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
    // IL field access; reflection would run UnityEngine.Object's native static constructor.
    private static readonly AccessTools.FieldRef<UnityEngine.Object, IntPtr> CachedPtrRef = AccessTools.FieldRefAccess<UnityEngine.Object, IntPtr>(CachedPtr);
    private static readonly MethodInfo Helper = AccessTools.Method(typeof(RangePatches), nameof(RangePatches.ReadUseDistance));
    private static readonly FieldInfo UseField = AccessTools.Field(typeof(InteractObjects), "useDistance");
    private static readonly FieldInfo ScanField = AccessTools.Field(typeof(InteractObjects), "maxDistance");
    private static readonly FieldInfo MeleeField = AccessTools.Field(typeof(InteractObjects), "maxMeleeDistance");

    internal static int Run()
    {
        // Fresh per-run folder; removed even when a check fails.
        working = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "implementation", "test-run", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(working);
        try { return RunChecks(); }
        finally
        {
            RangePatches.Enabled = false;
            RangePatches.Settings = null;
            settings?.Dispose();
            Directory.Delete(working, true);
        }
    }

    private static int RunChecks()
    {
        Console.WriteLine("BepInEx: " + typeof(ConfigFile).Assembly.FullName);
        Console.WriteLine("Harmony: " + typeof(Harmony).Assembly.FullName);
        BepInEx.Logging.Logger.Listeners.Add(Log);
        RangePatches.Log = BepInEx.Logging.Logger.CreateLogSource("InteractionRange.Tests");
        Check("selected loader references and plugin base", () =>
        {
            var pluginAssembly = typeof(Plugin).Assembly;
            var references = pluginAssembly.GetReferencedAssemblies();
            var loaderReferences = references.Where(reference => reference.Name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase)).ToArray();
#if BEPINEX6
            True(loaderReferences.Length == 2);
            True(loaderReferences.Any(reference => reference.Name == "BepInEx.Core" && reference.Version == new Version(6, 0, 0, 0)));
            True(loaderReferences.Any(reference => reference.Name == "BepInEx.Unity.Mono" && reference.Version == new Version(6, 0, 0, 0)));
            True(typeof(Plugin).BaseType == typeof(BepInEx.Unity.Mono.BaseUnityPlugin));
#else
            True(loaderReferences.Length == 1);
            var loader = loaderReferences.Single();
            True(loader.Name == "BepInEx");
            True(loader.Version == new Version(5, 4, 23, 5));
            True(typeof(Plugin).BaseType == typeof(BepInEx.BaseUnityPlugin));
#endif
            True(Plugin.Guid == "com.skeptic043.obenseuer.interactionrange");
            True(Plugin.Version == "1.0.1");
        });
        Check("no compile-time game references; game members are resolved by name", () =>
            True(!typeof(Plugin).Assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Assembly-CSharp")));
        Check("six stops and native preservation", () =>
        {
            foreach (float stop in new[] { 1f, 1.2f, 1.4f, 1.6f, 1.8f, 2f }) Equal(stop, RangePolicy.Normalize(stop));
            Equal(1.5f, RangePolicy.UseDistance(1.5f, 1f));
            Equal(1.8f, RangePolicy.UseDistance(1.5f, 1.2f));
            Equal(3f, RangePolicy.UseDistance(1.5f, 2f));
        });
        Check("snap, bounds and nonfinite helper safety", () =>
        {
            Equal(1.2f, RangePolicy.Normalize(1.1f));
            Equal(1.4f, RangePolicy.Normalize(1.3f));
            Equal(1.6f, RangePolicy.Normalize(1.5f));
            Equal(1.8f, RangePolicy.Normalize(1.7f));
            Equal(2f, RangePolicy.Normalize(1.9f));
            Equal(1.2f, RangePolicy.Normalize(1.299f));
            Equal(1.4f, RangePolicy.Normalize(1.301f));
            Equal(1f, RangePolicy.Normalize(-20f));
            Equal(2f, RangePolicy.Normalize(20f));
            foreach (float bad in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity }) Equal(1.2f, RangePolicy.Normalize(bad));
        });
        ConfigTests();
        MemberTests();
        TranspilerTests();
        RangePatches.Settings = settings;
        RangePatches.Enabled = true;
        HelperTests();
        UsePrefixTests();
        WrapperTests();
        InstallTests();
        BuildingSystemMenu.instance = null;
        Console.WriteLine($"PASS: {passed} checks. Actual game IL inspected and real game methods patched; Harmony wrappers executed on desktop .NET Framework. No Unity native runtime or gameplay executed.");
        return 0;
    }

    private static void ConfigTests()
    {
        string path = Path.Combine(working, "roundtrip.cfg");
        File.WriteAllText(path, "[General]\nRange multiplier = 1.5\n");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = true };
        settings = new RangeSettings(config);
        Check("existing 1.5 config snaps to 1.6", () => Equal(1.6f, settings.Multiplier.Value));
        Check("one standard slider-compatible range", () =>
        {
            var range = settings.Multiplier.Description.AcceptableValues as AcceptableValueRange<float>;
            True(range != null);
            Equal(1f, range.MinValue); Equal(2f, range.MaxValue); Equal(1, config.Count);
            True(settings.Multiplier.Description.Description ==
                "Normal interaction reach. Snaps to 1x, 1.2x, 1.4x, 1.6x, 1.8x or 2x. Build/cleaning mode, melee and item placement stay unchanged.");
        });
        Check("config changes snap without recursion", () =>
        {
            int changes = 0;
            EventHandler counter = (sender, args) => changes++;
            settings.Multiplier.SettingChanged += counter;
            settings.Multiplier.Value = 1.76f;
            settings.Multiplier.SettingChanged -= counter;
            // The user's change and the single snap correction.
            Equal(1.8f, settings.Multiplier.Value); Equal(2, changes);
        });
        Check("actual BepInEx nonfinite clamp", () =>
        {
            settings.Multiplier.Value = float.NaN; Equal(1f, settings.Multiplier.Value);
            settings.Multiplier.Value = float.PositiveInfinity; Equal(2f, settings.Multiplier.Value);
            settings.Multiplier.Value = float.NegativeInfinity; Equal(1f, settings.Multiplier.Value);
        });
        Check("config save/load roundtrip and reload snap", () =>
        {
            settings.Multiplier.Value = 1.8f; config.Save();
            using (var copy = new RangeSettings(new ConfigFile(path, false))) Equal(1.8f, copy.Multiplier.Value);
            File.WriteAllText(path, "[General]\nRange multiplier = 2.76\n"); config.Reload(); Equal(2f, settings.Multiplier.Value);
            File.WriteAllText(path, "[General]\nRange multiplier = 0.25\n"); config.Reload(); Equal(1f, settings.Multiplier.Value);
        });
        Check("fresh default is 1.2x", () =>
        {
            string fresh = Path.Combine(working, "fresh.cfg");
            using (var value = new RangeSettings(new ConfigFile(fresh, false))) Equal(1.2f, value.Multiplier.Value);
        });
    }

    private static void MemberTests()
    {
        Check("game members resolve against the real game assembly", () =>
        {
            True(RangePatches.Resolve() == null);
            game = RangePatches.Game;
            True(Equals(UseField, game.UseDistance));
            True(game.Raycasts.Length == 1 && Equals(game.Raycasts[0], AccessTools.Method(typeof(InteractObjects), "RaycastInteract")));
            True(game.Uses.Length == 1 && Equals(game.Uses[0], AccessTools.Method(typeof(InteractObjects), "UseInteractable")));
            True(Equals(game.Interactable, AccessTools.Field(typeof(InteractObjects), "interactable")));
            True(Equals(game.CanUse, AccessTools.Field(typeof(InteractObjects), "canUse")));
            True(Equals(game.MenuInstance, AccessTools.Field(typeof(BuildingSystemMenu), "instance")));
            True(Equals(game.MenuIsOpen, AccessTools.Field(typeof(BuildingSystemMenu), "menuIsOpen")));
            True(game.Menu != null && game.MenuOpen != null);
        });
        Check("missing or changed members give a problem string, not an exception", () =>
        {
            True(RangePatches.Find(null, typeof(BuildingSystemMenu), out var none) != null && none == null);
            True(RangePatches.Find(typeof(InteractObjects), null, out none) != null);
            True(RangePatches.Find(typeof(InteractObjects), typeof(FakeTarget), out none) != null);
            True(RangePatches.Find(typeof(FakeTarget), typeof(BuildingSystemMenu), out none).Contains("useDistance"));
            True(RangePatches.Find(typeof(NoRaycast), typeof(BuildingSystemMenu), out none).Contains("RaycastInteract"));
            True(RangePatches.Find(typeof(InteractObjects), typeof(FakeMenu), out none).Contains("instance"));
            // A reader that cannot be built is reported like a missing member (Find then returns no members).
            var unreadable = new GameMembers();
            string readerProblem = RangePatches.BuildMenuReaders(unreadable);
            True(readerProblem != null && readerProblem.Contains("readers") && unreadable.MenuOpen == null);
            True(RangePatches.CheckField(null, "field", typeof(float), false) != null);
            True(RangePatches.CheckField(UseField, "field", typeof(int), false) != null);
            True(RangePatches.CheckField(UseField, "field", typeof(float), true) != null);
            True(RangePatches.CheckField(UseField, "field", typeof(float), false) == null);
            True(RangePatches.CheckMethods(new MethodInfo[0], "method") != null);
            True(RangePatches.CheckMethods(null, "method") != null);
        });
        Check("optional canUse and UseInteractable may be absent", () =>
        {
            True(RangePatches.Find(typeof(MinimalTarget), typeof(BuildingSystemMenu), out var minimal) == null);
            True(minimal.CanUse == null && minimal.Uses.Length == 0 && minimal.Raycasts.Length == 2);
        });
    }

    private static void TranspilerTests()
    {
        RangePatches.Game = game;
        var raycast = game.Raycasts[0];
        Check("actual RaycastInteract IL: one helper call right after the useDistance read", () =>
        {
            var original = PatchProcessor.GetOriginalInstructions(raycast).ToList();
            Equal(1, original.Count(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, UseField)));
            var rewritten = RangePatches.Transpile(original, raycast).ToList();
            Equal(original.Count + 1, rewritten.Count);
            var calls = Enumerable.Range(0, rewritten.Count).Where(i => rewritten[i].Calls(Helper)).ToList();
            Equal(1, calls.Count);
            True(rewritten[calls[0] - 1].opcode == OpCodes.Ldfld && Equals(rewritten[calls[0] - 1].operand, UseField));
            // Every original instruction (with its labels and blocks) is kept in order by reference.
            var kept = rewritten.Where(i => !i.Calls(Helper)).ToList();
            Equal(original.Count, kept.Count);
            for (int i = 0; i < original.Count; i++) True(ReferenceEquals(original[i], kept[i]));
            True(!rewritten.Any(i => i.opcode == OpCodes.Stfld && (Equals(i.operand, UseField) || Equals(i.operand, ScanField))));
            foreach (var field in new[] { ScanField, MeleeField })
            {
                Equal(1, original.Count(i => Equals(i.operand, field)));
                int index = rewritten.FindIndex(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, field));
                True(!rewritten[index + 1].Calls(Helper));
            }
            True(!RangePatches.Inactive);
        });
        Check("synthetic: no useDistance read returns the input unchanged and marks inactive", () =>
        {
            var original = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, ScanField),
                new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ret),
            };
            var result = RangePatches.Transpile(original, raycast).ToList();
            Equal(original.Count, result.Count);
            for (int i = 0; i < original.Count; i++) True(ReferenceEquals(original[i], result[i]));
            True(RangePatches.Inactive);
            RangePatches.Transpile(PatchProcessor.GetOriginalInstructions(raycast), raycast).ToList();
            True(!RangePatches.Inactive);
        });
        Check("synthetic: two useDistance reads are both wrapped", () =>
        {
            var original = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, UseField),
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, UseField),
                new CodeInstruction(OpCodes.Add), new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ret),
            };
            var result = RangePatches.Transpile(original, raycast).ToList();
            Equal(original.Count + 2, result.Count);
            True(result[2].Calls(Helper) && result[5].Calls(Helper));
            True(ReferenceEquals(original[1], result[1]) && ReferenceEquals(original[3], result[4]));
            True(!RangePatches.Inactive);
        });
        Check("transpiler failure returns the input unchanged without throwing", () =>
        {
            var original = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldarg_0), null, new CodeInstruction(OpCodes.Ret) };
            var result = RangePatches.Transpile(original, raycast).ToList();
            Equal(3, result.Count);
            True(ReferenceEquals(original[0], result[0]) && result[1] == null && ReferenceEquals(original[2], result[2]));
            True(RangePatches.Inactive);
            RangePatches.Transpile(PatchProcessor.GetOriginalInstructions(raycast), raycast).ToList();
            True(!RangePatches.Inactive);
        });
    }

    private static void HelperTests()
    {
        Check("unity liveness simulation", () =>
        {
            // UnityEngine.Object's static constructor needs the native engine, so desktop tests
            // replace Unity's operator with its managed rule for MonoBehaviours: alive while m_CachedPtr != 0.
            True(CachedPtr != null && CachedPtr.FieldType == typeof(IntPtr));
            RangePatches.IsAlive = UnityLiveness;
            True(UnityLiveness(NewMenu(true)) && !UnityLiveness(NewMenu(false)) && !UnityLiveness(null));
        });
        Check("helper disabled returns native", () =>
        {
            RangePatches.Enabled = false; settings.Multiplier.Value = 2f;
            Equal(1.5f, RangePatches.ReadUseDistance(1.5f)); Equal(1f, RangePatches.LastApplied);
            RangePatches.Enabled = true;
        });
        Check("helper at 2x, 1.2x and 1x", () =>
        {
            BuildingSystemMenu.instance = null;
            settings.Multiplier.Value = 2f; Equal(3f, RangePatches.ReadUseDistance(1.5f)); Equal(2f, RangePatches.LastApplied);
            settings.Multiplier.Value = 1.2f; Equal(1.8f, RangePatches.ReadUseDistance(1.5f)); Equal(1.2f, RangePatches.LastApplied);
            settings.Multiplier.Value = 1f; Equal(1.5f, RangePatches.ReadUseDistance(1.5f)); Equal(1f, RangePatches.LastApplied);
        });
        Check("helper uses native reach while a live build menu is open", () =>
        {
            settings.Multiplier.Value = 2f;
            var menu = NewMenu(true); BuildingSystemMenu.instance = menu;
            menu.menuIsOpen = true; Equal(1.5f, RangePatches.ReadUseDistance(1.5f)); Equal(1f, RangePatches.LastApplied);
            menu.menuIsOpen = false; Equal(3f, RangePatches.ReadUseDistance(1.5f)); Equal(2f, RangePatches.LastApplied);
            BuildingSystemMenu.instance = null;
        });
        Check("destroyed build menu counts as closed, matching the game", () =>
        {
            settings.Multiplier.Value = 2f;
            var menu = NewMenu(false); menu.menuIsOpen = true; BuildingSystemMenu.instance = menu;
            Equal(3f, RangePatches.ReadUseDistance(1.5f));
            BuildingSystemMenu.instance = null;
        });
        Check("per-frame read allocates nothing with the menu closed or open", () =>
        {
            settings.Multiplier.Value = 2f;
            var menu = NewMenu(true); BuildingSystemMenu.instance = menu;
            try
            {
                foreach (bool open in new[] { false, true })
                {
                    menu.menuIsOpen = open;
                    long bytes = SteadyStateAllocation(open ? 1.5f : 3f, out bool exact);
                    Console.WriteLine($"  menu {(open ? "open" : "closed")}: {bytes} bytes over {AllocationIterations} reads ({(exact ? "thread" : "app-domain")} counter)");
                    // One boxed bool per read would be at least 12 bytes per call; the app-domain counter is only quantum-accurate.
                    True(exact ? bytes == 0 : bytes < AllocationIterations);
                }
            }
            finally { BuildingSystemMenu.instance = null; }
        });
        Check("helper exception returns native and disables", () =>
        {
            settings.Multiplier.Value = 2f;
            RangePatches.Game = null;
            try { Equal(1.5f, RangePatches.ReadUseDistance(1.5f)); True(!RangePatches.Enabled); }
            finally { RangePatches.Game = game; }
            Equal(1.5f, RangePatches.ReadUseDistance(1.5f));
            RangePatches.Enabled = true;
            Equal(3f, RangePatches.ReadUseDistance(1.5f));
        });
    }

    private static void UsePrefixTests()
    {
        Check("use prefix clears a target picked at longer reach, keeping saved/forced/last-used", () =>
        {
            var target = NewTarget(out var held, out var saved);
            settings.Multiplier.Value = 2f; RangePatches.ReadUseDistance(1.5f);
            settings.Multiplier.Value = 1f; RangePatches.BeforeUse(target);
            True(target.interactable == null && !target.canUse);
            True(ReferenceEquals(held, target.lastUsedInteractable));
            True(ReferenceEquals(saved, target.savedInteractable) && ReferenceEquals(saved, target.savedForceInteractable));
            Equal(1f, RangePatches.LastApplied);
        });
        Check("use prefix clears when a live build menu opened after targeting", () =>
        {
            var target = NewTarget(out _, out _);
            settings.Multiplier.Value = 2f; RangePatches.ReadUseDistance(1.5f);
            var menu = NewMenu(true); menu.menuIsOpen = true; BuildingSystemMenu.instance = menu;
            RangePatches.BeforeUse(target);
            True(target.interactable == null && !target.canUse);
            BuildingSystemMenu.instance = null;
        });
        Check("use prefix leaves the target alone at the same or higher reach and when disabled", () =>
        {
            var target = NewTarget(out _, out _); var selected = target.interactable;
            settings.Multiplier.Value = 1.6f; RangePatches.ReadUseDistance(1.5f);
            RangePatches.BeforeUse(target);
            settings.Multiplier.Value = 2f; RangePatches.BeforeUse(target);
            True(ReferenceEquals(selected, target.interactable) && target.canUse);
            settings.Multiplier.Value = 2f; RangePatches.ReadUseDistance(1.5f); settings.Multiplier.Value = 1f;
            RangePatches.Enabled = false; RangePatches.BeforeUse(target); RangePatches.Enabled = true;
            True(ReferenceEquals(selected, target.interactable) && target.canUse);
        });
    }

    private static void WrapperTests()
    {
        var harmony = new Harmony("interactionrange.tests.wrapper");
        var method = AccessTools.Method(typeof(Surrogate), nameof(Surrogate.Reach));
        try
        {
            Check("Harmony generates an actual surrogate wrapper", () =>
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Tests), nameof(SurrogateTranspiler))));
            var surrogate = new Surrogate();
            Check("wrapper scales the read without changing the field", () =>
            {
                settings.Multiplier.Value = 2f; Equal(3f, surrogate.Reach()); Equal(3f, surrogate.Reach());
                Equal(1.5f, surrogate.NativeUse);
                settings.Multiplier.Value = 1.4f; Equal(2.1f, surrogate.Reach());
                settings.Multiplier.Value = 1f; Equal(1.5f, surrogate.Reach());
            });
            Check("wrapper uses native reach while the build menu is open", () =>
            {
                settings.Multiplier.Value = 2f;
                var menu = NewMenu(true); menu.menuIsOpen = true; BuildingSystemMenu.instance = menu;
                Equal(1.5f, surrogate.Reach()); BuildingSystemMenu.instance = null; Equal(3f, surrogate.Reach());
            });
            Check("prefix wrapper with object __instance clears a stale target on the surrogate", () =>
            {
                RangePatches.Game = new GameMembers
                {
                    Interactable = AccessTools.Field(typeof(Surrogate), nameof(Surrogate.Selected)),
                    CanUse = AccessTools.Field(typeof(Surrogate), nameof(Surrogate.CanUse)),
                    MenuInstance = game.MenuInstance, MenuIsOpen = game.MenuIsOpen, Menu = game.Menu, MenuOpen = game.MenuOpen,
                };
                try
                {
                    harmony.Patch(AccessTools.Method(typeof(Surrogate), nameof(Surrogate.Use)),
                        prefix: new HarmonyMethod(typeof(RangePatches), nameof(RangePatches.BeforeUse)));
                    var selected = new FakeInteractable(); var held = new FakeInteractable();
                    surrogate.Selected = selected; surrogate.CanUse = true; surrogate.LastUsed = held;
                    settings.Multiplier.Value = 2f; RangePatches.ReadUseDistance(1.5f);
                    surrogate.Use(); Equal(1, selected.Uses); True(ReferenceEquals(selected, surrogate.LastUsed));
                    surrogate.LastUsed = held; settings.Multiplier.Value = 1f; surrogate.Use();
                    Equal(1, selected.Uses); True(surrogate.Selected == null && !surrogate.CanUse && ReferenceEquals(held, surrogate.LastUsed));
                    True(RangePatches.Enabled);
                }
                finally { RangePatches.Game = game; }
            });
        }
        finally { harmony.UnpatchSelf(); }
        Check("wrapper unpatch restores native", () => { settings.Multiplier.Value = 2f; Equal(1.5f, new Surrogate().Reach()); });
    }

    private static void InstallTests()
    {
        var raycast = game.Raycasts[0];
        var use = game.Uses[0];
        var harmony = new Harmony("interactionrange.tests.install");
        var competitor = new Harmony("interactionrange.tests.competitor");
        var competing = new HarmonyMethod(typeof(Tests), nameof(RemoveUseRead)) { priority = Priority.First };
        try
        {
            Check("actual game installation owns exactly RaycastInteract and UseInteractable", () =>
            {
                RangePatches.Install(harmony);
                var owned = Harmony.GetAllPatchedMethods().Where(m => Owns(m, harmony)).ToList();
                Equal(2, owned.Count);
                True(owned.Contains(raycast) && owned.Contains(use));
                True(!Owns(AccessTools.Method(typeof(InteractObjects), "GetCrosshairTarget"), harmony));
                True(!Owns(AccessTools.Method(typeof(InteractObjects), "GetInteractablesWithinSphere"), harmony));
                True(!RangePatches.Inactive);
                Equal(0, RangePatches.OtherOwners(harmony).Length);
            });
        }
        finally { RangePatches.RemoveOwnedPatches(harmony); }
        Check("cleanup leaves no owned patches", () => True(!Harmony.GetAllPatchedMethods().Any(m => Owns(m, harmony))));
        try
        {
            Check("competing transpiler installed first: install succeeds, patch inactive", () =>
            {
                competitor.Patch(raycast, transpiler: competing);
                RangePatches.Install(harmony);
                True(RangePatches.Inactive);
                True(Owns(raycast, competitor) && Owns(raycast, harmony));
                True(RangePatches.OtherOwners(harmony).SequenceEqual(new[] { competitor.Id }));
            });
        }
        finally
        {
            RangePatches.RemoveOwnedPatches(harmony);
            competitor.Unpatch(raycast, HarmonyPatchType.All, competitor.Id);
        }
        try
        {
            Check("competing transpiler installed after ours: its patch succeeds, both owners kept", () =>
            {
                RangePatches.Install(harmony);
                True(!RangePatches.Inactive);
                competitor.Patch(raycast, transpiler: competing);
                True(RangePatches.Inactive);
                True(Owns(raycast, competitor) && Owns(raycast, harmony));
            });
        }
        finally
        {
            RangePatches.RemoveOwnedPatches(harmony);
            competitor.Unpatch(raycast, HarmonyPatchType.All, competitor.Id);
        }
        Check("all test patches removed", () =>
            True(!Harmony.GetAllPatchedMethods().Any(m => Owns(m, harmony) || Owns(m, competitor))));
        Check("failed install removes the patches it already applied", () =>
        {
            // The unpatchable second target fails after RaycastInteract was patched.
            RangePatches.Game = new GameMembers
            {
                UseDistance = game.UseDistance, Interactable = game.Interactable, CanUse = game.CanUse,
                MenuInstance = game.MenuInstance, MenuIsOpen = game.MenuIsOpen,
                Raycasts = game.Raycasts, Uses = new[] { AccessTools.Method(typeof(Unpatchable), nameof(Unpatchable.UseInteractable)) },
            };
            var failing = new Harmony("interactionrange.tests.failing");
            try
            {
                bool failed = false;
                try { RangePatches.Install(failing); }
                catch (Exception error) { failed = true; Console.WriteLine("  expected install failure: " + error.GetType().Name + ": " + error.Message); }
                True(failed);
                True(!Owns(raycast, failing) && !Owns(use, failing));
            }
            finally { RangePatches.Game = game; }
        });
    }

    private abstract class Unpatchable
    {
        public abstract void UseInteractable();
    }

    private static bool Owns(MethodBase method, Harmony harmony) => Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) ?? false;

    private static IEnumerable<CodeInstruction> RemoveUseRead(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, UseField))
            {
                // A valid competing transform that replaces the field read with a constant.
                var pop = new CodeInstruction(OpCodes.Pop);
                pop.labels.AddRange(instruction.labels);
                pop.blocks.AddRange(instruction.blocks);
                yield return pop;
                yield return new CodeInstruction(OpCodes.Ldc_R4, 1.5f);
            }
            else yield return instruction;
        }
    }

    private static IEnumerable<CodeInstruction> SurrogateTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var result = RangePatches.Rewrite(instructions.ToList(), AccessTools.Field(typeof(Surrogate), nameof(Surrogate.NativeUse)), Helper, out int reads);
        if (reads != 1) throw new InvalidOperationException("Surrogate shape changed.");
        return result;
    }

    private const int AllocationIterations = 200000;

    private static long SteadyStateAllocation(float expected, out bool exact)
    {
        // Desktop .NET Framework 4.8 exposes the exact per-thread counter at runtime; the 4.7.2 reference assemblies do not.
        var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        var threadBytes = method == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        exact = threadBytes != null;
        if (!exact) AppDomain.MonitoringIsEnabled = true;
        var domain = AppDomain.CurrentDomain;
        int wrong = 0;
        for (int i = 0; i < 1000; i++) RangePatches.ReadUseDistance(1.5f);
        long before = exact ? threadBytes() : domain.MonitoringTotalAllocatedMemorySize;
        for (int i = 0; i < AllocationIterations; i++)
            if (RangePatches.ReadUseDistance(1.5f) != expected) wrong++;
        long after = exact ? threadBytes() : domain.MonitoringTotalAllocatedMemorySize;
        Equal(0, wrong);
        True(RangePatches.Enabled);
        return after - before;
    }

    private static BuildingSystemMenu NewMenu(bool alive)
    {
        var menu = (BuildingSystemMenu)FormatterServices.GetUninitializedObject(typeof(BuildingSystemMenu));
        // Unity treats an object without a native pointer as destroyed.
        if (alive) CachedPtrRef(menu) = new IntPtr(1);
        return menu;
    }

    private static InteractObjects NewTarget(out FakeInteractable held, out Interactable saved)
    {
        var target = (InteractObjects)FormatterServices.GetUninitializedObject(typeof(InteractObjects));
        held = new FakeInteractable();
        saved = (Interactable)FormatterServices.GetUninitializedObject(typeof(Interactable));
        target.interactable = new FakeInteractable(); target.canUse = true;
        target.lastUsedInteractable = held; target.savedInteractable = saved; target.savedForceInteractable = saved;
        return target;
    }

    private static void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Equal(float expected, float actual) { if (float.IsNaN(actual) || Math.Abs(expected - actual) > 0.00001f) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }

    private static readonly LogCapture Log = new LogCapture();

    private sealed class LogCapture : BepInEx.Logging.ILogListener
    {
        internal readonly List<string> Messages = new List<string>();
        public BepInEx.Logging.LogLevel LogLevelFilter => BepInEx.Logging.LogLevel.All;
        public void LogEvent(object sender, BepInEx.Logging.LogEventArgs eventArgs)
        {
            Messages.Add(eventArgs.Level + ": " + eventArgs.Data);
            Console.WriteLine("  log " + eventArgs.Level + ": " + eventArgs.Data);
        }
        public void Dispose() { }
    }

    private sealed class FakeInteractable : IInteractable
    {
        internal int Uses;
        public void Interact() => Uses++;
        public IInteractable StopInteract(bool stopHold) => this;
        public void UpdateMoney() { }
        public void UpdateUseMessage() { }
        public bool IsInteractDisabled() => false;
        public void SetInteractDisabled(bool value) { }
    }

    private static bool UnityLiveness(UnityEngine.Object obj)
        => !ReferenceEquals(obj, null) && CachedPtrRef(obj) != IntPtr.Zero;

    private sealed class Surrogate
    {
        public float NativeUse = 1.5f;
        public IInteractable Selected, LastUsed;
        public bool CanUse;
        [MethodImpl(MethodImplOptions.NoInlining)] public float Reach() => NativeUse;
        // Shape of InteractObjects.UseInteractable's normal branch.
        [MethodImpl(MethodImplOptions.NoInlining)] public void Use()
        {
            if (Selected == null) return;
            LastUsed = Selected;
            Selected.Interact();
        }
    }

    // Wrong-typed members.
    private sealed class FakeTarget
    {
        public int useDistance = 1;
        public object interactable = null;
        public static object instance = null;
        public void RaycastInteract() { }
    }

    private sealed class NoRaycast
    {
        public float useDistance = 1.5f;
        public object interactable = null;
    }

    private sealed class FakeMenu : MonoBehaviour
    {
        public static object instance = null;
        public bool menuIsOpen = false;
    }

    // Required members only, with overloads.
    private sealed class MinimalTarget
    {
        public float useDistance = 1.5f;
        public object interactable = null;
        public void RaycastInteract() { }
        public void RaycastInteract(int mode) { }
    }
}
