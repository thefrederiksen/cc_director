using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Hooks;
using Xunit;

namespace CcDirector.Core.Tests;

public class HookInstallerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly string _relayScript;

    public HookInstallerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HookInstallerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var claudeDir = Path.Combine(_tempDir, ".claude");
        Directory.CreateDirectory(claudeDir);

        _settingsPath = Path.Combine(claudeDir, "settings.json");
        _relayScript = Path.Combine(_tempDir, "hook-relay.ps1");
    }

    [Fact]
    public async Task Install_CreatesHooksInEmptySettings()
    {
        // Write empty settings
        await File.WriteAllTextAsync(_settingsPath, "{}");

        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;
        var hooks = root["hooks"]!.AsObject();

        var expectedHooks = new[]
        {
            "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
            "PostToolUseFailure", "PermissionRequest", "Notification",
            "SubagentStart", "SubagentStop", "Stop", "PreCompact", "SessionEnd"
        };
        Assert.Equal(12, expectedHooks.Length);
        foreach (var name in expectedHooks)
            Assert.True(hooks.ContainsKey(name), $"Missing hook: {name}");

        // Verify structure
        var stopArray = hooks["Stop"]!.AsArray();
        Assert.Single(stopArray);
        var entry = stopArray[0]!;
        var hookDefs = entry["hooks"]!.AsArray();
        Assert.Single(hookDefs);
        Assert.Equal("command", hookDefs[0]!["type"]!.GetValue<string>());
        Assert.True(hookDefs[0]!["async"]!.GetValue<bool>());
        Assert.Equal(5, hookDefs[0]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public async Task Install_PreservesExistingUserHooks()
    {
        var existingSettings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = "my-custom-hook.sh"
                            }
                        }
                    }
                }
            },
            ["someOtherSetting"] = "preserved"
        };

        await File.WriteAllTextAsync(_settingsPath, existingSettings.ToJsonString());

        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;

        // User's existing hook preserved
        var stopArray = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stopArray.Count); // user hook + our hook

        var firstHookCmd = stopArray[0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("my-custom-hook.sh", firstHookCmd);

        // Other settings preserved
        Assert.Equal("preserved", root["someOtherSetting"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_Idempotent_DoesNotDuplicate()
    {
        await File.WriteAllTextAsync(_settingsPath, "{}");

        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);
        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;
        var stopArray = root["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stopArray); // Only one entry, not two
    }

    [Fact]
    public async Task Install_CreatesBackup()
    {
        await File.WriteAllTextAsync(_settingsPath, "{\"existing\": true}");

        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        var backups = Directory.GetFiles(Path.GetDirectoryName(_settingsPath)!, "settings.json.backup.*");
        Assert.NotEmpty(backups);
    }

    [Fact]
    public async Task Uninstall_RemovesDirectorHooks()
    {
        await File.WriteAllTextAsync(_settingsPath, "{}");
        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        await HookInstaller.UninstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;

        // hooks object should be cleaned up
        Assert.Null(root["hooks"]);
    }

    [Fact]
    public async Task Uninstall_PreservesUserHooks()
    {
        // Setup: existing user hook + our hook
        await File.WriteAllTextAsync(_settingsPath, "{}");

        // First add a user hook manually
        var settings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = "my-custom-hook.sh"
                            }
                        }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(_settingsPath, settings.ToJsonString());

        // Install our hooks on top
        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        // Verify both exist
        var beforeJson = await File.ReadAllTextAsync(_settingsPath);
        var beforeRoot = JsonNode.Parse(beforeJson)!;
        Assert.Equal(2, beforeRoot["hooks"]!["Stop"]!.AsArray().Count);

        // Now uninstall
        await HookInstaller.UninstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;

        // User hook should remain
        var stopArray = root["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stopArray);
        Assert.Equal("my-custom-hook.sh", stopArray[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_RemovesStaleDirectorHooks()
    {
        // Simulate a settings.json with Director hooks under old/invalid event names
        var staleSettings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["TaskCompleted"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_tempDir, "hook-relay.ps1")}\""
                            }
                        }
                    }
                },
                ["TeammateIdle"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_tempDir, "hook-relay.ps1")}\""
                            }
                        }
                    }
                }
            }
        };

        await File.WriteAllTextAsync(_settingsPath, staleSettings.ToJsonString());

        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;
        var hooks = root["hooks"]!.AsObject();

        // Stale keys should be gone
        Assert.False(hooks.ContainsKey("TaskCompleted"), "TaskCompleted should have been removed");
        Assert.False(hooks.ContainsKey("TeammateIdle"), "TeammateIdle should have been removed");

        // Valid hooks should be present
        Assert.True(hooks.ContainsKey("Stop"));
        Assert.True(hooks.ContainsKey("SessionStart"));
    }

    [Fact]
    public async Task Install_PreservesNonDirectorHooksOnStaleEvents()
    {
        // A stale event name that has BOTH a Director hook and a user hook
        var mixedSettings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["TaskCompleted"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = "my-custom-task-hook.sh"
                            }
                        }
                    },
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = $"powershell.exe -File \"{Path.Combine(_tempDir, "hook-relay.ps1")}\""
                            }
                        }
                    }
                }
            }
        };

        await File.WriteAllTextAsync(_settingsPath, mixedSettings.ToJsonString());

        await HookInstaller.InstallAsync(_relayScript, settingsPath: _settingsPath);

        var json = await File.ReadAllTextAsync(_settingsPath);
        var root = JsonNode.Parse(json)!;
        var hooks = root["hooks"]!.AsObject();

        // TaskCompleted should still exist because user has their own hook there
        Assert.True(hooks.ContainsKey("TaskCompleted"));
        var arr = hooks["TaskCompleted"]!.AsArray();
        Assert.Single(arr); // only the user's hook remains
        Assert.Equal("my-custom-task-hook.sh", arr[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
