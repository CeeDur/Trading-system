// RingReplayer.cs — replays recorded DOM events (engine --save-events file,
// 40-byte records) into a DOM ring so the engine, hog and dashboard can be run
// offline on real tape, or publishes a synthetic random-walk book for smoke tests.
//
//   mcs -out:RingReplayer.exe RingReplayer.cs
//
//   RingReplayer.exe <ring> --events C:\capture\events_20260904.bin [--speed 1] [--start-skip-s 0] [--max-s 0]
//   RingReplayer.exe <ring> --synthetic 30 200          (seconds, books/sec)
//
// <ring> is a named map ("Local\LNN_DOM_MMF_MES" — the engine/hog/dashboard
// see it exactly as the NT8 bridge) or "file:<path>" (file-backed, any OS).
// --speed 1 keeps the recorded inter-event timing (wall-clock faithful);
// 0 = as fast as possible (the engine's event clock stays correct, wall-clock
// features like resting-depth and paper latency compress).
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

class RingReplayer
{
    const int RecordBytes = 40;
    static void Main(string[] a)
    {
        if (a.Length < 1) { Console.WriteLine("usage: RingReplayer <ring> --events <file> [--speed 1] [--start-skip-s 0] [--max-s 0] | --synthetic <seconds> <books/sec>"); return; }
        string name = a[0]; string events = null; double speed = 1.0; double skipS = 0, maxS = 0; int synthSec = 0, synthEps = 200;
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] == "--events" && i + 1 < a.Length) events = a[++i];
            else if (a[i] == "--speed" && i + 1 < a.Length) speed = double.Parse(a[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (a[i] == "--start-skip-s" && i + 1 < a.Length) skipS = double.Parse(a[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (a[i] == "--max-s" && i + 1 < a.Length) maxS = double.Parse(a[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (a[i] == "--synthetic" && i + 2 < a.Length) { synthSec = int.Parse(a[++i]); synthEps = int.Parse(a[++i]); }
        }
        int cap = 1 << 16; int slot = 48; long header = 64;
        var map = name.StartsWith("file:") ? MemoryMappedFile.CreateFromFile(name.Substring(5), FileMode.Create, null, header + (long)cap * slot) : MemoryMappedFile.CreateOrOpen(name, header + (long)cap * slot);
        var view = map.CreateViewAccessor();
        view.Write(0, 0L); view.Write(8, cap); view.Write(12, slot); view.Write(16, 0x4C4E4E4D4D463031L);
        EventWaitHandle ev = null; try { ev = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_EVENT"); } catch { }
        long seq = 0;
        if (events == null) { Synthetic(view, cap, slot, header, ref seq, ev, synthSec > 0 ? synthSec : 20, synthEps); return; }

        byte[] buf = new byte[RecordBytes];
        long firstEvent = 0, lastEvent = 0, published = 0; DateTime wallStart = DateTime.UtcNow;
        using (FileStream fs = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
        {
            while (fs.Read(buf, 0, RecordBytes) == RecordBytes)
            {
                long eventTime = BitConverter.ToInt64(buf, 0);
                double price = BitConverter.ToDouble(buf, 16); long volume = BitConverter.ToInt64(buf, 24);
                byte side = buf[32], op = buf[33]; short pos = BitConverter.ToInt16(buf, 34);
                if (firstEvent == 0) firstEvent = eventTime;
                double elapsedS = (eventTime - firstEvent) / 1e7;
                if (elapsedS < skipS) continue;
                if (maxS > 0 && elapsedS > skipS + maxS) break;
                if (speed > 0)
                {
                    double targetWall = (elapsedS - skipS) / speed;
                    double nowWall = (DateTime.UtcNow - wallStart).TotalSeconds;
                    double wait = targetWall - nowWall;
                    if (wait > 0.002) Thread.Sleep((int)Math.Min(1000, wait * 1000));
                }
                long next = seq + 1; long off = header + (next & (cap - 1)) * slot;
                long cb = DateTime.UtcNow.Ticks;
                view.Write(off, next * 2 + 1);
                view.Write(off + 8, eventTime); view.Write(off + 16, cb); view.Write(off + 24, price); view.Write(off + 32, volume);
                view.Write(off + 40, side); view.Write(off + 41, op); view.Write(off + 42, pos);
                view.Write(off, next * 2);
                view.Write(0, next);
                seq = next; lastEvent = eventTime; published++;
                if (ev != null && (published & 7) == 0) ev.Set();
            }
        }
        if (ev != null) ev.Set();
        Console.WriteLine("replay done events=" + published + " tape-seconds=" + ((lastEvent - firstEvent) / 1e7).ToString("F1") + " wall-seconds=" + (DateTime.UtcNow - wallStart).TotalSeconds.ToString("F1"));
        Thread.Sleep(1500);
    }

    static void Synthetic(MemoryMappedViewAccessor view, int cap, int slot, long header, ref long seq, EventWaitHandle ev, int seconds, int eps)
    {
        // Sticky synthetic book: sizes drift by a few contracts per event, one or two
        // levels change per tick of the clock, trades hit the touch now and then and
        // consume it. Crude, but it lets the resting/aggression gates actually pass.
        Random r = new Random(1); double mid = 5000.0, tick = 0.25;
        double[] bid = new double[10], ask = new double[10]; long[] bv = new long[10], av = new long[10];
        for (int l = 0; l < 10; l++) { bid[l] = mid - (l + 0.5) * tick; ask[l] = mid + (l + 0.5) * tick; bv[l] = 20 + r.Next(60); av[l] = 20 + r.Next(60); }
        for (int l = 0; l < 10; l++) { Publish(view, cap, slot, header, ref seq, bid[l], bv[l], 0, 0, (short)l); Publish(view, cap, slot, header, ref seq, ask[l], av[l], 1, 0, (short)l); }
        DateTime end = DateTime.UtcNow.AddSeconds(seconds); int published = 0;
        while (DateTime.UtcNow < end)
        {
            int l = r.Next(10) < 6 ? 0 : r.Next(10);
            bool bidSide = r.NextDouble() < 0.5;
            if (r.Next(25) == 0)
            {
                // trade at the touch, consuming part of it
                bool hitBid = r.NextDouble() < 0.5; long vol = 1 + r.Next(4);
                Publish(view, cap, slot, header, ref seq, hitBid ? bid[0] : ask[0], vol, 2, 1, -1);
                if (hitBid) { bv[0] = Math.Max(0, bv[0] - vol); Publish(view, cap, slot, header, ref seq, bid[0], bv[0], 0, 1, 0); }
                else { av[0] = Math.Max(0, av[0] - vol); Publish(view, cap, slot, header, ref seq, ask[0], av[0], 1, 1, 0); }
                if ((hitBid && bv[0] == 0) || (!hitBid && av[0] == 0))
                {
                    // level exhausted: the book shifts one tick
                    mid += hitBid ? -tick : tick;
                    for (int k = 0; k < 10; k++) { bid[k] = mid - (k + 0.5) * tick; ask[k] = mid + (k + 0.5) * tick; bv[k] = 20 + r.Next(60); av[k] = 20 + r.Next(60); Publish(view, cap, slot, header, ref seq, bid[k], bv[k], 0, 1, (short)k); Publish(view, cap, slot, header, ref seq, ask[k], av[k], 1, 1, (short)k); }
                }
            }
            else
            {
                long delta = r.Next(7) - 3;
                if (bidSide) { bv[l] = Math.Max(1, bv[l] + delta); Publish(view, cap, slot, header, ref seq, bid[l], bv[l], 0, 1, (short)l); }
                else { av[l] = Math.Max(1, av[l] + delta); Publish(view, cap, slot, header, ref seq, ask[l], av[l], 1, 1, (short)l); }
            }
            if (ev != null) ev.Set();
            published++;
            Thread.Sleep(Math.Max(1, 1000 / eps));
        }
        Console.WriteLine("synthetic done books=" + published + " events=" + seq);
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
