using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.Tests.Fakes;
using Serilog;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The storm guard: a fault that repeats on every render is counted, not written (Impl §10.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The residual this closes.</strong> The dispatcher policy marks every exception handled,
/// deliberately, so a converter that throws during a render pass is absorbed and no test ever
/// sees it. That is the design; what it costs is that the same fault is absorbed on every render,
/// and at one log line apiece the file fills with the failure and buries the diagnostics that
/// would explain it. The guard does not make the fault visible — it makes it diagnosable.
/// </para>
/// <para>
/// <strong>Every test here carries its negative.</strong> A rate limiter has an obvious
/// degenerate implementation that passes the positive test: suppress everything after the first.
/// So the suite pins that a second distinct fault is not suppressed by the first, that the count
/// in the summary is right rather than merely present, and that an occasional fault is still
/// written in full every time.
/// </para>
/// </remarks>
public sealed class StormGuardTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly RecordingLogSink _sink = new();
    private readonly FakeClock _clock = new();
    private readonly UnhandledExceptionPolicy _policy;

    public StormGuardTests()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
        _policy = new UnhandledExceptionPolicy(logger, _clock, Window);
    }

    /// <summary>The same fault, thrown from the same place, over and over.</summary>
    private static Exception ConverterFault(string message = "the converter threw")
    {
        try
        {
            return Throw(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        static Exception Throw(string message) => throw new InvalidOperationException(message);
    }

    /// <summary>A different fault: same type, a different throwing method.</summary>
    private static Exception BindingFault(string message = "the binding threw")
    {
        try
        {
            return ThrowElsewhere(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        static Exception ThrowElsewhere(string message) => throw new InvalidOperationException(message);
    }

    /// <summary>A different fault again: a different type from the same place.</summary>
    private static Exception NullFault()
    {
        try
        {
            return Throw();
        }
        catch (ArgumentNullException ex)
        {
            return ex;
        }

        static Exception Throw() => throw new ArgumentNullException("row");
    }

    private int Summaries => _sink.Containing("more times in");

    // ---- What must happen -------------------------------------------------------------------

    /// <summary>
    /// The first sighting is written in full, unsuppressed — it carries the stack trace, which is
    /// the only thing in the file that says what actually broke.
    /// </summary>
    [Fact]
    public void The_first_occurrence_is_logged_with_its_exception()
    {
        _policy.HandleDispatcherException(ConverterFault());

        Assert.Equal(1, _sink.WithException);
        Assert.Equal(1, _sink.Containing("Unhandled exception on the UI thread"));
    }

    /// <summary>
    /// Fifteen rows re-rendering on a tick is one fault many times. One line, then silence, then
    /// one summary a minute later.
    /// </summary>
    [Fact]
    public void A_repeating_fault_is_logged_once_and_then_counted()
    {
        for (var i = 0; i < 900; i++)
        {
            _policy.HandleDispatcherException(ConverterFault());
        }

        // Nine hundred faults, one line.
        Assert.Single(_sink.Events);
        Assert.Equal(0, Summaries);

        _clock.Advance(Window);
        _policy.HandleDispatcherException(ConverterFault());

        var summary = Assert.Single(_sink.Matching("more times in"));
        Assert.Contains("failed 899 more times", summary, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is right, not merely present. A summary that always said "many" would satisfy
    /// the test above.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(150)]
    public void The_summary_count_is_the_number_that_were_suppressed(int repeats)
    {
        _policy.HandleDispatcherException(ConverterFault());

        for (var i = 0; i < repeats; i++)
        {
            _policy.HandleDispatcherException(ConverterFault());
        }

        _clock.Advance(Window);
        _policy.HandleDispatcherException(ConverterFault());

        Assert.Contains(
            $"failed {repeats} more times",
            Assert.Single(_sink.Matching("more times in")),
            StringComparison.Ordinal);
    }

    /// <summary>One summary per window, not one per fault, however long the storm runs.</summary>
    [Fact]
    public void A_long_storm_produces_one_summary_per_window()
    {
        for (var minute = 0; minute < 5; minute++)
        {
            for (var i = 0; i < 200; i++)
            {
                _policy.HandleDispatcherException(ConverterFault());
            }

            _clock.Advance(Window);
        }

        // The fifth window's count is still open — five minutes of storm, one opening line and
        // four summaries so far.
        Assert.Equal(4, Summaries);
        Assert.Equal(1, _sink.WithException);

        _policy.Flush();
        Assert.Equal(5, Summaries);
    }

    // ---- Flushing the tail ------------------------------------------------------------------

    /// <summary>
    /// A storm that stops mid-window must not take its last count with it. Nothing here runs a
    /// timer — the process is allowed one periodic loop and it belongs to the event consumer —
    /// so a window only expires when the fault happens again, and the tail would otherwise be
    /// lost exactly when the storm ended.
    /// </summary>
    [Fact]
    public void The_final_partial_window_is_flushed()
    {
        for (var i = 0; i < 41; i++)
        {
            _policy.HandleDispatcherException(ConverterFault());
        }

        _clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(0, Summaries);

        _policy.Flush();

        var summary = Assert.Single(_sink.Matching("more times in"));
        Assert.Contains("failed 40 more times", summary, StringComparison.Ordinal);
        Assert.Contains("in 40s", summary, StringComparison.Ordinal);
    }

    /// <summary>A flush with nothing held writes nothing — shutdown is not a log entry.</summary>
    [Fact]
    public void Flushing_an_empty_guard_says_nothing()
    {
        _policy.Flush();
        _policy.HandleDispatcherException(ConverterFault());
        _sink.Clear();
        _policy.Flush();

        Assert.Empty(_sink.Events);
    }

    /// <summary>
    /// And something actually calls it. Unwiring the process-wide handlers is the shutdown path,
    /// so that is where the tail is written.
    /// </summary>
    /// <remarks>
    /// The composition half. Without this the guard would behave correctly in every test here and
    /// still lose the last count of every real storm, because nothing in the process would ever
    /// have called <c>Flush</c>.
    /// </remarks>
    [Fact]
    public void Unwiring_the_process_handlers_flushes_what_is_held()
    {
        var wiring = AppHost.WireProcessExceptionHandlers(_policy);

        _policy.HandleDispatcherException(ConverterFault());
        _policy.HandleDispatcherException(ConverterFault());
        Assert.Equal(0, Summaries);

        wiring.Dispose();

        Assert.Equal(1, Summaries);
    }

    /// <summary>And a second flush does not re-report what the first already wrote.</summary>
    [Fact]
    public void A_flush_does_not_repeat_itself()
    {
        _policy.HandleDispatcherException(ConverterFault());
        _policy.HandleDispatcherException(ConverterFault());

        _policy.Flush();
        _policy.Flush();

        Assert.Equal(1, Summaries);
    }

    /// <summary>
    /// The AppDomain handler flushes first: when the CLR is tearing the process down, the counts
    /// have one chance to reach disk and it is before that line, not after.
    /// </summary>
    [Fact]
    public void A_terminating_fault_flushes_the_counts_before_it_writes()
    {
        _policy.HandleDispatcherException(ConverterFault());
        _policy.HandleDispatcherException(ConverterFault());

        _policy.HandleDomainException(new InvalidOperationException("the end"), isTerminating: true);

        var messages = _sink.Messages;
        var summary = messages.ToList().FindIndex(m => m.Contains("more times in", StringComparison.Ordinal));
        var fatal = messages.ToList().FindIndex(m => m.Contains("the process is terminating", StringComparison.Ordinal));

        Assert.True(summary >= 0, "the held count was never written");
        Assert.True(fatal > summary, "the count must reach the log before the terminating line");
    }

    // ---- What must NOT happen ----------------------------------------------------------------

    /// <summary>
    /// <strong>The degenerate implementation's test.</strong> A guard that suppressed everything
    /// after the first fault would pass every positive test above. A second, different fault is
    /// a different fault, and it gets its own line immediately.
    /// </summary>
    [Fact]
    public void A_different_fault_is_not_suppressed_by_the_first()
    {
        for (var i = 0; i < 50; i++)
        {
            _policy.HandleDispatcherException(ConverterFault());
        }

        _policy.HandleDispatcherException(BindingFault());

        Assert.Equal(2, _sink.WithException);
        Assert.Equal(2, _sink.Events.Count);
    }

    /// <summary>Same type, different throwing method: still two faults.</summary>
    [Fact]
    public void Two_sites_throwing_the_same_type_are_counted_separately()
    {
        _policy.HandleDispatcherException(ConverterFault());
        _policy.HandleDispatcherException(BindingFault());

        for (var i = 0; i < 10; i++)
        {
            _policy.HandleDispatcherException(ConverterFault());
        }

        for (var i = 0; i < 3; i++)
        {
            _policy.HandleDispatcherException(BindingFault());
        }

        _policy.Flush();

        Assert.Equal(2, Summaries);
        Assert.Single(_sink.Matching("failed 10 more times"));
        Assert.Single(_sink.Matching("failed 3 more times"));
    }

    /// <summary>Different type, same throwing method: also two faults.</summary>
    [Fact]
    public void Two_types_from_one_site_are_counted_separately()
    {
        _policy.HandleDispatcherException(ConverterFault());
        _policy.HandleDispatcherException(NullFault());

        Assert.Equal(2, _sink.WithException);
    }

    /// <summary>
    /// The message is not part of the key. A converter failing on fifteen rows produces fifteen
    /// messages carrying fifteen row values, and keying on those would turn one storm back into
    /// fifteen — which is the flood the guard exists to prevent.
    /// </summary>
    [Fact]
    public void The_same_fault_with_different_messages_is_still_one_fault()
    {
        for (var row = 0; row < 15; row++)
        {
            _policy.HandleDispatcherException(ConverterFault($"row {row} would not convert"));
        }

        Assert.Single(_sink.Events);

        _policy.Flush();
        Assert.Contains(
            "failed 14 more times",
            Assert.Single(_sink.Matching("more times in")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An occasional fault is not a storm. Spaced further apart than the window, each occurrence
    /// is written in full — a guard that swallowed them into a count would be hiding stack traces
    /// nobody asked it to hide.
    /// </summary>
    [Fact]
    public void An_occasional_repeat_is_logged_in_full_every_time()
    {
        for (var i = 0; i < 4; i++)
        {
            _policy.HandleDispatcherException(ConverterFault());
            _clock.Advance(Window + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(4, _sink.WithException);
        Assert.Equal(0, Summaries);
    }

    /// <summary>
    /// Which exceptions are handled does not change. Every one is still marked handled and still
    /// counted — the guard decides what is written, and nothing else.
    /// </summary>
    [Fact]
    public void Suppression_changes_nothing_about_handling()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(_policy.HandleDispatcherException(ConverterFault()));
        }

        Assert.Equal(100, _policy.ObservedCount);
        Assert.Equal(99, _policy.SuppressedCount);
    }

    /// <summary>
    /// Unobserved task faults are not rate-limited: the finalizer raises one per dropped task,
    /// not one per render, so there is no storm and every one is worth its line.
    /// </summary>
    [Fact]
    public void Unobserved_task_faults_are_not_suppressed()
    {
        for (var i = 0; i < 5; i++)
        {
            _policy.HandleUnobservedTaskException(ConverterFault());
        }

        Assert.Equal(5, _sink.WithException);
    }

    [Fact]
    public void The_policy_needs_an_exception()
    {
        Assert.Throws<ArgumentNullException>(() => _policy.HandleDispatcherException(null!));
    }
}
