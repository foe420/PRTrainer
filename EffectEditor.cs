using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using I2.Loc;
using UnityEngine;

namespace PocketRoguesTrainer
{
    internal enum EffectKind { Ring, Weapon, Helmet, Body, Shield }

    internal enum RingSkillMode { None, Skill, Slot }

    /// <summary>Display and rule metadata for one effect asset (cached per asset).</summary>
    internal sealed class EffectInfo
    {
        public ItemEffect Asset;
        public string Title;
        public string DupKey;            // the game's "same effect" rule
        public int MaxLevel;             // 0-based, so 4 = level V
        public bool Leveled;             // weapon/armor: tracked in effectsLvl; ring: has more than one level
        public bool Valid;               // reloads from its save path
        public string InvalidReason;
        public bool OnlyAsDefault;       // the game removes it unless it is a default effect
        public RingSkillMode SkillMode;
        public CharacterSkill[] Skills;  // RingSkillMode.Skill choices
        public Texture2D IconTexture;
        public Rect IconUv;
        public string PoolDescription;   // description at max level, for the pool list
        public string SearchKey;
    }

    /// <summary>One effect chosen for the item being edited.</summary>
    internal sealed class EffectChoice
    {
        public EffectInfo Info;
        public int Level;
        public int SkillIndex;
        public int SkillSlot;
        public bool Locked; // weapon/armor default effect: the game re-adds it on load at a quality-based level
    }

    /// <summary>Editing state for one item template.</summary>
    internal sealed class EffectEditorSession
    {
        public ItemEntry Entry;
        public EffectKind Kind;
        public SO_ItemEquip Template;
        public readonly List<EffectChoice> Choices = new List<EffectChoice>();
        public List<EffectInfo> OwnPool;
        private List<EffectInfo> _allPool;
        public int ChestSafeCap;
        public int DefaultCount;

        public List<EffectInfo> AllPool => _allPool ?? (_allPool = EffectEditor.BuildAllPool(Kind));

        public bool NoLimits => Cheats.NoEffectLimits;

        // The game's NormalizeEffects (run on every load/equip/chest) enforces:
        //   rings:        sum of (level + 1) over all effects <= quality + 2
        //   weapon/armor: number of effects <= quality index + default effects
        public int Budget(Qualities q) => Kind == EffectKind.Ring ? (int)q + 2 : (int)q + DefaultCount;

        public int Used(Qualities q)
        {
            if (Kind != EffectKind.Ring) return Choices.Count;
            int points = 0;
            foreach (EffectChoice c in Choices) points += EffectEditor.EffectiveLevel(c, q) + 1;
            return points;
        }

        /// <summary>Null when the game would keep the item exactly as edited.</summary>
        public string LimitProblem(Qualities q)
        {
            if (NoLimits) return null;
            if (Used(q) > Budget(q))
            {
                return Kind == EffectKind.Ring
                    ? $"uses {Used(q)} points but {Cheats.QualityName(q)} allows {Budget(q)}"
                    : $"has {Choices.Count} effects but {Cheats.QualityName(q)} allows {Budget(q)}";
            }
            if (Choices.Count > ChestSafeCap) return $"has {Choices.Count} effects but a fortress chest keeps {ChestSafeCap}";
            return null;
        }

        /// <summary>True when the game, left to itself, would trim this item.</summary>
        public bool BeyondGameLimits(Qualities q) => Used(q) > Budget(q) || Choices.Count > ChestSafeCap;

        public bool CanRaise(EffectChoice c, Qualities q) =>
            !c.Locked && c.Level < c.Info.MaxLevel &&
            (NoLimits || Kind != EffectKind.Ring || Used(q) + 1 <= Budget(q));

        public string KindName =>
            Kind == EffectKind.Ring ? "Ring" : Kind == EffectKind.Weapon ? "Weapon" :
            Kind == EffectKind.Helmet ? "Helmet" : Kind == EffectKind.Body ? "Armor" : "Off-hand";

