// Deterministic DOM ring writer: drives the mid along a scripted path so label
// geometry can be verified rather than assumed. Same layout as the NT8 bridge.
using System;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Threading;

class ScriptedRingWriter
{
    static void Main(string[] a)
    {
        // args: <file:path> <seconds> <pattern>
        //   sawtooth = repeat: drift -4 ticks, then +10 ticks, then back to start
        //   up       = repeat: drift +10 ticks, then back to start
        string name = a[0];
        int seconds = int.Parse(a[1], CultureInfo.InvariantCulture);
        string pattern = a.Length > 2 ? a[2] : "sawtooth";

        int cap = 1 << 16; int slot = 48; long header = 64;
        var map = MemoryMappedFile.CreateFromFile(name.Substring(5), System.IO.FileMode.Create, null, header + (long)cap * slot);
        var view = map.CreateViewAccessor();
        view.Write(0, 0L); view.Write(8, cap); view.Write(12, slot); view.Write(16, 0x4C4E4E4D4D463031L);

        double tick = 0.25, mid = 5000.0, baseMid = mid;
        long seq = 0;
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);

        // one "leg" is a list of (ticks to move, steps to take getting there)
        int[] legTicks = pattern == "up" ? new[] { 10, -10 } : new[] { -4, 10, -6 };
        int leg = 0, stepInLeg = 0;
        const int stepsPerLeg = 12;

        while (DateTime.UtcNow < end)
        {
            double legTarget = baseMid + legTicks[leg] * tick;
            double from = stepInLeg == 0 ? mid : mid;
            mid = from + (legTarget - from) / Math.Max(1, stepsPerLeg - stepInLeg);
            mid = Math.Round(mid / tick) * tick;

            for (int l = 0; l < 10; l++)
            {
                Publish(view, cap, slot, header, ref seq, mid - (l + 0.5) * tick, 40, 0, 0, (short)l);
                Publish(view, cap, slot, header, ref seq, mid + (l + 0.5) * tick, 40, 1, 0, (short)l);
            }
            Publish(view, cap, slot, header, ref seq, mid, 2, 2, 0, 0);

            if (++stepInLeg >= stepsPerLeg)
            {
                stepInLeg = 0;
                baseMid = mid;
                leg = (leg + 1) % legTicks.Length;
                if (leg == 0) baseMid = 5000.0;   // re-anchor so the path does not drift away
            }
            Thread.Sleep(6);
        }
        Console.WriteLine("scripted writer done events=" + seq);
    }

    static void Publish(MemoryMappedViewAccessor v, int cap, int slot, long header, ref long seq,
                        double price, long vol, byte side, byte op, short pos)
    {
        long next = seq + 1; long off = header + (next & (cap - 1)) * slot; long now = DateTime.UtcNow.Ticks;
        v.Write(off, next * 2 + 1);
        v.Write(off + 8, now); v.Write(off + 16, now); v.Write(off + 24, price); v.Write(off + 32, vol);
        v.Write(off + 40, side); v.Write(off + 41, op); v.Write(off + 42, pos);
        v.Write(off, next * 2);
        v.Write(0, next);
        seq = next;
    }
}
