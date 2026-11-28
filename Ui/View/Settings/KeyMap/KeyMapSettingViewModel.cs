using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Service;
using Lemora.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using KM = Lemora.Service.KeyMap;

namespace Lemora.View.Settings.KeyMap
{
    /// <summary>one key of the page: on = to the remote PC (default of the protocol shown at the top)</summary>
    public class KeyRowViewModel : NotifyPropertyChangedBase
    {
        private readonly KeyMapSettingViewModel _page;
        public KeyDef Def { get; }
        public KeyRowViewModel(KeyMapSettingViewModel page, KeyDef def) { _page = page; Def = def; }

        public string Combo => Def.Combo;
        public string Description => Def.Description;
        public bool CanChange => !Def.Fixed;

        public bool IsOn
        {
            get => !Def.Fixed && KM.Default(_page.IsVnc, Def.Id);
            set
            {
                if (Def.Fixed || value == IsOn) return;
                KM.SetDefault(_page.IsVnc, Def.Id, value);
                Refresh();
            }
        }

        public string StatusText => IoC.Translate(IsOn ? "keys_to_remote" : "keys_on_my_pc");
        public string AccessibleName => string.Format(IoC.Translate("keys_send_to_remote_name"), Def.Combo);

        public void Refresh() { RaisePropertyChanged(nameof(IsOn)); RaisePropertyChanged(nameof(StatusText)); }
    }

    public class KeyGroupViewModel
    {
        public string Title { get; init; } = "";
        public List<KeyRowViewModel> Rows { get; init; } = new();
    }

    public record Choice<T>(T Value, string Label);

    /// <summary>
    /// Settings › Key mapping: RDP | VNC switch at the top, the keys of that protocol and its screen settings.
    /// They are the defaults of the new connections; inside a connection a single key can be changed (KeyOverrides of the connection)
    /// </summary>
    public class KeyMapSettingViewModel : NotifyPropertyChangedBase
    {
        public List<KeyGroupViewModel> Groups { get; }

        public KeyMapSettingViewModel()
        {
            Groups = KM.All.GroupBy(k => k.Group).Select(g => new KeyGroupViewModel
            {
                Title = string.Format(IoC.Translate("keymap_shortcuts_group"), g.Key),
                Rows = g.Select(k => new KeyRowViewModel(this, k)).ToList(),
            }).ToList();
        }

        /// <summary>values read again (the database may not have been ready the first time)</summary>
        public void Reload()
        {
            foreach (var r in Groups.SelectMany(g => g.Rows)) r.Refresh();
            RaiseAll();
        }

        private bool _isVnc;
        public bool IsVnc
        {
            get => _isVnc;
            set
            {
                if (!SetAndNotifyIfChanged(ref _isVnc, value)) return;
                RaisePropertyChanged(nameof(ProtocolIndex));
                Reload();
            }
        }

        /// <summary>0 = RDP, 1 = VNC (selected item of the switch at the top)</summary>
        public int ProtocolIndex
        {
            get => IsVnc ? 1 : 0;
            set => IsVnc = value == 1;
        }

        public string IntroText => string.Format(IoC.Translate("keymap_intro"), IsVnc ? "VNC" : "RDP");

        private void RaiseAll()
        {
            foreach (var n in new[] { nameof(IntroText), nameof(Full), nameof(FsBar), nameof(Res), nameof(SysScale), nameof(Scale), nameof(ScaleText),
                         nameof(MonFit), nameof(FitIndex), nameof(Zoom), nameof(ZoomText), nameof(ZoomDimmed) })
                RaisePropertyChanged(n);
        }

        private static void Save() => KM.Save();

        // ---- RDP screen

        public List<Choice<ERdpFullScreenFlag>> FullChoices { get; } = new()
        {
            new(ERdpFullScreenFlag.EnableFullScreen, IoC.Translate("keymap_full_single")),
            new(ERdpFullScreenFlag.EnableFullAllScreens, IoC.Translate("keymap_full_all")),
            new(ERdpFullScreenFlag.Disable, IoC.Translate("server_editor_display_full_screen_flag_window")),
        };