        public string KindPlural =>
            Kind == EffectKind.Ring ? "ring" : Kind == EffectKind.Weapon ? "weapon" :
            Kind == EffectKind.Helmet ? "helmet" : Kind == EffectKind.Body ? "armor" : "off-hand";

        /// <summary>Null when the effect can be added, otherwise the reason it can't.</summary>
        public string CannotAddReason(EffectInfo info, Qualities q)
        {
            if (info == null) return "missing";
            if (!info.Valid) return info.InvalidReason;
            if (info.OnlyAsDefault) return "default-only";
            foreach (EffectChoice c in Choices)
            {
                if (c.Info == info || c.Info.DupKey == info.DupKey) return "on item";
            }
            if (Kind == EffectKind.Ring && ConflictsWith(info) is EffectInfo other) return "conflicts: " + other.Title;
            if (!NoLimits)
            {
                if (Choices.Count + 1 > ChestSafeCap) return "max " + ChestSafeCap + " effects";
                if (Used(q) + 1 > Budget(q)) return Kind == EffectKind.Ring ? "no points left" : "quality limit";
            }
            return null;
        }

        /// <summary>New effects start at the highest level the budget allows.</summary>
        public void Add(EffectInfo info, Qualities q)
        {
            if (CannotAddReason(info, q) != null) return;
            int level = info.MaxLevel;
            if (!NoLimits && Kind == EffectKind.Ring) level = Mathf.Clamp(Budget(q) - Used(q) - 1, 0, info.MaxLevel);
            Choices.Add(new EffectChoice { Info = info, Level = level });
        }

        /// <summary>Raises levels in list order while the budget allows.</summary>
        public void MaxAllLevels(Qualities q)
        {
            foreach (EffectChoice c in Choices)
                while (CanRaise(c, q)) c.Level++;
        }

        /// <summary>The game refuses to roll an effect listed as incompatible with one already on the ring.</summary>
        private EffectInfo ConflictsWith(EffectInfo info)
        {
            if (!(info.Asset is ItemEffectArtifact candidate)) return null;
            foreach (EffectChoice c in Choices)
            {
                if (!(c.Info.Asset is ItemEffectArtifact have)) continue;
                if (Lists(have.incompatibleEffects, candidate) || Lists(candidate.incompatibleEffects, have)) return c.Info;
            }
            return null;
        }

        private static bool Lists(List<ItemEffectArtifact> list, ItemEffectArtifact x) =>
            list != null && list.Any(e => e != null && (e == x || e.effect == x.effect));
    }

    internal static class EffectEditor
    {
        private static readonly Dictionary<int, EffectInfo> Cache = new Dictionary<int, EffectInfo>();
        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly FieldInfo RingSkillsField =
            typeof(ItemEffectArt_RingSkill).GetField("allAvailableSkills", BindingFlags.Instance | BindingFlags.NonPublic);

        // ---------------------------------------------------------------- sessions

        public static bool CanEdit(ItemEntry e) =>
            e != null && e.QualitySelectable && KindOf(e.Template).HasValue;

        public static EffectKind? KindOf(SO_Item item)
        {
            switch (item)
            {
                case SO_ItemRing _: return EffectKind.Ring;
                case SO_ItemWeapon _: return EffectKind.Weapon;
                case SO_ItemHead _: return EffectKind.Helmet;
                case SO_ItemBody _: return EffectKind.Body;
                case SO_ItemShield _: return EffectKind.Shield;
                default: return null;
            }
        }

        public static EffectEditorSession Begin(ItemEntry entry)
        {
            var template = (SO_ItemEquip)entry.Template;
            var session = new EffectEditorSession
            {
                Entry = entry,
                Kind = KindOf(template).Value,
                Template = template,
                // ChestItemSlot.LoadItemFromSaveString reads at most 5 effects (4 without defaults).
                ChestSafeCap = template.HasDefaultEffects ? 5 : 4,
            };
            session.OwnPool = BuildPool(OwnPoolOf(template));

            if (template is SO_ItemRing ring)
            {
                // Rings are fully editable: start from the base effect, which may be removed.
                EffectInfo baseInfo = Info(ring.DefaultRingEffect);
                if (baseInfo != null && baseInfo.Valid)
                    session.Choices.Add(new EffectChoice { Info = baseInfo, Level = 0 });
            }
            else
            {
                foreach (ItemEffect def in DefaultsOf(template))
                {
                    EffectInfo info = Info(def);
                    if (info == null) continue;
                    session.Choices.Add(new EffectChoice { Info = info, Locked = true });
                    session.DefaultCount++;
                }
            }
            return session;
        }

