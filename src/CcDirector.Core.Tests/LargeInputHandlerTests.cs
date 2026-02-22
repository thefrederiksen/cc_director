using CcDirector.Core.Input;
using Xunit;

namespace CcDirector.Core.Tests;

public class LargeInputHandlerTests : IDisposable
{
    private readonly string _testDir;

    public LargeInputHandlerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"LargeInputHandlerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ignore I/O errors during test cleanup (file locks, etc.)
        }
    }

    [Fact]
    public void IsLargeInput_AtThreshold_ReturnsFalse()
    {
        var text = new string('a', LargeInputHandler.LargeInputThreshold);
        Assert.False(LargeInputHandler.IsLargeInput(text));
    }

    [Fact]
    public void IsLargeInput_AboveThreshold_ReturnsTrue()
    {
        var text = new string('a', LargeInputHandler.LargeInputThreshold + 1);
        Assert.True(LargeInputHandler.IsLargeInput(text));
    }

    [Fact]
    public void IsLargeInput_BelowThreshold_ReturnsFalse()
    {
        var text = new string('a', 500);
        Assert.False(LargeInputHandler.IsLargeInput(text));
    }

    [Fact]
    public void IsLargeInput_EmptyString_ReturnsFalse()
    {
        Assert.False(LargeInputHandler.IsLargeInput(string.Empty));
    }

    [Fact]
    public void CreateTempFile_CreatesDirectory()
    {
        var tempDir = Path.Combine(_testDir, ".temp");
        Assert.False(Directory.Exists(tempDir));

        LargeInputHandler.CreateTempFile("test content", _testDir);

        Assert.True(Directory.Exists(tempDir));
    }

    [Fact]
    public void CreateTempFile_WritesCorrectContent()
    {
        var content = "This is the test content\nwith multiple lines\nand special chars: @#$%";

        var filepath = LargeInputHandler.CreateTempFile(content, _testDir);

        var readContent = File.ReadAllText(filepath);
        Assert.Equal(content, readContent);
    }

    [Fact]
    public void CreateTempFile_ReturnsValidPath()
    {
        var filepath = LargeInputHandler.CreateTempFile("test", _testDir);

        Assert.True(File.Exists(filepath));
        Assert.StartsWith(Path.Combine(_testDir, ".temp"), filepath);
        Assert.EndsWith(".txt", filepath);
    }

    [Fact]
    public void CreateTempFile_FilenameContainsTimestamp()
    {
        var filepath = LargeInputHandler.CreateTempFile("test", _testDir);

        var filename = Path.GetFileName(filepath);
        Assert.StartsWith("input_", filename);
        // Filename format: input_YYYYMMDD_HHMMSS_random.txt
        Assert.Matches(@"input_\d{8}_\d{6}_\w{6}\.txt", filename);
    }

    [Fact]
    public void CreateTempFile_MultipleCallsCreateUniqueFiles()
    {
        var file1 = LargeInputHandler.CreateTempFile("content1", _testDir);
        var file2 = LargeInputHandler.CreateTempFile("content2", _testDir);

        Assert.NotEqual(file1, file2);
        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        Assert.Equal("content1", File.ReadAllText(file1));
        Assert.Equal("content2", File.ReadAllText(file2));
    }

    [Fact]
    public void CreateTempFile_HandlesLargeContent()
    {
        var largeContent = new string('x', 100_000); // 100KB

        var filepath = LargeInputHandler.CreateTempFile(largeContent, _testDir);

        var readContent = File.ReadAllText(filepath);
        Assert.Equal(largeContent.Length, readContent.Length);
        Assert.Equal(largeContent, readContent);
    }

    [Fact]
    public void LargeInputThreshold_IsExpectedValue()
    {
        // Document the expected threshold value
        Assert.Equal(1000, LargeInputHandler.LargeInputThreshold);
    }
}
