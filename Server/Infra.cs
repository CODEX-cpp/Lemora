using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Lemora.Server;

internal static class Paths
{
    public static string DefaultDataDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lemora Server")
        : "/var/lib/lemora-server";
}

internal static class NetUtil
{
    /// <summary>true per 10.x, 172.16-31.x, 192.168.x (e 169.254.x): il server non si mette mai in ascolto su indirizzi pubblici</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>indirizzi privati delle schede attive; se qualcuna ha un gateway (e' la rete vera) si scartano le schede virtuali senza uscita (WSL, Hyper-V, VPN di prova)</summary>
    public static List<IPAddress> PrivateAddresses()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .ToList();
        var withGateway = nics.Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))).ToList();
        return (withGateway.Count > 0 ? withGateway : nics)
            .SelectMany(p => p.UnicastAddresses.Select(a => a.Address))
            .Where(IsPrivate)
            .Distinct()
            .ToList();
    }
}

/// <summary>certificato autofirmato del server: creato una volta sola, i client ne ricordano l'impronta all'abbinamento</summary>
internal static class CertStore
{
    public static X509Certificate2 LoadOrCreate(string file, IEnumerable<IPAddress> addresses)
    {
        if (File.Exists(file)) return X509CertificateLoader.LoadPkcs12FromFile(file, null);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Lemora Server", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        foreach (var ip in addresses) san.AddIpAddress(ip);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        // 10 anni: rinnovarlo cambierebbe l'impronta e obbligherebbe a riabbinare tutti i PC
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(file, created.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(file, null);
    }

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
}
