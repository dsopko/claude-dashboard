using System.IO;
using Microsoft.AspNetCore.Connections;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ui;
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
/// <strong>This is the only entry point, and that took arranging.</strong> As an
/// <c>ApplicationDefinition</c>, <c>App.xaml</c> makes WPF generate a <c>Main</c> of its own
/// that constructs <c>new App()</c> — which collides with this one (CS0017) and, once
/// <see cref="App"/> takes a constructor argument, does not compile at all. Naming this method
/// in <c>StartupObject</c> would not have been enough: it resolves which <c>Main</c> runs, not
/// whether the generated one compiles. So the .csproj compiles <c>App.xaml</c> as a
/// <c>Page</c> instead, which still generates <c>InitializeComponent</c> and the resource
/// loading T1.11 will use, but generates no entry point. (T1.0's placeholder
/// <c>Program.Main</c> is gone — this replaces it.)
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

            // This is the UI thread, and the only place the window and its view model may be
            // built. Attaching the tick here rather than inside the container is what keeps the
            // consumer thread from ever constructing UI-thread state (see UiTick).
            var window = host.Services.GetRequiredService<MainWindow>();
            var tick = host.Services.GetRequiredService<UiTick>();
            tick.Attach(window.ViewModel);

            // The tray is a Win32 shell notification and a WPF ContextMenu, so it belongs on
            // this thread with the window. Constructing it also puts it on the clock — see
            // TrayIcon, which attaches itself so that being registered and being driven cannot
            // come apart the way T1.6's and T1.11's ticks did.
            using var tray = host.Services.GetRequiredService<TrayIcon>();
            tray.ViewModel.OpenRequested += (_, _) => window.ToggleDashboard();
            tray.ViewModel.QuitRequested += (_, _) => app.Shutdown();

            var exitCode = app.Run(window);

            host.StopAsync().GetAwaiter().GetResult();
            Log.Information("Claude Dashboard exited with code {ExitCode}.", exitCode);
            return exitCode;
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            // Impl §3.1 fixes the port so the hook URL stays stable, which makes "something
            // else already has it" the likeliest startup failure this app will ever have. The
            // operator has no console and no window, so the log line has to be the whole
            // diagnosis — a stack trace alone would say what happened but not what to do.
            Log.Fatal(
                ex,
                "Claude Dashboard could not start: another process is already using the ingress port. " +
                "Stop that process, or set a different \"port\" in the settings file, and remember that " +
                "the hook URL registered with Claude Code must match it.");
            return 1;
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
