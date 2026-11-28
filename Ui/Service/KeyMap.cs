using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.Locality;
using Lemora.Utils;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>one key combination of the "Key mapping" page; combo, description and group are language keys</summary>
    public sealed record KeyDef(string Id, string ComboKey, string DescriptionKey, string GroupKey, bool Fixed = false)
    {
        public string Combo => IoC.Translate(ComboKey);
        public string Description => IoC.Translate(DescriptionKey);
        public string Group => IoC.Translate(GroupKey);
    }

    /// <summary>
    /// "Key mapping": for each key combination, does it go to the remote PC or stay on this PC.
    /// Three levels: RDP defaults, VNC defaults (page in the settings), and the keys changed by hand in one connection (KeyOverrides of that
    /// connection: only the changed keys, so a later change of another default reaches the connection too).
    /// Defaults and screen settings are in the encrypted database (AppDbStore), the overrides in the connection.
    /// </summary>
    public static class KeyMap
    {
        public const string GroupWindows = "keymap_group_windows", GroupWin = "keymap_group_win", GroupSystem = "keymap_group_system", GroupBrowser = "keymap_group_browser";

        public static readonly IReadOnlyList<KeyDef> All = new[]
        {
            new KeyDef("altTab", "keymap_combo_altTab", "keymap_desc_altTab", GroupWindows),
            new KeyDef("altEsc", "keymap_combo_altEsc", "keymap_desc_altEsc", GroupWindows),
            new KeyDef("altF4", "keymap_combo_altF4", "keymap_desc_altF4", GroupWindows),
            new KeyDef("ctrlEsc", "keymap_combo_ctrlEsc", "keymap_desc_ctrlEsc", GroupWindows),
            new KeyDef("win", "keymap_combo_win", "keymap_desc_win", GroupWin),
            new KeyDef("winR", "keymap_combo_winR", "keymap_desc_winR", GroupWin),
            new KeyDef("winE", "keymap_combo_winE", "keymap_desc_winE", GroupWin),
            new KeyDef("winD", "keymap_combo_winD", "keymap_desc_winD", GroupWin),
            new KeyDef("winV", "keymap_combo_winV", "keymap_desc_winV", GroupWin),
            new KeyDef("winDot", "keymap_combo_winDot", "keymap_desc_winDot", GroupWin),
            new KeyDef("winTab", "keymap_combo_winTab", "keymap_desc_winTab", GroupWin),
            new KeyDef("winX", "keymap_combo_winX", "keymap_desc_winX", GroupWin),
            new KeyDef("winI", "keymap_combo_winI", "keymap_desc_winI", GroupWin),
            new KeyDef("winShiftS", "keymap_combo_winShiftS", "keymap_desc_winShiftS", GroupWin),
            // Windows keeps Ctrl+Alt+Del for itself before any program sees it: for the remote PC there is the button in the session bar
            new KeyDef("cad", "keymap_combo_cad", "keymap_desc_cad", GroupSystem, Fixed: true),
            new KeyDef("ctrlShiftEsc", "keymap_combo_ctrlShiftEsc", "keymap_desc_ctrlShiftEsc", GroupSystem),
            new KeyDef("winL", "keymap_combo_winL", "keymap_desc_winL", GroupSystem),
            new KeyDef("prtSc", "keymap_combo_prtSc", "keymap_desc_prtSc", GroupSystem),
            new KeyDef("ctrlW", "keymap_combo_ctrlW", "keymap_desc_ctrlW", GroupBrowser),
            new KeyDef("ctrlD", "keymap_combo_ctrlD", "keymap_desc_ctrlD", GroupBrowser),
            new KeyDef("ctrlL", "keymap_combo_ctrlL", "keymap_desc_ctrlL", GroupBrowser),
            new KeyDef("ctrlTab", "keymap_combo_ctrlTab", "keymap_desc_ctrlTab", GroupBrowser),
        };

        private static readonly HashSet<string> Ids = All.Select(k => k.Id).ToHashSet();

        public sealed class RdpScreen
        {
            public ERdpFullScreenFlag Full = ERdpFullScreenFlag.EnableFullScreen;
            public bool FsBar = true;
            public ERdpWindowResizeMode Res = ERdpWindowResizeMode.AutoResize;
            public bool SysScale = true;
            public int Scale = 100;
            public bool MonFit;
        }

        public sealed class VncScreen
        {
            public VncFitMode Fit = VncFitMode.Width;
            public int Zoom = 100;
        }

        public sealed class ProtocolDefaults
        {
            /// <summary>only the keys that differ from the factory value (on = to the remote PC); missing = on</summary>
            public Dictionary<string, bool> Keys = new();
        }

        public sealed class Data
        {
            public ProtocolDefaults Rdp = new();
            public ProtocolDefaults Vnc = new();
            public RdpScreen RdpScreen = new();
            public VncScreen VncScreen = new();
        }

        private const string StoreKey = "keymap";
        private static Data? _data;

        /// <summary>for tests: replaces the stored data (null = read again from the database)</summary>
        internal static Func<Data?>? TestData;

        /// <summary>defaults in use; factory values while the database is not ready (retried at the next call)</summary>
        public static Data Current
        {
            get
            {
                if (TestData != null) return TestData() ?? new Data();
                if (_data != null) return _data;
                if (!AppDbStore.TryGet(StoreKey, out var json)) return new Data();   // database not ready yet: nothing written
                Data? d = null;
                if (!string.IsNullOrEmpty(json))
                {
                    try { d = JsonConvert.DeserializeObject<Data>(json); }
                    catch (Exception e) { SimpleLogHelper.Warning("Key mapping: data not readable, using factory values (" + e.Message + ")"); }
                }
                if (d == null)
                {
                    d = new Data();
                    // the old "Alt+Tab to VNC" switch of the General page becomes the VNC default of Alt+Tab
                    if (IoC.TryGet<ConfigurationService>()?.General.SendAltTabToVnc == false) d.Vnc.Keys["altTab"] = false;
                }
                _data = d;
                return d;
            }
        }

        public static bool Save()
        {
            if (TestData != null) return true;
            var ok = AppDbStore.Set(StoreKey, JsonConvert.SerializeObject(Current));
            if (!ok) SimpleLogHelper.Warning("Key mapping: save failed (database not ready)");
            return ok;
        }

        public static ProtocolDefaults DefaultsFor(bool vnc) => vnc ? Current.Vnc : Current.Rdp;

        /// <summary>protocol default of a key: true = to the remote PC</summary>
        public static bool Default(bool vnc, string id) => !DefaultsFor(vnc).Keys.TryGetValue(id, out var v) || v;

        public static void SetDefault(bool vnc, string id, bool toRemote)
        {
            var keys = DefaultsFor(vnc).Keys;
            if (toRemote) keys.Remove(id); else keys[id] = false;
            Save();
        }

        // ---- overrides of one connection: "win=0;altTab=1"

        public static Dictionary<string, bool> ParseOverrides(string? text)
        {
            var d = new Dictionary<string, bool>();
            foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && Ids.Contains(kv[0].Trim()) && kv[1].Trim() is "0" or "1") d[kv[0].Trim()] = kv[1].Trim() == "1";
            }
            return d;
        }

        public static string FormatOverrides(IReadOnlyDictionary<string, bool> d) =>
            string.Join(";", All.Where(k => d.ContainsKey(k.Id)).Select(k => $"{k.Id}={(d[k.Id] ? 1 : 0)}"));

        public static string? OverridesOf(ProtocolBase? s) => s switch { RDP r => r.KeyOverrides, VNC v => v.KeyOverrides, _ => null };

        public static void SetOverridesOf(ProtocolBase s, string text)
        {
            if (s is RDP r) r.KeyOverrides = text;
            else if (s is VNC v) v.KeyOverrides = text;
        }

        /// <summary>effective value in a connection: its override if there is one, otherwise the default of its protocol</summary>
        public static bool ToRemote(ProtocolBase? server, string id)
        {
            if (All.FirstOrDefault(k => k.Id == id) is { Fixed: true }) return false;
            if (ParseOverrides(OverridesOf(server)).TryGetValue(id, out var v)) return v;
            return Default(server is VNC, id);
        }

        /// <summary>a new connection starts with the screen settings of the page (RDP); keys need nothing: no override = defaults</summary>
        public static void ApplyNewConnectionDefaults(ProtocolBase p)
        {
            if (p is not RDP rdp) return;
            var s = Current.RdpScreen;
            rdp.RdpFullScreenFlag = s.Full;   // its setter also adjusts the resize mode: the resize mode is set after
            rdp.IsFullScreenWithConnectionBar = s.FsBar;
            rdp.RdpWindowResizeMode = s.Res;
            rdp.IsScaleFactorFollowSystem = s.SysScale;
            rdp.ScaleFactorCustomValue = (uint)Math.Clamp(s.Scale, 100, 300);
            rdp.RdpFullScreenFollowMonitors = s.MonFit;
        }

        // ---- which combination a key press is (used by the keyboard hook)

        private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_OEM_PERIOD = 0xBE;

        /// <summary>combination of Windows + key (Shift held or not); unknown keys follow "win"</summary>
        public static string WinCombo(int vk, bool shift) => vk switch
        {
            'R' => "winR", 'E' => "winE", 'D' => "winD", 'V' => "winV", VK_OEM_PERIOD => "winDot", VK_TAB => "winTab",
            'X' => "winX", 'I' => "winI", 'L' => "winL", 'S' when shift => "winShiftS",
            _ => "win",
        };

        /// <summary>combination with Alt/Ctrl that this key press completes (null = none of the page)</summary>
        public static string? ModCombo(int vk, bool ctrl, bool alt, bool shift) => vk switch
        {
            VK_TAB when alt && !ctrl => "altTab",
            VK_ESCAPE when alt && !ctrl => "altEsc",
            VK_F4 when alt && !ctrl => "altF4",
            VK_ESCAPE when ctrl && shift && !alt => "ctrlShiftEsc",
            VK_ESCAPE when ctrl && !alt => "ctrlEsc",
            'W' when ctrl && !alt && !shift => "ctrlW",
            'D' when ctrl && !alt && !shift => "ctrlD",
            'L' when ctrl && !alt && !shift => "ctrlL",
            VK_TAB when ctrl && !alt => "ctrlTab",
            _ => null,
        };

        /// <summary>combinations that Windows takes for itself if they reach it (the others go to the window with the keyboard, i.e. the session)</summary>
        public static bool IsSystemCombo(string id) => id is "altTab" or "altEsc" or "ctrlEsc" or "ctrlShiftEsc";
    }
}
