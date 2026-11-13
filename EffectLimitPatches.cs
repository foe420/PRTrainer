using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PocketRoguesTrainer
{
    /// <summary>
    /// "No limits": SO_ItemEquip.NormalizeEffects runs on every load, equip and chest read and
    /// trims items back to the quality budget. Instead of replacing the game's logic, take a
    /// snapshot before it runs and put back only what the budget trimming removed. Duplicate,
    /// blacklist and legacy "default-only" removals still apply. Inert unless No limits is on
    /// and the game is offline.
    /// </summary>
    [HarmonyPatch(typeof(SO_ItemEquip), nameof(SO_ItemEquip.NormalizeEffects))]
    internal static class Patch_NormalizeEffects
    {
        internal sealed class Snapshot
        {
            public List<KeyValuePair<ItemEffectArtifact, int>> Ring;
            public List<ItemEffect> Effects;
            public Dictionary<ItemEffect, int> Levels;
        }

        private static void Prefix(SO_ItemEquip __instance, out Snapshot __state)
        {
            __state = null;
            if (__instance == null || !Cheats.NoEffectLimits) return;
            try
            {
                if (__instance is SO_ItemRing ring)
                {
                    if (ring.RingResultEffects == null) return;
                    __state = new Snapshot
                    {
                        Ring = ring.RingResultEffects
                            .Where(e => e != null)
                            .Select(e => new KeyValuePair<ItemEffectArtifact, int>(e, e.customLvl))
                            .ToList(),
                    };
                    return;
                }
                IList list = EffectList(__instance);
                if (list == null) return;
                __state = new Snapshot
                {
                    Effects = list.Cast<ItemEffect>().Where(e => e != null).ToList(),
                    Levels = __instance.effectsLvl != null ? new Dictionary<ItemEffect, int>(__instance.effectsLvl) : null,
                };
            }
            catch (Exception ex)
            {
                __state = null;
                TrainerPlugin.Log.LogDebug("NormalizeEffects snapshot: " + ex.Message);
            }
        }

        private static void Postfix(SO_ItemEquip __instance, Snapshot __state)
        {
            if (__state == null) return;
            try
            {
                if (__instance is SO_ItemRing ring)
                {
                    if (ring.RingResultEffects == null) ring.RingResultEffects = new List<ItemEffectArtifact>();
                    foreach (KeyValuePair<ItemEffectArtifact, int> kv in __state.Ring)
                    {
                        if (!ring.RingResultEffects.Contains(kv.Key)) ring.RingResultEffects.Add(kv.Key); // trimmed by budget
                        kv.Key.customLvl = kv.Value;                                                     // level clamp undone
                    }
                    return;
                }

                IList list = EffectList(__instance);
                if (list == null) return;
                foreach (ItemEffect effect in __state.Effects)
                {
                    if (list.Contains(effect) || DeliberatelyRemoved(effect)) continue;
                    list.Add(effect);
                    if (__state.Levels != null && __state.Levels.TryGetValue(effect, out int level))
                    {
                        if (__instance.effectsLvl == null) __instance.effectsLvl = new ItemEffectIntDictionary();
                        __instance.effectsLvl[effect] = level;
                    }
                }
            }
            catch (Exception ex)
            {
                TrainerPlugin.Log.LogDebug("NormalizeEffects restore: " + ex.Message);
            }
        }

        private static IList EffectList(SO_ItemEquip item)
        {
            switch (item)
            {
                case SO_ItemWeapon w: return w.effects;
                case SO_ItemHead h: return h.effects;
                case SO_ItemBody b: return b.effects;
                case SO_ItemShield s: return s.effects;
                default: return null;
            }
        }

        private static bool DeliberatelyRemoved(ItemEffect e) =>
            SO_ItemEquip.effectsBlacklist.Contains(e.name) || SO_ItemEquip.effectsBlacklist_ONLY_DEFS.Contains(e.name);
    }

    /// <summary>
    /// "No limits": ChestItemSlot.LoadItemFromSaveString only reads save-string fields 3..7
    /// (or 3..6) as effects, i.e. 5 (or 4) effects. The window comes from
    ///     num = HasDefaultEffects ? 8 : 7;
    /// so a call is inserted just before that store to widen it while No limits is on.
    /// </summary>
    [HarmonyPatch(typeof(ChestItemSlot), nameof(ChestItemSlot.LoadItemFromSaveString))]
    internal static class Patch_ChestEffectWindow
    {
        internal static bool Applied;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo hasDefaults = AccessTools.PropertyGetter(typeof(SO_ItemEquip), nameof(SO_ItemEquip.HasDefaultEffects));
            int call = code.FindIndex(ci => ci.Calls(hasDefaults));
            int store = -1;
            if (call >= 0)
            {
                for (int i = call + 1; i < Math.Min(code.Count, call + 8); i++)
                {
                    if (code[i].IsStloc())
                    {
                        store = i;
                        break;
                    }
                }
            }
            if (store < 0)
            {
                TrainerPlugin.Log.LogWarning("Chest effect window not found (game updated?) - fortress chests keep the game's 5/4 effect limit.");
                return code;
            }

            var hook = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_ChestEffectWindow), nameof(Window)));
            hook.MoveLabelsFrom(code[store]); // both "? 8 : 7" branches jump to the store
            code.Insert(store, hook);
            Applied = true;
            return code;
        }

        public static int Window(int original) => Cheats.NoEffectLimits ? original + 32 : original;
    }
}
