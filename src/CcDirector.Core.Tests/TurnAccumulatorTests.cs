using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.Pipes;
using Xunit;

namespace CcDirector.Core.Tests;

public class TurnAccumulatorTests
{
    private readonly TurnAccumulator _accumulator = new();

    [Fact]
    public void StartTurn_SetsPromptAndIsActive()
    {
        _accumulator.StartTurn("fix the tests");

        Assert.Equal("fix the tests", _accumulator.UserPrompt);
        Assert.True(_accumulator.IsActive);
    }

    [Fact]
    public void StartTurn_SetsStartedAt()
    {
        var before = DateTimeOffset.Now;
        _accumulator.StartTurn("test prompt");
        var after = DateTimeOffset.Now;

        Assert.InRange(_accumulator.StartedAt, before, after);
    }

    [Fact]
    public void AddToolUse_RecordsUniqueToolNames()
    {
        _accumulator.StartTurn("prompt");

        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read" });
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read" });
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Edit" });

        Assert.Equal(["Read", "Edit"], _accumulator.ToolsUsed);
    }

    [Fact]
    public void AddToolUse_IgnoresNullToolName()
    {
        _accumulator.StartTurn("prompt");

        _accumulator.AddToolUse(new PipeMessage { ToolName = null });
        _accumulator.AddToolUse(new PipeMessage { ToolName = "" });

        Assert.Empty(_accumulator.ToolsUsed);
    }

    [Fact]
    public void AddToolUse_ExtractsFilePath()
    {
        _accumulator.StartTurn("prompt");

        var json = JsonSerializer.Deserialize<JsonElement>("""{"file_path": "/src/Foo.cs"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read", ToolInput = json });

        Assert.Equal(["/src/Foo.cs"], _accumulator.FilesTouched);
    }

    [Fact]
    public void AddToolUse_ExtractsFilePath_NoDuplicates()
    {
        _accumulator.StartTurn("prompt");

        var json = JsonSerializer.Deserialize<JsonElement>("""{"file_path": "/src/Foo.cs"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read", ToolInput = json });
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Edit", ToolInput = json });

        Assert.Single(_accumulator.FilesTouched);
    }

    [Fact]
    public void AddToolUse_ExtractsBashCommand()
    {
        _accumulator.StartTurn("prompt");

        var json = JsonSerializer.Deserialize<JsonElement>("""{"command": "dotnet test"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Bash", ToolInput = json });

        Assert.Equal(["dotnet test"], _accumulator.BashCommands);
    }

    [Fact]
    public void AddToolUse_DoesNotExtractCommandForNonBashTools()
    {
        _accumulator.StartTurn("prompt");

        var json = JsonSerializer.Deserialize<JsonElement>("""{"command": "dotnet test"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read", ToolInput = json });

        Assert.Empty(_accumulator.BashCommands);
    }

    [Fact]
    public void FinishTurn_ReturnsTurnDataAndDeactivates()
    {
        _accumulator.StartTurn("fix the tests");

        var json = JsonSerializer.Deserialize<JsonElement>("""{"file_path": "/src/Foo.cs"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read", ToolInput = json });

        var turn = _accumulator.FinishTurn();

        Assert.False(_accumulator.IsActive);
        Assert.Equal("fix the tests", turn.UserPrompt);
        Assert.Equal(["Read"], turn.ToolsUsed);
        Assert.Equal(["/src/Foo.cs"], turn.FilesTouched);
    }

    [Fact]
    public void FinishTurn_ReturnsSnapshotNotReference()
    {
        _accumulator.StartTurn("prompt");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read" });

        var turn = _accumulator.FinishTurn();

        // Starting a new turn clears the accumulator but shouldn't affect the snapshot
        _accumulator.StartTurn("new prompt");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Write" });

        Assert.Equal(["Read"], turn.ToolsUsed);
        Assert.Equal("prompt", turn.UserPrompt);
    }

    [Fact]
    public void StartTurn_ClearsPreviousData()
    {
        _accumulator.StartTurn("first");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read" });

        var json = JsonSerializer.Deserialize<JsonElement>("""{"file_path": "/a.cs"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Edit", ToolInput = json });

        var bashJson = JsonSerializer.Deserialize<JsonElement>("""{"command": "ls"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Bash", ToolInput = bashJson });

        // Finish the first turn before starting a new one (to test clearing specifically)
        _accumulator.FinishTurn();
        _accumulator.StartTurn("second");

        Assert.Equal("second", _accumulator.UserPrompt);
        Assert.Empty(_accumulator.ToolsUsed);
        Assert.Empty(_accumulator.FilesTouched);
        Assert.Empty(_accumulator.BashCommands);
        Assert.True(_accumulator.IsActive);
    }

    [Fact]
    public void StartTurn_WhileActive_AutoFinishesPreviousTurn()
    {
        _accumulator.StartTurn("first prompt");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read" });
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Edit" });

        var json = JsonSerializer.Deserialize<JsonElement>("""{"file_path": "/src/Foo.cs"}""");
        _accumulator.AddToolUse(new PipeMessage { ToolName = "Write", ToolInput = json });

        // Start a new turn while first is still active
        var interrupted = _accumulator.StartTurn("second prompt");

        // The interrupted turn should contain all the data from the first turn
        Assert.NotNull(interrupted);
        Assert.Equal("first prompt", interrupted.UserPrompt);
        Assert.Equal(["Read", "Edit", "Write"], interrupted.ToolsUsed);
        Assert.Equal(["/src/Foo.cs"], interrupted.FilesTouched);

        // The accumulator should now be tracking the new turn
        Assert.Equal("second prompt", _accumulator.UserPrompt);
        Assert.Empty(_accumulator.ToolsUsed);
        Assert.True(_accumulator.IsActive);
    }

    [Fact]
    public void StartTurn_WhileInactive_ReturnsNull()
    {
        // Accumulator is not active (never started or already finished)
        var result = _accumulator.StartTurn("new prompt");

        Assert.Null(result);
        Assert.Equal("new prompt", _accumulator.UserPrompt);
        Assert.True(_accumulator.IsActive);
    }

    [Fact]
    public void AddToolUse_HandlesNoToolInput()
    {
        _accumulator.StartTurn("prompt");

        _accumulator.AddToolUse(new PipeMessage { ToolName = "Read", ToolInput = null });

        Assert.Equal(["Read"], _accumulator.ToolsUsed);
        Assert.Empty(_accumulator.FilesTouched);
    }
}
