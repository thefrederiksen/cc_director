# ConPTY Test Application

Standalone test WPF application to experiment with ConPTY mode without risking the main cc_director app.

## Purpose

- Isolated testing environment for ConPTY terminal functionality
- Test rendering, keyboard input, and process management
- Safe experimentation before integrating into main app

## Running

```
cd tests\ConPtyTest
run.bat
```

Or:
```
cd tests\ConPtyTest
dotnet run
```

## Features

- ConPTY-based terminal emulation
- ANSI/VT100 sequence parsing with color support
- Keyboard input (including special keys and Ctrl combinations)
- Window resize with ConPTY resize
- Scrollback buffer

## Configuration

Working directory is hardcoded in MainWindow.xaml.cs:
```csharp
private const string WorkingDir = @"D:\ReposFred\cc_director";
```

Edit this to change the directory where claude starts.

## Files Structure

```
tests\ConPtyTest\
  ConPty\           - ConPTY wrapper (NativeMethods, PseudoConsole, ProcessHost)
  Memory\           - CircularTerminalBuffer for output buffering
  Controls\         - TerminalControl WPF rendering
  Helpers\          - AnsiParser and TerminalCell
  MainWindow.xaml   - Simple test UI layout
  App.xaml          - WPF app entry point
```
