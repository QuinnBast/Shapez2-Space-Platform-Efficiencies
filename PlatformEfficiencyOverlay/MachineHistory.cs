using System;

/// <summary>
/// One machine's throughput over time, kept as four short rings instead of one long one.
///
/// The obvious shape - a bucket every 5 seconds for six hours - is 4,320 buckets, which
/// across a completed factory is a quarter of a gigabyte. Four rings of 60, one per
/// selectable range, is 240 buckets for the same six hours, because the older a bucket is
/// the coarser it may be without anyone noticing. That is the whole trick, and it is what
/// makes per-machine history affordable at all: about half a kilobyte each, so roughly
/// 45 MB across the ~90k processing machines of a finished save.
///
/// Belts are deliberately excluded by the caller. They outnumber machines four to one and
/// their history answers nothing a machine's does not - including them is the difference
/// between 45 MB and 250 MB.
///
/// Buckets hold items per minute, not item counts. Rates are directly comparable across
/// ranges, which is what a graph needs, and a rate fits a ushort with room to spare where
/// a six-minute count would be flirting with overflow.
/// </summary>
public sealed class MachineHistory
{
    /// <summary>Seconds covered by one bucket, per range: 5m, 30m, 1h, 6h across 60 buckets.</summary>
    public static readonly int[] BucketSeconds = { 5, 30, 60, 360 };

    /// <summary>Short labels for the ranges, in the same order.</summary>
    public static readonly string[] RangeNames = { "5m", "30m", "1h", "6h" };

    public const int Buckets = 60;
    public static int Ranges => BucketSeconds.Length;

    /// Closed buckets, items per minute. Range r occupies [r * Buckets, (r + 1) * Buckets).
    private readonly ushort[] Rates = new ushort[Buckets * 4];

    /// Items counted into the bucket currently open, per range.
    private readonly uint[] Counting = new uint[4];

    /// Simulated seconds at which the open bucket started, per range.
    private readonly float[] OpenedAt = new float[4];

    /// Index of the open bucket and how many are filled, six bits per range each.
    private uint Heads;
    private uint Filled;

    /// Items this machine had produced when it was last advanced.
    private long LastTotal;

    private bool Started;

    /// <summary>
    /// Folds everything produced since the last call into the open buckets, closing any
    /// that have run their span.
    ///
    /// Cheap enough to call on every machine every second: a handful of adds, and a
    /// bucket actually closes at most once per range per call.
    /// </summary>
    public void Advance(float nowSeconds, long total)
    {
        if (!Started)
        {
            // Nothing is known about the past, so start the clock here rather than
            // attributing a lifetime of production to the first bucket.
            Started = true;
            LastTotal = total;

            for (int range = 0; range < Ranges; range++)
            {
                OpenedAt[range] = nowSeconds;
            }

            return;
        }

        long produced = total - LastTotal;
        LastTotal = total;

        if (produced < 0)
        {
            produced = 0;
        }

        for (int range = 0; range < Ranges; range++)
        {
            Counting[range] += (uint)produced;

            float span = BucketSeconds[range];
            int closed = 0;

            while (nowSeconds - OpenedAt[range] >= span)
            {
                // Only the first close of a long gap gets the counted items. A gap means
                // nobody looked for a while - the save was loading, or the game was
                // paused - and spreading one number across every skipped bucket would
                // invent a plateau that never happened.
                Close(range, closed == 0 ? ToRate(Counting[range], span) : (ushort)0);
                Counting[range] = 0;
                OpenedAt[range] += span;
                closed++;

                // A gap longer than the whole ring: stop rather than spin sixty times.
                if (closed >= Buckets)
                {
                    OpenedAt[range] = nowSeconds;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The series for one range, oldest first, with the open bucket's partial rate last.
    /// Returns how many entries were written; the rest of the destination is untouched.
    /// </summary>
    public int Read(int range, float nowSeconds, float[] destination)
    {
        if (destination == null || range < 0 || range >= Ranges)
        {
            return 0;
        }

        int filled = FilledFor(range);
        int head = HeadFor(range);
        int written = 0;
        int room = destination.Length;

        // The oldest filled bucket sits `filled` places behind the open one.
        for (int i = 0; i < filled && written < room; i++)
        {
            int bucket = (head - filled + i + Buckets * 2) % Buckets;
            destination[written++] = Rates[range * Buckets + bucket];
        }

        if (written < room)
        {
            destination[written++] = Live(range, nowSeconds);
        }

        return written;
    }

    /// <summary>The rate the open bucket is running at so far, so a graph has a live tip.</summary>
    public float Live(int range, float nowSeconds)
    {
        if (range < 0 || range >= Ranges)
        {
            return 0f;
        }

        float elapsed = nowSeconds - OpenedAt[range];

        // Right after a bucket opens there is not enough of it to divide by.
        return elapsed < 0.5f ? 0f : Counting[range] * 60f / elapsed;
    }

    /// <summary>How much of a range actually holds data, in seconds of coverage.</summary>
    public int CoveredSeconds(int range)
    {
        return range < 0 || range >= Ranges ? 0 : FilledFor(range) * BucketSeconds[range];
    }

    private void Close(int range, ushort rate)
    {
        int head = HeadFor(range);
        Rates[range * Buckets + head] = rate;

        SetHead(range, (head + 1) % Buckets);

        int filled = FilledFor(range);
        if (filled < Buckets)
        {
            SetFilled(range, filled + 1);
        }
    }

    private static ushort ToRate(uint count, float seconds)
    {
        if (seconds <= 0f)
        {
            return 0;
        }

        float rate = count * 60f / seconds;
        return rate >= ushort.MaxValue ? ushort.MaxValue : (ushort)rate;
    }

    private int HeadFor(int range)
    {
        return (int)((Heads >> (range * 6)) & 0x3F);
    }

    private void SetHead(int range, int value)
    {
        int shift = range * 6;
        Heads = (Heads & ~(0x3Fu << shift)) | ((uint)value << shift);
    }

    private int FilledFor(int range)
    {
        return (int)((Filled >> (range * 6)) & 0x3F);
    }

    private void SetFilled(int range, int value)
    {
        int shift = range * 6;
        Filled = (Filled & ~(0x3Fu << shift)) | ((uint)value << shift);
    }
}
