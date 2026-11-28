using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lemora.Server;

/// <summary>
/// Database folder chosen in the setup wizard: it must not inherit the permissions of its parent folder
/// (e.g. C:\Lemora writable by everyone: whoever writes server.db becomes console administrator).
/// Windows: only SYSTEM, administrators and this process (the service with its own identity). Linux: 700, owned by the service.
/// </summary>
internal static class DirAcl
{
    public static void Protect(string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            if (OperatingSystem.IsWindows()) ProtectWindows(dir);
            else File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Database folder permissions not set ({dir}): {ex.Message}"); }
    }

    /// <summary>who must be able to write: the service SID (S-1-5-80-...) when running as a service, otherwise the process user</summary>
    [SupportedOSPlatform("windows")]
    internal static SecurityIdentifier Me()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.Groups?.OfType<SecurityIdentifier>().FirstOrDefault(g => g.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)) ?? id.User!;
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(string dir)
    {
        var di = new DirectoryInfo(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);   // nothing inherited from the parent
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Me(), FileSystemRights.Modify, all, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }
}
