using ClaudeDashboard.App.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ClaudeDashboard.App;

/// <summary>The process entry point (Impl §3.1, §10.1).</summary>
/// <remarks>
/// <para>
/// The Generic Host is the outer thing and the WPF application runs inside it, which is what
/// Impl §3.1 describes — "the .NET Generic Host that also owns the WPF app, logging, and
/// background services". So this is an explicit <c>Main</c> rather than an override of
/// <c>App.OnStartup</c>: with the host built first, everything the app resolves already exists
/// by the time WPF starts, and there is no window where a WPF callback could run against a
/// half-built container.
/// </para>
/// <para>
/// <c>App.xaml</c> is an <c>ApplicationDefinition</c>, so WPF also generates an entry point;
/// <c>StartupObject</c> in the .csproj names this one, which is what keeps CS0017 away without
/// giving up the generated <c>InitializeComponent</c> and resource loading that T1.11 will use.
/// (T1.0's placeholder <c>Program.Main</c> is gone — this replaces it.)
/// </para>
/// <para>
/// <strong>Synchronous on purpose.</strong> An <c>async Task Main</c> under
/// <c>[STAThread]</c> is a trap here: a continuation after an <c>await</c> resumes on a thread
/// pool thread, and <see cref="System.Windows.Application.Run()"/> must be called on the STA
/// thread that owns the dispatcher. Blocking here costs nothing, because the dispatcher this
/// would otherwise stall does not exist until <c>Run</c> is called.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>Builds and starts the host, runs the WPF app, then shuts the host down.</summary>
    /// <returns>0 on a clean exit, 1 if startup failed.</returns>
    [STAThread]
    public static int Main()
    {
        IHost? host = null;

        try
        {
            host = AppHost.Build();
            host.Start();

            var policy = host.Services.GetRequiredService<UnhandledExceptionPolicy>();
            using var handlers = AppHost.WireProcessExceptionHandlers(policy);

            var app = new App(policy);
            var exitCode = app.Run();

            host.StopAsync().GetAwaiter().GetResult();
            Log.Information("Claude Dashboard exited with code {ExitCode}.", exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            // Startup failed before the handlers could take over. Nothing is running to report
            // this any other way, so the log file is the only place it can go.
            Log.Fatal(ex, "Claude Dashboard failed to start.");
            return 1;
        }
        finally
        {
            host?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