        public List<Choice<ERdpWindowResizeMode>> ResChoices { get; } = new()
        {
            new(ERdpWindowResizeMode.AutoResize, IoC.Translate("session_display_auto")),
            new(ERdpWindowResizeMode.Stretch, IoC.Translate("keymap_res_custom_stretch")),
            new(ERdpWindowResizeMode.Fixed, IoC.Translate("keymap_res_custom_fixed")),
            new(ERdpWindowResizeMode.StretchFullScreen, IoC.Translate("keymap_res_full_stretch")),
            new(ERdpWindowResizeMode.FixedFullScreen, IoC.Translate("keymap_res_full_fixed")),
        };

        private static KM.RdpScreen Rdp => KM.Current.RdpScreen;
        private static KM.VncScreen Vnc => KM.Current.VncScreen;

        public ERdpFullScreenFlag Full { get => Rdp.Full; set { if (Rdp.Full == value) return; Rdp.Full = value; Save(); RaisePropertyChanged(); } }
        public bool FsBar { get => Rdp.FsBar; set { if (Rdp.FsBar == value) return; Rdp.FsBar = value; Save(); RaisePropertyChanged(); } }
        public ERdpWindowResizeMode Res { get => Rdp.Res; set { if (Rdp.Res == value) return; Rdp.Res = value; Save(); RaisePropertyChanged(); } }
        public bool SysScale { get => Rdp.SysScale; set { if (Rdp.SysScale == value) return; Rdp.SysScale = value; Save(); RaisePropertyChanged(); } }

        /// <summary>100-300%, 25% steps</summary>
        public double Scale
        {
            get => Rdp.Scale;
            set
            {
                var v = (int)Math.Clamp(Math.Round(value / 25) * 25, 100, 300);
                if (Rdp.Scale == v) return;
                Rdp.Scale = v; Save();
                RaisePropertyChanged(); RaisePropertyChanged(nameof(ScaleText));
            }
        }
        public string ScaleText => $"{Rdp.Scale}%";

        public bool MonFit { get => Rdp.MonFit; set { if (Rdp.MonFit == value) return; Rdp.MonFit = value; Save(); RaisePropertyChanged(); } }

        private RelayCommand? _cmdResetScale;
        public RelayCommand CmdResetScale => _cmdResetScale ??= new RelayCommand(_ => Scale = 100);

        // ---- VNC screen: Fit width / Fit height / Fixed zoom, and the zoom (25-350%, 5% steps)

        private static readonly VncFitMode[] FitOrder = { VncFitMode.Width, VncFitMode.Height, VncFitMode.Actual };

        public int FitIndex
        {
            get => Math.Max(0, Array.IndexOf(FitOrder, Vnc.Fit));
            set
            {
                if (value < 0 || value >= FitOrder.Length || Vnc.Fit == FitOrder[value]) return;
                Vnc.Fit = FitOrder[value]; Save();
                RaisePropertyChanged(); RaisePropertyChanged(nameof(ZoomDimmed));
            }
        }

        public double Zoom
        {
            get => Vnc.Zoom;
            set
            {
                var v = (int)Math.Clamp(Math.Round(value / 5) * 5, 25, 350);
                if (Vnc.Zoom == v) return;
                Vnc.Zoom = v;
                // moving the zoom means "Fixed zoom"
                Vnc.Fit = VncFitMode.Actual;
                Save();
                RaisePropertyChanged(); RaisePropertyChanged(nameof(ZoomText)); RaisePropertyChanged(nameof(FitIndex)); RaisePropertyChanged(nameof(ZoomDimmed));
            }
        }
        public string ZoomText => $"{Vnc.Zoom}%";
        /// <summary>the zoom row is dimmed until the fit is "Fixed zoom"</summary>
        public bool ZoomDimmed => Vnc.Fit != VncFitMode.Actual;

        private RelayCommand? _cmdResetZoom;
        public RelayCommand CmdResetZoom => _cmdResetZoom ??= new RelayCommand(_ =>
        {
            Zoom = 100;
            if (Vnc.Fit != VncFitMode.Actual) { Vnc.Fit = VncFitMode.Actual; Save(); RaisePropertyChanged(nameof(FitIndex)); RaisePropertyChanged(nameof(ZoomDimmed)); }
        });
    }
}
