using System.Diagnostics;

namespace CcDirector.Core.Git;

public enum GitFileStatus { Modified, Added, Deleted, Renamed, Copied, Untracked, Unknown }

public class GitFileEntry
{
    public GitFileStatus Status { get; init; }
    public string StatusChar { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool IsStaged { get; init; }
}

public class GitStatusResult
{
    public List<GitFileEntry> StagedChanges { get; init; } = new();
    public List<GitFileEntry> UnstagedChanges { get; init; } = new();
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public class GitStatusProvider
{
    public async Task<GitStatusResult> GetStatusAsync(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain=v1 -u",
                WorkingDirectory = repoPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new GitStatusResult { Success = false, Error = "Failed to start git process" };

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return new GitStatusResult { Success = false, Error = error };

            return ParsePorcelainOutput(output);
        }
        catch (Exception ex)
        {
            return new GitStatusResult { Success = false, Error = ex.Message };
        }
    }

    public static GitStatusResult ParsePorcelainOutput(string output)
    {
        var staged = new List<GitFileEntry>();
        var unstaged = new List<GitFileEntry>();

        if (string.IsNullOrWhiteSpace(output))
            return new GitStatusResult { StagedChanges = staged, UnstagedChanges = unstaged, Success = true };

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
                continue;

            var x = line[0]; // index (staged) status
            var y = line[1]; // worktree (unstaged) status
            var filePath = line[3..].Trim();

            // Handle renames: "R  old -> new"
            if (filePath.Contains(" -> "))
                filePath = filePath.Split(" -> ")[1];

            // Strip trailing slashes from directory entries
            filePath = filePath.TrimEnd('/', '\\');

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
                fileName = filePath;

            // Untracked files
            if (x == '?' && y == '?')
            {
                unstaged.Add(new GitFileEntry
                {
                    Status = GitFileStatus.Untracked,
                    StatusChar = "?",
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = false
                });
                continue;
            }

            // Staged changes (X is non-space)
            if (x != ' ')
            {
                staged.Add(new GitFileEntry
                {
                    Status = CharToStatus(x),
                    StatusChar = x.ToString(),
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = true
                });
            }

            // Unstaged changes (Y is non-space)
            if (y != ' ')
            {
                unstaged.Add(new GitFileEntry
                {
                    Status = CharToStatus(y),
                    StatusChar = y.ToString(),
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = false
                });
            }
        }

        return new GitStatusResult { StagedChanges = staged, UnstagedChanges = unstaged, Success = true };
    }

    private static GitFileStatus CharToStatus(char c) => c switch
    {
        'M' => GitFileStatus.Modified,
        'A' => GitFileStatus.Added,
        'D' => GitFileStatus.Deleted,
        'R' => GitFileStatus.Renamed,
        'C' => GitFileStatus.Copied,
        '?' => GitFileStatus.Untracked,
        _ => GitFileStatus.Unknown
    };
}
