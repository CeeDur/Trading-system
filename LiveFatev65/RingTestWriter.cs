// Synthetic DOM ring writer for end-to-end engine tests (same layout as the NT8 bridge).
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
class W
{
    static void Main(string[] a)
    {
        string name = a.Length > 0 ? a[0] : "LNN_TEST_RING";
        int seconds = a.Length > 1 ? int.Parse(a[1]) : 20;
        int eps = a.Length > 2 ? int.Parse(a[2]) : 200;
        int cap = 1 << 16; int slot = 48; long header = 64;
        var map = name.StartsWith("file:") ? MemoryMappedFile.CreateFromFile(name.Substring(5), System.IO.FileMode.Create, null, header + (long)cap * slot) : MemoryMappedFile.CreateOrOpen(name, header + (long)cap * slot);
        var view = map.CreateViewAccessor();
        view.Write(0, 0L); view.Write(8, cap); view.Write(12, slot); view.Write(16, 0x4C4E4E4D4D463031L);
        EventWaitHandle ev = null; try { ev = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_EVENT"); } catch { }
        Random r = new Random(1); double mid = 5000.0, tick = 0.25; long seq = 0;
        double[] bid = new double[10], ask = new double[10]; long[] bv = new long[10], av = new long[10];
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        int published = 0;
        while (DateTime.UtcNow < end)
        {
            // random walk of the mid with a tick step every ~50 events
            if (r.Next(50) == 0) mid += (r.NextDouble() < 0.5 ? -1 : 1) * tick;
            for (int l = 0; l < 10; l++)
            {
                bid[l] = mid - (l + 0.5) * tick; ask[l] = mid + (l + 0.5) * tick;
                bv[l] = 5 + r.Next(60); av[l] = 5 + r.Next(60);
            }
            // publish a full book refresh as events (side 0/1, op 0 = update), plus an occasional trade
            for (int l = 0; l < 10; l++)
            {
                Publish(view, cap, slot, header, ref seq, bid[l], bv[l], 0, 0, (short)l);
                Publish(view, cap, slot, header, ref seq, ask[l], av[l], 1, 0, (short)l);
            }
            if (r.Next(4) == 0) Publish(view, cap, slot, header, ref seq, r.NextDouble() < 0.5 ? bid[0] : ask[0], 1 + r.Next(5), 2, 0, 0);
            if (ev != null) ev.Set();
            published++;
            Thread.Sleep(Math.Max(1, 1000 / eps));
        }
        Console.WriteLine("writer done books=" + published + " events=" + seq);
    }
    static void Publish(MemoryMappedViewAccessor v, int cap, int slot, long header, ref long seq, double price, long vol, byte side, byte op, short pos)
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
