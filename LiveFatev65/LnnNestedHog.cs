// LnnNestedHog.cs — the nested multi-timescale LNN "hog", as its own process.
//
// NOT the trading engine: this is a lightweight standalone that reads the same
// MES DOM ring, runs the nested reservoir (8 timescale sub-cells x 5 ensemble
// cells + meta-cell, H=600 by default), and trains a simple online logistic
// read-out on first-touch labels. It prints telemetry to stdout for the
// dashboard's "Nested hog" tab. It never sends orders and never touches the
// engine's rings, checkpoints or pipes.
//
// CPU: runs the 5 cells in parallel by default and keeps itself OFF core 0
// (--exclude-core 0) so the trading engine + executioner keep the first core.
//
// Build:  mcs -target:exe -out:LnnNestedHog.exe LnnNestedHog.cs
// Usage:  LnnNestedHog.exe [--map Local\LNN_DOM_MMF_MES] [--tick-size 0.25]
//           [--hidden 600] [--target-params N] [--pass-ms 50] [--parallel 1]
//           [--exclude-core 0] [--affinity-core -1] [--force 0] [--seed 12345]
//           [--min-target-ticks 1.0] [--min-horizon-ms 2000] [--max-horizon-ms 30000]
//           [--horizon-events 20] [--learning-rate 0.02] [--label-latency-ms 0]
//           [--prob-ms 33] [--selftest 1]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LnnNestedHog
{
    internal static class Program
    {
        private const int DepthLevels = 10;
        private const int RawFeatures = 74;   // read-out feature vector size
        private const long RingHeaderBytes = 64;
        private const int RingSlotBytes = 48;
        private const long RingMagic = 0x4C4E4E4D4D463031L;

        internal sealed class HogSettings
        {
            internal string Map = @"Local\LNN_DOM_MMF_MES";
            internal double TickSize = 0.25;
            internal int NestedHidden = 600;
            internal long NestedTargetParams;
            internal bool NestedParallel = true;
            internal int ExcludeCore = 0;
            internal int NestedAffinityCore = -1;
            internal bool NestedForce;
            internal int Seed = 12345;
            internal int PassMs = 50;
            internal double MinTargetTicks = 1.0;
            internal int MinHorizonMs = 2000, MaxHorizonMs = 30000, HorizonEvents = 20;
            internal double LearningRate = 0.02;
            internal int LabelLatencyMs;
            internal int ProbMs = 33;
            internal bool SelfTest;
            internal string EvidenceMap = @"Local\LNN_HOG_EVIDENCE_MES";   // "" = don't publish
            internal string Timescales = "";                               // v64k: 8 comma-separated seconds, ascending ("" = scalp default)
        }

        // Publishes the latest read-out as a single seqlock record the engine's
        // hog-fusion head reads: 0 seq, 8 magic, 16 utc, 24 pUp, 32 meta,
        // 40 ensemble, 48 agreement, 56 passMs, 64 hidden.
        internal sealed class EvidenceWriter : IDisposable
        {
            private const long Magic = 0x4C4E4E484F474556L;
            private MemoryMappedFile map; private MemoryMappedViewAccessor view; private long seq;
            internal bool Open(string name)
            {
                try
                {
                    map = name.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                        ? MemoryMappedFile.CreateFromFile(name.Substring(5), System.IO.FileMode.Create, null, 4096)
                        : MemoryMappedFile.CreateOrOpen(name, 4096);
                    view = map.CreateViewAccessor();
                    view.Write(0, 0L); view.Write(8, Magic);
                    return true;
                }
                catch (Exception ex) { Console.WriteLine("hog evidence: could not open " + name + " (" + ex.Message + ")"); view = null; return false; }
            }
            internal void Publish(double pUp, double meta, double ensemble, double agreement, double passMs, int hidden)
            {
                if (view == null) return;
                try
                {
                    seq++; view.Write(0, seq * 2 - 1);
                    view.Write(16, DateTime.UtcNow.Ticks); view.Write(24, pUp); view.Write(32, meta); view.Write(40, ensemble);
                    view.Write(48, agreement); view.Write(56, passMs); view.Write(64, hidden);
                    view.Write(0, seq * 2);
                }
                catch { }
            }
            public void Dispose() { try { if (view != null) view.Dispose(); } catch { } try { if (map != null) map.Dispose(); } catch { } }
        }

        internal sealed class BookState
        {
            internal readonly double[] Bid = new double[DepthLevels];
            internal readonly double[] Ask = new double[DepthLevels];
            internal readonly long[] BidVolume = new long[DepthLevels];
            internal readonly long[] AskVolume = new long[DepthLevels];
        }

        private static void Main(string[] args)
        {
            HogSettings s = new HogSettings();
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                string a = args[i], v = args[i + 1];
                if (a == "--map") s.Map = v;
                else if (a == "--tick-size") s.TickSize = D(v, 0.25);
                else if (a == "--hidden") s.NestedHidden = Math.Max(8, (int)D(v, 600));
                else if (a == "--target-params") { long tp; s.NestedTargetParams = long.TryParse(v.Replace(",", "").Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out tp) ? Math.Max(0, tp) : 0; }
                else if (a == "--pass-ms") s.PassMs = Math.Max(10, (int)D(v, 50));
                else if (a == "--parallel") s.NestedParallel = B(v);
                else if (a == "--exclude-core") s.ExcludeCore = (int)D(v, 0);
                else if (a == "--affinity-core") s.NestedAffinityCore = (int)D(v, -1);
                else if (a == "--force") s.NestedForce = B(v);
                else if (a == "--seed") s.Seed = (int)D(v, 12345);
                else if (a == "--min-target-ticks" || a == "--target-ticks") s.MinTargetTicks = D(v, 1.0);
                else if (a == "--min-horizon-ms") s.MinHorizonMs = (int)D(v, 2000);
                else if (a == "--max-horizon-ms") s.MaxHorizonMs = (int)D(v, 30000);
                else if (a == "--horizon-events") s.HorizonEvents = (int)D(v, 20);
                else if (a == "--learning-rate") s.LearningRate = D(v, 0.02);
                else if (a == "--label-latency-ms") s.LabelLatencyMs = Math.Max(0, (int)D(v, 0));
                else if (a == "--prob-ms") s.ProbMs = Math.Max(5, (int)D(v, 33));
                else if (a == "--selftest") s.SelfTest = B(v);
                else if (a == "--evidence-map") s.EvidenceMap = v.Trim();
                else if (a == "--timescales") s.Timescales = v.Trim();
                else { Console.Error.WriteLine("Unknown argument " + a); Environment.ExitCode = 2; return; }
            }
            string tsError = NestedReservoirSource.ConfigureTimescales(s.Timescales);
            if (tsError != null) { Console.Error.WriteLine("ERROR --timescales: " + tsError); Environment.ExitCode = 2; return; }
            Console.WriteLine("hog starting map=" + s.Map + " tick-size=" + s.TickSize.ToString(CultureInfo.InvariantCulture) +
                " pass-ms=" + s.PassMs + " prob-ms=" + s.ProbMs + " seed=" + s.Seed + " label-latency-ms=" + s.LabelLatencyMs);
            Console.WriteLine("hog-timescales " + NestedReservoirSource.TimescaleLine());
            Console.WriteLine("hog learning=reservoir(fixed cells)+online-logistic-readout on first-touch labels (paper only; no orders)");
            if (!NestedReservoirSource.Plan(s)) { Environment.ExitCode = 3; return; }
            ApplyAffinity(s);
            if (s.SelfTest) { SelfTest(s); return; }

            HogNet net = new HogNet(s);
            RingReader reader = new RingReader(s.Map);
            EvidenceWriter evidence = null;
            if (!string.IsNullOrEmpty(s.EvidenceMap)) { evidence = new EvidenceWriter(); if (evidence.Open(s.EvidenceMap)) Console.WriteLine("hog evidence publishing to " + s.EvidenceMap); else evidence = null; }
            long lastPass = 0, lastProb = 0, lastTele = 0, frames = 0;
            while (true)
            {
                BookState book; bool changed = reader.Drain(out book);
                long now = DateTime.UtcNow.Ticks;
                if (changed) { frames++; net.OnBook(book, now); }
                if (changed && (lastPass == 0 || now - lastPass >= s.PassMs * TimeSpan.TicksPerMillisecond))
                {
                    double delta = lastPass == 0 ? 0.1 : (now - lastPass) / (double)TimeSpan.TicksPerSecond;
                    lastPass = now;
                    net.Pass(book, now);
                    if (evidence != null) evidence.Publish(net.UpProbability, net.MetaScore, net.EnsembleScore, net.Agreement, net.LastPassMs, s.NestedHidden);
                    if (lastProb == 0 || now - lastProb >= s.ProbMs * TimeSpan.TicksPerMillisecond)
                    { lastProb = now; Console.WriteLine("hog-up-probability=" + net.UpProbability.ToString("F4", CultureInfo.InvariantCulture)); }
                }
                if (lastTele == 0 || now - lastTele >= TimeSpan.TicksPerSecond) { lastTele = now; net.Telemetry(frames); }
                Thread.Sleep(2);
            }
        }

        private static double D(string v, double d) { double x; return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : d; }
        private static bool B(string v) { v = v.Trim().ToLowerInvariant(); return v == "1" || v == "true" || v == "yes" || v == "on"; }

        private static void ApplyAffinity(HogSettings s)
        {
            try
            {
                int n = Environment.ProcessorCount;
                long mask;
                if (s.NestedAffinityCore >= 0 && s.NestedAffinityCore < n) mask = 1L << s.NestedAffinityCore;
                else
                {
                    mask = n >= 63 ? -1L : (1L << n) - 1;
                    if (s.ExcludeCore >= 0 && s.ExcludeCore < n && n > 1) mask &= ~(1L << s.ExcludeCore);
                }
                System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
                Console.WriteLine("hog affinity: cores=" + n + " mask=0x" + mask.ToString("X") +
                    (s.NestedAffinityCore >= 0 ? " (pinned to core " + s.NestedAffinityCore + ")" : s.ExcludeCore >= 0 && n > 1 ? " (all cores except " + s.ExcludeCore + ")" : " (all cores)"));
            }
            catch (Exception ex) { Console.WriteLine("hog affinity: failed (" + ex.Message + ")"); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length; public uint MemoryLoad; public ulong TotalPhys; public ulong AvailPhys;
            public ulong TotalPageFile; public ulong AvailPageFile; public ulong TotalVirtual; public ulong AvailVirtual; public ulong AvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
        private static void PhysicalMemory(out ulong total, out ulong avail)
        {
            total = 0; avail = 0;
            try { MemoryStatusEx m = new MemoryStatusEx(); m.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)); if (GlobalMemoryStatusEx(ref m)) { total = m.TotalPhys; avail = m.AvailPhys; } } catch { }
        }

        private static void SelfTest(HogSettings s)
        {
            Console.WriteLine("hog-selftest hidden=" + s.NestedHidden + " parallel=" + (s.NestedParallel ? 1 : 0));
            HogNet net = new HogNet(s);
            Random r = new Random(7); double mid = 5000.0; long utc = DateTime.UtcNow.Ticks;
            int n = 30; double total = 0, max = 0;
            for (int p = 0; p < n; p++)
            {
                BookState b = new BookState(); mid += (r.NextDouble() - 0.5) * s.TickSize * 2;
                for (int l = 0; l < DepthLevels; l++) { b.Bid[l] = mid - (l + 1) * s.TickSize; b.BidVolume[l] = 5 + r.Next(40); b.Ask[l] = mid + (l + 1) * s.TickSize; b.AskVolume[l] = 5 + r.Next(40); }
                utc += TimeSpan.TicksPerMillisecond * 100;
                net.OnBook(b, utc); net.Pass(b, utc);
                if (p > 0) { total += net.LastPassMs; if (net.LastPassMs > max) max = net.LastPassMs; }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "hog-selftest passes={0} avg-pass-ms={1:F1} max-pass-ms={2:F1} up-prob={3:F3}", n, total / (n - 1), max, net.UpProbability));
            net.Telemetry(n);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Hog net: reservoir features -> standardize -> online logistic
        //  read-out trained on first-touch labels (latency-aware).
        // ══════════════════════════════════════════════════════════════════
        internal sealed class HogNet
        {
            private readonly HogSettings s;
            private readonly NestedReservoirSource src;
            private readonly double[] w = new double[RawFeatures];
            private double b;
            private readonly double[] mean = new double[RawFeatures], var = new double[RawFeatures];
            private long seen;
            private sealed class Pending { internal double[] X; internal double UpPx, DownPx; internal long ArmUtc, Deadline, HorizonTicks; internal bool Armed; internal double TargetTicks; }
            private readonly List<Pending> pending = new List<Pending>();
            private double recentVolTicks, eventsPerSec = 1.0, prevMid; private long prevUtc;
            private long labels, correct, upLabels, downLabels, armed;
            internal double UpProbability = 0.5, LastPassMs;
            internal double MetaScore { get { return src.MetaScoreValue; } }
            internal double EnsembleScore { get { return src.EnsembleScore; } }
            internal double Agreement { get { return src.Agreement; } }

            internal HogNet(HogSettings settings)
            {
                s = settings;
                src = new NestedReservoirSource(s.NestedHidden, s.Seed, s.TickSize, s.NestedParallel);
            }

            private static double Sig(double z) { return 1.0 / (1.0 + Math.Exp(-z)); }

            internal void OnBook(BookState book, long now)
            {
                double mid = book.Bid[0] > 0 && book.Ask[0] > book.Bid[0] ? (book.Bid[0] + book.Ask[0]) * 0.5 : prevMid;
                if (prevUtc > 0)
                {
                    double dt = Math.Max((now - prevUtc) / (double)TimeSpan.TicksPerSecond, 1e-6);
                    eventsPerSec = 0.98 * eventsPerSec + 0.02 * (1.0 / dt);
                    if (prevMid > 0) recentVolTicks = 0.95 * recentVolTicks + 0.05 * Math.Abs(mid - prevMid) / s.TickSize;
                }
                prevUtc = now; prevMid = mid;
                if (book.Bid[0] <= 0 || book.Ask[0] <= book.Bid[0]) return;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    Pending p = pending[i];
                    if (!p.Armed)
                    {
                        if (now < p.ArmUtc) continue;
                        // latency-aware: reference is the mid at signal + round trip
                        double m = (book.Bid[0] + book.Ask[0]) * 0.5;
                        p.UpPx = m + p.TargetTicks * s.TickSize; p.DownPx = m - p.TargetTicks * s.TickSize;
                        p.Deadline = now + p.HorizonTicks; p.Armed = true; armed++;
                    }
                    int outcome = 0;
                    if (book.Bid[0] >= p.UpPx) outcome = 1; else if (book.Ask[0] <= p.DownPx) outcome = -1;
                    if (outcome == 0 && now >= p.Deadline) outcome = 2;
                    if (outcome == 0) continue;
                    pending.RemoveAt(i);
                    if (outcome == 2) continue;
                    double target = outcome == 1 ? 1.0 : 0.0;
                    double z = b; for (int k = 0; k < RawFeatures; k++) z += w[k] * p.X[k];
                    double pr = Sig(z), err = pr - target;
                    for (int k = 0; k < RawFeatures; k++) w[k] -= s.LearningRate * (err * p.X[k] + 0.0001 * w[k]);
                    b -= s.LearningRate * err;
                    labels++; if ((pr >= 0.5) == (target >= 0.5)) correct++;
                    if (outcome == 1) upLabels++; else downLabels++;
                }
            }

            internal void Pass(BookState book, long now)
            {
                double[] raw = src.MakeRawFeatures(book, now);
                LastPassMs = src.LastPassLatencyMs;
                // running standardization of the read-out inputs
                seen++; double a = seen < 200 ? 1.0 / seen : 0.005;
                double[] x = new double[RawFeatures];
                for (int k = 0; k < RawFeatures; k++)
                {
                    mean[k] += a * (raw[k] - mean[k]); double d = raw[k] - mean[k]; var[k] += a * (d * d - var[k]);
                    double z = d / Math.Sqrt(var[k] + 1e-6); if (z > 5) z = 5; if (z < -5) z = -5; if (double.IsNaN(z)) z = 0; x[k] = z;
                }
                double zz = b; for (int k = 0; k < RawFeatures; k++) zz += w[k] * x[k];
                UpProbability = Sig(zz);
                if (book.Bid[0] > 0 && book.Ask[0] > book.Bid[0] && pending.Count < 4096)
                {
                    double mid = (book.Bid[0] + book.Ask[0]) * 0.5;
                    double tgt = s.MinTargetTicks;   // v64k: fixed tick target (the 2x per-event volatility term never exceeded the floor)
                    double hz = eventsPerSec > 0 ? 1000.0 * s.HorizonEvents / eventsPerSec : 500.0;
                    if (hz < s.MinHorizonMs) hz = s.MinHorizonMs; if (hz > s.MaxHorizonMs) hz = s.MaxHorizonMs;
                    long hzTicks = (long)hz * TimeSpan.TicksPerMillisecond;
                    long lat = (long)s.LabelLatencyMs * TimeSpan.TicksPerMillisecond;
                    Pending p = new Pending { X = x, TargetTicks = tgt, HorizonTicks = hzTicks, ArmUtc = now + lat, Armed = lat <= 0 };
                    if (p.Armed) { p.UpPx = mid + tgt * s.TickSize; p.DownPx = mid - tgt * s.TickSize; p.Deadline = now + hzTicks; }
                    pending.Add(p);
                }
            }

            internal void Telemetry(long frames)
            {
                double acc = labels > 0 ? 100.0 * correct / labels : 0, baseline = labels > 0 ? 100.0 * Math.Max(upLabels, downLabels) / labels : 0;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "hog frames={0} labels={1} up-labels={2} down-labels={3} online-accuracy={4:F2}% majority-baseline={5:F2}% skill={6:+0.00;-0.00}% up-probability={7:F3} open-labels={8} latency-armed={9} vol-ticks={10:F2} events-per-sec={11:F1} pass-ms={12:F1} hidden={13} params={14}",
                    frames, labels, upLabels, downLabels, acc, baseline, acc - baseline, UpProbability, pending.Count, armed, recentVolTicks, eventsPerSec, LastPassMs, s.NestedHidden, src.ParamCount));
                Console.WriteLine("hog-cells " + src.CellsLine().Substring("nested-cells ".Length));
                Console.WriteLine("hog-ts " + src.TsLine().Substring("nested-ts ".Length));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  DOM ring reader (seqlock event ring; layout identical to the engine)
        // ══════════════════════════════════════════════════════════════════
        internal sealed class RingReader
        {
            private readonly string mapName; private MemoryMappedFile map; private MemoryMappedViewAccessor view;
            private long capacity, readSequence; private readonly BookState book = new BookState(); private long openFailures, lastFailLog;
            internal RingReader(string name) { mapName = name; }
            private bool Open()
            {
                if (view != null) return true;
                try
                {
                    map = mapName.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                        ? MemoryMappedFile.CreateFromFile(mapName.Substring(5), System.IO.FileMode.Open, null, 0, MemoryMappedFileAccess.Read)
                        : MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                    view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    capacity = view.ReadInt32(8); int rec = view.ReadInt32(12); long magic = view.ReadInt64(16);
                    if (magic != RingMagic || capacity < 2 || (capacity & (capacity - 1)) != 0 || rec != RingSlotBytes)
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "ring layout mismatch in {0}: magic=0x{1:X} capacity={2} slot-bytes={3} (expected magic=0x{4:X}, slot-bytes={5})", mapName, magic, capacity, rec, RingMagic, RingSlotBytes));
                    readSequence = view.ReadInt64(0);
                    Console.WriteLine("hog DOM connected: " + mapName + " sequence=" + readSequence.ToString(CultureInfo.InvariantCulture) + " capacity=" + capacity + " slot-bytes=" + rec);
                    openFailures = 0;
                    return true;
                }
                catch (Exception ex)
                {
                    Close(); openFailures++;
                    long nowT = DateTime.UtcNow.Ticks;
                    if (openFailures == 1 || nowT - lastFailLog >= 5 * TimeSpan.TicksPerSecond) { lastFailLog = nowT; Console.WriteLine("ERROR hog DOM open failed (attempt " + openFailures + "): " + ex.Message); }
                    return false;
                }
            }
            private void Close() { try { if (view != null) view.Dispose(); } catch { } try { if (map != null) map.Dispose(); } catch { } view = null; map = null; }
            internal bool Drain(out BookState outBook)
            {
                outBook = book;
                if (view == null) { if (!Open()) { Thread.Sleep(500); return false; } }
                bool changed = false;
                try
                {
                    long ws = view.ReadInt64(0);
                    if (ws < readSequence) readSequence = ws;
                    if (ws - readSequence > capacity) readSequence = ws - capacity;
                    while (readSequence < ws)
                    {
                        long next = readSequence + 1, off = RingHeaderBytes + (next & (capacity - 1)) * RingSlotBytes;
                        long first = view.ReadInt64(off); if ((first & 1) != 0) break;
                        double price = view.ReadDouble(off + 24); long vol = view.ReadInt64(off + 32);
                        byte side = view.ReadByte(off + 40), op = view.ReadByte(off + 41); short pos = view.ReadInt16(off + 42);
                        long second = view.ReadInt64(off);
                        if (first != second || second != next * 2) { if (second > next * 2) { readSequence = second / 2 - 1; continue; } break; }
                        readSequence = next;
                        if (side < 2 && pos >= 0 && pos < DepthLevels)
                        {
                            // NT8 depth semantics (v64j, same as the engine): Add inserts a
                            // row at pos (deeper rows shift down), Remove deletes it (deeper
                            // rows shift up), Update sets it in place.
                            double[] px = side == 0 ? book.Bid : book.Ask; long[] vl = side == 0 ? book.BidVolume : book.AskVolume;
                            if (op == 0) { for (int i = DepthLevels - 1; i > pos; i--) { px[i] = px[i - 1]; vl[i] = vl[i - 1]; } px[pos] = price; vl[pos] = Math.Max(0, vol); }
                            else if (op == 2) { for (int i = pos; i < DepthLevels - 1; i++) { px[i] = px[i + 1]; vl[i] = vl[i + 1]; } px[DepthLevels - 1] = 0; vl[DepthLevels - 1] = 0; }
                            else { px[pos] = price; vl[pos] = Math.Max(0, vol); }
                            changed = true;
                        }
                    }
                }
                catch { Close(); return false; }
                return changed;
            }
        }

        private sealed class NestedReservoirSource
        {
            private const int NPl = DepthLevels;      // 10 price levels
            private const int NTs = 8;
            private const int InSize = 17;
            private const int MetaHidden = 30;
            private const int CommIterations = 3;
            // Timescale groups (scalping spec):
            //  G0   50ms   no injection   ~3 ticks    BBO flicker, spread oscillation
            //  G1  250ms   no injection   ~5 ticks    quote update rate, microprice drift
            //  G2    1s    inject 1s      ~20 ticks   trade burst, consumption rate, aggression
            //  G3    5s    inject 5s      ~100 ticks  order-flow episode, multi-level consumption
            //  G4   10s    inject 10s     ~200 ticks  queue regime, spread regime, replenishment
            //  G5   30s    inject 30s     ~600 ticks  mini momentum, level defense, toxicity
            //  G6   60s    inject 60s     ~1200 ticks minute momentum, volume profile, path shape
            //  G7  300s    inject 300s    ~6000 ticks trend structure, mini-trend persistence
            // Anticipation (forward/backward loop) runs on G0-G3; its horizon is
            // the group's "~ticks" x 50 ms, in SECONDS (independent of pass cadence).
            // v64k: the time frame is configurable (--timescales a,b,c,d,e,f,g,h in seconds,
            // ascending). Everything derived from it scales with it: injection period = tau
            // for G2-G7, anticipation look-ahead = default look-ahead x (tau / default tau)
            // for G0-G3, and the per-neuron tau multiplier is relative to G0.
            private static readonly double[] DefaultTaus = { 0.05, 0.25, 1.0, 5.0, 10.0, 30.0, 60.0, 300.0 };
            private static readonly double[] DefaultLookahead = { 3 * 0.05, 5 * 0.05, 20 * 0.05, 100 * 0.05, 0, 0, 0, 0 };
            private static double[] Taus = (double[])DefaultTaus.Clone();
            private static readonly bool[] FastTs = { true, true, true, true, false, false, false, false };
            private static double[] LookaheadSeconds = (double[])DefaultLookahead.Clone();
            private static double[] Periods = { 0.0, 0.0, 1.0, 5.0, 10.0, 30.0, 60.0, 300.0 };
            private static readonly double[] Urgency = { 2.0, 3.0, 5.0, 5.0, 3.0, 1.0, 0.5, 0.1 };
            private static readonly string[] CellNames = { "standard", "fast", "slow", "flow", "vol" };
            private static readonly double[] CellWeights = { 0.30, 0.25, 0.20, 0.15, 0.10 };
            private static string[] TsNames = { "50ms", "250ms", "1s", "5s", "10s", "30s", "1m", "5m" };

            internal static string TsName(double seconds)
            {
                if (seconds < 1.0) return Math.Round(seconds * 1000).ToString(CultureInfo.InvariantCulture) + "ms";
                if (seconds < 60.0) return (seconds % 1 == 0 ? seconds.ToString("0", CultureInfo.InvariantCulture) : seconds.ToString("0.##", CultureInfo.InvariantCulture)) + "s";
                if (seconds < 3600.0) return (seconds / 60.0).ToString("0.##", CultureInfo.InvariantCulture) + "m";
                return (seconds / 3600.0).ToString("0.##", CultureInfo.InvariantCulture) + "h";
            }

            // Returns null on success, else an error message. Must run before any cell is built.
            internal static string ConfigureTimescales(string spec)
            {
                if (string.IsNullOrEmpty(spec)) return null;
                string[] parts = spec.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != NTs) return "need exactly " + NTs + " values (seconds, ascending), got " + parts.Length;
                double[] t = new double[NTs];
                for (int i = 0; i < NTs; i++)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out t[i]) || t[i] <= 0) return "'" + parts[i] + "' is not a positive number of seconds";
                    if (i > 0 && t[i] <= t[i - 1]) return "values must be ascending (" + parts[i - 1] + " then " + parts[i] + ")";
                }
                if (t[0] < 0.005) return "smallest timescale below 5 ms is not meaningful for a 50 ms pass";
                Taus = t;
                Periods = new double[NTs]; LookaheadSeconds = new double[NTs]; TsNames = new string[NTs];
                for (int i = 0; i < NTs; i++)
                {
                    Periods[i] = i >= 2 ? t[i] : 0.0;
                    LookaheadSeconds[i] = FastTs[i] ? DefaultLookahead[i] * (t[i] / DefaultTaus[i]) : 0.0;
                    TsNames[i] = TsName(t[i]);
                }
                return null;
            }

            internal static string TimescaleLine()
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < NTs; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append("G").Append(i).Append('=').Append(TsNames[i]);
                    if (FastTs[i]) sb.Append("(ant ").Append(TsName(LookaheadSeconds[i])).Append(')');
                    if (Periods[i] > 0) sb.Append("(inj)");
                }
                return sb.ToString();
            }

            private readonly Random rng;
            private readonly int H;
            private readonly Cell[] cells;
            private readonly Meta meta;
            private readonly double tickSize;
            private readonly bool parallelCells;
            internal long ParamCount;

            // input normalisation (running z-score per input feature)
            private readonly double[] inMean = new double[InSize];
            private readonly double[] inVar = new double[InSize];
            private long inCount;

            // feature carry-state
            private readonly double[] prevBidVol = new double[NPl], prevAskVol = new double[NPl];
            private readonly double[] prevBidPx = new double[NPl], prevAskPx = new double[NPl];
            private readonly double[] prevOfi = new double[NPl], prevOfiVel = new double[NPl];
            private readonly double[] bidConsumed = new double[NPl], askConsumed = new double[NPl];
            private bool havePrev;
            private readonly List<double> priceHistory = new List<double>();
            private double prevRv;
            private long lastPassUtc;

            internal double LastPassLatencyMs;
            internal double EnsembleScore, Agreement;
            internal double MetaScoreValue { get { return meta.Score; } }

            internal NestedReservoirSource(int hidden, int seed, double tickSize) : this(hidden, seed, tickSize, false) { }
            internal NestedReservoirSource(int hidden, int seed, double tickSize, bool parallel)
            {
                H = hidden; this.tickSize = tickSize; parallelCells = parallel;
                rng = new Random(seed);
                ParamCount = ParamsForHidden(hidden);
                cells = new Cell[CellNames.Length];
                for (int c = 0; c < cells.Length; c++) cells[c] = new Cell(this, CellNames[c]);
                meta = new Meta(this, 9, MetaHidden);
            }

            // ── Size planner ──────────────────────────────────────────────
            // Exact parameter count of the whole nested net for a hidden size.
            private static long Lin(long i, long o) { return i * o + o; }
            internal static long ParamsForHidden(int h)
            {
                long H = h;
                long core = 3 * Lin(InSize + H, H) + Lin(InSize, 1) + 2 * Lin(H, H) + Lin(2 * H, 1) + Lin(H, H);
                long fast = 2 * Lin(H, H) + Lin(2 * H, H) + Lin(H, 1);
                long inject = 2 * Lin(H, H);
                long total = 0;
                for (int t = 0; t < NTs; t++)
                {
                    long sub = core;
                    if (FastTs[t]) sub += fast;
                    if (Periods[t] > 0) sub += inject;
                    total += sub;
                }
                total *= CellNames.Length;
                long M = MetaHidden;
                total += 3 * Lin(9 + M, M) + Lin(M, 1) + Lin(M, 1);
                return total;
            }
            internal static int HiddenForParams(long target)
            {
                int lo = 8, hi = 200000;
                while (lo < hi)
                {
                    int mid = (int)(((long)lo + hi + 1) / 2);
                    if (ParamsForHidden(mid) <= target) lo = mid; else hi = mid - 1;
                }
                return lo;
            }
            // Multiply-accumulates per forward pass (approximate; dominant terms).
            internal static double MacsPerPass(int h)
            {
                double H = h;
                double perLevelCore = 3 * (InSize + H) * H;
                double perLevelFast = 2 * H * H + 2 * H * H + H;
                double subs = 0;
                for (int t = 0; t < NTs; t++) subs += NPl * (perLevelCore + (FastTs[t] ? perLevelFast : 0)) + (Periods[t] > 0 ? 2 * H * H : 0);
                double comm = NTs * H * H + NTs * (NTs - 1) * 2 * H * H + CommIterations * NTs * (NTs - 1) * (2 * H + NPl * H);
                return CellNames.Length * (subs + comm);
            }
            // Resolves --nested-target-params, prints the plan, checks RAM.
            internal static bool Plan(HogSettings settings)
            {
                if (settings.NestedTargetParams > 0)
                {
                    int h = HiddenForParams(settings.NestedTargetParams);
                    Console.WriteLine("nested-hog target-params=" + settings.NestedTargetParams.ToString("N0", CultureInfo.InvariantCulture) + " -> hidden=" + h);
                    settings.NestedHidden = Math.Max(8, h);
                }
                long p = ParamsForHidden(settings.NestedHidden);
                double bytes = p * 8.0;
                // state: own+ctx per sub per level, plus scratch; small vs weights
                double stateBytes = CellNames.Length * NTs * (2.0 * NPl * settings.NestedHidden + 12.0 * settings.NestedHidden) * 8.0;
                double gb = (bytes + stateBytes) / (1024.0 * 1024.0 * 1024.0);
                double macs = MacsPerPass(settings.NestedHidden);
                ulong tot, avail; PhysicalMemory(out tot, out avail);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "nested-hog hidden={0} params={1:N0} weights={2:F2} GB (double) est-macs-per-pass={3:F2} G parallel-cells={4} affinity-core={5} cores={6} ram-total={7} ram-avail={8}",
                    settings.NestedHidden, p, gb, macs / 1e9, settings.NestedParallel ? 1 : 0, settings.NestedAffinityCore, Environment.ProcessorCount,
                    tot > 0 ? (tot / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " GB" : "unknown",
                    avail > 0 ? (avail / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " GB" : "unknown"));
                if (avail > 0 && (bytes + stateBytes) > avail * 0.9)
                {
                    int fitH = HiddenForParams((long)(avail * 0.6 / 8.0));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "nested-hog WILL NOT FIT: needs {0:F1} GB but only {1:F1} GB physical RAM is available. Largest hidden that fits (~60% of free RAM): {2} ({3:N0} params). {4}",
                        gb, avail / (1024.0 * 1024 * 1024), fitH, ParamsForHidden(fitH),
                        settings.NestedForce ? "--nested-force 1 set: attempting anyway (expect paging or OutOfMemory)." : "Refusing. Use --nested-hidden " + fitH + " or --nested-force 1 to try anyway."));
                    if (!settings.NestedForce) return false;
                }
                else if (avail == 0 && gb > 32.0 && !settings.NestedForce)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "nested-hog RAM unknown on this OS and the model needs {0:F1} GB - refusing above 32 GB without --nested-force 1.", gb));
                    return false;
                }
                return true;
            }

            private static double Sig(double z) { return 1.0 / (1.0 + Math.Exp(-z)); }
            private static double Softplus(double z) { return z > 20 ? z : Math.Log(1.0 + Math.Exp(z)); }

            private sealed class Dense
            {
                internal readonly int In, Out; internal readonly double[] W, B;
                internal Dense(Random r, int i, int o, double biasFill)
                {
                    In = i; Out = o; W = new double[i * o]; B = new double[o];
                    double lim = Math.Sqrt(6.0 / (i + o));
                    for (int k = 0; k < W.Length; k++) W[k] = (r.NextDouble() * 2 - 1) * lim;
                    for (int k = 0; k < o; k++) B[k] = biasFill;
                }
                internal void Forward(double[] x, double[] y)
                {
                    for (int o = 0; o < Out; o++) { double a = B[o]; int b = o * In; for (int i = 0; i < In; i++) a += W[b + i] * x[i]; y[o] = a; }
                }
            }

            private sealed class Sub
            {
                private readonly NestedReservoirSource p; private readonly int ts, H;
                private readonly double tau, period, urg; private readonly bool fast, inject; private readonly double laSeconds;
                private readonly Dense tauNet, sigNet, gate, urgNet, msgEnc, msgDec, msgAttn, ctxGate;
                private readonly Dense antGate, antStr, confH, confO, sumNet, sumInj;
                internal readonly double[] Own, Ctx; internal double Direction; private double elapsed;
                private readonly double[] joined, ownNew, sigBuf, tauBuf, decBuf, fmsg, confIn;

                internal Sub(NestedReservoirSource parent, int tsIdx)
                {
                    p = parent; ts = tsIdx; H = parent.H; tau = Taus[ts]; fast = FastTs[ts];
                    period = Periods[ts]; inject = period > 0; laSeconds = LookaheadSeconds[ts]; urg = Urgency[ts];
                    Random r = parent.rng;
                    // v64t: tau was being applied TWICE - once as this bias, and again below
                    // as tauScale = tau / Taus[0]. The per-neuron time constant came out at
                    // roughly tau^2 / 0.05, so the 5-minute group ran with ~20-day neurons
                    // that could not move. The bias now carries no tau (Softplus(1) ~ 1.31)
                    // and tauScale carries it once, giving t ~ 1.31 * tau / urgency for every
                    // group instead of a ratio that explodes with tau.
                    tauNet = new Dense(r, InSize + H, H, 1.0); sigNet = new Dense(r, InSize + H, H, 0); gate = new Dense(r, InSize + H, H, 0);
                    urgNet = new Dense(r, InSize, 1, 0); msgEnc = new Dense(r, H, H, 0); msgDec = new Dense(r, H, H, 0);
                    msgAttn = new Dense(r, 2 * H, 1, 0); ctxGate = new Dense(r, H, H, 0);
                    if (fast) { antGate = new Dense(r, H, H, 0); antStr = new Dense(r, H, H, 0); confH = new Dense(r, 2 * H, H, 0); confO = new Dense(r, H, 1, 0); }
                    if (inject) { sumNet = new Dense(r, H, H, 0); sumInj = new Dense(r, H, H, 0); }
                    Own = new double[NPl * H]; Ctx = new double[NPl * H];
                    joined = new double[InSize + H]; ownNew = new double[NPl * H]; sigBuf = new double[NPl * H];
                    tauBuf = new double[NPl * H]; decBuf = new double[NPl * H]; fmsg = new double[H]; confIn = new double[2 * H];
                    meanCtxBuf = new double[H]; attnInBuf = new double[2 * H];
                    tvB = new double[H]; svB = new double[H]; gvB = new double[H]; agB = new double[H]; asrB = new double[H]; chB = new double[H];
                    meanFeatB = new double[InSize]; pooledB = new double[H]; summaryB = new double[H]; injB = new double[H];
                    lagMeanB = new double[H]; meanNowB = new double[H];
                }
                private readonly double[] tvB, svB, gvB, agB, asrB, chB, meanFeatB, pooledB, summaryB, injB;
                private readonly double[] uB = new double[1], coB = new double[1];
                // v64u Direction: this sub-cell's mean state now, against its own mean state
                // one of ITS timescales ago. Every per-hidden-unit quantity in the update
                // (sg = Sig(...), t, on) is strictly positive, so nothing built from them can
                // ever be negative - which is why the old "projected drift" read UP forever.
                // A difference between two states CAN go either way.
                private readonly double[] lagMeanB, meanNowB;
                private double lagClock; private bool lagPrimed;

                internal void Step(double[] feat, double delta)
                {
                    double[] meanFeat = meanFeatB; Array.Clear(meanFeat, 0, InSize);
                    for (int l = 0; l < NPl; l++) for (int f = 0; f < InSize; f++) meanFeat[f] += feat[l * InSize + f] / NPl;
                    double[] u = uB; urgNet.Forward(meanFeat, u);
                    double urgDiv = 1.0 + urg * Sig(u[0]);
                    double tauScale = tau;   // v64t: tau enters ONCE, here (see tauNet's bias)
                    double[] tv = tvB, sv = svB, gv = gvB;
                    double[] ag = agB, asr = asrB, ch = chB, co = coB;
                    for (int l = 0; l < NPl; l++)
                    {
                        int fo = l * InSize, so = l * H;
                        for (int f = 0; f < InSize; f++) joined[f] = feat[fo + f];
                        for (int h = 0; h < H; h++) joined[InSize + h] = Own[so + h];
                        tauNet.Forward(joined, tv); sigNet.Forward(joined, sv);
                        for (int h = 0; h < H; h++)
                        {
                            double t = (Softplus(tv[h]) + 1e-6) * tauScale / urgDiv;
                            if (t < 1e-3) t = 1e-3;                     // floor: no inf/NaN
                            tauBuf[so + h] = t; sigBuf[so + h] = Sig(sv[h]);
                            double d = Math.Exp(-delta / t); decBuf[so + h] = d;
                            ownNew[so + h] = Own[so + h] * d + sigBuf[so + h] * t * (1 - d);
                        }
                        if (fast)
                        {
                            double laD = Math.Max(delta, laSeconds);   // anticipation horizon in seconds
                            for (int h = 0; h < H; h++)
                            {
                                double t = tauBuf[so + h], s = sigBuf[so + h], on = ownNew[so + h];
                                double e = laD / t; if (e > 30) e = 30;      // clamp exponent
                                double gF = Math.Exp(-e), gB = Math.Exp(e);
                                double pred = on * gF + s * t * (1 - gF);
                                double back = pred * gB - s * t * (gB - 1.0);
                                fmsg[h] = back - on; confIn[h] = on; confIn[H + h] = pred;
                            }
                            confH.Forward(confIn, ch); for (int h = 0; h < H; h++) if (ch[h] < 0) ch[h] = 0;
                            confO.Forward(ch, co); double conf = Sig(co[0]);
                            antGate.Forward(fmsg, ag); antStr.Forward(fmsg, asr);
                            // The anticipation perturbation stays; only its use as a DIRECTION
                            // is gone. fmsg = back - on is an algebraic identity (back == on
                            // exactly), so its sign was floating-point rounding residue.
                            for (int h = 0; h < H; h++) ownNew[so + h] += 0.1 * Sig(ag[h]) * Sig(asr[h]) * conf * fmsg[h];
                        }
                        gate.Forward(joined, gv);
                        for (int h = 0; h < H; h++) { double g = Sig(gv[h]); ownNew[so + h] = g * ownNew[so + h] + (1 - g) * Own[so + h] * decBuf[so + h]; }
                    }
                    if (inject)
                    {
                        elapsed += delta;
                        if (elapsed >= period)
                        {
                            elapsed = elapsed % period;
                            double[] pooled = pooledB, summary = summaryB, inj = injB; Array.Clear(pooled, 0, H);
                            for (int l = 0; l < NPl; l++) for (int h = 0; h < H; h++) pooled[h] += ownNew[l * H + h] / NPl;
                            sumNet.Forward(pooled, summary); for (int h = 0; h < H; h++) summary[h] = Math.Tanh(summary[h]);
                            sumInj.Forward(summary, inj); for (int h = 0; h < H; h++) inj[h] = Sig(inj[h]);
                            double carry = Math.Min(0.3, 3.0 / period);
                            for (int l = 0; l < NPl; l++) for (int h = 0; h < H; h++) ownNew[l * H + h] = ownNew[l * H + h] * (1 - carry) + inj[h] * carry;
                        }
                    }
                    for (int k = 0; k < ownNew.Length; k++) { double v = ownNew[k]; if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0; if (v > 50) v = 50; if (v < -50) v = -50; Own[k] = v; }

                    // v64u: Direction = has this sub-cell's state risen or fallen since one of
                    // its own timescales ago. The reference is refreshed every tau seconds, so
                    // each group answers the question at the resolution it exists to cover.
                    double[] mn = meanNowB; Array.Clear(mn, 0, H);
                    for (int l = 0; l < NPl; l++) { int lo = l * H; for (int h = 0; h < H; h++) mn[h] += Own[lo + h]; }
                    double invPl = 1.0 / NPl;
                    for (int h = 0; h < H; h++) mn[h] *= invPl;
                    if (!lagPrimed)
                    {
                        Array.Copy(mn, lagMeanB, H); lagPrimed = true; lagClock = 0.0; Direction = 0;
                    }
                    else
                    {
                        double drift = 0.0;
                        for (int h = 0; h < H; h++) drift += mn[h] - lagMeanB[h];
                        Direction = double.IsNaN(drift) || Math.Abs(drift) < 1e-9 ? 0 : Math.Sign(drift);
                        lagClock += delta;
                        if (lagClock >= tau) { Array.Copy(mn, lagMeanB, H); lagClock = 0.0; }
                    }
                }

                internal void GetMessage(double[] outMsg)
                {
                    double[] mean = new double[H];
                    for (int l = 0; l < NPl; l++) for (int h = 0; h < H; h++) mean[h] += Own[l * H + h] / NPl;
                    msgEnc.Forward(mean, outMsg);
                }

                // Decode a sender's message through THIS receiver's decoder/gate.
                // The result depends only on (receiver, message) — and messages
                // are constant across the communication iterations — so callers
                // compute it once per pair and reuse it.
                internal void DecodeMessage(double[] msg, double[] outDec, double[] outGate)
                {
                    msgDec.Forward(msg, outDec); ctxGate.Forward(outDec, outGate);
                    for (int h = 0; h < H; h++) outGate[h] = Sig(outGate[h]);
                }

                private readonly double[] meanCtxBuf, attnInBuf, attnOut = new double[1];
                private static double depthWeightCache = -1;

                internal void Receive(double[] msg, double[] dec, double[] gateSig)
                {
                    double[] meanCtx = meanCtxBuf ?? new double[H];
                    Array.Clear(meanCtx, 0, H);
                    for (int l = 0; l < NPl; l++) for (int h = 0; h < H; h++) meanCtx[h] += Ctx[l * H + h] / NPl;
                    double[] ai = attnInBuf ?? new double[2 * H];
                    Array.Copy(msg, 0, ai, 0, H); Array.Copy(meanCtx, 0, ai, H, H);
                    msgAttn.Forward(ai, attnOut); double attn = Sig(attnOut[0]);
                    if (depthWeightCache < 0) { double dw = 0; for (int l = 0; l < NPl; l++) dw += Math.Exp(-0.3 * l / (double)NPl) / NPl; depthWeightCache = dw; }
                    attn *= depthWeightCache;
                    double scale = 0.1 * attn;
                    for (int l = 0; l < NPl; l++)
                    {
                        int so = l * H;
                        for (int h = 0; h < H; h++) Ctx[so + h] = Ctx[so + h] * 0.9 + gateSig[h] * dec[h] * scale;
                    }
                }

                internal double MeanOwn() { double s = 0; for (int k = 0; k < Own.Length; k++) s += Own[k]; return s / Own.Length; }
                internal double MeanCtx() { double s = 0; for (int k = 0; k < Ctx.Length; k++) s += Ctx[k]; return s / Ctx.Length; }
                internal double MeanOwnLevel0() { double s = 0; for (int h = 0; h < H; h++) s += Own[h]; return s / H; }
            }

            private sealed class Cell
            {
                internal readonly string Name; internal readonly Sub[] Subs;
                internal string Dir = "NEUTRAL"; internal double Conf, Coh; internal readonly double[] TsDir = new double[NTs];
                private readonly double[][] msgs;
                private readonly double[][][] decCache, gateCache; // [receiver][sender]
                internal Cell(NestedReservoirSource p, string name)
                {
                    Name = name; Subs = new Sub[NTs]; msgs = new double[NTs][];
                    decCache = new double[NTs][][]; gateCache = new double[NTs][][];
                    for (int t = 0; t < NTs; t++)
                    {
                        Subs[t] = new Sub(p, t); msgs[t] = new double[p.H];
                        decCache[t] = new double[NTs][]; gateCache[t] = new double[NTs][];
                        for (int j = 0; j < NTs; j++) { decCache[t][j] = new double[p.H]; gateCache[t][j] = new double[p.H]; }
                    }
                }
                internal void Step(double[] feat, double delta)
                {
                    for (int t = 0; t < NTs; t++) Subs[t].Step(feat, delta);
                    // Messages derive from own_state, which communication does not
                    // modify, so they (and each receiver's decode of them) are
                    // constant across the iterations: compute once, apply 3x.
                    for (int t = 0; t < NTs; t++) Subs[t].GetMessage(msgs[t]);
                    for (int i = 0; i < NTs; i++) for (int j = 0; j < NTs; j++) if (i != j) Subs[i].DecodeMessage(msgs[j], decCache[i][j], gateCache[i][j]);
                    for (int it = 0; it < CommIterations; it++)
                        for (int i = 0; i < NTs; i++) for (int j = 0; j < NTs; j++) if (i != j) Subs[i].Receive(msgs[j], decCache[i][j], gateCache[i][j]);
                    double net = 0; for (int t = 0; t < NTs; t++) { TsDir[t] = Subs[t].Direction; net += TsDir[t]; }
                    net /= NTs; int agree = 0; for (int t = 0; t < NTs; t++) if (TsDir[t] * net > 0) agree++;
                    Dir = net > 0.1 ? "UP" : net < -0.1 ? "DOWN" : "NEUTRAL"; Conf = Math.Abs(net); Coh = agree / (double)NTs;
                }
            }

            private sealed class Meta
            {
                private readonly int H, inSize; private readonly Dense tauNet, sigNet, gate, dirHead, confHead;
                internal readonly double[] State; internal double Score, Conf; internal string Dir = "NEUTRAL";
                internal Meta(NestedReservoirSource p, int inputSize, int hidden)
                {
                    H = hidden; inSize = inputSize; Random r = p.rng;
                    tauNet = new Dense(r, inputSize + H, H, 0); sigNet = new Dense(r, inputSize + H, H, 0); gate = new Dense(r, inputSize + H, H, 0);
                    dirHead = new Dense(r, H, 1, 0); confHead = new Dense(r, H, 1, 0); State = new double[H];
                }
                internal void Step(double[] inp, double delta)
                {
                    double[] j = new double[inSize + H]; Array.Copy(inp, 0, j, 0, inSize); Array.Copy(State, 0, j, inSize, H);
                    double[] t = new double[H], s = new double[H], g = new double[H];
                    tauNet.Forward(j, t); sigNet.Forward(j, s); gate.Forward(j, g);
                    for (int h = 0; h < H; h++)
                    {
                        double tv = Softplus(t[h]) + 1e-6; if (tv < 1e-3) tv = 1e-3;
                        double d = Math.Exp(-delta / tv);
                        double st = State[h] * d + Sig(s[h]) * tv * (1 - d);
                        double gg = Sig(g[h]); double v = gg * st + (1 - gg) * st * d;
                        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0; if (v > 50) v = 50; if (v < -50) v = -50;
                        State[h] = v;
                    }
                    double[] d1 = new double[1], c1 = new double[1]; dirHead.Forward(State, d1); confHead.Forward(State, c1);
                    Score = Math.Tanh(d1[0]); Conf = Sig(c1[0]);
                    Dir = Score > 0.1 ? "UP" : Score < -0.1 ? "DOWN" : "NEUTRAL";
                }
            }

            // Builds the 17 per-level inputs (z-scored), runs one nested pass,
            // and returns the 74-slot raw feature vector for the learner.
            internal double[] MakeRawFeatures(BookState frame, long utcTicks)
            {
                long t0 = DateTime.UtcNow.Ticks;
                double delta = lastPassUtc == 0 ? 0.1 : Math.Max(1e-3, (utcTicks - lastPassUtc) / (double)TimeSpan.TicksPerSecond);
                if (delta > 5.0) delta = 5.0;
                lastPassUtc = utcTicks;

                double[] feat = BuildInputs(frame);
                if (parallelCells)
                    Parallel.For(0, cells.Length, delegate(int c) { cells[c].Step(feat, delta); });
                else
                    for (int c = 0; c < cells.Length; c++) cells[c].Step(feat, delta);

                double net = 0; int nUp = 0, nDown = 0; double[] dm = new double[cells.Length];
                for (int c = 0; c < cells.Length; c++)
                {
                    dm[c] = cells[c].Dir == "UP" ? 1.0 : cells[c].Dir == "DOWN" ? -1.0 : 0.0;
                    net += dm[c] * cells[c].Conf * CellWeights[c];
                    if (cells[c].Dir == "UP") nUp++; else if (cells[c].Dir == "DOWN") nDown++;
                }
                EnsembleScore = net; Agreement = Math.Max(nUp, nDown) / (double)cells.Length;
                double[] mi = new double[9];
                for (int c = 0; c < cells.Length; c++) mi[c] = dm[c] * cells[c].Conf;
                mi[5] = Agreement;
                meta.Step(mi, delta);

                // Assemble exactly RawFeatures (74) slots.
                double[] raw = new double[RawFeatures];
                int k = 0;
                for (int h = 0; h < MetaHidden; h++) raw[k++] = meta.State[h];                 // 30
                for (int c = 0; c < cells.Length; c++) raw[k++] = dm[c] * cells[c].Conf;        // 5
                for (int c = 0; c < cells.Length; c++) raw[k++] = cells[c].Coh;                  // 5
                raw[k++] = Agreement; raw[k++] = EnsembleScore;                                  // 2
                for (int t = 0; t < NTs; t++) { double s = 0; for (int c = 0; c < cells.Length; c++) s += cells[c].TsDir[t]; raw[k++] = s / cells.Length; } // 8
                for (int t = 0; t < NTs; t++) { double s = 0; for (int c = 0; c < cells.Length; c++) s += cells[c].Subs[t].MeanOwn(); raw[k++] = s / cells.Length; } // 8
                for (int t = 0; t < NTs; t++) { double s = 0; for (int c = 0; c < cells.Length; c++) s += cells[c].Subs[t].MeanCtx(); raw[k++] = s / cells.Length; } // 8
                raw[k++] = meta.Score; raw[k++] = meta.Conf;                                     // 2
                for (int c = 0; c < cells.Length; c++) raw[k++] = cells[c].Subs[0].MeanOwnLevel0(); // 5
                raw[k++] = EnsembleScore * Agreement;                                             // 1  => 74
                while (k < RawFeatures) raw[k++] = 0.0;
                for (int i = 0; i < RawFeatures; i++) if (double.IsNaN(raw[i]) || double.IsInfinity(raw[i])) raw[i] = 0.0;

                LastPassLatencyMs = (DateTime.UtcNow.Ticks - t0) / (double)TimeSpan.TicksPerMillisecond;
                return raw;
            }

            private double[] BuildInputs(BookState frame)
            {
                double[] feat = new double[NPl * InSize];
                double[] ofi = new double[NPl], ofiVel = new double[NPl], ofiAcc = new double[NPl], cum = new double[NPl];
                for (int i = 0; i < NPl; i++)
                {
                    double bv = frame.BidVolume[i], av = frame.AskVolume[i];
                    if (havePrev)
                    {
                        ofi[i] = (bv - prevBidVol[i]) - (av - prevAskVol[i]);
                        // consumed proxy: size that left a level whose price did not move
                        if (frame.Bid[i] > 0 && Math.Abs(frame.Bid[i] - prevBidPx[i]) < 1e-9 && bv < prevBidVol[i]) bidConsumed[i] += prevBidVol[i] - bv; else if (Math.Abs(frame.Bid[i] - prevBidPx[i]) > 1e-9) bidConsumed[i] = 0;
                        if (frame.Ask[i] > 0 && Math.Abs(frame.Ask[i] - prevAskPx[i]) < 1e-9 && av < prevAskVol[i]) askConsumed[i] += prevAskVol[i] - av; else if (Math.Abs(frame.Ask[i] - prevAskPx[i]) > 1e-9) askConsumed[i] = 0;
                    }
                    ofiVel[i] = ofi[i] - prevOfi[i]; ofiAcc[i] = ofiVel[i] - prevOfiVel[i];
                    prevOfi[i] = ofi[i]; prevOfiVel[i] = ofiVel[i];
                    prevBidVol[i] = bv; prevAskVol[i] = av; prevBidPx[i] = frame.Bid[i]; prevAskPx[i] = frame.Ask[i];
                }
                havePrev = true;
                double run = 0; for (int i = 0; i < NPl; i++) { run += ofi[i]; cum[i] = run; }
                double mid = frame.Bid[0] > 0 && frame.Ask[0] > 0 ? (frame.Bid[0] + frame.Ask[0]) * 0.5 : 0;
                double rv, volReg, volAcc; ComputeVol(mid, out rv, out volReg, out volAcc);
                for (int i = 0; i < NPl; i++)
                {
                    int o = i * InSize; double bv = frame.BidVolume[i], av = frame.AskVolume[i];
                    // prices are expressed relative to mid in ticks (scale-free)
                    feat[o + 0] = mid > 0 && frame.Bid[i] > 0 ? (frame.Bid[i] - mid) / tickSize : 0;
                    feat[o + 1] = bv;
                    feat[o + 2] = mid > 0 && frame.Ask[i] > 0 ? (frame.Ask[i] - mid) / tickSize : 0;
                    feat[o + 3] = av;
                    feat[o + 4] = bv > 0 ? bidConsumed[i] / (bv + 1e-8) : 0; feat[o + 5] = av > 0 ? askConsumed[i] / (av + 1e-8) : 0;
                    feat[o + 6] = bidConsumed[i]; feat[o + 7] = askConsumed[i];
                    feat[o + 8] = bv; feat[o + 9] = av;
                    feat[o + 10] = ofi[i]; feat[o + 11] = cum[i]; feat[o + 12] = ofiVel[i]; feat[o + 13] = ofiAcc[i];
                    feat[o + 14] = rv; feat[o + 15] = volReg; feat[o + 16] = volAcc;
                }
                // running z-score per input column (uses level-0 stats as representative
                // then applies to all levels; cheap and keeps cells in a sane range)
                inCount++;
                double a = inCount < 200 ? 1.0 / inCount : 0.005;
                for (int f = 0; f < InSize; f++)
                {
                    double m = 0; for (int i = 0; i < NPl; i++) m += feat[i * InSize + f] / NPl;
                    inMean[f] += a * (m - inMean[f]);
                    double dv = m - inMean[f]; inVar[f] += a * (dv * dv - inVar[f]);
                }
                for (int i = 0; i < NPl; i++)
                    for (int f = 0; f < InSize; f++)
                    {
                        double sd = Math.Sqrt(inVar[f] + 1e-6);
                        double z = (feat[i * InSize + f] - inMean[f]) / sd;
                        if (z > 5) z = 5; if (z < -5) z = -5; if (double.IsNaN(z)) z = 0;
                        feat[i * InSize + f] = z;
                    }
                return feat;
            }

            private void ComputeVol(double price, out double rv, out double volReg, out double volAcc)
            {
                if (price > 0) priceHistory.Add(price);
                if (priceHistory.Count > 200) priceHistory.RemoveRange(0, priceHistory.Count - 200);
                if (priceHistory.Count < 2) { rv = 0; volReg = 0.5; volAcc = 0; return; }
                double sum = 0;
                for (int i = 1; i < priceHistory.Count; i++) { double r = (priceHistory[i] - priceHistory[i - 1]) / (priceHistory[i - 1] + 1e-10); sum += r * r; }
                rv = sum; volReg = 1.0 / (1.0 + Math.Exp(-(rv - prevRv) / (rv + 1e-10))); volAcc = rv - prevRv; prevRv = rv;
            }

            internal string CellsLine()
            {
                StringBuilder sb = new StringBuilder("nested-cells");
                for (int c = 0; c < cells.Length; c++)
                    sb.Append(" " + cells[c].Name.Substring(0, 3) + "=" + cells[c].Dir + ":" + cells[c].Conf.ToString("F2", CultureInfo.InvariantCulture) + ":coh" + cells[c].Coh.ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(string.Format(CultureInfo.InvariantCulture, " meta={0}:{1:F3}:{2:F2} ensemble={3:F3} agreement={4:F0}% pass-ms={5:F1}",
                    meta.Dir, meta.Score, meta.Conf, EnsembleScore, Agreement * 100.0, LastPassLatencyMs));
                return sb.ToString();
            }

            internal string TsLine()
            {
                StringBuilder sb = new StringBuilder("nested-ts");
                for (int t = 0; t < NTs; t++)
                {
                    double s = 0; for (int c = 0; c < cells.Length; c++) s += cells[c].TsDir[t]; s /= cells.Length;
                    sb.Append(" " + TsNames[t] + "=" + (s > 0.1 ? "U" : s < -0.1 ? "D" : "-"));
                }
                return sb.ToString();
            }
        }
    }
}
