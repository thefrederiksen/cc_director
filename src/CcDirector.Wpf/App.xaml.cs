using System.IO;
using System.Text.Json;
using System.Windows;
using CcDirector.Core.Configuration;
using CcDirector.Core.Hooks;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf;

public partial class App : Application
{
    public SessionManager SessionManager { get; private set; } = null!;
    public AgentOptions Options { get; private set; } = null!;
    public List<RepositoryConfig> Repositories { get; private set; } = new();
    public RepositoryRegistry RepositoryRegistry { get; private set; } = null!;
    public DirectorPipeServer PipeServer { get; private set; } = null!;
    public EventRouter EventRouter { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LoadConfiguration();

        // Initialize repository registry and seed from appsettings
        RepositoryRegistry = new RepositoryRegistry();
        RepositoryRegistry.Load();
        RepositoryRegistry.SeedFrom(Repositories);

        Action<string> log = msg => System.Diagnostics.Debug.WriteLine($"[CcDirector] {msg}");

        SessionManager = new SessionManager(Options, log);
        SessionManager.ScanForOrphans();

        // Start pipe server and event router
        PipeServer = new DirectorPipeServer(log);
        EventRouter = new EventRouter(SessionManager, log);
        PipeServer.OnMessageReceived += EventRouter.Route;
        PipeServer.Start();

        // Install hooks (fire-and-forget, non-blocking startup)
        _ = InstallHooksAsync(log);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EmbeddedConsoleHost.DisposeAll();
        PipeServer.Dispose();
        SessionManager.KillAllSessionsAsync().GetAwaiter().GetResult();
        SessionManager.Dispose();
        base.OnExit(e);
    }

    private async Task InstallHooksAsync(Action<string> log)
    {
        try
        {
            var relayScriptPath = Path.Combine(AppContext.BaseDirectory, "Hooks", "hook-relay.ps1");
            if (File.Exists(relayScriptPath))
            {
                await HookInstaller.InstallAsync(relayScriptPath, log);
            }
            else
            {
                log($"Hook relay script not found at {relayScriptPath}, skipping hook installation.");
            }
        }
        catch (Exception ex)
        {
            log($"Failed to install hooks: {ex.Message}");
        }
    }

    private void LoadConfiguration()
    {
        Options = new AgentOptions();
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
            return;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Agent", out var agentSection))
            {
                if (agentSection.TryGetProperty("ClaudePath", out var cp))
                    Options.ClaudePath = cp.GetString() ?? "claude";
                if (agentSection.TryGetProperty("DefaultBufferSizeBytes", out var bs))
                    Options.DefaultBufferSizeBytes = bs.GetInt32();
                if (agentSection.TryGetProperty("GracefulShutdownTimeoutSeconds", out var gs))
                    Options.GracefulShutdownTimeoutSeconds = gs.GetInt32();
            }

            if (doc.RootElement.TryGetProperty("Repositories", out var reposSection))
            {
                Repositories = JsonSerializer.Deserialize<List<RepositoryConfig>>(
                    reposSection.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<RepositoryConfig>();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
        }
    }
}
