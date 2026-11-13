using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace PocketRoguesTrainer
{
    /// <summary>
    /// All trainer state, input handling and UI. A plain class (not a MonoBehaviour), so it
    /// survives the game destroying BepInEx's manager object; Unity's per-frame callbacks
    /// reach it through <see cref="TrainerRunner"/>.
    /// </summary>
    internal sealed class TrainerCore
    {
        private static ManualLogSource Log => TrainerPlugin.Log;
        private const string PluginName = TrainerPlugin.PluginName;
        private const string PluginVersion = TrainerPlugin.PluginVersion;

        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<bool> _pauseWhileOpen;
        private ConfigEntry<float> _uiScale;
        private ConfigEntry<bool> _diagnostics;

        private float _lastToggleTime = -10f;
        private bool _restoreInputPending;
        private bool _loggedFirstUpdate, _loggedFirstGui;
        private float _nextHeartbeat;

        private readonly HashSet<string> _loggedErrors = new HashSet<string>();

        // ---- menu state
        private bool _open;
        private Rect _window = new Rect(40f, 40f, 620f, 680f);
        private int _tab;
        private int _pendingTab = -1; // applied on the next Layout event (IMGUI needs a stable layout per frame)
        private static readonly string[] TabNames = { "Player", "Items", "Currency", "Unlocks" };
        private string _message = "";
        private bool _messageIsError;
        private float _messageTime;

        // ---- pause / input handling while the menu is open
        private bool _pausedByUs;
        private float _savedTimeScale = 1f;
        private bool _savedCursorVisible;
        private CursorLockMode _savedCursorLock;
        private bool _inputBlocked;
        private bool _savedKeyboardEnabled;
        private bool _savedMouseEnabled;
        // The game's menus take clicks through Unity's EventSystem (not Rewired), so it is
        // switched off too while the menu is open; otherwise clicks land on buttons underneath.
        private readonly List<EventSystem> _disabledEventSystems = new List<EventSystem>();
        private bool _uiBlocked;

        // ---- items tab
        private string _search = "";
        private int _categoryFilter;
        private int _qualityIndex = 4; // Legendary
        private int _count = 1;
        private Vector2 _itemScroll;
        private readonly List<ItemEntry> _filtered = new List<ItemEntry>();
        private string _lastSearch;
        private int _lastCategory = -1;

        // ---- effect editor (inside the Items tab); opened/closed only on Layout events
        private EffectEditorSession _editor;
        private ItemEntry _pendingEditorOpen;
        private bool _pendingEditorClose;
        private int _poolMode; // 0 = this item's pool, 1 = every same-kind item's pool
        private string _poolSearch = "";
        private Vector2 _chosenScroll, _poolScroll;
        private const float ChosenRowHeight = 42f;
        private const float ChosenListHeight = 152f;
        private const float PoolRowHeight = 40f;
        private const float PoolListHeight = 160f;

        // ---- unlocks tab (data refreshed on Layout events, about once a second)
        private List<Unlocks.Category> _shopCategories;
        private string _forgeRecipeStatus = "", _forgeEffectStatus = "";
        private float _unlocksRefreshedAt = -10f;
        private Vector2 _unlockScroll;
        private const float UnlockRowHeight = 28f;
        private const float UnlockListHeight = 236f;

        // ---- currency tab
        private string _goldInput = "";
        private string _crystalInput = "";

        // ---- styles (created lazily inside OnGUI)
        private GUIStyle _windowStyle, _msgStyle, _errStyle, _warnStyle, _dimStyle, _titleStyle, _subStyle, _rowStyle, _headStyle, _centerStyle;
        private Texture2D _windowBg, _rowBg;

        private const int WindowId = 0x50525431;
        private const float RowHeight = 40f;
        private const float ListHeight = 360f;

        // =================================================================== lifecycle

        public TrainerCore(ConfigFile config)
        {
            _toggleKey = config.Bind("General", "ToggleMenu", new KeyboardShortcut(KeyCode.F8),
                "Key that opens and closes the trainer menu.");
            _pauseWhileOpen = config.Bind("General", "PauseWhileOpen", true,
                "While the menu is open during a run: pause the game and stop it from reading keyboard/mouse " +
                "(so typing in the search box or clicking doesn't move or attack). Single-player only.");
            _uiScale = config.Bind("General", "UiScale", 0f,
                "Menu scale factor. 0 = automatic (1.25 at 1080p, grows with resolution).");
            _diagnostics = config.Bind("Debug", "Diagnostics", false,
                "Write extra troubleshooting lines (key detection, scene loads, heartbeat) to BepInEx\\LogOutput.log.");
            Cheats.NoLimitsSetting = config.Bind("Items", "NoEffectLimits", false,
                "Lets the effect editor go beyond the game's limits (ring point budget, effect count, fortress-chest " +
                "effect limit). Items built that way keep their extra effects only while this stays true and the " +
                "trainer is installed (offline); otherwise the game trims them the next time they load.");
        }

        public KeyboardShortcut ToggleKey => _toggleKey.Value;

        /// <summary>The runner object went away: undo any pause / input blocking we applied.</summary>
        public void OnRunnerDestroyed()
        {
            if (_open) SetOpen(false);
            _restoreInputPending = false;
            BlockGameInput(false);
            BlockUiInput(false);
        }

        public void Tick()
        {
            try
            {
                if (!_loggedFirstUpdate)
                {
                    _loggedFirstUpdate = true;
                    Diag($"Update loop running (scene '{SceneManager.GetActiveScene().name}').");
                }
                if (_diagnostics.Value && Time.unscaledTime >= _nextHeartbeat)
                {
                    _nextHeartbeat = Time.unscaledTime + 20f;
                    Diag($"alive t={Time.unscaledTime:0}s scene='{SceneManager.GetActiveScene().name}' focused={Application.isFocused} " +
                         $"timeScale={Time.timeScale} inRun={Cheats.InRun} offline={Cheats.Allowed} menuOpen={_open}");
                }

                // Poll both keys every frame (edge detection needs it), then act.
                KeyboardShortcut shortcut = _toggleKey.Value;
                KeyInput.Hit toggle = KeyInput.PressedThisFrame(shortcut.MainKey);
                KeyInput.Hit escape = KeyInput.PressedThisFrame(KeyCode.Escape);

                if (toggle.Any && KeyInput.ModifiersHeld(shortcut.Modifiers))
                {
                    Diag($"toggle key {shortcut} seen ({toggle})");
                    // Sources can report the same press a frame apart; debounce.
                    if (Time.unscaledTime - _lastToggleTime > 0.3f)
                    {
                        _lastToggleTime = Time.unscaledTime;
                        SetOpen(!_open);
                    }
                }
                else if (_open && escape.Any)
                {
                    SetOpen(false);
                }

                // Give the game its keyboard/mouse back only once the keys are released,
                // so it doesn't see the closing Esc as a fresh press (pause menu).
                if (_restoreInputPending && !KeyInput.IsHeld(shortcut.MainKey) && !KeyInput.IsHeld(KeyCode.Escape))
                {
                    _restoreInputPending = false;
                    BlockGameInput(false);
                    BlockUiInput(false);
                }
                // A scene change while the menu is open can bring a new EventSystem.
                if (_open && _uiBlocked) DisableEventSystem(EventSystem.current);

                Cheats.Tick();
            }
            catch (Exception e)
            {
                LogOnce("Update", e);
            }
        }

        internal void Diag(string message)
        {
            if (_diagnostics != null && _diagnostics.Value) Log.LogInfo("[diag] " + message);
        }

        /// <summary>Diagnostics only: lets external test tooling see whether the menu is open.</summary>
        internal void WriteStateFile()
        {
            if (_diagnostics == null || !_diagnostics.Value) return;
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "trainer-state.txt"),
                    (_open ? "open" : "closed") + " " + DateTime.Now.ToString("o"));
            }
            catch
            {
                // best effort
            }
        }

        public void LateTick()
        {
            if (!_open) return;
            // The game hides (and sometimes locks) the OS cursor and draws its own.
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            WriteStateFile();
            try
            {
                if (open)
                {
                    _savedCursorVisible = Cursor.visible;
                    _savedCursorLock = Cursor.lockState;
                    Diag("menu opened");
                    _restoreInputPending = false;
                    if (_pauseWhileOpen.Value)
                    {
                        BlockGameInput(true);
                        BlockUiInput(true);
                        // Never touch timeScale online: that would desync from other players.
                        if (Cheats.Allowed && Cheats.InRun && Time.timeScale > 0f)
                        {
                            _savedTimeScale = Time.timeScale;
                            Time.timeScale = 0f;
                            _pausedByUs = true;
                        }
                    }
                }
                else
                {
                    if (_pausedByUs)
                    {
                        // Only undo our own pause; if the game changed timeScale meanwhile, leave it.
                        if (Time.timeScale == 0f) Time.timeScale = _savedTimeScale;
                        _pausedByUs = false;
                    }
                    Diag("menu closed");
                    _restoreInputPending = _inputBlocked || _uiBlocked; // restored in Tick once keys are released
                    Cursor.visible = _savedCursorVisible;
                    Cursor.lockState = _savedCursorLock;
                    GUIUtility.keyboardControl = 0;
                }
            }
            catch (Exception e)
            {
                LogOnce("SetOpen", e);
            }
        }

        /// <summary>The game reads input through Rewired; disabling its keyboard/mouse
        /// controllers mutes gameplay input while IMGUI (legacy input) keeps working.</summary>
        private void BlockGameInput(bool block)
        {
            try
            {
                if (!Rewired.ReInput.isReady) return;
                Rewired.Keyboard keyboard = Rewired.ReInput.controllers.Keyboard;
                Rewired.Mouse mouse = Rewired.ReInput.controllers.Mouse;
                if (block && !_inputBlocked)
                {
                    _savedKeyboardEnabled = keyboard.enabled;
                    _savedMouseEnabled = mouse.enabled;
                    keyboard.enabled = false;
                    mouse.enabled = false;
                    _inputBlocked = true;
                }
                else if (!block && _inputBlocked)
                {
                    keyboard.enabled = _savedKeyboardEnabled;
                    mouse.enabled = _savedMouseEnabled;
                    _inputBlocked = false;
                }
            }
            catch (Exception e)
            {
                _inputBlocked = false;
                LogOnce("BlockGameInput", e);
            }
        }

        private void BlockUiInput(bool block)
        {
            try
            {
                if (block)
                {
                    _uiBlocked = true;
                    foreach (EventSystem es in UnityEngine.Object.FindObjectsOfType<EventSystem>())
                        DisableEventSystem(es);
                }
                else
                {
                    _uiBlocked = false;
                    foreach (EventSystem es in _disabledEventSystems)
                        if (es != null) es.enabled = true;
                    _disabledEventSystems.Clear();
                }
            }
            catch (Exception e)
            {
                LogOnce("BlockUiInput", e);
            }
        }

        private void DisableEventSystem(EventSystem es)
        {
            if (es == null || !es.enabled) return;
            es.enabled = false;
            _disabledEventSystems.Add(es);
        }

        private void LogOnce(string where, Exception e)
        {
            if (_loggedErrors.Add(where + "|" + e.GetType().Name + "|" + e.Message))
                Log.LogError($"[{where}] {e}");
        }

        private void Say(string message, bool error = false)
        {
            _message = message ?? "";
            _messageIsError = error;
            _messageTime = Time.unscaledTime;
            if (!string.IsNullOrEmpty(_message)) Log.LogInfo(_message);
        }

        // ======================================================================== GUI

        public void DrawGui()
        {
            if (!_loggedFirstGui)
            {
                _loggedFirstGui = true;
                Diag("OnGUI running");
            }
            if (!_open) return;
            try
            {
                EnsureStyles();
                // Auto: 1.25x at 1080p (IMGUI's default font is small), scaling with resolution.
                float scale = _uiScale.Value > 0.1f ? _uiScale.Value : Mathf.Clamp(Screen.height / 864f, 1f, 3f);
                Matrix4x4 previous = GUI.matrix;
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

                float screenW = Screen.width / scale, screenH = Screen.height / scale;
                _window.width = Mathf.Min(620f, screenW - 20f);
                _window.height = Mathf.Min(680f, screenH - 20f);
                _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, screenW - _window.width));
                _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, screenH - _window.height));

                _window = GUI.Window(WindowId, _window, DrawWindow,
                    $"{PluginName} {PluginVersion}   ({_toggleKey.Value} / Esc to close)", _windowStyle);
                GUI.matrix = previous;
            }
            catch (Exception e)
            {
                LogOnce("OnGUI", e);
            }
        }

        private void DrawWindow(int id)
        {
            try
            {
                if (Event.current.type == EventType.Layout && _pendingTab >= 0)
                {
                    _tab = _pendingTab;
                    _pendingTab = -1;
                }
                GUILayout.BeginVertical();
                int clickedTab = GUILayout.Toolbar(_tab, TabNames, GUILayout.Height(28f));
                if (clickedTab != _tab) _pendingTab = clickedTab;
                GUILayout.Space(4f);
                DrawStatus();
                GUILayout.Space(6f);

                switch (_tab)
                {
                    case 0: DrawPlayerTab(); break;
                    case 1: DrawItemsTab(); break;
                    case 2: DrawCurrencyTab(); break;
                    case 3: DrawUnlocksTab(); break;
                }

                GUILayout.FlexibleSpace();
                // Always emit this label (possibly empty) so the layout has the same
                // controls in every event of a frame.
                bool showMessage = Time.unscaledTime - _messageTime < 10f;
                GUILayout.Label(showMessage ? _message : "", _messageIsError ? _errStyle : _msgStyle, GUILayout.MinHeight(36f));
                GUILayout.EndVertical();
            }
            catch (Exception e)
            {
                LogOnce("DrawWindow", e);
            }
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawStatus()
        {
            if (!Cheats.Allowed)
            {
                GUILayout.Label("Online session detected - cheats are disabled. They only work offline / solo.", _warnStyle);
                return;
            }
            Character ch = Cheats.LocalPlayer();
            if (ch != null)
            {
                GUILayout.Label($"In run:  {ch.charClass}  Lv {ch.lvl}     HP {ch.HP_cur:0}/{ch.HP_max:0}     MP {ch.ST_cur:0}/{ch.ST_max:0}", _headStyle);
            }
            else
            {
                GUILayout.Label("Not in a dungeon run. Player cheats and item spawning need an active run; " +
                                "gold and crystals work anywhere.", _dimStyle);
            }
        }

        // ---------------------------------------------------------------- player tab

        private void DrawPlayerTab()
        {
            Cheats.GodMode = GUILayout.Toggle(Cheats.GodMode, "  God mode  (no damage taken, HP kept full)");
            Cheats.InfiniteMana = GUILayout.Toggle(Cheats.InfiniteMana, "  Infinite mana");
            Cheats.OneHitKill = GUILayout.Toggle(Cheats.OneHitKill, "  One-hit kills  (enemies only)");

            GUILayout.Space(8f);
            GUILayout.Label($"Damage multiplier vs enemies:  x{Cheats.DamageMultiplier:0.#}");
            GUILayout.BeginHorizontal();
            foreach (float m in new[] { 1f, 2f, 5f, 10f, 25f, 100f })
            {
                if (GUILayout.Button("x" + m, GUILayout.Height(26f))) Cheats.DamageMultiplier = m;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Full heal + mana", GUILayout.Height(30f))) Say(Cheats.FullHeal());
            if (GUILayout.Button("+1 level", GUILayout.Height(30f))) Say(Cheats.LevelUp());
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            GUILayout.Label("Toggles reset when the game restarts. Pets, summons and friendly NPCs are not affected " +
                            "by the damage multiplier.", _dimStyle);
        }

        // ----------------------------------------------------------------- items tab

        private void DrawItemsTab()
        {
            if (ItemCatalog.Items == null)
            {
                ItemCatalog.EnsureBuilt();
                _lastCategory = -1; // force refilter
            }
            if (ItemCatalog.Error != null) GUILayout.Label(ItemCatalog.Error, _errStyle);

            if (Event.current.type == EventType.Layout)
            {
                if (_pendingEditorClose)
                {
                    _editor = null;
                    _pendingEditorClose = false;
                }
                if (_pendingEditorOpen != null)
                {
                    ItemEntry target = _pendingEditorOpen;
                    _pendingEditorOpen = null;
                    try
                    {
                        _editor = EffectEditor.Begin(target);
                        _chosenScroll = _poolScroll = Vector2.zero;
                        _poolSearch = "";
                        _poolMode = 0;
                    }
                    catch (Exception ex)
                    {
                        _editor = null;
                        Say("Could not open the effect editor: " + ex.Message, true);
                        LogOnce("EffectEditor.Begin:" + target.Path, ex);
                    }
                }
            }
            if (_editor != null)
            {
                DrawEffectEditor();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", GUILayout.Width(52f));
            GUI.SetNextControlName("trainer_search");
            _search = GUILayout.TextField(_search ?? "", 60);
            if (GUILayout.Button("x", GUILayout.Width(24f))) _search = "";
            GUILayout.EndHorizontal();

            _categoryFilter = GUILayout.SelectionGrid(_categoryFilter, ItemCatalog.CategoryNames, 7);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Quality:", GUILayout.Width(52f));
            _qualityIndex = GUILayout.Toolbar(_qualityIndex, Cheats.SelectableQualityNames);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Count:", GUILayout.Width(52f));
            if (GUILayout.Button("-", GUILayout.Width(28f))) _count = Mathf.Max(1, _count - 1);
            GUILayout.Label(_count.ToString(), GUILayout.Width(30f));
            if (GUILayout.Button("+", GUILayout.Width(28f))) _count = Mathf.Min(99, _count + 1);
            foreach (int n in new[] { 1, 5, 10, 25 })
            {
                if (GUILayout.Button(n.ToString(), GUILayout.Width(36f))) _count = n;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            RefilterIfNeeded();
            List<ItemEntry> items = _filtered;
            GUILayout.Label($"{items.Count} items" +
                            (ItemCatalog.Items != null ? $"  (of {ItemCatalog.Items.Count})" : "") +
                            "  -  quality applies to equipment that can roll quality, and crafting gems", _dimStyle);

            Rect area = GUILayoutUtility.GetRect(100f, 10000f, ListHeight, ListHeight, GUILayout.ExpandWidth(true));
            Rect content = new Rect(0f, 0f, area.width - 20f, Mathf.Max(area.height, items.Count * RowHeight));
            _itemScroll = GUI.BeginScrollView(area, _itemScroll, content, false, true);

            int first = Mathf.Max(0, Mathf.FloorToInt(_itemScroll.y / RowHeight));
            int last = Mathf.Min(items.Count, first + Mathf.CeilToInt(area.height / RowHeight) + 2);
            Qualities quality = Cheats.SelectableQualities[Mathf.Clamp(_qualityIndex, 0, Cheats.SelectableQualities.Length - 1)];
            for (int i = first; i < last; i++)
            {
                ItemEntry e = items[i];
                Rect row = new Rect(0f, i * RowHeight, content.width, RowHeight - 2f);
                if ((i & 1) == 0) GUI.Box(row, GUIContent.none, _rowStyle);

                Rect icon = new Rect(row.x + 4f, row.y + 3f, 32f, 32f);
                if (e.IconTexture != null) GUI.DrawTextureWithTexCoords(icon, e.IconTexture, e.IconUv, true);

                float textW = row.width - 44f - 100f - 84f - 84f - 8f;
                GUI.Label(new Rect(icon.xMax + 8f, row.y + 1f, textW, 20f), e.Title, _titleStyle);
                GUI.Label(new Rect(icon.xMax + 8f, row.y + 20f, textW, 16f), e.InternalName, _subStyle);
                GUI.Label(new Rect(row.xMax - 270f, row.y + 10f, 100f, 20f),
                    ItemCatalog.CategoryNames[(int)e.Category + 1] + (e.QualitySelectable ? " *" : ""), _subStyle);

                if (EffectEditor.CanEdit(e) &&
                    GUI.Button(new Rect(row.xMax - 168f, row.y + 6f, 80f, RowHeight - 14f), "Effects..."))
                {
                    _pendingEditorOpen = e;
                }

                if (GUI.Button(new Rect(row.xMax - 84f, row.y + 6f, 78f, RowHeight - 14f), "Give"))
                {
                    try
                    {
                        string result = Cheats.Give(e, quality, _count);
                        Say(result, !result.StartsWith("Gave"));
                    }
                    catch (Exception ex)
                    {
                        Say("Spawning failed: " + ex.Message, true);
                        LogOnce("Give:" + e.Path, ex);
                    }
                }
            }
            GUI.EndScrollView();
        }

        private Qualities CurrentQuality() =>
            Cheats.SelectableQualities[Mathf.Clamp(_qualityIndex, 0, Cheats.SelectableQualities.Length - 1)];

        // ------------------------------------------------------------ effect editor

        private void DrawEffectEditor()
        {
            EffectEditorSession s = _editor;
            Qualities quality = CurrentQuality();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("< Back", GUILayout.Width(64f))) _pendingEditorClose = true;
            GUILayout.Label($"  {s.Entry.Title}   ({s.KindName})", _headStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Quality:", GUILayout.Width(52f));
            _qualityIndex = GUILayout.Toolbar(_qualityIndex, Cheats.SelectableQualityNames);
            GUILayout.EndHorizontal();

            GUILayout.Label(BudgetLine(s, quality), _dimStyle);

            Rect chosenArea = GUILayoutUtility.GetRect(100f, 10000f, ChosenListHeight, ChosenListHeight, GUILayout.ExpandWidth(true));
            DrawChosenEffects(chosenArea, s, quality);

            GUILayout.BeginHorizontal();
            _poolMode = GUILayout.Toolbar(_poolMode, new[] { "This item's pool", $"All {s.KindPlural} effects" }, GUILayout.Width(290f));
            GUILayout.Label(" Search:", GUILayout.Width(52f));
            _poolSearch = GUILayout.TextField(_poolSearch ?? "", 40);
            if (GUILayout.Button("x", GUILayout.Width(24f))) _poolSearch = "";
            GUILayout.EndHorizontal();

            Rect poolArea = GUILayoutUtility.GetRect(100f, 10000f, PoolListHeight, PoolListHeight, GUILayout.ExpandWidth(true));
            DrawEffectPool(poolArea, s, quality);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Count:", GUILayout.Width(44f));
            if (GUILayout.Button("-", GUILayout.Width(26f))) _count = Mathf.Max(1, _count - 1);
            GUILayout.Label(_count.ToString(), GUILayout.Width(28f));
            if (GUILayout.Button("+", GUILayout.Width(26f))) _count = Mathf.Min(99, _count + 1);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All levels max", GUILayout.Width(110f), GUILayout.Height(26f))) s.MaxAllLevels(quality);
            if (GUILayout.Button("Give with these effects", GUILayout.Width(180f), GUILayout.Height(26f)))
            {
                try
                {
                    string result = Cheats.GiveCustom(s, quality, _count, out bool problem);
                    Say(result, problem);
                }
                catch (Exception ex)
                {
                    Say("Spawning failed: " + ex.Message, true);
                    LogOnce("GiveCustom:" + s.Entry.Path, ex);
                }
            }
            GUILayout.EndHorizontal();

            bool noLimits = GUILayout.Toggle(Cheats.NoLimitsSetting.Value, " No limits - any number of effects at any level (saved setting)");
            if (noLimits != Cheats.NoLimitsSetting.Value) Cheats.NoLimitsSetting.Value = noLimits;

            // Always emit this label (text varies) so the layout stays identical within a frame.
            string limitProblem = s.LimitProblem(quality);
            string note;
            GUIStyle noteStyle;
            if (limitProblem != null)
            {
                note = "Over the game's limit: this item " + limitProblem + ". The game would trim it.";
                noteStyle = _errStyle;
            }
            else if (Cheats.NoLimitsSetting.Value)
            {
                note = Cheats.Allowed
                    ? "Over-limit items keep their extras only while No limits stays on with this trainer installed (offline). Otherwise the game trims them at their next load."
                    : "No limits is paused during online sessions.";
                noteStyle = _warnStyle;
            }
            else
            {
                note = s.Kind == EffectKind.Ring
                    ? "Within the game's own rules - the ring stays exactly like this, even without the trainer."
                    : "Within the game's own rules. Default effects are fixed; their level follows quality.";
                noteStyle = _dimStyle;
            }
            GUILayout.Label(note, noteStyle);

            if (_diagnostics.Value && GUILayout.Button("Self-test both save formats (diagnostics)"))
            {
                try { Say(Cheats.SelfTestCustom(s, quality)); }
                catch (Exception ex) { Say("Self-test failed: " + ex.Message, true); LogOnce("SelfTest", ex); }
            }
        }

        private void DrawChosenEffects(Rect area, EffectEditorSession s, Qualities quality)
        {
            GUI.Box(area, GUIContent.none, _rowStyle);
            List<EffectChoice> list = s.Choices;
            Rect content = new Rect(0f, 0f, area.width - 20f, Mathf.Max(area.height, list.Count * ChosenRowHeight));
            _chosenScroll = GUI.BeginScrollView(area, _chosenScroll, content, false, true);
            if (list.Count == 0)
                GUI.Label(new Rect(8f, 8f, content.width - 16f, 40f), "No effects yet - add some from the list below.", _dimStyle);

            int remove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                EffectChoice c = list[i];
                Rect row = new Rect(0f, i * ChosenRowHeight, content.width, ChosenRowHeight - 2f);
                if ((i & 1) == 1) GUI.Box(row, GUIContent.none, _rowStyle);

                Rect icon = new Rect(row.x + 4f, row.y + 5f, 30f, 30f);
                if (c.Info.IconTexture != null) GUI.DrawTextureWithTexCoords(icon, c.Info.IconTexture, c.Info.IconUv, true);

                float right = row.xMax;
                if (c.Locked)
                {
                    GUI.Label(new Rect(right - 58f, row.y + 12f, 56f, 18f), "default", _subStyle);
                }
                else if (GUI.Button(new Rect(right - 30f, row.y + 8f, 26f, 24f), "x"))
                {
                    remove = i;
                }
                right -= 62f;

                if (c.Info.Leveled && !c.Locked)
                {
                    if (GUI.Button(new Rect(right - 84f, row.y + 8f, 24f, 24f), "-")) c.Level = Mathf.Max(0, c.Level - 1);
                    GUI.Label(new Rect(right - 60f, row.y + 8f, 36f, 24f), EffectEditor.Roman(c.Level), _centerStyle);
                    bool wasEnabled = GUI.enabled;
                    GUI.enabled = s.CanRaise(c, quality);
                    if (GUI.Button(new Rect(right - 24f, row.y + 8f, 24f, 24f), "+")) c.Level = Mathf.Min(c.Info.MaxLevel, c.Level + 1);
                    GUI.enabled = wasEnabled;
                    right -= 92f;
                }

                if (c.Info.SkillMode != RingSkillMode.None)
                {
                    int options = c.Info.SkillMode == RingSkillMode.Skill ? Mathf.Max(1, c.Info.Skills.Length) : 3;
                    if (GUI.Button(new Rect(right - 54f, row.y + 8f, 24f, 24f), "<")) CycleSkill(c, -1, options);
                    if (GUI.Button(new Rect(right - 26f, row.y + 8f, 24f, 24f), ">")) CycleSkill(c, +1, options);
                    right -= 60f;
                }

                float textX = icon.xMax + 8f;
                float textW = Mathf.Max(40f, right - textX - 4f);
                GUI.Label(new Rect(textX, row.y + 1f, textW, 20f), EffectEditor.ChoiceTitle(c, quality), _titleStyle);
                GUI.Label(new Rect(textX, row.y + 20f, textW, 18f),
                    EffectEditor.Describe(c.Info, EffectEditor.EffectiveLevel(c, quality), c.SkillIndex, c.SkillSlot), _subStyle);
            }
            GUI.EndScrollView();
            if (remove >= 0) list.RemoveAt(remove);
        }

        private static void CycleSkill(EffectChoice c, int direction, int options)
        {
            if (c.Info.SkillMode == RingSkillMode.Skill) c.SkillIndex = ((c.SkillIndex + direction) % options + options) % options;
            else c.SkillSlot = ((c.SkillSlot + direction) % 3 + 3) % 3;
        }

        private static string BudgetLine(EffectEditorSession s, Qualities quality)
        {
            string q = Cheats.QualityName(quality);
            if (s.NoLimits) return $"Effects on the item: {s.Choices.Count}   -   No limits (each effect still stops at its own top level)";
            if (s.Kind == EffectKind.Ring)
                return $"Points used: {s.Used(quality)} / {s.Budget(quality)} for {q} (level I costs 1 ... V costs 5)   -   effects {s.Choices.Count} / {s.ChestSafeCap}";
            int allowed = Mathf.Min(s.Budget(quality), s.ChestSafeCap);
            return $"Effects on the item: {s.Choices.Count} / {allowed}   ({q}: {s.DefaultCount} default + {(int)quality} more)";
        }

        private void DrawEffectPool(Rect area, EffectEditorSession s, Qualities quality)
        {
            List<EffectInfo> pool = _poolMode == 0 ? s.OwnPool : s.AllPool;
            string[] terms = (_poolSearch ?? "").Trim().ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<EffectInfo> shown = pool;
            if (terms.Length > 0)
            {
                shown = new List<EffectInfo>();
                foreach (EffectInfo info in pool)
                {
                    bool match = true;
                    foreach (string t in terms)
                    {
                        if (info.SearchKey.IndexOf(t, StringComparison.Ordinal) < 0) { match = false; break; }
                    }
                    if (match) shown.Add(info);
                }
            }

            GUI.Box(area, GUIContent.none, _rowStyle);
            Rect content = new Rect(0f, 0f, area.width - 20f, Mathf.Max(area.height, shown.Count * PoolRowHeight));
            _poolScroll = GUI.BeginScrollView(area, _poolScroll, content, false, true);
            if (shown.Count == 0)
            {
                GUI.Label(new Rect(8f, 8f, content.width - 16f, 40f),
                    pool.Count == 0 ? "This item has no random-effect pool - try \"All effects\"." : "No effect matches the search.", _dimStyle);
            }

            int first = Mathf.Max(0, Mathf.FloorToInt(_poolScroll.y / PoolRowHeight));
            int last = Mathf.Min(shown.Count, first + Mathf.CeilToInt(area.height / PoolRowHeight) + 2);
            EffectInfo toAdd = null;
            for (int i = first; i < last; i++)
            {
                EffectInfo info = shown[i];
                Rect row = new Rect(0f, i * PoolRowHeight, content.width, PoolRowHeight - 2f);
                if ((i & 1) == 0) GUI.Box(row, GUIContent.none, _rowStyle);

                Rect icon = new Rect(row.x + 4f, row.y + 4f, 30f, 30f);
                if (info.IconTexture != null) GUI.DrawTextureWithTexCoords(icon, info.IconTexture, info.IconUv, true);

                string reason = s.CannotAddReason(info, quality);
                float textX = icon.xMax + 8f;
                float textW = row.width - textX - 140f;
                string title = info.Title + (info.Leveled ? "   (I-" + EffectEditor.Roman(info.MaxLevel) + ")" : "");
                GUI.Label(new Rect(textX, row.y + 1f, textW, 20f), title, reason == null ? _titleStyle : _subStyle);
                GUI.Label(new Rect(textX, row.y + 20f, textW, 18f), info.PoolDescription, _subStyle);

                if (reason == null)
                {
                    if (GUI.Button(new Rect(row.xMax - 84f, row.y + 6f, 78f, PoolRowHeight - 12f), "Add")) toAdd = info;
                }
                else
                {
                    GUI.Label(new Rect(row.xMax - 134f, row.y + 10f, 130f, 20f), reason, _subStyle);
                }
            }
            GUI.EndScrollView();
            if (toAdd != null) s.Add(toAdd, quality);
        }

        private void RefilterIfNeeded()
        {
            if (ItemCatalog.Items == null) return;
            if (_search == _lastSearch && _categoryFilter == _lastCategory) return;

            _filtered.Clear();
            string[] terms = (_search ?? "").Trim().ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (ItemEntry e in ItemCatalog.Items)
            {
                if (!ItemCatalog.MatchesCategory(e, _categoryFilter)) continue;
                bool ok = true;
                foreach (string t in terms)
                {
                    if (e.SearchKey.IndexOf(t, StringComparison.Ordinal) < 0) { ok = false; break; }
                }
                if (ok) _filtered.Add(e);
            }
            _lastSearch = _search;
            _lastCategory = _categoryFilter;
            _itemScroll = Vector2.zero;
        }

        // -------------------------------------------------------------- currency tab

        private void DrawCurrencyTab()
        {
            int gold = Cheats.GetGold();
            GUILayout.Label($"Gold:  {gold:N0}", _headStyle);
            GUILayout.BeginHorizontal();
            foreach (long n in new long[] { 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000 })
            {
                if (GUILayout.Button("+" + Short(n), GUILayout.Height(26f))) Say(Cheats.SetGold(gold + n));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Set to:", GUILayout.Width(52f));
            _goldInput = GUILayout.TextField(_goldInput ?? "", 13, GUILayout.Width(160f));
            if (GUILayout.Button("Set gold", GUILayout.Width(90f))) SetFromInput(_goldInput, Cheats.SetGold);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(14f);

            int crystals = Cheats.GetCrystals();
            GUILayout.Label($"Crystals:  {crystals:N0}", _headStyle);
            GUILayout.BeginHorizontal();
            foreach (long n in new long[] { 100, 1_000, 10_000, 100_000, 1_000_000 })
            {
                if (GUILayout.Button("+" + Short(n), GUILayout.Height(26f))) Say(Cheats.SetCrystals(crystals + n));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Set to:", GUILayout.Width(52f));
            _crystalInput = GUILayout.TextField(_crystalInput ?? "", 13, GUILayout.Width(160f));
            if (GUILayout.Button("Set crystals", GUILayout.Width(90f))) SetFromInput(_crystalInput, Cheats.SetCrystals);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(12f);
            GUILayout.Label($"Values are capped at {Cheats.MaxCurrency:N0}. The game resets gold/crystals to 0 if an " +
                            "addition overflows a 32-bit number, so leave headroom.", _dimStyle);
            if (Cheats.InRun)
                GUILayout.Label("During a run the game keeps gold in memory and saves it when you change floor or leave; " +
                                "this tab updates both, so it sticks either way.", _dimStyle);
        }

        // ------------------------------------------------------------- unlocks tab

        private void RefreshUnlocks()
        {
            try
            {
                _shopCategories = Unlocks.ShopCategories();
                _forgeRecipeStatus = Unlocks.ForgeRecipeStatus();
                _forgeEffectStatus = Unlocks.ForgeEffectStatus();
            }
            catch (Exception ex)
            {
                _shopCategories = new List<Unlocks.Category>();
                LogOnce("RefreshUnlocks", ex);
            }
            _unlocksRefreshedAt = Time.unscaledTime;
        }

        private void DrawUnlocksTab()
        {
            if (Event.current.type == EventType.Layout && (_shopCategories == null || Time.unscaledTime - _unlocksRefreshedAt > 3f))
                RefreshUnlocks();
            List<Unlocks.Category> categories = _shopCategories ?? new List<Unlocks.Category>();
            bool allowed = Cheats.Allowed;

            int found = 0, total = 0;
            foreach (Unlocks.Category c in categories)
            {
                found += c.Found;
                total += c.Total;
            }

            GUILayout.Label($"Shop blueprints:  {found} / {total} found", _headStyle);
            GUILayout.Label("A found blueprint lets the fortress shop sell that item. Unlocking only marks blueprints as found: " +
                            "items you own and shop stock stay as they are, and quest items are never touched.", _dimStyle);

            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = allowed && found < total;
            if (GUILayout.Button("Unlock all blueprints", GUILayout.Width(190f), GUILayout.Height(28f)))
                Say(UnlockShopMessage(Unlocks.UnlockShop(categories)));
            GUI.enabled = wasEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            Rect area = GUILayoutUtility.GetRect(100f, 10000f, UnlockListHeight, UnlockListHeight, GUILayout.ExpandWidth(true));
            GUI.Box(area, GUIContent.none, _rowStyle);
            Rect content = new Rect(0f, 0f, area.width - 20f, Mathf.Max(area.height, categories.Count * UnlockRowHeight));
            _unlockScroll = GUI.BeginScrollView(area, _unlockScroll, content, false, true);
            Unlocks.Category clicked = null;
            for (int i = 0; i < categories.Count; i++)
            {
                Unlocks.Category c = categories[i];
                Rect row = new Rect(0f, i * UnlockRowHeight, content.width, UnlockRowHeight - 2f);
                if ((i & 1) == 0) GUI.Box(row, GUIContent.none, _rowStyle);
                GUI.Label(new Rect(row.x + 8f, row.y + 4f, row.width - 230f, 20f), c.Name, _titleStyle);
                GUI.Label(new Rect(row.xMax - 210f, row.y + 4f, 110f, 20f),
                    c.Found >= c.Total ? $"{c.Found} / {c.Total}  all" : $"{c.Found} / {c.Total}", c.Found >= c.Total ? _subStyle : _titleStyle);
                GUI.enabled = allowed && c.Found < c.Total;
                if (GUI.Button(new Rect(row.xMax - 90f, row.y + 2f, 84f, UnlockRowHeight - 6f), "Unlock")) clicked = c;
                GUI.enabled = wasEnabled;
            }
            GUI.EndScrollView();
            if (clicked != null) Say(UnlockShopMessage(Unlocks.UnlockShop(new[] { clicked })) + $" ({clicked.Name})");

            GUILayout.Space(8f);
            GUILayout.Label("Forge (the blacksmith in a run)", _headStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Combination recipes: " + _forgeRecipeStatus, GUILayout.Width(330f));
            GUI.enabled = allowed;
            if (GUILayout.Button("Learn all recipes", GUILayout.Width(170f)))
            {
                try
                {
                    Unlocks.LearnAllForgeRecipes();
                    RefreshUnlocks();
                    Say("All forge recipes learned - the forge's recipe list shows every combination.");
                }
                catch (Exception ex)
                {
                    Say("Could not update forge recipes: " + ex.Message, true);
                    LogOnce("LearnAllForgeRecipes", ex);
                }
            }
            GUI.enabled = wasEnabled;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Effect codex: " + _forgeEffectStatus, GUILayout.Width(330f));
            GUI.enabled = allowed;
            if (GUILayout.Button("Reveal all effects", GUILayout.Width(170f)))
            {
                try
                {
                    Unlocks.RevealAllForgeEffects();
                    RefreshUnlocks();
                    Say("All forge effects revealed.");
                }
                catch (Exception ex)
                {
                    Say("Could not update the forge effects: " + ex.Message, true);
                    LogOnce("RevealAllForgeEffects", ex);
                }
            }
            GUI.enabled = wasEnabled;
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Normally an item drop turns into a blueprint ~8.5% of the time, at most once every ~6 minutes, " +
                            "only after 12+ completed raids and with the Blacksmith built (artifacts: Artifacts building level 3+; " +
                            "consumables: 8%, no raid requirement). Forge recipes are learned by crafting a combination once.", _dimStyle);
            if (!allowed) GUILayout.Label("Disabled during online sessions.", _warnStyle);
        }

        private static string UnlockShopMessage(int count) =>
            count == 0
                ? "Nothing to unlock - those blueprints are already found."
                : $"Unlocked {count} blueprint{(count == 1 ? "" : "s")}. The shop shows them the next time the fortress loads (after a run, or restart the game).";

        private void SetFromInput(string text, Func<long, string> setter)
        {
            string digits = (text ?? "").Replace(",", "").Replace(".", "").Replace(" ", "").Replace("_", "");
            if (long.TryParse(digits, out long value) && value >= 0) Say(setter(value));
            else Say("Enter a whole number, e.g. 500000.", true);
        }

        private static string Short(long n)
        {
            if (n >= 1_000_000) return (n / 1_000_000) + "M";
            if (n >= 1_000) return (n / 1_000) + "K";
            return n.ToString();
        }

        // -------------------------------------------------------------------- styles

        private void EnsureStyles()
        {
            if (_windowStyle != null) return;

            _windowBg = SolidTexture(new Color(0.07f, 0.075f, 0.09f, 0.97f));
            _rowBg = SolidTexture(new Color(1f, 1f, 1f, 0.05f));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _windowBg;
            _windowStyle.onNormal.background = _windowBg;
            _windowStyle.normal.textColor = new Color(1f, 0.85f, 0.45f);
            _windowStyle.onNormal.textColor = new Color(1f, 0.85f, 0.45f);
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.padding = new RectOffset(10, 10, 26, 10);

            _rowStyle = new GUIStyle(GUIStyle.none);
            _rowStyle.normal.background = _rowBg;

            _msgStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontStyle = FontStyle.Bold };
            _msgStyle.normal.textColor = new Color(0.55f, 1f, 0.55f);

            _errStyle = new GUIStyle(_msgStyle);
            _errStyle.normal.textColor = new Color(1f, 0.55f, 0.45f);

            _warnStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontStyle = FontStyle.Bold };
            _warnStyle.normal.textColor = new Color(1f, 0.7f, 0.3f);

            _dimStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
            _dimStyle.normal.textColor = new Color(0.72f, 0.72f, 0.75f);

            _headStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13, wordWrap = false, clipping = TextClipping.Clip };
            _headStyle.normal.textColor = Color.white;

            _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, wordWrap = false };
            _titleStyle.normal.textColor = Color.white;

            _subStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, clipping = TextClipping.Clip, wordWrap = false };
            _subStyle.normal.textColor = new Color(0.62f, 0.62f, 0.66f);

            _centerStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _centerStyle.normal.textColor = new Color(1f, 0.85f, 0.45f);
        }

        private static Texture2D SolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}
