/// <summary>
/// One machine's - or one platform's - capacity used over time, kept as five short rings
/// instead of one long one.
///
/// The obvious shape, a bucket every second for six hours, is 21,600 buckets per machine
/// and gigabytes across a finished factory. Five rings of 60, one per selectable range, is
/// 300 buckets for the same six hours, because the older a bucket is the coarser it may be
/// without anyone noticing. That is what makes per-machine history affordable at all.
///
/// Buckets hold a percentage of capacity, not a rate: one byte each, and the same thing
/// the colour wash means, so a graph and the overlay cannot disagree. 300 bytes of buckets
/// works out around 35 MB across the ~90k machines of a completed save.
///
/// Belts are deliberately excluded by the caller. They outnumber machines four to one and
/// their history answers nothing a machine's does not.
/// </summary>
public sealed class MachineHistory
{
    /// <summary>Seconds covered by one bucket, per range. 60 buckets each.</summary>
    public static readonly int[] BucketSeconds = { 1, 5, 30, 60, 360 };

    /// <summary>What each range spans, for the range selector.</summary>
    public static readonly string[] RangeNames = { "1m", "5m", "30m", "1h", "6h" };

    public const int Buckets = 60;

    public static int Ranges => BucketSeconds.Length;

    /// Percent of capacity per closed bucket, one byte each. Range r occupies
    /// [r * Buckets, (r + 1) * Buckets). 255 is the cap, not a full scale: research can
    /// push a machine past its stated rate and clipping that is better than wrapping it.
    private readonly byte[] Percents = new byte[Buckets * 5];

    /// Items counted into the bucket currently open, per range.
    private readonly uint[] Counting = new uint[5];

    /// Simulated seconds at which the open bucket started, per range.
    private readonly float[] OpenedAt = new float[5];

    /// Index of the open bucket, and how many are filled: six bits per range in each.
    private uint Heads;
    private uint Filled;

    /// Items handed over as of the last advance, so the next one knows the difference.
    private long LastTotal;

    private bool Started;

    /// <summary>
    /// Folds everything produced since the last call into the open buckets, closing any
    /// that have run their span. Cheap enough to call on every machine every second.
    /// </summary>
    /// <param name="ceiling">Items per minute this could manage if never held up.</param>
    public void Advance(float nowSeconds, long total, float ceiling)
    {
        if (!Started)
        {
            // Nothing is known about the past, so start the clock here rather than
            // charging a whole session's production to the first bucket.
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
                // nobody advanced this for a while - a save loading, or a paused game -
                // and spreading one number over every skipped bucket would invent a
                // plateau that never happened.
                Close(range, closed == 0 ? ToPercent(Counting[range], span, ceiling) : (byte)0);
                Counting[range] = 0;
                OpenedAt[range] += span;
                closed++;

                // A gap longer than the whole ring: catch up rather than spin.
                if (closed >= Buckets)
                {
                    OpenedAt[range] = nowSeconds;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The series for one range as fractions of capacity, oldest first, with the open
    /// bucket's partial value last. Returns how many were written.
    /// </summary>
    public int Read(int range, float nowSeconds, float ceiling, float[] destination)
    {
        if (destination == null || range < 0 || range >= Ranges)
        {
            return 0;
        }

        int filled = FilledFor(range);
        int head = HeadFor(range);
        int written = 0;

        // The oldest filled bucket sits `filled` places behind the open one.
        for (int i = 0; i < filled && written < destination.Length; i++)
        {
            int bucket = (head - filled + i + Buckets * 2) % Buckets;
            destination[written++] = Percents[range * Buckets + bucket] / 100f;
        }

        if (written < destination.Length)
        {
            destination[written++] = Live(range, nowSeconds, ceiling);
        }

        return written;
    }

    /// <summary>How the open bucket is running so far, so a graph has a live tip.</summary>
    public float Live(int range, float nowSeconds, float ceiling)
    {
        if (range < 0 || range >= Ranges || ceiling <= 0f)
        {
            return 0f;
        }

        float elapsed = nowSeconds - OpenedAt[range];

        // Just after a bucket opens there is not enough of it to divide by.
        if (elapsed < 0.25f)
        {
            return 0f;
        }

        return Counting[range] * 60f / elapsed / ceiling;
    }

    /// <summary>How much of a range holds data, in seconds.</summary>
    public int CoveredSeconds(int range)
    {
        return range < 0 || range >= Ranges ? 0 : FilledFor(range) * BucketSeconds[range];
    }

    private void Close(int range, byte percent)
    {
        int head = HeadFor(range);
        Percents[range * Buckets + head] = percent;

        SetHead(range, (head + 1) % Buckets);

        int filled = FilledFor(range);
        if (filled < Buckets)
        {
            SetFilled(range, filled + 1);
        }
    }

    private static byte ToPercent(uint count, float seconds, float ceiling)
    {
        if (seconds <= 0f || ceiling <= 0f)
        {
            return 0;
        }

        float percent = count * 60f / seconds / ceiling * 100f;

        if (percent <= 0f)
        {
            return 0;
        }

        return percent >= 255f ? (byte)255 : (byte)(percent + 0.5f);
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
