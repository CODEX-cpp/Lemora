using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.Locality;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// The log file must not say who: entry names, addresses, users, domains, folder names and the Teams server address
    /// become codes [#3f2a91] before going to disk (security hardening).
    /// The code is an HMAC with the vault key (always the same for the same data); the code -> data table lives in the encrypted
    /// database and is used by "Export readable log". Before unlocking no entry data is known, so there is nothing to replace.
    /// </summary>
    public static class LogRedactor
    {
        private const string DbKey = "log_codes";
        private const int MinLength = 3;   // shorter words would ruin the log without hiding anything useful
        private static readonly string[] NotNames = { "rdp", "vnc", "ssh", "sftp", "ftp", "telnet", "app", "localhost" };

        private static readonly object Lock = new();
        private static Regex? _re;
        private static DateTime _builtAt = DateTime.MinValue;
        private static int _sourcesHash;
        private static Dictionary<string, string>? _codes;   // code -> data (in-memory copy of the encrypted table)
        private static bool _dirty, _saving;
        [ThreadStatic] private static bool _busy;

        public static void Install() => SimpleLogHelper.Redactor = Redact;

        /// <summary>log text with the sensitive data replaced by the codes</summary>
        public static string Redact(string text)
        {
            if (_busy || !DataVault.IsUnlocked || string.IsNullOrEmpty(text)) return text;   // _busy: lines written while the list is being read (no names)
            _busy = true;
            try
            {
                var re = Current();
                return re == null ? text : re.Replace(text, m => CodeFor(m.Value));
            }
            finally { _busy = false; }
        }

        private static Regex? Current()
        {
            lock (Lock)
            {
                if (_re != null && (DateTime.UtcNow - _builtAt).TotalSeconds < 5) return _re;
                _builtAt = DateTime.UtcNow;
                var tokens = Sources();
                var h = tokens.Aggregate(17, (a, t) => unchecked(a * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(t)));
                if (_re != null && h == _sourcesHash) return _re;
                _sourcesHash = h;
                if (tokens.Count == 0) return _re = null;
                // the whole word: not inside another one (Roma does not touch Roma2, 10.0.0.1 does not touch 10.0.0.10); longest first
                var alt = string.Join("|", tokens.OrderByDescending(t => t.Length).Select(Regex.Escape));
                // '_' before the data is allowed: the session id is '{id}_{address}:{port}' and the address stayed in clear (security hardening)
                return _re = new Regex($@"(?<![\p{{L}}\p{{N}}.\-])(?:{alt})(?![\p{{L}}\p{{N}}_\-]|\.[\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        private static readonly HashSet<string> Extra = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// data of an unsaved session (quick connect, Claude's apri_indirizzo): they are in no entry, but the log writes them.
        /// Call BEFORE writing lines with that data (security hardening). They stay hidden until Lemora closes
        /// </summary>
        public static void Remember(params string?[] values)
        {
            bool added = false;
            lock (Extra) foreach (var v in values) if (!string.IsNullOrWhiteSpace(v) && Extra.Add(v!.Trim())) added = true;
            if (added) lock (Lock) _builtAt = DateTime.MinValue;   // the list is rebuilt at the next line (only if there is something new)
        }

        /// <summary>the data to hide: from entries (local and Teams), folders, folder credentials and the Teams server</summary>
        private static List<string> Sources()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? v)
            {
                v = v?.Trim();
                if (string.IsNullOrEmpty(v) || v.Length < MinLength || NotNames.Contains(v.ToLowerInvariant()) || v.All(char.IsDigit)) return;
                set.Add(v);
                foreach (var part in v.Split('\\', '@')) if (part != v) Add(part);   // DOMAIN\user, user@domain
            }
            try
            {
                var servers = IoC.TryGet<GlobalData>()?.VmItemList.ToArray().Select(x => x.Server) ?? Enumerable.Empty<ProtocolBase>();
                foreach (var s in servers)
                {
                    Add(s.DisplayName);
                    if (s is ProtocolBaseWithAddressPort ap)
                    {
                        Add(ap.Address);
                        // alternate addresses: the log writes them when an alternate is tried or used (security hardening)
                        foreach (var c in ap.AlternateCredentials.ToArray()) { Add(c.Address); Add(c.Name); Add(c.UserName); }
                    }
                    if (s is ProtocolBaseWithAddressPortUserPwd u)
                    {
                        Add(u.UserName);
                        foreach (var e in u.Users.ToArray()) Add(e.UserName);
                    }
                    if (s is Lemora.Model.Protocol.RDP r) Add(r.Domain);
                    foreach (var n in s.TreeNodes ?? new List<string>()) Add(n);
                }
                foreach (var c in FolderCredentialService.Cached) { Add(c.UserName); foreach (var n in FolderService.ToNodes(c.FolderPath)) Add(n); }
                lock (Extra) foreach (var e in Extra) Add(e);
                var teams = Lemora.Setup.TeamsLink.Load().Server;
                if (!string.IsNullOrEmpty(teams)) Add(teams.Split(':')[0]);
            }
            catch { /* list changed while being read: retry on the next round */ _builtAt = DateTime.MinValue; }
            return set.ToList();
        }

        private static string CodeFor(string value)
        {
            var key = DataVault.CurrentKey();
            if (key == null) return value;
            using var hmac = new HMACSHA256(key);
            var code = "#" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("log|" + value.ToLowerInvariant()))).ToLowerInvariant()[..6];
            lock (Lock)
            {
                var codes = Codes();
                if (!codes.ContainsKey(code)) { codes[code] = value; _dirty = true; SaveLater(); }
            }
            return "[" + code + "]";
        }

        private static Dictionary<string, string> Codes()
        {
            if (_codes != null) return _codes;
            _codes = new Dictionary<string, string>();
            try { if (AppDbStore.TryGet(DbKey, out var j) && !string.IsNullOrEmpty(j)) _codes = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(j!) ?? new(); }
            catch { /* unreadable table: start over, the codes stay the same (HMAC) */ }
            return _codes;
        }

        /// <summary>saves the table outside the log write (which holds its lock), once in a while</summary>
        private static void SaveLater()
        {
            if (_saving) return;
            _saving = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                string json;
                lock (Lock) { _saving = false; if (!_dirty || _codes == null) return; _dirty = false; json = Newtonsoft.Json.JsonConvert.SerializeObject(_codes); }
                try { AppDbStore.Set(DbKey, json); } catch { lock (Lock) _dirty = true; }
            });
        }

        /// <summary>
        /// The log with the real data in place of the codes (for a diagnosis). Codes not in the table stay as they are.
        /// Requires the vault to be unlocked.
        /// </summary>
        public static string Decode(string text)
        {
            Dictionary<string, string> codes;
            lock (Lock) codes = new Dictionary<string, string>(Codes());
            return Regex.Replace(text, @"\[(#[0-9a-f]{6})\]", m => codes.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        }

        /// <summary>log files of the last days, decoded, one after the other (oldest first)</summary>
        public static string ReadableLog(string currentLogFile, int days)
        {
            var dir = Path.GetDirectoryName(currentLogFile);
            if (dir == null || !Directory.Exists(dir)) return "";
            var since = DateTime.Now.AddDays(-days);
            var sb = new StringBuilder();
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.md").Where(f => f.LastWriteTime >= since).OrderBy(f => f.LastWriteTime))
            {
                string t;
                try { using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var r = new StreamReader(fs, Encoding.UTF8); t = r.ReadToEnd(); }
                catch { continue; }
                sb.AppendLine("==== " + f.Name).AppendLine(Decode(t));
            }
            return sb.ToString();
        }
    }
}
