# CC Director Coding Style Guide

This document defines the coding standards for CC Director. This is **enterprise-level software** that requires robust error handling, comprehensive logging, thorough testing, and responsive UI patterns.

## Core Philosophy

1. **Enterprise Quality** - This is production software, not a prototype
2. **Fail Fast, Fail Loud** - Validate early, throw specific exceptions, log everything
3. **No Fallbacks** - Fix root causes, don't add workarounds that hide problems
4. **Responsive UI** - Users must get immediate feedback for every action
5. **Test Everything** - Every feature needs unit tests
6. **Zero Warnings** - Treat warnings as errors

---

## 1. Responsive UI - CRITICAL

**Every user action MUST provide immediate visual feedback.**

### Rules

1. **Immediate Response**: When a user clicks a button or triggers an action, something must appear on screen within 100ms
   - Show the dialog/panel immediately, even if empty
   - Display a loading indicator if data isn't ready yet

2. **Loading Indicators**: Any operation that might take >200ms MUST show a loading state
   - Use spinning indicators or "Loading..." text
   - Disable buttons and show progress for long operations
   - Never freeze the UI waiting for I/O or network

3. **Async by Default**: All I/O operations (file reads, network, database) must be async
   - Load UI structure first, populate data in background
   - Use INotifyPropertyChanged to update UI when data arrives
   - Never block the UI thread with synchronous I/O

4. **Progressive Loading**: For lists with expensive item initialization:
   - Show items immediately with placeholder data
   - Load expensive metadata (file reads, API calls) in background
   - Update items as data becomes available

### Examples

```csharp
// BAD - Blocks UI
public MyDialog()
{
    InitializeComponent();
    // This blocks the UI thread!
    var items = LoadExpensiveData();
    ListBox.ItemsSource = items;
}

// GOOD - Immediate response with async loading
public MyDialog()
{
    InitializeComponent();
    LoadingIndicator.Visibility = Visibility.Visible;

    Loaded += async (_, _) =>
    {
        var items = await Task.Run(() => LoadExpensiveData());
        ListBox.ItemsSource = items;
        LoadingIndicator.Visibility = Visibility.Collapsed;
    };
}
```

---

## 2. Logging Standards

### Use FileLog for All Operations

CC Director uses `FileLog` for structured logging. Logs are written to:
- `%LOCALAPPDATA%/CcDirector/logs/director-YYYY-MM-DD-PID.log`

### Logging Levels

| Level | When to Use | Example |
|-------|-------------|---------|
| **Error** | Operation failed, needs attention | Exception caught, process failed |
| **Warning** | Potential issue, didn't cause failure | Retry needed, deprecated usage |
| **Info** | Important business events | Session start/stop, user actions |
| **Debug** | Detailed diagnostic info | Method entry/exit, state changes |

### Required Logging

**Service/Manager Methods (Public):**
- Log entry with parameters
- Log exit with result
- Log errors with full context

```csharp
public Session CreateSession(string repoPath, string? claudeArgs)
{
    FileLog.Write($"[SessionManager] CreateSession: repoPath={repoPath}, args={claudeArgs}");

    try
    {
        var session = CreateSessionInternal(repoPath, claudeArgs);
        FileLog.Write($"[SessionManager] Session created: id={session.Id}, pid={session.ProcessId}");
        return session;
    }
    catch (Exception ex)
    {
        FileLog.Write($"[SessionManager] CreateSession FAILED: {ex.Message}");
        throw;
    }
}
```

**Event Handlers and Entry Points:**
- Try-catch-finally with logging
- User-friendly error message
- Full exception logged

```csharp
private async void BtnNewSession_Click(object sender, RoutedEventArgs e)
{
    FileLog.Write("[MainWindow] New Session button clicked");
    try
    {
        await CreateNewSessionAsync();
    }
    catch (Exception ex)
    {
        FileLog.Write($"[MainWindow] New Session FAILED: {ex}");
        MessageBox.Show($"Failed to create session:\n{ex.Message}",
            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

### Log Context

Always include relevant context in log messages:

```csharp
// GOOD - includes context
FileLog.Write($"[SessionManager] Session {session.Id} exited: pid={pid}, exitCode={exitCode}");