        /// <summary>Effects this item can roll naturally (rings also get their base effect back).</summary>
        private static IEnumerable<ItemEffect> OwnPoolOf(SO_ItemEquip item)
        {
            switch (item)
            {
                case SO_ItemRing r:
                    if (r.DefaultRingEffect != null) yield return r.DefaultRingEffect;
                    if (r.AllRandomEffects != null) foreach (var e in r.AllRandomEffects) yield return e;
                    break;
                case SO_ItemWeapon w:
                    if (w.allPossibleEffects != null) foreach (var e in w.allPossibleEffects) yield return e;
                    break;
                case SO_ItemHead h:
                    if (h.allEffects != null) foreach (var e in h.allEffects) yield return e;
                    break;
                case SO_ItemBody b:
                    if (b.allEffects != null) foreach (var e in b.allEffects) yield return e;
                    break;
                case SO_ItemShield s:
                    if (s.allEffects != null) foreach (var e in s.allEffects) yield return e;
                    break;
            }
        }

        private static IEnumerable<ItemEffect> DefaultsOf(SO_ItemEquip item)
        {
            switch (item)
            {
                case SO_ItemWeapon w: return w.defaultEffects ?? new List<ItemEffectWeapon>();
                case SO_ItemHead h: return h.defaultEffects ?? new List<ItemEffectArmor>();
                case SO_ItemBody b: return b.defaultEffects ?? new List<ItemEffectArmor>();
                case SO_ItemShield s: return s.defaultEffects ?? new List<ItemEffectArmor>();
                default: return Enumerable.Empty<ItemEffect>();
            }
        }

        /// <summary>Union of every same-kind item's pool, e.g. every effect any ring can roll.</summary>
        public static List<EffectInfo> BuildAllPool(EffectKind kind)
        {
            ItemCatalog.EnsureBuilt();
            IEnumerable<ItemEffect> all = ItemCatalog.Items
                .Where(e => e.Template is SO_ItemEquip && KindOf(e.Template) == kind)
                .SelectMany(e => OwnPoolOf((SO_ItemEquip)e.Template));
            return BuildPool(all);
        }

