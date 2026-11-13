using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PocketRoguesTrainer
{
    /// <summary>
    /// Blueprint and forge unlocks.
    ///
    /// Shop blueprints: the fortress shop sells an item once FileBasedPrefs[item.Name] != 0.
    /// The key also encodes stock/ownership (usables/passives: 1 + copies, equipment: 100 +
    /// durability), so only 0 -> 1 is ever written - the same thing the game's own
    /// ItemsShopManager.DefaultItems() does. Only the shop catalog (Resources "ItemsDB") is
    /// touched; quest/event items such as "cryptEndless" are left alone.
    ///
    /// Forge: recipes are learned by crafting a combination once ("forge_knownRecipes*"),
    /// effects are revealed by socketing them once ("crafting_allEffectsValues"). Both are
    /// strings of '0'/'1' indexed by the forge's own lists; extra characters are ignored.
    /// </summary>
    internal static class Unlocks
    {
        internal sealed class Category
        {
            public string Name;
            public SO_Item[] Items;
            public int Found;
            public int Total;
        }

        private const string RecipesKey = "forge_knownRecipes";
        private const string RecipesDupKey = "forge_knownRecipesDuplicates";
        private const string RecipesSteelKey = "forge_knownRecipesWithSteel";
        private const string EffectsKey = "crafting_allEffectsValues";

        private static ItemsDataBase _db;

        /// <summary>The scene's catalog (what the fortress shop reads) when loaded, else the "ItemsDB" prefab.</summary>
        private static ItemsDataBase Db
        {
            get
            {
                if (ItemsDataBase.Instance != null) return ItemsDataBase.Instance;
                if (_db == null)
                {
                    var prefab = Resources.Load("ItemsDB") as GameObject;
                    _db = prefab != null ? prefab.GetComponent<ItemsDataBase>() : null;
                }
                return _db;
            }
        }

        // ------------------------------------------------------------------ shop

        public static List<Category> ShopCategories()
        {
            var list = new List<Category>();
            ItemsDataBase db = Db;
            if (db == null) return list;
            Add(list, "Helmets", db.equipmentHead);
            Add(list, "Armor", db.equipmentBody);
            Add(list, "Weapons - Warrior", db.equipmentWeapon_WARRIOR);
            Add(list, "Weapons - Archer", db.equipmentWeapon_ARCHER);
            Add(list, "Weapons - Wizard", db.equipmentWeapon_WIZARD);
            Add(list, "Weapons - Hunter", db.equipmentWeapon_HUNTER);
            Add(list, "Weapons - Berserker", db.equipmentWeapon_BERSERKER);
            Add(list, "Weapons - Necromancer", db.equipmentWeapon_NECROMANCER);
            Add(list, "Off-hand - Warrior", db.equipmentSecondWeapon_WARRIOR);
            Add(list, "Off-hand - Archer", db.equipmentSecondWeapon_ARCHER);
            Add(list, "Off-hand - Wizard", db.equipmentSecondWeapon_WIZARD);
            Add(list, "Off-hand - Hunter", db.equipmentSecondWeapon_HUNTER);
            Add(list, "Off-hand - Necromancer", db.equipmentSecondWeapon_NECROMANCER);
            Add(list, "Artifacts", db.artifactItems);
            Add(list, "Consumables", db.usableItems);
            Add(list, "Passives", db.passiveItems);
            return list;
        }

        private static void Add(List<Category> list, string name, SO_Item[] items)
        {
            if (items == null || items.Length == 0) return;
            var c = new Category { Name = name, Items = items };
            Recount(c);
            list.Add(c);
        }

        public static void Recount(Category c)
        {
            c.Found = 0;
            c.Total = 0;
            foreach (SO_Item item in c.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                c.Total++;
                if (FileBasedPrefs.GetInt(item.Name) != 0) c.Found++;
            }
        }

        /// <summary>Unlocks every still-locked blueprint in the given categories.</summary>
        public static int UnlockShop(IEnumerable<Category> categories)
        {
            int unlocked = 0;
            foreach (Category c in categories)
            {
                foreach (SO_Item item in c.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Name)) continue;
                    if (FileBasedPrefs.GetInt(item.Name) != 0) continue; // found already, or owned/stocked
                    FileBasedPrefs.SetInt(item.Name, 1);
                    MarkNewInShop(item);
                    unlocked++;
                }
                Recount(c);
            }
            if (unlocked > 0) PlayerPrefs.Save();
            return unlocked;
        }

        /// <summary>Same "new" badges ItemDrop.GetRecipe sets when a blueprint is picked up.</summary>
        private static void MarkNewInShop(SO_Item item)
        {
            if (!(item is SO_ItemEquip equip)) return;
            switch (equip.EquipSlot)
            {
                case EquipmentSlots.Head:
                    FileBasedPrefs.SetInt("NewHead", 1);
                    break;
                case EquipmentSlots.Body:
                    FileBasedPrefs.SetInt("NewBody", 1);
                    break;
                case EquipmentSlots.Shield:
                case EquipmentSlots.Arrows:
                case EquipmentSlots.Book:
                    if (item is SO_ItemShield shield) FileBasedPrefs.SetInt("NewShield" + shield.characterClass, 1);
                    break;
                case EquipmentSlots.Weapon:
                case EquipmentSlots.Bow:
                case EquipmentSlots.Staff:
                    if (item is SO_ItemWeapon weapon) FileBasedPrefs.SetInt("NewWeapon" + weapon.characterClass, 1);
                    break;
            }
        }

        // ----------------------------------------------------------------- forge

        /// <summary>Exact "known / total" when a forge is loaded (in a run); otherwise what the save says.</summary>
        public static string ForgeRecipeStatus()
        {
            CraftingForgeUI forge = FindForge();
            if (forge != null)
            {
                int total = (forge.customRecipes?.Length ?? 0) + (forge.customRecipes_Duplicates?.Length ?? 0) + (forge.customRecipes_WithSteel?.Length ?? 0);
                int known = CountOnes(RecipesKey, forge.customRecipes?.Length ?? 0) +
                            CountOnes(RecipesDupKey, forge.customRecipes_Duplicates?.Length ?? 0) +
                            CountOnes(RecipesSteelKey, forge.customRecipes_WithSteel?.Length ?? 0);
                return $"{known} / {total} learned";
            }
            if (AllOnes(RecipesKey) && AllOnes(RecipesDupKey) && AllOnes(RecipesSteelKey)) return "all learned";
            int soFar = CountOnes(RecipesKey, -1) + CountOnes(RecipesDupKey, -1) + CountOnes(RecipesSteelKey, -1);
            return $"{soFar} learned so far";
        }

        public static string ForgeEffectStatus()
        {
            CraftingForgeUI forge = FindForge();
            if (forge != null && forge.allEffects != null)
                return $"{CountOnes(EffectsKey, forge.allEffects.Length)} / {forge.allEffects.Length} revealed";
            if (AllOnes(EffectsKey)) return "all revealed";
            return $"{CountOnes(EffectsKey, -1)} revealed so far";
        }

        private static bool AllOnes(string key)
        {
            string s = FileBasedPrefs.GetString(key) ?? "";
            return s.Length > 0 && s.IndexOf('0') < 0;
        }

        public static void LearnAllForgeRecipes()
        {
            CraftingForgeUI forge = FindForge();
            WriteAllOnes(RecipesKey, forge?.customRecipes?.Length ?? 0, 512);
            WriteAllOnes(RecipesDupKey, forge?.customRecipes_Duplicates?.Length ?? 0, 512);
            WriteAllOnes(RecipesSteelKey, forge?.customRecipes_WithSteel?.Length ?? 0, 512);
            PlayerPrefs.Save();
            // A loaded forge keeps its own copy and would write it back on its next save.
            if (forge != null) forge.KnownRecipes_Load();
        }

        public static void RevealAllForgeEffects()
        {
            CraftingForgeUI forge = FindForge();
            WriteAllOnes(EffectsKey, forge?.allEffects?.Length ?? 0, 2048);
            PlayerPrefs.Save();
            if (forge != null)
            {
                MethodInfo reload = typeof(CraftingForgeUI).GetMethod("EffectsState_Initialize", BindingFlags.Instance | BindingFlags.NonPublic);
                reload?.Invoke(forge, null);
            }
        }

        /// <summary>Writes '1' for every index: the exact length when known, a generous run otherwise.</summary>
        private static void WriteAllOnes(string key, int exactLength, int fallbackLength)
        {
            int existing = FileBasedPrefs.GetString(key)?.Length ?? 0;
            int length = Math.Max(exactLength > 0 ? exactLength : fallbackLength, existing);
            FileBasedPrefs.SetString(key, new string('1', length));
        }

        private static int CountOnes(string key, int limit)
        {
            string s = FileBasedPrefs.GetString(key) ?? "";
            int n = limit >= 0 ? Math.Min(limit, s.Length) : s.Length;
            int count = 0;
            for (int i = 0; i < n; i++)
                if (s[i] == '1') count++;
            return count;
        }

        private static CraftingForgeUI FindForge()
        {
            try
            {
                if (CraftingForgeUI.Instance != null) return CraftingForgeUI.Instance;
                foreach (CraftingForgeUI f in Resources.FindObjectsOfTypeAll<CraftingForgeUI>())
                    if (f != null && f.customRecipes != null) return f;
            }
            catch
            {
                // not loaded in this scene
            }
            return null;
        }
    }
}
