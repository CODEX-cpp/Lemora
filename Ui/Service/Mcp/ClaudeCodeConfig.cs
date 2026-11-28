using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shawn.Utils;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// The "lemora" entry in the Claude Code file (%USERPROFILE%\.claude.json, global mcpServers section): the same one
    /// "claude mcp add --transport http lemora ..." would write, without needing the CLI. Only that entry is touched; before writing a backup is made
    /// (.claude.json.lemora-bak) and the write is atomic (temporary file then replace), because Claude Code rewrites the file often.
    /// Dates and numbers in the file stay as they are (read without conversions).
    /// </summary>
    public static class ClaudeCodeConfig
    {
        public const string EntryName = "lemora";

        /// <summary>for tests another file can be set with LEMORA_CLAUDE_JSON</summary>
        public static string FilePath => Lemora.Utils.DevOnly.Env("LEMORA_CLAUDE_JSON") is { Length: > 0 } p
            ? p : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

        private static JObject? Read(out bool exists)
        {
            exists = File.Exists(FilePath);
            if (!exists) return new JObject();
            using var sr = new StreamReader(FilePath);
            using var reader = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
            return JToken.ReadFrom(reader) as JObject;   // null = not a JSON object: leave it alone
        }

        /// <summary>the lemora entry exists and points to this port with this token</summary>
        public static bool IsLinked(out bool current)
        {
            current = false;
            try
            {
                var e = Read(out _)?["mcpServers"]?[EntryName] as JObject;
                if (e == null) return false;
                current = e.Value<string>("url") == Url(McpGuard.Settings.Port) && e["headers"]?.Value<string>("Authorization") == "Bearer " + McpGuard.Token;
                return true;
            }
            catch { return false; }
        }

        private static string Url(int port) => $"http://localhost:{port}/mcp";

        /// <summary>writes (or updates) the lemora entry; returns null on success, otherwise the reason</summary>
        public static string? Link() => Change(root =>
        {
            var servers = root["mcpServers"] as JObject ?? (JObject)(root["mcpServers"] = new JObject());
            servers[EntryName] = new JObject
            {
                ["type"] = "http",
                ["url"] = Url(McpGuard.Settings.Port),
                ["headers"] = new JObject { ["Authorization"] = "Bearer " + McpGuard.Token },
            };
        });

        public static string? Unlink() => Change(root => (root["mcpServers"] as JObject)?.Remove(EntryName));

        /// <summary>port or token changed: if Lemora is already linked the entry updates itself</summary>
        public static void UpdateIfLinked()
        {
            if (IsLinked(out var current) && !current && Link() is { } why) SimpleLogHelper.Warning("MCP: Claude Code entry not updated: " + why);
        }

        private static string? Change(Action<JObject> edit)
        {
            try
            {
                var root = Read(out var exists);
                if (root == null) return IoC.Translate("mcp_claude_json_bad_format");
                edit(root);
                var dir = Path.GetDirectoryName(FilePath)!;
                var tmp = Path.Combine(dir, $".claude.json.lemora-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tmp, root.ToString(Formatting.Indented));
                if (exists)
                {
                    File.Copy(FilePath, FilePath + ".lemora-bak", true);
                    File.Replace(tmp, FilePath, null);   // atomic replace: Claude Code never reads a half-written file
                }
                else File.Move(tmp, FilePath);
                McpGuard.Log($"\"{EntryName}\" entry of the Claude Code file {(root["mcpServers"]?[EntryName] != null ? "written" : "removed")}");
                return null;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("MCP: Claude Code file not changed: " + e.Message);
                return e.Message;
            }
        }
    }
}
