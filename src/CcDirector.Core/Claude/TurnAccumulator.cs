using System.Text.Json;
using CcDirector.Core.Pipes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Accumulates hook events for a single turn (UserPromptSubmit → Stop).
/// Reset on each new UserPromptSubmit.
/// </summary>
public sealed class TurnAccumulator
{
    public string? UserPrompt { get; private set; }
    public List<string> ToolsUsed { get; } = [];
    public List<string> FilesTouched { get; } = [];
    public List<string> BashCommands { get; } = [];
    public DateTimeOffset StartedAt { get; private set; }
    public bool IsActive { get; private set; }

    public TurnData? StartTurn(string prompt)
    {
        FileLog.Write($"[TurnAccumulator] StartTurn: promptLen={prompt.Length}, wasActive={IsActive}");

        TurnData? previous = null;
        if (IsActive)
        {
            // Auto-finish the interrupted turn before starting a new one
            previous = FinishTurn();
            FileLog.Write($"[TurnAccumulator] StartTurn: auto-finished interrupted turn, tools={previous.ToolsUsed.Count}");
        }

        UserPrompt = prompt;
        ToolsUsed.Clear();
        FilesTouched.Clear();
        BashCommands.Clear();
        StartedAt = DateTimeOffset.Now;
        IsActive = true;
        return previous;
    }

    public void AddToolUse(PipeMessage msg)
    {
        if (string.IsNullOrEmpty(msg.ToolName))
            return;

        if (!ToolsUsed.Contains(msg.ToolName))
            ToolsUsed.Add(msg.ToolName);

        if (msg.ToolInput is not { } toolInput)
            return;

        // Extract file_path from Read, Edit, Write, Glob tools
        if (TryGetStringProperty(toolInput, "file_path", out var filePath))
        {
            if (!FilesTouched.Contains(filePath))
                FilesTouched.Add(filePath);
        }

        // Extract command from Bash tool
        if (msg.ToolName == "Bash" && TryGetStringProperty(toolInput, "command", out var command))
        {
            BashCommands.Add(command);
        }
    }

    public TurnData FinishTurn()
    {
        FileLog.Write($"[TurnAccumulator] FinishTurn: prompt={UserPrompt?.Length ?? 0} chars, tools={ToolsUsed.Count}, files={FilesTouched.Count}, commands={BashCommands.Count}");
        IsActive = false;
        return new TurnData(
            UserPrompt ?? "",
            ToolsUsed.ToList(),
            FilesTouched.ToList(),
            BashCommands.ToList(),
            StartedAt);
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;
        if (prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString() ?? "";
        return !string.IsNullOrEmpty(value);
    }
}

/// <summary>
/// Snapshot of data accumulated during a single turn (UserPromptSubmit → Stop).
/// </summary>
public sealed record TurnData(
    string UserPrompt,
    List<string> ToolsUsed,
    List<string> FilesTouched,
    List<string> BashCommands,
    DateTimeOffset Timestamp);
