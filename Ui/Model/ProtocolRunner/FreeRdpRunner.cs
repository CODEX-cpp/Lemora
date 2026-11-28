using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Lemora.Model.Protocol;
using Lemora.Service;
using Lemora.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.Model.ProtocolRunner
{
    /// <summary>
    /// RDP opened with FreeRDP (sdl-freerdp.exe) instead of the Microsoft component. The Microsoft component is not touched:
    /// this RDP client is used only if chosen in the settings (or for a single server).
    /// The server's RDP settings are translated into the FreeRDP parameters below (BuildArguments).
    /// </summary>
    public class FreeRdpRunner : ExternalRunner
    {
        public const string RunnerName = "FreeRDP";

        /// <summary>environment variable of the FreeRDP process only, with all the parameters: so the password does not appear on the command line</summary>
        public const string ArgsEnvName = "LEMORA_FREERDP_ARGS";

        [JsonConstructor]
        public FreeRdpRunner(string ownerProtocolName) : base(RunnerName, ownerProtocolName)
        {
            _exePath = DefaultExePath;
            _runWithHosting = true;
        }

        /// <summary>FreeRDP next to Lemora.exe (FreeRDP folder): where the installer puts it</summary>
        public static string DefaultExePath => Path.Combine(AppContext.BaseDirectory, "FreeRDP", "sdl-freerdp.exe");

        /// <summary>
        /// program path: the one chosen in the settings, otherwise next to Lemora.exe or in the data folder.
        /// If found elsewhere it updates the path (so the settings also stop flagging it in red).
        /// </summary>
        public bool TryResolveExe(out string fullName)
        {
            var bundleOk = ExtractBundled(out var extracted);
            var dataDir = Path.GetFullPath(Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, "FreeRDP")) + Path.DirectorySeparatorChar;
            foreach (var candidate in new[] { ExePath, DefaultExePath, extracted })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                // in the data folder only the copy just checked and write-locked is used (identical to the one inside Lemora.exe):
                // an sdl-freerdp*.exe put there by others, even if chosen in the settings, does not get the password (security hardening)
                var full = Path.GetFullPath(candidate);
                if (full.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase) && !(bundleOk && string.Equals(full, extracted, StringComparison.OrdinalIgnoreCase))) continue;
                fullName = Path.GetFullPath(candidate);
                if (!string.Equals(fullName, ExePath, StringComparison.OrdinalIgnoreCase)) ExePath = fullName;
                return true;
            }
            fullName = ExePath;
            return false;
        }

        public const string LicenseFileName = "LICENZE-FREERDP.txt";

        /// <summary>writes (or updates) the FreeRDP license file in the given folder; false if it cannot (folder not writable)</summary>
        public static bool WriteLicense(string dir)
        {
            try
            {
                using var res = typeof(FreeRdpRunner).Assembly.GetManifestResourceStream("Lemora.FreeRDP." + LicenseFileName);
                if (res == null) return false;
                using var ms = new MemoryStream();
                res.CopyTo(ms);
                var path = Path.Combine(dir, LicenseFileName);
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(ms.ToArray())) return true;
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, ms.ToArray());
                return true;
            }
            catch (Exception e) { SimpleLogHelper.Warning($"FreeRDP: licenses not written in {dir} ({e.GetType().Name})"); return false; }
        }

        private static readonly object ExtractLock = new();
        private static FileStream? _pinned;   // extracted and checked copy, opened without allowing writes or deletes while Lemora is open

        private static bool SameBytes(string path, byte[] hash, long length)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return fs.Length == length && System.Security.Cryptography.SHA256.HashData(fs).AsSpan().SequenceEqual(hash);
        }

        /// <summary>
        /// single exe (installer, updates): sdl-freerdp.exe is inside Lemora.exe as a resource and is written to the data folder
        /// (Program Files is not writable). It is rewritten if missing or different.
        /// Security: the data folder may be writable by others (portable mode, shared folder) and FreeRDP receives the password.
        /// So the file hash is checked before use and the file is then kept open read-only (nobody can replace it
        /// while Lemora is open). false = the copy inside Lemora.exe exists but the file on disk differs and could not be rewritten.
        /// </summary>
        private static bool ExtractBundled(out string dst)
        {
            dst = "";
            lock (ExtractLock)
            {
                try
                {
                    if (_pinned != null) { dst = _pinned.Name; return true; }   // already checked and locked
                    using var res = typeof(FreeRdpRunner).Assembly.GetManifestResourceStream("Lemora.FreeRDP.sdl-freerdp.exe");
                    if (res == null) return true;   // folder build: FreeRDP is already next to Lemora.exe (program folder)
                    using var ms = new MemoryStream();
                    res.CopyTo(ms);
                    var bytes = ms.ToArray();
                    var hash = System.Security.Cryptography.SHA256.HashData(bytes);
                    // one file per version (hash in the name): two different Lemora builds on the same data folder do not block each other
                    dst = Path.GetFullPath(Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, "FreeRDP", $"sdl-freerdp-{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}.exe"));
                    // the licenses always go with the extracted program (required by Apache 2.0 and similar licenses)
                    WriteLicense(Path.GetDirectoryName(dst)!);
                    if (!(File.Exists(dst) && SameBytes(dst, hash, bytes.Length)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        var tmp = dst + ".new";
                        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);   // a folder with that name blocked the rewrite
                        File.WriteAllBytes(tmp, bytes);
                        File.Move(tmp, dst, overwrite: true);
                        SimpleLogHelper.Warning($"FreeRDP extracted from Lemora.exe to {dst} ({bytes.Length / 1024} KB)");
                    }
                    // lock before the final check: from here on the file no longer changes (FileShare.Read: no writes, renames or deletes)
                    var pin = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (pin.Length != bytes.Length || !System.Security.Cryptography.SHA256.HashData(pin).AsSpan().SequenceEqual(hash))
                    {
                        pin.Dispose();
                        SimpleLogHelper.Error($"FreeRDP: {dst} differs from the one in Lemora.exe and cannot be rewritten: not used");
                        return false;
                    }
                    _pinned?.Dispose();
                    _pinned = pin;
                    return true;
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Warning($"FreeRDP: extraction or check failed ({e.GetType().Name}: {e.Message})");
                    return false;
                }
            }
        }

        /// <summary>
        /// all the FreeRDP 3 parameters for this server, one per item (FreeRDP reads them from ArgsEnvName with /args-from:env:, one line per parameter).
        /// The password is in plaintext: rdp must already be decrypted (DecryptToConnectLevel).
        /// </summary>
        public static List<string> BuildArguments(RDP rdp, bool fullScreen, string extra)
        {
            var a = new List<string>
            {
                // an IPv6 address goes in brackets: /v:::1:3389 failed with error -1002 and left a dead tab (external test)
                $"/v:{(System.Net.IPAddress.TryParse(rdp.Address?.Trim().TrimStart('[').TrimEnd(']'), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{rdp.Address!.Trim().TrimStart('[').TrimEnd(']')}]" : rdp.Address)}:{rdp.GetPort()}",
                $"/t:{rdp.DisplayName}",
                // placeholder: at the real start it is replaced by the certificate fingerprint checked by RdpCertPin (BuildEnvValue)
                "/cert:ignore",
                "+auto-reconnect", "/auto-reconnect-max-retries:20",
                // wait for session activation: FreeRDP by default waits 9 s (TcpAckTimeout); some servers with a domain user
                // time out at 9 s (cause still unknown), so 15 s is used; /timeout also applies to the TCP connection (15 s by default: unchanged)
                "/timeout:15000",
                // no FreeRDP windows (progress, errors): loading and messages are Lemora's
                "-sdl-connection-dialog",
            };
            // inside the tab: without FreeRDP's title bar and borders
            if (!fullScreen) a.Add("-decorations");
            if (!string.IsNullOrEmpty(rdp.UserName)) a.Add($"/u:{rdp.UserName}");
            // a user written as DOMAIN\user or user@domain wins over the Domain field, like mstsc: with /d FreeRDP would send
            // "DOMAIN\user" as the user name and the server rejects it (password "wrong")
            if (!string.IsNullOrEmpty(rdp.Domain) && !HasDomainInUser(rdp.UserName)) a.Add($"/d:{rdp.Domain}");
            // "/p" without a value: FreeRDP does not open its own password prompt (Lemora asks for the credentials)
            a.Add(string.IsNullOrEmpty(rdp.Password) ? "/p" : $"/p:{rdp.Password}");
            if (rdp.IsAdministrativePurposes == true) a.Add("+admin");
            if (!string.IsNullOrEmpty(rdp.LoadBalanceInfo)) a.Add($"/load-balance-info:{rdp.LoadBalanceInfo}");

            // screen
            if (fullScreen)
            {
                a.Add("/f");
                // shortcut: FreeRDP (SDL) monitor numbers are not the Windows/Lemora ones ("/monitors:0" rejected in testing),
                // so the choice of specific monitors is not passed: with several monitors chosen or "all monitors" all are used
                var monitors = (rdp.RdpFullScreenMonitors ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (monitors.Length > 1 || rdp.RdpFullScreenFlag == ERdpFullScreenFlag.EnableFullAllScreens) a.Add("/multimon");
            }
            switch (rdp.RdpWindowResizeMode)
            {
                case ERdpWindowResizeMode.Fixed:
                case ERdpWindowResizeMode.FixedFullScreen:
                    if (!fullScreen) a.Add($"/size:{(rdp.RdpWidth > 0 ? rdp.RdpWidth : 800)}x{(rdp.RdpHeight > 0 ? rdp.RdpHeight : 600)}");
                    break;
                case ERdpWindowResizeMode.Stretch:
                case ERdpWindowResizeMode.StretchFullScreen:
                    if (!fullScreen) a.Add($"/size:{(rdp.RdpWidth > 0 ? rdp.RdpWidth : 800)}x{(rdp.RdpHeight > 0 ? rdp.RdpHeight : 600)}");
                    a.Add("/smart-sizing");
                    break;
                default: // automatic resize: the remote desktop follows the tab size
                    a.Add("+dynamic-resolution");
                    break;
            }
            var scale = rdp.IsScaleFactorFollowSystem != false
                ? (uint)Math.Round(new ScreenInfoEx(ScreenInfoEx.Primary).ScaleFactor * 100)
                : rdp.ScaleFactorCustomValue ?? 100;
            if (scale > 100) a.Add($"/scale-desktop:{Math.Min(scale, 500)}");

            // performance: same levels as the .rdp file (ToRdpConfig)
            switch (rdp.DisplayPerformance)
            {
                case EDisplayPerformance.Low:
                    a.AddRange(new[] { "/bpp:8", "/network:modem", "-wallpaper", "-themes", "-menu-anims", "-window-drag", "-fonts", "-aero" });
                    break;
                case EDisplayPerformance.Middle:
                    a.AddRange(new[] { "/bpp:16", "/network:broadband-low", "-wallpaper", "-themes", "-menu-anims", "-window-drag", "+fonts", "+aero" });
                    break;
                case EDisplayPerformance.High:
                    a.AddRange(new[] { "/bpp:32", "/network:lan", "+wallpaper", "+themes", "+menu-anims", "+window-drag", "+fonts", "+aero" });
                    break;
                default:
                    a.Add("/network:auto");
                    break;
            }

            // local resources
            a.Add(rdp.EnableClipboard == true ? "+clipboard" : "-clipboard");
            if (rdp.EnableDiskDrives == true) a.Add("+drives");
            if (rdp.EnableRedirectDrivesPlugIn == true) a.Add("/drive:hotplug,*");
            if (rdp.EnablePrinters == true) a.Add("/printer");
            if (rdp.EnableSmartCardsAndWinHello == true) a.Add("/smartcard");
            if (rdp.EnableAudioCapture == true) a.Add("/microphone");
            // key combinations (Alt+Tab, Win...): never FreeRDP's own grab (all or nothing). Lemora's keyboard hook decides key by key
            // with the "Key mapping" rules (VncKeyForwarder)
            a.Add("-grab-keyboard");
            // keyboard layout of this PC, as mstsc sends it: by itself FreeRDP on Windows reads it from the LANG variable (normally missing)
            // and falls back to the US layout, so the server turned Italian keys into other symbols ("-" typed as another character)
            if (KeyboardLayoutArg() is { } kbd) a.Add(kbd);
            switch (rdp.AudioRedirectionMode)
            {
                case EAudioRedirectionMode.RedirectToLocal:
                    a.Add("/audio-mode:0");
                    a.Add("/sound:quality:" + (rdp.AudioQualityMode switch { EAudioQualityMode.Medium => "medium", EAudioQualityMode.High => "high", _ => "dynamic" }));
                    break;
                case EAudioRedirectionMode.LeaveOnRemote: a.Add("/audio-mode:1"); break;
                default: a.Add("/audio-mode:2"); break;
            }

            // gateway: FreeRDP has no automatic detection, it is used only with the data written in the server
            if (rdp.GatewayMode == EGatewayMode.UseTheseGatewayServerSettings && !string.IsNullOrWhiteSpace(rdp.GatewayHostName))
            {
                var g = $"/gateway:g:{rdp.GatewayHostName},usage-method:{(rdp.GatewayBypassForLocalAddress == true ? "detect" : "direct")}";
                if (!string.IsNullOrEmpty(rdp.GatewayUserName)) g += $",u:{rdp.GatewayUserName}";
                var gp = DataVault.DecryptOrReturnOriginalString(rdp.GatewayPassword);
                if (!string.IsNullOrEmpty(gp)) g += $",p:{gp}";
                a.Add(g);
            }

            // parameters added by hand in the RDP client settings, one per line
            foreach (var line in (extra ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(line)) a.Add(line.Trim());
            return a;
        }

        /// <summary>value of ArgsEnvName for this server (password decrypted only here, on a copy); the log gets the parameters without user and password</summary>
        /// <param name="certArgs">certificate parameters decided by RdpCertPin (PC fingerprint), in place of /cert:ignore</param>
        /// <param name="multimon">
        /// several monitors in full screen: FreeRDP gets the layout of all the monitors (one borderless window per monitor, which the host
        /// places inside the session area at the same offsets) instead of a single wide screen. The size is the one of the monitors, so
        /// no /size and no +dynamic-resolution
        /// </param>
        public string BuildEnvValue(RDP rdp, System.Drawing.Size? initialSize = null, IReadOnlyList<string>? certArgs = null, bool multimon = false)
        {
            var r = (RDP)rdp.Clone();
            r.DecryptToConnectLevel();
            // always inside a Lemora window (full screen too): never /f, the tab decides the size
            var args = BuildArguments(r, false, Arguments);
            if (certArgs != null && args.IndexOf("/cert:ignore") is var ci and >= 0) { args.RemoveAt(ci); args.InsertRange(ci, certArgs); }
            if (multimon)
            {
                // "force": FreeRDP uses several monitors only in /f, here the window is inside Lemora's full screen window
                args.Remove("+dynamic-resolution");
                args.Add("/multimon:force");
            }
            // automatic resize: start already with the size of the session area (then +dynamic-resolution follows it)
            else if (initialSize is { Width: >= 200, Height: >= 150 } s && args.Contains("+dynamic-resolution"))
                args.Insert(args.IndexOf("+dynamic-resolution"), $"/size:{s.Width}x{s.Height}");
            // FreeRDP reads one parameter per line and the variable ends up in the environment block: a newline or a NUL in address, name,
            // user, domain or password (from import, .mora, Teams) would add parameters (/drive:...) or variables (OPENSSL_CONF). Security hardening.
            args = args.Select(StripControl).ToList();
            Shawn.Utils.SimpleLogHelper.Info("FreeRDP: " + string.Join(" ", args.Select(x => x.StartsWith("/p:") || x.StartsWith("/u:") || x.StartsWith("/gateway:") ? x.Substring(0, x.IndexOf(':') + 1) + "***" : x)));
            return string.Join("\n", args);
        }

        internal static bool HasDomainInUser(string? user) => !string.IsNullOrEmpty(user) && (user.Contains('\\') || user.Contains('@'));

        /// <summary>"/kbd:layout:0x00000410" with the active keyboard layout of this PC (KLID), null if Windows does not give it</summary>
        internal static string? KeyboardLayoutArg()
        {
            var name = new System.Text.StringBuilder(9);
            if (!GetKeyboardLayoutNameW(name)) return null;
            return uint.TryParse(name.ToString(), System.Globalization.NumberStyles.HexNumber, null, out var id) && id != 0 ? $"/kbd:layout:0x{id:X8}" : null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool GetKeyboardLayoutNameW(System.Text.StringBuilder name);

        internal static string StripControl(string s) => s.Any(char.IsControl) ? new string(s.Where(c => !char.IsControl(c)).ToArray()) : s;

        /// <summary>normal end of the session (close, disconnect or sign-out from the remote PC): the tab closes without messages</summary>
        public static bool IsNormalEnd(int exitCode) => exitCode is 0 or 1 or 2 or 11;

        /// <summary>credentials rejected or missing: Lemora asks for them with its own window</summary>
        public static bool IsCredentialFailure(int exitCode) => exitCode is 10 or 132 or 134 or 154 or 159;

        /// <summary>
        /// readable reason from the sdl-freerdp exit code (SDL_EXIT_*, measured in testing: 141 server unreachable, 140 name not found)
        /// </summary>
        public static string DescribeExit(int exitCode) => exitCode switch
        {
            3 => IoC.Translate("freerdp_exit_idle"),
            4 => IoC.Translate("freerdp_exit_logon_timeout"),
            5 => IoC.Translate("freerdp_exit_replaced"),
            7 or 155 => IoC.Translate("freerdp_exit_refused"),
            9 or 144 => IoC.Translate("freerdp_exit_no_permission"),
            10 or 132 or 134 or 154 => IoC.Translate("freerdp_exit_bad_credentials"),
            159 => IoC.Translate("freerdp_exit_missing_credentials"),
            135 => IoC.Translate("freerdp_exit_account_locked"),
            148 or 149 or 152 => IoC.Translate("freerdp_exit_password_expired"),
            151 => IoC.Translate("freerdp_exit_account_disabled"),
            157 => IoC.Translate("freerdp_exit_account_expired"),
            156 or 158 => IoC.Translate("freerdp_exit_account_restricted"),
            >= 16 and <= 26 => IoC.Translate("freerdp_exit_licensing"),
            139 or 140 => IoC.Translate("freerdp_exit_dns"),
            141 or 147 => IoC.Translate("freerdp_exit_unreachable"),
            131 => IoC.Translate("freerdp_exit_conn_failed"),   // CONN_FAILED: error not classified by FreeRDP, it does not mean the PC is off
            143 => IoC.Translate("freerdp_exit_tls"),
            133 => IoC.Translate("freerdp_exit_security"),
            150 => IoC.Translate("freerdp_exit_kerberos"),
            128 => IoC.Translate("freerdp_exit_bad_args"),
            _ => IoC.Translate("freerdp_exit_other", exitCode),
        };

        /// <summary>
        /// RDP settings FreeRDP does not have: shown in the RDP client settings, so nobody thinks they apply
        /// </summary>
        [JsonIgnore]
        public static string NotMappedText => IoC.Translate("freerdp_not_mapped");
    }
}
