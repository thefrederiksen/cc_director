namespace CcDirector.Core.Configuration;

public class AgentOptions
{
    public string ClaudePath { get; set; } = "claude";
    public string DefaultClaudeArgs { get; set; } = "--dangerously-skip-permissions";
    public int DefaultBufferSizeBytes { get; set; } = 2_097_152; // 2 MB
    public int GracefulShutdownTimeoutSeconds { get; set; } = 5;
}
