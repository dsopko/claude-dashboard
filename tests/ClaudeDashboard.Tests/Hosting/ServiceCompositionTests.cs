using System.IO;
using System.Linq;
using System.Reflection;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// No silent collaborators (T1.12b): a missing registration must fail loudly at startup rather
/// than quietly degrading the app.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The class this closes.</strong> Five instances of one bug were found by hand before
/// this existed — T1.6's unowned tick, T1.11's <c>UiTick</c> registration, T1.11a's
/// <c>Flush</c>, T1.11's collapsed-row restatement, T1.12's ack publisher — and the cause was
/// never a forgotten test. It is that <strong>an optional collaborator's absence is
/// silent</strong>. Microsoft DI honours a constructor default for a service it cannot resolve
/// instead of throwing, so a deleted registration becomes a program that starts, renders, and
/// does less. Deleting one line left 746 tests green with every Ack button in the shipped app
/// permanently disabled.
/// </para>
/// <para>
/// <strong>A property, not a list.</strong> This asserts something about the shape of the
/// container rather than enumerating the types in it, so a type added next month is covered the
/// day it is registered and nobody has to remember. That is also why there is no allowlist — see
/// <see cref="The_carve_out_is_not_an_exemption"/>.
/// </para>
/// </remarks>
public sealed class ServiceCompositionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public ServiceCompositionTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DashboardPaths(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// For every type the container constructs, no constructor parameter that is itself a
    /// registered service may have a default value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parameter that is both <em>a registered service</em> and <em>optional</em> is the whole
    /// bug in one sentence: the container fills it while the registration exists and silently
    /// falls back to the default the moment it does not. Requiring it turns the same deletion
    /// into a throw at startup.
    /// </para>
    /// <para>
    /// A default on a parameter that is <em>not</em> a service — <c>EventPipeline</c>'s
    /// <c>capacity</c>, <c>UnhandledExceptionPolicy</c>'s <c>suppressionWindow</c> — is
    /// deliberate configuration rather than a collaborator, and the container never had anything
    /// to put there. Those are left alone.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_type_the_container_constructs_has_an_optional_registered_collaborator()
    {
        using var host = AppHost.Build(_paths);
        var isService = host.Services.GetRequiredService<IServiceProviderIsService>();

        var offenders = ConstructedTypes(host.Services)
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(ctor => ctor.GetParameters().Select(parameter => (Ctor: ctor, Parameter: parameter)))
            .Where(found => found.Parameter.HasDefaultValue && isService.IsService(found.Parameter.ParameterType))
            .Select(found =>
                $"{found.Ctor.DeclaringType!.Name}.{found.Parameter.Name} : {found.Parameter.ParameterType.Name}")
            .Distinct()
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These collaborators are registered services and also optional, so deleting their "
            + "registration would degrade the app silently instead of failing at startup. Make them "
            + "required; do not add an exemption here." + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// The guard's coverage boundary, stated rather than assumed: it sees exactly the types whose
    /// constructor arguments the <em>container</em> chooses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A descriptor carries an implementation type, a pre-built instance, or a factory, and only
    /// the first is a hazard. A pre-built instance was constructed by us before the container ever
    /// saw it, so no default was filled by anybody. A factory names its own arguments at the call
    /// site in <c>AppHost</c>, where an omission is visible in the source rather than silent — a
    /// different bug with a different remedy, and this guard does not claim it.
    /// </para>
    /// <para>
    /// Without the descriptor seam in <c>AppHost</c> the visible set collapses to concretely
    /// registered types, and every type registered behind an interface — <c>AckPublisher</c>,
    /// <c>WpfDispatcher</c>, <c>SystemClock</c> — becomes invisible to
    /// <see cref="IServiceProviderIsService"/>. That was measured on a clean host, and it is why
    /// the seam exists.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_guard_sees_every_type_the_container_constructs_including_those_behind_interfaces()
    {
        using var host = AppHost.Build(_paths);

        var seen = ConstructedTypes(host.Services).Select(type => type.Name).ToList();

        // Registered concretely — visible with or without the seam.
        Assert.Contains(nameof(SessionRegistry), seen);

        // Registered behind an interface — visible only because AppHost publishes its descriptors.
        Assert.Contains(nameof(AckPublisher), seen);
        Assert.Contains(nameof(WpfDispatcher), seen);
        Assert.Contains(nameof(SystemClock), seen);

        // And the boundary itself: a pre-built instance has no implementation type to inspect,
        // because nothing in the container constructed it.
        var descriptors = host.Services.GetRequiredService<IReadOnlyList<ServiceDescriptor>>();
        Assert.Contains(descriptors, d => d.ImplementationInstance is not null && d.ImplementationType is null);
    }

    /// <summary>
    /// The carve-out falls out of the property; it is not an exemption anyone maintains.
    /// </summary>
    /// <remarks>
    /// <see cref="SessionViewModel"/> takes an optional <c>motion</c> and an optional <c>ack</c>,
    /// both registered services — so a naive form of this guard would flag it. It is not flagged,
    /// and not because it is on a list: rows are built by <see cref="MainViewModel"/> and never
    /// resolved from a container, so the container never chooses those arguments and their
    /// defaults cannot mask a lost registration. The property does the discriminating, which is
    /// what makes this different from enumerating what happens to be wrong today.
    /// </remarks>
    [Fact]
    public void The_carve_out_is_not_an_exemption()
    {
        using var host = AppHost.Build(_paths);
        var isService = host.Services.GetRequiredService<IServiceProviderIsService>();

        var optional = typeof(SessionViewModel)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Where(parameter => parameter.HasDefaultValue && isService.IsService(parameter.ParameterType))
            .Select(parameter => parameter.Name)
            .ToList();

        // It really does have the shape the guard looks for…
        Assert.NotEmpty(optional);

        // …and it is out of scope because the container never constructs one.
        Assert.DoesNotContain(typeof(SessionViewModel), ConstructedTypes(host.Services));
    }

    /// <summary>
    /// One guard, shared. The Registry and the sound engine must sit inside the <em>same</em>
    /// mutual-exclusion region, not each inside its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Making the guard required stops it going <em>missing</em>; it does not stop it being
    /// <em>duplicated</em>. <c>AddTransient</c> in place of <c>AddSingleton</c> would hand the
    /// Registry, the sound engine and the consumer a guard each — every constructor satisfied,
    /// nothing null, nothing thrown, and the invariant the lock-free design rests on quietly
    /// gone, because a thread inside the Registry could then also be inside the sound engine.
    /// That is the same shape as the transient-<c>EventPipeline</c> mutation that broke publisher
    /// from consumer on T1.12.
    /// </para>
    /// <para>
    /// <strong>Shown, not inferred.</strong> Two resolves returning the same instance pins only
    /// the <em>lifetime</em>; that the Registry and the engine hold <em>that</em> instance would
    /// then be an inference from the parameter being required, and reasoning-by-construction is
    /// what failed us last cycle. So the region is held on another thread and both are asked to
    /// write: sharing it, they must refuse. The guard is re-entrant for its owning thread by
    /// design — the invariant is one thread, not one call — which is why this needs a second
    /// thread rather than a nested <c>Enter</c>.
    /// </para>
    /// <para>
    /// <strong>Why the Registry's half asserts the write did not happen.</strong> The throw alone
    /// does not distinguish the two guards. <c>AppHost</c> wires
    /// <c>registry.SessionChanged += … sound.OnSessionChanged(…)</c>, so a Registry sitting in a
    /// <em>private</em> region enters it unopposed, does the work, raises the notification, and
    /// the <em>engine</em> throws from the shared region — a
    /// <see cref="SingleWriterViolationException"/> comes out of <c>Apply</c> either way and
    /// <c>Assert.Throws</c> cannot tell them apart. <c>Apply</c> stores the session on the line
    /// before it raises, so the surviving evidence is the store: if the Registry shares the
    /// region it is refused entry and writes nothing, and if it does not, the session is already
    /// there when the engine throws. Asserting the throw <em>prevented</em> the write is what
    /// separates them.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_single_writer_guard_is_one_shared_instance()
    {
        using var host = AppHost.Build(_paths);

        var guard = host.Services.GetRequiredService<SingleWriterGuard>();
        Assert.Same(guard, host.Services.GetRequiredService<SingleWriterGuard>());

        var registry = host.Services.GetRequiredService<SessionRegistry>();
        var sound = host.Services.GetRequiredService<SoundPolicyEngine>();

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            using var scope = guard.Enter("the test is holding the region");
            held.Set();
            release.Wait();
        })
        {
            IsBackground = true,
        };

        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "the holding thread never entered the region.");

        try
        {
            Assert.Throws<SingleWriterViolationException>(() => registry.Apply(new SessionStart
            {
                SessionId = new SessionId("s-1"),
                Timestamp = DateTimeOffset.UtcNow,
                Cwd = _root,
                Source = "startup",
            }));

            // The throw must have *prevented* the write. See the remarks: without this, a Registry
            // in a private region is refused by nothing, stores the session, and the engine throws
            // downstream — which satisfies Assert.Throws just as well.
            Assert.Empty(registry.Sessions);

            Assert.Throws<SingleWriterViolationException>(
                () => sound.SetSessionMuted(new SessionId("s-1"), muted: true));
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(5)), "the holding thread never left the region.");
        }
    }

    /// <summary>Every type this container will construct itself, from our own assemblies.</summary>
    private static IReadOnlyList<Type> ConstructedTypes(IServiceProvider services) =>
        [.. services.GetRequiredService<IReadOnlyList<ServiceDescriptor>>()
            .Select(descriptor => descriptor.ImplementationType)
            .OfType<Type>()
            .Where(type => type.Assembly.GetName().Name?
                .StartsWith("ClaudeDashboard", StringComparison.Ordinal) == true)
            .Distinct()];
}
