using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Helpers;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Pure WPF terminal control that renders ANSI terminal output using DrawingVisual.
/// Polls the session buffer via DispatcherTimer and parses VT100 sequences.
/// </summary>
public class TerminalControl : FrameworkElement
{
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int ScrollbackLines = 1000;
    private const double PollIntervalMs = 50;

    private Session? _session;
    private long _bufferPosition;
    private DispatcherTimer? _pollTimer;
    private AnsiParser? _parser;

    // Cell grid
    private TerminalCell[,] _cells;
    private int _cols = DefaultCols;
    private int _rows = DefaultRows;

    // Scrollback
    private readonly List<TerminalCell[]> _scrollback = new();
    private int _scrollOffset; // 0 = bottom (current view), >0 = scrolled up

    // Font metrics
    private double _cellWidth;
    private double _cellHeight;
    private Typeface _typeface;
    private double _fontSize = 14;
    private double _dpiScale = 1.0;

    // Drawing
    private readonly DrawingGroup _backingStore = new();

    public TerminalControl()
    {
        _typeface = new Typeface(new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        _cells = new TerminalCell[DefaultCols, DefaultRows];
        InitializeCells();
        MeasureFontMetrics();

        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public void Attach(Session session)
    {
        Detach();
        _session = session;
        _bufferPosition = 0;
        _scrollOffset = 0;
        _scrollback.Clear();

        RecalculateGridSize();
        InitializeCells();

        _parser = new AnsiParser(_cells, _cols, _rows, _scrollback, ScrollbackLines);

        // Load any existing buffer content
        var (initial, pos) = session.Buffer.GetWrittenSince(0);
        _bufferPosition = pos;
        if (initial.Length > 0)
        {
            _parser.Parse(initial);
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        InvalidateVisual();
    }

    public void Detach()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _parser = null;
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_session == null) return;

        var (data, newPos) = _session.Buffer.GetWrittenSince(_bufferPosition);
        if (data.Length > 0)
        {
            _bufferPosition = newPos;
            _parser?.Parse(data);

            // Auto-scroll to bottom when new data arrives
            if (_scrollOffset > 0)
                _scrollOffset = 0;

            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        bg.Freeze();
        drawingContext.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_parser == null) return;

        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _cols; col++)
            {
                TerminalCell cell;

                if (_scrollOffset > 0)
                {
                    // Virtual index into combined scrollback + current screen
                    int virtualIndex = _scrollback.Count - _scrollOffset + row;

                    if (virtualIndex < 0)
                    {
                        // Scrolled beyond available history
                        cell = default;
                    }
                    else if (virtualIndex < _scrollback.Count)
                    {
                        // This row comes from scrollback
                        var line = _scrollback[virtualIndex];
                        cell = col < line.Length ? line[col] : default;
                    }
                    else
                    {
                        // This row comes from current screen buffer
                        int screenRow = virtualIndex - _scrollback.Count;
                        cell = (screenRow >= 0 && screenRow < _rows)
                            ? _cells[col, screenRow]
                            : default;
                    }
                }
                else
                {
                    // Not scrolled - show current buffer
                    cell = _cells[col, row];
                }

                // Draw background if not default
                if (cell.Background != default && cell.Background != Color.FromRgb(30, 30, 30))
                {
                    var cellBg = new SolidColorBrush(cell.Background);
                    cellBg.Freeze();
                    drawingContext.DrawRectangle(cellBg, null,
                        new Rect(col * _cellWidth, row * _cellHeight, _cellWidth, _cellHeight));
                }

                // Draw character
                char ch = cell.Character;
                if (ch == '\0' || ch == ' ') continue;

                var fg = cell.Foreground == default ? Colors.LightGray : cell.Foreground;
                var brush = new SolidColorBrush(fg);
                brush.Freeze();

                var weight = cell.Bold ? FontWeights.Bold : FontWeights.Normal;
                var style = cell.Italic ? FontStyles.Italic : FontStyles.Normal;
                var tf = new Typeface(_typeface.FontFamily, style, weight, FontStretches.Normal);

                var formattedText = new FormattedText(
                    ch.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    _fontSize,
                    brush,
                    _dpiScale);

                drawingContext.DrawText(formattedText,
                    new Point(col * _cellWidth, row * _cellHeight));
            }
        }

