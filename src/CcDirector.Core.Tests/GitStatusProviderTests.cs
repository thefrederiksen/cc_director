using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class GitStatusProviderTests
{
    [Fact]
    public void ParsePorcelain_ModifiedUnstaged()
    {
        var result = GitStatusProvider.ParsePorcelainOutput(" M file.cs\n");

        Assert.True(result.Success);
        Assert.Empty(result.StagedChanges);
        Assert.Single(result.UnstagedChanges);
        Assert.Equal(GitFileStatus.Modified, result.UnstagedChanges[0].Status);
        Assert.Equal("M", result.UnstagedChanges[0].StatusChar);
        Assert.Equal("file.cs", result.UnstagedChanges[0].FilePath);
        Assert.False(result.UnstagedChanges[0].IsStaged);
    }

    [Fact]
    public void ParsePorcelain_ModifiedStaged()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("M  file.cs\n");

        Assert.True(result.Success);
        Assert.Single(result.StagedChanges);
        Assert.Empty(result.UnstagedChanges);
        Assert.Equal(GitFileStatus.Modified, result.StagedChanges[0].Status);
        Assert.Equal("M", result.StagedChanges[0].StatusChar);
        Assert.True(result.StagedChanges[0].IsStaged);
    }

    [Fact]
    public void ParsePorcelain_BothStagedAndUnstaged()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("MM file.cs\n");

        Assert.True(result.Success);
        Assert.Single(result.StagedChanges);
        Assert.Single(result.UnstagedChanges);
        Assert.Equal("file.cs", result.StagedChanges[0].FilePath);
        Assert.Equal("file.cs", result.UnstagedChanges[0].FilePath);
    }

    [Fact]
    public void ParsePorcelain_Untracked()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("?? file.cs\n");

        Assert.True(result.Success);
        Assert.Empty(result.StagedChanges);
        Assert.Single(result.UnstagedChanges);
        Assert.Equal(GitFileStatus.Untracked, result.UnstagedChanges[0].Status);
        Assert.Equal("?", result.UnstagedChanges[0].StatusChar);
    }

    [Fact]
    public void ParsePorcelain_Added()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("A  file.cs\n");

        Assert.True(result.Success);
        Assert.Single(result.StagedChanges);
        Assert.Empty(result.UnstagedChanges);
        Assert.Equal(GitFileStatus.Added, result.StagedChanges[0].Status);
        Assert.Equal("A", result.StagedChanges[0].StatusChar);
    }

    [Fact]
    public void ParsePorcelain_Deleted()
    {
        var result = GitStatusProvider.ParsePorcelainOutput(" D file.cs\n");

        Assert.True(result.Success);
        Assert.Empty(result.StagedChanges);
        Assert.Single(result.UnstagedChanges);
        Assert.Equal(GitFileStatus.Deleted, result.UnstagedChanges[0].Status);
    }

    [Fact]
    public void ParsePorcelain_EmptyOutput()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("");

        Assert.True(result.Success);
        Assert.Empty(result.StagedChanges);
        Assert.Empty(result.UnstagedChanges);
    }

    [Fact]
    public void ParsePorcelain_MultipleFiles()
    {
        var output = " M src/File1.cs\nA  src/File2.cs\n?? README.md\n D old.txt\n";
        var result = GitStatusProvider.ParsePorcelainOutput(output);

        Assert.True(result.Success);
        Assert.Single(result.StagedChanges);   // A  File2.cs
        Assert.Equal(GitFileStatus.Added, result.StagedChanges[0].Status);

        Assert.Equal(3, result.UnstagedChanges.Count); // M File1.cs, ?? README.md, D old.txt
        Assert.Equal(GitFileStatus.Modified, result.UnstagedChanges[0].Status);
        Assert.Equal(GitFileStatus.Untracked, result.UnstagedChanges[1].Status);
        Assert.Equal(GitFileStatus.Deleted, result.UnstagedChanges[2].Status);
    }

    [Fact]
    public void ParsePorcelain_SubdirectoryFiles()
    {
        var result = GitStatusProvider.ParsePorcelainOutput(" M src/components/Button.tsx\n");

        Assert.Single(result.UnstagedChanges);
        Assert.Equal("src/components/Button.tsx", result.UnstagedChanges[0].FilePath);
        Assert.Equal("Button.tsx", result.UnstagedChanges[0].FileName);
    }

    [Fact]
    public void ParsePorcelain_Renamed()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("R  old.cs -> new.cs\n");

        Assert.Single(result.StagedChanges);
        Assert.Equal(GitFileStatus.Renamed, result.StagedChanges[0].Status);
        Assert.Equal("R", result.StagedChanges[0].StatusChar);
        Assert.Equal("new.cs", result.StagedChanges[0].FilePath);
    }

    [Fact]
    public void ParsePorcelain_UntrackedDirectoryTrailingSlash()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("?? .claude/\n");

        Assert.True(result.Success);
        Assert.Single(result.UnstagedChanges);
        Assert.Equal(".claude", result.UnstagedChanges[0].FilePath);
        Assert.Equal(".claude", result.UnstagedChanges[0].FileName);
        Assert.Equal(GitFileStatus.Untracked, result.UnstagedChanges[0].Status);
    }

    [Fact]
    public void ParsePorcelain_UntrackedNestedDirectoryTrailingSlash()
    {
        var result = GitStatusProvider.ParsePorcelainOutput("?? src/components/\n");

        Assert.True(result.Success);
        Assert.Single(result.UnstagedChanges);
        Assert.Equal("src/components", result.UnstagedChanges[0].FilePath);
        Assert.Equal("components", result.UnstagedChanges[0].FileName);
    }
}