        private static List<EffectInfo> BuildPool(IEnumerable<ItemEffect> effects)
        {
            var seen = new HashSet<int>();
            var list = new List<EffectInfo>();
            foreach (ItemEffect e in effects)
            {
                if (e == null || !seen.Add(e.GetInstanceID())) continue;
                EffectInfo info = Info(e);
                if (info == null || IsBlacklisted(e)) continue; // the game strips these on load
                list.Add(info);
            }
            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // ------------------------------------------------------------ effect info

        public static EffectInfo Info(ItemEffect asset)
        {
            if (asset == null) return null;
            int id = asset.GetInstanceID();
            if (Cache.TryGetValue(id, out EffectInfo cached)) return cached;
            EffectInfo info;
            try
            {
                info = Create(asset);
            }
            catch (Exception ex)
            {
                TrainerPlugin.Log.LogDebug($"Effect {asset.name}: {ex.Message}");
                info = new EffectInfo { Asset = asset, Title = asset.name, DupKey = "X:" + asset.name, InvalidReason = "unreadable", SearchKey = asset.name.ToLowerInvariant() };
            }
            Cache[id] = info;
            return info;
        }

        private static EffectInfo Create(ItemEffect asset)
        {
            var info = new EffectInfo { Asset = asset };

            // Same-effect rule, mirroring the NormalizeEffects overrides: same effect type is a
            // duplicate, except "add buff" (compared by buff) and armor "perk stats" (by stat).
            switch (asset)
            {
                case ItemEffectArtifact art:
                    info.DupKey = "A:" + art.effect;
                    break;
                case ItemEffectWeapon w:
                    info.DupKey = w.effect == WeaponEffects._addEffect && w.effectBuff != null
                        ? "W:buff:" + w.effectBuff.buffType
                        : "W:" + w.effect;
                    break;
                case ItemEffectArmor a:
                    if (a.effect == ArmorEffect._addEffect && a.effectBuff != null)
                        info.DupKey = "R:buff:" + a.effectBuff.buffType;
                    else if (a.effect == ArmorEffect._addPerkStats && a.perkStats != null && a.perkStats.Length > 0 && a.perkStats[0] != null)
                        info.DupKey = "R:perk:" + a.perkStats[0].type;
                    else
                        info.DupKey = "R:" + a.effect;
                    break;
                default:
                    info.DupKey = "X:" + asset.name;
                    break;
            }

            // Levels. Ring effects are read as customLvls[customLvl] (unchecked in several
            // places), weapon/armor effects as customLvlValues[level].
            if (asset is ItemEffectArtifact artifact)
            {
                int n = artifact.customLvls != null ? artifact.customLvls.Length : 0;
                info.MaxLevel = Mathf.Clamp(n - 1, 0, 4);
                info.Leveled = info.MaxLevel > 0;
            }
            else
            {
                int[] values = asset.customLvlValues;
                info.Leveled = values != null && values.Length > 0 && values[0] != 0;
                info.MaxLevel = info.Leveled ? Mathf.Clamp(values.Length - 1, 0, 4) : 0;
            }

            if (asset is ItemEffectArt_RingSkill ringSkill)
            {
                if (ringSkill.effect == ArtifactEffects.RingRainbow_AddSkillLvls)
                {
                    info.SkillMode = RingSkillMode.Skill;
                    info.Skills = (RingSkillsField?.GetValue(ringSkill) as CharacterSkill[]) ?? new CharacterSkill[0];
                }
                else if (ringSkill.effect == ArtifactEffects.RingRainbow_SkillTypeLvls)
                {
                    info.SkillMode = RingSkillMode.Slot;
                }
            }

            info.OnlyAsDefault = SO_ItemEquip.effectsBlacklist_ONLY_DEFS.Contains(asset.name);
            info.Valid = PathReloads(asset, out info.InvalidReason);
            info.Title = TitleOf(asset);
            info.PoolDescription = Describe(info, info.MaxLevel, 0, 0);
            info.SearchKey = (info.Title + " " + info.PoolDescription + " " + asset.name).ToLowerInvariant();
            SetIcon(info, asset.Icon);
            return info;
        }

        private static bool IsBlacklisted(ItemEffect e)
        {
            string last = string.IsNullOrEmpty(e.path) ? e.name : e.path.Split('/').Last();
            return SO_ItemEquip.effectsBlacklist.Contains(e.name) || SO_ItemEquip.effectsBlacklist.Contains(last);
        }

        /// <summary>Effects are saved by Resources path and reloaded the same way the game does.</summary>
        private static bool PathReloads(ItemEffect asset, out string reason)
        {
            reason = null;
            string path = asset.path;
            if (string.IsNullOrEmpty(path))
            {
                reason = "no save path";
                return false;
            }
            if (Load(asset, path) == null && Load(asset, path.Split('/').Last()) == null)
            {
                reason = "won't reload";
                return false;
            }
            return true;
        }

        private static UnityEngine.Object Load(ItemEffect asset, string path)
        {
            switch (asset)
            {
                case ItemEffectArtifact _: return Resources.Load<ItemEffectArtifact>(path);
                case ItemEffectWeapon _: return Resources.Load<ItemEffectWeapon>(path);
                case ItemEffectArmor _: return Resources.Load<ItemEffectArmor>(path);
                default: return Resources.Load<ItemEffect>(path);
            }
        }

        // --------------------------------------------------------------- display

        public static string Roman(int level) => level >= 0 && level <= 4 ? new[] { "I", "II", "III", "IV", "V" }[level] : (level + 1).ToString();

        /// <summary>Locked defaults get the level the game assigns on load: Clamp(quality, 0, 4).</summary>
        public static int EffectiveLevel(EffectChoice c, Qualities quality) =>
            Mathf.Clamp(c.Locked ? Mathf.Clamp((int)quality, 0, 4) : c.Level, 0, c.Info.MaxLevel);

        public static string ChoiceTitle(EffectChoice c, Qualities quality)
        {
            EffectInfo info = c.Info;
            string title = info.Title;
            if (info.SkillMode == RingSkillMode.Skill) title = SkillName(info, c.SkillIndex);
            else if (info.SkillMode == RingSkillMode.Slot) title += " - " + SlotName(c.SkillSlot);
            if (info.Leveled) title += " (" + Roman(EffectiveLevel(c, quality)) + ")";
            return title;
        }

        public static string SkillName(EffectInfo info, int index)
        {
            if (info.Skills == null || info.Skills.Length == 0) return "Skill";
            CharacterSkill skill = info.Skills[Mathf.Clamp(index, 0, info.Skills.Length - 1)];
            if (skill == null) return "Skill #" + index;
            string cls = skill.mainClass.ToString();
            cls = cls == "NECROM" ? "Necromancer" : cls.Substring(0, 1) + cls.Substring(1).ToLowerInvariant();
            return cls + ": " + Clean(SafeString(() => skill.Title, skill.name));
        }

        public static string SlotName(int slot)
        {
            string key = slot == 0 ? "Characters/SkillTypes/MainSpd" : slot == 1 ? "Characters/SkillTypes/Power" : "Characters/SkillTypes/Special";
            string fallback = slot == 0 ? "Main attack" : slot == 1 ? "Power skill" : "Special skill";
            string t = SafeString(() => LocalizationManager.GetTranslation(key), fallback);
            return string.IsNullOrEmpty(t) ? fallback : t;
        }

        /// <summary>Same text the game's tooltip builds (ItemEffectContainer.Initialize).</summary>
        public static string Describe(EffectInfo info, int level, int skillIndex, int skillSlot)
        {
            try
            {
                if (info.Asset is ItemEffectArtifact art)
                {
                    string d = SafeString(() => art.descriptionLocalized, "");
                    ItemEffectArtifact.ItemEffectLvl lvl =
                        art.customLvls != null && art.customLvls.Length > level ? art.customLvls[level] : null;
                    if (info.SkillMode == RingSkillMode.Skill)
                    {
                        string skillTitle = SkillName(info, skillIndex);
                        int colon = skillTitle.IndexOf(": ", StringComparison.Ordinal);
                        if (colon >= 0) skillTitle = skillTitle.Substring(colon + 2);
                        return Clean(Format(d.Replace("{TITLE}", skillTitle.ToUpperInvariant()), lvl?.value1 ?? 0f));
                    }
                    if (info.SkillMode == RingSkillMode.Slot)
                        return Clean(Format(d.Replace("{TYPE}", SlotName(skillSlot).ToLowerInvariant()), lvl?.value1 ?? 0f));
                    if (lvl != null)
                        return Clean(Format(d, art.value, lvl.value1, lvl.value2, lvl.value3));
                    return Clean(art.GetDescription(0));
                }
                return Clean(info.Asset.GetDescription(level));
            }
            catch
            {
                return "";
            }
        }

        private static string Format(string text, params object[] args)
        {
            try { return string.Format(text, args); }
            catch (FormatException) { return text; }
        }

        private static string TitleOf(ItemEffect asset)
        {
            string t = SafeString(() => asset.GetTitle, null);
            if (string.IsNullOrWhiteSpace(t)) t = asset.name;
            return Clean(t);
        }

        private static string SafeString(Func<string> get, string fallback)
        {
            try { return get() ?? fallback; }
            catch { return fallback; }
        }

        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : Tags.Replace(s, "").Replace("\n", " ").Trim();

        private static void SetIcon(EffectInfo info, Sprite sprite)
        {
            if (sprite == null) return;
            try
            {
                Texture2D tex = sprite.texture;
                if (tex == null || tex.width == 0 || tex.height == 0) return;
                Rect r;
                try { r = sprite.textureRect; }
                catch { r = sprite.rect; }
                info.IconTexture = tex;
                info.IconUv = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);
            }
            catch
            {
                info.IconTexture = null;
            }
        }