        // Draw cursor (only when visible and not scrolled)
        if (_scrollOffset == 0 && _parser != null && _parser.IsCursorVisible)
        {
            var (cursorCol, cursorRow) = _parser.GetCursorPosition();
            if (cursorCol >= 0 && cursorCol < _cols && cursorRow >= 0 && cursorRow < _rows)
            {
                var cursorBrush = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200));
                cursorBrush.Freeze();
                drawingContext.DrawRectangle(cursorBrush, null,
                    new Rect(cursorCol * _cellWidth, cursorRow * _cellHeight,
                        _cellWidth, _cellHeight));
            }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        int oldCols = _cols;
        int oldRows = _rows;
        RecalculateGridSize();

        if (_cols != oldCols || _rows != oldRows)
        {
            var oldCells = _cells;
            _cells = new TerminalCell[_cols, _rows];
            InitializeCells();

            // Copy existing content
            int copyC = Math.Min(oldCols, _cols);
            int copyR = Math.Min(oldRows, _rows);
            for (int r = 0; r < copyR; r++)
                for (int c = 0; c < copyC; c++)
                    _cells[c, r] = oldCells[c, r];

            _parser?.UpdateGrid(_cells, _cols, _rows);
            _session?.Resize((short)_cols, (short)_rows);
            InvalidateVisual();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;

        byte[]? data = MapKeyToBytes(e.Key, Keyboard.Modifiers);
        if (data != null)
        {
            _session.SendInput(data);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session == null || string.IsNullOrEmpty(e.Text)) return;

        var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
        _session.SendInput(bytes);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int lines = e.Delta > 0 ? 3 : -3;
        _scrollOffset = Math.Max(0, Math.Min(_scrollback.Count, _scrollOffset + lines));
        InvalidateVisual();
        e.Handled = true;
    }

    private void RecalculateGridSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            _cols = DefaultCols;
            _rows = DefaultRows;
            return;
        }

        _cols = Math.Max(10, (int)(ActualWidth / _cellWidth));
        _rows = Math.Max(3, (int)(ActualHeight / _cellHeight));
    }

    private void InitializeCells()
    {
        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
                _cells[c, r] = new TerminalCell();
    }

    private void MeasureFontMetrics()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            _dpiScale = source.CompositionTarget.TransformToDevice.M11;
        else
            _dpiScale = 1.0;

        var formatted = new FormattedText(
            "M",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White,
            _dpiScale);

        _cellWidth = formatted.WidthIncludingTrailingWhitespace;
        _cellHeight = formatted.Height;
    }

    private static byte[]? MapKeyToBytes(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;

        // Ctrl+C
        if (ctrl && key == Key.C) return new byte[] { 0x03 };
        // Ctrl+D
        if (ctrl && key == Key.D) return new byte[] { 0x04 };
        // Ctrl+Z
        if (ctrl && key == Key.Z) return new byte[] { 0x1A };
        // Ctrl+L
        if (ctrl && key == Key.L) return new byte[] { 0x0C };

        return key switch
        {
            Key.Enter => "\r"u8.ToArray(),
            Key.Back => new byte[] { 0x7F },
            Key.Tab => "\t"u8.ToArray(),
            Key.Escape => new byte[] { 0x1B },
            Key.Up => "\x1b[A"u8.ToArray(),
            Key.Down => "\x1b[B"u8.ToArray(),
            Key.Right => "\x1b[C"u8.ToArray(),
            Key.Left => "\x1b[D"u8.ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            Key.PageUp => "\x1b[5~"u8.ToArray(),
            Key.PageDown => "\x1b[6~"u8.ToArray(),
            Key.Insert => "\x1b[2~"u8.ToArray(),
            Key.F1 => "\x1bOP"u8.ToArray(),
            Key.F2 => "\x1bOQ"u8.ToArray(),
            Key.F3 => "\x1bOR"u8.ToArray(),
            Key.F4 => "\x1bOS"u8.ToArray(),
            Key.F5 => "\x1b[15~"u8.ToArray(),
            Key.F6 => "\x1b[17~"u8.ToArray(),
            Key.F7 => "\x1b[18~"u8.ToArray(),
            Key.F8 => "\x1b[19~"u8.ToArray(),
            Key.F9 => "\x1b[20~"u8.ToArray(),
            Key.F10 => "\x1b[21~"u8.ToArray(),
            Key.F11 => "\x1b[23~"u8.ToArray(),
            Key.F12 => "\x1b[24~"u8.ToArray(),
            _ => null
        };
    }
}
