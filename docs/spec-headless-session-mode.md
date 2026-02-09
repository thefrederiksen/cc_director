# Specification: Hybrid SDK + Local Session Management

## Overview

Director manages Claude Code sessions in two modes:
- **Local mode**: Embedded console window (current behavior)
- **Headless mode**: Via Claude Agent SDK (no console window, for remote/background use)

Headless sessions can be **promoted** to local embedded console using Claude's `--resume` flag, preserving full conversation context.

## Design Principle

Director always owns sessions. The **mode** determines HOW the session runs:

| Mode | UI | Process | Use Case |
|------|-----|---------|----------|
| Local (Embedded) | Console window | `claude.exe` | Active development |
| Headless | None | Python SDK | Remote access, background tasks |

Both modes use the same hooks, same session tracking, and same Claude session_id.

---

## Session Modes

### Current Modes
```csharp
public enum SessionMode
{
    ConPty,      // Legacy PTY-based
    Pipe,        // One-shot prompts via stdin/stdout
    Embedded     // Console overlay (current default)
}
```

### Proposed Addition
```csharp
public enum SessionMode
{
    ConPty,
    Pipe,
    Embedded,
    Headless     // NEW: SDK-based, no console window
}
```

---

## Architecture

### Headless Session Flow

```
1. User creates headless session (via UI or API)
2. Director creates Session with Mode=Headless
3. SdkRunner calls Python agent_runner.py
4. Python uses claude-agent-sdk query()
5. Hooks fire -> DirectorPipeServer receives them
6. Session tracked normally (state, activity, etc.)
7. Claude session_id captured from SessionStart hook
```

### Promotion Flow

```
1. User clicks "Promote to Local" on headless session
2. Director terminates SDK process (if still running)
3. Director spawns: claude --resume <claudeSessionId>
4. EmbeddedConsoleHost captures console window
5. Session continues with full conversation context
```

---

## Python Integration via CSnakes

### Why CSnakes?

CSnakes embeds Python directly in the .NET process:
- No subprocess overhead
- Direct function calls between C# and Python
- Shared memory for data transfer
- Python runs in-process

### Architecture Diagram

```
C# (Director)                     Python (via CSnakes)
     |                                   |
SessionManager                           |
     |                                   |
     +---> SdkRunner.RunQuery() -------> agent_runner.py
                |                             |
                |                             v
                |                      claude-agent-sdk
                |                      query(prompt, options)
                |                             |
                <--- messages ----------------+
                |
         Session.HandleSdkMessage()
```

---

## New Components

### 1. SdkRunner (C#)

Wrapper that calls Python via CSnakes.

```csharp
public class SdkRunner : IDisposable
{
    private readonly IPythonEnvironment _python;

    public SdkRunner(string pythonVenvPath)
    {
        _python = PythonEnvironment.Create(pythonVenvPath);
    }

    public async IAsyncEnumerable<SdkMessage> RunQuery(
        string prompt,
        string cwd,
        string[] allowedTools,
        CancellationToken cancellationToken = default)
    {
        using var scope = _python.CreateScope();
        var agentRunner = scope.Import("agent_runner");

        // Blocking call to Python - messages collected
        var messages = agentRunner.run_query(prompt, cwd, allowedTools);

        foreach (var msg in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return SdkMessage.FromPython(msg);
        }
    }

    public void Stop()
    {
        // Signal Python to stop (via shared flag or cancellation)
    }
}
```

### 2. agent_runner.py (Python)

Python module that wraps claude-agent-sdk.

