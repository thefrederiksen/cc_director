using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static ConPtyTest.ConPty.NativeMethods;

namespace ConPtyTest.ConPty;

/// <summary>
/// Managed wrapper around a Windows Pseudo Console (ConPTY).
/// Creates input/output pipes and the pseudo console handle.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>The ConPTY handle.</summary>
    public IntPtr Handle => _handle;

    /// <summary>Write side of the input pipe - send bytes here to feed the console's stdin.</summary>
    public SafeFileHandle InputWriteSide { get; }

    /// <summary>Read side of the output pipe - read bytes here to get the console's stdout.</summary>
    public SafeFileHandle OutputReadSide { get; }

    private PseudoConsole(IntPtr handle, SafeFileHandle inputWriteSide, SafeFileHandle outputReadSide)
    {
        _handle = handle;
        InputWriteSide = inputWriteSide;
        OutputReadSide = outputReadSide;
    }

    /// <summary>
    /// Create a new pseudo console with the given dimensions.
    /// </summary>
    public static PseudoConsole Create(short cols = 120, short rows = 30)
    {
        // Create input pipe pair: we write to inputWriteSide, ConPTY reads from inputReadSide
        if (!CreatePipe(out var inputReadSide, out var inputWriteSide, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe.");

        // Create output pipe pair: ConPTY writes to outputWriteSide, we read from outputReadSide
        if (!CreatePipe(out var outputReadSide, out var outputWriteSide, IntPtr.Zero, 0))
        {
            inputReadSide.Dispose();
            inputWriteSide.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe.");
        }

        var size = new COORD(cols, rows);
        int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var handle);
        if (hr != 0)
        {
            inputReadSide.Dispose();
            inputWriteSide.Dispose();
            outputReadSide.Dispose();
            outputWriteSide.Dispose();
            throw new Win32Exception(hr, $"CreatePseudoConsole failed with HRESULT 0x{hr:X8}.");
        }

        // Critical: Close the sides that CreatePseudoConsole has duplicated internally.
        // Keeping them open prevents EOF from propagating when the console is closed.
        inputReadSide.Dispose();
        outputWriteSide.Dispose();

        return new PseudoConsole(handle, inputWriteSide, outputReadSide);
    }

    /// <summary>Resize the pseudo console.</summary>
    public void Resize(short cols, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int hr = ResizePseudoConsole(_handle, new COORD(cols, rows));
        if (hr != 0)
            throw new Win32Exception(hr, $"ResizePseudoConsole failed with HRESULT 0x{hr:X8}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClosePseudoConsole(_handle);
        _handle = IntPtr.Zero;

        InputWriteSide?.Dispose();
        OutputReadSide?.Dispose();
    }
}
