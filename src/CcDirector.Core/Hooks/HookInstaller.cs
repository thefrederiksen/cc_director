using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Hooks;

/// <summary>
/// Installs and uninstalls Claude Code hooks in ~/.claude/settings.json.
/// Uses JsonNode tree manipulation to preserve arbitrary user content.
/// </summary>
public static class HookInstaller
{
    private static readonly string[] HookEvents =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
        "PostToolUseFailure", "PermissionRequest", "Notification",
        "SubagentStart", "SubagentStop", "Stop", "TeammateIdle",
        "TaskCompleted", "PreCompact", "SessionEnd"
    };

    /// <summary>
    /// Install Director hooks into Claude's settings.json.
    /// Idempotent: won't duplicate if already present.
    /// </summary>
    public static async Task InstallAsync(string relayScriptPath, Action<string>? log = null, string? settingsPath = null)
    {
        settingsPath ??= GetSettingsPath();
        var dir = Path.GetDirectoryName(settingsPath)!;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        JsonNode? root = null;

        if (File.Exists(settingsPath))
        {
            var existing = await File.ReadAllTextAsync(settingsPath);
            // Backup before modifying
            var backupPath = settingsPath + $".backup.{DateTime.UtcNow:yyyyMMddHHmmss}";
            await File.WriteAllTextAsync(backupPath, existing);
            log?.Invoke($"Backed up settings to {backupPath}");

            root = JsonNode.Parse(existing);
        }

        root ??= new JsonObject();

        var hooks = root["hooks"]?.AsObject();
        if (hooks == null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{relayScriptPath}\"";

        foreach (var eventName in HookEvents)
        {
            var eventArray = hooks[eventName]?.AsArray();
            if (eventArray == null)
            {
                eventArray = new JsonArray();
                hooks[eventName] = eventArray;
            }

            // Check if our hook already exists (identify by relay script path in command)
            bool alreadyInstalled = false;
            foreach (var entry in eventArray)
            {
                var entryHooks = entry?["hooks"]?.AsArray();
                if (entryHooks == null) continue;
                foreach (var hook in entryHooks)
                {
                    var cmd = hook?["command"]?.GetValue<string>();
                    if (cmd != null && cmd.Contains(relayScriptPath, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyInstalled = true;
                        break;
                    }
                }
                if (alreadyInstalled) break;
            }

            if (alreadyInstalled)
            {
                log?.Invoke($"Hook for {eventName} already installed, skipping.");
                continue;
            }

            // Add our hook entry: { hooks: [{ type: "command", command: "...", async: true, timeout: 5 }] }
            var hookEntry = new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                        ["async"] = true,
                        ["timeout"] = 5
                    }
                }
            };

            eventArray.Add(hookEntry);
            log?.Invoke($"Installed hook for {eventName}.");
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(options);
        await File.WriteAllTextAsync(settingsPath, json);
        log?.Invoke($"Saved settings to {settingsPath}");
    }

    /// <summary>
    /// Remove Director hooks from Claude's settings.json.
    /// </summary>
    public static async Task UninstallAsync(string relayScriptPath, Action<string>? log = null, string? settingsPath = null)
    {
        settingsPath ??= GetSettingsPath();
        if (!File.Exists(settingsPath)) return;

        var existing = await File.ReadAllTextAsync(settingsPath);
        var root = JsonNode.Parse(existing);
        if (root == null) return;

        var hooks = root["hooks"]?.AsObject();
        if (hooks == null) return;

        foreach (var eventName in HookEvents)
        {
            var eventArray = hooks[eventName]?.AsArray();
            if (eventArray == null) continue;

            for (int i = eventArray.Count - 1; i >= 0; i--)
            {
                var entryHooks = eventArray[i]?["hooks"]?.AsArray();
                if (entryHooks == null) continue;

                bool isOurs = false;
                foreach (var hook in entryHooks)
                {
                    var cmd = hook?["command"]?.GetValue<string>();
                    if (cmd != null && cmd.Contains(relayScriptPath, StringComparison.OrdinalIgnoreCase))
                    {
                        isOurs = true;
                        break;
                    }
                }

                if (isOurs)
                {
                    eventArray.RemoveAt(i);
                    log?.Invoke($"Removed Director hook for {eventName}.");
                }
            }

            // Clean up empty event arrays
            if (eventArray.Count == 0)
                hooks.Remove(eventName);
        }

        // Clean up empty hooks object
        if (hooks.Count == 0)
            root.AsObject().Remove("hooks");

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(options);
        await File.WriteAllTextAsync(settingsPath, json);
        log?.Invoke($"Saved settings after uninstall to {settingsPath}");
    }

    /// <summary>Get the path to Claude's settings.json.</summary>
    public static string GetSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "settings.json");
    }
}
