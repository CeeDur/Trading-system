// LnnExecBacktest.cs — offline backtester for the limit executioner.
//
// Replays a recorded DOM event file (engine --save-events, 40-byte records:
// depth add/update/remove with level position, and trades) through a faithful
// port of LnnOrderExecStrategy's executioner state machine — the same
// top-of-book gates (imbalance, microprice, resting-depth, flicker,
// aggression), the same repeg/chase, aggression-cancel and timeout handling,
// and the same queue-position fill model (a resting order fills only once the
// size that was ahead of it has traded at/through its price). Every attempt
// is scored by markout, and compared against crossing the spread at the same
// moment, so the output answers the real question: does posting beat a
// market order, and under which gate settings.
//
//   mcs -out:LnnExecBacktest.exe LnnExecBacktest.cs
//
//   LnnExecBacktest.exe <events.bin> [--tick 0.25] [--qty 1]
//        signals:  --signal-every-ms 5000 [--signal-random 1] | --signals-csv file (utcTicks,dir | ISO8601,LONG/SHORT)
//        gates:    --imbalance 0,0.1,0.2  --resting-ms 0,200,500  --flicker 0,20,50  --microprice 0,1
//                  --aggr-qty 15 --aggr-window-ms 500 --chase 0,1,2 --timeout-ms 3000,8000 --on-timeout 0,1
//                  --markout1-ms 1000 --markout2-ms 5000 --cancel-ahead 0,0.5,1  (queue estimator: 0 = trades only, 1 = uniform cancels)
//        output:   --out results.csv   (also printed; sorted by post-minus-cross)
//        fill model (learned P(fill within T | queue/book state) from the tape):
//                  --fit-fill-model model.txt [--fit-every-ms 250] [--fit-horizons 500,1000,2000,5000] [--cancel-ahead 0.5]
//        EV policy (hold/pull/cross by expected value using the fill model and the signal's P(up)):
//                  --policy gates,ev --fill-model model.txt [--expected-move-ticks 2]
//                  signals CSV may carry a 3rd column p_up (0..1); without it the edge term is 0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LnnExecBacktest
{
    internal struct Ev { public long T; public double Price; public long Vol; public byte Side, Op; public short Pos; }

    internal sealed class Config
    {
        public double Imbalance; public int RestingMs; public double Flicker; public bool Microprice;
        public int AggrQty, AggrWindowMs, Chase, TimeoutMs, OnTimeout, Markout1Ms, Markout2Ms;
        public double CancelAhead = 0.5;   // cancel-ahead factor k: 0 = legacy trades-only, 1 = uniform cancels, 0.5 = strategy default
        public int Policy = 0;             // 0 = gates (current executioner), 1 = EV (fill model + P(up))
        public string Key() { return string.Format(CultureInfo.InvariantCulture, "policy={12} imb={0:F2} rest={1} flick={2:F0} mp={3} aggr={4}/{5}ms chase={6} timeout={7}/{8} mk={9}/{10} cancel-ahead={11:F2}",
            Imbalance, RestingMs, Flicker, Microprice ? 1 : 0, AggrQty, AggrWindowMs, Chase, TimeoutMs, OnTimeout == 1 ? "cross" : "abandon", Markout1Ms, Markout2Ms, CancelAhead, Policy == 1 ? "ev" : "gates"); }
    }

    internal sealed class Result
    {
        public Config Cfg; public long Signals, Posts, Fills, Crosses, GateNeverPassed, ReplacedWhilePosting;
        public long CancelAggr, CancelTimeout, CancelChase;   // ReplacedWhilePosting = a new signal arrived while still posting (counted separately)
        public double SumTimeToFillMs, SumEffSpread, SumMk1, SumMk2, SumChasedTicks;
        public long MkCount;
        public double SumCrossMk2; public long CrossBaselineCount;
        public long EvCrossAtArm, EvCrossWhilePosting;   // EV policy decisions
        public double FillRate { get { return Posts > 0 ? 100.0 * Fills / Posts : 0; } }
        public double AvgTtf { get { return Fills > 0 ? SumTimeToFillMs / Fills : 0; } }
        public double AvgEff { get { return MkCount > 0 ? SumEffSpread / MkCount : 0; } }
        public double AvgMk1 { get { return MkCount > 0 ? SumMk1 / MkCount : 0; } }
        public double AvgMk2 { get { return MkCount > 0 ? SumMk2 / MkCount : 0; } }
        public double CrossMk2 { get { return CrossBaselineCount > 0 ? SumCrossMk2 / CrossBaselineCount : 0; } }
        // value per SIGNAL: posting = P(fill or cross) x avg markout2 of those; crossing = avg cross markout2
        public double PostPerSignal { get { return Signals > 0 ? SumMk2 / Signals : 0; } }
        public double PostMinusCross { get { return PostPerSignal - CrossMk2; } }
        public long Positions { get { return MkCount; } }
        // per POSITION taken (fills + crosses): entry quality independent of how many signals were skipped
        public double PerPosition { get { return MkCount > 0 ? SumMk2 / MkCount : double.NaN; } }
        public double PerPositionMinusCross { get { return MkCount > 0 ? PerPosition - CrossMk2 : double.NaN; } }
    }

    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length < 1) { Console.WriteLine("usage: LnnExecBacktest <events.bin> [options]  (see header)"); return; }
            string path = args[0]; double tick = 0.25; int qty = 1; int signalEveryMs = 5000; bool signalRandom = false; string signalsCsv = null; string outPath = null;
            List<double> imb = new List<double> { 0.0 }, flick = new List<double> { 0.0 }; List<int> rest = new List<int> { 200 }, mp = new List<int> { 1 };
            List<int> aggrQty = new List<int> { 15 }, aggrWin = new List<int> { 500 }, chase = new List<int> { 1 }, timeout = new List<int> { 8000 }, onTimeout = new List<int> { 0 }, mk1 = new List<int> { 1000 }, mk2 = new List<int> { 5000 };
            List<double> cancelAhead = new List<double> { 0.5 };
            List<int> policies = new List<int> { 0 };
            string fitOut = null, fillModelPath = null; int fitEveryMs = 250; List<int> fitHorizons = new List<int> { 500, 1000, 2000, 5000 }; double expectedMoveTicks = 2.0;
            for (int i = 1; i + 1 < args.Length; i += 2)
            {
                string k = args[i], v = args[i + 1];
                switch (k)
                {
                    case "--tick": tick = D(v); break;
                    case "--qty": qty = (int)D(v); break;
                    case "--signal-every-ms": signalEveryMs = (int)D(v); break;
                    case "--signal-random": signalRandom = v == "1"; break;
                    case "--signals-csv": signalsCsv = v; break;
                    case "--imbalance": imb = DL(v); break;
                    case "--resting-ms": rest = IL(v); break;
                    case "--flicker": flick = DL(v); break;
                    case "--microprice": mp = IL(v); break;
                    case "--aggr-qty": aggrQty = IL(v); break;
                    case "--aggr-window-ms": aggrWin = IL(v); break;
                    case "--chase": chase = IL(v); break;
                    case "--timeout-ms": timeout = IL(v); break;
                    case "--on-timeout": onTimeout = IL(v); break;
                    case "--markout1-ms": mk1 = IL(v); break;
                    case "--markout2-ms": mk2 = IL(v); break;
                    case "--cancel-ahead": cancelAhead = DL(v); break;
                    case "--policy": policies = new List<int>(); foreach (string pp in v.Split(new char[] { ',' })) { string t = pp.Trim().ToLowerInvariant(); if (t == "ev" || t == "1") policies.Add(1); else if (t.Length > 0) policies.Add(0); } break;
                    case "--fill-model": fillModelPath = v; break;
                    case "--fit-fill-model": fitOut = v; break;
                    case "--fit-every-ms": fitEveryMs = (int)D(v); break;
                    case "--fit-horizons": fitHorizons = IL(v); break;
                    case "--expected-move-ticks": expectedMoveTicks = D(v); break;
                    case "--out": outPath = v; break;
                    default: Console.WriteLine("unknown option " + k); return;
                }
            }
            List<Ev> events = Load(path);
            if (events.Count == 0) { Console.WriteLine("no events in " + path); return; }
            long t0 = events[0].T, t1 = events[events.Count - 1].T;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "events={0} tape={1:F1}s ({2:u} -> {3:u})", events.Count, (t1 - t0) / 1e7, new DateTime(t0, DateTimeKind.Utc), new DateTime(t1, DateTimeKind.Utc)));
            if (fitOut != null)
            {
                FillModelFitter.Fit(events, tick, cancelAhead[0], fitEveryMs, fitHorizons, fitOut);
                if (fillModelPath == null && policies.Count == 1 && policies[0] == 0) return;
                if (fillModelPath == null) fillModelPath = fitOut;
            }
            FillModel fillModel = null;
            if (fillModelPath != null) { fillModel = FillModel.Load(fillModelPath); Console.WriteLine("fill model: " + fillModelPath + " -> " + (fillModel != null ? fillModel.Describe() : "FAILED TO LOAD")); }
            List<KeyValuePair<long, int>> signals = signalsCsv != null ? LoadSignals(signalsCsv) : SyntheticSignals(t0, t1, signalEveryMs, signalRandom);
            Console.WriteLine("signals=" + signals.Count + (signalsCsv != null ? " (from " + signalsCsv + ")" : " (synthetic every " + signalEveryMs + " ms, " + (signalRandom ? "random" : "alternating") + " direction)"));

            List<Config> configs = new List<Config>();
            foreach (double a in imb) foreach (int b in rest) foreach (double c in flick) foreach (int d in mp) foreach (int e in aggrQty) foreach (int f in aggrWin)
            foreach (int g in chase) foreach (int h in timeout) foreach (int o in onTimeout) foreach (int m1 in mk1) foreach (int m2 in mk2) foreach (double ca in cancelAhead) foreach (int pol in policies)
            {
                if (pol == 1 && fillModel == null) { Console.WriteLine("policy=ev needs --fill-model (or --fit-fill-model); skipping"); continue; }
                configs.Add(new Config { Imbalance = a, RestingMs = b, Flicker = c, Microprice = d == 1, AggrQty = e, AggrWindowMs = f, Chase = g, TimeoutMs = h, OnTimeout = o, Markout1Ms = m1, Markout2Ms = m2, CancelAhead = ca, Policy = pol });
            }
            Console.WriteLine("configs=" + configs.Count);

            List<Result> results = new List<Result>();
            foreach (Config cfg in configs) { results.Add(new Sim(cfg, tick, qty, fillModel, expectedMoveTicks, signalPUp).Run(events, signals)); }
            // Rank by entry quality per POSITION vs crossing (does not reward skipping signals);
            // the per-signal columns are the policy view for configs that always end in a position (on-timeout=cross).
            results.Sort(delegate(Result x, Result y)
            {
                bool xv = !double.IsNaN(x.PerPositionMinusCross), yv = !double.IsNaN(y.PerPositionMinusCross);
                if (xv != yv) return xv ? -1 : 1;                       // configs that never took a position rank last
                if (xv) { int c1 = y.PerPositionMinusCross.CompareTo(x.PerPositionMinusCross); if (c1 != 0) return c1; }
                return y.Positions.CompareTo(x.Positions);
            });

            StringBuilder csv = new StringBuilder();
            csv.AppendLine("config,signals,posts,fills,fill_rate_pct,crosses,positions,cancel_aggr,cancel_timeout,cancel_chase,replaced_while_posting,gate_never_passed,ev_cross_at_arm,ev_cross_while_posting,avg_time_to_fill_ms,avg_eff_spread_ticks,avg_markout1_ticks,avg_markout2_ticks,cross_baseline_markout2_ticks,per_position_minus_cross_ticks,per_signal_minus_cross_ticks");
            foreach (Result r in results)
                csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "\"{0}\",{1},{2},{3},{4:F1},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14:F0},{15:F3},{16:F3},{17:F3},{18:F3},{19:F3},{20:F3}",
                    r.Cfg.Key(), r.Signals, r.Posts, r.Fills, r.FillRate, r.Crosses, r.Positions, r.CancelAggr, r.CancelTimeout, r.CancelChase, r.ReplacedWhilePosting, r.GateNeverPassed, r.EvCrossAtArm, r.EvCrossWhilePosting, r.AvgTtf, r.AvgEff, r.AvgMk1, r.AvgMk2, r.CrossMk2, r.PerPositionMinusCross, r.PostMinusCross));
            Console.WriteLine();
            Console.WriteLine("HOW TO READ: markout2 = EXIT TOUCH at +markout2 ms minus your entry, signed by side (a buy exits at the later bid, a sell at the later ask; + = you could close in profit). cross_baseline = the same for a market order at the signal (~ -spread).");
            Console.WriteLine("  per_position_minus_cross = entry quality per position taken vs crossing (ranking key; does not reward skipping signals).");
            Console.WriteLine("  per_signal_minus_cross   = policy value per signal; only comparable across configs that always end in a position (on-timeout=cross).");
            Console.WriteLine("  With synthetic/random signals the cross baseline is about -0.5t by construction (half the spread) and markouts are noise; use real engine signals (--signals-csv) for policy value.");
            Console.WriteLine("RESULTS (best first)");
            Console.WriteLine(csv.ToString());
            if (outPath != null) { File.WriteAllText(outPath, csv.ToString()); Console.WriteLine("written " + outPath); }
            Result best = results[0];
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "BEST (per position): {0}\n  positions {1} ({2} passive fills = {3:F1}% of {4} posts, {5} crosses), avg time-to-fill {6:F0} ms, eff-spread {7:+0.00;-0.00}t, markout {8:+0.00;-0.00}t / {9:+0.00;-0.00}t, cross baseline {10:+0.00;-0.00}t -> per position minus cross {11:+0.000;-0.000}t, per signal {12:+0.000;-0.000}t",
                best.Cfg.Key(), best.Positions, best.Fills, best.FillRate, best.Posts, best.Crosses, best.AvgTtf, best.AvgEff, best.AvgMk1, best.AvgMk2, best.CrossMk2, best.PerPositionMinusCross, best.PostMinusCross));
        }

        private static double D(string v) { return double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture); }
        private static List<double> DL(string v) { List<double> l = new List<double>(); foreach (string p in v.Split(new char[] { ',' })) if (p.Trim().Length > 0) l.Add(D(p.Trim())); return l; }
        private static List<int> IL(string v) { List<int> l = new List<int>(); foreach (string p in v.Split(new char[] { ',' })) if (p.Trim().Length > 0) l.Add((int)D(p.Trim())); return l; }

        private static List<Ev> Load(string path)
        {
            List<Ev> list = new List<Ev>();
            byte[] buf = new byte[40];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                while (fs.Read(buf, 0, 40) == 40)
                    list.Add(new Ev { T = BitConverter.ToInt64(buf, 0), Price = BitConverter.ToDouble(buf, 16), Vol = BitConverter.ToInt64(buf, 24), Side = buf[32], Op = buf[33], Pos = BitConverter.ToInt16(buf, 34) });
            return list;
        }

        private static List<KeyValuePair<long, int>> SyntheticSignals(long t0, long t1, int everyMs, bool random)
        {
            List<KeyValuePair<long, int>> s = new List<KeyValuePair<long, int>>(); Random r = new Random(7); int dir = 1;
            for (long t = t0 + 5 * TimeSpan.TicksPerSecond; t < t1; t += (long)everyMs * TimeSpan.TicksPerMillisecond)
            { if (random) dir = r.NextDouble() < 0.5 ? 1 : -1; else dir = -dir; s.Add(new KeyValuePair<long, int>(t, dir)); }
            return s;
        }

        internal static Dictionary<long, double> signalPUp = new Dictionary<long, double>();
        private static List<KeyValuePair<long, int>> LoadSignals(string path)
        {
            List<KeyValuePair<long, int>> s = new List<KeyValuePair<long, int>>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split(new char[] { ',' });
                if (parts.Length < 2) continue;
                long t; DateTime dt; int dir;
                string a = parts[0].Trim(), b = parts[1].Trim().ToUpperInvariant();
                if (!long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out t))
                { if (!DateTime.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) continue; t = dt.Ticks; }
                if (b == "LONG" || b == "1" || b == "BUY") dir = 1; else if (b == "SHORT" || b == "-1" || b == "SELL") dir = -1; else continue;
                s.Add(new KeyValuePair<long, int>(t, dir));
                double pu; if (parts.Length >= 3 && double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pu) && pu >= 0 && pu <= 1) signalPUp[t] = pu;
            }
            s.Sort(delegate(KeyValuePair<long, int> x, KeyValuePair<long, int> y) { return x.Key.CompareTo(y.Key); });
            return s;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Fill model: P(fill within T | queue/book state at decision time).
    //  8 features (standardized), one logistic head per horizon T.
    //   0 log1p(ahead estimate)     1 log1p(opposite touch size)
    //   2 imbalance signed by side  3 trade rate hitting OUR side (contracts/s, 2 s)
    //   4 trade rate hitting the other side    5 touch event rate (events/s)
    //   6 spread ticks              7 microprice offset signed by side (ticks)
    //  File is plain text so the strategy can load it and you can read it.
    // ══════════════════════════════════════════════════════════════════════
    internal sealed class FillModel
    {
        internal const int F = 8;
        // v2 (hazard): fill rate per second lambda = exp(hazard_bias + hazard_w . z), P(fill within T) = 1 - exp(-lambda T)
        internal bool Hazard; internal double HazardBias; internal double[] HazardW = new double[F];
        internal double HazardShape = 1.0;   // Weibull shape: 1 = constant rate; >1 = rate rises as the queue drains
        // v1 (per-horizon logistic heads) still loads for P(fill)
        internal int[] Horizons = new int[0];
        internal double[] Mean = new double[F], Std = new double[F];
        internal double[] Bias = new double[0]; internal double[][] W = new double[0][];
        internal double CancelAhead = 0.5, AdverseFillTicks = 0, HalfSpreadTicks = 0.5; internal long Samples;
        // touch-to-touch markouts (ticks) measured on the tape: a passive fill exits at the touch later
        // (buy fill at B -> later bid; sell fill at A -> later ask); a cross enters at the far touch and exits the same way.
        internal double FillMarkoutTouchTicks = double.NaN, CrossMarkoutTouchTicks = double.NaN; internal int MarkoutMs = 2000;

        internal string Describe()
        {
            return (Hazard ? "hazard model" : "horizons=" + string.Join("/", Array.ConvertAll(Horizons, delegate(int h) { return h.ToString(); }))) +
                " samples=" + Samples + " k=" + CancelAhead.ToString("F2", CultureInfo.InvariantCulture) +
                (double.IsNaN(FillMarkoutTouchTicks) ? " (v1: no touch markouts — refit for the EV policy)" :
                 " fill-markout@touch=" + FillMarkoutTouchTicks.ToString("F3", CultureInfo.InvariantCulture) + "t cross-markout@touch=" + CrossMarkoutTouchTicks.ToString("F3", CultureInfo.InvariantCulture) + "t");
        }
        internal double LambdaPerSec(double[] x)
        {
            double z = HazardBias;
            for (int i = 0; i < F; i++) z += HazardW[i] * ((x[i] - Mean[i]) / (Std[i] > 1e-9 ? Std[i] : 1.0));
            if (z > 8) z = 8; if (z < -12) z = -12;
            return Math.Exp(z);
        }

        internal static double[] Features(double ahead, double oppositeSize, double imbSigned, double tradeRateOurSide, double tradeRateOtherSide, double touchRate, double spreadTicks, double mpOffsetSigned)
        { return new double[] { Math.Log(1 + Math.Max(0, ahead)), Math.Log(1 + Math.Max(0, oppositeSize)), imbSigned, tradeRateOurSide, tradeRateOtherSide, touchRate, spreadTicks, mpOffsetSigned }; }

        private double Logit(int hi, double[] x)
        {
            double z = Bias[hi];
            for (int i = 0; i < F; i++) z += W[hi][i] * ((x[i] - Mean[i]) / (Std[i] > 1e-9 ? Std[i] : 1.0));
            return z;
        }
        // P(fill within tMs): interpolate the logit between the two nearest fitted
        // horizons, then enforce monotonicity in T (a fill within a shorter window
        // is also a fill within this one) by taking the running max over shorter horizons.
        internal double PFill(double[] x, double tMs)
        {
            if (Hazard) return 1.0 - Math.Exp(-Math.Pow(LambdaPerSec(x) * Math.Max(0, tMs) / 1000.0, HazardShape));
            if (Horizons.Length == 0) return 0;
            double best = 0;
            for (int i = 0; i < Horizons.Length && Horizons[i] < tMs; i++) best = Math.Max(best, Sig(Logit(i, x)));
            double at;
            if (tMs <= Horizons[0]) at = Sig(Logit(0, x) * Math.Max(0.05, tMs / Horizons[0]));
            else
            {
                at = Sig(Logit(Horizons.Length - 1, x));
                for (int i = 1; i < Horizons.Length; i++)
                    if (tMs <= Horizons[i]) { double a = (tMs - Horizons[i - 1]) / (double)(Horizons[i] - Horizons[i - 1]); at = Sig(Logit(i - 1, x) * (1 - a) + Logit(i, x) * a); break; }
            }
            return Math.Max(best, at);
        }
        internal static double Sig(double z) { if (z > 30) z = 30; if (z < -30) z = -30; return 1.0 / (1.0 + Math.Exp(-z)); }

        internal static FillModel Load(string path)
        {
            try
            {
                FillModel m = new FillModel(); List<int> hs = new List<int>(); List<double> bs = new List<double>(); List<double[]> ws = new List<double[]>();
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                    int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash).Trim();
                    int eq = line.IndexOf('='); if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "mean") m.Mean = Arr(v); else if (k == "std") m.Std = Arr(v);
                    else if (k == "cancel_ahead") m.CancelAhead = Dbl(v); else if (k == "adverse_fill_ticks") m.AdverseFillTicks = Dbl(v);
                    else if (k == "half_spread_ticks") m.HalfSpreadTicks = Dbl(v); else if (k == "samples") m.Samples = (long)Dbl(v);
                    else if (k == "hazard_bias") { m.Hazard = true; m.HazardBias = Dbl(v); } else if (k == "hazard_w") m.HazardW = Arr(v);
                    else if (k == "hazard_shape") m.HazardShape = Math.Max(0.2, Dbl(v));
                    else if (k == "fill_markout_touch_ticks") m.FillMarkoutTouchTicks = Dbl(v); else if (k == "cross_markout_touch_ticks") m.CrossMarkoutTouchTicks = Dbl(v);
                    else if (k == "markout_ms") m.MarkoutMs = (int)Dbl(v);
                    else if (k.StartsWith("h") && k.EndsWith("_bias")) { hs.Add(int.Parse(k.Substring(1, k.Length - 6), CultureInfo.InvariantCulture)); bs.Add(Dbl(v)); }
                    else if (k.StartsWith("h") && k.EndsWith("_w")) ws.Add(Arr(v));
                }
                if (!m.Hazard && (hs.Count == 0 || hs.Count != ws.Count)) return null;
                m.Horizons = hs.ToArray(); m.Bias = bs.ToArray(); m.W = ws.ToArray();
                return m;
            }
            catch { return null; }
        }
        private static double Dbl(string v) { return double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture); }
        private static double[] Arr(string v) { string[] p = v.Split(new char[] { ',' }); double[] a = new double[p.Length]; for (int i = 0; i < p.Length; i++) a[i] = Dbl(p[i].Trim()); return a; }
    }

    // One side of the touch, rebuilt with NinjaTrader's depth semantics (v64j):
    // Operation.Add INSERTS a row at its position (deeper rows shift down) and
    // Operation.Remove DELETES it (deeper rows shift up). Tracking row 1 lets a
    // Remove@0 promote the next level immediately instead of leaving the touch
    // blank until the feed touches row 0 again (which is exactly when a limit
    // executioner most needs the true touch). Mirrors LnnOrderExecStrategy.
    internal sealed class TouchSide
    {
        internal double Tob, Row1; internal long TobSz, Row1Sz;
        // Returns true when the touch (row 0) may have changed.
        internal bool Apply(byte op, short pos, double price, long vol)
        {
            if (pos == 0)
            {
                if (op == 0) { Row1 = Tob; Row1Sz = TobSz; Tob = price; TobSz = Math.Max(0, vol); }
                else if (op == 2) { Tob = Row1; TobSz = Row1Sz; Row1 = 0; Row1Sz = 0; }
                else { Tob = price; TobSz = Math.Max(0, vol); }
                return true;
            }
            if (pos == 1)
            {
                if (op == 2) { Row1 = 0; Row1Sz = 0; }
                else { Row1 = price; Row1Sz = Math.Max(0, vol); }
            }
            return false;
        }
    }

    internal static class FillModelFitter
    {
        private sealed class VPost
        {
            public long T; public int Side; public double Price; public double Ahead, Behind; public long LevelSeen, TradedSince; public double[] X;
            public bool Done; public long FillT = -1; public long EndT = -1; public bool Missed; public double MidAtPost, MidAtFill;
        }
        private sealed class VCross { public long T; public int Side; public double Price; }

        internal static void Fit(List<Ev> events, double tick, double k, int everyMs, List<int> horizons, string outPath)
        {
            horizons.Sort();
            long maxH = (long)horizons[horizons.Count - 1] * TimeSpan.TicksPerMillisecond;
            double tobBid = 0, tobAsk = 0; long bidSz = 0, askSz = 0;
            TouchSide bidSide = new TouchSide(), askSide = new TouchSide();
            List<long> touchEvents = new List<long>(); List<KeyValuePair<long, long>> sellAggr = new List<KeyValuePair<long, long>>(), buyAggr = new List<KeyValuePair<long, long>>();
            List<VPost> open = new List<VPost>(); List<double[]> X = new List<double[]>(); List<long> fillMs = new List<long>(); List<bool> missed = new List<bool>(); List<long> censorMs = new List<long>();
            List<VCross> crosses = new List<VCross>(); double sumFillMk = 0, sumCrossMk = 0; long nFillMk = 0, nCrossMk = 0; List<VPost> filledPending = new List<VPost>();
            long lastPost = 0; double sumSpread = 0; long nSpread = 0;
            long mkWindow = 2000L * TimeSpan.TicksPerMillisecond;
            foreach (Ev e in events)
            {
                long now = e.T;
                if (e.Side == 2)
                {
                    if (tobAsk > 0 && e.Price >= tobAsk) buyAggr.Add(new KeyValuePair<long, long>(now, e.Vol)); else if (tobBid > 0 && e.Price <= tobBid) sellAggr.Add(new KeyValuePair<long, long>(now, e.Vol));
                    foreach (VPost v in open)
                    {
                        if (v.Done) continue;
                        bool through = v.Side > 0 ? e.Price <= v.Price : e.Price >= v.Price;
                        if (!through) continue;
                        v.TradedSince += e.Vol; v.Ahead = Math.Max(0, v.Ahead - e.Vol);
                        if (v.Ahead <= 0) { v.Done = true; v.FillT = now; v.MidAtFill = tobBid > 0 && tobAsk > 0 ? (tobBid + tobAsk) * 0.5 : v.Price; filledPending.Add(v); }
                    }
                }
                else if (e.Pos <= 1 && (e.Side == 0 ? bidSide : askSide).Apply(e.Op, e.Pos, e.Price, e.Vol))
                {
                    // touch (row 0) changed - row-1-only events fall through to the per-event upkeep below
                    if (e.Side == 0) { tobBid = bidSide.Tob; bidSz = bidSide.TobSz; } else { tobAsk = askSide.Tob; askSz = askSide.TobSz; }
                    touchEvents.Add(now);
                    foreach (VPost v in open)
                    {
                        if (v.Done) continue;
                        double touch = v.Side > 0 ? tobBid : tobAsk; long sizeNow = v.Side > 0 ? bidSz : askSz;
                        bool movedAway = v.Side > 0 ? touch > v.Price : touch < v.Price;
                        if (movedAway && touch > 0) { v.Done = true; v.Missed = true; v.EndT = now; continue; }
                        if (touch <= 0) continue;
                        if (Math.Abs(touch - v.Price) < tick * 0.5)
                        {
                            long delta = sizeNow - v.LevelSeen;
                            if (delta < 0) { long cancels = Math.Max(0, -delta - v.TradedSince); if (cancels > 0 && k > 0) { double tot = v.Ahead + v.Behind; double share = tot > 0 ? v.Ahead / tot : 0; double ca = Math.Min(v.Ahead, cancels * share * k); v.Ahead -= ca; v.Behind = Math.Max(0, v.Behind - (cancels - ca)); } }
                            else if (delta > 0) v.Behind += delta;
                            v.LevelSeen = sizeNow; v.TradedSince = 0;
                        }
                        else { v.Ahead = 0; v.Behind = 0; v.LevelSeen = 0; v.TradedSince = 0; }
                    }
                }
                // retire old
                while (touchEvents.Count > 0 && now - touchEvents[0] > TimeSpan.TicksPerSecond) touchEvents.RemoveAt(0);
                long keep = 2 * TimeSpan.TicksPerSecond;
                while (sellAggr.Count > 0 && now - sellAggr[0].Key > keep) sellAggr.RemoveAt(0);
                while (buyAggr.Count > 0 && now - buyAggr[0].Key > keep) buyAggr.RemoveAt(0);
                // finalize posts past max horizon
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    VPost v = open[i];
                    if (!v.Done && now - v.T < maxH) continue;
                    if (!v.Done) { v.Done = true; v.Missed = false; v.EndT = now; }
                    X.Add(v.X); fillMs.Add(v.FillT >= 0 ? (v.FillT - v.T) / TimeSpan.TicksPerMillisecond : -1); missed.Add(v.Missed);
                    censorMs.Add(v.FillT >= 0 ? (v.FillT - v.T) / TimeSpan.TicksPerMillisecond : Math.Max(1, (v.EndT - v.T) / TimeSpan.TicksPerMillisecond));
                    open.RemoveAt(i);
                }
                // markouts for filled posts and cross baselines (same instants)
                double mid = tobBid > 0 && tobAsk > 0 ? (tobBid + tobAsk) * 0.5 : 0;
                if (mid > 0)
                {
                    // Touch-to-touch markouts: the exit is at the touch you would trade against later
                    // (a buy exits at the later BID, a sell at the later ASK). No mid flattery.
                    for (int i = filledPending.Count - 1; i >= 0; i--)
                    {
                        VPost v = filledPending[i]; if (now - v.FillT < mkWindow) continue;
                        double exitPx = v.Side > 0 ? tobBid : tobAsk;
                        sumFillMk += (exitPx - v.Price) / tick * v.Side; nFillMk++; filledPending.RemoveAt(i);
                    }
                    for (int i = crosses.Count - 1; i >= 0; i--)
                    {
                        VCross c = crosses[i]; if (now - c.T < mkWindow) continue;
                        double exitPx = c.Side > 0 ? tobBid : tobAsk;
                        sumCrossMk += (exitPx - c.Price) / tick * c.Side; nCrossMk++; crosses.RemoveAt(i);
                    }
                }
                // new virtual posts on a cadence, both sides
                if (tobBid > 0 && tobAsk > tobBid && (lastPost == 0 || now - lastPost >= (long)everyMs * TimeSpan.TicksPerMillisecond))
                {
                    lastPost = now;
                    double spread = (tobAsk - tobBid) / tick; sumSpread += spread; nSpread++;
                    double mp = (bidSz + askSz) > 0 ? (tobBid * askSz + tobAsk * bidSz) / (bidSz + askSz) : mid;
                    double sellRate = 0, buyRate = 0; foreach (KeyValuePair<long, long> a in sellAggr) sellRate += a.Value; foreach (KeyValuePair<long, long> a in buyAggr) buyRate += a.Value; sellRate /= 2.0; buyRate /= 2.0;
                    double touchRate = touchEvents.Count;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        double price = side > 0 ? tobBid : tobAsk; long ahead = side > 0 ? bidSz : askSz; long opp = side > 0 ? askSz : bidSz;
                        double imb = (double)(bidSz - askSz) / Math.Max(1, bidSz + askSz) * side;
                        double ours = side > 0 ? sellRate : buyRate, other = side > 0 ? buyRate : sellRate;
                        double mpOff = (mp - mid) / tick * side;
                        open.Add(new VPost { T = now, Side = side, Price = price, Ahead = ahead, Behind = 0, LevelSeen = ahead, TradedSince = 0, MidAtPost = mid,
                            X = FillModel.Features(ahead, opp, imb, ours, other, touchRate, spread, mpOff) });
                        crosses.Add(new VCross { T = now, Side = side, Price = side > 0 ? tobAsk : tobBid });
                    }
                }
            }
            int n = X.Count;
            Console.WriteLine("fill-model: virtual posts=" + n + " (every " + everyMs + " ms, both sides), k=" + k.ToString("F2", CultureInfo.InvariantCulture));
            if (n < 50) { Console.WriteLine("fill-model: not enough samples to fit"); return; }
            // standardize
            double[] mean = new double[FillModel.F], std = new double[FillModel.F];
            for (int j = 0; j < FillModel.F; j++) { double m = 0; foreach (double[] x in X) m += x[j]; m /= n; double v = 0; foreach (double[] x in X) v += (x[j] - m) * (x[j] - m); mean[j] = m; std[j] = Math.Sqrt(v / n); if (std[j] < 1e-9) std[j] = 1; }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# LNN fill model: P(fill within T | queue/book state). Fitted by LnnExecBacktest --fit-fill-model.");
            sb.AppendLine("format=LNNFILLMODEL2   # hazard model on exact fill times; touch-to-touch markouts");
            sb.AppendLine("cancel_ahead=" + k.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("samples=" + n);
            sb.AppendLine("half_spread_ticks=" + (nSpread > 0 ? sumSpread / nSpread / 2.0 : 0.5).ToString("F4", CultureInfo.InvariantCulture));
            double fillMkTouch = nFillMk > 0 ? sumFillMk / nFillMk : 0;
            double crossMkTouch = nCrossMk > 0 ? sumCrossMk / nCrossMk : 0;
            sb.AppendLine("markout_ms=2000");
            sb.AppendLine("fill_markout_touch_ticks=" + fillMkTouch.ToString("F4", CultureInfo.InvariantCulture) + "   # passive fill, exit at the touch 2 s later, random direction (n=" + nFillMk + "); negative = adverse selection");
            sb.AppendLine("cross_markout_touch_ticks=" + crossMkTouch.ToString("F4", CultureInfo.InvariantCulture) + "   # market order at the far touch, exit at the touch 2 s later (n=" + nCrossMk + "); ~ -spread");
            sb.AppendLine("adverse_fill_ticks=" + (-fillMkTouch).ToString("F4", CultureInfo.InvariantCulture) + "   # = -fill_markout_touch_ticks (kept for v1 readers)");
            sb.AppendLine("features=log1p_ahead,log1p_opposite,imbalance_signed,trade_rate_our_side,trade_rate_other_side,touch_rate,spread_ticks,microprice_offset_signed");
            sb.AppendLine("mean=" + Join(mean)); sb.AppendLine("std=" + Join(std));
            // ── Exponential hazard fit on EXACT fill times, censored posts included ──
            //   fill at t:      contributes  log(lambda) - lambda t
            //   censored at t:  contributes            - lambda t     (missed / timed out: it had not filled by t)
            //   lambda = exp(b + w.z), t in seconds. Fitted by SGD on the negative log-likelihood.
            // Weibull: S(t) = exp(-(lambda t)^kappa). kappa > 1 means the fill rate RISES with time
            // in the queue (it drains), which removes the exponential's spurious mass at t ~ 0.
            //   fill at t:     -loglik = -log kappa - kappa log lambda - (kappa-1) log t + u,  u = (lambda t)^kappa
            //   censored at t: -loglik = u
            double hb = Math.Log(Math.Max(1e-3, (double)CountFills(fillMs) / Math.Max(1e-3, SumSeconds(censorMs)))); double[] hw = new double[FillModel.F];
            double hc = 0.0;   // kappa = exp(hc)
            {
                double lr = 0.02; Random rnd = new Random(3);
                for (int epoch = 0; epoch < 60; epoch++)
                {
                    for (int it = 0; it < n; it++)
                    {
                        int i = rnd.Next(n); double tSec = Math.Max(0.001, censorMs[i] / 1000.0); bool filled = fillMs[i] >= 0;
                        double z = hb; for (int j = 0; j < FillModel.F; j++) z += hw[j] * (X[i][j] - mean[j]) / std[j];
                        if (z > 8) z = 8; if (z < -12) z = -12;
                        double kappa = Math.Exp(Math.Max(-1.5, Math.Min(2.0, hc)));
                        double logLt = z + Math.Log(tSec);
                        double u = Math.Exp(Math.Max(-30, Math.Min(30, kappa * logLt)));
                        double gz = kappa * u - (filled ? kappa : 0.0);                       // d(-loglik)/dz
                        double gc = kappa * u * logLt - (filled ? (1.0 + kappa * logLt) : 0.0); // d(-loglik)/dc
                        if (gz > 5) gz = 5; if (gz < -5) gz = -5; if (gc > 5) gc = 5; if (gc < -5) gc = -5;
                        hb -= lr * gz; hc -= lr * 0.5 * gc;
                        for (int j = 0; j < FillModel.F; j++) hw[j] -= lr * (gz * (X[i][j] - mean[j]) / std[j] + 1e-4 * hw[j]);
                    }
                    lr *= 0.95;
                }
            }
            double kappaFit = Math.Exp(Math.Max(-1.5, Math.Min(2.0, hc)));
            // calibration at a few horizons: predicted 1-exp(-lambda T) vs realized among posts observed at least T (or filled before T)
            foreach (int h in horizons)
            {
                double[] sumP = new double[5], sumY = new double[5]; int[] cnt = new int[5]; int pos = 0, obs = 0;
                for (int i = 0; i < n; i++)
                {
                    bool filledWithin = fillMs[i] >= 0 && fillMs[i] <= h;
                    if (!filledWithin && censorMs[i] < h) continue;   // censored before T: outcome unknown
                    obs++; if (filledWithin) pos++;
                    double z = hb; for (int j = 0; j < FillModel.F; j++) z += hw[j] * (X[i][j] - mean[j]) / std[j];
                    double pz = 1 - Math.Exp(-Math.Pow(Math.Exp(Math.Max(-12, Math.Min(8, z))) * h / 1000.0, kappaFit));
                    int q = Math.Min(4, (int)(pz * 5)); sumP[q] += pz; sumY[q] += filledWithin ? 1 : 0; cnt[q]++;
                }
                StringBuilder cal = new StringBuilder();
                for (int q = 0; q < 5; q++) cal.Append(cnt[q] > 0 ? string.Format(CultureInfo.InvariantCulture, " q{0}: pred {1:F2} real {2:F2} (n={3})", q, sumP[q] / cnt[q], sumY[q] / cnt[q], cnt[q]) : " q" + q + ": -");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "fill-model T={0}ms observed={1} base-rate={2:F1}% calibration:{3}", h, obs, obs > 0 ? 100.0 * pos / obs : 0, cal.ToString()));
            }
            long filledN = CountFills(fillMs); double medianFillMs = MedianFill(fillMs);
            double medianModelMs = 1000.0 * Math.Pow(Math.Log(2), 1.0 / kappaFit) / Math.Max(1e-6, Math.Exp(hb));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "fill-model hazard: fills={0}/{1} censored={2} median-time-to-fill={3:F0} ms (filled only); shape kappa={4:F2} lambda at mean state = {5:F3}/s -> model median {6:F0} ms",
                filledN, n, n - filledN, medianFillMs, kappaFit, Math.Exp(hb), medianModelMs));
            sb.AppendLine("hazard_bias=" + hb.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("hazard_w=" + Join(hw));
            sb.AppendLine("hazard_shape=" + kappaFit.ToString("R", CultureInfo.InvariantCulture) + "   # Weibull shape; >1 = fill rate rises with time in queue");
            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine("fill-model written " + outPath + "  fill-markout@touch=" + fillMkTouch.ToString("F3", CultureInfo.InvariantCulture) + "t cross-markout@touch=" + crossMkTouch.ToString("F3", CultureInfo.InvariantCulture) + "t half_spread=" + (nSpread > 0 ? sumSpread / nSpread / 2.0 : 0.5).ToString("F2", CultureInfo.InvariantCulture) + "t");
        }
        private static long CountFills(List<long> fillMs) { long c = 0; foreach (long f in fillMs) if (f >= 0) c++; return c; }
        private static double SumSeconds(List<long> ms) { double s = 0; foreach (long v in ms) s += Math.Max(1, v) / 1000.0; return s; }
        private static double MedianFill(List<long> fillMs) { List<long> f = new List<long>(); foreach (long v in fillMs) if (v >= 0) f.Add(v); if (f.Count == 0) return 0; f.Sort(); return f[f.Count / 2]; }
        private static string Join(double[] a) { string[] p = new string[a.Length]; for (int i = 0; i < a.Length; i++) p[i] = a[i].ToString("R", CultureInfo.InvariantCulture); return string.Join(",", p); }
    }

    // ── Faithful port of the strategy's executioner (measure mode) ──
    internal sealed class Sim
    {
        private readonly Config c; private readonly double tick; private readonly int qty;
        private readonly FillModel model; private readonly double expectedMove; private readonly Dictionary<long, double> pUpBySignal;
        private double curPUp = 0.5; private long lastEvEval;
        private double tobBidPrice, tobAskPrice; private long tobBidSize, tobAskSize;
        private long bidRestingSince = long.MinValue, askRestingSince = long.MinValue; private double bidRestingPrice, askRestingPrice; private long bidRestingSize, askRestingSize;
        private readonly List<long> touchFlicker = new List<long>();
        private readonly List<KeyValuePair<long, long>> sellAggr = new List<KeyValuePair<long, long>>(), buyAggr = new List<KeyValuePair<long, long>>();
        private int exState, exSide; private double exPostPrice; private long exPostT, exQueueAhead, exFilledInto; private int exChased;
        private double qAhead, qBehind; private long levelSizeSeen, tradedSinceBook;
        private double exFillPrice, exMidAtFill; private long exFillT; private double mk1, mk2; private bool mk1Done, mk2Done;
        private readonly Result r;
        private sealed class Baseline { public long T; public int Side; public double CrossPrice; }
        private readonly List<Baseline> baselines = new List<Baseline>();
        // Fills are scored independently of the executioner's arm state, so a new
        // signal arriving inside the markout window cannot lose the sample.
        private sealed class PendingFill { public long T; public int Side; public double FillPrice, MidAtFill, Mk1; public bool Mk1Done; }
        private readonly List<PendingFill> fills = new List<PendingFill>();

        internal Sim(Config cfg, double tickSize, int quantity, FillModel fillModel, double expectedMoveTicks, Dictionary<long, double> pUp)
        { c = cfg; tick = tickSize; qty = quantity; model = fillModel; expectedMove = expectedMoveTicks; pUpBySignal = pUp; r = new Result { Cfg = cfg }; }

        private double EdgeTicks() { return (2.0 * curPUp - 1.0) * expectedMove; }
        private double[] EvFeatures(bool buy, long now)
        {
            long ours = buy ? bidSz() : askSz(), opp = buy ? askSz() : bidSz();
            double mid = Mid(), mp = Microprice();
            double imb = (double)(tobBidSize - tobAskSize) / Math.Max(1, tobBidSize + tobAskSize) * (buy ? 1 : -1);
            double ourRate = AggrRate(buy, now), otherRate = AggrRate(!buy, now);
            double ahead = exState == 1 ? qAhead : ours;
            return FillModel.Features(ahead, opp, imb, ourRate, otherRate, FlickerPerSec(now), (tobAskPrice - tobBidPrice) / tick, (mp - mid) / tick * (buy ? 1 : -1));
        }
        private long bidSz() { return tobBidSize; } private long askSz() { return tobAskSize; }
        private double AggrRate(bool sellSide, long now) { List<KeyValuePair<long, long>> src = sellSide ? sellAggr : buyAggr; double v = 0; for (int i = src.Count - 1; i >= 0; i--) { if (now - src[i].Key <= 2 * TimeSpan.TicksPerSecond) v += src[i].Value; else break; } return v / 2.0; }
        // EV of holding/posting vs crossing now. Returns true when crossing is better.
        private bool EvPrefersCross(bool buy, long now, double remainingMs)
        {
            double p = model.PFill(EvFeatures(buy, now), Math.Max(50, remainingMs));
            double edge = EdgeTicks(), spread = (tobAskPrice - tobBidPrice) / tick;
            // touch-to-touch values from the fitted tape (v1 files fall back to half-spread arithmetic)
            double fillMk = !double.IsNaN(model.FillMarkoutTouchTicks) ? model.FillMarkoutTouchTicks : -model.AdverseFillTicks;
            double crossMk = !double.IsNaN(model.CrossMarkoutTouchTicks) ? model.CrossMarkoutTouchTicks : -spread;
            double evCross = crossMk + edge;
            double evMiss = c.OnTimeout == 1 ? (crossMk + edge * 0.5) : 0.0;   // late cross keeps some of the edge; abandon keeps none
            double evPost = p * (fillMk + edge) + (1 - p) * evMiss;
            return evCross > evPost;
        }

        private double Mid() { return tobBidPrice > 0 && tobAskPrice > 0 ? (tobBidPrice + tobAskPrice) * 0.5 : 0; }
        private double Microprice() { long s = tobBidSize + tobAskSize; if (s <= 0 || tobBidPrice <= 0 || tobAskPrice <= 0) return Mid(); return (tobBidPrice * tobAskSize + tobAskPrice * tobBidSize) / s; }
        private double Imbalance() { long s = tobBidSize + tobAskSize; return s <= 0 ? 0.0 : (double)(tobBidSize - tobAskSize) / s; }
        private double FlickerPerSec(long now) { while (touchFlicker.Count > 0 && now - touchFlicker[0] > TimeSpan.TicksPerSecond) touchFlicker.RemoveAt(0); return touchFlicker.Count; }
        private long AggrInto(bool sellSide, long now)
        {
            List<KeyValuePair<long, long>> src = sellSide ? sellAggr : buyAggr; long v = 0;
            for (int i = src.Count - 1; i >= 0; i--) { if (now - src[i].Key <= (long)c.AggrWindowMs * TimeSpan.TicksPerMillisecond) v += src[i].Value; else break; }
            return v;
        }
        private void TrimAggr(long now)
        {
            long keep = (long)Math.Max(c.AggrWindowMs, 2000) * TimeSpan.TicksPerMillisecond;
            while (sellAggr.Count > 0 && now - sellAggr[0].Key > keep) sellAggr.RemoveAt(0);
            while (buyAggr.Count > 0 && now - buyAggr[0].Key > keep) buyAggr.RemoveAt(0);
        }

        internal Result Run(List<Ev> events, List<KeyValuePair<long, int>> signals)
        {
            int si = 0;
            foreach (Ev e in events)
            {
                // deliver signals whose time has come
                while (si < signals.Count && signals[si].Key <= e.T) { Arm(signals[si].Value, e.T); si++; }
                if (e.Side == 2) OnTrade(e.Price, e.Vol, e.T);
                else if (e.Pos <= 1) OnTopOfBook(e);
                ScoreFills(e.T);
                Baselines(e.T);
            }
            return r;
        }

        private void Arm(int side, long now)
        {
            r.Signals++;
            if (exSide != 0 && exState == 0) r.GateNeverPassed++;
            if (exSide != 0 && exState == 1) r.ReplacedWhilePosting++;
            exSide = side; exState = 0; exChased = 0; exFilledInto = 0; mk1Done = mk2Done = false; mk1 = mk2 = 0; exFillPrice = 0; exMidAtFill = 0;
            double pu; curPUp = pUpBySignal != null && pUpBySignal.TryGetValue(now, out pu) ? pu : 0.5;
            // cross-the-spread baseline for this same signal
            double cross = side > 0 ? tobAskPrice : tobBidPrice;
            if (cross > 0) baselines.Add(new Baseline { T = now, Side = side, CrossPrice = cross });
        }

        private readonly TouchSide simBid = new TouchSide(), simAsk = new TouchSide();
        private void OnTopOfBook(Ev e)
        {
            long now = e.T;
            TouchSide ts = e.Side == 0 ? simBid : simAsk;
            if (!ts.Apply(e.Op, e.Pos, e.Price, e.Vol)) return;   // row-1 bookkeeping only: touch unchanged
            if (e.Side == 0)
            {
                double np = ts.Tob; long ns = ts.TobSz;
                if (np != bidRestingPrice || ns < bidRestingSize) { bidRestingPrice = np; bidRestingSince = now; }
                bidRestingSize = ns; tobBidPrice = np; tobBidSize = ns; touchFlicker.Add(now);
            }
            else
            {
                double np = ts.Tob; long ns = ts.TobSz;
                if (np != askRestingPrice || ns < askRestingSize) { askRestingPrice = np; askRestingSince = now; }
                askRestingSize = ns; tobAskPrice = np; tobAskSize = ns; touchFlicker.Add(now);
            }
            ExecOnBook(now);
        }

        private void OnTrade(double price, long volume, long now)
        {
            if (tobAskPrice > 0 && price >= tobAskPrice) buyAggr.Add(new KeyValuePair<long, long>(now, volume));
            else if (tobBidPrice > 0 && price <= tobBidPrice) sellAggr.Add(new KeyValuePair<long, long>(now, volume));
            TrimAggr(now);
            ExecOnTrade(price, volume, now);
        }

        private bool PostGatePasses(long now)
        {
            if (tobBidPrice <= 0 || tobAskPrice <= 0) return false;
            bool buy = exSide > 0; double imb = Imbalance();
            if (buy && imb < c.Imbalance) return false;
            if (!buy && -imb < c.Imbalance) return false;
            if (c.Microprice) { double mp = Microprice(), mid = Mid(); if (buy && mp < mid) return false; if (!buy && mp > mid) return false; }
            if (c.RestingMs > 0) { long since = buy ? bidRestingSince : askRestingSince; if (since == long.MinValue || now - since < (long)c.RestingMs * TimeSpan.TicksPerMillisecond) return false; }
            if (c.Flicker > 0 && FlickerPerSec(now) > c.Flicker) return false;
            if (c.AggrQty > 0 && AggrInto(buy, now) >= c.AggrQty) return false;
            return true;
        }

        private void ExecOnBook(long now)
        {
            if (exSide == 0) return;
            bool buy = exSide > 0;
            if (exState == 0)
            {
                if (c.Policy == 1)
                {
                    if (tobBidPrice <= 0 || tobAskPrice <= 0) return;
                    if (EvPrefersCross(buy, now, c.TimeoutMs)) { r.EvCrossAtArm++; CrossFill(now); return; }
                }
                else if (!PostGatePasses(now)) return;
                exPostPrice = buy ? tobBidPrice : tobAskPrice; exQueueAhead = buy ? tobBidSize : tobAskSize;
                exPostT = now; exFilledInto = 0; exChased = 0; r.Posts++; exState = 1;
                qAhead = exQueueAhead; qBehind = 0; levelSizeSeen = exQueueAhead; tradedSinceBook = 0;
                return;
            }
            if (exState == 1)
            {
                double touch = buy ? tobBidPrice : tobAskPrice;
                bool movedAway = buy ? touch > exPostPrice : touch < exPostPrice;
                if (movedAway && touch > 0)
                {
                    int moved = (int)Math.Round(Math.Abs(touch - exPostPrice) / tick);
                    if (exChased + moved > c.Chase) { CompleteNoFill(4, now); return; }
                    exChased += moved; r.SumChasedTicks += moved;
                    exPostPrice = touch; exQueueAhead = buy ? tobBidSize : tobAskSize; exFilledInto = 0;
                    qAhead = exQueueAhead; qBehind = 0; levelSizeSeen = exQueueAhead; tradedSinceBook = 0;
                }
                if (!movedAway && touch > 0)
                {
                    long sizeNow = buy ? tobBidSize : tobAskSize;
                    if (Math.Abs(touch - exPostPrice) < tick * 0.5)
                    {
                        long delta = sizeNow - levelSizeSeen;
                        if (delta < 0)
                        {
                            long cancels = Math.Max(0, -delta - tradedSinceBook);
                            if (cancels > 0 && c.CancelAhead > 0)
                            {
                                double total = qAhead + qBehind; double share = total > 0 ? qAhead / total : 0.0;
                                double cancelAhead = Math.Min(qAhead, cancels * share * c.CancelAhead);
                                qAhead -= cancelAhead; qBehind = Math.Max(0, qBehind - (cancels - cancelAhead));
                            }
                        }
                        else if (delta > 0) qBehind += delta;
                        levelSizeSeen = sizeNow; tradedSinceBook = 0;
                    }
                    else { qAhead = 0; qBehind = 0; levelSizeSeen = 0; tradedSinceBook = 0; }
                }
                if (c.Policy == 1 && now - lastEvEval >= 100 * TimeSpan.TicksPerMillisecond)
                {
                    lastEvEval = now;
                    double remaining = c.TimeoutMs - (now - exPostT) / (double)TimeSpan.TicksPerMillisecond;
                    if (EvPrefersCross(buy, now, remaining)) { r.EvCrossWhilePosting++; CrossFill(now); return; }
                }
                long aggr = AggrInto(buy, now); long touchSize = buy ? tobBidSize : tobAskSize;
                if (c.AggrQty > 0 && aggr >= c.AggrQty && touchSize < exQueueAhead) { CompleteNoFill(1, now); return; }
                if (c.TimeoutMs > 0 && now - exPostT >= (long)c.TimeoutMs * TimeSpan.TicksPerMillisecond)
                {
                    if (c.OnTimeout == 1) CrossFill(now); else CompleteNoFill(2, now);
                }
            }
        }

        private void ExecOnTrade(double price, long volume, long now)
        {
            if (exState != 1) return;
            bool buy = exSide > 0;
            bool through = buy ? price <= exPostPrice : price >= exPostPrice;
            if (!through) return;
            exFilledInto += volume; tradedSinceBook += volume; qAhead = Math.Max(0, qAhead - volume);
            bool filled = c.CancelAhead > 0 ? qAhead <= 0 : exFilledInto >= exQueueAhead;
            if (filled) { r.Fills++; r.SumTimeToFillMs += (now - exPostT) / (double)TimeSpan.TicksPerMillisecond; RecordFill(exPostPrice, now); }
        }

        private void CrossFill(long now)
        {
            bool buy = exSide > 0; double cross = buy ? tobAskPrice : tobBidPrice; if (cross <= 0) cross = Mid();
            r.Crosses++; RecordFill(cross, now);
        }

        private void RecordFill(double fillPrice, long now)
        {
            fills.Add(new PendingFill { T = now, Side = exSide, FillPrice = fillPrice, MidAtFill = Mid() });
            exSide = 0; exState = 0;   // executioner is free; the fill is scored from the list
        }

        private void MarkoutTick(long now) { }

        // Exit price basis for markouts: the touch you would trade against (buy -> bid, sell -> ask).
        private double ExitPx(int side) { return side > 0 ? tobBidPrice : tobAskPrice; }
        private void ScoreFills(long now)
        {
            for (int i = fills.Count - 1; i >= 0; i--)
            {
                PendingFill f = fills[i]; double mid = Mid(); if (mid <= 0) continue;
                double exitPx = ExitPx(f.Side);
                if (!f.Mk1Done && now - f.T >= (long)c.Markout1Ms * TimeSpan.TicksPerMillisecond) { f.Mk1 = (exitPx - f.FillPrice) / tick * f.Side; f.Mk1Done = true; }
                if (now - f.T >= (long)c.Markout2Ms * TimeSpan.TicksPerMillisecond)
                {
                    double m2 = (exitPx - f.FillPrice) / tick * f.Side, eff = (f.MidAtFill - f.FillPrice) / tick * f.Side;
                    r.MkCount++; r.SumEffSpread += eff; r.SumMk1 += f.Mk1; r.SumMk2 += m2;
                    fills.RemoveAt(i);
                }
            }
        }

        private void CompleteNoFill(int outcome, long now)
        {
            if (outcome == 1) r.CancelAggr++; else if (outcome == 2) r.CancelTimeout++; else if (outcome == 4) r.CancelChase++;
            exSide = 0; exState = 0;
        }

        private void Baselines(long now)
        {
            for (int i = baselines.Count - 1; i >= 0; i--)
            {
                Baseline b = baselines[i];
                if (now - b.T < (long)c.Markout2Ms * TimeSpan.TicksPerMillisecond) continue;
                double mid = Mid(); if (mid > 0) { r.SumCrossMk2 += (ExitPx(b.Side) - b.CrossPrice) / tick * b.Side; r.CrossBaselineCount++; }
                baselines.RemoveAt(i);
            }
        }
    }
}
