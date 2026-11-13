using System;
using HarmonyLib;
using UnityEngine;

namespace PocketRoguesTrainer
{
    /// <summary>
    /// Cheat state and the game-facing actions. Everything here runs on Unity's main
    /// thread (called from the plugin's Update/OnGUI), which is what the game's own
    /// code expects.
    /// </summary>
    internal static class Cheats
    {
        /// <summary>
        /// Gold/crystals are 32-bit ints and the game resets the value to 0 when an
        /// addition overflows to a negative number, so stay well below int.MaxValue.
        /// </summary>
        public const int MaxCurrency = 2_000_000_000;

        /// <summary>
        /// Persisted (unlike the toggles below): items built beyond the game's effect limits
        /// only keep their effects while this stays on, so it must survive restarts.
        /// </summary>
        public static BepInEx.Configuration.ConfigEntry<bool> NoLimitsSetting;

        public static bool NoEffectLimits => NoLimitsSetting != null && NoLimitsSetting.Value && Allowed;

        // Runtime-only toggles: everything starts OFF on each game launch.
        public static bool GodMode;
        public static bool InfiniteMana;
        public static bool OneHitKill;
        public static float DamageMultiplier = 1f;

        /// <summary>Cheats are inert while an online (Photon Bolt) session is connected.</summary>
        public static bool Allowed
        {
            get
            {
                try { return !Controller.IsConnected; }
                catch { return false; }
            }
        }

        public const string OnlineMessage = "Disabled during online sessions.";

        /// <summary>True while a dungeon run is loaded (GameManager only exists there).</summary>
        public static bool InRun => GameManager.Instance != null;

        public static Character LocalPlayer()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return null;
            try { return gm.Player_Mine(); }
            catch { return null; }
        }

