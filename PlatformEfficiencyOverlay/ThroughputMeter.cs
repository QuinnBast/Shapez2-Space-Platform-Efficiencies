using System;
using Game.Core.Simulation;

/// <summary>
/// Counts the items a lane accepts and reports the rate over a window of simulation time
/// (so it stays correct when the game is paused or sped up).
///
/// The window is not fixed. A busy belt delivers hundreds of items in a few seconds and
/// wants a short window so the overlay reacts; a machine producing one item every twenty
/// seconds needs a long one or the answer alternates between zero and double. So the
/// window grows backwards until it holds enough arrivals to divide by - which is the same
/// statistic the game's own efficiency gauge computes from its arrival timestamps, just
/// bucketed rather than stored one per item.
///
/// Counting is a single array increment on the item handover itself.
/// </summary>
public class ThroughputMeter
{
    private const int BucketCount = 13;
    private const int MinSamples = 3;

    private static readonly Ticks BucketDuration = Ticks.FromSeconds(5f);

    /// Nothing arriving for this long means stopped, not slow.
    private const int StoppedAfterBuckets = 5;

    private readonly int[] Buckets = new int[BucketCount];
    private int Head;
    private Ticks BucketStart;
    private bool Started;

    /// Hooked onto a lane's PostAcceptHook - runs once per item that enters the lane.
    public void CountItem(IItemReceiver receiver, IBeltItem item)
    {
        Buckets[Head]++;
    }

    /// Rolls the window forward to <paramref name="now"/>. Cheap when nothing has expired.
    public void Advance(Ticks now)
    {
        if (!Started)
        {
            Started = true;
            BucketStart = now;
            return;
        }

        long elapsed = now.Value - BucketStart.Value;

        // A backwards clock means a different save. Everything measured belongs to the old
        // one, and waiting for simulation time to catch up would mean waiting it out.
        if (elapsed < 0)
        {
            Array.Clear(Buckets, 0, BucketCount);
            Head = 0;
            BucketStart = now;
            return;
        }

        if (elapsed < BucketDuration.Value)
        {
            return;
        }

        long steps = elapsed / BucketDuration.Value;
        if (steps >= BucketCount)
        {
            // Nothing in the window is still current (overlay was off, or the game paused).
            Array.Clear(Buckets, 0, BucketCount);
            Head = 0;
        }
        else
        {
            for (long i = 0; i < steps; i++)
            {
                Head = (Head + 1) % BucketCount;
                Buckets[Head] = 0;
            }
        }

        BucketStart = new Ticks(BucketStart.Value + steps * BucketDuration.Value);
    }

    /// <summary>
    /// Items per minute, measured over the shortest run of completed buckets that holds
    /// enough arrivals to be meaningful.
    /// </summary>
    public float ItemsPerMinute(Ticks now)
    {
        Advance(now);

        int total = 0;
        int bucketsUsed = 0;
        int quietAtTheEnd = 0;

        // Walk back from the newest completed bucket; the one at Head is still filling.
        for (int i = 1; i < BucketCount; i++)
        {
            int index = (Head - i + BucketCount) % BucketCount;
            bucketsUsed++;
            total += Buckets[index];

            if (total == 0)
            {
                quietAtTheEnd++;
            }
            else if (total >= MinSamples)
            {
                break;
            }
        }

        if (total == 0 || quietAtTheEnd >= StoppedAfterBuckets)
        {
            return 0f;
        }

        return total * 60f / (bucketsUsed * BucketDuration.FloatSeconds);
    }
}
