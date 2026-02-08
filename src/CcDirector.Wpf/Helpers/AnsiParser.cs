using System.Windows.Media;

namespace CcDirector.Wpf.Helpers;

/// <summary>
/// VT100/ANSI escape sequence parser. Reads raw bytes and updates a TerminalCell grid.
/// Handles SGR (colors/bold), cursor movement, erase, and scrolling.
/// </summary>
public class AnsiParser
{
    private TerminalCell[,] _cells;
    private int _cols;
    private int _rows;
    private readonly List<TerminalCell[]> _scrollback;
    private readonly int _maxScrollback;

    // Cursor position (0-based)
    private int _cursorCol;
    private int _cursorRow;

    // Current text attributes
    private Color _fg = Colors.LightGray;
    private Color _bg = default;
    private bool _bold;
    private bool _italic;
    private bool _underline;

    // Parser state machine
    private ParserState _state = ParserState.Ground;
    private readonly List<int> _params = new();
    private int _currentParam;
    private bool _hasParam;
    private char _intermediateChar;

    // UTF-8 decoding
    private readonly byte[] _utf8Buf = new byte[4];
    private int _utf8Needed;  // remaining continuation bytes expected
    private int _utf8Len;     // bytes collected so far

    private enum ParserState
    {
        Ground,
        Escape,
        Csi,
        OscString
    }

    // Standard 8 colors + bright variants
    private static readonly Color[] AnsiColors =
    {
        Color.FromRgb(0, 0, 0),       // 0 Black
        Color.FromRgb(205, 49, 49),    // 1 Red
        Color.FromRgb(13, 188, 121),   // 2 Green
        Color.FromRgb(229, 229, 16),   // 3 Yellow
        Color.FromRgb(36, 114, 200),   // 4 Blue
        Color.FromRgb(188, 63, 188),   // 5 Magenta
        Color.FromRgb(17, 168, 205),   // 6 Cyan
        Color.FromRgb(204, 204, 204),  // 7 White
        // Bright variants
        Color.FromRgb(102, 102, 102),  // 8 Bright Black
        Color.FromRgb(241, 76, 76),    // 9 Bright Red
        Color.FromRgb(35, 209, 139),   // 10 Bright Green
        Color.FromRgb(245, 245, 67),   // 11 Bright Yellow
        Color.FromRgb(59, 142, 234),   // 12 Bright Blue
        Color.FromRgb(214, 112, 214),  // 13 Bright Magenta
        Color.FromRgb(41, 184, 219),   // 14 Bright Cyan
        Color.FromRgb(242, 242, 242),  // 15 Bright White
    };

    public AnsiParser(TerminalCell[,] cells, int cols, int rows,
        List<TerminalCell[]> scrollback, int maxScrollback)
    {
        _cells = cells;
        _cols = cols;
        _rows = rows;
        _scrollback = scrollback;
        _maxScrollback = maxScrollback;
    }

    public void UpdateGrid(TerminalCell[,] cells, int cols, int rows)
    {
        _cells = cells;
        _cols = cols;
        _rows = rows;
        _cursorCol = Math.Min(_cursorCol, cols - 1);
        _cursorRow = Math.Min(_cursorRow, rows - 1);
    }

    public (int Col, int Row) GetCursorPosition() => (_cursorCol, _cursorRow);

    public void Parse(byte[] data)
    {
        foreach (byte b in data)
        {
            switch (_state)
            {
                case ParserState.Ground:
                    HandleGround(b);
                    break;
                case ParserState.Escape:
                    HandleEscape(b);
                    break;
                case ParserState.Csi:
                    HandleCsi(b);
                    break;
                case ParserState.OscString:
                    HandleOsc(b);
                    break;
            }
        }
    }

