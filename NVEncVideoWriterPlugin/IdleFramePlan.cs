namespace NVEncVideoWriterPlugin;

public enum IdleCacheOrder { FromCurrentTime, AroundCurrentTime, FromStart }

// A bounded, allocation-free traversal. Every frame appears once, including frames before the CTI.
internal static class IdleFramePlan
{
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
