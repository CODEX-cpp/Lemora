using System;
using System.Collections.Generic;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>
    /// Fingerprints of SSH servers already seen (encrypted database, key "terminal.hosts"): "host:port" -> SHA256 fingerprint.
    /// Used from the UI thread; it is read once and then kept in memory.
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
            catch (Exception e) { SimpleLogHelper.Warning("Terminal: fingerprints not readable. " + e.Message); }
            return new();   // database not ready: not cached, retried next time
        }

        public static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

        /// <summary>
        /// as for RDP (RdpCertPin): the fingerprint is remembered per saved entry, so two servers of different networks with the same
        /// private address do not raise "fingerprint changed" at every switch. Quick connections (no entry) stay per address
        /// </summary>
        private static string KeyFor(string host, int port, string? entryId) => string.IsNullOrEmpty(entryId) ? Key(host, port) : RdpCertPin.EntryKey("ssh:" + entryId);

        /// <summary>fingerprint saved for the server (or for the entry), or null if it is the first time</summary>
        public static string? Get(string host, int port, string? entryId = null) => Map().TryGetValue(KeyFor(host, port, entryId), out var f) ? f : null;

        /// <summary>automated tests only: answer instead of the dialog (saved fingerprint or null, new fingerprint)</summary>
        internal static Func<string?, string, bool>? AskForTests;

        /// <summary>
        /// true if it is OK to proceed with this server: fingerprint known and equal, or confirmed now by the user (then remembered).
        /// Used by the terminal and by the old SFTP (security: SFTP accepted any server)
        /// </summary>
        /// <param name="entryId">id of the saved entry; null for a quick connection</param>
        public static bool Trust(string host, int port, string fingerprint, string? entryId = null)
        {
            if (AskForTests == null && !TerminalSettings.Current.VerifyHostKey) return true;   // check turned off in settings (not recommended)
            var known = Get(host, port, entryId);
            var byAddress = Get(host, port);
            var action = RdpCertPin.Decide(known, byAddress, !string.IsNullOrEmpty(entryId), fingerprint);
            if (action == RdpCertPin.PinAction.Ok) return true;
            // same key already seen at this address (data from older versions, or another entry to the same server): it becomes this entry's
            if (action == RdpCertPin.PinAction.Adopt) { Set(host, port, fingerprint, entryId); return true; }
            var remembered = known ?? byAddress;
            var ok = false;
            if (AskForTests != null) ok = AskForTests(remembered, fingerprint);
            else Stylet.Execute.OnUIThreadSync(() =>
            {
                ok = action switch
                {
                    RdpCertPin.PinAction.First => Lemora.Utils.MessageBoxHelper.Confirm(IoC.Translate("ssh_hostkey_first_body", host, port, fingerprint), IoC.Translate("ssh_hostkey_continue"), IoC.Translate("Cancel"), IoC.Translate("ssh_hostkey_first_title")),
                    RdpCertPin.PinAction.AskOtherPcSameAddress => Lemora.Utils.MessageBoxHelper.Confirm(IoC.Translate("ssh_hostkey_other_address_body", host, port, remembered ?? "", fingerprint), IoC.Translate("ssh_hostkey_trust_continue"), IoC.Translate("Cancel"), IoC.Translate("ssh_hostkey_other_address_title")),
                    _ => Lemora.Utils.MessageBoxHelper.Confirm(IoC.Translate("ssh_hostkey_changed_body", host, port, remembered ?? "", fingerprint), IoC.Translate("ssh_hostkey_trust_continue"), IoC.Translate("Cancel"), IoC.Translate("ssh_hostkey_changed_title")),
                };
            });
            if (ok) Set(host, port, fingerprint, entryId);
            return ok;
        }

        public static void Set(string host, int port, string fingerprint, string? entryId = null)
        {
            var m = Map();
            m[KeyFor(host, port, entryId)] = fingerprint;
            // the first key seen for the address stays also per address: another entry to the same server recognises it without asking again
            if (!string.IsNullOrEmpty(entryId)) m.TryAdd(Key(host, port), fingerprint);
            try { if (AppDbStore.Set(DbKey, JsonConvert.SerializeObject(m))) _map = m; }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }
    }
}