        // ---------------------------------------------------------------- building

        /// <summary>
        /// Builds the item from a fresh template copy, the same way the game rebuilds a saved item:
        /// set quality without rolling effects, then add the chosen effects explicitly.
        /// </summary>
        public static SO_Item Build(EffectEditorSession s, Qualities quality, bool normalize = true)
        {
            var copy = ItemDrop.ItemCopy(s.Template) as SO_ItemEquip;
            if (copy == null) return null;

            copy.ResetInitialization();
            copy.SetInitialized(false);
            copy.useRandomQuality = false;
            copy.ChangeQuality(quality, false, true); // rings: clears effects; weapon/armor: adds defaults only

            switch (copy)
            {
                case SO_ItemRing ring:
                    ring.RingResultEffects = new List<ItemEffectArtifact>();
                    foreach (EffectChoice c in s.Choices)
                    {
                        ItemEffectArtifact effect = UnityEngine.Object.Instantiate((ItemEffectArtifact)c.Info.Asset);
                        effect.customLvl = Mathf.Clamp(c.Level, 0, c.Info.MaxLevel);
                        if (effect is ItemEffectArt_RingSkill ringSkill)
                        {
                            if (c.Info.SkillMode == RingSkillMode.Skill && c.Info.Skills.Length > 0)
                                ringSkill.GetSkillByIndex(Mathf.Clamp(c.SkillIndex, 0, c.Info.Skills.Length - 1));
                            else if (c.Info.SkillMode == RingSkillMode.Slot)
                                ringSkill.skillSlot = (SO_Skill.SkillType)Mathf.Clamp(c.SkillSlot, 0, 2);
                        }
                        ring.RingResultEffects.Add(effect);
                    }
                    break;
                case SO_ItemWeapon weapon:
                    AddChosen(weapon.effects, weapon, s);
                    break;
                case SO_ItemHead head:
                    AddChosen(head.effects, head, s);
                    break;
                case SO_ItemBody body:
                    AddChosen(body.effects, body, s);
                    break;
                case SO_ItemShield shield:
                    AddChosen(shield.effects, shield, s);
                    break;
            }

            // The game runs this on every load; doing it now means what you get is what it keeps.
            if (normalize) copy.NormalizeEffects();
            return copy;
        }

