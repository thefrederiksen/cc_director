# Coding Style Guide

> **Current language scope:** C# / .NET / WPF

This document defines the coding standards for CC Director. This is **enterprise-level software** that requires robust error handling, comprehensive logging, thorough testing, and responsive UI patterns.

---

## 0. Development Philosophy

1. **Enterprise Quality** - This is production software, not a prototype
2. **Fail Fast, Fail Loud** - Validate early, throw specific exceptions, log everything
3. **No Fallbacks** - Fix root causes, don't add workarounds that hide problems
4. **Responsive UI** - Users must get immediate feedback for every action
5. **Test Everything** - Every feature needs unit tests
6. **Zero Warnings** - Treat warnings as errors
7. **Simplicity** - The simplest solution that works correctly

### Dependency Injection

Use DI sparingly. Full DI containers create a "black box problem" where object creation is hidden behind container magic, making it harder to reason about what's actually happening. Prefer explicit construction with `new` for most classes. Use DI only when you genuinely need runtime substitution or when a framework requires it.

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

5. **Disable During Async**: Disable buttons during async operations to prevent double-clicks

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

## 2. Error Handling

### Universal Rule: Catch at the Boundary

Every UI framework has "boundaries" where user actions enter your code. Catch exceptions **only** at these boundaries. Never in helpers or services. This principle is framework-agnostic — included here for cross-project reference.

| Framework | Boundaries |
|-----------|-----------|
| WPF | Event handlers (`Button_Click`), lifecycle (`Loaded`), timer callbacks, external event subscriptions |
| Blazor Server | Controller actions, component lifecycle (`OnInitializedAsync`) |
| API | Controller actions, middleware |

**At every boundary:**
1. Log the full exception internally (class + method + full exception)
2. Show the user a friendly message (never raw exception text)
3. Let service/helper methods throw — they are NOT boundaries

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

## 3. Null Safety

### The Null-Forgiving Operator (`!`) Is FORBIDDEN

The null-forgiving operator (`!`) is **FORBIDDEN** in this codebase. It is lazy programming that hides problems instead of fixing them.

### Why It's Forbidden

- It doesn't fix null reference issues - just silences the compiler
- If the value IS null at runtime, you still get NullReferenceException
- No context about what went wrong or why
- It's fallback programming in disguise - "trust me bro" coding

### What To Do Instead

**If null is a programming error - throw with context:**

```csharp
// BAD - lazy, crashes with useless error
var data = session.Buffer!.DumpAll();

// GOOD - fails fast with useful error
if (session.Buffer == null)
    throw new InvalidOperationException(
        $"Session {session.Id} has no buffer (BackendType={session.BackendType})");
var data = session.Buffer.DumpAll();
```

**If null is valid - handle explicitly:**

```csharp
// BAD
var name = user!.Name;

// GOOD
if (user == null)
    return "Anonymous";
return user.Name;
```

**In tests - use Assert.NotNull:**

```csharp
// BAD
Assert.Equal(expected, obj!.Value);

// GOOD
Assert.NotNull(obj);
Assert.Equal(expected, obj.Value);
```

### Only Exception

Constructor field assignment where you're immediately initializing and the compiler can't figure it out. Must include a comment explaining why.

```csharp
// OK - compiler can't see that Loaded event initializes this
private DataService _dataService = null!; // Initialized in OnLoaded

private void OnLoaded()
{
    _dataService = new DataService();
}
```

### Pattern Matching for Null Checks

```csharp
// Preferred
if (session is null)
    throw new ArgumentNullException(nameof(session));

if (result is not null)
    ProcessResult(result);
```

---

## 4. Logging Standards

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

### Log Format

```
[ClassName] MethodName: context={value}, result={result}
[ClassName] MethodName FAILED: {ex.Message}
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

## 5. Testing Standards

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

### Test Project Configuration

```xml
<PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
</ItemGroup>
```

---

## 6. Async and Threading

### Async Rules

1. **Async all the way.** Once you go async, stay async up the call chain.
2. **Never use `.Result` or `.Wait()`.** These cause deadlocks in UI applications.
3. **`async void` only for event handlers.** Everything else returns `Task` or `Task<T>`.
4. **Use `Task.Run` for CPU-bound work** to keep the UI thread free.

```csharp
// async void ONLY for event handlers
private async void Button_Click(object sender, RoutedEventArgs e)
{
    await DoWorkAsync();
}

