using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Lemora.Utils
{
    /// <summary>
    /// Data vault: everything the app encrypts in the database (passwords, keys, whole connections, key/value store)
    /// goes through here. AES-256-GCM with a random key (the "data key") that exists only in memory after unlocking.
    /// In the database the data key is stored only locked: with the master password (PBKDF2-SHA256, works on every PC)
    /// and, for each PC/Windows user, with Windows Hello (key in the TPM, see VaultUnlock). Without one of the two the database cannot be read.
    /// Format of an encrypted value: "lv1:" + base64(nonce 12 | ciphertext | tag 16).
    /// </summary>
    public static class DataVault
    {
        private const string Tag = "lv1:";
        public const string HeaderConfigKey = "lemora.vault";
        private const int Pbkdf2Iterations = 600_000;   // OWASP 2023 recommendation for PBKDF2-HMAC-SHA256

        private static byte[]? _key;
        public static bool IsUnlocked => _key != null;

        /// <summary>
        /// Data key change (master password changed): while the database is re-encrypted no other thread encrypts or decrypts
        /// (read = normal use, write = re-encryption). Recursive: the re-encryption thread can use SimpleDecrypt and the like.
        /// </summary>
        private static readonly System.Threading.ReaderWriterLockSlim KeyLock = new(System.Threading.LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// After the key change, until restart: memory still holds values encrypted with the old key (entries already loaded), which once saved
        /// would become unreadable. Every new encryption is refused (the save fails, nothing gets damaged) until Lemora is reopened.
        /// </summary>
        public static bool Frozen { get; private set; }

        // a decryption costs a few microseconds, but the Home does hundreds at startup: the same text is decrypted only once
        private static readonly ConcurrentDictionary<string, string?> DecryptCache = new();
        public static int DecryptCalls, DecryptCacheHits;
        public static long DecryptTicks;

        public static void SetKey(byte[] key)
        {
            if (key.Length != 32) throw new ArgumentException("the data key must be 32 bytes");
            KeyLock.EnterWriteLock();
            try
            {
                _key = (byte[])key.Clone();
                DecryptCache.Clear();
            }
            finally { KeyLock.ExitWriteLock(); }
        }

        public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

        /// <summary>copy of the current data key (to change the master password), null when the vault is locked</summary>
        public static byte[]? CurrentKey()
        {
            KeyLock.EnterReadLock();
            try { return _key == null ? null : (byte[])_key.Clone(); }
            finally { KeyLock.ExitReadLock(); }
        }

        public static bool IsVaultText(string? s) => s != null && s.StartsWith(Tag, StringComparison.Ordinal);

        /// <summary>
        /// Secret coming from outside (imported file, Teams server): an "lv1:..." is not a password but a ciphertext taken from a database
        /// and it would be decrypted with this PC's key (security hardening). It is dropped: the entry stays without password.
        /// </summary>
        public static string FromOutside(string? s) => IsVaultText(s) ? "" : s ?? "";

        public static string SimpleEncrypt(string txt)
        {
            KeyLock.EnterReadLock();
            try
            {
                if (Frozen) throw new InvalidOperationException("data key just changed: Lemora must be reopened before saving");
                var key = _key ?? throw new InvalidOperationException("vault locked: no data key");
                return Tag + Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(txt)));
            }
            finally { KeyLock.ExitReadLock(); }
        }

        /// <summary>null if the text is not in the vault format or does not decrypt with the current key</summary>
        public static string? SimpleDecrypt(string? encryptString)
        {
            if (!IsVaultText(encryptString)) return null;
            KeyLock.EnterReadLock();
            try { return SimpleDecryptLocked(encryptString); }
            finally { KeyLock.ExitReadLock(); }
        }

        private static string? SimpleDecryptLocked(string? encryptString)
        {
            if (!IsVaultText(encryptString) || _key == null) return null;
            if (DecryptCache.TryGetValue(encryptString!, out var cached))
            {
                System.Threading.Interlocked.Increment(ref DecryptCacheHits);
                return cached;
            }
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            string? plain = null;
            try
            {
                var data = Open(_key, Convert.FromBase64String(encryptString!.Substring(Tag.Length)));
                if (data != null) plain = Encoding.UTF8.GetString(data);
            }
            catch (FormatException) { /* not base64: not ours */ }
            System.Threading.Interlocked.Add(ref DecryptTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            System.Threading.Interlocked.Increment(ref DecryptCalls);
            DecryptCache[encryptString!] = plain;
            return plain;
        }

        public static string EncryptOnce(string str) => IsVaultText(str) && SimpleDecrypt(str) != null ? str : SimpleEncrypt(str);

        // ---------------------------------------------------------------- data key change
        // security hardening: changing the master password now also changes the data key
        // and re-encrypts everything, so an old copy of the database with the old password no longer opens the new data.

        public sealed class RekeyStats
        {
            /// <summary>whole encrypted values re-encrypted (columns)</summary>
            public int Whole;
            /// <summary>encrypted values inside other data (password inside an entry, quick connect history...)</summary>
            public int Inner;
            /// <summary>values already unreadable before (different key, damaged data): they stay as they are</summary>
            public int Unreadable;
        }

        private static readonly System.Text.RegularExpressions.Regex InnerToken = new(@"lv1:[A-Za-z0-9+/]+={0,2}", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string? OpenText(byte[] key, string vaultText)
        {
            try { var d = Open(key, Convert.FromBase64String(vaultText.Substring(Tag.Length))); return d == null ? null : Encoding.UTF8.GetString(d); }
            catch (FormatException) { return null; }
        }

        private static string SealText(byte[] key, string plain) => Tag + Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(plain)));

        /// <summary>
        /// The same data encrypted with the new key: a whole "lv1:..." value, or a text with "lv1:..." values inside (even at several levels).
        /// What does not decrypt with the old key stays as it is (and is counted).
        /// </summary>
        public static string Reencrypt(string value, byte[] oldKey, byte[] newKey, RekeyStats st)
        {
            if (IsVaultText(value) && InnerToken.Match(value) is { Success: true } m && m.Index == 0 && m.Length == value.Length)
            {
                var plain = OpenText(oldKey, value);
                if (plain == null) { st.Unreadable++; return value; }
                st.Whole++;
                return SealText(newKey, ReencryptInner(plain, oldKey, newKey, st));
            }
            return ReencryptInner(value, oldKey, newKey, st);
        }

        private static string ReencryptInner(string text, byte[] oldKey, byte[] newKey, RekeyStats st)
        {
            if (text.IndexOf(Tag, StringComparison.Ordinal) < 0) return text;
            return InnerToken.Replace(text, m =>
            {
                var plain = OpenText(oldKey, m.Value);
                if (plain == null) { st.Unreadable++; return m.Value; }
                st.Inner++;
                return SealText(newKey, ReencryptInner(plain, oldKey, newKey, st));
            });
        }

        /// <summary>how many "lv1:" values (whole and inside other data) decrypt with this key and how many do not: for the check before saving</summary>
        public static (int ok, int bad) CountReadable(string value, byte[] key)
        {
            int ok = 0, bad = 0;
            void Walk(string t)
            {
                if (t.IndexOf(Tag, StringComparison.Ordinal) < 0) return;
                foreach (System.Text.RegularExpressions.Match m in InnerToken.Matches(t))
                {
                    var p = OpenText(key, m.Value);
                    if (p == null) bad++; else { ok++; Walk(p); }
                }
            }
            Walk(value);
            return (ok, bad);
        }

        /// <summary>
        /// Data key change with everything stopped: no other thread encrypts or decrypts until <paramref name="work"/> finishes.
        /// <paramref name="work"/> receives the old and new key and can activate the new one with <see cref="UseKeyDuringChange"/> (for hashes
        /// computed with the current key). If it returns true the new key stays active and the vault "freezes" until restart
        /// (<see cref="Frozen"/>); on false or exception the old one comes back, as if nothing happened. Do not write to the log inside (the log uses the key).
        /// </summary>
        public static bool ChangeKey(byte[] newKey, Func<byte[], byte[], bool> work)
        {
            if (newKey.Length != 32) throw new ArgumentException("the data key must be 32 bytes");
            KeyLock.EnterWriteLock();
            var old = _key;
            var ok = false;
            try
            {
                if (old == null || Frozen) return false;
                ok = work((byte[])old.Clone(), (byte[])newKey.Clone());
                return ok;
            }
            finally
            {
                _key = ok ? (byte[])newKey.Clone() : old;
                if (ok) Frozen = true;
                DecryptCache.Clear();
                KeyLock.ExitWriteLock();
            }
        }

        /// <summary>automated tests only: removes the freeze after a key change (in the app, Lemora is reopened)</summary>
        public static void UnfreezeForTests() { KeyLock.EnterWriteLock(); try { Frozen = false; } finally { KeyLock.ExitWriteLock(); } }

        /// <summary>only inside <see cref="ChangeKey"/>: activates a key (to compute hashes with the normal functions)</summary>
        public static void UseKeyDuringChange(byte[] key)
        {
            if (!KeyLock.IsWriteLockHeld) throw new InvalidOperationException("only during the key change");
            _key = (byte[])key.Clone();
            DecryptCache.Clear();
        }

        public static string DecryptOrReturnOriginalString(string originalString) => SimpleDecrypt(originalString) ?? originalString;

        // ---------------------------------------------------------------- AES-256-GCM

        private static byte[] Seal(byte[] key, byte[] plain)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
            var all = new byte[12 + cipher.Length + 16];
            nonce.CopyTo(all, 0); cipher.CopyTo(all, 12); tag.CopyTo(all, 12 + cipher.Length);
            return all;
        }

        private static byte[]? Open(byte[] key, byte[] all)
        {
            if (all.Length < 28) return null;
            var plain = new byte[all.Length - 28];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(12, plain.Length), all.AsSpan(12 + plain.Length, 16), plain);
                return plain;
            }
            catch (AuthenticationTagMismatchException) { return null; }   // wrong key or tampered data
        }

        // ---------------------------------------------------------------- header: the data key locked with the two locks

        public sealed class Header
        {
            public int Version { get; set; } = 1;
            public string Salt { get; set; } = "";
            public int Iterations { get; set; } = Pbkdf2Iterations;
            /// <summary>data key locked with the key derived from the master password</summary>
            public string ByPassword { get; set; } = "";
            /// <summary>data key locked with Windows Hello, one item per PC and Windows user</summary>
            public List<HelloLock> Hello { get; set; } = new();
        }

        public sealed class HelloLock
        {
            /// <summary>PC\SID of the Windows user: only that user on that PC has the key in the TPM</summary>
            public string Who { get; set; } = "";
            public string Locked { get; set; } = "";
            /// <summary>
            /// random value signed by Hello together with the fixed phrase: changes at every new lock (Hello added, master password changed),
            /// so a lock key seen once does not open the next one (security hardening). Empty = old lock
            /// </summary>
            public string Nonce { get; set; } = "";
        }

        public static Header? ParseHeader(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Header>(json); } catch { return null; }
        }

        public static string HeaderToJson(Header h) => JsonConvert.SerializeObject(h);

        public static byte[] KeyFromPassword(string password, Header h) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(h.Salt), h.Iterations, HashAlgorithmName.SHA256, 32);

        /// <summary>new header for a data key: new salt, locked with the password</summary>
        public static Header NewHeader(byte[] dataKey, string password)
        {
            var h = new Header { Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) };
            h.ByPassword = Lock(dataKey, KeyFromPassword(password, h));
            return h;
        }

        /// <summary>changes the master password: same data key, new salt (the Windows Hello locks stay valid)</summary>
        public static void SetPassword(Header h, byte[] dataKey, string password)
        {
            h.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            h.Iterations = Pbkdf2Iterations;
            h.ByPassword = Lock(dataKey, KeyFromPassword(password, h));
        }

        public static string Lock(byte[] dataKey, byte[] lockKey) => Convert.ToBase64String(Seal(lockKey, dataKey));

        /// <summary>null if the lock key is wrong</summary>
        public static byte[]? Unlock(string locked, byte[] lockKey)
        {
            try { var k = Open(lockKey, Convert.FromBase64String(locked)); return k?.Length == 32 ? k : null; }
            catch (FormatException) { return null; }
        }

        /// <summary>data key from the master password, null if the password is wrong</summary>
        public static byte[]? UnlockWithPassword(Header h, string password) => Unlock(h.ByPassword, KeyFromPassword(password, h));
    }
}
