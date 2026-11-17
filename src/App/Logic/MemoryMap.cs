// The memory map (redesign B): one bar for the whole of RAM, cut into the parts that fill it.
// Pure logic, no WPF, so the test runner compiles it as it is.
using System;

namespace RamOptimiser.App
{
    public enum MapPart { InUse, Modified, Standby, Free }

    /// <summary>
    /// The four parts of physical memory, in map order. With the page lists (Detailed) they are what Task
    /// Manager shows: in use (not counting modified), modified, standby and free, adding up to the whole.
    /// Without them only two parts are known: in use, and available in the Free slot.
    /// </summary>
    public sealed class MemoryMap
    {
        public static readonly MapPart[] Parts = { MapPart.InUse, MapPart.Modified, MapPart.Standby, MapPart.Free };

        public bool Detailed;
        readonly ulong[] bytes = new ulong[4];

        public ulong Total
        {
            get
            {
                ulong t = 0;
                foreach (ulong b in bytes) t += b;
                return t;
            }
        }

        public ulong Bytes(MapPart part) { return bytes[(int)part]; }

        /// <summary>The part's share of the bar (0 to 1). The parts are read a moment apart, so they are
        /// scaled by their own sum rather than by the installed total.</summary>
        public double Fraction(MapPart part)
        {
            ulong total = Total;
            return total == 0 ? 0 : (double)bytes[(int)part] / total;
        }

        public static MemoryMap From(MemorySnapshot s)
        {
            if (s == null) throw new ArgumentNullException("s");
            var m = new MemoryMap { Detailed = s.HasLists };
            ulong inUse = s.InUse;
            if (s.HasLists)
            {
                // Modified pages count as in use for Windows; the map shows them as their own part.
                ulong modified = Math.Min(s.Modified, inUse);
                m.bytes[(int)MapPart.InUse] = inUse - modified;
                m.bytes[(int)MapPart.Modified] = modified;
                m.bytes[(int)MapPart.Standby] = s.Standby;
                m.bytes[(int)MapPart.Free] = s.FreeAndZero;
            }
            else
            {
                m.bytes[(int)MapPart.InUse] = inUse;
                m.bytes[(int)MapPart.Free] = s.AvailablePhys;
            }
            return m;
        }

        /// <summary>Signed change of one part, after minus before (negative when it shrank).</summary>
        public static long Change(MemoryMap before, MemoryMap after, MapPart part)
        {
            if (before == null) throw new ArgumentNullException("before");
            if (after == null) throw new ArgumentNullException("after");
            return (long)Math.Min(after.Bytes(part), (ulong)long.MaxValue) - (long)Math.Min(before.Bytes(part), (ulong)long.MaxValue);
        }
    }
}