    private void HandleGround(byte b)
    {
        // If we're in the middle of a UTF-8 multi-byte sequence, accumulate
        if (_utf8Needed > 0)
        {
            if ((b & 0xC0) == 0x80) // Valid continuation byte
            {
                _utf8Buf[_utf8Len++] = b;
                _utf8Needed--;
                if (_utf8Needed == 0)
                {
                    // Decode the complete UTF-8 sequence
                    var str = System.Text.Encoding.UTF8.GetString(_utf8Buf, 0, _utf8Len);
                    foreach (char ch in str)
                        PutChar(ch);
                }
                return;
            }
            else
            {
                // Invalid continuation - discard partial sequence and re-process this byte
                _utf8Needed = 0;
                _utf8Len = 0;
            }
        }

        switch (b)
        {
            case 0x1B: // ESC
                _state = ParserState.Escape;
                _params.Clear();
                _currentParam = 0;
                _hasParam = false;
                _intermediateChar = '\0';
                break;
            case 0x07: // BEL - ignore
                break;
            case 0x08: // BS - backspace
                if (_cursorCol > 0) _cursorCol--;
                break;
            case 0x09: // HT - tab
                _cursorCol = Math.Min(_cols - 1, (_cursorCol / 8 + 1) * 8);
                break;
            case 0x0A: // LF - line feed
            case 0x0B: // VT
            case 0x0C: // FF
                LineFeed();
                break;
            case 0x0D: // CR - carriage return
                _cursorCol = 0;
                break;
            default:
                if (b <= 0x7F)
                {
                    // Single-byte ASCII
                    if (b >= 0x20)
                        PutChar((char)b);
                }
                else if ((b & 0xE0) == 0xC0) // 2-byte UTF-8 lead: 110xxxxx
                {
                    _utf8Buf[0] = b;
                    _utf8Len = 1;
                    _utf8Needed = 1;
                }
                else if ((b & 0xF0) == 0xE0) // 3-byte UTF-8 lead: 1110xxxx
                {
                    _utf8Buf[0] = b;
                    _utf8Len = 1;
                    _utf8Needed = 2;
                }
                else if ((b & 0xF8) == 0xF0) // 4-byte UTF-8 lead: 11110xxx
                {
                    _utf8Buf[0] = b;
                    _utf8Len = 1;
                    _utf8Needed = 3;
                }
                // else: stray continuation byte (0x80-0xBF) - ignore
                break;
        }
    }

    private void HandleEscape(byte b)
    {
        switch (b)
        {
            case (byte)'[': // CSI
                _state = ParserState.Csi;
                break;
            case (byte)']': // OSC
                _state = ParserState.OscString;
                break;
            case (byte)'M': // RI - reverse index (scroll down)
                if (_cursorRow == 0)
                    ScrollDown();
                else
                    _cursorRow--;
                _state = ParserState.Ground;
                break;
            case (byte)'7': // DECSC - save cursor
                // Simplified: just ignore
                _state = ParserState.Ground;
                break;
            case (byte)'8': // DECRC - restore cursor
                _state = ParserState.Ground;
                break;
            case (byte)'=': // DECKPAM
            case (byte)'>': // DECKPNM
                _state = ParserState.Ground;
                break;
            default:
                _state = ParserState.Ground;
                break;
        }
    }

    private void HandleCsi(byte b)
    {
        if (b >= '0' && b <= '9')
        {
            _currentParam = _currentParam * 10 + (b - '0');
            _hasParam = true;
            return;
        }

        if (b == ';')
        {
            _params.Add(_hasParam ? _currentParam : 0);
            _currentParam = 0;
            _hasParam = false;
            return;
        }

        // Intermediate characters (? ! > etc.)
        if (b >= 0x20 && b <= 0x2F)
        {
            _intermediateChar = (char)b;
            return;
        }

        // Final character - execute CSI sequence
        if (_hasParam)
            _params.Add(_currentParam);

        ExecuteCsi((char)b);
        _state = ParserState.Ground;
    }

    private void HandleOsc(byte b)
    {
        // OSC strings end with BEL (0x07) or ST (ESC \)
        if (b == 0x07 || b == 0x1B)
        {
            _state = ParserState.Ground;
        }
    }

