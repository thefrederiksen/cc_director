using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// Handles large text input by creating temporary files for Claude Code's @filepath reference.
/// When text exceeds the threshold, it's written to a temp file and the @filepath is returned
/// instead, allowing Claude Code to read the file directly.
/// </summary>
public static class LargeInputHandler
{
    /// <summary>
    /// Character threshold above which input is written to a temp file.
    /// Conservative value to ensure reliability across all terminal configurations.
    /// </summary>
    public const int LargeInputThreshold = 1000;

    private const string TempDirName = ".temp";

    /// <summary>
    /// Check if the given text exceeds the large input threshold.
    /// </summary>
    public static bool IsLargeInput(string text) => text.Length > LargeInputThreshold;

    /// <summary>
    /// Creates a temp file in {workingDir}/.temp/ and returns the full path.
    /// The file contains the original text and can be referenced via @filepath in Claude Code.
    /// </summary>
    /// <param name="text">The text content to write.</param>
    /// <param name="workingDir">The working directory (repository root) where .temp will be created.</param>
    /// <returns>Full path to the created temp file.</returns>
    public static string CreateTempFile(string text, string workingDir)
    {
        FileLog.Write($"[LargeInputHandler] CreateTempFile: workingDir={workingDir}, textLength={text.Length}");

        var tempDir = Path.Combine(workingDir, TempDirName);
        Directory.CreateDirectory(tempDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var random = Path.GetRandomFileName()[..6];
        var filename = $"input_{timestamp}_{random}.txt";
        var filepath = Path.Combine(tempDir, filename);

        File.WriteAllText(filepath, text);
        FileLog.Write($"[LargeInputHandler] Created temp file: {filepath} ({text.Length} chars)");

        return filepath;
    }
}