        public static bool IsLocalPlayer(Creature creature)
        {
            if (!(creature is Character character)) return false;
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.playerList == null) return false;
            return gm.playerList.Contains(character);
        }

        /// <summary>Per-frame upkeep for the toggle cheats.</summary>
        public static void Tick()
        {
            if (!(GodMode || InfiniteMana) || !Allowed) return;
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.playerList == null) return;
            var players = gm.playerList;
            for (int i = 0; i < players.Count; i++)
            {
                Character ch = players[i];
                if (ch == null || !ch.alive) continue;
                // Damage-over-time and anti-regen subtract HP directly (not through the
                // damage gate patched below), so top HP up every frame as well.
                if (GodMode && ch.HP_cur < ch.HP_max) ch.HP_cur = ch.HP_max;
                if (InfiniteMana && ch.ST_cur < ch.ST_max) ch.ST_cur = ch.ST_max;
            }
        }

        // ------------------------------------------------------------------ player

        public static string FullHeal()
        {
            if (!Allowed) return OnlineMessage;
            Character ch = LocalPlayer();
            if (ch == null) return "Enter a dungeon run first.";
            ch.HP_cur = ch.HP_max;
            ch.ST_cur = ch.ST_max;
            return "Healed and mana refilled.";
        }

        public static string LevelUp()
        {
            if (!Allowed) return OnlineMessage;
            Character ch = LocalPlayer();
            if (ch == null) return "Enter a dungeon run first.";
            if (!ch.alive) return "Your character is dead.";
            int need = Mathf.Max(1, Mathf.CeilToInt(ch.xp_max - ch.xp_cur) + 1);
            // ignoreModifiers so XP bonuses/penalties don't over- or under-shoot.
            ch.AddXP(need, false, null, true);
            return $"Level {ch.lvl} -> {ch.lvl + 1} (the XP bar fills over ~30 frames).";
        }

        // ---------------------------------------------------------------- currency

        public static int GetGold()
        {
            GameManager gm = GameManager.Instance;
            // During a run the game caches gold in curMoney (+ pending moneyMod) and
            // writes it back to PlayerPrefs when it saves.
            if (gm != null) return (int)Math.Min(int.MaxValue, (long)gm.curMoney + gm.moneyMod);
            return PlayerPrefs.GetInt("money");
        }

        public static string SetGold(long value)
        {
            if (!Allowed) return OnlineMessage;
            int v = ClampCurrency(value);
            GameManager gm = GameManager.Instance;
            if (gm != null)
            {
                gm.moneyMod = 0;
                gm.curMoney = v;
            }
            PlayerPrefs.SetInt("money", v);
            PlayerPrefs.Save();
            RefreshCurrencyUi();
            return $"Gold set to {v:N0}.";
        }

        public static int GetCrystals() => PlayerPrefs.GetInt("gems");

        public static string SetCrystals(long value)
        {
            if (!Allowed) return OnlineMessage;
            int v = ClampCurrency(value);
            // During a run, crystals are written straight to PlayerPrefs; curGems is
            // only the HUD copy.
            PlayerPrefs.SetInt("gems", v);
            GameManager gm = GameManager.Instance;
            if (gm != null)
            {
                gm.GemsMod = 0;
                gm.curGems = v;
            }
            PlayerPrefs.Save();
            RefreshCurrencyUi();
            return $"Crystals set to {v:N0}.";
        }

        private static int ClampCurrency(long value) => (int)Math.Max(0L, Math.Min(MaxCurrency, value));

        private static void RefreshCurrencyUi()
        {
            try
            {
                if (MoneyGemsPanel.Instance != null) MoneyGemsPanel.Instance.OnUpdate();
            }
            catch (Exception e)
            {
                TrainerPlugin.Log.LogDebug("Currency UI refresh failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------- items

        public static string Give(ItemEntry entry, Qualities quality, int count)
        {
            if (entry == null) return "No item selected.";
            if (!Allowed) return OnlineMessage;
            Character ch = LocalPlayer();
            if (ch == null) return "Enter a dungeon run first - items go into your character's inventory.";
            if (!ch.alive) return "Your character is dead.";

            count = Mathf.Clamp(count, 1, 99);
            int given = 0;
            for (int i = 0; i < count; i++)
            {
                SO_Item copy = MakeCopy(entry, quality);
                if (copy == null) break;
                // Same entry point the game uses for pickups: stacks consumables,
                // auto-equips into empty slots, and drops the item at your feet if
                // the inventory is full.
                ch.AddItem(copy, null, i == 0, false);
                given++;
            }
            if (given == 0) return "Could not create " + entry.Title + ".";
            string q = entry.QualitySelectable ? " [" + QualityName(quality) + "]" : "";
            return $"Gave {given}x {entry.Title}{q}.";
        }

        /// <summary>Gives equipment with hand-picked effects (see EffectEditor).</summary>
        public static string GiveCustom(EffectEditorSession session, Qualities quality, int count, out bool problem)
        {
            problem = true;
            if (session == null) return "No item selected.";
            if (!Allowed) return OnlineMessage;
            Character ch = LocalPlayer();
            if (ch == null) return "Enter a dungeon run first - items go into your character's inventory.";
            if (!ch.alive) return "Your character is dead.";
            string limit = session.LimitProblem(quality);
            if (limit != null)
                return $"Not given: this item {limit}. Lower levels or remove effects, raise the quality, or turn on No limits.";

            SO_Item first = EffectEditor.Build(session, quality);
            if (first == null) return "Could not create " + session.Entry.Title + ".";
            string[] intended = EffectEditor.IntendedEffects(session, quality);
            bool saveOk = EffectEditor.VerifyInventoryRoundTrip(first, intended, out string check);

            count = Mathf.Clamp(count, 1, 99);
            int given = 0;
            for (int i = 0; i < count; i++)
            {
                SO_Item item = i == 0 ? first : EffectEditor.Build(session, quality);
                if (item == null) break;
                ch.AddItem(item, null, i == 0, false);
                given++;
            }

            problem = !saveOk || given == 0;
            string beyond = session.BeyondGameLimits(quality)
                ? " Beyond the game's limits: it keeps these effects only while No limits stays on (offline)."
                : "";
            return $"Gave {given}x {session.Entry.Title} [{QualityName(quality)}] with {session.Choices.Count} effects. " +
                   $"Save check: {check}.{beyond}";
        }

        /// <summary>Diagnostics: build the custom item and run it through both save formats.</summary>
        public static string SelfTestCustom(EffectEditorSession session, Qualities quality)
        {
            SO_Item item = EffectEditor.Build(session, quality);
            if (item == null) return "Self-test: could not build the item.";
            string[] intended = EffectEditor.IntendedEffects(session, quality);
            bool inv = EffectEditor.VerifyInventoryRoundTrip(item, intended, out string invReport);
            bool chest = EffectEditor.VerifyChestRoundTrip(item, intended, out string chestReport);
            string signature = string.Join(" | ", intended);
            TrainerPlugin.Log.LogInfo($"[self-test] {session.Entry.Path} [{quality}] effects: {signature}");
            return $"Self-test [{(NoEffectLimits ? "no limits" : "game rules")}, chest patch {(Patch_ChestEffectWindow.Applied ? "on" : "off")}] - " +
                   $"inventory/hero save: {(inv ? "OK" : "PROBLEM")} ({invReport}); " +
                   $"fortress chest: {(chest ? "OK" : "PROBLEM")} ({chestReport}).";
        }

        /// <summary>
        /// Never mutate the Resources template: copy it first (as the game does), then
        /// roll the chosen quality the same way the game's own debug helper does.
        /// </summary>
        private static SO_Item MakeCopy(ItemEntry entry, Qualities quality)
        {
            SO_Item copy = ItemDrop.ItemCopy(entry.Template);
            if (copy == null) return null;

            if (copy is SO_ItemUsable usable)
            {
                usable.count = 1;
            }
            else if (entry.QualitySelectable && copy is SO_ItemEquip equip)
            {
                equip.ResetInitialization();
                equip.SetInitialized(false);
                equip.useRandomQuality = false;
                equip.ChangeQuality(quality, true, true);
                equip.AddEffectLevels();
            }
            else if (entry.QualitySelectable && copy is SO_ItemTrash gem && quality != Qualities.COMMON1)
            {
                gem.CraftGem_SetQuality(quality);
            }
            return copy;
        }

        public static readonly Qualities[] SelectableQualities =
        {
            Qualities.COMMON1, Qualities.UNCOMMON2, Qualities.RARE3, Qualities.EPIC4, Qualities.LEGENDARY5
        };

        public static readonly string[] SelectableQualityNames = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };

        public static string QualityName(Qualities q)
        {
            int i = Array.IndexOf(SelectableQualities, q);
            return i >= 0 ? SelectableQualityNames[i] : q.ToString();
        }
    }

    // ======================================================================= patches

    /// <summary>God mode: every hit goes through Creature.SetDamageRPC -> CanReceiveDamage.</summary>
    [HarmonyPatch(typeof(Creature), "CanReceiveDamage")]
    internal static class Patch_CanReceiveDamage
    {
        private static bool Prefix(Creature __instance, ref bool __result)
        {
            if (Cheats.GodMode && Cheats.Allowed && Cheats.IsLocalPlayer(__instance))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>Damage multiplier / one-hit kills, applied only to non-friendly creatures.</summary>
    [HarmonyPatch(typeof(Creature), nameof(Creature.SetDamageRPC))]
    internal static class Patch_SetDamageRPC
    {
        private static void Prefix(Creature __instance, ref DamageStruct damageStruct)
        {
            if (!Cheats.OneHitKill && Cheats.DamageMultiplier <= 1.001f) return;
            if (__instance == null || __instance is Character) return;
            if (__instance.faction == c.Factions.Human) return; // pets, summons, friendly NPCs
            if (!Cheats.Allowed) return;

            float mult = Cheats.DamageMultiplier;
            if (Cheats.OneHitKill)
            {
                float total = (float)damageStruct;
                // 10x the target's max HP survives armour/resistance reductions.
                if (total > 0.0001f) mult = Mathf.Max(mult, __instance.HP_max * 10f / total);
            }
            if (mult > 1.001f) damageStruct *= mult;
        }
    }
}
