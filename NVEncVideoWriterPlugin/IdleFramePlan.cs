namespace NVEncVideoWriterPlugin;

// A bounded, allocation-free traversal. Every frame appears once, including frames before the CTI.
internal static class IdleFramePlan
{
    internal static (int Start, int End) Range(int length, int start, int endExclusive)
    {
        length = Math.Max(0, length);
        int first = Math.Clamp(start, 0, length);
        int end = endExclusive <= 0 ? length : Math.Clamp(endExclusive, 0, length);
        return (first, Math.Max(first, end));
    }
    internal static bool TryGetFrame(int start, int endExclusive, int anchor, IdleCacheOrder order, long ordinal, out int frame)
    {
        frame = 0;
        if (start < 0 || endExclusive <= start) return false;
        if (!TryGetFrame(endExclusive - start, Math.Clamp(anchor, start, endExclusive - 1) - start, order, ordinal, out int relative)) return false;
        frame = start + relative;
        return true;
    }
    internal static bool TryGetFrame(int length, int anchor, IdleCacheOrder order, long ordinal, out int frame)
    {
        frame = 0;
        if (length <= 0 || ordinal < 0 || ordinal >= length) return false;
        anchor = Math.Clamp(anchor, 0, length - 1);
        long value;
        if (order == IdleCacheOrder.FromStart) value = ordinal;
        else if (order == IdleCacheOrder.AroundCurrentTime)
        {
            int paired = Math.Min(anchor, length - 1 - anchor);
            long pairedCount = 2L * paired + 1;
            if (ordinal < pairedCount)
                value = ordinal == 0 ? anchor : anchor + (ordinal % 2 == 1 ? -(ordinal + 1) / 2 : ordinal / 2);
            else
                value = anchor + (anchor > length - 1 - anchor ? -(ordinal - paired) : ordinal - paired);
        }
        else value = (anchor + ordinal) % length;
        frame = (int)value;
        return true;
    }
}