// Task for everything else
private async Task DoWorkAsync()
{
    var result = await Task.Run(() => ExpensiveOperation());
    UpdateUI(result);
}
```

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

### Thread Safety Guidelines

- Use `Dispatcher.BeginInvoke` (not `Invoke`) for non-blocking UI updates
- Use `ConcurrentDictionary` for shared caches across threads
- Use `lock` for short critical sections protecting shared mutable state
- Use `Debug.Assert(Dispatcher.CheckAccess())` to verify thread assumptions
- Create defensive `.ToList()` snapshots when iterating collections that may be modified

---

## 7. Naming Conventions

### General Rules

| Element | Convention | Example |
|---------|------------|---------|
| Classes | PascalCase | `SessionManager` |
| Interfaces | IPascalCase | `ISessionProvider` |
| Methods | PascalCase, Verb+Noun | `CreateSession()`, `SendText()` |
| Async methods | Suffix `Async` | `SendTextAsync()`, `KillAsync()` |
| Properties | PascalCase | `IsRunning`, `SessionCount` |
| Private fields | _camelCase | `_sessionManager`, `_sessions` |
| Local variables | camelCase | `sessionId`, `exitCode` |
| Constants | PascalCase | `DefaultTimeout`, `MaxRetries` |
| Enums | PascalCase (singular) | `SessionState { Running, Stopped }` |
| Boolean members | `Is`, `Has`, `Can` prefix | `IsRunning`, `HasExited`, `CanSend` |

### Class Suffix Conventions

| Suffix | Responsibility | Example |
|--------|---------------|---------|
| `*Manager` | Lifecycle management, orchestration | `SessionManager` |
| `*Store` | Persistence (load/save) | `RecentSessionStore` |
| `*Backend` | I/O abstraction, external processes | `ConPtyBackend` |
| `*Reader` | Read-only data access | `ClaudeSessionReader` |
| `*ViewModel` | UI data binding | `SessionViewModel` |
| `*Control` | Custom WPF control | `TerminalControl` |
| `*Dialog` | Modal/modeless window | `NewSessionDialog` |

### Event Handler Naming

```csharp
// Pattern: ElementName_EventName
private void BtnNewSession_Click(object sender, RoutedEventArgs e) { }
private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

// Pattern: OnEventName for overrides and lifecycle
protected override void OnClosing(CancelEventArgs e) { }
private void OnSessionExited(Session session) { }
```

### Private Fields

```csharp
// Good - underscore prefix, readonly when possible
private readonly SessionManager _sessionManager;
private readonly ObservableCollection<SessionViewModel> _sessions = new();
private CancellationTokenSource? _cts;

// Bad - missing underscore
private SessionManager sessionManager;
```

---

## 8. Architecture and Project Structure

### Layer Separation

```
Solution/
  src/
    ProjectName.Core/         # Business logic, models, services (no UI references)
    ProjectName.Wpf/          # WPF UI, controls, dialogs, view models
    ProjectName.Core.Tests/   # Unit tests for Core
    ProjectName.TestHarness/  # Integration testing and manual testing
```

### Rules

- **Core has no UI dependencies.** The Core project must never reference WPF, WinForms, or any UI framework. This enables testing without a UI thread.
- **One responsibility per class.** Managers manage lifecycle. Stores handle persistence. Backends handle I/O. Readers handle read-only access.
- **InternalsVisibleTo for testing.** Expose internal members to test projects only.

```xml
<!-- Core.csproj -->
<ItemGroup>
  <InternalsVisibleTo Include="ProjectName.Core.Tests" />
</ItemGroup>
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

### Validation Boundaries

Validate at system boundaries where external data enters:
- User input (text boxes, dialog results)
- File I/O (paths, file contents)
- External API responses
- Process output

Trust internal code within the same assembly.

---

## 10. UI Patterns

### MVVM Lite

For smaller WPF applications, a pragmatic approach:

- **Code-behind for simple event handlers.** Don't over-abstract with full MVVM for small dialogs.
- **ViewModels for data binding.** Use `INotifyPropertyChanged` for dynamic UI updates.
- **Static resources for shared styles.** Define in `App.xaml` for consistency.

### Dialog Pattern

```csharp
// Dialogs set Owner for proper centering and z-order
var dialog = new NewSessionDialog { Owner = this };
if (dialog.ShowDialog() == true)
{
    var result = dialog.SelectedItem;
    // Use result...
}
```

