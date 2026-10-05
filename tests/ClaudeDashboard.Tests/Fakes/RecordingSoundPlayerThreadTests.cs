using System.Collections.Concurrent;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// <see cref="RecordingSoundPlayer"/> is safe to read on one thread while another plays (T1.75, for issue #112): in a
/// pipeline test the consumer thread plays while the test thread polls.
/// </summary>
public sealed class RecordingSoundPlayerThreadTests
{
    /// <summary>The plays of the writing thread: enough that a reader without the lock meets an add.</summary>
    private const int Plays = 100_000;

    /// <summary>
    /// <strong>One thread plays many times while another reads every member in a loop, and nothing throws.</strong>
    /// Without the lock, a read during an add threw "Collection was modified" in the T1.75 plant runs.
    /// </summary>
    [Fact]
    public void Every_member_can_be_read_while_another_thread_plays()
    {
        var player = new RecordingSoundPlayer();
        var failures = new ConcurrentQueue<Exception>();
        using var start = new Barrier(2);

        var writer = new Thread(() =>
        {
            start.SignalAndWait();

            for (var i = 0; i < Plays; i++)
            {
                player.Play(i % 2 == 0 ? SoundId.Finished : SoundId.Permission, 1.0, TimeSpan.Zero);
            }
        });

        writer.Start();
        start.SignalAndWait();

        var reads = 0;

        while (writer.IsAlive)
        {
            try
            {
                // Every member, and an enumeration of the list itself, as a test's wait loop reads them.
                _ = player.Played.Count(played => played.Gain > 0);
                _ = player.Last;
                _ = player.Gains.Sum();
                _ = player.PlayedOf(SoundId.Finished).Count;
                reads++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                failures.Enqueue(ex);
                break;
            }
        }

        writer.Join();

        Assert.Empty(failures);
        Assert.True(reads > 0, "the reader never ran while the writer played");
        Assert.Equal(Plays, player.Played.Count);
        Assert.Equal(Plays / 2, player.PlayedOf(SoundId.Finished).Count);
    }
}
