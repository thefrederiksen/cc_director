# CC Director — High-Level Implementation

## Completed

### Session Management
- Create multiple concurrent Claude Code sessions targeting different repos
- All sessions use Embedded mode (native console overlay); ConPTY/Pipe modes exist in code but are unused
- Session lifecycle tracking: Starting, Running, Exiting, Exited, Failed
- Kill or close individual sessions from UI
- Switch between sessions without restarting processes (hide/show overlay)
- Logs orphaned claude.exe processes on startup (detection only, no reattach or kill)

### Claude Code Hook Integration
- Auto-install 14 hook types into ~/.claude/settings.json on startup
- Named pipe server receives JSON hook events from Claude Code via PowerShell relay
- Event routing: maps Claude session_id to Director session by ID or working directory
- Activity state tracking: Starting, Idle, Working, WaitingForInput, WaitingForPerm, Exited

### Embedded Console Hosting
- Spawn claude.exe in native console window, overlay as borderless child of WPF window
- Z-order management via owner relationship
- Text injection via WriteConsoleInput (fallback: clipboard paste)
- Show/hide without killing process

### Terminal Display
- Native console window rendered as borderless overlay inside the WPF window
- ConPTY/Pipe modes with WPF ANSI terminal control exist in codebase but are unused

### Git Integration
- Async git status polling per session repo
- Staged/unstaged change display with status color coding

### UI
- 3-panel layout: sessions sidebar, terminal + git tabs, pipe messages
- Activity state color indicators per session
- Prompt input bar for sending text to active session
- Pipe message viewer (last 500 events) with event type coloring
- New Session dialog with repo picker and registry persistence

### Configuration
- Repository registry persisted to ~/Documents/CcDirector/repositories.json
- Claude path, default args, buffer size, shutdown timeout via appsettings.json

---

## Outstanding — Required for Daily Use

### Session Persistence (not started)
- Detach from running console windows on shutdown without killing them
- Reattach to existing console windows on startup
- Single-instance enforcement so only one Director manages the windows
- "Shutdown & Keep Sessions" mode — consoles become normal visible windows
- "Shutdown & Kill All" mode — clean exit killing all consoles
- Kill individual console window from UI

### Permission Handling (not started)
- UI to approve/deny permission requests from Claude Code hooks

### Session Naming (not started)
- User-defined session names instead of folder-name-only

### Prompt History (not started)
- Recall previously sent prompts per session

---

## Outstanding — Nice to Have

### Subagent Visualization
- Show subagent start/stop events in a tree or timeline view

### Tool Execution Display
- Render tool name, input, and response from hook events

### Task Tracking
- Surface TaskCompleted events as a progress dashboard

### Multi-Monitor Support
- Remember and restore console overlay positions per monitor layout

### Transcript Viewer
- Load and display Claude Code transcript files linked from hook events

---

## Go-Live Gate

**I will switch from VS Code to CC Director when Session Persistence is complete.**

That means: I can close CC Director, reopen it, and all my Claude sessions are still running and reconnected — no lost state, no restarted conversations.
