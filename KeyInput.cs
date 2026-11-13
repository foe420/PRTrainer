using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PocketRoguesTrainer
{
    /// <summary>
    /// Key detection that doesn't depend on a single input layer. Unity's legacy Input,
    /// Windows' own key state and the game's Rewired keyboard are all consulted, so the
    /// hotkey still works if one of them swallows the key.
    /// </summary>
    internal static class KeyInput
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static readonly Dictionary<KeyCode, bool> WasDown = new Dictionary<KeyCode, bool>();

        /// <summary>Which sources reported the key going down this frame (for diagnostics).</summary>
        public struct Hit
        {
            public bool Unity, Windows, Rewired;
            public bool Any => Unity || Windows || Rewired;
            public override string ToString() => $"unity={Unity} windows={Windows} rewired={Rewired}";
        }

        /// <summary>Must be called exactly once per frame per key so edge detection stays correct.</summary>
        public static Hit PressedThisFrame(KeyCode key)
        {
            var hit = new Hit();
            if (key == KeyCode.None) return hit;

            try { hit.Unity = Input.GetKeyDown(key); } catch { /* legacy input unavailable */ }

            bool osDown = OsKeyDown(key);
            WasDown.TryGetValue(key, out bool was);
            WasDown[key] = osDown;
            hit.Windows = osDown && !was && Application.isFocused;

            try
            {
                if (Rewired.ReInput.isReady)
                {
                    Rewired.Keyboard kb = Rewired.ReInput.controllers.Keyboard;
                    hit.Rewired = kb != null && kb.enabled && kb.GetKeyDown(key);
                }
            }
            catch { /* Rewired not ready */ }

            return hit;
        }

        public static bool IsHeld(KeyCode key)
        {
            bool held = false;
            try { held = Input.GetKey(key); } catch { }
            return held || OsKeyDown(key);
        }

        public static bool ModifiersHeld(IEnumerable<KeyCode> modifiers)
        {
            foreach (KeyCode m in modifiers)
                if (!IsHeld(m)) return false;
            return true;
        }

        private static bool OsKeyDown(KeyCode key)
        {
            int vk = ToVirtualKey(key);
            if (vk == 0) return false;
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }
        }

        /// <summary>Unity KeyCode to Win32 virtual-key code (common keys only).</summary>
        private static int ToVirtualKey(KeyCode k)
        {
            if (k >= KeyCode.F1 && k <= KeyCode.F15) return 0x70 + (k - KeyCode.F1);
            if (k >= KeyCode.A && k <= KeyCode.Z) return 0x41 + (k - KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return 0x30 + (k - KeyCode.Alpha0);
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return 0x60 + (k - KeyCode.Keypad0);
            switch (k)
            {
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Pause: return 0x13;
                case KeyCode.ScrollLock: return 0x91;
                case KeyCode.BackQuote: return 0xC0;
                case KeyCode.Backslash: return 0xDC;
                case KeyCode.LeftShift: return 0xA0;
                case KeyCode.RightShift: return 0xA1;
                case KeyCode.LeftControl: return 0xA2;
                case KeyCode.RightControl: return 0xA3;
                case KeyCode.LeftAlt: return 0xA4;
                case KeyCode.RightAlt: return 0xA5;
                default: return 0;
            }
        }
    }
}
