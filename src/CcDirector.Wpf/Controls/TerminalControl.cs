using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
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

    // Link detection patterns with 50ms timeout to prevent catastrophic backtracking
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex AbsoluteWindowsPathRegex = new(@"[A-Za-z]:[/\\][^\s""'<>|*?()\[\]]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex AbsoluteUnixPathRegex = new(@"/[a-z]/[^\s""'<>|*?()\[\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex RelativePathRegex = new(@"\.{0,2}/[^\s""'<>|*?:()\[\]]+|[A-Za-z_][A-Za-z0-9_\-]*/[^\s""'<>|*?:()\[\]]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex UrlRegex = new(@"https?://[^\s""'<>]+|git@[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // Cached typefaces - avoid creating new Typeface per character (4 variants for normal/bold/italic combinations)
    private static readonly FontFamily _fontFamily = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface _typefaceNormal = new(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface _typefaceBold = new(_fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface _typefaceItalic = new(_fontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface _typefaceBoldItalic = new(_fontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

    // Cached brushes - avoid creating new SolidColorBrush per character
    private static readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
    private static readonly object _brushCacheLock = new();

    private static SolidColorBrush GetCachedBrush(Color color)
    {
        lock (_brushCacheLock)
        {
            if (!_brushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                _brushCache[color] = brush;
            }
            return brush;
        }
    }

    private static Typeface GetCachedTypeface(bool bold, bool italic)
    {
        if (bold && italic) return _typefaceBoldItalic;
        if (bold) return _typefaceBold;
        if (italic) return _typefaceItalic;
        return _typefaceNormal;
    }

    private enum LinkType { None, Path, Url }

    // Link region for hover detection
    private readonly record struct LinkRegion(Rect Bounds, string Text, LinkType Type);
    private readonly List<LinkRegion> _linkRegions = new();

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
    private bool _userScrolled; // True when user has manually scrolled up (prevents auto-scroll)

    // Selection state
    private bool _isSelecting;
    private (int col, int row) _selectionStart;  // Anchor point (where mouse down occurred)
    private (int col, int row) _selectionEnd;    // Current drag point
    private bool _hasSelection;

    // Link detection state
    private ContextMenu? _linkContextMenu;
    private string? _detectedLink;
    private LinkType _detectedLinkType;

    // Path existence cache - avoids disk I/O in OnRender
    private readonly ConcurrentDictionary<string, bool> _pathExistsCache = new();
    private int _pathCacheInvalidateNeeded;

    /// <summary>Raised when scroll position or scrollback changes.</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Number of lines scrolled up from bottom. 0 = current view.</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int clamped = Math.Max(0, Math.Min(_scrollback.Count, value));
            if (_scrollOffset != clamped)
            {
                _scrollOffset = clamped;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Total number of lines in scrollback buffer.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Number of visible rows in the viewport.</summary>
    public int ViewportRows => _rows;

    /// <summary>Total number of lines (scrollback + current screen) - includes empty rows.</summary>
    public int TotalLineCount => _scrollback.Count + _rows;

    /// <summary>
    /// Count of lines with actual content (non-empty lines).
    /// Used for terminal verification to determine when we have enough content.
    /// </summary>
    public int ContentLineCount
    {
        get
        {
            int count = _scrollback.Count; // All scrollback lines have content

            // Count non-empty rows in current screen buffer
            for (int row = 0; row < _rows; row++)
            {
                bool hasContent = false;
                for (int col = 0; col < _cols; col++)
                {
                    char ch = _cells[col, row].Character;
                    if (ch != '\0' && ch != ' ')
                    {
                        hasContent = true;
                        break;
                    }
                }
                if (hasContent)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Get all terminal text (scrollback + current screen) as a single string.
    /// Used for terminal-to-JSONL verification.
    /// </summary>
    public string GetAllTerminalText()
    {
        var sb = new StringBuilder();

        // First, add all scrollback lines
        foreach (var line in _scrollback)
        {
            var lineBuilder = new StringBuilder();
            for (int col = 0; col < line.Length; col++)
            {
                char ch = line[col].Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(lineBuilder.ToString().TrimEnd());
        }

        // Then add current screen buffer lines
        for (int row = 0; row < _rows; row++)
        {
            var lineBuilder = new StringBuilder();
            for (int col = 0; col < _cols; col++)
            {
                char ch = _cells[col, row].Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(lineBuilder.ToString().TrimEnd());
        }

        return sb.ToString();
    }

    // Font metrics
    private double _cellWidth;
    private double _cellHeight;
    private double _fontSize = 14;
    private double _dpiScale = 1.0;

    public TerminalControl()
    {
        _cells = new TerminalCell[DefaultCols, DefaultRows];
        InitializeCells();
        MeasureFontMetrics();

        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public void Attach(Session session)
    {
        FileLog.Write($"[TerminalControl] Attach: sessionId={session.Id}");

        Detach();
        _session = session;
        _bufferPosition = 0;
        _scrollOffset = 0;
        _scrollback.Clear();
        _pathExistsCache.Clear();

        RecalculateGridSize();
        InitializeCells();

        _parser = new AnsiParser(_cells, _cols, _rows, _scrollback, ScrollbackLines);

        // Load any existing buffer content
        if (session.Buffer != null)
        {
            var (initial, pos) = session.Buffer.GetWrittenSince(0);
            _bufferPosition = pos;
            if (initial.Length > 0)
            {
                _parser.Parse(initial);
            }
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        InvalidateVisual();
        FileLog.Write($"[TerminalControl] Attach complete: cols={_cols}, rows={_rows}");
    }

    public void Detach()
    {
        FileLog.Write($"[TerminalControl] Detach: sessionId={_session?.Id}");

        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _parser = null;
        _linkRegions.Clear();
        _pathExistsCache.Clear();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_session?.Buffer == null) return;

            var (data, newPos) = _session.Buffer.GetWrittenSince(_bufferPosition);
            if (data.Length > 0)
            {
                _bufferPosition = newPos;
                _parser?.Parse(data);

                // Clear path cache so links re-evaluate with new terminal content
                _pathExistsCache.Clear();
                Interlocked.Exchange(ref _pathCacheInvalidateNeeded, 0);

                // Let selection persist during output so users can
                // select text while Claude is generating

                // Only auto-scroll if user hasn't manually scrolled up
                // This lets users review history while output continues
                if (!_userScrolled && _scrollOffset > 0)
                    _scrollOffset = 0;

                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] PollTimer_Tick FAILED: {ex.Message}");
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Reset so background path checks can schedule one more InvalidateVisual
        Interlocked.Exchange(ref _pathCacheInvalidateNeeded, 0);

        var bg = GetCachedBrush(Color.FromRgb(30, 30, 30));
        drawingContext.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // Clear link regions for fresh hit-testing
        _linkRegions.Clear();

        if (_parser == null) return;

        // Link color - light blue like web links
        var linkColor = Color.FromRgb(0x6C, 0xB6, 0xFF);
        var linkBrush = GetCachedBrush(linkColor);

        // Underline pen for links
        var underlinePen = new Pen(linkBrush, 1);
        underlinePen.Freeze();

        for (int row = 0; row < _rows; row++)
        {
            // Get line text and find link matches for this row
            string lineText = GetLineText(row);
            var linkMatches = FindAllLinkMatches(lineText);

            // Create a lookup for which columns are part of a link
            var columnToLink = new Dictionary<int, LinkMatch>();
            foreach (var match in linkMatches)
            {
                for (int c = match.StartCol; c < match.EndCol && c < _cols; c++)
                {
                    columnToLink[c] = match;
                }

                // Store link region for hover detection
                double linkX = match.StartCol * _cellWidth;
                double linkY = row * _cellHeight;
                double linkWidth = (match.EndCol - match.StartCol) * _cellWidth;
                _linkRegions.Add(new LinkRegion(
                    new Rect(linkX, linkY, linkWidth, _cellHeight),
                    match.Text,
                    match.Type));
            }

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
                    var cellBg = GetCachedBrush(cell.Background);
                    drawingContext.DrawRectangle(cellBg, null,
                        new Rect(col * _cellWidth, row * _cellHeight, _cellWidth, _cellHeight));
                }

                // Draw character
                char ch = cell.Character;
                if (ch == '\0' || ch == ' ') continue;

                // Determine if this character is part of a link
                bool isLink = columnToLink.ContainsKey(col);

                var fg = isLink ? linkColor : (cell.Foreground == default ? Colors.LightGray : cell.Foreground);
                var brush = isLink ? linkBrush : GetCachedBrush(fg);

                var tf = GetCachedTypeface(cell.Bold, cell.Italic);

                var formattedText = new FormattedText(
                    ch.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    _fontSize,
                    brush,
                    _dpiScale);

                double charX = col * _cellWidth;
                double charY = row * _cellHeight;

                drawingContext.DrawText(formattedText, new Point(charX, charY));

                // Draw underline for links
                if (isLink)
                {
                    double underlineY = charY + _cellHeight - 2;
                    drawingContext.DrawLine(underlinePen,
                        new Point(charX, underlineY),
                        new Point(charX + _cellWidth, underlineY));
                }
            }
        }

        // Draw selection highlight
        if (_hasSelection)
        {
            var (startCol, startRow, endCol, endRow) = NormalizeSelection();
            var highlightBrush = GetCachedBrush(Color.FromArgb(100, 50, 100, 200));

            for (int row = startRow; row <= endRow; row++)
            {
                int colStart = (row == startRow) ? startCol : 0;
                int colEnd = (row == endRow) ? endCol : _cols - 1;

                double x = colStart * _cellWidth;
                double y = row * _cellHeight;
                double width = (colEnd - colStart + 1) * _cellWidth;

                drawingContext.DrawRectangle(highlightBrush, null,
                    new Rect(x, y, width, _cellHeight));
            }
        }

        // Draw cursor (only when visible and not scrolled)
        if (_scrollOffset == 0 && _parser != null && _parser.IsCursorVisible)
        {
            var (cursorCol, cursorRow) = _parser.GetCursorPosition();
            if (cursorCol >= 0 && cursorCol < _cols && cursorRow >= 0 && cursorRow < _rows)
            {
                var cursorBrush = GetCachedBrush(Color.FromArgb(180, 200, 200, 200));
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
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        try
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            var pos = e.GetPosition(this);

            // Check if clicking on a link - show context menu
            foreach (var region in _linkRegions)
            {
                if (region.Bounds.Contains(pos))
                {
                    ShowLinkContextMenu(pos, region.Text, region.Type);
                    e.Handled = true;
                    return;
                }
            }

            // Not clicking on a link - start selection
            var cell = HitTestCell(pos);

            _selectionStart = cell;
            _selectionEnd = cell;
            _isSelecting = true;
            _hasSelection = false;

            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnMouseLeftButtonDown FAILED: {ex.Message}");
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var pos = e.GetPosition(this);

        // Update cursor based on whether hovering over a link
        bool overLink = false;
        foreach (var region in _linkRegions)
        {
            if (region.Bounds.Contains(pos))
            {
                overLink = true;
                break;
            }
        }
        Cursor = overLink ? Cursors.Hand : Cursors.IBeam;

        // Handle selection dragging
        if (!_isSelecting) return;

        var cell = HitTestCell(pos);

        if (cell != _selectionEnd)
        {
            _selectionEnd = cell;
            _hasSelection = (_selectionStart != _selectionEnd);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();

            // Keep selection visible for copying
            // _hasSelection stays true if start != end
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        try
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // Ctrl+C with selection = copy to clipboard (not SIGINT)
            // Ctrl+Shift+C = always copy to clipboard
            if (ctrl && e.Key == Key.C && _hasSelection)
            {
                FileLog.Write($"[TerminalControl] Ctrl+C detected with selection, copying to clipboard");
                CopySelectionToClipboard();
                ClearSelection();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (ctrl && shift && e.Key == Key.C)
            {
                FileLog.Write($"[TerminalControl] Ctrl+Shift+C detected, hasSelection={_hasSelection}");
                if (_hasSelection)
                {
                    CopySelectionToClipboard();
                    ClearSelection();
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }

            if (_session == null) return;

            byte[]? data = MapKeyToBytes(e.Key, Keyboard.Modifiers);
            if (data != null)
            {
                _session.SendInput(data);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnPreviewKeyDown FAILED: {ex.Message}");
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        try
        {
            base.OnMouseRightButtonUp(e);

            // Right-click with selection copies to clipboard
            if (_hasSelection)
            {
                FileLog.Write($"[TerminalControl] Right-click with selection, copying to clipboard");
                CopySelectionToClipboard();
                ClearSelection();
                InvalidateVisual();
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnMouseRightButtonUp FAILED: {ex.Message}");
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        try
        {
            if (_session == null || string.IsNullOrEmpty(e.Text)) return;

            var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
            _session.SendInput(bytes);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnTextInput FAILED: {ex.Message}");
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        try
        {
            int lines = e.Delta > 0 ? 3 : -3;
            ScrollOffset = _scrollOffset + lines; // Uses property to trigger event

            // Track if user is reviewing history (scrolled up)
            // Reset when user scrolls back to bottom
            _userScrolled = _scrollOffset > 0;

            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnMouseWheel FAILED: {ex.Message}");
        }
    }

    private void RecalculateGridSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _cellWidth <= 0 || _cellHeight <= 0)
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
            _typefaceNormal,
            _fontSize,
            Brushes.White,
            _dpiScale);

        _cellWidth = formatted.WidthIncludingTrailingWhitespace;
        _cellHeight = formatted.Height;
    }

    /// <summary>
    /// Convert screen coordinates to cell (col, row).
    /// Accounts for scroll offset to give virtual row coordinates.
    /// </summary>
    private (int col, int row) HitTestCell(Point position)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
            return (0, 0);

        int col = (int)(position.X / _cellWidth);
        int row = (int)(position.Y / _cellHeight);

        // Clamp to valid range
        col = Math.Max(0, Math.Min(_cols - 1, col));
        row = Math.Max(0, Math.Min(_rows - 1, row));

        return (col, row);
    }

    /// <summary>
    /// Get a cell at the specified position, accounting for scrollback.
    /// </summary>
    private TerminalCell GetCellAt(int col, int row)
    {
        if (_scrollOffset > 0)
        {
            int virtualIndex = _scrollback.Count - _scrollOffset + row;

            if (virtualIndex < 0)
            {
                return default;
            }
            else if (virtualIndex < _scrollback.Count)
            {
                var line = _scrollback[virtualIndex];
                return col < line.Length ? line[col] : default;
            }
            else
            {
                int screenRow = virtualIndex - _scrollback.Count;
                return (screenRow >= 0 && screenRow < _rows)
                    ? _cells[col, screenRow]
                    : default;
            }
        }
        else
        {
            return (col >= 0 && col < _cols && row >= 0 && row < _rows)
                ? _cells[col, row]
                : default;
        }
    }

    /// <summary>
    /// Get the full text of a row.
    /// </summary>
    private string GetLineText(int row)
    {
        var sb = new StringBuilder();
        for (int col = 0; col < _cols; col++)
        {
            TerminalCell cell = GetCellAt(col, row);
            sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Represents a detected link match with its column range.
    /// </summary>
    private readonly record struct LinkMatch(int StartCol, int EndCol, string Text, LinkType Type);

    /// <summary>
    /// Find all link matches (paths and URLs) in a line of text.
    /// </summary>
    private List<LinkMatch> FindAllLinkMatches(string lineText)
    {
        var matches = new List<LinkMatch>();
        if (string.IsNullOrWhiteSpace(lineText))
            return matches;

        // Collect URL matches
        foreach (Match m in UrlRegex.Matches(lineText))
        {
            matches.Add(new LinkMatch(m.Index, m.Index + m.Length, m.Value, LinkType.Url));
        }

        // Collect absolute Windows path matches
        foreach (Match m in AbsoluteWindowsPathRegex.Matches(lineText))
        {
            string path = StripLineNumber(m.Value);
            matches.Add(new LinkMatch(m.Index, m.Index + m.Length, path, LinkType.Path));
        }

        // Collect Unix-style path matches
        foreach (Match m in AbsoluteUnixPathRegex.Matches(lineText))
        {
            string path = StripLineNumber(m.Value);
            matches.Add(new LinkMatch(m.Index, m.Index + m.Length, path, LinkType.Path));
        }

        // Collect relative path matches (only if session has repo path and path exists in cache)
        if (_session?.RepoPath != null)
        {
            foreach (Match m in RelativePathRegex.Matches(lineText))
            {
                string relativePath = StripLineNumber(m.Value);
                string fullPath = Path.Combine(_session.RepoPath, relativePath.Replace('/', '\\'));

                if (_pathExistsCache.TryGetValue(fullPath, out bool exists))
                {
                    // Cache hit
                    if (exists)
                        matches.Add(new LinkMatch(m.Index, m.Index + m.Length, relativePath, LinkType.Path));
                }
                else
                {
                    // Cache miss — don't block render; check in background
                    var capturedPath = fullPath;
                    var capturedRelative = relativePath;
                    _ = Task.Run(() =>
                    {
                        bool found = File.Exists(capturedPath) || Directory.Exists(capturedPath);
                        _pathExistsCache[capturedPath] = found;
                        if (found && Interlocked.CompareExchange(ref _pathCacheInvalidateNeeded, 1, 0) == 0)
                            Dispatcher.BeginInvoke(InvalidateVisual);
                    });
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Detect if there's a path or URL at the specified cell position.
    /// </summary>
    private (string? text, LinkType type) DetectLinkAtCell(int col, int row)
    {
        string lineText = GetLineText(row);
        if (string.IsNullOrWhiteSpace(lineText))
            return (null, LinkType.None);

        // Try URL first (more specific)
        var urlMatch = UrlRegex.Match(lineText);
        while (urlMatch.Success)
        {
            if (col >= urlMatch.Index && col < urlMatch.Index + urlMatch.Length)
            {
                return (urlMatch.Value, LinkType.Url);
            }
            urlMatch = urlMatch.NextMatch();
        }

        // Try absolute Windows path
        var winPathMatch = AbsoluteWindowsPathRegex.Match(lineText);
        while (winPathMatch.Success)
        {
            if (col >= winPathMatch.Index && col < winPathMatch.Index + winPathMatch.Length)
            {
                string path = StripLineNumber(winPathMatch.Value);
                return (path, LinkType.Path);
            }
            winPathMatch = winPathMatch.NextMatch();
        }

        // Try Unix-style absolute path (/c/path -> C:\path)
        var unixPathMatch = AbsoluteUnixPathRegex.Match(lineText);
        while (unixPathMatch.Success)
        {
            if (col >= unixPathMatch.Index && col < unixPathMatch.Index + unixPathMatch.Length)
            {
                string path = StripLineNumber(unixPathMatch.Value);
                return (path, LinkType.Path);
            }
            unixPathMatch = unixPathMatch.NextMatch();
        }

        // Try relative path (only if we have a session with a repo path)
        if (_session?.RepoPath != null)
        {
            var relPathMatch = RelativePathRegex.Match(lineText);
            while (relPathMatch.Success)
            {
                if (col >= relPathMatch.Index && col < relPathMatch.Index + relPathMatch.Length)
                {
                    string relativePath = StripLineNumber(relPathMatch.Value);
                    string fullPath = Path.Combine(_session.RepoPath, relativePath.Replace('/', '\\'));

                    // Check cache first, fall back to synchronous check (click context, not render)
                    if (_pathExistsCache.TryGetValue(fullPath, out bool exists))
                    {
                        if (exists) return (relativePath, LinkType.Path);
                    }
                    else
                    {
                        bool found = File.Exists(fullPath) || Directory.Exists(fullPath);
                        _pathExistsCache[fullPath] = found;
                        if (found) return (relativePath, LinkType.Path);
                    }
                }
                relPathMatch = relPathMatch.NextMatch();
            }
        }

        return (null, LinkType.None);
    }

    /// <summary>
    /// Strip line number suffix from path (e.g., "file.cs:42" -> "file.cs").
    /// </summary>
    private static string StripLineNumber(string path)
    {
        // Match :number or :number:number at the end
        int colonIndex = path.LastIndexOf(':');
        if (colonIndex > 0 && colonIndex < path.Length - 1)
        {
            string afterColon = path.Substring(colonIndex + 1);
            // Check if everything after colon is digits (possibly with another :number)
            if (afterColon.All(c => char.IsDigit(c) || c == ':'))
            {
                return path.Substring(0, colonIndex);
            }
        }
        return path;
    }

    /// <summary>
    /// Normalize selection so start is before end (reading order).
    /// </summary>
    private (int startCol, int startRow, int endCol, int endRow) NormalizeSelection()
    {
        int startRow = _selectionStart.row;
        int startCol = _selectionStart.col;
        int endRow = _selectionEnd.row;
        int endCol = _selectionEnd.col;

        // Swap if end is before start
        if (endRow < startRow || (endRow == startRow && endCol < startCol))
        {
            (startRow, endRow) = (endRow, startRow);
            (startCol, endCol) = (endCol, startCol);
        }

        return (startCol, startRow, endCol, endRow);
    }

    /// <summary>
    /// Get the selected text from the terminal buffer.
    /// </summary>
    private string GetSelectedText()
    {
        if (!_hasSelection) return string.Empty;

        var (startCol, startRow, endCol, endRow) = NormalizeSelection();
        var sb = new System.Text.StringBuilder();

        for (int row = startRow; row <= endRow; row++)
        {
            int colStart = (row == startRow) ? startCol : 0;
            int colEnd = (row == endRow) ? endCol : _cols - 1;

            var lineBuilder = new System.Text.StringBuilder();

            for (int col = colStart; col <= colEnd; col++)
            {
                TerminalCell cell;

                if (_scrollOffset > 0)
                {
                    // Same logic as OnRender for scrollback
                    int virtualIndex = _scrollback.Count - _scrollOffset + row;

                    if (virtualIndex < 0)
                    {
                        cell = default;
                    }
                    else if (virtualIndex < _scrollback.Count)
                    {
                        var line = _scrollback[virtualIndex];
                        cell = col < line.Length ? line[col] : default;
                    }
                    else
                    {
                        int screenRow = virtualIndex - _scrollback.Count;
                        cell = (screenRow >= 0 && screenRow < _rows)
                            ? _cells[col, screenRow]
                            : default;
                    }
                }
                else
                {
                    cell = _cells[col, row];
                }

                char ch = cell.Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }

            // Trim trailing whitespace from each line
            string lineText = lineBuilder.ToString().TrimEnd();
            sb.Append(lineText);

            if (row < endRow)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Copy the selected text to the clipboard.
    /// </summary>
    private void CopySelectionToClipboard()
    {
        var text = GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            FileLog.Write($"[TerminalControl] Copied {text.Length} characters to clipboard");
        }
    }

    /// <summary>
    /// Clear the current selection.
    /// </summary>
    private void ClearSelection()
    {
        _hasSelection = false;
        _isSelecting = false;
    }

    /// <summary>
    /// Show context menu for detected link.
    /// </summary>
    private void ShowLinkContextMenu(Point position, string link, LinkType type)
    {
        _detectedLink = link;
        _detectedLinkType = type;

        _linkContextMenu = new ContextMenu();

        if (type == LinkType.Path)
        {
            var copyItem = new MenuItem { Header = "Copy Path" };
            copyItem.Click += (_, _) => CopyLinkToClipboard();
            _linkContextMenu.Items.Add(copyItem);

            var explorerItem = new MenuItem { Header = "Open in Explorer" };
            explorerItem.Click += (_, _) => OpenInExplorer();
            _linkContextMenu.Items.Add(explorerItem);

            var vscodeItem = new MenuItem { Header = "Open in VS Code" };
            vscodeItem.Click += (_, _) => OpenInVsCode();
            _linkContextMenu.Items.Add(vscodeItem);
        }
        else if (type == LinkType.Url)
        {
            var copyItem = new MenuItem { Header = "Copy URL" };
            copyItem.Click += (_, _) => CopyLinkToClipboard();
            _linkContextMenu.Items.Add(copyItem);

            var browserItem = new MenuItem { Header = "Open in Browser" };
            browserItem.Click += (_, _) => OpenInBrowser();
            _linkContextMenu.Items.Add(browserItem);
        }

        _linkContextMenu.PlacementTarget = this;
        _linkContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _linkContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Copy detected link to clipboard.
    /// </summary>
    private void CopyLinkToClipboard()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        string textToCopy = _detectedLinkType == LinkType.Path
            ? ResolvePath(_detectedLink)
            : _detectedLink;

        Clipboard.SetText(textToCopy);
        FileLog.Write($"[TerminalControl] Copied link: {textToCopy}");
    }

    /// <summary>
    /// Open path in Windows Explorer.
    /// </summary>
    private void OpenInExplorer()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        try
        {
            string path = ResolvePath(_detectedLink);
            string target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;

            Process.Start("explorer.exe", target);
            FileLog.Write($"[TerminalControl] Opened in Explorer: {target}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OpenInExplorer FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open in Explorer:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open path in VS Code.
    /// </summary>
    private void OpenInVsCode()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        try
        {
            string path = ResolvePath(_detectedLink);
            var startInfo = new ProcessStartInfo("code", $"\"{path}\"")
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
            FileLog.Write($"[TerminalControl] Opened in VS Code: {path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OpenInVsCode FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open in VS Code:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open URL in default browser.
    /// </summary>
    private void OpenInBrowser()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        try
        {
            var startInfo = new ProcessStartInfo(_detectedLink)
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
            FileLog.Write($"[TerminalControl] Opened in browser: {_detectedLink}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OpenInBrowser FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open in browser:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Resolve a detected path to an absolute Windows path.
    /// </summary>
    private string ResolvePath(string path)
    {
        // Unix-style path /c/path -> C:\path
        if (path.StartsWith("/") && path.Length >= 3 && path[2] == '/')
        {
            char driveLetter = char.ToUpper(path[1]);
            string remainder = path.Substring(3).Replace('/', '\\');
            return $"{driveLetter}:\\{remainder}";
        }

        // Already an absolute Windows path
        if (path.Length >= 2 && path[1] == ':')
        {
            return path;
        }

        // Relative path - resolve against session's repo path
        if (_session?.RepoPath != null)
        {
            string normalized = path.Replace('/', '\\');
            return Path.GetFullPath(Path.Combine(_session.RepoPath, normalized));
        }

        // No session, return as-is
        return path;
    }

    private static byte[]? MapKeyToBytes(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

        // Ctrl+C
        if (ctrl && key == Key.C) return new byte[] { 0x03 };
        // Ctrl+D
        if (ctrl && key == Key.D) return new byte[] { 0x04 };
        // Ctrl+Z
        if (ctrl && key == Key.Z) return new byte[] { 0x1A };
        // Ctrl+L
        if (ctrl && key == Key.L) return new byte[] { 0x0C };

        // Shift+Tab (backtab) - used by Claude Code for mode cycling
        if (shift && key == Key.Tab) return "\x1b[Z"u8.ToArray();

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
