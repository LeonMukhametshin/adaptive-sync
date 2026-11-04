using System;

namespace Sync.Core.Time
{
    public sealed class LogicalClock
    {
        public LogicalClock(long start = 0)
        {
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start), start, "Logical time cannot be negative.");
            Now = start;
        }

        public long Now { get; private set; }

        public long Tick() => ++Now;

        public void AdvanceTo(long time)
        {
            if (time > Now) Now = time;
        }
    }
}
