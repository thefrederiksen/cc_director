# TerminalControl Performance Optimization - Status Document

**Date:** 2026-02-12
**Status:** Phase 1 Complete, Phase 2 Pending

---

## Problem Summary

The `TerminalControl.cs` OnRender method was creating excessive allocations per frame:
- 3,600 `FormattedText` objects (one per character, 120 cols x 30 rows)
- 3,600 `Typeface` objects (one per character)
- 3,600+ `SolidColorBrush` objects (one per character + backgrounds)

At 60fps, this equals 216,000+ allocations/second causing GC pressure and slow renders.

**Observed symptoms from logs:**
- OnRender: 139ms (should be <16ms for 60fps)
- AnsiParser: 93-126ms per 100KB
- URL regex: 11ms/line

---

## Completed Work (Phase 1)

### 1. Typeface Caching - DONE

**File:** `src/CcDirector.Wpf/Controls/TerminalControl.cs` (lines 34-65)

Added 4 static pre-cached typefaces:
```csharp
private static readonly Typeface _typefaceNormal;
private static readonly Typeface _typefaceBold;
private static readonly Typeface _typefaceItalic;
private static readonly Typeface _typefaceBoldItalic;
```

Added helper:
```csharp
private static Typeface GetCachedTypeface(bool bold, bool italic)
```

**Impact:** Eliminates 3,600 Typeface allocations per frame.

### 2. Brush Caching - DONE

**File:** `src/CcDirector.Wpf/Controls/TerminalControl.cs` (lines 41-57)

Added thread-safe brush cache:
```csharp
private static readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
private static readonly object _brushCacheLock = new();

private static SolidColorBrush GetCachedBrush(Color color)
```

Updated all brush creations in OnRender to use cache:
- Background brush (line 269)
- Link brush (line 298)
- Cell background brush (line 362)
- Foreground text brush (line 376)
- Selection highlight brush (line 406)
- Cursor brush (line 429)

**Impact:** Eliminates 3,600+ SolidColorBrush allocations per frame.

### 3. Removed Instance _typeface Field - DONE

Removed the instance `_typeface` field and constructor initialization since we now use static typefaces. Updated `MeasureFontMetrics()` to use `_typefaceNormal`.

---

## Remaining Work (Phase 2 - Not Started)

### 1. Batch Character Rendering (HIGH IMPACT - Complex)

**Current:** Creates one `FormattedText` per character (3,600 per frame)
**Goal:** Create one `FormattedText` per "style run" (~100-200 per frame)

Group consecutive characters with same (foreground, background, bold, italic) into single strings:
```csharp
// Instead of:
for each cell:
    new FormattedText(cell.Character.ToString(), ...)

// Do:
var runs = BuildStyledRuns(row);  // "Hello" (white), " world" (green)
foreach (var run in runs):
    new FormattedText(run.Text, ...)
```

**Expected impact:** 50-80% faster OnRender
**Effort:** High - requires restructuring the render loop

### 2. Skip Link Detection During Rapid Output (LOW IMPACT)

When receiving large data bursts (>50KB), skip link detection for 1-2 frames:
```csharp
private bool _skipLinkDetection;
private DateTime _lastLargeChunk;

// In PollTimer_Tick: set flag on large chunks
// In OnRender: skip DetectAllLinks() if within 200ms of large chunk
```

**Expected impact:** Smoother scrolling during rapid output
**Effort:** Low

### 3. Simplify URL Regex (LOW IMPACT)

Current regex is broad and may backtrack. Could tighten to:
```csharp
// Current:
@"https?://[^\s""'<>]+"

// More specific:
@"https?://[a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+[^\s""'<>]*"
```

**Expected impact:** Minor improvement in link detection time
**Effort:** Low

---

## Build Status

**Last build:** SUCCESS (0 warnings, 0 errors)
**Command:** `dotnet build --no-restore`

---

## Files Modified

| File | Changes |
|------|---------|
| `src/CcDirector.Wpf/Controls/TerminalControl.cs` | Added typeface cache, brush cache, updated OnRender |

---

## Testing Recommendations

1. **Basic functionality:**
   - Open a session, run commands that produce colored output
   - Verify colors render correctly (ANSI colors, bold, italic)
   - Click on file paths and URLs - verify links still work

2. **Performance verification:**
   - Run a command that produces lots of output (e.g., `find . -name "*.cs"`)
   - Check logs for OnRender times - should be <50ms consistently
   - Look for `[TerminalControl] OnRender SLOW` log entries

3. **Stress test:**
   - Run `cat` on a large file or rapid build output
   - Verify no visual glitches or freezing

---

## Architecture Notes

The caching is implemented at the static class level:
- Typefaces are created once at class load time (4 variants)
- Brushes are created on-demand and cached permanently (Dictionary keyed by Color)
- Brush cache uses lock for thread safety (OnRender runs on UI thread, but being safe)

The brush cache will grow over time but is bounded by unique colors used. ANSI terminals typically use 16-256 colors, so memory impact is minimal.

---

## Next Steps

1. **Test the Phase 1 changes** - verify no regressions in rendering
2. **Measure performance** - check if OnRender times are now acceptable
3. **If still slow:** Implement batch character rendering (Phase 2, item 1)
4. **If acceptable:** Phase 2 items can be deferred or skipped
