using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PocketRoguesTrainer
{
    internal enum ItemCategory
    {
        Weapon, Offhand, Helmet, Armor, Ring, Amulet, Consumable, Passive, Artifact, Gem, Treasure, Other
    }

    internal sealed class ItemEntry
    {
        public SO_Item Template;
        public string Title;
        public string InternalName;
        public string Path;
        public string SearchKey;
        public ItemCategory Category;
        public bool QualitySelectable;
        public Texture2D IconTexture;
        public Rect IconUv;
    }

    /// <summary>
    /// Every item the game can save, loaded the same way the game's own map-editor
    /// catalog does it (Resources.LoadAll over SO_Item assets).
    /// </summary>
    internal static class ItemCatalog
    {
        public static readonly string[] CategoryNames =
        {
            "All", "Weapons", "Off-hand", "Helmets", "Armor", "Rings", "Amulets",
            "Usables", "Passives", "Artifacts", "Gems", "Treasures", "Other"
        };

        public static List<ItemEntry> Items { get; private set; }
        public static int SkippedUnsaveable { get; private set; }
        public static string Error { get; private set; }

        private static readonly Regex RichTextTags = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static void EnsureBuilt()
        {
            if (Items == null) Build();
        }

        public static void Build()
        {
            var list = new List<ItemEntry>();
            var seen = new HashSet<int>();
            int skipped = 0;
            Error = null;

            SO_Item[] all;
            try
            {
                all = Resources.LoadAll<SO_Item>("");
            }
            catch (Exception e)
            {
                Error = "Loading items failed: " + e.Message;
                TrainerPlugin.Log.LogError(e);
                Items = list;
                return;
            }

            foreach (SO_Item item in all)
            {
                if (item == null || !seen.Add(item.GetInstanceID())) continue;
                try
                {
                    // Items without a resolvable save path would vanish when the run is
                    // saved, so they are not offered.
                    string path = item.savingObjectPath;
                    if (string.IsNullOrEmpty(path) || DungeonSaving.ResourcesLoad_Item(path) == null)
                    {
                        skipped++;
                        continue;
                    }

                    var entry = new ItemEntry
                    {
                        Template = item,
                        Path = path,
                        Category = Categorize(item),
                        Title = TitleOf(item),
                        InternalName = string.IsNullOrEmpty(item.Name) ? item.name : item.Name,
                    };
                    entry.QualitySelectable =
                        (item is SO_ItemEquip equip && equip.canBeRare) ||
                        (item is SO_ItemTrash trash && trash.craftGem_sprites != null && trash.craftGem_sprites.Length >= 5);
                    entry.SearchKey = (entry.Title + " " + entry.InternalName + " " + path).ToLowerInvariant();
                    SetIcon(entry, item.Icon);
                    list.Add(entry);
                }
                catch (Exception e)
                {
                    skipped++;
                    TrainerPlugin.Log.LogDebug($"Skipping item {item.name}: {e.Message}");
                }
            }

            list.Sort((a, b) =>
            {
                int byCategory = a.Category.CompareTo(b.Category);
                return byCategory != 0 ? byCategory : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            Items = list;
            SkippedUnsaveable = skipped;
            TrainerPlugin.Log.LogInfo($"Item catalog: {list.Count} spawnable items ({skipped} skipped: no save path).");
        }

        /// <summary>Category filter index 0 = All, otherwise ItemCategory + 1.</summary>
        public static bool MatchesCategory(ItemEntry e, int filterIndex) =>
            filterIndex <= 0 || (int)e.Category == filterIndex - 1;

        private static ItemCategory Categorize(SO_Item item)
        {
            switch (item)
            {
                case SO_ItemWeapon _: return ItemCategory.Weapon;
                case SO_ItemShield _: return ItemCategory.Offhand;
                case SO_ItemHead _: return ItemCategory.Helmet;
                case SO_ItemBody _: return ItemCategory.Armor;
                case SO_ItemRing _: return ItemCategory.Ring;
                case SO_ItemAmulet _: return ItemCategory.Amulet;
                case SO_ItemUsable usable: return usable.passive ? ItemCategory.Passive : ItemCategory.Consumable;
                case SO_ItemTrash trash:
                    if (trash.Artifact) return ItemCategory.Artifact;
                    if (trash.Quality == Qualities.TREASURES) return ItemCategory.Treasure;
                    if (trash.GemEffects != null && trash.GemEffects.Length > 0) return ItemCategory.Gem;
                    return ItemCategory.Other;
                default: return ItemCategory.Other;
            }
        }

        private static string TitleOf(SO_Item item)
        {
            string title = null;
            try { title = item.titleLocalized; } catch { /* missing localisation term */ }
            if (string.IsNullOrEmpty(title)) title = item.TitleEN;
            if (string.IsNullOrEmpty(title)) title = item.Name;
            if (string.IsNullOrEmpty(title)) title = item.name;
            return RichTextTags.Replace(title ?? "?", "").Trim();
        }

        private static void SetIcon(ItemEntry entry, Sprite sprite)
        {
            if (sprite == null) return;
            try
            {
                Texture2D tex = sprite.texture;
                if (tex == null || tex.width == 0 || tex.height == 0) return;
                Rect r;
                try { r = sprite.textureRect; }      // throws for tightly packed sprites
                catch { r = sprite.rect; }
                entry.IconTexture = tex;
                entry.IconUv = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);
            }
            catch
            {
                entry.IconTexture = null;
            }
        }
    }
}