        private static void AddChosen<T>(List<T> effects, SO_ItemEquip item, EffectEditorSession s) where T : ItemEffect
        {
            foreach (EffectChoice c in s.Choices)
            {
                if (c.Locked || !(c.Info.Asset is T effect) || effects.Contains(effect)) continue;
                effects.Add(effect);
                if (!c.Info.Leveled) continue;
                if (item.effectsLvl == null) item.effectsLvl = new ItemEffectIntDictionary();
                item.effectsLvl[effect] = Mathf.Clamp(c.Level, 0, c.Info.MaxLevel);
            }
        }

        // ---------------------------------------------------------------- checking

        /// <summary>The effects exactly as chosen, before the game's normalization touches them.</summary>
        public static string[] IntendedEffects(EffectEditorSession s, Qualities quality)
        {
            SO_Item raw = Build(s, quality, normalize: false);
            return raw != null ? DungeonSaving.GetEffects(raw) : new string[0];
        }

        /// <summary>
        /// Runs the item through the game's own inventory save/load code
        /// (DungItemFromSOItem -> CreateDisplayItem) and compares the result with the chosen effects.
        /// </summary>
        public static bool VerifyInventoryRoundTrip(SO_Item item, string[] intended, out string report)
        {
            try
            {
                string[] before = intended ?? DungeonSaving.GetEffects(item);
                DungItem saved = ItemsBanEnforcer.DungItemFromSOItem(item);
                SO_Item reloaded = saved != null ? ItemsMessagePopupItemBuilder.CreateDisplayItem(saved) : null;
                if (reloaded == null)
                {
                    report = "save check: the item could not be reloaded";
                    return false;
                }
                return Compare(before, DungeonSaving.GetEffects(reloaded), out report);
            }
            catch (Exception ex)
            {
                report = "save check failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Diagnostics only: saves into a temporary fortress-chest key with the game's
        /// ChestItemSlot.SaveItem, reloads it, then deletes the key.
        /// </summary>
        public static bool VerifyChestRoundTrip(SO_Item item, string[] intended, out string report)
        {
            const string chestId = "_trainerSelfTest";
            string key = "chest" + chestId + "0";
            var host = new GameObject("PocketRoguesTrainer.ChestProbe");
            host.SetActive(false); // no Awake/OnEnable on the probe component
            try
            {
                ChestItemSlot slot = host.AddComponent<ChestItemSlot>();
                slot.SaveItem(item, 0, chestId);
                SO_Item reloaded = ChestItemSlot.LoadItemFromSaveString(PlayerPrefs.GetString(key));
                if (reloaded == null)
                {
                    report = "chest check: the item could not be reloaded";
                    return false;
                }
                return Compare(intended ?? DungeonSaving.GetEffects(item), DungeonSaving.GetEffects(reloaded), out report);
            }
            catch (Exception ex)
            {
                report = "chest check failed: " + ex.Message;
                return false;
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
                UnityEngine.Object.Destroy(host);
            }
        }

        /// <summary>Compares saved-effect strings ("path~level[~skill]") by effect, then by level/skill.</summary>
        private static bool Compare(string[] before, string[] after, out string report)
        {
            var b = before.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var a = after.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (b.SequenceEqual(a))
            {
                report = $"all {b.Count} effects survive";
                return true;
            }

            var wanted = b.GroupBy(PathOf).ToDictionary(g => g.Key, g => g.ToList());
            var got = a.GroupBy(PathOf).ToDictionary(g => g.Key, g => g.ToList());
            var lost = new List<string>();
            var changed = new List<string>();
            var added = new List<string>();
            int intact = 0;
            foreach (var kv in wanted)
            {
                if (!got.TryGetValue(kv.Key, out List<string> have)) { lost.Add(ShortName(kv.Key)); continue; }
                if (kv.Value.OrderBy(x => x).SequenceEqual(have.OrderBy(x => x))) { intact++; continue; }
                changed.Add($"{ShortName(kv.Key)} {LevelOf(kv.Value[0])}->{LevelOf(have[0])}");
            }
            foreach (string key in got.Keys)
                if (!wanted.ContainsKey(key)) added.Add(ShortName(key));

            report = $"{intact} of {wanted.Count} effects survive unchanged" +
                     (changed.Count > 0 ? "; changed: " + string.Join(", ", changed.ToArray()) : "") +
                     (lost.Count > 0 ? "; lost: " + string.Join(", ", lost.ToArray()) : "") +
                     (added.Count > 0 ? "; added by the game: " + string.Join(", ", added.ToArray()) : "");
            return false;
        }

        private static string PathOf(string saved) => saved.Split('~')[0];

        private static string LevelOf(string saved)
        {
            string[] parts = saved.Split('~');
            return parts.Length > 1 && int.TryParse(parts[1], out int level) ? Roman(level) : "-";
        }

        private static string ShortName(string path)
        {
            int slash = path.LastIndexOf('/');
            string name = slash >= 0 ? path.Substring(slash + 1) : path;
            foreach (string prefix in new[] { "Ring Effect - ", "Ring Effect Base - ", "Weapon Effect - ", "Armor Effect - " })
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return name.Substring(prefix.Length);
            return name;
        }
    }
}