// BAD - no context
FileLog.Write("Session exited");
```

### Never Log Sensitive Data

Do not log:
- API keys or tokens
- Passwords or credentials
- Personal user information
- Full file contents (truncate to ~100 chars)

---

## 3. Error Handling

### No Fallback Programming

**Never add fallback logic.** If something might fail, fix the root cause or fail explicitly.

```csharp
// BAD - fallback hides problems
public string GetSessionName(Guid id)
{
    try
    {
        return _sessions[id].CustomName ?? "Unknown";
    }
    catch
    {
        return "Unknown";  // NO! This hides the real problem
    }
}

// GOOD - fail explicitly with clear error
public string GetSessionName(Guid id)
{
    if (!_sessions.TryGetValue(id, out var session))
        throw new KeyNotFoundException($"Session {id} not found");

    return session.CustomName
        ?? throw new InvalidOperationException($"Session {id} has no name");
}
```

### Try-Catch at Boundaries Only

Try-catch belongs at **entry points** only:
- Event handlers (button clicks, etc.)
- Lifecycle methods (Loaded, Initialized)
- Timer callbacks
- External event subscriptions (pipe messages, process events)

**Do NOT put try-catch in:**
- Private helper methods
- Service layer methods (they should throw)
- Pure business logic

```csharp
// ENTRY POINT - HAS try-catch
private async void BtnSendPrompt_Click(object sender, RoutedEventArgs e)
{
    try
    {
        FileLog.Write("[MainWindow] Sending prompt");
        await SendPromptAsync();  // No try-catch inside
    }
    catch (Exception ex)
    {
        FileLog.Write($"[MainWindow] Send prompt FAILED: {ex}");
        ShowError("Failed to send prompt. Please try again.");
    }
}

// HELPER METHOD - NO try-catch, exceptions bubble up
private async Task SendPromptAsync()
{
    var text = PromptInput.Text.Trim();
    await _activeSession.SendTextAsync(text);  // Throws if fails
}
```

### Result Objects for Expected Failures

For operations that can fail in expected ways (validation, external checks):

```csharp
public class OperationResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }

    public static OperationResult<T> Ok(T value) =>
        new() { Success = true, Value = value };

    public static OperationResult<T> Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
```

---

## 4. Testing Standards

### Test Coverage Requirements

- **All public methods** in Core library must have unit tests
- **All bug fixes** must include a regression test
- **All new features** must include tests before merge

### Test Structure

Use the Arrange-Act-Assert pattern:

```csharp
[Fact]
public void UpdateClaudeSessionId_UpdatesExistingEntry()
{
    // Arrange
    var store = new RecentSessionStore(_filePath);
    store.Load();
    store.Add(_tempDir, "TestSession");

    // Act
    store.UpdateClaudeSessionId(_tempDir, "TestSession", "abc123-session-id");

    // Assert
    var recent = store.GetRecent();
    Assert.Single(recent);
    Assert.Equal("abc123-session-id", recent[0].ClaudeSessionId);
}
```

### Test Naming

`MethodName_Scenario_ExpectedResult`

```csharp
// GOOD
public void CreateSession_WithInvalidPath_ThrowsDirectoryNotFoundException()
public void HandlePipeEvent_StopEvent_SetsWaitingForInput()
public void GetRecent_AfterAdd_ReturnsMostRecentFirst()

// BAD
public void TestCreateSession()
public void Test1()
```

### What to Test

| Type | Must Test | Example |
|------|-----------|---------|
| Business logic | All branches | Session state transitions |
| Data persistence | Load/Save round-trip | RecentSessionStore |
| Validation | Valid + invalid inputs | Path validation |
| Edge cases | Nulls, empty, boundaries | Empty list, max entries |

---

## 5. Threading and UI

### WPF Threading Rules

**Never modify UI elements from background threads.**

```csharp
// BAD - modifies collection from background thread
private void OnToolResult(string result)
{
    _sessions.Add(new SessionViewModel(result));  // CRASH!
}

// GOOD - dispatch to UI thread
private void OnToolResult(string result)
{
    Dispatcher.BeginInvoke(() =>
    {
        _sessions.Add(new SessionViewModel(result));
    });
}
```

### Async Event Handlers

```csharp
// Use async void ONLY for event handlers
private async void Button_Click(object sender, RoutedEventArgs e)
{
    await DoWorkAsync();
}