    private void ExecuteCsi(char final)
    {
        int p0 = _params.Count > 0 ? _params[0] : 0;
        int p1 = _params.Count > 1 ? _params[1] : 0;

        // Handle ? prefix (DEC private modes)
        if (_intermediateChar == '?')
        {
            // Ignore DEC private mode set/reset (cursor visibility, alt screen, etc.)
            return;
        }

        switch (final)
        {
            case 'A': // CUU - Cursor Up
                _cursorRow = Math.Max(0, _cursorRow - Math.Max(1, p0));
                break;
            case 'B': // CUD - Cursor Down
                _cursorRow = Math.Min(_rows - 1, _cursorRow + Math.Max(1, p0));
                break;
            case 'C': // CUF - Cursor Forward
                _cursorCol = Math.Min(_cols - 1, _cursorCol + Math.Max(1, p0));
                break;
            case 'D': // CUB - Cursor Back
                _cursorCol = Math.Max(0, _cursorCol - Math.Max(1, p0));
                break;
            case 'E': // CNL - Cursor Next Line
                _cursorCol = 0;
                _cursorRow = Math.Min(_rows - 1, _cursorRow + Math.Max(1, p0));
                break;
            case 'F': // CPL - Cursor Previous Line
                _cursorCol = 0;
                _cursorRow = Math.Max(0, _cursorRow - Math.Max(1, p0));
                break;
            case 'G': // CHA - Cursor Horizontal Absolute
                _cursorCol = Math.Min(_cols - 1, Math.Max(0, (p0 > 0 ? p0 : 1) - 1));
                break;
            case 'H': // CUP - Cursor Position
            case 'f': // HVP - same as CUP
                _cursorRow = Math.Min(_rows - 1, Math.Max(0, (p0 > 0 ? p0 : 1) - 1));
                _cursorCol = Math.Min(_cols - 1, Math.Max(0, (p1 > 0 ? p1 : 1) - 1));
                break;
            case 'J': // ED - Erase in Display
                EraseInDisplay(p0);
                break;
            case 'K': // EL - Erase in Line
                EraseInLine(p0);
                break;
            case 'L': // IL - Insert Lines
                InsertLines(Math.Max(1, p0));
                break;
            case 'M': // DL - Delete Lines
                DeleteLines(Math.Max(1, p0));
                break;
            case 'P': // DCH - Delete Characters
                DeleteChars(Math.Max(1, p0));
                break;
            case 'S': // SU - Scroll Up
                for (int i = 0; i < Math.Max(1, p0); i++)
                    ScrollUp();
                break;
            case 'T': // SD - Scroll Down
                for (int i = 0; i < Math.Max(1, p0); i++)
                    ScrollDown();
                break;
            case 'X': // ECH - Erase Characters
                EraseChars(Math.Max(1, p0));
                break;
            case 'd': // VPA - Line Position Absolute
                _cursorRow = Math.Min(_rows - 1, Math.Max(0, (p0 > 0 ? p0 : 1) - 1));
                break;
            case 'm': // SGR - Select Graphic Rendition
                HandleSgr();
                break;
            case 'r': // DECSTBM - Set Top and Bottom Margins (ignore for now)
                break;
            case 's': // SCP - Save Cursor Position
                break;
            case 'u': // RCP - Restore Cursor Position
                break;
            case '@': // ICH - Insert Characters
                InsertChars(Math.Max(1, p0));
                break;
            case 'h': // SM - Set Mode
            case 'l': // RM - Reset Mode
                // Ignore mode changes
                break;
            case 'n': // DSR - Device Status Report (ignore)
                break;
            case 't': // Window manipulation (ignore)
                break;
        }
    }

