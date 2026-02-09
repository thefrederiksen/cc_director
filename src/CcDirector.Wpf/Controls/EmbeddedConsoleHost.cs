using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Manages a console process as a borderless overlay window positioned over
/// the WPF terminal area. Does NOT reparent (SetParent) — the console stays
/// top-level so keyboard input works naturally.
/// </summary>
public class EmbeddedConsoleHost : IDisposable
{
    private static readonly ConcurrentDictionary<EmbeddedConsoleHost, byte> _allInstances = new();

    public static IReadOnlyCollection<EmbeddedConsoleHost> ActiveInstances =>
        _allInstances.Keys.ToArray();

    public static void DisposeAll()
    {
        foreach (var instance in _allInstances.Keys)
        {
            try { instance.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[EmbeddedConsoleHost] DisposeAll error: {ex.Message}"); }
        }
    }

    private IntPtr _consoleHwnd;
    private Process? _process;
    private bool _disposed;
    private bool _visible;

    public int ProcessId => _process?.Id ?? 0;
    public bool HasExited => _process == null || _process.HasExited;
    public IntPtr ConsoleHwnd => _consoleHwnd;

    public event Action<int>? OnProcessExited;

    /// <summary>
    /// Spawn the console process, find its window handle, and strip borders.
    /// </summary>
    public void StartProcess(string exe, string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) =>
        {
            int code = 0;
            try { code = _process.ExitCode; } catch { }
            Application.Current?.Dispatcher.BeginInvoke(() => OnProcessExited?.Invoke(code));
        };
        _process.Start();
        _allInstances.TryAdd(this, 0);

        Debug.WriteLine($"[EmbeddedConsoleHost] Process started, PID={_process.Id}");

        _consoleHwnd = WaitForConsoleWindow(_process, TimeSpan.FromSeconds(5));

        if (_consoleHwnd == IntPtr.Zero)
        {
            Debug.WriteLine("[EmbeddedConsoleHost] Could not find console window handle");
            return;
        }

        Debug.WriteLine($"[EmbeddedConsoleHost] Found console hwnd=0x{_consoleHwnd:X}");
        StripBorders(_consoleHwnd);
        _visible = true;
    }

    /// <summary>
    /// Remove title bar, borders, and taskbar presence from the console window.
    /// </summary>
    private static void StripBorders(IntPtr hwnd)
    {
        // Remove title bar, resize borders, and scrollbar
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_VSCROLL | WS_HSCROLL);
        SetWindowLong(hwnd, GWL_STYLE, style);

