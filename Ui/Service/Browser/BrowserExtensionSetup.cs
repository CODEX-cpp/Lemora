using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Service.Locality;
using Lemora.Utils;
using Microsoft.Win32;
using Shawn.Utils;

namespace Lemora.Service.Browser
{
    /// <summary>
    /// Installs the extension in the browser. Only people who have Lemora get the extension: for Firefox the file signed by Mozilla
    /// (published "unlisted", so it cannot be found by searching addons.mozilla.org) is embedded in Lemora.exe and opened in Firefox,
    /// which asks the user "Add Lemora?". Chrome and Edge: after the "unlisted" publication in their stores (ids not yet assigned).
    /// </summary>
    public static class BrowserExtensionSetup
    {
        private const string Resource = "Lemora.Browser.lemora-firefox.xpi";
        /// <summary>offer after installation already made (only once: if the user declines it is installed from settings)</summary>
        private const string OfferedKey = "browser.ext.offered";

        /// <summary>the extension works only if Lemora could register itself in the browser (installed copy, not portable)</summary>
        public static bool HostRegistered
        {
            get
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Mozilla\NativeMessagingHosts\" + BrowserProtocol.HostName); return k?.GetValue("") is string p && File.Exists(p); }
                catch { return false; }
            }
        }

        public enum Kind { None, Firefox, Chrome, Edge, Brave, Other }

