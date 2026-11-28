using System;
using Lemora.Utils.RdpFile;
using Lemora.View.Host.ProtocolHosts;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security fixes that can be verified without the UI (the extension and the Teams server have their own tests)</summary>
    public class SecurityFixes4Tests
    {
        [Theory]
        [InlineData("id\u000f")]            // Ctrl+O: in bash it runs the line
        [InlineData("rm -rf x\u000d")]      // CR stays: it is a counted Enter
        [InlineData("a\u0018\u0005b")]      // Ctrl+X Ctrl+E
        [InlineData("x\u0085y\u009cz")]     // C1
        public void Paste_OnlyTabAndNewlinesSurvive(string pasted)
        {
            var s = TerminalHost.StripPasteMarkers(pasted);
            foreach (var c in s) Assert.True(!char.IsControl(c) || c is '\t' or '\r' or '\n', $"character {(int)c} left");
        }

        [Fact]
        public void Paste_NormalTextUnchanged()
        {
            Assert.Equal("ls -la\tx\r\n", TerminalHost.StripPasteMarkers("ls -la\tx\r\n"));
        }

        [Fact]
        public void InstallDir_WritableByUsersIsNotSafe()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lemora-acl-" + Guid.NewGuid().ToString("N"));
            var di = System.IO.Directory.CreateDirectory(dir);
            try
            {
                var all = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                System.Security.AccessControl.FileSystemAccessRule R(System.Security.Principal.WellKnownSidType t, System.Security.AccessControl.FileSystemRights r) =>
                    new(new System.Security.Principal.SecurityIdentifier(t, null), r, all, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow);
                var sec = new System.Security.AccessControl.DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(R(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl));
                sec.AddAccessRule(R(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl));
                sec.AddAccessRule(R(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute));
                System.IO.FileSystemAclExtensions.SetAccessControl(di, sec);
                Assert.True(Lemora.Setup.SetupEngine.AclProtected(dir));   // like Program Files\Lemora
                sec.AddAccessRule(R(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, System.Security.AccessControl.FileSystemRights.Modify));
                System.IO.FileSystemAclExtensions.SetAccessControl(di, sec);
                Assert.False(Lemora.Setup.SetupEngine.AclProtected(dir));  // like a folder created under C:\ without its own permissions
            }
            finally
            {
                // restore the owner's permissions so it can be deleted
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                var open = new System.Security.AccessControl.DirectorySecurity();
                open.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                try { System.IO.FileSystemAclExtensions.SetAccessControl(di, open); System.IO.Directory.Delete(dir, true); } catch { /* stays in %TEMP% */ }
            }
        }

        [Fact]
        public void RdpFile_ControlCharsInFieldsAddNoLines()
        {
            var c = new RdpConfig("entry","10.0.0.1", "mario\r\nalternate full address:s:evil.example", "");
            c.Domain = "CORP\nauthentication level:i:0";
            var text = c.ToString();
            Assert.DoesNotContain("\nalternate full address", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\nauthentication level:i:0", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mario", text);
        }
    }
}