// Use async Task for everything else
private async Task DoWorkAsync()
{
    await Task.Run(() => ExpensiveOperation());
}
```

---

## 6. Naming Conventions

### Classes
- **PascalCase** for all class names
- Suffix patterns:
  - `*Manager` for lifecycle management: `SessionManager`
  - `*Store` for persistence: `RecentSessionStore`
  - `*Backend` for I/O abstraction: `ConPtyBackend`
  - `*Reader` for read-only access: `ClaudeSessionReader`
  - `*ViewModel` for UI binding: `SessionViewModel`

### Methods
- **PascalCase**
- **Verb + Noun** pattern: `CreateSession()`, `SendText()`
- Async methods: suffix with `Async`: `SendTextAsync()`, `KillAsync()`
- Boolean methods: prefix with `Is`, `Has`, `Can`: `IsRunning()`, `HasExited()`

### Private Fields
- **_camelCase** with underscore prefix
- Readonly when possible: `private readonly SessionManager _sessionManager;`

```csharp
// Good
private readonly SessionManager _sessionManager;
private readonly ObservableCollection<SessionViewModel> _sessions = new();
private CancellationTokenSource? _cts;

// Bad
private SessionManager sessionManager;  // Missing underscore
```

---

## 7. Project Configuration

### Every Project Must Include

```xml
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

---

## 8. Documentation

### XML Documentation for Public APIs

```csharp
/// <summary>
/// Creates a new Claude session in the specified repository.
/// </summary>
/// <param name="repoPath">Path to the git repository.</param>
/// <param name="claudeArgs">Optional arguments to pass to Claude.</param>
/// <returns>The created session.</returns>
/// <exception cref="DirectoryNotFoundException">Repository path does not exist.</exception>
public Session CreateSession(string repoPath, string? claudeArgs = null)
```

### Code Comments

- Comments explain **why**, not **what**
- Code should be self-documenting through good names

```csharp
// GOOD - explains why
// Delay to ensure Claude has initialized before sending input
await Task.Delay(500);

// BAD - explains what (obvious from code)
// Wait 500 milliseconds
await Task.Delay(500);
```

---

## 9. Validation

### Validate Early

```csharp
public Session CreateSession(string repoPath, string? claudeArgs)
{
    // Validate at method entry
    if (string.IsNullOrWhiteSpace(repoPath))
        throw new ArgumentException("Repository path is required", nameof(repoPath));

    if (!Directory.Exists(repoPath))
        throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

    // Rest of method...
}
```

### Use Pattern Matching for Null Checks

```csharp
// Good
if (session is null)
    throw new ArgumentNullException(nameof(session));

if (result is not null)
    ProcessResult(result);

// Bad
if (session == null)  // Less clear intent
```

---

## 10. Quick Reference

| Aspect | Convention | Example |
|--------|------------|---------|
| Warnings | Treat as errors | `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` |
| Classes | PascalCase + suffix | `SessionManager`, `ConPtyBackend` |
| Methods | PascalCase, Verb+Noun | `CreateSession()`, `SendTextAsync()` |
| Private fields | _camelCase | `_sessionManager`, `_sessions` |
| Async methods | Suffix with Async | `KillSessionAsync()` |
| Null checks | Pattern matching | `if (x is null)` |
| Validation | Throw early | `ArgumentException.ThrowIfNullOrEmpty()` |
| Logging | FileLog with context | `FileLog.Write($"[Class] message: {param}")` |
| Try-catch | Entry points only | Event handlers, lifecycle methods |
| UI thread | Dispatcher.BeginInvoke | For UI modifications from background |
| Tests | Required for all public methods | Arrange-Act-Assert pattern |

---

## Summary

CC Director prioritizes:

1. **Reliability** - Enterprise-level logging and error handling
2. **Responsiveness** - Immediate UI feedback for all user actions
3. **Testability** - Comprehensive test coverage
4. **Maintainability** - Clear code structure and documentation
5. **Debuggability** - Every operation is logged with context
6. **Thread Safety** - Proper synchronization for UI operations
7. **Simplicity** - The simplest solution that works correctly

**When in doubt:**
- Log more, not less
- Fail explicitly, not silently
- Show feedback immediately
- Write a test