        /// <summary>Windows default browser (the one that opens https links), from the ProgId chosen by the user</summary>
        public static Kind DefaultBrowser()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
                var id = (k?.GetValue("ProgId") as string ?? "").ToLowerInvariant();
                if (id == "") return Kind.None;
                if (id.StartsWith("firefox")) return Kind.Firefox;
                if (id.StartsWith("chromehtml")) return Kind.Chrome;
                if (id.StartsWith("msedgehtm")) return Kind.Edge;
                if (id.StartsWith("bravehtml")) return Kind.Brave;
                return Kind.Other;
            }
            catch { return Kind.None; }
        }

        public static string Name(Kind k) => k switch { Kind.Firefox => "Firefox", Kind.Chrome => "Chrome", Kind.Edge => "Edge", Kind.Brave => "Brave", _ => IoC.Translate("browser_default_name") };

        /// <summary>
        /// installs in the default browser. Returns the message to show if it cannot (null = Firefox opened with the "Add?" prompt).
        /// Chrome, Edge and Brave: the extension comes with the "unlisted" publication in their stores (ids not yet assigned)
        /// </summary>
        public static string? InstallInDefault()
        {
            var k = DefaultBrowser();
            return k switch
            {
                Kind.Firefox => InstallFirefox(),
                Kind.Chrome or Kind.Edge or Kind.Brave => string.Format(IoC.Translate("browser_ext_not_yet"), Name(k)),
                _ => IoC.Translate("browser_ext_unsupported"),
            };
        }

        public static bool DefaultHasExtension() => DefaultBrowser() == Kind.Firefox && FirefoxHasExtension();

        /// <summary>firefox.exe from the Windows registry ("App Paths"), null if Firefox is not installed</summary>
        public static string? FirefoxExe()
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
                    if (k?.GetValue("") is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
                catch { /* key not readable: try the other one */ }
            }
            return null;
        }

        /// <summary>true if at least one Firefox profile has the extension (Firefox keeps the file as &lt;id&gt;.xpi in the profile's extensions folder)</summary>
        public static bool FirefoxHasExtension()
        {
            try
            {
                var profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
                return Directory.Exists(profiles) && Directory.EnumerateDirectories(profiles)
                    .Any(p => File.Exists(Path.Combine(p, "extensions", BrowserProtocol.FirefoxExtensionId + ".xpi")));
            }
            catch { return false; }
        }

        /// <summary>opens the extension file in Firefox: Firefox asks the user to add it. Returns the error to show, null if launched</summary>
        public static string? InstallFirefox()
        {
            var exe = FirefoxExe();
            if (exe == null) return IoC.Translate("browser_firefox_missing");
            try
            {
                // temporary file with a unique name: Firefox copies it into the profile when the user confirms; Firefox itself checks Mozilla's signature
                var dir = Path.Combine(Path.GetTempPath(), "lemora-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var xpi = Path.Combine(dir, "lemora-firefox.xpi");
                using (var s = typeof(BrowserExtensionSetup).Assembly.GetManifestResourceStream(Resource))
                {
                    if (s == null) return IoC.Translate("browser_ext_file_missing");
                    using var f = File.Create(xpi);
                    s.CopyTo(f);
                }
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { xpi }, UseShellExecute = false });
                return null;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("Browser extension: Firefox not opened: " + e.Message);
                return string.Format(IoC.Translate("browser_firefox_not_opened"), e.Message);
            }
        }

        private static string? _bundled;
        /// <summary>version of the signed Firefox extension embedded in Lemora.exe (read from its manifest.json), null if missing</summary>
        public static string? BundledVersion()
        {
            if (_bundled != null) return _bundled.Length == 0 ? null : _bundled;
            try
            {
                using var s = typeof(BrowserExtensionSetup).Assembly.GetManifestResourceStream(Resource);
                using var zip = s == null ? null : new System.IO.Compression.ZipArchive(s);
                using var m = zip?.GetEntry("manifest.json")?.Open();
                _bundled = m == null ? "" : Newtonsoft.Json.Linq.JObject.Parse(new StreamReader(m).ReadToEnd()).Value<string>("version") ?? "";
            }
            catch { _bundled = ""; }
            return _bundled.Length == 0 ? null : _bundled;
        }

        /// <summary>true if the installed extension is older than the embedded one. Missing version = older (before 1.0.7 the extension did not send it)</summary>
        public static bool IsOlder(string? installed, string? bundled) =>
            Version.TryParse(bundled, out var b) && (!Version.TryParse(installed, out var a) || a < b);

        private const string LaterKey = "browser.ext.update_later";
        private static int _asked;

        /// <summary>
        /// Firefox extension older than the one inside Lemora (it does not update by itself before 1.0.4): Lemora asks whether to install the new one.
        /// At most once per run, and "Later" is remembered for 7 days
        /// </summary>
        public static void CheckOutdated(string? installed)
        {
            var bundled = BundledVersion();
            if (!IsOlder(installed, bundled) || Interlocked.Exchange(ref _asked, 1) == 1) return;
            if (AppDbStore.TryGet(LaterKey, out var later) && DateTime.TryParse(later, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until) && DateTime.UtcNow < until) return;
            Task.Run(() =>
            {
                bool ok = false;
                Stylet.Execute.OnUIThreadSync(() =>
                {
                    IoC.TryGet<Lemora.View.MainWindowViewModel>()?.ShowMe();
                    ok = MessageBoxHelper.Confirm(string.Format(IoC.Translate("browser_ext_old_text").Replace("\\n", "\n"), string.IsNullOrEmpty(installed) ? "?" : installed, bundled),
                        IoC.Translate("browser_ext_old_yes"), IoC.Translate("browser_ext_old_no"), IoC.Translate("browser_ext_old_title"));
                });
                if (!ok) { AppDbStore.Set(LaterKey, DateTime.UtcNow.AddDays(7).ToString("o")); return; }
                if (InstallFirefox() is { } err) ToastService.Show(err, ToastKind.Warning);
            });
        }

        /// <summary>
        /// after installation with the "Install the extension in the browser" checkbox (value BrowserExtension=1 in HKLM\SOFTWARE\Lemora written by SetupEngine):
        /// on first start, already as a normal user, it is offered in Firefox. Only once (database); if the database is not open yet it retries on the next run
        /// </summary>
        public static void OfferAfterSetup()
        {
            Task.Run(async () =>
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Lemora")) if (k?.GetValue("BrowserExtension") is not int on || on != 1) return;
                    // main window ready and vault unlocked (otherwise "already offered" is not saved and it would come back): wait up to 10 minutes
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    for (int i = 0; i < 60 && !DataVault.IsUnlocked; i++) await Task.Delay(TimeSpan.FromSeconds(10));
                    if (!DataVault.IsUnlocked) return;
                    if (!AppDbStore.TryGet(OfferedKey, out var done) || !string.IsNullOrEmpty(done)) return;
                    // only in the default browser and only where the extension already exists (Firefox today): the others will be retried once they are in the stores
                    if (!HostRegistered || DefaultBrowser() != Kind.Firefox || FirefoxExe() == null) return;
                    if (!FirefoxHasExtension() && InstallFirefox() is { } err) SimpleLogHelper.Warning("Browser extension: " + err);
                    AppDbStore.Set(OfferedKey, "1");
                }
                catch (Exception e) { SimpleLogHelper.Warning("Browser extension: offer after installation failed: " + e.Message); }
            });
        }
    }
}
