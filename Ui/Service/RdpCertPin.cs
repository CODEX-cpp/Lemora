using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>
    /// Identity of RDP PCs (security hardening): before, FreeRDP started with /cert:ignore and a man in the middle
    /// received the connection (and with NLA the credential response). Now, like SSH: Lemora reads the PC's TLS certificate before FreeRDP,
    /// remembers it the first time (encrypted database, key "rdp.certpins"), and the following times warns and asks if it changed.
    /// FreeRDP gets the fingerprint (/cert:fingerprint): a certificate different from the one checked here is rejected by it too.
    /// </summary>
    public static class RdpCertPin
    {
        private const string DbKey = "rdp.certpins";
        private static Dictionary<string, string>? _map;
        private static readonly object Gate = new();

        public enum Kind { Tls, NoTls, Unreachable }

        public sealed record Probe(Kind Kind, string? Fingerprint, string Detail);

        private static Dictionary<string, string> Map()
        {
            if (_map != null) return _map;
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json))
                    return _map = string.IsNullOrEmpty(json) ? new() : JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
            }
            catch (Exception e) { SimpleLogHelper.Warning("RDP: certificate fingerprints not readable. " + e.Message); }
            return new();
        }

        public static string Key(string host, int port) => $"{host.Trim().TrimEnd('.').ToLowerInvariant()}:{port}";

        /// <summary>
        /// key of a saved entry: the certificate is remembered per entry, not per address. Before, two PCs of different customers with the same
        /// private address (e.g. 192.0.2.1) shared the remembered certificate and every switch gave a false "certificate changed".
        /// Quick connections (no saved entry) stay per address
        /// </summary>
        public static string EntryKey(string entryId) => "entry:" + entryId;

        private static string KeyFor(string host, int port, string? entryId) => string.IsNullOrEmpty(entryId) ? Key(host, port) : EntryKey(entryId);

        public static string? Get(string host, int port, string? entryId = null) { lock (Gate) return Map().TryGetValue(KeyFor(host, port, entryId), out var f) ? f : null; }

        public static void Set(string host, int port, string fingerprint, string? entryId = null)
        {
            lock (Gate)
            {
                var m = Map();
                m[KeyFor(host, port, entryId)] = fingerprint;
                try { if (AppDbStore.Set(DbKey, JsonConvert.SerializeObject(m))) _map = m; }
                catch (Exception e) { SimpleLogHelper.Error(e); }
            }
        }

        /// <summary>SHA-256 fingerprint of the certificate in lowercase hex, without separators</summary>
        public static string Fingerprint(byte[] rawCert) => Convert.ToHexString(SHA256.HashData(rawCert)).ToLowerInvariant();

        /// <summary>
        /// opens the RDP connection up to TLS (X.224 request with SSL|HYBRID|HYBRID_EX) and reads the PC's certificate.
        /// NoTls = the PC accepts only the legacy RDP security (no certificate to verify)
        /// </summary>
        public static Probe Fetch(string host, int port, int timeoutMs = 8000)
        {
            try
            {
                using var tcp = new TcpClient();
                // Unreachable details only go to the log; NoTls details are shown in the dialog, so they come from the language files
                if (!tcp.ConnectAsync(host, port).Wait(timeoutMs)) return new Probe(Kind.Unreachable, null, "no answer");
                tcp.ReceiveTimeout = timeoutMs; tcp.SendTimeout = timeoutMs;
                var ns = tcp.GetStream();
                // TPKT (4) + X.224 Connection Request (7) + RDP_NEG_REQ (8): requested protocols SSL | HYBRID | HYBRID_EX
                ns.Write(new byte[] { 0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x0B, 0x00, 0x00, 0x00 });
                var head = ReadExact(ns, 4);
                if (head[0] != 0x03) return new Probe(Kind.Unreachable, null, "not an RDP server");
                var len = (head[2] << 8) | head[3];
                if (len < 11 || len > 512) return new Probe(Kind.Unreachable, null, "invalid RDP reply");
                var rest = ReadExact(ns, len - 4);
                // X.224 Connection Confirm (7 bytes) and then, if present, RDP_NEG_RSP (type 2) or RDP_NEG_FAILURE (type 3)
                if (rest.Length < 7 + 8) return new Probe(Kind.NoTls, null, IoC.Translate("rdpcert_detail_no_tls_offer"));
                var type = rest[7];
                if (type == 0x03) return new Probe(Kind.NoTls, null, IoC.Translate("rdpcert_detail_refuses_tls"));
                var selected = BitConverter.ToUInt32(rest, 11);
                if (type != 0x02 || selected == 0) return new Probe(Kind.NoTls, null, IoC.Translate("rdpcert_detail_legacy_chosen"));
                byte[]? raw = null;
                using var ssl = new SslStream(ns, false);
#pragma warning disable SYSLIB0039 // old PCs speak only TLS 1.0/1.1: here only the certificate is read
                ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 | SslProtocols.Tls11 | SslProtocols.Tls,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => { raw = cert?.GetRawCertData(); return true; },
                    // only the fingerprint matters: no lookup of root certificates on the internet, which with blocked outbound traffic caused a ~12 s wait
                    CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck, TrustMode = X509ChainTrustMode.CustomRootTrust },
                });