```python
import asyncio
from claude_agent_sdk import query, ClaudeAgentOptions

def run_query(prompt: str, cwd: str, allowed_tools: list[str]) -> list[dict]:
    """
    Synchronous wrapper for C# to call.
    Collects all messages and returns them.
    """
    messages = []

    async def _run():
        async for msg in query(
            prompt=prompt,
            options=ClaudeAgentOptions(
                cwd=cwd,
                allowed_tools=allowed_tools
            )
        ):
            messages.append({
                "type": msg.type,
                "content": msg.content,
                "session_id": getattr(msg, "session_id", None),
                # ... other fields
            })
        return messages

    return asyncio.run(_run())


def run_query_streaming(prompt: str, cwd: str, callback) -> None:
    """
    Streaming version - calls back to C# for each message.
    Better for long-running queries.
    """
    async def _run():
        async for msg in query(
            prompt=prompt,
            options=ClaudeAgentOptions(cwd=cwd)
        ):
            callback({
                "type": msg.type,
                "content": msg.content,
                "session_id": getattr(msg, "session_id", None),
            })

    asyncio.run(_run())
```

### 3. SdkMessage (C#)

DTO for messages from the SDK.

```csharp
public record SdkMessage
{
    public string Type { get; init; }
    public string Content { get; init; }
    public string? SessionId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolInput { get; init; }

    public static SdkMessage FromPython(dynamic pyDict)
    {
        return new SdkMessage
        {
            Type = pyDict["type"],
            Content = pyDict["content"],
            SessionId = pyDict.get("session_id"),
            // ...
        };
    }
}
```

---

## Session.cs Changes

### New Properties

```csharp
public class Session
{
    // Existing properties...

    // NEW: For headless mode
    public Task? HeadlessTask { get; private set; }
    public CancellationTokenSource? HeadlessCts { get; private set; }
    public string? ClaudeSessionId { get; set; }  // From SDK init event

    public bool IsHeadless => Mode == SessionMode.Headless;
    public bool CanPromoteToLocal => IsHeadless && ClaudeSessionId != null;
}
```

### New Methods

```csharp
public void StartHeadless(Func<CancellationToken, Task> sdkTask)
{
    HeadlessCts = new CancellationTokenSource();
    HeadlessTask = sdkTask(HeadlessCts.Token);
}

public async Task StopHeadless()
{
    HeadlessCts?.Cancel();
    if (HeadlessTask != null)
    {
        try { await HeadlessTask; }
        catch (OperationCanceledException) { }
    }
    HeadlessTask = null;
    HeadlessCts = null;
}
```

---

## SessionManager.cs Changes

### Create Headless Session

```csharp
public async Task<Session> CreateHeadlessSession(
    string repoPath,
    string initialPrompt,
    string[]? allowedTools = null)
{
    var id = Guid.NewGuid();
    var session = new Session(id, repoPath, SessionMode.Headless);
    _sessions[id] = session;

    // Start SDK query
    session.StartHeadless(async ct =>
    {
        await foreach (var msg in _sdkRunner.RunQuery(
            initialPrompt, repoPath, allowedTools ?? Array.Empty<string>(), ct))
        {
            HandleSdkMessage(session, msg);
        }
    });

    return session;
}

private void HandleSdkMessage(Session session, SdkMessage msg)
{
    // Capture session ID from init
    if (msg.Type == "init" && msg.SessionId != null)
        session.ClaudeSessionId = msg.SessionId;

    // Update activity state
    session.LastActivity = DateTime.Now;

    // Route to existing event handlers
    OnSessionMessage?.Invoke(session, msg);
}
```

### Promote to Local

```csharp
public async Task PromoteToLocal(Guid sessionId)
{
    var session = _sessions[sessionId];

    if (!session.CanPromoteToLocal)
        throw new InvalidOperationException(
            "Session cannot be promoted (not headless or no session ID)");

    // Stop headless process
    await session.StopHeadless();

    // Switch mode
    session.Mode = SessionMode.Embedded;

    // Caller responsible for spawning console with --resume
    OnSessionPromoted?.Invoke(session);
}
```

---

## UI Changes

### Session List Display

Each session shows its mode:

```
[Embedded] my-project      Active    5m ago
[Headless] api-refactor    Running   2m ago
[Embedded] bug-fix         Idle      1h ago
```

### Promote Button

For headless sessions, show "Promote to Local" button:

```xaml
<Button Content="Promote to Local"
        Visibility="{Binding CanPromoteToLocal}"
        Command="{Binding PromoteCommand}" />
```

### Create Session Dialog

Option to create headless session:

```
[x] Start as headless (no console window)
    [ ] Initial prompt: _______________
```

---

## File Changes Summary

### Modified Files

| File | Changes |
|------|---------|
| `CcDirector.Core.csproj` | Add CSnakes NuGet package |
| `Session.cs` | Add HeadlessTask, ClaudeSessionId, Stop/Start methods |
| `SessionManager.cs` | Add CreateHeadlessSession(), PromoteToLocal() |
| `SessionMode.cs` | Add `Headless` enum value |
| `MainWindow.xaml` | Mode indicator column, promote button |
| `MainWindow.xaml.cs` | PromoteToLocal handler |

### New Files

| File | Purpose |
|------|---------|
| `src/CcDirector.Core/Sdk/SdkRunner.cs` | C# wrapper for Python calls |
| `src/CcDirector.Core/Sdk/SdkMessage.cs` | Message DTOs from SDK |
| `src/CcDirector.Core/Sdk/PythonEnvironmentSetup.cs` | CSnakes venv setup |
| `python/agent_runner.py` | Python SDK wrapper module |
| `python/requirements.txt` | claude-agent-sdk dependency |

### Unchanged Files

| File | Reason |
|------|--------|
| `EmbeddedConsoleHost.cs` | Just receives `--resume` arg, no changes needed |
| `EventRouter.cs` | Hooks work the same way |
| `DirectorPipeServer.cs` | Pipe server unchanged |
| Hook system | SDK uses same hooks infrastructure |

---

## Python Environment Setup

### Directory Structure

```
cc_director/
  python/
    agent_runner.py
    requirements.txt
    .venv/              # Created during setup
```

### requirements.txt

```
claude-agent-sdk
```

### Setup Script

```powershell
# Create venv and install dependencies
cd python
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

### CSnakes Configuration

```csharp
public static class PythonEnvironmentSetup
{
    public static IPythonEnvironment Create()
    {
        var pythonPath = Path.Combine(
            AppContext.BaseDirectory,
            "python", ".venv", "Scripts", "python.exe");

        return PythonEnvironment.Create(new PythonEnvironmentOptions
        {
            PythonPath = pythonPath,
            // Add python/ to sys.path
            AdditionalPaths = new[] {
                Path.Combine(AppContext.BaseDirectory, "python")
            }
        });
    }
}
```

---

## Hooks Integration

Headless sessions use the same hooks as local sessions:

1. Claude Agent SDK reads hooks from `.claude/settings.json`
2. Hooks call `CcDirectorHook.exe` (same as local)
3. Hook sends message to DirectorPipeServer
4. Director processes event normally

No changes needed to hook infrastructure.

---

## Testing Plan

### Unit Tests

1. SdkRunner correctly calls Python and returns messages
2. Session state transitions (Headless -> Embedded)
3. Cancellation works correctly

### Integration Tests

1. Create headless session, verify hooks received
2. Run query via SDK, verify messages processed
3. Promote to local, verify console embeds with `--resume`
4. Verify conversation context preserved after promotion
5. Verify activity state continues correctly

### Manual Tests

1. Create headless session from UI
2. Watch messages flow in without console window
3. Click "Promote to Local"
4. Verify console appears with full history
5. Continue conversation in console

---

## Future Enhancements

### Phase 2: Remote API

Expose headless sessions via REST API:

```
POST /api/sessions          # Create headless session
POST /api/sessions/{id}/query  # Send prompt
GET  /api/sessions/{id}/messages  # Get messages
POST /api/sessions/{id}/promote   # Promote to local
```

### Phase 3: Multiple Headless

Run multiple headless sessions simultaneously for parallel tasks.

### Phase 4: Session Persistence

Save/restore headless sessions across Director restarts.
