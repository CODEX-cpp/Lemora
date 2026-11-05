using System;
using System.Collections.Generic;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>
    /// Impronte dei server SSH gia' incontrati (database cifrato, chiave "terminal.hosts"): "host:porta" -> impronta SHA256.
    /// Si usa dal thread dell'interfaccia; la lettura avviene una volta e poi resta in memoria.
    /// </summary>
    public static class TerminalKnownHosts
    {
        private const string DbKey = "terminal.hosts";
        private static Dictionary<string, string>? _map;

        private static Dictionary<string, string> Map()
        {
            if (_map != null) return _map;
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json))
                    return _map = string.IsNullOrEmpty(json) ? new() : JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
            }
            catch (Exception e) { SimpleLogHelper.Warning("Terminale: impronte illeggibili. " + e.Message); }
            return new();   // database non pronto: non si memorizza, si riprova la prossima volta
        }

        public static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

        /// <summary>impronta salvata per il server, oppure null se e' la prima volta</summary>
        public static string? Get(string host, int port) => Map().TryGetValue(Key(host, port), out var f) ? f : null;

        public static void Set(string host, int port, string fingerprint)
        {
            var m = Map();
            m[Key(host, port)] = fingerprint;
            try { if (AppDbStore.Set(DbKey, JsonConvert.SerializeObject(m))) _map = m; }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }
    }
}
