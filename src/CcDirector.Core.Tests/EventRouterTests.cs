using CcDirector.Core.Configuration;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class EventRouterTests : IDisposable
{
    private readonly SessionManager _manager;
    private readonly EventRouter _router;
    private readonly List<string> _logs = new();

    public EventRouterTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options, msg => _logs.Add(msg));
        _router = new EventRouter(_manager, msg => _logs.Add(msg));
    }

    [Fact]
    public void Route_AutoRegisters_ByMatchingCwd()
    {
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);

        Assert.Null(session.ClaudeSessionId);

        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = "claude-abc-123",
            Cwd = tempPath
        };

        _router.Route(msg);

        Assert.Equal("claude-abc-123", session.ClaudeSessionId);
        Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
    }

    [Fact]
    public void Route_RoutesToCorrectSession_AfterRegistration()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        _manager.RegisterClaudeSession("known-session", session.Id);

        var msg = new PipeMessage
        {
            HookEventName = "UserPromptSubmit",
            SessionId = "known-session"
        };

        _router.Route(msg);

        Assert.Equal(ActivityState.Working, session.ActivityState);
    }

    [Fact]
    public void Route_UnknownSessionId_NoUnmatchedSessions_Skips()
    {
        // No sessions exist
        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = "unknown-session",
            Cwd = @"C:\does\not\match"
        };

        _router.Route(msg);

        // Should log and skip without throwing
        Assert.Contains(_logs, l => l.Contains("No unmatched session"));
    }

    [Fact]
    public void Route_RaisesOnRawMessage()
    {
        PipeMessage? received = null;
        _router.OnRawMessage += m => received = m;

        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = "any-id"
        };

        _router.Route(msg);

        Assert.NotNull(received);
        Assert.Equal("Stop", received.HookEventName);
    }

    [Fact]
    public void Route_NoSessionId_Skips()
    {
        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = null
        };

        _router.Route(msg);

        Assert.Contains(_logs, l => l.Contains("no session_id"));
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