        // Set WS_EX_TOOLWINDOW to hide from taskbar and alt-tab
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_APPWINDOW);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Force the window to redraw with new styles
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Set the WPF window as the owner of the console window so the console
    /// always stays above the WPF window in Z-order.
    /// </summary>
    public void SetOwner(IntPtr ownerHwnd)
    {
        if (_consoleHwnd != IntPtr.Zero && ownerHwnd != IntPtr.Zero)
        {
            SetWindowLong(_consoleHwnd, GWL_HWNDPARENT, (int)ownerHwnd);
        }
    }

    /// <summary>
    /// Position the console window to cover the given screen-space rectangle.
    /// Uses SetWindowPos with HWND_TOP to maintain Z-order above the WPF window.
    /// </summary>
    public void UpdatePosition(Rect screenRect)
    {
        if (_consoleHwnd == IntPtr.Zero || !IsWindow(_consoleHwnd)) return;

        SetWindowPos(_consoleHwnd, HWND_TOP,
            (int)screenRect.X, (int)screenRect.Y,
            (int)screenRect.Width, (int)screenRect.Height,
            SWP_SHOWWINDOW | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Send text to the console using a two-tier approach:
    /// Tier 1: Unicode WriteConsoleInput (fast, layout-independent)
    /// Tier 2: Clipboard paste via Ctrl+V (fallback)
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        if (_process == null || _process.HasExited)
        {
            Debug.WriteLine("[EmbeddedConsoleHost] SendTextAsync: process not running");
            return;
        }

        if (await SendTextViaWriteConsoleInput(text))
        {
            Debug.WriteLine("[EmbeddedConsoleHost] Tier 1 (Unicode WriteConsoleInput) succeeded");
            return;
        }

        Debug.WriteLine("[EmbeddedConsoleHost] Tier 1 failed, falling back to Tier 2 (clipboard paste)");
        await SendTextViaClipboardPaste(text);
    }

    /// <summary>
    /// Tier 1: Inject text via WriteConsoleInput using pure Unicode key events.
    /// Uses UnicodeChar field only (VK=0, SC=0) so keyboard layout is irrelevant.
    /// Text and Enter are sent in separate batches with a delay so Claude Code's
    /// TUI doesn't treat the Enter as a newline within a paste operation.
    /// Returns true on success.
    /// </summary>
    private async Task<bool> SendTextViaWriteConsoleInput(string text)
    {
        FreeConsole();
        if (!AttachConsole((uint)_process!.Id))
        {
            Debug.WriteLine($"[EmbeddedConsoleHost] Tier1: AttachConsole failed, error={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
            if (hInput == IntPtr.Zero || hInput == INVALID_HANDLE_VALUE)
            {
                Debug.WriteLine("[EmbeddedConsoleHost] Tier1: GetStdHandle failed");
                return false;
            }

            // --- Batch 1: text characters ---
            var textRecords = new List<INPUT_RECORD>();
            foreach (char c in text)
            {
                textRecords.Add(MakeUnicodeKeyEvent(c, true));
                textRecords.Add(MakeUnicodeKeyEvent(c, false));
            }

            var textArr = textRecords.ToArray();
            if (!WriteConsoleInput(hInput, textArr, (uint)textArr.Length, out uint textWritten))
            {
                Debug.WriteLine($"[EmbeddedConsoleHost] Tier1: WriteConsoleInput (text) failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }
            Debug.WriteLine($"[EmbeddedConsoleHost] Tier1: wrote {textWritten}/{textArr.Length} text records");

            // Detach before awaiting so we don't hold the console across the delay
            FreeConsole();

            // Delay so the TUI processes the text as a complete paste before Enter arrives
            await Task.Delay(100);

            // Re-attach for the Enter key
            if (!AttachConsole((uint)_process!.Id))
            {
                Debug.WriteLine($"[EmbeddedConsoleHost] Tier1: re-AttachConsole failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            hInput = GetStdHandle(STD_INPUT_HANDLE);
            if (hInput == IntPtr.Zero || hInput == INVALID_HANDLE_VALUE)
            {
                Debug.WriteLine("[EmbeddedConsoleHost] Tier1: GetStdHandle (Enter) failed");
                return false;
            }

            // --- Batch 2: Enter key ---
            var enterRecords = new INPUT_RECORD[]
            {
                MakeKeyEvent('\r', VK_RETURN, true),
                MakeKeyEvent('\r', VK_RETURN, false),
            };

            if (!WriteConsoleInput(hInput, enterRecords, (uint)enterRecords.Length, out uint enterWritten))
            {
                Debug.WriteLine($"[EmbeddedConsoleHost] Tier1: WriteConsoleInput (Enter) failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }
            Debug.WriteLine($"[EmbeddedConsoleHost] Tier1: wrote {enterWritten}/{enterRecords.Length} Enter records");

            return textWritten == (uint)textArr.Length && enterWritten == (uint)enterRecords.Length;
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>
    /// Tier 2: Paste text via clipboard + simulated Ctrl+V keystroke.
    /// Saves/restores the clipboard and returns WPF focus afterward.
    /// </summary>
    private async Task SendTextViaClipboardPaste(string text)
    {
        // Must run clipboard operations on STA thread
        string? savedClipboard = null;
        bool hadText = false;

        try
        {
            // Save current clipboard
            if (Clipboard.ContainsText())
            {
                hadText = true;
                savedClipboard = Clipboard.GetText();
            }

            // Set our text + Enter (newline triggers submit)
            Clipboard.SetText(text + "\r\n");

            // Bring the console window to front for SendInput to target it
            IntPtr prevForeground = GetForegroundWindow();
            SetForegroundWindow(_consoleHwnd);

            // Small delay for window activation
            await Task.Delay(50);

            // Simulate Ctrl+V
            SimulateCtrlV();

            // Wait for paste to be processed
            await Task.Delay(100);

            // Restore WPF window focus
            if (prevForeground != IntPtr.Zero)
                SetForegroundWindow(prevForeground);
        }
        finally
        {
            // Restore clipboard
            try
            {
                if (hadText && savedClipboard != null)
                    Clipboard.SetText(savedClipboard);
                else if (!hadText)
                    Clipboard.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EmbeddedConsoleHost] Tier2: clipboard restore failed: {ex.Message}");
            }
        }
    }

    private static void SimulateCtrlV()
    {
        var inputs = new SENDINPUT[]
        {
            // Ctrl down
            MakeKeyboardInput(VK_CONTROL, 0),
            // V down
            MakeKeyboardInput(VK_V, 0),
            // V up
            MakeKeyboardInput(VK_V, KEYEVENTF_KEYUP),
            // Ctrl up
            MakeKeyboardInput(VK_CONTROL, KEYEVENTF_KEYUP),
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<SENDINPUT>());
        Debug.WriteLine($"[EmbeddedConsoleHost] SimulateCtrlV: sent {sent}/{inputs.Length} inputs");
    }

    private static SENDINPUT MakeKeyboardInput(ushort vk, uint flags)
    {
        return new SENDINPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
    }

    /// <summary>Show the console window and force it above the WPF owner.</summary>
    public void Show()
    {
        if (_consoleHwnd == IntPtr.Zero || !IsWindow(_consoleHwnd)) return;

        ShowWindow(_consoleHwnd, SW_SHOWNOACTIVATE);

        // "TOPMOST flash": briefly set TOPMOST to force above everything,
        // then revert to NOTOPMOST so the owner relationship keeps Z-order correct.
        // Plain HWND_TOP can lose a race with WPF's own activation Z-order management.
        SetWindowPos(_consoleHwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
        SetWindowPos(_consoleHwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);

        _visible = true;
    }

    /// <summary>Hide the console window.</summary>
    public void Hide()
    {
        if (_consoleHwnd != IntPtr.Zero)
        {
            ShowWindow(_consoleHwnd, SW_HIDE);
            _visible = false;
        }
    }

    public bool IsVisible => _visible;

    public void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EmbeddedConsoleHost] Kill error: {ex.Message}");
        }

        _process?.Dispose();
        _process = null;
        _consoleHwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _allInstances.TryRemove(this, out _);
        KillProcess();
    }

    private static IntPtr WaitForConsoleWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return IntPtr.Zero;

            FreeConsole();

            if (AttachConsole((uint)process.Id))
            {
                IntPtr hwnd = GetConsoleWindow();
                FreeConsole();
                if (hwnd != IntPtr.Zero)
                {
                    Debug.WriteLine($"[EmbeddedConsoleHost] AttachConsole succeeded, hwnd=0x{hwnd:X}");
                    return hwnd;
                }
            }

            Thread.Sleep(100);
        }

        Debug.WriteLine("[EmbeddedConsoleHost] Timed out waiting for console window");
        return IntPtr.Zero;
    }

    private static INPUT_RECORD MakeKeyEvent(char c, byte virtualKey, bool keyDown)
    {
        return new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown,
                wRepeatCount = 1,
                wVirtualKeyCode = virtualKey,
                wVirtualScanCode = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC),
                UnicodeChar = c,
                dwControlKeyState = 0,
            }
        };
    }

    private static INPUT_RECORD MakeUnicodeKeyEvent(char c, bool keyDown)
    {
        return new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown,
                wRepeatCount = 1,
                wVirtualKeyCode = 0,
                wVirtualScanCode = 0,
                UnicodeChar = c,
                dwControlKeyState = 0,
            }
        };
    }

    // --- Win32 constants ---

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_VSCROLL = 0x00200000;
    private const int WS_HSCROLL = 0x00100000;
    private const int WS_EX_WINDOWEDGE = 0x00000100;
    private const int WS_EX_CLIENTEDGE = 0x00000200;
    private const int WS_EX_DLGMODALFRAME = 0x00000001;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int GWL_HWNDPARENT = -8;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020;
    private const int SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const ushort KEY_EVENT = 0x0001;
    private const byte VK_RETURN = 0x0D;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int INPUT_KEYBOARD = 1;

    // --- kernel32 P/Invoke ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInput(
        IntPtr hConsoleInput,
        INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    // --- user32 P/Invoke ---

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int cx, int cy, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, SENDINPUT[] pInputs, int cbSize);

    // --- Structs ---

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        [FieldOffset(0)] public bool bKeyDown;
        [FieldOffset(4)] public ushort wRepeatCount;
        [FieldOffset(6)] public ushort wVirtualKeyCode;
        [FieldOffset(8)] public ushort wVirtualScanCode;
        [FieldOffset(10)] public char UnicodeChar;
        [FieldOffset(12)] public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SENDINPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
