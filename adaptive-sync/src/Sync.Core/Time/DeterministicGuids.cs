using System;

namespace Sync.Core.Time
{
    public static class DeterministicGuids
    {
        public static Func<Guid> FromSeed(int seed)
        {
            var random = new Random(seed);
            return () =>
            {
                var bytes = new byte[16];
                random.NextBytes(bytes);
                bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
                return new Guid(bytes);
            };
        }
    }
}