#pragma warning restore SYSLIB0039
                return raw == null ? new Probe(Kind.Unreachable, null, "no certificate") : new Probe(Kind.Tls, Fingerprint(raw), "");
            }
            catch (AuthenticationException e) { return new Probe(Kind.Unreachable, null, "TLS failed: " + e.Message); }
            catch (Exception e) { return new Probe(Kind.Unreachable, null, e.GetBaseException().Message); }
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n;)
            {
                var r = s.Read(b, i, n - i);
                if (r <= 0) throw new IOException("connection closed by the PC");
                i += r;
            }
            return b;
        }

        /// <summary>automated tests only: answer instead of the dialog (saved fingerprint, new fingerprint)</summary>
        internal static Func<string, string, bool>? AskForTests;

        /// <summary>
        /// check before FreeRDP (any thread, not the UI one). Returns the arguments to give FreeRDP for the certificate,
        /// or null = do not connect (the user refused). error = reason to show.
        /// </summary>
        /// <param name="entryId">id of the saved entry (certificate remembered per entry); null for a quick connection (remembered per address)</param>
        public static List<string>? CheckForFreeRdp(string host, int port, string displayName, out string error, bool viaGateway = false, string? entryId = null)
        {
            error = "";
            // with an RD gateway the certificate seen from here (direct connection) is not the one FreeRDP will see, and the PC's fingerprint would also be compared
            // against the gateway: the check is left to FreeRDP, which verifies gateway and PC with its own list and asks the user (never /cert:ignore). Security hardening.
            if (viaGateway) return new List<string>();
            var p = Fetch(host, port);
            // in older versions everything was remembered per address: that certificate is the starting point of an entry that has none yet
            var byAddress = Get(host, port);
            var known = Get(host, port, entryId);
            switch (p.Kind)
            {
                case Kind.Tls:
                    {
                        var action = Decide(known, byAddress, entryId != null, p.Fingerprint!);
                        var fromAddress = action == PinAction.AskOtherPcSameAddress;
                        if (action == PinAction.Adopt)
                        {
                            Set(host, port, p.Fingerprint!, entryId);   // same certificate already seen at this address: it simply becomes this entry's
                        }
                        else if (action == PinAction.First)
                        {
                            Set(host, port, p.Fingerprint!, entryId);   // first time: remembered (like SSH, but without asking: RDP PCs almost always have self-signed certificates)
                            SimpleLogHelper.Warning($"RDP: certificate of {Key(host, port)} remembered at the first connection");
                        }
                        else if (action != PinAction.Ok)
                        {
                            var remembered = known ?? byAddress!;
                            // the address had another certificate, but this entry never had one: often another PC with the same address (another customer's network)
                            bool ok = false;
                            if (AskForTests != null) ok = AskForTests(remembered, p.Fingerprint!);
                            else Stylet.Execute.OnUIThreadSync(() => ok = Lemora.Utils.MessageBoxHelper.Confirm(
                                IoC.Translate(fromAddress ? "rdpcert_other_address_body" : "rdpcert_changed_body", displayName, host, port, Pretty(remembered), Pretty(p.Fingerprint!)),
                                IoC.Translate("rdpcert_trust_connect"), IoC.Translate("Cancel"), IoC.Translate(fromAddress ? "rdpcert_other_address_title" : "rdpcert_changed_title")));
                            if (!ok) { error = "PC certificate changed: connection cancelled"; SimpleLogHelper.Warning($"RDP: certificate of {Key(host, port)} changed, connection cancelled"); return null; }
                            Set(host, port, p.Fingerprint!, entryId);
                            SimpleLogHelper.Warning($"RDP: certificate of {Key(host, port)} changed, accepted by the user{(fromAddress ? " (before remembered only per address)" : "")}");
                        }
                        // FreeRDP accepts only this certificate, and never the legacy RDP security (which has none to verify)
                        // "deny": without it, with a different certificate FreeRDP opened ITS OWN "accept" prompt (tested in the sandbox); with deny it exits (code 143).
                        // FreeRDP 3: "-sec-rdp" no longer exists, it is written /sec:rdp:off
                        return new List<string> { $"/cert:deny,fingerprint:sha256:{p.Fingerprint}", "/sec:rdp:off" };
                    }
                case Kind.NoTls:
                    {
                        bool ok = false;
                        // PC with an already remembered certificate that now says "no TLS": a man in the middle is more likely than a changed PC (security hardening).
                        // Strong warning, and in any case no /cert:ignore:
                        // with /sec:rdp FreeRDP uses only the legacy security and does not then accept an arbitrary TLS
                        var pinned = (known ?? byAddress) != null;
                        if (AskForTests != null) ok = AskForTests("", "");
                        else Stylet.Execute.OnUIThreadSync(() => ok = Lemora.Utils.MessageBoxHelper.Confirm(
                            IoC.Translate(pinned ? "rdpcert_notls_pinned_body" : "rdpcert_notls_body", displayName, host, port, p.Detail),
                            IoC.Translate("rdpcert_connect_anyway"), IoC.Translate("Cancel"), IoC.Translate(pinned ? "rdpcert_notls_pinned_title" : "rdpcert_notls_title")));
                        if (!ok) { error = "unprotected connection refused"; return null; }
                        return new List<string> { "/sec:rdp" };
                    }
                default:
                    {
                        // not read from here (unreachable, a TLS that .NET cannot speak, or a man in the middle making only this read fail):
                        // with a remembered certificate FreeRDP accepts only that one and never the legacy RDP security; without one, no /cert:ignore: FreeRDP checks
                        // with its own list and asks the user (security hardening). A certificate is never "forgotten"
                        SimpleLogHelper.Warning($"RDP: certificate of {Key(host, port)} not read ({p.Detail})");
                        // this entry's certificate; without one, the one remembered for the address (as in older versions)
                        var pin = known ?? byAddress;
                        return pin != null ? new List<string> { $"/cert:deny,fingerprint:sha256:{pin}", "/sec:rdp:off" } : new List<string>();
                    }
            }
        }

        internal enum PinAction { Ok, Adopt, First, AskChanged, AskOtherPcSameAddress }

        /// <summary>
        /// what to do with the certificate just read. known = remembered for this entry (or for the address, for a quick connection);
        /// byAddress = remembered for the address (in older versions the only one); perEntry = saved entry
        /// </summary>
        internal static PinAction Decide(string? known, string? byAddress, bool perEntry, string fingerprint)
        {
            static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            if (known != null) return Same(known, fingerprint) ? PinAction.Ok : PinAction.AskChanged;
            if (perEntry && byAddress != null) return Same(byAddress, fingerprint) ? PinAction.Adopt : PinAction.AskOtherPcSameAddress;
            return PinAction.First;
        }

        private static string Pretty(string hex)
        {
            var parts = new List<string>();
            for (int i = 0; i + 2 <= hex.Length; i += 2) parts.Add(hex.Substring(i, 2).ToUpperInvariant());
            return string.Join(":", parts);
        }
    }
}
