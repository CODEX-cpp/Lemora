using System;

namespace Lemora.Utils
{
    /// <summary>
    /// Environment variables reserved for tests (fixed vault password, forced MCP server, imposed token):
    /// read only in the development build (Debug). In the installed version (Release) they are always null, so a program
    /// that writes the user's environment cannot create the vault with a known password nor turn on the MCP server (security hardening).
    /// </summary>
    internal static class DevOnly
    {
#if DEBUG
        public const bool Enabled = true;
#else
        public const bool Enabled = false;
#endif
        public static string? Env(string name) => Enabled ? Environment.GetEnvironmentVariable(name) : null;
    }
}
