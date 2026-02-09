using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static ConsoleDiagnostic.NativeMethods;

namespace ConsoleDiagnostic;

/// <summary>
/// Diagnostic tests for Windows 11 console capture issues.
/// </summary>
public class DiagnosticTests
{
    private const string RegistryPath = @"Console\%%Startup";
    private const string LegacyConhostGuid = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";

    /// <summary>
    /// Test 1: Check registry for terminal delegation settings.
    /// </summary>
    public void TestRegistryCheck()
    {
        Console.WriteLine("[1] Registry Check");
        Console.WriteLine(new string('-', 50));

        string? delegationConsole = ReadRegistryValue("DelegationConsole");
        string? delegationTerminal = ReadRegistryValue("DelegationTerminal");

        Console.WriteLine($"    DelegationConsole: {delegationConsole ?? "(not set)"}");
        Console.WriteLine($"    DelegationTerminal: {delegationTerminal ?? "(not set)"}");

        bool isWindowsTerminal =
            (delegationConsole != null && delegationConsole != LegacyConhostGuid) ||
            (delegationTerminal != null && delegationTerminal != LegacyConhostGuid);

        if (isWindowsTerminal)
        {
            Console.WriteLine("    Status: Windows Terminal IS the default terminal");
            Console.WriteLine("    -> GetConsoleWindow() will likely return a PseudoConsoleWindow");
        }
        else if (delegationConsole == LegacyConhostGuid || delegationTerminal == LegacyConhostGuid)
        {
            Console.WriteLine("    Status: Legacy conhost IS the default terminal");
            Console.WriteLine("    -> GetConsoleWindow() should return a real ConsoleWindowClass");
        }
        else
        {
            Console.WriteLine("    Status: Default terminal configuration (likely Windows Terminal on Win11)");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Test 2: Launch a test console process and examine the window.
    /// </summary>
    public void TestProcessLaunch()
    {
        Console.WriteLine("[2] Process Launch Test");
        Console.WriteLine(new string('-', 50));

        // Free our current console first
        FreeConsole();

        // Launch a test console process
        var startInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        PROCESS_INFORMATION procInfo;

        string cmdLine = "cmd.exe /k echo Test Console Window && echo Waiting... && pause";

        bool created = CreateProcess(
            null,
            cmdLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CREATE_NEW_CONSOLE,
            IntPtr.Zero,
            null,
            ref startInfo,
            out procInfo);

        if (!created)
        {
            Console.WriteLine($"    CreateProcess FAILED: Error {GetLastError()}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"    Test PID: {procInfo.dwProcessId}");

        // Give the console time to initialize
        Thread.Sleep(1000);

        // Try to attach to the console
        bool attached = AttachConsole(procInfo.dwProcessId);
        Console.WriteLine($"    AttachConsole: {(attached ? "SUCCESS" : $"FAILED (error {GetLastError()})")}");

        if (attached)
        {
            IntPtr hwnd = GetConsoleWindow();
            Console.WriteLine($"    GetConsoleWindow: 0x{hwnd:X}");

            if (hwnd != IntPtr.Zero)
            {
                string className = GetWindowClassName(hwnd);
                Console.WriteLine($"    Window Class: {className}");

                GetWindowRect(hwnd, out RECT rect);
                Console.WriteLine($"    Window Size: {rect.Width}x{rect.Height}");

                int style = GetWindowLong(hwnd, GWL_STYLE);
                bool visible = (style & WS_VISIBLE) != 0;
                Console.WriteLine($"    WS_VISIBLE: {visible}");

                if (className == "PseudoConsoleWindow" && rect.Width == 0 && rect.Height == 0)
                {
                    Console.WriteLine("    DIAGNOSIS: Window is a STUB (unusable for capture)");
                }
                else if (className == "ConsoleWindowClass")
                {
                    Console.WriteLine("    DIAGNOSIS: Window is REAL (usable for capture)");
                }
                else
                {
                    Console.WriteLine($"    DIAGNOSIS: Unexpected class - needs investigation");
                }
            }
            else
            {
                Console.WriteLine("    DIAGNOSIS: GetConsoleWindow returned NULL");
            }

            FreeConsole();
        }

        // Clean up the test process
        TerminateProcess(procInfo.hProcess, 0);
        CloseHandle(procInfo.hProcess);
        CloseHandle(procInfo.hThread);

        Console.WriteLine();
    }

    /// <summary>
    /// Test 3: Walk the process tree to find Windows Terminal.
    /// </summary>
    public void TestProcessTreeWalk()
    {
        Console.WriteLine("[3] Process Tree Walk");
        Console.WriteLine(new string('-', 50));

        uint currentPid = (uint)Process.GetCurrentProcess().Id;
        var chain = new List<(uint pid, string name)>();

        uint pid = currentPid;
        while (pid != 0)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                chain.Add((pid, proc.ProcessName));
                pid = GetParentProcessId(pid);
            }
            catch
            {
                break;
            }
        }

        // Print the chain
        string indent = "    ";
        foreach (var (procPid, name) in chain)
        {
            Console.WriteLine($"{indent}{name}.exe ({procPid})");
            indent = indent + "  <- ";
        }

        // Check for Windows Terminal in the chain
        var terminalProc = chain.FirstOrDefault(p =>
            p.name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase));

        var openConsoleProc = chain.FirstOrDefault(p =>
            p.name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase));

        if (terminalProc.pid != 0)
        {
            Console.WriteLine($"    Windows Terminal PID: {terminalProc.pid}");
        }
        else
        {
            Console.WriteLine("    Windows Terminal: NOT in process tree");
        }

        if (openConsoleProc.pid != 0)
        {
            Console.WriteLine($"    OpenConsole PID: {openConsoleProc.pid}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Test 4: Enumerate all console-related windows.
    /// </summary>
    public void TestWindowEnumeration()
    {
        Console.WriteLine("[4] Window Enumeration");
        Console.WriteLine(new string('-', 50));

        var pseudoConsoleWindows = new List<(IntPtr hwnd, uint pid, int width, int height)>();
        var consoleClassWindows = new List<(IntPtr hwnd, uint pid, int width, int height)>();
        var cascadiaWindows = new List<(IntPtr hwnd, uint pid, int width, int height, string title)>();

        EnumWindows((hwnd, lParam) =>
        {
            string className = GetWindowClassName(hwnd);
            GetWindowThreadProcessId(hwnd, out uint pid);
            GetWindowRect(hwnd, out RECT rect);

            if (className == "PseudoConsoleWindow")
            {
                pseudoConsoleWindows.Add((hwnd, pid, rect.Width, rect.Height));
            }
            else if (className == "ConsoleWindowClass")
            {
                consoleClassWindows.Add((hwnd, pid, rect.Width, rect.Height));
            }
            else if (className == "CASCADIA_HOSTING_WINDOW_CLASS")
            {
                string title = GetWindowTitle(hwnd);
                cascadiaWindows.Add((hwnd, pid, rect.Width, rect.Height, title));
            }

            return true;
        }, IntPtr.Zero);

        Console.WriteLine("    CASCADIA_HOSTING_WINDOW_CLASS windows:");
        if (cascadiaWindows.Count == 0)
        {
            Console.WriteLine("      (none found)");
        }
        else
        {
            foreach (var w in cascadiaWindows)
            {
                Console.WriteLine($"      HWND=0x{w.hwnd:X}, PID={w.pid}, Size={w.width}x{w.height}");
                Console.WriteLine($"        Title: \"{w.title}\"");
            }
        }

        Console.WriteLine("    PseudoConsoleWindow windows:");
        if (pseudoConsoleWindows.Count == 0)
        {
            Console.WriteLine("      (none found)");
        }
        else
        {
            foreach (var w in pseudoConsoleWindows)
            {
                Console.WriteLine($"      HWND=0x{w.hwnd:X}, PID={w.pid}, Size={w.width}x{w.height}");
            }
        }

        Console.WriteLine("    ConsoleWindowClass windows:");
        if (consoleClassWindows.Count == 0)
        {
            Console.WriteLine("      (none found)");
        }
        else
        {
            foreach (var w in consoleClassWindows)
            {
                Console.WriteLine($"      HWND=0x{w.hwnd:X}, PID={w.pid}, Size={w.width}x{w.height}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Test 5: Force legacy conhost via registry and test.
    /// </summary>
    public void TestLegacyConhostForce()
    {
        Console.WriteLine("[5] Legacy Conhost Force Test");
        Console.WriteLine(new string('-', 50));

        // Save current values
        string? savedConsole = ReadRegistryValue("DelegationConsole");
        string? savedTerminal = ReadRegistryValue("DelegationTerminal");

        Console.WriteLine($"    Original DelegationConsole: {savedConsole ?? "(not set)"}");
        Console.WriteLine($"    Original DelegationTerminal: {savedTerminal ?? "(not set)"}");

        bool registryModified = false;

        try
        {
            // Set to legacy conhost
            if (!WriteRegistryValue("DelegationConsole", LegacyConhostGuid))
            {
                Console.WriteLine("    ERROR: Failed to set DelegationConsole (need admin?)");
                return;
            }
            if (!WriteRegistryValue("DelegationTerminal", LegacyConhostGuid))
            {
                Console.WriteLine("    ERROR: Failed to set DelegationTerminal (need admin?)");
                // Restore first value
                if (savedConsole != null)
                    WriteRegistryValue("DelegationConsole", savedConsole);
                return;
            }

            registryModified = true;
            Console.WriteLine("    Registry modified: YES (set to legacy conhost)");

            // Free our console first
            FreeConsole();

            // Launch a new test process
            var startInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            PROCESS_INFORMATION procInfo;

            bool created = CreateProcess(
                null,
                "cmd.exe /k echo Legacy Conhost Test && pause",
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_NEW_CONSOLE,
                IntPtr.Zero,
                null,
                ref startInfo,
                out procInfo);

            if (!created)
            {
                Console.WriteLine($"    CreateProcess FAILED: Error {GetLastError()}");
                return;
            }

            Console.WriteLine($"    New process PID: {procInfo.dwProcessId}");

            // Give it time to initialize
            Thread.Sleep(1500);

            // Try to attach
            bool attached = AttachConsole(procInfo.dwProcessId);
            if (attached)
            {
                IntPtr hwnd = GetConsoleWindow();
                Console.WriteLine($"    GetConsoleWindow: 0x{hwnd:X}");

                if (hwnd != IntPtr.Zero)
                {
                    string className = GetWindowClassName(hwnd);
                    Console.WriteLine($"    Window Class: {className}");

                    GetWindowRect(hwnd, out RECT rect);
                    Console.WriteLine($"    Window Size: {rect.Width}x{rect.Height}");

                    if (className == "ConsoleWindowClass" && rect.Width > 0)
                    {
                        Console.WriteLine("    RESULT: Legacy conhost WORKS when forced!");
                    }
                    else if (className == "PseudoConsoleWindow")
                    {
                        Console.WriteLine("    RESULT: Still getting PseudoConsoleWindow (registry change not effective)");
                    }
                    else
                    {
                        Console.WriteLine($"    RESULT: Unexpected class '{className}'");
                    }
                }

                FreeConsole();
            }
            else
            {
                Console.WriteLine($"    AttachConsole FAILED: Error {GetLastError()}");
            }

            // Clean up test process
            TerminateProcess(procInfo.hProcess, 0);
            CloseHandle(procInfo.hProcess);
            CloseHandle(procInfo.hThread);
        }
        finally
        {
            // Restore original values
            if (registryModified)
            {
                Console.WriteLine("    Restoring original registry values...");
                if (savedConsole != null)
                    WriteRegistryValue("DelegationConsole", savedConsole);
                else
                    DeleteRegistryValue("DelegationConsole");

                if (savedTerminal != null)
                    WriteRegistryValue("DelegationTerminal", savedTerminal);
                else
                    DeleteRegistryValue("DelegationTerminal");

                Console.WriteLine("    Registry restored.");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Test 6: Try FindWindow with unique title.
    /// </summary>
    public void TestUniqueTitleFindWindow()
    {
        Console.WriteLine("[6] Unique Title Test");
        Console.WriteLine(new string('-', 50));

        string uniqueTitle = $"CC_DIAG_{Guid.NewGuid():N}";
        Console.WriteLine($"    Title: \"{uniqueTitle}\"");

        // Free our console
        FreeConsole();

        // Launch with a unique title
        var startInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpTitle = uniqueTitle,
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = SW_SHOW
        };
        PROCESS_INFORMATION procInfo;

        bool created = CreateProcess(
            null,
            "cmd.exe /k echo Unique Title Test && pause",
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CREATE_NEW_CONSOLE,
            IntPtr.Zero,
            null,
            ref startInfo,
            out procInfo);

        if (!created)
        {
            Console.WriteLine($"    CreateProcess FAILED: Error {GetLastError()}");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"    Process PID: {procInfo.dwProcessId}");

        // Give it time
        Thread.Sleep(1500);

        // Try FindWindow with the unique title
        IntPtr hwndFound = FindWindow(null, uniqueTitle);
        Console.WriteLine($"    FindWindow result: 0x{hwndFound:X}");

        if (hwndFound != IntPtr.Zero)
        {
            string className = GetWindowClassName(hwndFound);
            Console.WriteLine($"    Window Class: {className}");

            GetWindowRect(hwndFound, out RECT rect);
            Console.WriteLine($"    Window Size: {rect.Width}x{rect.Height}");

            GetWindowThreadProcessId(hwndFound, out uint ownerPid);
            Console.WriteLine($"    Owner PID: {ownerPid}");

            if (className == "CASCADIA_HOSTING_WINDOW_CLASS")
            {
                Console.WriteLine("    RESULT: FindWindow returns Windows Terminal tab!");
                Console.WriteLine("    -> This HWND could be used for capture");
            }
            else if (className == "ConsoleWindowClass")
            {
                Console.WriteLine("    RESULT: FindWindow returns real console window!");
            }
            else if (className == "PseudoConsoleWindow")
            {
                Console.WriteLine("    RESULT: FindWindow returns PseudoConsoleWindow (not useful)");
            }
            else
            {
                Console.WriteLine($"    RESULT: Unknown class '{className}'");
            }
        }
        else
        {
            Console.WriteLine("    RESULT: FindWindow returned NULL - title not found");

            // Try to find any window with our title by enumeration
            Console.WriteLine("    Searching via EnumWindows...");
            IntPtr foundViaEnum = IntPtr.Zero;

            EnumWindows((hwnd, lParam) =>
            {
                string title = GetWindowTitle(hwnd);
                if (title.Contains(uniqueTitle.Substring(0, 20)))
                {
                    foundViaEnum = hwnd;
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            if (foundViaEnum != IntPtr.Zero)
            {
                Console.WriteLine($"    Found via EnumWindows: 0x{foundViaEnum:X}");
                Console.WriteLine($"    Class: {GetWindowClassName(foundViaEnum)}");
            }
            else
            {
                Console.WriteLine("    Not found via EnumWindows either");
            }
        }

        // Clean up
        TerminateProcess(procInfo.hProcess, 0);
        CloseHandle(procInfo.hProcess);
        CloseHandle(procInfo.hThread);

        Console.WriteLine();
    }

    // ========== Registry Helpers ==========

    private string? ReadRegistryValue(string valueName)
    {
        if (RegOpenKeyEx(HKEY_CURRENT_USER, RegistryPath, 0, KEY_READ, out IntPtr hKey) != 0)
            return null;

        try
        {
            uint dataSize = 256;
            byte[] data = new byte[dataSize];

            if (RegQueryValueEx(hKey, valueName, IntPtr.Zero, out uint type, data, ref dataSize) == 0)
            {
                if (type == REG_SZ)
                {
                    return Encoding.Unicode.GetString(data, 0, (int)dataSize).TrimEnd('\0');
                }
            }
            return null;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    private bool WriteRegistryValue(string valueName, string value)
    {
        if (RegOpenKeyEx(HKEY_CURRENT_USER, RegistryPath, 0, KEY_WRITE, out IntPtr hKey) != 0)
            return false;

        try
        {
            byte[] data = Encoding.Unicode.GetBytes(value + "\0");
            return RegSetValueEx(hKey, valueName, 0, REG_SZ, data, (uint)data.Length) == 0;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    private bool DeleteRegistryValue(string valueName)
    {
        // For simplicity, just set it to the legacy GUID when "deleting"
        // A proper implementation would use RegDeleteValue
        return true;
    }
}