    private void HandleSgr()
    {
        if (_params.Count == 0)
        {
            ResetAttributes();
            return;
        }

        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];

            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _bold = true; break;
                case 3: _italic = true; break;
                case 4: _underline = true; break;
                case 7: // Reverse video
                    (_fg, _bg) = (_bg == default ? Color.FromRgb(30, 30, 30) : _bg,
                                  _fg == default ? Colors.LightGray : _fg);
                    break;
                case 22: _bold = false; break;
                case 23: _italic = false; break;
                case 24: _underline = false; break;
                case 27: // Reverse off (just reset to default)
                    break;
                case >= 30 and <= 37:
                    _fg = _bold ? AnsiColors[p - 30 + 8] : AnsiColors[p - 30];
                    break;
                case 38: // Extended foreground
                    i = ParseExtendedColor(i, out _fg);
                    break;
                case 39: _fg = Colors.LightGray; break;
                case >= 40 and <= 47:
                    _bg = AnsiColors[p - 40];
                    break;
                case 48: // Extended background
                    i = ParseExtendedColor(i, out _bg);
                    break;
                case 49: _bg = default; break;
                case >= 90 and <= 97:
                    _fg = AnsiColors[p - 90 + 8];
                    break;
                case >= 100 and <= 107:
                    _bg = AnsiColors[p - 100 + 8];
                    break;
            }
        }
    }

    private int ParseExtendedColor(int index, out Color color)
    {
        color = Colors.LightGray;

        if (index + 1 >= _params.Count) return index;

        int mode = _params[index + 1];

        if (mode == 5 && index + 2 < _params.Count)
        {
            // 256 color mode
            int colorIndex = _params[index + 2];
            color = Get256Color(colorIndex);
            return index + 2;
        }

        if (mode == 2 && index + 4 < _params.Count)
        {
            // True color (RGB)
            int r = Math.Clamp(_params[index + 2], 0, 255);
            int g = Math.Clamp(_params[index + 3], 0, 255);
            int b = Math.Clamp(_params[index + 4], 0, 255);
            color = Color.FromRgb((byte)r, (byte)g, (byte)b);
            return index + 4;
        }

        return index;
    }

    private static Color Get256Color(int index)
    {
        if (index < 16)
            return AnsiColors[index];

        if (index < 232)
        {
            // 6x6x6 color cube
            index -= 16;
            int r = index / 36;
            int g = (index % 36) / 6;
            int b = index % 6;
            return Color.FromRgb(
                (byte)(r > 0 ? 55 + r * 40 : 0),
                (byte)(g > 0 ? 55 + g * 40 : 0),
                (byte)(b > 0 ? 55 + b * 40 : 0));
        }

        // Grayscale ramp
        int gray = 8 + (index - 232) * 10;
        return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }

    private void ResetAttributes()
    {
        _fg = Colors.LightGray;
        _bg = default;
        _bold = false;
        _italic = false;
        _underline = false;
    }

    private void PutChar(char ch)
    {
        if (_cursorCol >= _cols)
        {
            // Line wrap
            _cursorCol = 0;
            LineFeed();
        }

        _cells[_cursorCol, _cursorRow] = new TerminalCell
        {
            Character = ch,
            Foreground = _fg,
            Background = _bg,
            Bold = _bold,
            Italic = _italic,
            Underline = _underline
        };
        _cursorCol++;
    }

    private void LineFeed()
    {
        if (_cursorRow < _rows - 1)
        {
            _cursorRow++;
        }
        else
        {
            ScrollUp();
        }
    }

    private void ScrollUp()
    {
        // Save top row to scrollback
        var savedRow = new TerminalCell[_cols];
        for (int c = 0; c < _cols; c++)
            savedRow[c] = _cells[c, 0];
        _scrollback.Add(savedRow);

        // Trim scrollback
        while (_scrollback.Count > _maxScrollback)
            _scrollback.RemoveAt(0);

        // Shift rows up
        for (int r = 0; r < _rows - 1; r++)
            for (int c = 0; c < _cols; c++)
                _cells[c, r] = _cells[c, r + 1];

        // Clear bottom row
        for (int c = 0; c < _cols; c++)
            _cells[c, _rows - 1] = new TerminalCell();
    }

    private void ScrollDown()
    {
        // Shift rows down
        for (int r = _rows - 1; r > 0; r--)
            for (int c = 0; c < _cols; c++)
                _cells[c, r] = _cells[c, r - 1];

        // Clear top row
        for (int c = 0; c < _cols; c++)
            _cells[c, 0] = new TerminalCell();
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // Clear from cursor to end
                EraseInLine(0);
                for (int r = _cursorRow + 1; r < _rows; r++)
                    ClearRow(r);
                break;
            case 1: // Clear from start to cursor
                for (int r = 0; r < _cursorRow; r++)
                    ClearRow(r);
                for (int c = 0; c <= _cursorCol && c < _cols; c++)
                    _cells[c, _cursorRow] = new TerminalCell();
                break;
            case 2: // Clear entire display
            case 3: // Clear entire display and scrollback
                for (int r = 0; r < _rows; r++)
                    ClearRow(r);
                if (mode == 3) _scrollback.Clear();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: // Clear from cursor to end of line
                for (int c = _cursorCol; c < _cols; c++)
                    _cells[c, _cursorRow] = new TerminalCell();
                break;
            case 1: // Clear from start of line to cursor
                for (int c = 0; c <= _cursorCol && c < _cols; c++)
                    _cells[c, _cursorRow] = new TerminalCell();
                break;
            case 2: // Clear entire line
                ClearRow(_cursorRow);
                break;
        }
    }

    private void ClearRow(int row)
    {
        for (int c = 0; c < _cols; c++)
            _cells[c, row] = new TerminalCell();
    }

    private void InsertLines(int count)
    {
        for (int n = 0; n < count; n++)
        {
            for (int r = _rows - 1; r > _cursorRow; r--)
                for (int c = 0; c < _cols; c++)
                    _cells[c, r] = _cells[c, r - 1];
            ClearRow(_cursorRow);
        }
    }

    private void DeleteLines(int count)
    {
        for (int n = 0; n < count; n++)
        {
            for (int r = _cursorRow; r < _rows - 1; r++)
                for (int c = 0; c < _cols; c++)
                    _cells[c, r] = _cells[c, r + 1];
            ClearRow(_rows - 1);
        }
    }

    private void DeleteChars(int count)
    {
        for (int c = _cursorCol; c < _cols - count; c++)
            _cells[c, _cursorRow] = _cells[c + count, _cursorRow];
        for (int c = Math.Max(_cursorCol, _cols - count); c < _cols; c++)
            _cells[c, _cursorRow] = new TerminalCell();
    }

    private void InsertChars(int count)
    {
        for (int c = _cols - 1; c >= _cursorCol + count; c--)
            _cells[c, _cursorRow] = _cells[c - count, _cursorRow];
        for (int c = _cursorCol; c < _cursorCol + count && c < _cols; c++)
            _cells[c, _cursorRow] = new TerminalCell();
    }

    private void EraseChars(int count)
    {
        for (int c = _cursorCol; c < _cursorCol + count && c < _cols; c++)
            _cells[c, _cursorRow] = new TerminalCell();
    }
}
