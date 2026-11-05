using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Lemora.Utils
{
    /// <summary>
    /// Cassaforte dei dati: tutto cio' che l'app cifra nel database (password, chiavi, connessioni intere, archivio chiave/valore)
    /// passa da qui. AES-256-GCM con una chiave casuale (la "chiave dei dati") che esiste solo in memoria dopo lo sblocco.
    /// Nel database la chiave dei dati e' salvata solo chiusa: con la password principale (PBKDF2-SHA256, vale su ogni PC)
    /// e, per ogni PC/utente Windows, con Windows Hello (chiave nel TPM, vedi VaultUnlock). Senza una delle due il database non si legge.
    /// Formato di un valore cifrato: "lv1:" + base64(nonce 12 | testo cifrato | tag 16).
    /// </summary>
    public static class DataVault
    {
        private const string Tag = "lv1:";
        public const string HeaderConfigKey = "lemora.vault";
        private const int Pbkdf2Iterations = 600_000;   // raccomandazione OWASP 2023 per PBKDF2-HMAC-SHA256

        private static byte[]? _key;
        public static bool IsUnlocked => _key != null;

        // una decifratura costa pochi microsecondi, ma la Home ne fa centinaia all'avvio: lo stesso testo si decifra una volta sola
        private static readonly ConcurrentDictionary<string, string?> DecryptCache = new();
        public static int DecryptCalls, DecryptCacheHits;
        public static long DecryptTicks;

        public static void SetKey(byte[] key)
        {
            if (key.Length != 32) throw new ArgumentException("la chiave dei dati deve essere di 32 byte");
            _key = (byte[])key.Clone();
            DecryptCache.Clear();
        }

        public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

        /// <summary>copia della chiave dei dati attuale (per cambiare la password principale), null a cassaforte chiusa</summary>
        public static byte[]? CurrentKey() => _key == null ? null : (byte[])_key.Clone();

        public static bool IsVaultText(string? s) => s != null && s.StartsWith(Tag, StringComparison.Ordinal);

        public static string SimpleEncrypt(string txt)
        {
            var key = _key ?? throw new InvalidOperationException("cassaforte chiusa: nessuna chiave dei dati");
            return Tag + Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(txt)));
        }

        /// <summary>null se il testo non e' nel formato della cassaforte o non si decifra con la chiave attuale</summary>
        public static string? SimpleDecrypt(string? encryptString)
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
            catch (FormatException) { /* non e' base64: non e' nostro */ }
            System.Threading.Interlocked.Add(ref DecryptTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            System.Threading.Interlocked.Increment(ref DecryptCalls);
            DecryptCache[encryptString!] = plain;
            return plain;
        }

        public static string EncryptOnce(string str) => IsVaultText(str) && SimpleDecrypt(str) != null ? str : SimpleEncrypt(str);

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
            catch (AuthenticationTagMismatchException) { return null; }   // chiave sbagliata o dato alterato
        }

        // ---------------------------------------------------------------- intestazione: la chiave dei dati chiusa con i due lucchetti

        public sealed class Header
        {
            public int Version { get; set; } = 1;
            public string Salt { get; set; } = "";
            public int Iterations { get; set; } = Pbkdf2Iterations;
            /// <summary>chiave dei dati chiusa con la chiave ricavata dalla password principale</summary>
            public string ByPassword { get; set; } = "";
            /// <summary>chiave dei dati chiusa con Windows Hello, una voce per PC e utente Windows</summary>
            public List<HelloLock> Hello { get; set; } = new();
        }

        public sealed class HelloLock
        {
            /// <summary>PC\SID dell'utente Windows: solo quell'utente su quel PC ha la chiave nel TPM</summary>
            public string Who { get; set; } = "";
            public string Locked { get; set; } = "";
        }

        public static Header? ParseHeader(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Header>(json); } catch { return null; }
        }

        public static string HeaderToJson(Header h) => JsonConvert.SerializeObject(h);

        public static byte[] KeyFromPassword(string password, Header h) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(h.Salt), h.Iterations, HashAlgorithmName.SHA256, 32);

        /// <summary>nuova intestazione per una chiave dei dati: sale nuovo, chiusa con la password</summary>
        public static Header NewHeader(byte[] dataKey, string password)
        {
            var h = new Header { Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) };
            h.ByPassword = Lock(dataKey, KeyFromPassword(password, h));
            return h;
        }

        /// <summary>cambia la password principale: stessa chiave dei dati, nuovo sale (i lucchetti Windows Hello restano validi)</summary>
        public static void SetPassword(Header h, byte[] dataKey, string password)
        {
            h.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            h.Iterations = Pbkdf2Iterations;
            h.ByPassword = Lock(dataKey, KeyFromPassword(password, h));
        }

        public static string Lock(byte[] dataKey, byte[] lockKey) => Convert.ToBase64String(Seal(lockKey, dataKey));

        /// <summary>null se la chiave del lucchetto e' sbagliata</summary>
        public static byte[]? Unlock(string locked, byte[] lockKey)
        {
            try { var k = Open(lockKey, Convert.FromBase64String(locked)); return k?.Length == 32 ? k : null; }
            catch (FormatException) { return null; }
        }

        /// <summary>chiave dei dati dalla password principale, null se la password e' sbagliata</summary>
        public static byte[]? UnlockWithPassword(Header h, string password) => Unlock(h.ByPassword, KeyFromPassword(password, h));
    }
}