### Control Pattern

Custom controls extend `FrameworkElement` or `UserControl`:
- `UserControl` for composite controls with XAML
- `FrameworkElement` for render-level controls (e.g., terminal rendering with `OnRender`)

### Resource Pattern

Define shared brushes and styles in `App.xaml`:

```xml
<Application.Resources>
    <SolidColorBrush x:Key="PanelBackground" Color="#1E1E1E" />
    <SolidColorBrush x:Key="AccentBrush" Color="#007ACC" />
    <SolidColorBrush x:Key="TextForeground" Color="#CCCCCC" />
</Application.Resources>
```

Reference with `{StaticResource}`:

```xml
<Border Background="{StaticResource PanelBackground}">
    <TextBlock Foreground="{StaticResource TextForeground}" />
</Border>
```

---

## 11. Performance

### Caching

Cache expensive-to-create objects, especially in render paths:

```csharp
// Cache Typefaces (immutable, safe to share)
private static readonly Typeface _typefaceNormal = new(
    _fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

// Cache Brushes with Freeze() for cross-thread access
private static SolidColorBrush GetCachedBrush(Color color)
{
    lock (_brushCacheLock)
    {
        if (!_brushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();  // Required for cross-thread use
            _brushCache[color] = brush;
        }
        return brush;
    }
}
```

### Regex Safety

Always set timeouts on user-facing regex to prevent catastrophic backtracking:

```csharp
private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
private static readonly Regex PathRegex = new(
    @"[A-Za-z]:\\[^\s""'<>|*?]+",
    RegexOptions.Compiled,
    RegexTimeout);
```

### WPF Rendering Performance

- Use `VirtualizingPanel.IsVirtualizing="True"` on large lists
- Use `VirtualizingPanel.VirtualizationMode="Recycling"` for memory efficiency
- Avoid per-character object allocation in render loops
- Cache `FormattedText` objects when text doesn't change
- Use `ConcurrentDictionary` for async path existence caches in render paths

---

## 12. Security

### Never Hard-Code Credentials

```csharp
// BAD
var apiKey = "sk-abc123...";

// GOOD - read from environment or secure storage
var apiKey = Environment.GetEnvironmentVariable("API_KEY")
    ?? throw new InvalidOperationException("API_KEY environment variable not set");
```

### Never Log Secrets

```csharp
// BAD
FileLog.Write($"Connecting with key={apiKey}");

// GOOD
FileLog.Write($"Connecting with key={apiKey[..4]}...");
```

### Process Execution

- Validate all paths before passing to `Process.Start`
- Never construct shell commands from user input
- Use argument arrays, not string concatenation

---

## 13. Documentation

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

## 14. Project Configuration

### Every Project Must Include

```xml
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

### Build Verification

- Zero warnings policy: all warnings are errors
- Run `dotnet build` before every commit
- Run `dotnet test` before every push

---

## 15. Quick Reference

| Aspect | Rule | Example |
|--------|------|---------|
| Warnings | Treat as errors | `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` |
| Classes | PascalCase + suffix | `SessionManager`, `ConPtyBackend` |
| Methods | PascalCase, Verb+Noun | `CreateSession()`, `SendTextAsync()` |
| Private fields | _camelCase | `_sessionManager`, `_sessions` |
| Async methods | Suffix with Async | `KillSessionAsync()` |
| Null-forgiving (`!`) | Forbidden | Use explicit null checks |
| `.Result` / `.Wait()` | Forbidden | Use `await` instead |
| Fallback catches | Forbidden | Fix root cause |
| Null checks | Pattern matching | `if (x is null)` |
| Validation | Throw early | `ArgumentException.ThrowIfNullOrEmpty()` |
| Logging | FileLog with context | `FileLog.Write($"[Class] message: {param}")` |
| Try-catch | Entry points only | Event handlers, lifecycle methods |
| UI thread | Dispatcher.BeginInvoke | For UI modifications from background |
| `async void` | Event handlers only | Everything else returns `Task` |
| Tests | Required for all public methods | Arrange-Act-Assert pattern |
| Regex | Always set timeout | `TimeSpan.FromMilliseconds(50)` |
| Brushes | Freeze for cross-thread | `brush.Freeze()` |
| Collections | Snapshot before iterating across threads | `.ToList()` |

---

## When in Doubt

1. Log more, not less
2. Fail explicitly, not silently
3. Show feedback immediately
4. Write a test
