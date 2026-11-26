using System;

namespace Nore.Core
{
    /// <summary>
    /// All gameplay numbers are integers. Multipliers are "basis points" (10000 = 1.00x)
    /// and world coordinates are fixed point with Q = 1000 units per grid cell.
    /// No floats, no UnityEngine.Random, no per-platform math drift: a battle replayed
    /// with the same seed and the same inputs produces the same state hash everywhere.
    /// </summary>
    public static class Fixed
    {
        public const int Bp = 10000;
        public const int Q = 1000;

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Abs(int v) => v < 0 ? -v : v;
        public static long Abs(long v) => v < 0 ? -v : v;

        /// <summary>Multiply by a basis-point factor, rounding toward zero.</summary>
        public static long BpMul(long value, int bp) => (value * bp) / Bp;

        public static int BpMul(int value, int bp) => (int)(((long)value * bp) / Bp);

        /// <summary>Divide by a basis-point factor safely (bp must be > 0).</summary>
        public static long BpDiv(long value, int bp) => bp <= 0 ? value : (value * Bp) / bp;

        public static long Chain(long value, params int[] factors)
        {
            for (int i = 0; i < factors.Length; i++) value = BpMul(value, factors[i]);
            return value;
        }

        /// <summary>Squared distance in fixed-point grid units. Squared avoids sqrt entirely.</summary>
        public static long Dist2(int ax, int ay, int bx, int by)
        {
            long dx = ax - bx;
            long dy = ay - by;
            return dx * dx + dy * dy;
        }

        public static bool Within(int ax, int ay, int bx, int by, int radius)
        {
            long r = radius;
            return Dist2(ax, ay, bx, by) <= r * r;
        }

        /// <summary>Move a point toward a target by at most `step` fixed-point units.</summary>
        public static void MoveToward(ref int x, ref int y, int tx, int ty, int step)
        {
            long dx = tx - x;
            long dy = ty - y;
            long d2 = dx * dx + dy * dy;
            if (d2 == 0 || step <= 0) return;
            // Integer approximation of sqrt: good enough for steering, fully deterministic.
            long d = ISqrt(d2);
            if (d <= step) { x = tx; y = ty; return; }
            x += (int)((dx * step) / d);
            y += (int)((dy * step) / d);
        }

        public static long ISqrt(long n)
        {
            if (n <= 0) return 0;
            long x = n;
            long y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + n / x) / 2; }
            return x;
        }

        public static int CellToWorld(int cell) => cell * Q + Q / 2;
    }

    /// <summary>xorshift32. Small, fast, and identical on every platform.</summary>
    public sealed class DeterministicRng
    {
        private uint _state;
        public int Seed { get; }

        public DeterministicRng(int seed)
        {
            Seed = seed;
            _state = seed == 0 ? 0x9E3779B9u : (uint)seed;
        }

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>[0, upperExclusive)</summary>
        public int NextInt(int upperExclusive)
        {
            if (upperExclusive <= 1) return 0;
            return (int)(NextUInt() % (uint)upperExclusive);
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + NextInt(maxExclusive - minInclusive);
        }

        public bool ChanceBp(int bp) => (int)(NextUInt() % (uint)Fixed.Bp) < bp;

        public DeterministicRng Fork(int salt) => new DeterministicRng(Seed ^ (salt * 0x45D9F3B));

        public uint Snapshot() => _state;
        public void Restore(uint state) => _state = state == 0 ? 0x9E3779B9u : state;
    }

    public static class Hashing
    {
        public static ulong Fnv1a(params long[] values)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < values.Length; i++)
            {
                ulong v = unchecked((ulong)values[i]);
                for (int b = 0; b < 8; b++)
                {
                    h ^= (v >> (b * 8)) & 0xFF;
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        public static string Hex(ulong h) => h.ToString("X16");
    }

    public static class GameTime
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowUnix() => (long)(DateTime.UtcNow - Epoch).TotalSeconds;

        public static long FromUnixSeconds(long seconds) => seconds;

        public static string Duration(long seconds)
        {
            if (seconds <= 0) return "done";
            long d = seconds / 86400;
            long h = (seconds % 86400) / 3600;
            long m = (seconds % 3600) / 60;
            long s = seconds % 60;
            if (d > 0) return d + "d " + h + "h";
            if (h > 0) return h + "h " + m + "m";
            if (m > 0) return m + "m " + s + "s";
            return s + "s";
        }
    }
}
