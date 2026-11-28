using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Shawn.Utils;

namespace Lemora.Setup
{
    /// <summary>a newer version found on GitHub</summary>
    public record UpdateInfo(Version Version, string ZipUrl, string SigUrl);

    /// <summary>
    /// Automatic update, part 1: looks up the latest release on GitHub, downloads the package and checks its signature.
    /// A release must have the tag "v1.2.3.4" (like the app version) and two files:
    ///   lemora-1.2.3.4-update-package.zip       (the new version: Lemora.exe at the root)
    ///   lemora-1.2.3.4-update-package.zip.sig   (ECDSA P-256 signature in base64, made with Installer\update\sign.ps1)
    /// Nothing is installed here: the verified package stays in a temporary folder until the user chooses "Restart to update".
    /// </summary>
    public static class UpdateService
    {
        public const string Owner = "CODEX-cpp";
        public const string Repo = "Lemora";

        /// <summary>public signing key (ECCPUBLIC blob, base64): public by definition. The private key is kept by the maintainer, never in the repository</summary>
        public const string PublicKeyB64 = "RUNTMSAAAAAa3ie5qw/WRTBnxZJgqBLcf/83vqod1zRZ5KqVtmtMWrfkBqWMi3vPpjJxNHAI1O/hidYC5LAjgf8/hAAYunPa";

        /// <summary>for tests: an address in place of the GitHub API (LEMORA_UPDATE_API) and a test signing key (LEMORA_UPDATE_KEY)</summary>
        // The two variables apply ONLY if the program is NOT running as administrator: the elevated copy that installs the update (SetupEngine.RunUpdateElevated)
        // must always use the real key, otherwise anyone able to set an environment variable could get a package signed by themselves installed.
        private static bool CanOverride => !SetupEngine.IsAdmin();
        private static string ApiUrl => CanOverride && Lemora.Utils.DevOnly.Env("LEMORA_UPDATE_API") is { Length: > 0 } u ? u : $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=50";
        // The whole list, not /releases/latest: the same repo also holds the site and, later, server and extension releases
        // (tags "server-...", "estensione-..."). If one of them were "latest", the app would stop seeing its own updates.
        private static string KeyB64 => CanOverride && Lemora.Utils.DevOnly.Env("LEMORA_UPDATE_KEY") is { Length: > 0 } k ? k : PublicKeyB64;

        /// <summary>version of the running app (the file version, which grows with every build)</summary>
        public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

        public static string TempRoot => Path.Combine(Path.GetTempPath(), "Lemora-update");

        private static HttpClient NewClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Lemora-Updater");   // the GitHub API rejects requests without a User-Agent
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>app tag -> Version: "v1.2.3.4", "1.2.3", "app-1.2.3" or "app-v1.2.3". null if it is not an app version
        /// (e.g. "server-1.0.0", "estensione-1.0.4", "latest")</summary>
        public static Version? ParseTag(string? tag)
        {
            tag = (tag ?? "").Trim();
            if (tag.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(4);
            if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag.Substring(1);
            return Version.TryParse(tag, out var v) ? v : null;
        }

        /// <summary>the newest published app release in the answer: a list of releases (GitHub /releases) or a single one.
        /// Drafts, pre-releases and releases of other parts (server, extension) are skipped.</summary>
        public static (Version Version, JToken Release)? NewestAppRelease(string json)
        {
            var root = JToken.Parse(json);
            var all = root is JArray a ? (System.Collections.Generic.IEnumerable<JToken>)a : new[] { root };
            (Version, JToken)? best = null;
            foreach (var r in all)
            {
                if (r.Type != JTokenType.Object || r.Value<bool?>("draft") == true || r.Value<bool?>("prerelease") == true) continue;
                var v = ParseTag(r["tag_name"]?.ToString());
                if (v != null && (best == null || v > best.Value.Item1)) best = (v, r);
            }
            return best;
        }

        /// <summary>
        /// Latest release on GitHub. null = no update (same or older version, no release, no network: not an error to show).
        /// Throws only if the release is newer but malformed (missing files): that is the publisher's problem, and it goes to the log.
        /// </summary>
        public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            string json;
            try
            {
                using var http = NewClient();
                json = await http.GetStringAsync(ApiUrl, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                SimpleLogHelper.Debug("Update check failed: " + e.Message);
                return null;
            }
            if (NewestAppRelease(json) is not { } found) return null;
            var (remote, o) = found;
            if (remote <= CurrentVersion) return null;
            string zip = $"lemora-{remote}-update-package.zip";
            string? Url(string name) => o["assets"]?.FirstOrDefault(a => a["name"]?.ToString() == name)?["browser_download_url"]?.ToString();
            var zipUrl = Url(zip); var sigUrl = Url(zip + ".sig");
            if (zipUrl == null || sigUrl == null) throw new InvalidDataException($"Release {remote} does not contain {zip} and its signature");
            return new UpdateInfo(remote, zipUrl, sigUrl);
        }

        /// <summary>the signature (64 bytes in base64) is valid for this data with the public key</summary>
        public static bool VerifySignature(byte[] data, string sigB64)
        {
            try
            {
                var blob = Convert.FromBase64String(KeyB64);   // ECS1 (4) + length (4) + X (32) + Y (32)
                if (blob.Length != 72 || BitConverter.ToUInt32(blob, 0) != 0x31534345 || BitConverter.ToUInt32(blob, 4) != 32) return false;
                var p = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = blob[8..40], Y = blob[40..72] } };
                using var ec = ECDsa.Create(p);
                var sig = Convert.FromBase64String(sigB64.Trim());
                return sig.Length == 64 && ec.VerifyData(data, sig, HashAlgorithmName.SHA256);
            }
            catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads the package and the signature and checks them. Returns the path of the verified package (in a temporary folder);
        /// if the signature does not match, the file is deleted and an error is thrown: an unsigned package is never used.
        /// </summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var dir = Path.Combine(TempRoot, info.Version.ToString());
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "package.zip");
            using var http = NewClient();
            var sig = await http.GetStringAsync(info.SigUrl, ct);
            using (var resp = await http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(file);
                var buf = new byte[81920]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (!VerifySignature(await File.ReadAllBytesAsync(file, ct), sig))
            {
                File.Delete(file);
                throw new CryptographicException(IoC.Translate("update_bad_signature"));   // shown in the update error toast
            }
            await File.WriteAllTextAsync(file + ".sig", sig, ct);   // the copy with administrator rights rereads package and signature and re-verifies them on its own
            return file;
        }

        /// <summary>removes the temporary folders of old or already installed updates (called at startup)</summary>
        public static void CleanTemp()
        {
            try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); }
            catch (Exception e) { SimpleLogHelper.Debug("Temporary update cleanup: " + e.Message); }
        }
    }
}
