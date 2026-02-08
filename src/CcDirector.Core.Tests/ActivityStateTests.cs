using CcDirector.Core.Configuration;
using CcDirector.Core.ConPty;
using CcDirector.Core.Memory;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class ActivityStateTests : IDisposable
{
    private readonly SessionManager _manager;

    public ActivityStateTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    private Session CreateTestSession()
    {
        return _manager.CreateSession(Path.GetTempPath());
    }

    [Fact]
    public void NewSession_DefaultsToStarting()
    {
        var session = CreateTestSession();
        // After CreateSession, the ActivityState should be Starting (process just launched)
        // but the process exit handler may fire SessionEnd. Check initial states are reasonable.
        Assert.True(session.ActivityState is ActivityState.Starting or ActivityState.Exited);
    }

    [Fact]
    public void HandlePipeEvent_Stop_SetsWaitingForInput()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "Stop" });
        Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_UserPromptSubmit_SetsWorking()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "UserPromptSubmit" });
        Assert.Equal(ActivityState.Working, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_Notification_PermissionPrompt_SetsWaitingForPerm()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage
        {
            HookEventName = "Notification",
            NotificationType = "permission_prompt"
        });
        Assert.Equal(ActivityState.WaitingForPerm, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_Notification_Other_SetsWaitingForInput()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage
        {
            HookEventName = "Notification",
            NotificationType = "idle_prompt"
        });
        Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_SessionStart_SetsIdle()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "SessionStart" });
        Assert.Equal(ActivityState.Idle, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_SessionEnd_SetsExited()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "SessionEnd" });
        Assert.Equal(ActivityState.Exited, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_FiresOnActivityStateChanged()
    {
        var session = CreateTestSession();
        ActivityState? oldState = null;
        ActivityState? newState = null;

        session.OnActivityStateChanged += (old, @new) =>
        {
            oldState = old;
            newState = @new;
        };

        session.HandlePipeEvent(new PipeMessage { HookEventName = "UserPromptSubmit" });

        Assert.NotNull(newState);
        Assert.Equal(ActivityState.Working, newState);
    }

    [Fact]
    public void HandlePipeEvent_SameState_DoesNotFireEvent()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "Stop" }); // → WaitingForInput

        int eventCount = 0;
        session.OnActivityStateChanged += (_, _) => eventCount++;

        session.HandlePipeEvent(new PipeMessage { HookEventName = "Stop" }); // → WaitingForInput again (same)
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void SendText_SetsWorking()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "Stop" }); // → WaitingForInput
        session.SendText("hello");
        Assert.Equal(ActivityState.Working, session.ActivityState);
    }

    [Fact]
    public void HandlePipeEvent_UnknownEvent_DoesNotChangeState()
    {
        var session = CreateTestSession();
        session.HandlePipeEvent(new PipeMessage { HookEventName = "Stop" }); // → WaitingForInput
        session.HandlePipeEvent(new PipeMessage { HookEventName = "SomeUnknownEvent" });
        Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
