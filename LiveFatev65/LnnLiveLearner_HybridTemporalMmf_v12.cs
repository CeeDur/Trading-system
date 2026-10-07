// Online nested-temporal learner for NinjaTrader DOM bridge frames.
// It accepts frames through a local named pipe or the direct MMF ring.
// It has NO broker or account API. With --emit-orders it can write join
// intents (op 30/31) / flatten (op 13) into the local command ring that
// LnnOrderExecStrategy reads — it never talks to a broker directly; the NT8
// strategy remains the only thing that places orders, and its hard caps
// (max position, order rate, daily loss, hours) still apply.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime;
using System.Threading;

namespace LnnLiveLearner
{
    internal sealed class Program
    {
        private const int DepthLevels = 10;
        // 42 static-book features + 32 price-aligned order-flow features.
        // The latter compare each price level against the preceding snapshot,
        // rather than merely comparing array positions after a price move.
        private const int RawFeatures = 74;
        private const int ModelFeatures = RawFeatures * 3;
        private const int LegacyFrameSize = 376;
        private const int InstrumentedFrameSize = 392;
        private const uint Magic = 0x314E4E4C;
        private const ushort LegacyVersion = 1;
        private const ushort InstrumentedVersion = 2;
        private const ushort FullBookFlag = 4;
        private const uint CheckpointMagic = 0x3154434C;
        // Version 3 adds order-flow features, so prior 42-feature checkpoints
        // must not be silently reused.
        private const ushort CheckpointVersion = 3;
        private const double DefaultPaperLongThreshold = 0.60;
        private const double DefaultPaperShortThreshold = 0.40;
        private const double DefaultMinimumProfitTargetTicks = 1.0;
        private const long MinTrainingForPaperSignal = 100;
        // MES labels need enough time to clear the entry spread and reach a
        // real executable outcome.  Very short defaults produced almost only
        // expiry/no-trade samples rather than training labels.
        private const int DefaultMinimumLabelHorizonMilliseconds = 2000;
        private const int DefaultMaximumLabelHorizonMilliseconds = 30000;
        // Long deadlines retain pending examples. One minute covers slow MES
        // conditions without allowing an unbounded live-memory queue.
        private const int MaximumAllowedLabelHorizonMilliseconds = 60000;
        private const int MinimumCandidateSpacingMilliseconds = 50;
        // REVERT SWITCH: false = pure 222-feature threshold model only. No
        // cross-market shadow feeds (ES/NQ/VX/SPY), no reversal shadow/fusion.
        // Flip to true to restore the full stack. The probability, learning and
        // paper signal are byte-identical to the pre-shadow/pre-reversal engine
        // when this is false.
        private const bool EnableCrossMarketAndReversal = false;
        private const int AutomaticCheckpointIntervalMilliseconds = 30000;
        private const int NoveltyCheckpointCooldownMilliseconds = 300000;
        private const int NoveltyNormalClearMilliseconds = 5000;
        private const double NoveltyEnterScore = 2.5;
        private const int NoveltyEnterExtremeFeatures = 12;
        private const double NoveltyExitScore = 1.2;
        private const int NoveltyExitExtremeFeatures = 2;
        // The dashboard consumes this isolated line without repainting the
        // rest of its telemetry. Twenty Hertz looks live while remaining well
        // below a rate that can back up the process-output pipe.
        private const int LiveProbabilityOutputMilliseconds = 50;
        // Every fifth resolved example is kept out of gradient learning and is
        // used only to decide whether a checkpoint is genuinely improving.
        private const int DefaultValidationStride = 5;
        private const int ValidationWindow = 200;
        private const int MinimumValidationSamplesForBest = 50;
        private const int MinimumReversalResolvedForShadowFusion = 100;

        private sealed class Settings
        {
            internal string PipeName = "LnnDepthBridge";
            // When set, DOM events are read directly from this shared-memory
            // ring (LnnDomMmfWriter format) — no pipe, no forwarder process.
            // PipeName is still used to name the paper/model control pipes.
            internal string MmfMap;
            // Cross-market shadow rings (blank = skip that market)
            internal string EsMap = @"Local\LNN_DOM_MMF_ES", NqMap = @"Local\LNN_DOM_MMF_NQ", VxMap = @"Local\LNN_DOM_MMF_VX", SpyMap = @"Local\LNN_DOM_MMF_SPY";
            // Direct-MMF granularity: emit a snapshot per book-changing event
            // (finest resolution, higher CPU) instead of once per drain batch.
            internal bool EmitPerEvent;
            // v64k: in per-event mode, once the ring backlog exceeds this many events the
            // rest of the backlog is coalesced into one frame (latest book) instead of
            // being replayed one frame per event. 0 = never coalesce (pure per-event).
            internal int CoalesceAboveEvents;
            // Direct order emission (opt-in). When EmitOrders is true, on a
            // paper-signal CHANGE the engine writes op 30/31 (buy/sell join) or
            // op 13 (flatten) straight into OrderCmdMap, cutting the
            // engine->stdout->dashboard->ring hop. OFF by default.
            internal bool EmitOrders;
            internal string OrderMode = "market";  // "market" (op 0/1, fastest fill) or "join" (op 30/31, via executioner)
            internal string OrderCmdMap;          // e.g. Local\LNN_ORDER_CMD_MES
            internal int OrderQty = 1;
            internal double OrderTradeGapSeconds;  // min seconds between entries (0 = off)
            internal double OrderHoldSeconds;      // signal must persist this long (0 = act at once)
            // Paper exit: when true, an open paper trade closes ONLY on its
            // profit target or stop — never on the label-horizon deadline. Makes
            // the paper win-rate a clean ±target bracket outcome (no coin-flip
            // timeout closes). Does NOT affect training labels.
            internal bool PaperNoTimeout;
            internal int PaperMaxOpen = 1; // max concurrent open paper trades
            internal int PaperEntryLatencyMs; // signal->fill delay modeled in the paper sim (0 = instant)
            // v64k paper mirror: the dashboard's AI Trader drives paper entries/flattens through
            // the paper-control pipe ('L' 'S' 'F') with exactly the guards a live trade gets, and
            // the exits mirror the strategy's bracket (stop / target / trail ticks from the fill).
            internal bool PaperExternal;
            internal double PaperStopTicks, PaperTargetTicks, PaperTrailTicks;   // 0 = unset (falls back to +/- Target ticks)
            // Round-trip latency the LEARNER assumes: every training label, the
            // 2-tick skill meter and the paper-outcome scorer measure from the
            // touch/mid at signal-time + this delay, not at the signal itself.
            // 0 = legacy (assumes instant execution).
            internal int LabelLatencyMs;
            // Pin this engine process to one CPU core (-1 = off). The dashboard
            // passes 0 so the trading path keeps the first core while the hog
            // runs on the others.
            internal int AffinityCore = -1;
            // Hog evidence: the nested hog publishes its latest read-out into a
            // small shared-memory record; the engine reads it per frame and
            // feeds a separate HOG FUSION head that learns from the same labels
            // how much that evidence is worth. Shadow mode only scores it;
            // live mode uses the fused probability for the paper signal.
            internal string HogEvidenceMap = "";        // empty = off
            internal int HogEvidenceMaxAgeMs = 2000;    // older evidence counts as absent
            internal string HogFusionMode = "shadow";   // shadow | live
            internal bool HogAgreeGate;                 // engine-direct ENTRY orders require hog agreement
            // Transient-gap handling: a level that briefly disappears keeps its
            // last-known size for this long before falling back to zero volume.
            internal int BookGapStalenessMs = 500;
            // NT8 depth semantics: Operation.Add INSERTS a row at Position (deeper
            // rows shift down) and Operation.Remove DELETES the row (deeper rows
            // shift up). 1 = apply those shifts (correct book); 0 = legacy
            // "set/zero the slot" reconstruction (pre-v64j behaviour).
            internal bool DomShift = true;
            // Warm the book from the ring's history on attach instead of waiting
            // for fresh events to repopulate every level.
            internal bool DomWarmup = true;
            // "thresholds" (default, classic v12) or "fusion" (learned blend of
            // main probability + cross-market shadow evidence)
            internal string SignalMode = "thresholds";
            // Confirmation gate: how far the fusion/shadow read must oppose a
            // v12 trade before it vetoes it (0 = veto on any disagreement past
            // 0.5; higher = only veto on strong disagreement).
            internal double FusionConfirmMargin = 0.05;
            // Resting-depth filter: a level's size only counts toward features
            // once it has stayed unchanged this long (ms). Phantom size pulled
            // faster never enters the model. 0 = off (raw book, classic v12).
            internal int RestingDepthMs;
            // EMA smoothing of the SIGNAL probability (0 = off/raw). Lower alpha
            // = heavier smoothing = calmer signal but more lag. The model still
            // trains on the raw probability; only the trade decision is smoothed.
            internal double ProbabilitySmoothingAlpha;
            // Regime gate: stand aside (force NO-TRADE) when the market has
            // moved less than this many ticks over the last few seconds (flat,
            // nothing to predict) or when novelty exceeds this (a regime
            // transition the single model is still adapting to). 0 = off.
            internal double RegimeGateMinRangeTicks;
            internal double RegimeGateMaxNovelty;
            internal double TickSize = 0.0001;
            internal int HorizonEvents = 20;
            internal double LearningRate = 0.002;
            // 1 label in N is held out for unbiased validation (the rest train).
            internal int ValidationStride = DefaultValidationStride;
            internal double CostTicks = 0.0;
            internal double MinimumProfitTargetTicks = DefaultMinimumProfitTargetTicks;
            internal double LabelStopTicks;        // v64r: 0 = same as the target (symmetric labels)
            internal double PaperLongThreshold = DefaultPaperLongThreshold;
            internal double PaperShortThreshold = DefaultPaperShortThreshold;
            internal bool PaperSimulationStartEnabled = false;
            internal int MinimumLabelHorizonMilliseconds = DefaultMinimumLabelHorizonMilliseconds;
            internal int MaximumLabelHorizonMilliseconds = DefaultMaximumLabelHorizonMilliseconds;
            internal int ReversalTriggerTicks = 2;
            internal int ReversalDistanceTicks = 2;
            internal int ReversalTrendTicks = 6;
            internal int ReversalTimeoutMilliseconds = 5000;
            internal double ReversalProbabilityThreshold = 0.50;
            // Zero means every frame; positive values coalesce heavy model work
            // while the pipe reader continues draining every incoming frame.
            internal int MinProcessingIntervalMilliseconds = 0;
            internal string SaveFile;
            // Raw DOM EVENT recording (every ring event: depth add/update/remove
            // with level position, and trades) for the offline executioner
            // backtester / replayer. 40-byte records, written off the hot path.
            internal string SaveEventsFile;
            // Optional startup-only source.  When set, the loaded state is
            // written to CheckpointFile rather than overwriting this source.
            internal string LoadCheckpointFile;
            internal string CheckpointFile;
            internal string BestCheckpointFile;
            internal string ManualCheckpointFile;
            internal string NoveltyCheckpointDirectory;
            internal string LearningLogFile;
            internal string WeightUpdateLogFile;
            internal string PaperTradeLogFile;
            // Optional slow-side trainer contract.  It never runs on the DOM
            // thread: resolved snapshots are appended here and a compatible
            // compact-head candidate may be adopted at a safe frame boundary.
            internal string TeacherExampleFile;
            internal string TeacherCandidateFile;
            // The dashboard MMF forwarder is a named-pipe server.  This mode
            // makes the native learner its client; the model logic is exactly
            // the same as the legacy direct-bridge server mode.
            internal bool PipeClient;
            internal bool EnableReversalTracker;
        }

        private sealed class WeightChange
        {
            internal int Index;
            internal double InputValue;
            internal double WeightBefore;
            internal double DataGradient;
            internal double RegularizationGradient;
            internal double WeightDelta;
            internal double WeightAfter;
        }

        // ── Cross-market shadow stack (ported from the unified-shadow engine) ──
        // Read-only ring readers for ES/NQ/VX/SPY fed by LnnMultiMarketMmfWriter.
        // They supply trade-flow, momentum, top-of-book imbalance, absorption,
        // and the VX risk flag; a small fusion head learns to weigh all of it
        // against the main temporal model's probability, trained on the same
        // resolved labels.

        private sealed class ShadowRingReader : IDisposable
        {
            private const long RingHeaderBytes = 64, RingMagic = 0x4C4E4E4D4D463031L;
            private const int RingSlotBytes = 48;
            private readonly string mapName;
            private System.IO.MemoryMappedFiles.MemoryMappedFile map;
            private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view;
            private long readSequence, nextAttempt;
            private int capacity;
            internal ShadowRingReader(string name) { mapName = name; }
            internal void Drain(Action<long, long, double, long, byte, byte, short> accept)
            {
                if (!Open()) return;
                try
                {
                    long write = view.ReadInt64(0);
                    if (write < readSequence) readSequence = write;
                    if (write - readSequence > capacity) readSequence = write - capacity;
                    while (readSequence < write)
                    {
                        long next = readSequence + 1, offset = RingHeaderBytes + (next & (capacity - 1)) * RingSlotBytes;
                        long first = view.ReadInt64(offset);
                        if ((first & 1) != 0) break;
                        long eventTime = view.ReadInt64(offset + 8), callbackTime = view.ReadInt64(offset + 16);
                        double price = view.ReadDouble(offset + 24);
                        long volume = view.ReadInt64(offset + 32);
                        byte side = view.ReadByte(offset + 40), operation = view.ReadByte(offset + 41);
                        short position = view.ReadInt16(offset + 42);
                        long second = view.ReadInt64(offset);
                        if (first != second || second != next * 2)
                        {
                            if (second > next * 2) { readSequence = second / 2 - 1; continue; }
                            break;
                        }
                        readSequence = next;
                        accept(eventTime, callbackTime, price, volume, side, operation, position);
                    }
                }
                catch { Close(); }
            }
            private bool Open()
            {
                if (view != null) return true;
                long now = DateTime.UtcNow.Ticks;
                if (now < nextAttempt) return false;
                nextAttempt = now + TimeSpan.TicksPerSecond;
                try
                {
                    map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(mapName, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                    view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    capacity = view.ReadInt32(8);
                    if (view.ReadInt64(16) != RingMagic || capacity < 2 || (capacity & (capacity - 1)) != 0 || view.ReadInt32(12) != RingSlotBytes)
                        throw new InvalidOperationException();
                    readSequence = view.ReadInt64(0);
                    Console.WriteLine("shadow-market connected map=" + mapName);
                    return true;
                }
                catch { Close(); return false; }
            }
            private void Close() { try { view?.Dispose(); } catch { } try { map?.Dispose(); } catch { } view = null; map = null; }
            public void Dispose() { Close(); }
        }

        private sealed class ShadowMarket
        {
            internal readonly string Name;
            internal readonly double Tick;
            internal readonly ShadowRingReader Reader;
            private readonly double[] bid = new double[10], ask = new double[10];
            private readonly long[] bidVol = new long[10], askVol = new long[10];
            private readonly List<double[]> trades = new List<double[]>(); // [utcTicks, price, signedVolume]
            private double lastTrade, volumeEma;
            internal double Fast, Medium, Slow, MomentumTicks, Flow1000;
            internal int AbsorptionDirection; internal double AbsorptionScore; internal long AbsorptionUtc;
            internal long LastUtc;
            internal ShadowMarket(string name, string mapName, double tick) { Name = name; Tick = tick; Reader = new ShadowRingReader(mapName); }
            internal bool Valid { get { return bid[0] > 0 && ask[0] > bid[0]; } }
            internal double Imbalance
            {
                get
                {
                    long b = 0, a = 0;
                    for (int i = 0; i < 5; i++) { b += Math.Max(0, bidVol[i]); a += Math.Max(0, askVol[i]); }
                    return (b - a) / (double)(b + a + 1);
                }
            }
            internal void Drain()
            {
                Reader.Drain(delegate(long eventTime, long callbackTime, double price, long volume, byte side, byte operation, short position)
                {
                    long utc = callbackTime > 0 ? callbackTime : eventTime; if (utc <= 0) utc = DateTime.UtcNow.Ticks;
                    LastUtc = utc;
                    if (side < 2 && position >= 0 && position < 10)
                    {
                        double[] prices = side == 0 ? bid : ask; long[] volumes = side == 0 ? bidVol : askVol;
                        if (operation == 2) { prices[position] = 0; volumes[position] = 0; }
                        else { prices[position] = price; volumes[position] = Math.Max(0, volume); }
                        return;
                    }
                    if (side != 2) return;
                    double signed = 0.0;
                    if (Valid && price >= ask[0] - Tick * .25) signed = volume;
                    else if (Valid && price <= bid[0] + Tick * .25) signed = -volume;
                    else if (lastTrade > 0) signed = price > lastTrade ? volume : price < lastTrade ? -volume : 0.0;
                    lastTrade = price;
                    volumeEma = volumeEma <= 0 ? Math.Max(1, volume) : .04 * Math.Max(0, volume) + .96 * volumeEma;
                    trades.Add(new double[] { utc, price, signed });
                    while (trades.Count > 1 && utc - (long)trades[0][0] > TimeSpan.TicksPerSecond) trades.RemoveAt(0);
                    Recompute(utc);
                });
            }
            private void Recompute(long utc)
            {
                double flow250 = 0, flow1000 = 0, first250 = lastTrade, first1000 = lastTrade;
                foreach (double[] t in trades)
                {
                    flow1000 += t[2];
                    if (utc - (long)t[0] <= 250 * TimeSpan.TicksPerMillisecond) { flow250 += t[2]; first250 = t[1]; }
                    first1000 = t[1];
                }
                Flow1000 = flow1000;
                MomentumTicks = Tick > 0 ? (lastTrade - first250) / Tick : 0;
                Fast = .60 * Fast + .40 * ShadowClip(flow250 / Math.Max(10, volumeEma * 4), -1, 1);
                Medium = .90 * Medium + .10 * ShadowClip(flow1000 / Math.Max(20, volumeEma * 10), -1, 1);
                Slow = .985 * Slow + .015 * ShadowClip((lastTrade - first1000) / Math.Max(Tick, Tick * 4), -1, 1);
                double threshold = Math.Max(20.0, volumeEma * 3.0);
                AbsorptionDirection = 0; AbsorptionScore = 0;
                if (flow1000 <= -threshold && MomentumTicks >= -.5 && Imbalance > -.25) { AbsorptionDirection = 1; AbsorptionScore = ShadowClip(Math.Abs(flow1000) / threshold, 0, 2) * .5; AbsorptionUtc = utc; }
                if (flow1000 >= threshold && MomentumTicks <= .5 && Imbalance < .25) { AbsorptionDirection = -1; AbsorptionScore = ShadowClip(Math.Abs(flow1000) / threshold, 0, 2) * .5; AbsorptionUtc = utc; }
            }
        }

        private static double ShadowClip(double v, double low, double high) { return Math.Max(low, Math.Min(high, v)); }

        private sealed class FusionHead
        {
            private const int FusionInputs = 4;
            private readonly double[] weights = new double[FusionInputs];
            private double bias;
            internal long Updates;
            internal double Predict(double[] x)
            {
                double s = bias;
                for (int i = 0; i < FusionInputs; i++) s += weights[i] * x[i];
                return 1.0 / (1.0 + Math.Exp(-ShadowClip(s, -25, 25)));
            }
            internal void Learn(double[] x, double y, double learningRate)
            {
                double e = Predict(x) - y;
                bias -= learningRate * e;
                for (int i = 0; i < FusionInputs; i++) weights[i] -= learningRate * (e * x[i] + .0001 * weights[i]);
                Updates++;
            }
            internal void Save(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    using (BinaryWriter w = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read)))
                    {
                        w.Write(0x4C4E4E46555331L); w.Write(bias); w.Write(FusionInputs);
                        for (int i = 0; i < FusionInputs; i++) w.Write(weights[i]);
                        w.Write(Updates);
                    }
                }
                catch { }
            }
            internal void Load(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                try
                {
                    using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
                    {
                        if (r.ReadInt64() != 0x4C4E4E46555331L) return;
                        double b = r.ReadDouble();
                        if (r.ReadInt32() != FusionInputs) return;
                        for (int i = 0; i < FusionInputs; i++) weights[i] = r.ReadDouble();
                        Updates = r.ReadInt64();
                        bias = b;
                        Console.WriteLine("fusion-head loaded updates=" + Updates.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch { }
            }
        }

        private sealed class PendingExample
        {
            internal readonly double[] Features;
            // Entry reference touch. With label latency > 0 these are (re)set
            // when the example ARMS at signal-time + latency, using the touch as
            // it is THEN — the price an order sent at the signal could actually
            // get after the round trip. Until armed, the example is not scored.
            internal double EntryBid;
            internal double EntryAsk;
            internal readonly long FrameIndex;
            internal readonly long EntryUtcTicks;
            internal long DeadlineUtcTicks;
            internal readonly long ArmUtcTicks;     // EntryUtcTicks + label latency
            internal readonly long HorizonTicks;    // window length after arming
            internal bool Armed;
            internal double[] HogFeatures;          // hog-fusion inputs at prediction time (null = no fresh evidence)
            internal double HogPUp = double.NaN;    // hog's own P(up) at prediction time
            internal double EnginePUp = double.NaN; // primary head P(up) at prediction time (for shadow scoring)
            internal readonly bool PredictedUp;
            internal readonly double PredictedProbability;
            internal readonly double ProfitTargetTicks;
            // v64r asymmetric labels: the target is how far the trade must go IN FAVOUR,
            // the stop how far it may go AGAINST, both net of spread and cost. A long
            // bracket and a short bracket are scored independently against the same pair,
            // and each is killed the moment its own stop is touched. With
            // StopTicks == ProfitTargetTicks this is exactly the old symmetric
            // "first barrier to +/-T wins" label.
            internal readonly double StopTicks;
            internal bool LongStopped;
            internal bool ShortStopped;
            internal readonly double EntrySpreadTicks;
            internal readonly double EstimatedCostTicks;
            internal readonly double[] MetaFeatures; // cross-market evidence snapshot for the fusion head

            internal PendingExample(double[] features, double entryBid, double entryAsk, long frameIndex, long entryUtcTicks,
                long deadlineUtcTicks, bool predictedUp, double predictedProbability, double profitTargetTicks,
                double stopTicks, double entrySpreadTicks, double estimatedCostTicks, double[] metaFeatures, long labelLatencyTicks)
            {
                MetaFeatures = metaFeatures;
                Features = features;
                EntryBid = entryBid;
                EntryAsk = entryAsk;
                FrameIndex = frameIndex;
                EntryUtcTicks = entryUtcTicks;
                HorizonTicks = Math.Max(0, deadlineUtcTicks - entryUtcTicks);
                ArmUtcTicks = entryUtcTicks + Math.Max(0, labelLatencyTicks);
                Armed = labelLatencyTicks <= 0;           // latency 0 = legacy: armed at signal
                DeadlineUtcTicks = Armed ? deadlineUtcTicks : ArmUtcTicks + HorizonTicks;
                PredictedUp = predictedUp;
                PredictedProbability = predictedProbability;
                ProfitTargetTicks = profitTargetTicks;
                StopTicks = stopTicks > 0 ? stopTicks : profitTargetTicks;   // 0 = symmetric, as before
                EntrySpreadTicks = entrySpreadTicks;
                EstimatedCostTicks = estimatedCostTicks;
            }
        }

        private sealed class ValidationResult
        {
            internal readonly bool LabelUp;
            internal readonly bool PredictedUp;

            internal ValidationResult(bool labelUp, bool predictedUp)
            {
                LabelUp = labelUp;
                PredictedUp = predictedUp;
            }
        }

        // Lightweight causal reversal/trend monitor inspired by the supplied
        // offline script. It starts a scenario every 20 ms (bounded to 64),
        // predicts at the configured trigger, and labels configured trend or
        // reversal distances. A timeout is reported separately as unresolved,
        // not as a trend, so accuracy is not inflated by quiet price action.
        // as REVERSAL.  It is deliberately a small online logistic head, not a
        // PyTorch/ONNX call on the live DOM path.
        private sealed class ReversalTracker
        {
            // Features are DIRECTION-RELATIVE so one linear head can learn
            // "book leaning against the move -> reversal" for both up and down
            // pushes (with raw bid/ask features a linear model cannot represent
            // the imbalance x direction interaction). Slots:
            //  0 imbalance*dir      1 d(imbalance)*dir     2 total depth
            //  3 d(total depth)     4 spread ticks         5 L1 size AHEAD of the move
            //  6 L1 size BEHIND     7 velocity (ticks/ms start->trigger)
            //  8 ahead/behind ratio
            private const int Features = 9;
            private const long StartIntervalTicks = 20 * TimeSpan.TicksPerMillisecond;
            // Capacity must cover the full timeout window of 20 ms starts, or
            // slower reversals are silently evicted before they can resolve.
            private readonly int maximumScenarios;
            internal long Evicted;
            private readonly int triggerTicks;
            private readonly int reversalDistanceTicks;
            private readonly int trendTicks;
            private readonly long horizonTicks;
            private readonly double probabilityThreshold;
            private sealed class Scenario
            {
                internal long StartTicks; internal double StartMid;
                internal bool Triggered; internal int Direction; internal double Probability;
                internal double[] Input; internal bool PredictedReversal;
            }
            private readonly List<Scenario> scenarios = new List<Scenario>();
            private readonly double[] mean = new double[Features];
            private readonly double[] m2 = new double[Features];
            private readonly double[] weights = new double[Features];
            private long standardizerCount, lastStartTicks, lastObservationTicks;
            private double bias, previousImbalance, previousTotal;
            internal long Resolved, Reversals, Trends, PredictedReversals, TruePositive, Correct;
            internal double LastProbability = 0.5;
            internal long Timeouts;

            internal ReversalTracker(Settings settings)
            {
                triggerTicks = settings.ReversalTriggerTicks;
                reversalDistanceTicks = settings.ReversalDistanceTicks;
                trendTicks = settings.ReversalTrendTicks;
                horizonTicks = settings.ReversalTimeoutMilliseconds * TimeSpan.TicksPerMillisecond;
                probabilityThreshold = settings.ReversalProbabilityThreshold;
                maximumScenarios = (int)Math.Min(4096, Math.Max(64, horizonTicks / StartIntervalTicks + 16));
            }

            internal void Observe(long utcTicks, double midpoint, Frame frame, double tickSize)
            {
                // This state machine needs timely prices, but does not need to
                // run on every burst frame. Ten milliseconds preserves the
                // intended 20 ms starts while keeping its work bounded.
                if (lastObservationTicks > 0 && utcTicks - lastObservationTicks < 10 * TimeSpan.TicksPerMillisecond) return;
                lastObservationTicks = utcTicks;
                double bidTotal = 0.0, askTotal = 0.0;
                for (int level = 0; level < DepthLevels; level++) { bidTotal += frame.BidVolume[level]; askTotal += frame.AskVolume[level]; }
                double total = Math.Max(1.0, bidTotal + askTotal);
                double imbalance = (bidTotal - askTotal) / total;
                double spreadTicks = Math.Max(0.0, (frame.Ask[0] - frame.Bid[0]) / tickSize);
                if (lastStartTicks == 0 || utcTicks - lastStartTicks >= StartIntervalTicks)
                {
                    if (scenarios.Count >= maximumScenarios) { scenarios.RemoveAt(0); Evicted++; }
                    scenarios.Add(new Scenario { StartTicks = utcTicks, StartMid = midpoint });
                    lastStartTicks = utcTicks;
                }
                for (int index = scenarios.Count - 1; index >= 0; index--)
                {
                    Scenario scenario = scenarios[index];
                    int movementTicks = (int)Math.Round((midpoint - scenario.StartMid) / tickSize, MidpointRounding.AwayFromZero);
                    int magnitude = Math.Abs(movementTicks);
                    if (!scenario.Triggered && magnitude >= triggerTicks)
                    {
                        scenario.Direction = movementTicks > 0 ? 1 : -1;
                        double dir = scenario.Direction;
                        double ahead = dir > 0 ? frame.AskVolume[0] : frame.BidVolume[0];   // size the move must eat next
                        double behind = dir > 0 ? frame.BidVolume[0] : frame.AskVolume[0];  // size supporting the move
                        double elapsedMs = Math.Max(1.0, (utcTicks - scenario.StartTicks) / (double)TimeSpan.TicksPerMillisecond);
                        scenario.Input = new double[] { imbalance * dir, (imbalance - previousImbalance) * dir, total,
                            total - previousTotal, spreadTicks, ahead, behind, magnitude / elapsedMs, ahead / (behind + 1.0) };
                        scenario.Probability = Predict(scenario.Input);
                        LastProbability = scenario.Probability;
                        scenario.PredictedReversal = scenario.Probability >= probabilityThreshold;
                        if (scenario.PredictedReversal) PredictedReversals++;
                        scenario.Triggered = true;
                    }
                    if (!scenario.Triggered)
                    {
                        if (utcTicks - scenario.StartTicks >= horizonTicks) { Timeouts++; scenarios.RemoveAt(index); }
                        continue;
                    }
                    bool reversal = movementTicks * scenario.Direction <= -reversalDistanceTicks;
                    bool trend = movementTicks * scenario.Direction >= trendTicks;
                    if (!reversal && !trend)
                    {
                        if (utcTicks - scenario.StartTicks >= horizonTicks) { Timeouts++; scenarios.RemoveAt(index); }
                        continue;
                    }
                    Resolve(scenario, reversal);
                    scenarios.RemoveAt(index);
                }
                previousImbalance = imbalance; previousTotal = total;
            }

            internal bool TryGetCurrentScenario(out int direction, out double probability)
            {
                direction = 0;
                probability = 0.5;
                for (int index = scenarios.Count - 1; index >= 0; index--)
                {
                    Scenario scenario = scenarios[index];
                    if (!scenario.Triggered) continue;
                    direction = scenario.Direction;
                    probability = scenario.Probability;
                    return true;
                }
                return false;
            }

            private double Predict(double[] input)
            {
                double score = bias;
                for (int i = 0; i < Features; i++) score += weights[i] * Normalize(input[i], i);
                return 1.0 / (1.0 + Math.Exp(-Clip(score, -30.0, 30.0)));
            }

            private void Resolve(Scenario scenario, bool reversal)
            {
                Resolved++; if (reversal) Reversals++; else Trends++;
                if (scenario.PredictedReversal == reversal) Correct++;
                if (scenario.PredictedReversal && reversal) TruePositive++;
                double target = reversal ? 1.0 : 0.0;
                double probability = Predict(scenario.Input);
                double error = probability - target;
                for (int i = 0; i < Features; i++)
                {
                    double normalized = Normalize(scenario.Input[i], i);
                    weights[i] -= 0.003 * (error * normalized + 0.0001 * weights[i]);
                }
                bias -= 0.003 * error;
                standardizerCount++;
                for (int i = 0; i < Features; i++)
                {
                    double delta = scenario.Input[i] - mean[i];
                    mean[i] += delta / standardizerCount;
                    m2[i] += delta * (scenario.Input[i] - mean[i]);
                }
            }

            private double Normalize(double value, int index)
            {
                double variance = standardizerCount > 1 ? m2[index] / (standardizerCount - 1) : 1.0;
                return Clip((value - mean[index]) / Math.Sqrt(Math.Max(variance, 1e-5)), -6.0, 6.0);
            }

            internal string Summary()
            {
                double accuracy = Resolved > 0 ? 100.0 * Correct / Resolved : 0.0;
                double reversalRecall = Reversals > 0 ? 100.0 * TruePositive / Reversals : 0.0;
                double baseline = Resolved > 0 ? 100.0 * Math.Max(Reversals, Trends) / Resolved : 0.0;
                double reversalRate = Resolved > 0 ? 100.0 * Reversals / Resolved : 0.0;
                return string.Format(CultureInfo.InvariantCulture,
                    "reversal-scenarios={0} reversal-resolved={1} reversal-count={2} trend-count={3} reversal-timeouts={4} reversal-predictions={5} reversal-accuracy={6:F2}% reversal-recall-proxy={7:F2}% current-reversal-probability={8:F3} reversal-baseline={9:F2}% reversal-rate={10:F2}% reversal-skill={11:+0.00;-0.00}% reversal-evicted={12} reversal-capacity={13}",
                    scenarios.Count, Resolved, Reversals, Trends, Timeouts, PredictedReversals, accuracy, reversalRecall, LastProbability, baseline, reversalRate, accuracy - baseline, Evicted, maximumScenarios);
            }
        }

        private sealed class ReversalFusionSnapshot
        {
            internal string State;
            internal string MainDirection;
            internal string LocalDirection;
            internal double MainProbability;
            internal double ReversalProbability;
            internal double HypotheticalProbability;
            internal long Resolved;

            internal string Summary()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "reversal-fusion mode=shadow state={0} main={1} local={2} main-probability={3:F3} reversal-probability={4:F3} hypothetical-probability={5:F3} reversal-resolved={6}",
                    State, MainDirection, LocalDirection, MainProbability, ReversalProbability, HypotheticalProbability, Resolved);
            }
        }

        private static ReversalFusionSnapshot BuildReversalShadowFusion(ReversalTracker tracker, double mainProbability)
        {
            ReversalFusionSnapshot result = new ReversalFusionSnapshot
            {
                MainProbability = mainProbability,
                ReversalProbability = 0.5,
                HypotheticalProbability = mainProbability,
                Resolved = tracker.Resolved,
                MainDirection = mainProbability >= 0.5 ? "UP" : "DOWN",
                LocalDirection = "NONE",
                State = tracker.Resolved < MinimumReversalResolvedForShadowFusion ? "WARMUP" : "NO_ACTIVE_SCENARIO"
            };
            int localDirection;
            double reversalProbability;
            if (!tracker.TryGetCurrentScenario(out localDirection, out reversalProbability)) return result;
            result.LocalDirection = localDirection > 0 ? "UP" : "DOWN";
            result.ReversalProbability = reversalProbability;
            if (tracker.Resolved < MinimumReversalResolvedForShadowFusion) return result;

            int mainDirection = mainProbability >= 0.5 ? 1 : -1;
            int reversalDirection = -localDirection;
            double adjustment = Math.Min(0.10, Math.Abs(reversalProbability - 0.5) * 0.40);
            bool reversalAlignsMain = mainDirection == reversalDirection;
            if (reversalProbability >= 0.60)
            {
                result.State = reversalAlignsMain ? "REVERSAL_ALIGNS_MAIN" : "REVERSAL_OPPOSES_MAIN";
                result.HypotheticalProbability = Clip(mainProbability + (reversalAlignsMain ? mainDirection : -mainDirection) * adjustment, 0.01, 0.99);
            }
            else if (reversalProbability <= 0.40)
            {
                bool trendAlignsMain = mainDirection == localDirection;
                result.State = trendAlignsMain ? "TREND_ALIGNS_MAIN" : "TREND_OPPOSES_MAIN";
                result.HypotheticalProbability = Clip(mainProbability + (trendAlignsMain ? mainDirection : -mainDirection) * adjustment, 0.01, 0.99);
            }
            else result.State = "REVERSAL_NEUTRAL";
            return result;
        }

        // Internal simulation only. No broker, account, or NinjaTrader order
        // API is used by this data-only paper-trade record.
        private sealed class PaperTrade
        {
            internal readonly bool IsLong;
            internal readonly double EntryPrice;
            internal readonly long EntryFrame;
            internal readonly long EntryUtcTicks;
            internal readonly long DeadlineUtcTicks;
            internal readonly double TargetTicks;
            // v64k mirror bracket (0 = not used): stop / target / trail in ticks from the fill,
            // like the strategy's DefaultStopLoss / ProfitTarget / Trail. Trail applies only
            // when StopTicks is 0 (a fixed stop and a trail cannot share one position).
            internal double StopTicks, PtTicks, TrailTicks, BestTicks;

            internal PaperTrade(bool isLong, double entryPrice, long entryFrame, long entryUtcTicks,
                long deadlineUtcTicks, double targetTicks)
            {
                IsLong = isLong;
                EntryPrice = entryPrice;
                EntryFrame = entryFrame;
                EntryUtcTicks = entryUtcTicks;
                DeadlineUtcTicks = deadlineUtcTicks;
                TargetTicks = targetTicks;
            }
        }

        private sealed class PaperSimulationController
        {
            internal volatile bool Enabled;
            // External command from the dashboard's AI Trader (paper mirror): 0 none, 1 open long, 2 open short, 3 flatten.
            internal volatile int PendingCommand;
            internal PaperSimulationController(bool enabled) { Enabled = enabled; }
        }

        private sealed class ModelSnapshotController
        {
            internal volatile bool Requested;
        }

        private sealed class RollingLatency
        {
            private const int Capacity = 4096;
            private readonly List<double> values = new List<double>();

            internal int Count { get { return values.Count; } }

            internal void AddTicks(long startUtcTicks, long endUtcTicks)
            {
                double milliseconds = (endUtcTicks - startUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
                // Reject impossible clock/provider values rather than letting a
                // bad timestamp make the latency panel meaningless.
                if (milliseconds < 0.0 || milliseconds > 60000.0) return;
                if (values.Count >= Capacity) values.RemoveAt(0);
                values.Add(milliseconds);
            }

            internal string Percentiles()
            {
                if (values.Count == 0) return "waiting";
                double[] sorted = values.ToArray();
                Array.Sort(sorted);
                return string.Format(CultureInfo.InvariantCulture, "{0:F3}/{1:F3}/{2:F3}",
                    Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));
            }

            private static double Percentile(double[] sorted, double fraction)
            {
                int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
                return sorted[Math.Max(0, Math.Min(sorted.Length - 1, index))];
            }
        }

        private sealed class OnlineStandardizer
        {
            private readonly double[] mean = new double[RawFeatures];
            private readonly double[] m2 = new double[RawFeatures];
            private long count;

            internal bool IsReady { get { return count >= 100; } }

            private readonly double[] transformBuffer = new double[RawFeatures];
            internal double[] Transform(double[] value)
            {
                // Reused buffer: consumed within the frame, never retained.
                double[] result = transformBuffer;
                for (int i = 0; i < RawFeatures; i++)
                {
                    double variance = count > 1 ? m2[i] / (count - 1) : 1.0;
                    double standardDeviation = Math.Sqrt(Math.Max(variance, 1e-5));
                    result[i] = Clip((value[i] - mean[i]) / standardDeviation, -6.0, 6.0);
                }
                return result;
            }

            // Distance from the live session's observed feature distribution.
            // This is an out-of-distribution warning, not a trading gate and
            // never changes the primary model's weights or prediction.
            internal double NoveltyScore(double[] value, out int extremeFeatures)
            {
                extremeFeatures = 0;
                if (!IsReady) return 0.0;
                double sumSquared = 0.0;
                for (int i = 0; i < RawFeatures; i++)
                {
                    double variance = count > 1 ? m2[i] / (count - 1) : 1.0;
                    double standardDeviation = Math.Sqrt(Math.Max(variance, 1e-5));
                    double z = (value[i] - mean[i]) / standardDeviation;
                    if (Math.Abs(z) >= 3.0) extremeFeatures++;
                    z = Clip(z, -12.0, 12.0);
                    sumSquared += z * z;
                }
                return Math.Sqrt(sumSquared / RawFeatures);
            }

            internal void Observe(double[] value)
            {
                count++;
                for (int i = 0; i < RawFeatures; i++)
                {
                    double delta = value[i] - mean[i];
                    mean[i] += delta / count;
                    m2[i] += delta * (value[i] - mean[i]);
                }
            }

            internal void Save(BinaryWriter writer)
            {
                writer.Write(count);
                for (int i = 0; i < RawFeatures; i++) writer.Write(mean[i]);
                for (int i = 0; i < RawFeatures; i++) writer.Write(m2[i]);
            }

            internal void Load(BinaryReader reader)
            {
                count = reader.ReadInt64();
                if (count < 0) throw new InvalidDataException("Invalid standardizer count.");
                for (int i = 0; i < RawFeatures; i++) mean[i] = reader.ReadDouble();
                for (int i = 0; i < RawFeatures; i++) m2[i] = reader.ReadDouble();
            }
        }

        private sealed class NestedTemporalModel
        {
            private readonly double[] fast = new double[RawFeatures];
            private readonly double[] slow = new double[RawFeatures];
            private readonly double[] weights = new double[ModelFeatures];
            private double bias;

            internal void ResetTemporalState()
            {
                Array.Clear(fast, 0, fast.Length);
                Array.Clear(slow, 0, slow.Length);
            }

            private readonly double[] combinedBuffer = new double[ModelFeatures];
            internal double[] UpdateState(double[] normalized)
            {
                // Reused buffer. Anything that must OUTLIVE the frame (pending
                // examples) clones it at creation.
                double[] combined = combinedBuffer;
                for (int i = 0; i < RawFeatures; i++)
                {
                    fast[i] = 0.70 * fast[i] + 0.30 * normalized[i];
                    slow[i] = 0.97 * slow[i] + 0.03 * fast[i];
                    combined[i] = normalized[i];
                    combined[RawFeatures + i] = fast[i];
                    combined[2 * RawFeatures + i] = slow[i];
                }
                return combined;
            }

            internal double Predict(double[] features)
            {
                double score = bias;
                for (int i = 0; i < ModelFeatures; i++)
                    score += weights[i] * features[i];
                return Sigmoid(score);
            }

            // If requested, returns the full audit trail for this one gradient
            // update: all 222 weights plus the bias. It does not alter learning.
            internal WeightChange[] Learn(double[] features, double target, double prediction, double learningRate, bool captureChanges)
            {
                double error = prediction - target;
                WeightChange[] changes = captureChanges ? new WeightChange[ModelFeatures + 1] : null;
                for (int i = 0; i < ModelFeatures; i++)
                {
                    double before = weights[i];
                    double dataGradient = error * features[i];
                    double regularizationGradient = 0.0001 * before;
                    double delta = -learningRate * (dataGradient + regularizationGradient);
                    weights[i] = before + delta;
                    if (changes != null)
                    {
                        changes[i] = new WeightChange
                        {
                            Index = i,
                            InputValue = features[i],
                            WeightBefore = before,
                            DataGradient = dataGradient,
                            RegularizationGradient = regularizationGradient,
                            WeightDelta = delta,
                            WeightAfter = weights[i]
                        };
                    }
                }
                double biasBefore = bias;
                double biasDelta = -learningRate * error;
                bias = biasBefore + biasDelta;
                if (changes != null)
                {
                    changes[ModelFeatures] = new WeightChange
                    {
                        Index = -1,
                        InputValue = 1.0,
                        WeightBefore = biasBefore,
                        DataGradient = error,
                        RegularizationGradient = 0.0,
                        WeightDelta = biasDelta,
                        WeightAfter = bias
                    };
                }
                return changes;
            }

            internal double WeightL2()
            {
                double total = bias * bias;
                for (int i = 0; i < ModelFeatures; i++) total += weights[i] * weights[i];
                return Math.Sqrt(total);
            }

            internal void Save(BinaryWriter writer)
            {
                writer.Write(bias);
                for (int i = 0; i < ModelFeatures; i++) writer.Write(weights[i]);
            }

            internal void Load(BinaryReader reader)
            {
                bias = reader.ReadDouble();
                for (int i = 0; i < ModelFeatures; i++) weights[i] = reader.ReadDouble();
                ResetTemporalState();
            }

            internal void ReplaceHead(double replacementBias, double[] replacementWeights)
            {
                if (replacementWeights == null || replacementWeights.Length != ModelFeatures)
                    throw new ArgumentException("Candidate head has the wrong feature count.");
                if (double.IsNaN(replacementBias) || double.IsInfinity(replacementBias))
                    throw new ArgumentException("Candidate bias is not finite.");
                for (int i = 0; i < ModelFeatures; i++)
                    if (double.IsNaN(replacementWeights[i]) || double.IsInfinity(replacementWeights[i]))
                        throw new ArgumentException("Candidate weight is not finite.");
                bias = replacementBias;
                Array.Copy(replacementWeights, weights, ModelFeatures);
            }
        }

        // ── Hot-path latency hygiene ─────────────────────────────────────
        // stdout is a synchronous pipe write; on the frame thread that is a
        // tail-latency hazard. Every Console.WriteLine in this file now goes
        // into a queue drained by a background thread, so no call site changes
        // and nothing is dropped (flushed on exit / unhandled exception).
        private sealed class AsyncConsoleWriter : System.IO.TextWriter
        {
            private readonly System.IO.TextWriter inner;
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> queue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            private readonly AutoResetEvent signal = new AutoResetEvent(false);
            private readonly Thread thread;
            private volatile bool stopping;
            internal AsyncConsoleWriter(System.IO.TextWriter inner)
            {
                this.inner = inner;
                thread = new Thread(Pump) { IsBackground = true, Name = "LnnStdoutPump", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
            public override Encoding Encoding { get { return inner.Encoding; } }
            public override void Write(char value) { queue.Enqueue(value.ToString()); signal.Set(); }
            public override void Write(string value) { if (value != null) { queue.Enqueue(value); signal.Set(); } }
            public override void WriteLine(string value) { queue.Enqueue((value ?? string.Empty) + "\n"); signal.Set(); }
            public override void WriteLine() { queue.Enqueue("\n"); signal.Set(); }
            public override void Flush() { Drain(); try { inner.Flush(); } catch { } }
            private void Drain()
            {
                string item; StringBuilder sb = null;
                while (queue.TryDequeue(out item)) { if (sb == null) sb = new StringBuilder(); sb.Append(item); }
                if (sb != null) { try { inner.Write(sb.ToString()); inner.Flush(); } catch { } }
            }
            private void Pump()
            {
                while (!stopping) { signal.WaitOne(50); Drain(); }
                Drain();
            }
            internal void Shutdown() { stopping = true; signal.Set(); try { thread.Join(500); } catch { } Drain(); }
        }
        private static AsyncConsoleWriter asyncOut;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);

        // ── v64k clock hygiene ────────────────────────────────────────────
        // .NET Framework's DateTime.UtcNow is quantised to the system clock tick
        // (0.5-15.6 ms). Windows 11 also IGNORES a background/window-less
        // process's timeBeginPeriod(1) unless it opts out of power throttling, so
        // a hidden console engine can be stuck at 15.6 ms: every wait and every
        // timestamp rounds to that, and the latency panel's p99 shows it.
        [DllImport("kernel32.dll")] private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState state, int size);
        [StructLayout(LayoutKind.Sequential)] private struct ProcessPowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }
        private const int ProcessPowerThrottlingClass = 4;
        private const uint PowerThrottlingIgnoreTimerResolution = 0x4;
        private static bool preciseClock;
        private static string clockReport = "unknown";

        // Wall-clock UTC ticks with 100 ns precision where the OS offers it.
        internal static long UtcNowTicks()
        {
            if (preciseClock) { long ft; GetSystemTimePreciseAsFileTime(out ft); return ft + 504911232000000000L; } // FILETIME epoch (1601) -> DateTime ticks (0001)
            return DateTime.UtcNow.Ticks;
        }

        // ── Host scheduling-jitter monitor (v64k) ─────────────────────────
        // Answers "is this machine actually giving my thread the CPU?" without
        // guessing. A background thread asks for a 1 ms sleep over and over and
        // records how long it really took. On a dedicated core that lands at
        // 1-2 ms with a p99 of a few ms. On a shared/burstable VPS vCPU the
        // hypervisor deschedules the whole VM and p99 blows out to tens or
        // hundreds of ms - which is exactly the shape of an unexplained
        // 100 ms order latency, because every thread in NinjaTrader and in this
        // engine is stalled for the same window.
        private sealed class JitterMonitor
        {
            private readonly double[] window = new double[2048];
            private int count, index;
            private readonly object gate = new object();
            private double worst;
            internal void Start()
            {
                Thread t = new Thread(delegate()
                {
                    System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                    while (true)
                    {
                        sw.Restart();
                        Thread.Sleep(1);
                        double ms = sw.Elapsed.TotalMilliseconds;
                        lock (gate)
                        {
                            window[index] = ms; index = (index + 1) & (window.Length - 1);
                            if (count < window.Length) count++;
                            if (ms > worst) worst = ms;
                        }
                    }
                });
                t.IsBackground = true; t.Priority = ThreadPriority.Normal; t.Name = "LNN host-jitter";
                t.Start();
            }
            // p50 / p99 / worst-ever of a requested 1 ms sleep.
            internal string Line()
            {
                double[] copy; int n; double mx;
                lock (gate)
                {
                    n = count; if (n < 50) return null;
                    copy = new double[n]; Array.Copy(window, copy, n); mx = worst;
                }
                Array.Sort(copy);
                double p50 = copy[n / 2], p99 = copy[Math.Min(n - 1, (int)(0.99 * n))];
                string verdict = p99 >= 25 ? "SEVERE - the OS/hypervisor stalls this process for tens of ms; on a VPS this is shared-vCPU steal and it caps your order latency"
                    : p99 >= 8 ? "POOR - noticeable scheduling stalls (shared vCPU, power saving, or another process competing)"
                    : p99 >= 3 ? "OK"
                    : "GOOD - the CPU is genuinely yours";
                return string.Format(CultureInfo.InvariantCulture,
                    "host-jitter sleep1-ms p50={0:F2} p99={1:F2} worst={2:F1} samples={3} verdict={4}", p50, p99, mx, n, verdict);
            }
        }
        private static readonly JitterMonitor jitter = new JitterMonitor();

        private static void ApplyClockHygiene()
        {
            // 1. Opt out of Windows 11 timer-resolution throttling for this (window-less) process.
            string throttle;
            try
            {
                ProcessPowerThrottlingState st = new ProcessPowerThrottlingState { Version = 1, ControlMask = PowerThrottlingIgnoreTimerResolution, StateMask = 0 };
                throttle = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref st, Marshal.SizeOf(typeof(ProcessPowerThrottlingState))) ? "opted-out" : "not-applied(err " + Marshal.GetLastWin32Error() + ")";
            }
            catch { throttle = "unavailable"; }
            // 2. Precise clock if the OS has it (Windows 8+).
            try { long probe; GetSystemTimePreciseAsFileTime(out probe); preciseClock = probe > 0; } catch { preciseClock = false; }
            // 3. Measure what the process actually gets: DateTime.UtcNow tick and a 1 ms sleep.
            double coarseTickMs = -1, sleepMs = -1, preciseTickMs = -1;
            try
            {
                long t0 = DateTime.UtcNow.Ticks, t1; while ((t1 = DateTime.UtcNow.Ticks) == t0) { } long t2; while ((t2 = DateTime.UtcNow.Ticks) == t1) { }
                coarseTickMs = (t2 - t1) / 10000.0;
                if (preciseClock) { long p0 = UtcNowTicks(), p1; while ((p1 = UtcNowTicks()) == p0) { } preciseTickMs = (p1 - p0) / 10000.0; }
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew(); Thread.Sleep(1); sleepMs = sw.Elapsed.TotalMilliseconds;
            }
            catch { }
            clockReport = string.Format(CultureInfo.InvariantCulture, "timer-throttling={0} precise-clock={1} datetime-tick-ms={2:F3} precise-tick-ms={3:F4} sleep1-actual-ms={4:F2}{5}",
                throttle, preciseClock ? "on" : "off (DateTime.UtcNow)", coarseTickMs, preciseTickMs, sleepMs,
                sleepMs > 4 ? "  <-- TIMER RESOLUTION IS COARSE: waits and DateTime stamps round to this; p99 will show it" : "");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Hog evidence record (single seqlock record; hog writes, engine reads)
        //   0 seq(int64)  8 magic(int64)  16 utcTicks  24 pUp  32 metaScore
        //   40 ensembleScore  48 agreement  56 passMs  64 hidden(int32)
        // ══════════════════════════════════════════════════════════════════
        private sealed class HogEvidence
        {
            internal long UtcTicks; internal double PUp, Meta, Ensemble, Agreement, PassMs; internal int Hidden;
        }
        private sealed class HogEvidenceReader : IDisposable
        {
            private const long Magic = 0x4C4E4E484F474556L; // LNNHOGEV
            private readonly string mapName; private System.IO.MemoryMappedFiles.MemoryMappedFile map; private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view;
            private long lastOpenAttempt; internal bool Connected { get { return view != null; } }
            internal HogEvidenceReader(string name) { mapName = name; }
            private bool Open(long now)
            {
                if (view != null) return true;
                if (now - lastOpenAttempt < 2 * TimeSpan.TicksPerSecond) return false;
                lastOpenAttempt = now;
                try
                {
                    map = mapName.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                        ? System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(mapName.Substring(5), FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read)
                        : System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(mapName, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                    view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    if (view.ReadInt64(8) != Magic) throw new InvalidOperationException("not a hog evidence map");
                    Console.WriteLine("hog-evidence connected: " + mapName);
                    return true;
                }
                catch { Close(); return false; }
            }
            private void Close() { try { if (view != null) view.Dispose(); } catch { } try { if (map != null) map.Dispose(); } catch { } view = null; map = null; }
            internal bool TryRead(long now, HogEvidence ev)
            {
                if (!Open(now)) return false;
                try
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        long s1 = view.ReadInt64(0); if ((s1 & 1) != 0) continue;
                        ev.UtcTicks = view.ReadInt64(16); ev.PUp = view.ReadDouble(24); ev.Meta = view.ReadDouble(32);
                        ev.Ensemble = view.ReadDouble(40); ev.Agreement = view.ReadDouble(48); ev.PassMs = view.ReadDouble(56); ev.Hidden = view.ReadInt32(64);
                        long s2 = view.ReadInt64(0);
                        if (s1 == s2 && s1 > 0) return true;
                    }
                    return false;
                }
                catch { Close(); return false; }
            }
            public void Dispose() { Close(); }
        }

        // Separate logistic head over [engine logit, hog evidence]. Starts as
        // the engine (w_engine = 1, everything else 0) so it can only move
        // away from the primary as labels justify it. Keeps shadow accuracy
        // for fused / engine / hog on the same labels so you can see whether
        // the hog is complementary before trusting it live.
        private sealed class HogFusion
        {
            internal const int Inputs = 6;
            private readonly double[] w = new double[Inputs];
            private double b;
            internal long Updates, Labels, FusedCorrect, EngineCorrect, HogCorrect, AgreeCount, AgreeCorrect, DisagreeCorrect;
            internal HogFusion() { w[0] = 1.0; }
            internal static double[] Features(double pEngine, HogEvidence ev)
            {
                double pe = Math.Min(0.999, Math.Max(0.001, pEngine));
                double logit = Math.Log(pe / (1 - pe)); if (logit > 6) logit = 6; if (logit < -6) logit = -6;
                double hog = (ev.PUp - 0.5) * 2.0;
                double agree = Math.Sign(pEngine - 0.5) == Math.Sign(ev.PUp - 0.5) ? Math.Abs(hog) : -Math.Abs(hog);
                return new double[] { logit, hog, ev.Meta, ev.Ensemble, ev.Agreement, agree };
            }
            internal double Predict(double[] x)
            {
                double z = b; for (int i = 0; i < Inputs; i++) z += w[i] * x[i];
                if (z > 25) z = 25; if (z < -25) z = -25;
                return 1.0 / (1.0 + Math.Exp(-z));
            }
            internal void Learn(double[] x, double target, double pEngine, double pHog, double lr)
            {
                double p = Predict(x), e = p - target;
                b -= lr * e;
                for (int i = 0; i < Inputs; i++) w[i] -= lr * (e * x[i] + 0.0001 * w[i]);
                Updates++; Labels++;
                bool up = target >= 0.5;
                bool fusedUp = p >= 0.5, engUp = pEngine >= 0.5, hogUp = pHog >= 0.5;
                if (fusedUp == up) FusedCorrect++; if (engUp == up) EngineCorrect++; if (hogUp == up) HogCorrect++;
                if (engUp == hogUp) { AgreeCount++; if (engUp == up) AgreeCorrect++; } else if (engUp == up) DisagreeCorrect++;
            }
            internal double WEngine { get { return w[0]; } }
            internal double WHog { get { return w[1]; } }
            internal double WAgree { get { return w[5]; } }
            internal void Save(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    using (BinaryWriter wr = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read)))
                    {
                        wr.Write(0x4C4E4E484F474631L); wr.Write(Inputs); wr.Write(b); for (int i = 0; i < Inputs; i++) wr.Write(w[i]);
                        wr.Write(Updates); wr.Write(Labels); wr.Write(FusedCorrect); wr.Write(EngineCorrect); wr.Write(HogCorrect); wr.Write(AgreeCount); wr.Write(AgreeCorrect); wr.Write(DisagreeCorrect);
                    }
                }
                catch { }
            }
            internal void Load(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                try
                {
                    using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
                    {
                        if (r.ReadInt64() != 0x4C4E4E484F474631L || r.ReadInt32() != Inputs) return;
                        b = r.ReadDouble(); for (int i = 0; i < Inputs; i++) w[i] = r.ReadDouble();
                        Updates = r.ReadInt64(); Labels = r.ReadInt64(); FusedCorrect = r.ReadInt64(); EngineCorrect = r.ReadInt64(); HogCorrect = r.ReadInt64(); AgreeCount = r.ReadInt64(); AgreeCorrect = r.ReadInt64(); DisagreeCorrect = r.ReadInt64();
                    }
                }
                catch { }
            }
            internal string Line(string mode, string evidenceState, double ageMs, double pHog, double pEngine, double pFused, bool gate, long gateBlocked)
            {
                double fa = Labels > 0 ? 100.0 * FusedCorrect / Labels : 0, ea = Labels > 0 ? 100.0 * EngineCorrect / Labels : 0, ha = Labels > 0 ? 100.0 * HogCorrect / Labels : 0;
                double agreeRate = Labels > 0 ? 100.0 * AgreeCount / Labels : 0;
                double accAgree = AgreeCount > 0 ? 100.0 * AgreeCorrect / AgreeCount : 0;
                long disagree = Labels - AgreeCount; double accDis = disagree > 0 ? 100.0 * DisagreeCorrect / disagree : 0;
                return string.Format(CultureInfo.InvariantCulture,
                    "hog-fusion mode={0} evidence={1} age-ms={2:F0} p-hog={3:F3} p-engine={4:F3} p-fused={5:F3} w-engine={6:F3} w-hog={7:F3} w-agree={8:F3} labels={9} fused-acc={10:F2}% engine-acc={11:F2}% hog-acc={12:F2}% agree-rate={13:F1}% acc-when-agree={14:F2}% acc-when-disagree={15:F2}% gate={16} gate-blocked={17}",
                    mode, evidenceState, ageMs, pHog, pEngine, pFused, w[0], w[1], w[5], Labels, fa, ea, ha, agreeRate, accAgree, accDis, gate ? "on" : "off", gateBlocked);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  DOM event recorder: 40-byte records
        //   0 eventTime(int64) 8 callbackTime(int64) 16 price(double) 24 volume(int64)
        //   32 side(byte) 33 op(byte) 34 position(int16) 36 pad(int32)
        //  Queued on the frame thread, written by a background thread.
        // ══════════════════════════════════════════════════════════════════
        internal struct DomEventRecord { public long EventTime, CallbackTime; public double Price; public long Volume; public byte Side, Op; public short Position; }
        private sealed class DomEventRecorder : IDisposable
        {
            internal const int RecordBytes = 40;
            private readonly System.Collections.Concurrent.ConcurrentQueue<DomEventRecord> queue = new System.Collections.Concurrent.ConcurrentQueue<DomEventRecord>();
            private readonly AutoResetEvent signal = new AutoResetEvent(false);
            private readonly FileStream stream; private readonly Thread thread; private volatile bool stopping;
            internal long Written;
            internal DomEventRecorder(string path)
            {
                string folder = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                thread = new Thread(Pump) { IsBackground = true, Name = "LnnEventRecorder", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
            internal void Add(long eventTime, long callbackTime, double price, long volume, byte side, byte op, short position)
            {
                queue.Enqueue(new DomEventRecord { EventTime = eventTime, CallbackTime = callbackTime, Price = price, Volume = volume, Side = side, Op = op, Position = position });
                signal.Set();
            }
            private void Pump()
            {
                byte[] buf = new byte[RecordBytes];
                while (!stopping) { signal.WaitOne(100); Drain(buf); }
                Drain(buf);
                try { stream.Flush(); stream.Dispose(); } catch { }
            }
            private void Drain(byte[] buf)
            {
                DomEventRecord r;
                while (queue.TryDequeue(out r))
                {
                    Array.Copy(BitConverter.GetBytes(r.EventTime), 0, buf, 0, 8);
                    Array.Copy(BitConverter.GetBytes(r.CallbackTime), 0, buf, 8, 8);
                    Array.Copy(BitConverter.GetBytes(r.Price), 0, buf, 16, 8);
                    Array.Copy(BitConverter.GetBytes(r.Volume), 0, buf, 24, 8);
                    buf[32] = r.Side; buf[33] = r.Op;
                    Array.Copy(BitConverter.GetBytes(r.Position), 0, buf, 34, 2);
                    buf[36] = buf[37] = buf[38] = buf[39] = 0;
                    try { stream.Write(buf, 0, RecordBytes); Written++; } catch { }
                }
                try { stream.Flush(); } catch { }
            }
            public void Dispose() { stopping = true; signal.Set(); try { thread.Join(2000); } catch { } }
        }

        private static void ApplyLatencyHygiene()
        {
            try { asyncOut = new AsyncConsoleWriter(Console.Out); Console.SetOut(asyncOut); } catch { asyncOut = null; }
            try { AppDomain.CurrentDomain.ProcessExit += delegate { if (asyncOut != null) asyncOut.Shutdown(); }; } catch { }
            try { AppDomain.CurrentDomain.UnhandledException += delegate { if (asyncOut != null) asyncOut.Flush(); }; } catch { }
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }
            try { TimeBeginPeriod(1); } catch { }   // 1 ms scheduler granularity for any timed wait (Windows; no-op elsewhere)
            ApplyClockHygiene();
            jitter.Start();
        }

        private static void Main(string[] args)
        {
            ApplyLatencyHygiene();
            Settings settings;
            try
            {
                settings = ParseArguments(args);
            }
            catch (ArgumentException error)
            {
                Console.Error.WriteLine(error.Message);
                PrintUsage();
                Environment.ExitCode = 2;
                return;
            }
            if (settings == null)
                return;

            if (settings.AffinityCore >= 0)
            {
                try
                {
                    int n = Environment.ProcessorCount;
                    if (settings.AffinityCore < n)
                    {
                        System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)(1L << settings.AffinityCore);
                        Console.WriteLine("affinity: engine pinned to core " + settings.AffinityCore + " of " + n);
                    }
                    else Console.WriteLine("affinity: core " + settings.AffinityCore + " out of range (" + n + " cores) - not pinned");
                }
                catch (Exception ex) { Console.WriteLine("affinity: failed (" + ex.Message + ")"); }
            }
            Console.WriteLine("latency-clock: " + clockReport);
            Console.WriteLine("latency-hygiene: async-stdout=" + (asyncOut != null ? "on" : "off") + " gc-latency-mode=" + GCSettings.LatencyMode + " timer-1ms=requested frame-thread-priority=Highest zero-alloc-hot-path=on");
            Console.WriteLine("LNN live learner: read-only, no broker or order API is present.");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "pipe={0} tick={1} horizon={2} events lr={3} features={4} (42 static + 32 order-flow) target-ticks={5:F2} paper-long>={6:F2} paper-short<={7:F2}", settings.PipeName, settings.TickSize,
                settings.HorizonEvents, settings.LearningRate, RawFeatures, settings.MinimumProfitTargetTicks, settings.PaperLongThreshold, settings.PaperShortThreshold));
            Console.WriteLine(settings.LabelStopTicks > 0 && Math.Abs(settings.LabelStopTicks - settings.MinimumProfitTargetTicks) > 1e-9
                ? string.Format(CultureInfo.InvariantCulture,
                    "label-geometry=ASYMMETRIC target={0:F2}t stop={1:F2}t — P(up) answers \"does +{0:F2} arrive before -{1:F2}, net of spread and cost\". Reward:risk {2:F2}:1, so a long needs a {3:F1}% hit rate to break even before fees.",
                    settings.MinimumProfitTargetTicks, settings.LabelStopTicks,
                    settings.MinimumProfitTargetTicks / settings.LabelStopTicks,
                    100.0 * settings.LabelStopTicks / (settings.MinimumProfitTargetTicks + settings.LabelStopTicks))
                : string.Format(CultureInfo.InvariantCulture,
                    "label-geometry=SYMMETRIC target={0:F2}t — P(up) answers \"which side reaches {0:F2} ticks first\".",
                    settings.MinimumProfitTargetTicks));
            Run(settings);
        }

        private static Settings ParseArguments(string[] args)
        {
            Settings settings = new Settings();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument == "--help" || argument == "-h")
                {
                    PrintUsage();
                    return null;
                }
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Missing value for " + argument);
                string value = args[++i];
                if (argument == "--pipe") settings.PipeName = value;
                else if (argument == "--mmf-map") settings.MmfMap = value;
                else if (argument == "--es-map") settings.EsMap = value;
                else if (argument == "--nq-map") settings.NqMap = value;
                else if (argument == "--vx-map") settings.VxMap = value;
                else if (argument == "--spy-map") settings.SpyMap = value;
                else if (argument == "--emit-per-event") settings.EmitPerEvent = ParseBoolean(value, argument);
                else if (argument == "--coalesce-above") settings.CoalesceAboveEvents = Math.Max(0, int.Parse(value, CultureInfo.InvariantCulture));
                else if (argument == "--emit-orders") settings.EmitOrders = ParseBoolean(value, argument);
                else if (argument == "--order-mode") settings.OrderMode = (value == "join") ? "join" : "market";
                else if (argument == "--order-cmd-map") settings.OrderCmdMap = value;
                else if (argument == "--order-qty") settings.OrderQty = Math.Max(1, ParseInt(value, argument));
                else if (argument == "--order-trade-gap-s") settings.OrderTradeGapSeconds = Math.Max(0, ParseDouble(value, argument));
                else if (argument == "--order-hold-s") settings.OrderHoldSeconds = Math.Max(0, ParseDouble(value, argument));
                else if (argument == "--paper-no-timeout") settings.PaperNoTimeout = ParseBoolean(value, argument);
                else if (argument == "--paper-external") settings.PaperExternal = ParseBoolean(value, argument);
                else if (argument == "--paper-stop-ticks") settings.PaperStopTicks = Math.Max(0, ParseDouble(value, argument));
                else if (argument == "--paper-target-ticks") settings.PaperTargetTicks = Math.Max(0, ParseDouble(value, argument));
                else if (argument == "--paper-trail-ticks") settings.PaperTrailTicks = Math.Max(0, ParseDouble(value, argument));
                else if (argument == "--paper-max-open") settings.PaperMaxOpen = Math.Max(1, ParseInt(value, argument));
                else if (argument == "--paper-entry-latency-ms") settings.PaperEntryLatencyMs = Math.Max(0, ParseInt(value, argument));
                else if (argument == "--label-latency-ms") settings.LabelLatencyMs = Math.Max(0, Math.Min(5000, ParseInt(value, argument)));
                else if (argument == "--affinity-core") settings.AffinityCore = ParseInt(value, argument);
                else if (argument == "--hog-evidence-map") settings.HogEvidenceMap = value.Trim();
                else if (argument == "--hog-evidence-max-age-ms") settings.HogEvidenceMaxAgeMs = Math.Max(50, ParseInt(value, argument));
                else if (argument == "--hog-fusion-mode") settings.HogFusionMode = value.Trim().ToLowerInvariant() == "live" ? "live" : "shadow";
                else if (argument == "--hog-agree-gate") settings.HogAgreeGate = ParseBoolean(value, argument);
                else if (argument == "--book-gap-staleness-ms") settings.BookGapStalenessMs = int.Parse(value, CultureInfo.InvariantCulture);
                else if (argument == "--dom-shift") settings.DomShift = ParseBoolean(value, argument);
                else if (argument == "--dom-warmup") settings.DomWarmup = ParseBoolean(value, argument);
                else if (argument == "--signal-mode")
                {
                    string mode = value.ToLowerInvariant();
                    if (mode != "thresholds" && mode != "fusion") throw new ArgumentException("--signal-mode must be thresholds or fusion");
                    settings.SignalMode = mode;
                }
                else if (argument == "--tick-size") settings.TickSize = ParseDouble(value, argument);
                else if (argument == "--horizon-events") settings.HorizonEvents = ParseInt(value, argument);
                else if (argument == "--learning-rate") settings.LearningRate = ParseDouble(value, argument);
                else if (argument == "--validation-stride") { settings.ValidationStride = int.Parse(value, CultureInfo.InvariantCulture); if (settings.ValidationStride < 2) settings.ValidationStride = 2; }
                else if (argument == "--fusion-confirm-margin") settings.FusionConfirmMargin = ParseDouble(value, argument);
                else if (argument == "--resting-depth-ms") settings.RestingDepthMs = ParseInt(value, argument);
                else if (argument == "--prob-smoothing") settings.ProbabilitySmoothingAlpha = ParseDouble(value, argument);
                else if (argument == "--regime-min-range-ticks") settings.RegimeGateMinRangeTicks = ParseDouble(value, argument);
                else if (argument == "--regime-max-novelty") settings.RegimeGateMaxNovelty = ParseDouble(value, argument);
                else if (argument == "--cost-ticks") settings.CostTicks = ParseDouble(value, argument);
                else if (argument == "--minimum-profit-target-ticks" || argument == "--target-ticks") settings.MinimumProfitTargetTicks = ParseDouble(value, argument); // fixed label/paper/meter target in ticks
                else if (argument == "--label-stop-ticks") settings.LabelStopTicks = Math.Max(0, ParseDouble(value, argument)); // v64r: adverse distance that kills a label (0 = symmetric)
                else if (argument == "--paper-long-threshold") settings.PaperLongThreshold = ParseDouble(value, argument);
                else if (argument == "--paper-short-threshold") settings.PaperShortThreshold = ParseDouble(value, argument);
                else if (argument == "--paper-sim-start-enabled") settings.PaperSimulationStartEnabled = ParseBoolean(value, argument);
                else if (argument == "--min-label-horizon-ms") settings.MinimumLabelHorizonMilliseconds = ParseInt(value, argument);
                else if (argument == "--max-label-horizon-ms") settings.MaximumLabelHorizonMilliseconds = ParseInt(value, argument);
                else if (argument == "--reversal-trigger-ticks") settings.ReversalTriggerTicks = ParseInt(value, argument);
                else if (argument == "--reversal-distance-ticks") settings.ReversalDistanceTicks = ParseInt(value, argument);
                else if (argument == "--reversal-trend-ticks") settings.ReversalTrendTicks = ParseInt(value, argument);
                else if (argument == "--reversal-timeout-ms") settings.ReversalTimeoutMilliseconds = ParseInt(value, argument);
                else if (argument == "--reversal-probability-threshold") settings.ReversalProbabilityThreshold = ParseDouble(value, argument);
                else if (argument == "--min-processing-interval-ms") settings.MinProcessingIntervalMilliseconds = ParseInt(value, argument);
                else if (argument == "--save-file") settings.SaveFile = value;
                else if (argument == "--save-events") settings.SaveEventsFile = value;
                else if (argument == "--load-checkpoint-file") settings.LoadCheckpointFile = value;
                else if (argument == "--checkpoint-file") settings.CheckpointFile = value;
                else if (argument == "--best-checkpoint-file") settings.BestCheckpointFile = value;
                else if (argument == "--manual-checkpoint-file") settings.ManualCheckpointFile = value;
                else if (argument == "--novelty-checkpoint-dir") settings.NoveltyCheckpointDirectory = value;
                else if (argument == "--learning-log-file") settings.LearningLogFile = value;
                else if (argument == "--weight-update-log-file") settings.WeightUpdateLogFile = value;
                else if (argument == "--paper-trade-log-file") settings.PaperTradeLogFile = value;
                else if (argument == "--teacher-example-file") settings.TeacherExampleFile = value;
                else if (argument == "--teacher-candidate-file") settings.TeacherCandidateFile = value;
                else if (argument == "--pipe-client") settings.PipeClient = ParseBoolean(value, argument);
                else if (argument == "--enable-reversal-tracker") settings.EnableReversalTracker = ParseBoolean(value, argument);
                else throw new ArgumentException("Unknown argument " + argument);
            }
            if (settings.TickSize <= 0 || settings.HorizonEvents < 1 || settings.LearningRate <= 0 || settings.CostTicks < 0 || settings.MinimumProfitTargetTicks <= 0)
                throw new ArgumentException("Tick size, horizon, learning rate, cost ticks, and minimum target must be non-negative (tick size, learning rate, and target positive).");
            if (settings.PaperShortThreshold < 0.0 || settings.PaperShortThreshold >= 0.5 ||
                settings.PaperLongThreshold <= 0.5 || settings.PaperLongThreshold > 1.0)
                throw new ArgumentException("Paper short threshold must be from 0.00 to below 0.50, and paper long threshold from above 0.50 to 1.00.");
            if (settings.MinProcessingIntervalMilliseconds < 0 || settings.MinProcessingIntervalMilliseconds > 1000)
                throw new ArgumentException("Processing interval must be from 0 to 1000 milliseconds.");
            if (settings.MinimumLabelHorizonMilliseconds < 1 ||
                settings.MaximumLabelHorizonMilliseconds < settings.MinimumLabelHorizonMilliseconds ||
                settings.MaximumLabelHorizonMilliseconds > MaximumAllowedLabelHorizonMilliseconds)
                throw new ArgumentException("Label horizon must have 1 <= min <= max <= " + MaximumAllowedLabelHorizonMilliseconds + " milliseconds.");
            if (settings.ReversalTriggerTicks < 1 || settings.ReversalDistanceTicks < 1 || settings.ReversalTrendTicks < settings.ReversalTriggerTicks ||
                settings.ReversalTimeoutMilliseconds < 100 || settings.ReversalTimeoutMilliseconds > 60000 ||
                settings.ReversalProbabilityThreshold <= 0.0 || settings.ReversalProbabilityThreshold >= 1.0)
                throw new ArgumentException("Reversal settings require positive tick distances, trend >= trigger, timeout 100-60000 ms, and threshold between 0 and 1.");
            return settings;
        }

        private static void Run(Settings settings)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }
            OnlineStandardizer standardizer = new OnlineStandardizer();
            NestedTemporalModel model = new NestedTemporalModel();
            string startupCheckpoint = string.IsNullOrWhiteSpace(settings.LoadCheckpointFile)
                ? settings.CheckpointFile : settings.LoadCheckpointFile;
            if (!TryLoadCheckpoint(startupCheckpoint, standardizer, model))
            {
                standardizer = new OnlineStandardizer();
                model = new NestedTemporalModel();
            }
            List<PendingExample> pending = new List<PendingExample>();
            long frames = 0, valid = 0, invalidBooks = 0, lockedBooks = 0, corruptBooks = 0, trained = 0, skipped = 0, correct = 0, upLabels = 0, downLabels = 0, labeled = 0;
            long labelsArmedWithLatency = 0;
            long processedFrames = 0, performanceSkippedFrames = 0;
            List<ValidationResult> validationResults = new List<ValidationResult>();
            long validationCorrect = 0, validationUpLabels = 0, validationDownLabels = 0;
            double bestValidationEdge = LoadBestValidationEdge(settings.BestCheckpointFile);
            long lastProcessedUtcTicks = 0, lastSummaryUtcTicks = 0, lastLiveProbabilityOutputUtcTicks = 0;
            long rejectedSinceValid = 0, lastRejectLogUtcTicks = 0; // book-rejected watchdog (never silent)
            long lastCheckpointUtcTicks = 0, lastLabelCandidateUtcTicks = 0, noveltyNormalSinceUtcTicks = 0, lastNoveltyCheckpointUtcTicks = 0;
            bool unfamiliarRegimeActive = false;
            long predictions = 0, upPredictions = 0, downPredictions = 0;
            long paperLongSignals = 0, paperShortSignals = 0, paperNoTradeSignals = 0;
            long completedPaperTrades = 0, paperWins = 0, paperLosses = 0;
            double paperNetTicks = 0.0, paperPeakTicks = 0.0, paperMaxDrawdownTicks = 0.0;
            List<PaperTrade> openPaperTrades = new List<PaperTrade>();
            // Latency-aware entry: a signalled paper entry is held pending for
            // PaperEntryLatencyMs, then filled at the touch AS IT IS THEN — so the
            // paper price reflects the real signal->fill delay, not an instant fill.
            List<long[]> pendingPaperEntries = new List<long[]>(); // {isLong(1/0), eligibleUtcTicks}
            PaperSimulationController paperSimulation = new PaperSimulationController(settings.PaperSimulationStartEnabled);
            ModelSnapshotController modelSnapshot = new ModelSnapshotController();
            ulong previousSequence = 0;
            string lastPaperSignal = null;
            long previousEventUtcTicks = 0;
            double previousMidpoint = 0.0;
            double recentEventsPerSecond = 0.0;
            double recentVolatilityTicks = 0.0;
            Frame previousFeatureFrame = null;
            // Shadow observer: its state, labels, and tiny diagnostic head are
            // fully separate from the 222-feature primary temporal model.
            // It always runs, but it cannot change direction predictions,
            // primary weights, candidate adoption, or checkpoint promotion.
            ReversalTracker reversalTracker = new ReversalTracker(settings);
            RollingLatency feedEventAgeLatency = new RollingLatency();
            RollingLatency callbackToPublishLatency = new RollingLatency();
            RollingLatency publishToReceiveLatency = new RollingLatency();
            RollingLatency receiveToPredictionLatency = new RollingLatency();
            RollingLatency callbackToPredictionLatency = new RollingLatency();

            FileStream capture = null;
            StreamWriter learningLog = null;
            StreamWriter weightUpdateLog = null;
            StreamWriter paperTradeLog = null;
            StreamWriter teacherExamples = null;
            long teacherCandidateVersion = 0;
            DomEventRecorder eventRecorder = null;
            HogEvidenceReader hogEvidenceReader = string.IsNullOrWhiteSpace(settings.HogEvidenceMap) ? null : new HogEvidenceReader(settings.HogEvidenceMap);
            HogEvidence hogEvidence = new HogEvidence();
            HogFusion hogFusion = hogEvidenceReader != null ? new HogFusion() : null;
            string hogFusionPath = string.IsNullOrWhiteSpace(settings.CheckpointFile) ? null : settings.CheckpointFile + ".hogfusion";
            if (hogFusion != null) { hogFusion.Load(hogFusionPath); Console.WriteLine("hog-fusion=" + settings.HogFusionMode + " evidence-map=" + settings.HogEvidenceMap + " max-age-ms=" + settings.HogEvidenceMaxAgeMs + " agree-gate=" + (settings.HogAgreeGate ? "on" : "off") + " file=" + (hogFusionPath ?? "(none)") + " labels=" + hogFusion.Labels); }
            long hogFusionLastSave = 0, hogGateBlocked = 0, hogGateLastLogUtc = 0; string hogGateLastReason = "";
            bool hogFresh = false; double hogPUpNow = double.NaN, hogPFusedNow = double.NaN, hogAgeMsNow = 0; string hogEvidenceState = "none";
            FusionHead fusionHead = new FusionHead();
            string fusionPath = string.IsNullOrWhiteSpace(settings.CheckpointFile) ? null : settings.CheckpointFile + ".fusion";
            fusionHead.Load(fusionPath);
            if (!string.IsNullOrWhiteSpace(settings.SaveFile))
            {
                string folder = Path.GetDirectoryName(settings.SaveFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                capture = new FileStream(settings.SaveFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                Console.WriteLine("Saving processed full-book frames to " + settings.SaveFile);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(settings.LearningLogFile))
                {
                    string folder = Path.GetDirectoryName(settings.LearningLogFile);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    learningLog = new StreamWriter(settings.LearningLogFile, false);
                    learningLog.WriteLine("utc,update,entry_frame,label_frame,dynamic_horizon_ms,actual_horizon_ms,original_probability,original_direction,label,long_net_ticks,short_net_ticks,entry_spread_ticks,estimated_cost_ticks,profit_target_ticks,reason,learning");
                    Console.WriteLine("Saving detailed learning updates to " + settings.LearningLogFile);
                }
                if (!string.IsNullOrWhiteSpace(settings.WeightUpdateLogFile))
                {
                    string folder = Path.GetDirectoryName(settings.WeightUpdateLogFile);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    weightUpdateLog = new StreamWriter(settings.WeightUpdateLogFile, false);
                    weightUpdateLog.WriteLine("utc,update,entry_frame,label_frame,original_probability,label,reason,parameter_kind,feature_index,temporal_state,raw_feature_index,feature_name,input_value,weight_before,data_gradient,l2_gradient,weight_delta,weight_after");
                    Console.WriteLine("Saving full feature and weight audit to " + settings.WeightUpdateLogFile + " (223 rows per learning update).");
                }
                if (!string.IsNullOrWhiteSpace(settings.PaperTradeLogFile))
                {
                    string folder = Path.GetDirectoryName(settings.PaperTradeLogFile);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    paperTradeLog = new StreamWriter(settings.PaperTradeLogFile, false);
                    paperTradeLog.WriteLine("entry_utc,exit_utc,direction,entry_frame,exit_frame,entry_price,exit_price,gross_ticks,extra_cost_ticks,net_ticks,target_ticks,exit_reason,cumulative_net_ticks,drawdown_ticks");
                    Console.WriteLine("Saving internal paper-trade results to " + settings.PaperTradeLogFile + ". No broker or NinjaTrader orders are sent.");
                }
                if (!string.IsNullOrWhiteSpace(settings.TeacherExampleFile))
                {
                    string folder = Path.GetDirectoryName(settings.TeacherExampleFile);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    teacherExamples = new StreamWriter(settings.TeacherExampleFile, false);
                    // The compact teacher is intentionally slow-side work.
                    // Buffered writes avoid forcing a disk flush on every live
                    // label while still making a batch visible quickly.
                    teacherExamples.AutoFlush = false;
                    teacherExamples.WriteLine("utc_ticks,label,prediction,features_csv");
                    Console.WriteLine("compact-teacher examples=" + settings.TeacherExampleFile);
                }
                // Fixed 2-tick directional tracker/calibration (uses the max
                // label horizon as its first-touch timeout).
                long labelLatencyTicks = (long)settings.LabelLatencyMs * TimeSpan.TicksPerMillisecond;
                DirectionalTracker twoTickTracker = new DirectionalTracker(2.0, settings.TickSize, settings.MaximumLabelHorizonMilliseconds, labelLatencyTicks);
                SignalOutcomeTracker paperOutcomeTracker = new SignalOutcomeTracker(2.0, settings.TickSize, settings.MaximumLabelHorizonMilliseconds, labelLatencyTicks);
                Console.WriteLine("label-latency-ms=" + settings.LabelLatencyMs +
                    (settings.LabelLatencyMs > 0
                        ? " (labels, 2-tick meter and paper-outcome measure from the touch/mid " + settings.LabelLatencyMs + "ms AFTER the signal - the price an order could actually get)"
                        : " (legacy: labels assume instant execution)"));
                long lastTwoTickTelemetryUtc = 0;
                double smoothedProbability = 0.5; bool smoothedInit = false;
                // v64v: carries the traded probability to the 1 Hz meter line, so the number
                // printed is the same one the meter is grading.
                double lastDecisionProbabilityForMeter = 0.5;
                // Rolling mid window (~5s) for the regime range gate.
                List<long> regimeMidTimes = new List<long>(); List<double> regimeMidPx = new List<double>();
                const long RegimeWindowTicks = 5000L * TimeSpan.TicksPerMillisecond;
                ShadowController shadowController = new ShadowController { Fusion = EnableCrossMarketAndReversal && settings.SignalMode == "fusion" };
                StartShadowControlServer(settings.PipeName + "ShadowControl", shadowController);
                StartPaperControlServer(settings.PipeName + "PaperControl", paperSimulation);
                StartModelSnapshotControlServer(settings.PipeName + "ModelControl", modelSnapshot);
                Console.WriteLine("paper-simulation=" + (paperSimulation.Enabled ? "enabled" : "disabled") +
                    " control-pipe=\\.\\pipe\\" + settings.PipeName + "PaperControl" +
                    (settings.PaperExternal ? string.Format(CultureInfo.InvariantCulture, " mode=mirror (entries from the dashboard AI Trader; bracket stop={0:F2} target={1:F2} trail={2:F2} ticks, no time limit)", settings.PaperStopTicks, settings.PaperTargetTicks, settings.PaperTrailTicks) : " mode=self-driven (every LONG/SHORT signal)"));
                Console.WriteLine("heavy-processing-interval-ms=" + settings.MinProcessingIntervalMilliseconds +
                    " (all pipe frames are still drained)");
                Console.WriteLine("reversal-tracker=shadow trigger=" + settings.ReversalTriggerTicks + "t reversal=" + settings.ReversalDistanceTicks +
                    "t trend=" + settings.ReversalTrendTicks + "t timeout=" + settings.ReversalTimeoutMilliseconds + "ms threshold=" +
                    settings.ReversalProbabilityThreshold.ToString("F2", CultureInfo.InvariantCulture) + " (diagnostic only; never affects primary temporal model)");
                if (!string.IsNullOrWhiteSpace(settings.BestCheckpointFile))
                    Console.WriteLine("validated-best-checkpoint=" + settings.BestCheckpointFile +
                        " validation=every-" + settings.ValidationStride + "th-resolved-label rolling-window=" + ValidationWindow +
                        " minimum-samples=" + MinimumValidationSamplesForBest);
                // Every session has a recoverable best checkpoint from its
                // first instant.  Once enough held-out labels exist, this file
                // is replaced only by a strictly higher validation edge.
                if (!string.IsNullOrWhiteSpace(settings.BestCheckpointFile) && !File.Exists(settings.BestCheckpointFile))
                {
                    if (SaveCheckpoint(settings.BestCheckpointFile, standardizer, model))
                        Console.WriteLine("session-best-initialized file=" + settings.BestCheckpointFile +
                            " (baseline; waiting for held-out labels before promotion)");
                    else
                        Console.Error.WriteLine("session-best-initialization failed.");
                }
                if (!string.IsNullOrWhiteSpace(settings.ManualCheckpointFile))
                    Console.WriteLine("manual-model-snapshot=" + settings.ManualCheckpointFile +
                        " control-pipe=\\\\.\\pipe\\" + settings.PipeName + "ModelControl");
                MmfFrameSource directSource = null;
                if (!string.IsNullOrWhiteSpace(settings.MmfMap))
                {
                    directSource = new MmfFrameSource(settings.MmfMap, settings.EmitPerEvent, settings.BookGapStalenessMs, settings.RestingDepthMs,
                        settings.DomShift, settings.DomWarmup);
                    directSource.CoalesceAboveEvents = settings.CoalesceAboveEvents;
                    if (!string.IsNullOrWhiteSpace(settings.SaveEventsFile))
                    {
                        try { eventRecorder = new DomEventRecorder(settings.SaveEventsFile); directSource.Recorder = eventRecorder; Console.WriteLine("Recording DOM events to " + settings.SaveEventsFile + " (40-byte records; for LnnExecBacktest / RingReplayer)."); }
                        catch (Exception ex) { Console.Error.WriteLine("DOM event recording not started: " + ex.Message); }
                    }
                    Console.WriteLine("direct-mmf granularity=" + (settings.EmitPerEvent ? (settings.CoalesceAboveEvents > 0 ? "per-event(coalesce-above " + settings.CoalesceAboveEvents + ")" : "per-event") : "per-batch") +
                        " book-gap-staleness-ms=" + settings.BookGapStalenessMs +
                        " dom-shift=" + (settings.DomShift ? "nt8-insert-remove" : "legacy-set-slot") +
                        " dom-warmup=" + (settings.DomWarmup ? "ring-history" : "off"));
                }

                // Cross-market shadow stack + fusion head
                ShadowMarket esMarket = EnableCrossMarketAndReversal && !string.IsNullOrWhiteSpace(settings.EsMap) ? new ShadowMarket("ES", settings.EsMap, .25) : null;
                ShadowMarket nqMarket = EnableCrossMarketAndReversal && !string.IsNullOrWhiteSpace(settings.NqMap) ? new ShadowMarket("NQ", settings.NqMap, .25) : null;
                ShadowMarket vxMarket = EnableCrossMarketAndReversal && !string.IsNullOrWhiteSpace(settings.VxMap) ? new ShadowMarket("VX", settings.VxMap, .05) : null;
                ShadowMarket spyMarket = EnableCrossMarketAndReversal && !string.IsNullOrWhiteSpace(settings.SpyMap) ? new ShadowMarket("SPY", settings.SpyMap, .01) : null;
                double fusionProbability = .5;
                int lagDirection = 0; double lagScore = 0; bool vxRisk = false;
                int shadowAbsorptionDirection = 0; double shadowAbsorptionScore = 0;
                long lastShadowTelemetryUtc = 0, lastFusionSaveUpdates = 0;
                Console.WriteLine("cross-market shadow stack: ES=" + (esMarket != null ? settings.EsMap : "off") +
                    " NQ=" + (nqMarket != null ? settings.NqMap : "off") +
                    " VX=" + (vxMarket != null ? settings.VxMap : "off") +
                    " SPY=" + (spyMarket != null ? settings.SpyMap : "off") +
                    " signal-mode=" + settings.SignalMode);

                // Direct order emission (opt-in). On a paper-signal change the
                // engine writes join (op 30/31) or market (op 0/1) / flatten
                // (op 13) straight into the command ring — bypassing the
                // dashboard forward hop. Hold + trade-gap are the only engine
                // side gates; the NT8 strategy keeps the hard caps.
                DirectOrderRing orderRing = null;
                bool orderJoinMode = settings.OrderMode == "join";
                long orderGapTicks = (long)(settings.OrderTradeGapSeconds * TimeSpan.TicksPerSecond);
                long orderHoldTicks = (long)(settings.OrderHoldSeconds * TimeSpan.TicksPerSecond);
                string orderObservedSignal = null;   // last signal value seen
                long orderSignalSinceUtc = 0;        // when the observed signal began
                string orderActedSignal = null;      // last signal we sent a command for
                long orderLastEntryUtc = 0;
                if (settings.EmitOrders && !string.IsNullOrWhiteSpace(settings.OrderCmdMap))
                {
                    try
                    {
                        orderRing = new DirectOrderRing(settings.OrderCmdMap);
                        Console.WriteLine("DIRECT ORDERS ENABLED: writing " + (orderJoinMode ? "JOIN (op 30/31, via executioner)" : "MARKET (op 0/1, immediate)") +
                            " to " + settings.OrderCmdMap + " qty=" + settings.OrderQty +
                            " hold=" + settings.OrderHoldSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s gap=" +
                            settings.OrderTradeGapSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s. The NT8 strategy's hard caps still apply.");
                    }
                    catch (Exception oe) { Console.Error.WriteLine("Direct order ring failed to open (" + settings.OrderCmdMap + "): " + oe.Message + " — orders NOT enabled."); orderRing = null; }
                }

                while (true)
                {
                    PipeStream pipe = null;
                    if (directSource == null)
                    {
                        try
                        {
                            if (settings.PipeClient)
                            {
                                Console.WriteLine("Connecting to dashboard MMF forwarder at \\.\\pipe\\" + settings.PipeName);
                                NamedPipeClientStream client = new NamedPipeClientStream(".", settings.PipeName, PipeDirection.In, PipeOptions.None);
                                client.Connect(5000);
                                pipe = client;
                            }
                            else
                            {
                                NamedPipeServerStream server = new NamedPipeServerStream(settings.PipeName, PipeDirection.In, 1,
                                    PipeTransmissionMode.Byte, PipeOptions.None);
                                Console.WriteLine("Waiting for NinjaTrader bridge at \\.\\pipe\\" + settings.PipeName);
                                server.WaitForConnection();
                                pipe = server;
                            }
                        }
                        catch (IOException error)
                        {
                            Console.Error.WriteLine("Bridge connection failed: " + error.Message);
                            Thread.Sleep(250);
                            continue;
                        }
                    }
                    using (pipe)
                    {
                        Console.WriteLine(directSource != null
                            ? "Bridge connected. Direct shared-memory transport: " + settings.MmfMap + " (no pipe, no forwarder process)."
                            : "Bridge connected.");
                        Frame frame;
                        byte[] rawFrame;
                        while (directSource != null
                            ? directSource.TryReadFrame(out frame, out rawFrame)
                            : TryReadFrame(pipe, out frame, out rawFrame))
                        {
                            frames++;
                            long receiverUtcTicks = UtcNowTicks();
                            // Shadow stack runs ONLY when fusion is enabled. When
                            // off, the engine skips all cross-market work — pure
                            // v12 threshold inference, no influence, no overhead.
                            if (shadowController.Fusion)
                            {
                            // Drain cross-market shadow rings and refresh evidence.
                            // Fully isolated: any shadow fault degrades to "no
                            // cross-market evidence", never stops the receiver.
                            try
                            {
                            if (esMarket != null) esMarket.Drain();
                            if (nqMarket != null) nqMarket.Drain();
                            if (vxMarket != null) vxMarket.Drain();
                            if (spyMarket != null) spyMarket.Drain();
                            double lagBlend = .50 * (esMarket != null ? esMarket.Fast : 0) +
                                              .35 * (nqMarket != null ? nqMarket.Fast : 0) +
                                              .15 * (spyMarket != null ? spyMarket.Fast : 0);
                            lagScore = Math.Abs(lagBlend);
                            lagDirection = lagScore >= .25 ? (lagBlend > 0 ? 1 : -1) : 0;
                            vxRisk = vxMarket != null && (Math.Abs(vxMarket.Fast) >= .50 || Math.Abs(vxMarket.MomentumTicks) >= 2);
                            shadowAbsorptionDirection = 0; shadowAbsorptionScore = 0;
                            if (esMarket != null && receiverUtcTicks - esMarket.AbsorptionUtc < TimeSpan.TicksPerSecond)
                            { shadowAbsorptionDirection = esMarket.AbsorptionDirection; shadowAbsorptionScore = esMarket.AbsorptionScore; }
                            }
                            catch (Exception shadowError)
                            {
                                lagDirection = 0; lagScore = 0; vxRisk = false;
                                shadowAbsorptionDirection = 0; shadowAbsorptionScore = 0;
                                Console.Error.WriteLine("shadow-stack error (ignored): " + shadowError.Message);
                            }
                            }
                            else
                            {
                                // Shadow off: no cross-market evidence at all.
                                lagDirection = 0; lagScore = 0; vxRisk = false;
                                shadowAbsorptionDirection = 0; shadowAbsorptionScore = 0;
                            }
                            if (frame.HasLatencyTimestamps)
                            {
                                feedEventAgeLatency.AddTicks(frame.UtcTicks, frame.CallbackUtcTicks);
                                callbackToPublishLatency.AddTicks(frame.CallbackUtcTicks, frame.PipePublishUtcTicks);
                                publishToReceiveLatency.AddTicks(frame.PipePublishUtcTicks, receiverUtcTicks);
                            }
                            BookState bookState = CheckBook(frame, settings.TickSize);
                            if (bookState != BookState.Valid)
                            {
                                invalidBooks++;
                                rejectedSinceValid++;
                                if (receiverUtcTicks - lastRejectLogUtcTicks >= 2 * TimeSpan.TicksPerSecond)
                                {
                                    // Frames are arriving but every one is being thrown
                                    // away: report it so the summary's silence is explained.
                                    lastRejectLogUtcTicks = receiverUtcTicks;
                                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                        "book-rejected state={0} since-last-valid={1} total-invalid={2} locked={3} corrupt={4} bid0={5:F2}x{6} ask0={7:F2}x{8} bid1={9:F2} ask1={10:F2} frames={11} valid={12}",
                                        bookState, rejectedSinceValid, invalidBooks, lockedBooks, corruptBooks,
                                        frame.Bid[0], frame.BidVolume[0], frame.Ask[0], frame.AskVolume[0], frame.Bid[1], frame.Ask[1], frames, valid));
                                }
                                if (bookState == BookState.TemporarilyNonTradeable)
                                {
                                    // Do not resolve labels, predict, train, or
                                    // paper-trade on a locked/crossed quote.
                                    // Crucially, retain the last valid temporal
                                    // state and pending examples for the next
                                    // valid snapshot.
                                    lockedBooks++;
                                    // This frame was received, so advance the
                                    // transport sequence as well. Otherwise
                                    // the following valid frame would look like
                                    // a sequence gap and undo the preservation
                                    // policy above.
                                    previousSequence = frame.Sequence;
                                }
                                else
                                {
                                    // Structural corruption (missing/NaN,
                                    // negative volume, or bad depth order) can
                                    // invalidate feature identity. Reset only
                                    // for this materially unsafe condition.
                                    corruptBooks++;
                                    previousSequence = frame.Sequence;
                                    pending.Clear();
                                    model.ResetTemporalState();
                                    previousFeatureFrame = null;
                                    openPaperTrades.Clear(); pendingPaperEntries.Clear();
                                }
                                continue;
                            }
                            valid++;
                            rejectedSinceValid = 0;
                            if (previousSequence != 0 && frame.Sequence != previousSequence + 1)
                            {
                                pending.Clear();
                                model.ResetTemporalState();
                                previousFeatureFrame = null;
                                openPaperTrades.Clear(); pendingPaperEntries.Clear();
                                Console.WriteLine("Sequence discontinuity; cleared pending labels.");
                            }
                            previousSequence = frame.Sequence;

                            // Paper execution timing uses the local NinjaTrader
                            // callback clock when available. The provider event
                            // clock remains the training/label clock below, but
                            // it must not decide a local simulated exit.
                            long paperUtcTicks = frame.HasLatencyTimestamps
                                ? frame.CallbackUtcTicks : receiverUtcTicks;

                            double midpoint = (frame.Bid[0] + frame.Ask[0]) * 0.5;
                            UpdateMarketDynamics(frame.UtcTicks, midpoint, settings.TickSize,
                                ref previousEventUtcTicks, ref previousMidpoint,
                                ref recentEventsPerSecond, ref recentVolatilityTicks);
                            if (settings.EnableReversalTracker) reversalTracker.Observe(frame.UtcTicks, midpoint, frame, settings.TickSize);

                            // Exit handling deliberately runs before the model
                            // performance gate. In MES 5 ms mode, predictions
                            // remain rate-limited, but an already-open internal
                            // paper position is checked on every valid received
                            // DOM snapshot (0 ms additional simulator cadence).
                            // Resolve each open paper trade independently. Up to
                            // PaperMaxOpen may be open at once. A trade closes on
                            // its target/stop, on the deadline (unless PaperNoTimeout),
                            // or immediately if the sim was turned off.
                            for (int t = openPaperTrades.Count - 1; t >= 0; t--)
                            {
                                PaperTrade pt = openPaperTrades[t];
                                string exitReason = null;
                                if (!paperSimulation.Enabled) exitReason = "manual_paper_sim_stop";
                                else if (settings.PaperExternal && paperSimulation.PendingCommand == 3) exitReason = "flatten";
                                else if (pt.StopTicks > 0 || pt.PtTicks > 0 || pt.TrailTicks > 0)
                                {
                                    // Mirror of the strategy's bracket: fixed target, fixed stop, or a
                                    // trailing stop off the best touch when there is no fixed stop.
                                    double openGrossTicks = GetPaperTradeGrossTicks(pt, frame, settings.TickSize);
                                    if (openGrossTicks > pt.BestTicks) pt.BestTicks = openGrossTicks;
                                    if (pt.PtTicks > 0 && openGrossTicks >= pt.PtTicks) exitReason = "profit_target_reached";
                                    else if (pt.StopTicks > 0 && openGrossTicks <= -pt.StopTicks) exitReason = "stop_loss_reached";
                                    else if (pt.StopTicks <= 0 && pt.TrailTicks > 0 && openGrossTicks <= pt.BestTicks - pt.TrailTicks) exitReason = "trailing_stop";
                                }
                                else
                                {
                                    double openGrossTicks = GetPaperTradeGrossTicks(pt, frame, settings.TickSize);
                                    if (openGrossTicks >= pt.TargetTicks) exitReason = "profit_target_reached";
                                    else if (openGrossTicks <= -pt.TargetTicks) exitReason = "stop_loss_reached";
                                    else if (!settings.PaperNoTimeout && paperUtcTicks >= pt.DeadlineUtcTicks) exitReason = "time_limit_exit";
                                }
                                if (exitReason != null)
                                {
                                    if (exitReason == "flatten" && t == 0) paperSimulation.PendingCommand = 0;   // last open trade closed by the flatten
                                    ClosePaperTrade(paperTradeLog, pt, frame, paperUtcTicks,
                                        settings.TickSize, settings.CostTicks, exitReason,
                                        ref completedPaperTrades, ref paperWins, ref paperLosses, ref paperNetTicks,
                                        ref paperPeakTicks, ref paperMaxDrawdownTicks);
                                    openPaperTrades.RemoveAt(t);
                                }
                            }

                            if (settings.MinProcessingIntervalMilliseconds > 0 && lastProcessedUtcTicks > 0 &&
                                frame.UtcTicks - lastProcessedUtcTicks < settings.MinProcessingIntervalMilliseconds * TimeSpan.TicksPerMillisecond)
                            {
                                performanceSkippedFrames++;
                                continue;
                            }
                            lastProcessedUtcTicks = frame.UtcTicks;
                            processedFrames++;
                            if (modelSnapshot.Requested)
                            {
                                modelSnapshot.Requested = false;
                                if (SaveCheckpoint(settings.ManualCheckpointFile, standardizer, model))
                                    Console.WriteLine("manual-model-saved file=" + settings.ManualCheckpointFile +
                                        " processed-frame=" + processedFrames);
                                else
                                    Console.Error.WriteLine("manual-model-save failed; choose a valid capture folder and restart the learner.");
                            }
                            if (capture != null)
                            {
                                capture.Write(rawFrame, 0, rawFrame.Length);
                                if (processedFrames % 100 == 0) capture.Flush();
                            }

                            for (int pendingIndex = pending.Count - 1; pendingIndex >= 0; pendingIndex--)
                            {
                                PendingExample example = pending[pendingIndex];
                                if (!example.Armed)
                                {
                                    // Latency-aware label: the round trip has not elapsed yet,
                                    // so the model's order could not have been filled — don't
                                    // score. Once it has, the entry reference becomes the touch
                                    // NOW (what the order would actually get) and the window starts.
                                    if (frame.UtcTicks < example.ArmUtcTicks) continue;
                                    example.EntryBid = frame.Bid[0];
                                    example.EntryAsk = frame.Ask[0];
                                    example.DeadlineUtcTicks = frame.UtcTicks + example.HorizonTicks;
                                    example.Armed = true;
                                    labelsArmedWithLatency++;
                                }
                                double longNetTicks = (frame.Bid[0] - example.EntryAsk) / settings.TickSize - settings.CostTicks;
                                double shortNetTicks = (example.EntryBid - frame.Ask[0]) / settings.TickSize - settings.CostTicks;
                                // v64r asymmetric labels. Two brackets are scored side by side
                                // off the same entry, each with the SAME target and stop:
                                //   long  bracket: wins at +target, dies at -stop
                                //   short bracket: wins at +target (price down), dies at -stop
                                // A bracket that has been stopped can never win afterwards, so a
                                // move that first retraces past the stop and only later reaches the
                                // target is a loss - which is what actually happens to a live
                                // order. When stop == target this reduces exactly to the old
                                // symmetric "first barrier to +/-T" label.
                                double targetTicksL = example.ProfitTargetTicks, stopTicksL = example.StopTicks;
                                // The long bracket's stop is an adverse move, which is the short
                                // side's favourable move, and vice versa - both already net of
                                // spread and cost by construction of longNetTicks / shortNetTicks.
                                if (!example.LongStopped && shortNetTicks >= stopTicksL) example.LongStopped = true;
                                if (!example.ShortStopped && longNetTicks >= stopTicksL) example.ShortStopped = true;

                                bool longWon = !example.LongStopped && longNetTicks >= targetTicksL;
                                bool shortWon = !example.ShortStopped && shortNetTicks >= targetTicksL;
                                bool bothDead = example.LongStopped && example.ShortStopped;
                                bool expired = frame.UtcTicks >= example.DeadlineUtcTicks;
                                if (!longWon && !shortWon && !bothDead && !expired)
                                    continue;

                                pending.RemoveAt(pendingIndex);
                                if (!longWon && !shortWon)
                                {
                                    // Both brackets stopped, or the horizon ran out with neither
                                    // resolved. Neither direction was tradeable; not a label.
                                    skipped++;
                                    continue;
                                }
                                bool labelUp = longWon && (!shortWon || longNetTicks >= shortNetTicks);
                                double label = labelUp ? 1.0 : 0.0;
                                labeled++;
                                if (labelUp) upLabels++; else downLabels++;
                                WriteTeacherExample(teacherExamples, frame.UtcTicks, label, example);
                                if (teacherExamples != null && labeled % 20 == 0)
                                    teacherExamples.Flush();
                                bool isValidationExample = labeled % settings.ValidationStride == 0;
                                if (isValidationExample)
                                {
                                    AddValidationResult(validationResults, new ValidationResult(labelUp, example.PredictedUp),
                                        ref validationCorrect, ref validationUpLabels, ref validationDownLabels);
                                    double validationAccuracy = 100.0 * validationCorrect / validationResults.Count;
                                    double validationBaseline = 100.0 * Math.Max(validationUpLabels, validationDownLabels) / validationResults.Count;
                                    double validationEdge = validationAccuracy - validationBaseline;
                                    if (validationResults.Count >= MinimumValidationSamplesForBest &&
                                        validationEdge > bestValidationEdge + 0.0001 &&
                                        SaveCheckpoint(settings.BestCheckpointFile, standardizer, model))
                                    {
                                        bestValidationEdge = validationEdge;
                                        SaveBestValidationRecord(settings.BestCheckpointFile, validationAccuracy, validationBaseline,
                                            validationEdge, validationResults.Count);
                                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                            "validated-best-saved edge={0:F2}% accuracy={1:F2}% baseline={2:F2}% samples={3} file={4}",
                                            validationEdge, validationAccuracy, validationBaseline, validationResults.Count,
                                            settings.BestCheckpointFile));
                                    }
                                    WriteLearningLog(learningLog, labeled, example, frames, frame.UtcTicks,
                                        labelUp, longNetTicks, shortNetTicks, false);
                                }
                                else
                                {
                                    double prediction = model.Predict(example.Features);
                                    WeightChange[] weightChanges = model.Learn(example.Features, label, prediction, settings.LearningRate,
                                        weightUpdateLog != null);
                                    if (hogFusion != null && example.HogFeatures != null)
                                    {
                                        hogFusion.Learn(example.HogFeatures, label, example.EnginePUp, example.HogPUp, settings.LearningRate);
                                    }
                                    trained++;
                                    // The same resolved outcome teaches the fusion head
                                    // how much the shadow evidence was worth.
                                    if (example.MetaFeatures != null)
                                    {
                                        fusionHead.Learn(example.MetaFeatures, label, settings.LearningRate);
                                        if (fusionHead.Updates - lastFusionSaveUpdates >= 200)
                                        { fusionHead.Save(fusionPath); lastFusionSaveUpdates = fusionHead.Updates; }
                                    }
                                    WriteLearningLog(learningLog, trained, example, frames, frame.UtcTicks,
                                        labelUp, longNetTicks, shortNetTicks, true);
                                    WriteWeightUpdateLog(weightUpdateLog, trained, example, frames, labelUp, weightChanges);
                                    if (trained % 100 == 0)
                                    {
                                        if (learningLog != null) learningLog.Flush();
                                        if (weightUpdateLog != null) weightUpdateLog.Flush();
                                    }
                                }
                                // Score the prediction made when the feature snapshot was
                                // created, before any later labels could change the model.
                                if (example.PredictedUp == labelUp) correct++;
                            }

                            double[] raw = MakeRawFeatures(frame, previousFeatureFrame, settings.TickSize);
                            previousFeatureFrame = frame;
                            int noveltyExtremeFeatures;
                            double noveltyScore = standardizer.NoveltyScore(raw, out noveltyExtremeFeatures);
                            // Use asymmetric entry/exit thresholds plus a sustained-normal
                            // period.  Without this, a single noisy depth feature can flip
                            // NORMAL/UNFAMILIAR several times in one millisecond and cause a
                            // checkpoint storm.
                            bool noveltyEnter = standardizer.IsReady &&
                                (noveltyScore >= NoveltyEnterScore || noveltyExtremeFeatures >= NoveltyEnterExtremeFeatures);
                            bool noveltyClear = !standardizer.IsReady ||
                                (noveltyScore < NoveltyExitScore && noveltyExtremeFeatures <= NoveltyExitExtremeFeatures);
                            if (!unfamiliarRegimeActive && noveltyEnter)
                            {
                                unfamiliarRegimeActive = true;
                                noveltyNormalSinceUtcTicks = 0;
                                bool cooldownElapsed = lastNoveltyCheckpointUtcTicks == 0 ||
                                    frame.UtcTicks - lastNoveltyCheckpointUtcTicks >= NoveltyCheckpointCooldownMilliseconds * TimeSpan.TicksPerMillisecond;
                                if (cooldownElapsed && !string.IsNullOrWhiteSpace(settings.NoveltyCheckpointDirectory))
                                {
                                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                                    string file = Path.Combine(settings.NoveltyCheckpointDirectory, "pre_unfamiliar_" + stamp + "_score_" + noveltyScore.ToString("F2", CultureInfo.InvariantCulture) + ".bin");
                                    if (SaveCheckpoint(file, standardizer, model))
                                    {
                                        lastNoveltyCheckpointUtcTicks = frame.UtcTicks;
                                        Console.WriteLine("pre-unfamiliar-checkpoint-saved file=" + file + " score=" + noveltyScore.ToString("F3", CultureInfo.InvariantCulture) + " extremes=" + noveltyExtremeFeatures + " cooldown-ms=" + NoveltyCheckpointCooldownMilliseconds);
                                    }
                                }
                            }
                            else if (unfamiliarRegimeActive)
                            {
                                if (noveltyClear)
                                {
                                    if (noveltyNormalSinceUtcTicks == 0) noveltyNormalSinceUtcTicks = frame.UtcTicks;
                                    else if (frame.UtcTicks - noveltyNormalSinceUtcTicks >= NoveltyNormalClearMilliseconds * TimeSpan.TicksPerMillisecond)
                                        unfamiliarRegimeActive = false;
                                }
                                else noveltyNormalSinceUtcTicks = 0;
                            }
                            double[] normalized = standardizer.Transform(raw);
                            standardizer.Observe(raw);
                            if (!standardizer.IsReady)
                                continue;
                            double[] features = model.UpdateState(normalized);
                            double probability = model.Predict(features);
                            double enginePUp = probability;
                            double[] hogFeaturesNow = null;
                            hogFresh = false;
                            if (hogFusion != null)
                            {
                                long nowUtcForHog = DateTime.UtcNow.Ticks;
                                if (hogEvidenceReader.TryRead(nowUtcForHog, hogEvidence))
                                {
                                    hogAgeMsNow = (nowUtcForHog - hogEvidence.UtcTicks) / (double)TimeSpan.TicksPerMillisecond;
                                    hogFresh = hogAgeMsNow >= -1000 && hogAgeMsNow <= settings.HogEvidenceMaxAgeMs;
                                    hogEvidenceState = hogFresh ? "fresh" : "stale";
                                }
                                else hogEvidenceState = hogEvidenceReader.Connected ? "unreadable" : "none";
                                if (hogFresh)
                                {
                                    hogPUpNow = hogEvidence.PUp;
                                    hogFeaturesNow = HogFusion.Features(enginePUp, hogEvidence);
                                    hogPFusedNow = hogFusion.Predict(hogFeaturesNow);
                                    if (settings.HogFusionMode == "live") probability = hogPFusedNow;
                                }
                                else { hogPUpNow = double.NaN; hogPFusedNow = double.NaN; }
                            }
                            long predictionCompleteUtcTicks = UtcNowTicks();
                            // 2-tick first-touch tracking / calibration. The
                            // target MATCHES the label target so the meter grades
                            // the exact move the model is trained to predict.
                            // v64k: the target is the user's fixed tick setting.
                            // (The old max(min, 2 x per-event volatility) rule could
                            // never exceed the floor - per-event mid movement on MES
                            // is thousandths of a tick - so it was always the floor.)
                            double twoTickMid = (frame.Bid[0] + frame.Ask[0]) * 0.5;
                            double dynamicTargetTicks = settings.MinimumProfitTargetTicks;
                            // v64v: the skill meter is fed BELOW, once decisionProbability
                            // exists - it must grade the probability that actually places
                            // trades, not the raw one. With --prob-smoothing on, those are
                            // different numbers and the meter was scoring the wrong signal.
                            paperOutcomeTracker.Observe(frame.UtcTicks, twoTickMid);
                            if (lastTwoTickTelemetryUtc == 0 || frame.UtcTicks - lastTwoTickTelemetryUtc >= TimeSpan.TicksPerSecond)
                            {
                                lastTwoTickTelemetryUtc = frame.UtcTicks;
                                Console.WriteLine(twoTickTracker.Line(lastDecisionProbabilityForMeter));
                                Console.WriteLine(paperOutcomeTracker.Line());
                                if (directSource != null) { Console.WriteLine(directSource.FlickerLine(1.0)); Console.WriteLine(directSource.FlickerLevelLine(1.0)); }
                            }
                            if (lastLiveProbabilityOutputUtcTicks == 0 ||
                                predictionCompleteUtcTicks - lastLiveProbabilityOutputUtcTicks >= LiveProbabilityOutputMilliseconds * TimeSpan.TicksPerMillisecond)
                            {
                                Console.WriteLine("live-up-probability=" + probability.ToString("F4", CultureInfo.InvariantCulture));
                                lastLiveProbabilityOutputUtcTicks = predictionCompleteUtcTicks;
                            }
                            if (frame.HasLatencyTimestamps)
                            {
                                receiveToPredictionLatency.AddTicks(receiverUtcTicks, predictionCompleteUtcTicks);
                                callbackToPredictionLatency.AddTicks(frame.CallbackUtcTicks, predictionCompleteUtcTicks);
                            }
                            bool predictedUp = probability >= 0.5;
                            // This is a diagnostic fusion only.  It never enters
                            // the primary feature vector, learning update, paper
                            // signal, or checkpoint decision.
                            ReversalFusionSnapshot reversalFusion = settings.EnableReversalTracker ? BuildReversalShadowFusion(reversalTracker, probability) : null;
                            predictions++;
                            if (predictedUp) upPredictions++; else downPredictions++;
                            int dynamicHorizonMilliseconds = GetDynamicHorizonMilliseconds(settings.HorizonEvents, recentEventsPerSecond,
                                settings.MinimumLabelHorizonMilliseconds, settings.MaximumLabelHorizonMilliseconds);
                            double spreadTicks = (frame.Ask[0] - frame.Bid[0]) / settings.TickSize;
                            // Label target = the user's fixed tick target (same
                            // number the 2-tick meter and the paper sim use).
                            double profitTargetTicks = dynamicTargetTicks;
                            long labelDeadlineUtcTicks = frame.UtcTicks + dynamicHorizonMilliseconds * TimeSpan.TicksPerMillisecond;
                            long paperDeadlineUtcTicks = paperUtcTicks + dynamicHorizonMilliseconds * TimeSpan.TicksPerMillisecond;
                            // Predictions remain available on every processed frame.  Candidate
                            // creation is intentionally capped so a long MES horizon does not
                            // create thousands of nearly identical pending labels.
                            // Only build/keep fusion evidence while the shadow
                            // stack is on; otherwise the fusion head neither
                            // predicts nor trains (its stored weights are frozen).
                            double[] fusionMeta = null;
                            if (shadowController.Fusion)
                            {
                                fusionMeta = new double[]
                                {
                                    (probability - .5) * 2.0,
                                    shadowAbsorptionDirection * shadowAbsorptionScore,
                                    lagDirection * lagScore,
                                    vxRisk ? 1.0 : 0.0
                                };
                                fusionProbability = fusionHead.Predict(fusionMeta);
                            }
                            if (lastLabelCandidateUtcTicks == 0 ||
                                frame.UtcTicks - lastLabelCandidateUtcTicks >= MinimumCandidateSpacingMilliseconds * TimeSpan.TicksPerMillisecond)
                            {
                                PendingExample newExample = new PendingExample((double[])features.Clone(), frame.Bid[0], frame.Ask[0], frames, frame.UtcTicks,
                                    labelDeadlineUtcTicks, predictedUp, probability, profitTargetTicks, settings.LabelStopTicks, spreadTicks, settings.CostTicks, fusionMeta,
                                    (long)settings.LabelLatencyMs * TimeSpan.TicksPerMillisecond);
                                newExample.HogFeatures = hogFeaturesNow; newExample.HogPUp = hogPUpNow; newExample.EnginePUp = enginePUp;
                                pending.Add(newExample);
                                lastLabelCandidateUtcTicks = frame.UtcTicks;
                            }

                            // Optional EMA smoothing of the probability that drives
                            // the trade decision (raw probability still trains the
                            // model and feeds the 2-tick calibration untouched).
                            double decisionProbability = probability;
                            if (settings.ProbabilitySmoothingAlpha > 0.0 && settings.ProbabilitySmoothingAlpha < 1.0)
                            {
                                if (!smoothedInit) { smoothedProbability = probability; smoothedInit = true; }
                                else smoothedProbability += settings.ProbabilitySmoothingAlpha * (probability - smoothedProbability);
                                decisionProbability = smoothedProbability;
                            }
                            lastDecisionProbabilityForMeter = decisionProbability;
                            twoTickTracker.Observe(frame.UtcTicks, frame.Bid[0], frame.Ask[0], decisionProbability,
                                dynamicTargetTicks, settings.LabelStopTicks, settings.CostTicks);
                            // v12's clean 222-feature threshold model is the ONLY
                            // source of trade ideas — computed from its (optionally
                            // smoothed) probability, independent of the shadow stack.
                            string paperSignal = GetPaperSignal(decisionProbability, trained, settings);
                            // Confirmation gate: when the shadow stack is on, it may
                            // only VETO a v12 trade whose cross-market/fusion read
                            // opposes it — it never creates a trade or flips a side.
                            // The two act separately until v12 has a trade to make.
                            bool shadowVetoed = false;
                            if (shadowController.Fusion && (paperSignal == "LONG" || paperSignal == "SHORT"))
                            {
                                shadowVetoed =
                                    (paperSignal == "LONG" && fusionProbability < 0.5 - settings.FusionConfirmMargin) ||
                                    (paperSignal == "SHORT" && fusionProbability > 0.5 + settings.FusionConfirmMargin) ||
                                    vxRisk;
                                if (shadowVetoed) paperSignal = "NO-TRADE";
                            }
                            // Regime gate: measure recent price range and gate on
                            // flat tape or a novelty (transition) spike.
                            regimeMidTimes.Add(frame.UtcTicks); regimeMidPx.Add(twoTickMid);
                            while (regimeMidTimes.Count > 1 && frame.UtcTicks - regimeMidTimes[0] > RegimeWindowTicks)
                            { regimeMidTimes.RemoveAt(0); regimeMidPx.RemoveAt(0); }
                            bool regimeGated = false; string regimeReason = null;
                            if (paperSignal == "LONG" || paperSignal == "SHORT")
                            {
                                if (settings.RegimeGateMinRangeTicks > 0 && regimeMidPx.Count >= 2)
                                {
                                    double lo = regimeMidPx[0], hi = regimeMidPx[0];
                                    for (int k = 1; k < regimeMidPx.Count; k++) { if (regimeMidPx[k] < lo) lo = regimeMidPx[k]; if (regimeMidPx[k] > hi) hi = regimeMidPx[k]; }
                                    double rangeTicks = (hi - lo) / settings.TickSize;
                                    if (rangeTicks < settings.RegimeGateMinRangeTicks) { regimeGated = true; regimeReason = "flat tape (range " + rangeTicks.ToString("F1", CultureInfo.InvariantCulture) + "t < " + settings.RegimeGateMinRangeTicks.ToString("F1", CultureInfo.InvariantCulture) + "t over 5s)"; }
                                }
                                if (!regimeGated && settings.RegimeGateMaxNovelty > 0 && noveltyScore > settings.RegimeGateMaxNovelty)
                                { regimeGated = true; regimeReason = "regime transition (novelty " + noveltyScore.ToString("F2", CultureInfo.InvariantCulture) + " > " + settings.RegimeGateMaxNovelty.ToString("F2", CultureInfo.InvariantCulture) + ")"; }
                                if (regimeGated) paperSignal = "NO-TRADE";
                            }
                            double signalProbability = decisionProbability;
                            if (paperSignal == "LONG") paperLongSignals++;
                            else if (paperSignal == "SHORT") paperShortSignals++;
                            else if (paperSignal == "NO-TRADE") paperNoTradeSignals++;
                            if (paperSignal != lastPaperSignal)
                            {
                                // Score this new signal against actual price movement.
                                if (paperSignal == "LONG") paperOutcomeTracker.OnSignal(1, twoTickMid, frame.UtcTicks, profitTargetTicks);
                                else if (paperSignal == "SHORT") paperOutcomeTracker.OnSignal(-1, twoTickMid, frame.UtcTicks, profitTargetTicks);
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "paper-signal={0} current-up-probability={1:F3} trained={2} rule=long>={3:F2} short<={4:F2} min-labels=100",
                                    paperSignal, signalProbability, trained, settings.PaperLongThreshold, settings.PaperShortThreshold));
                                if (shadowVetoed)
                                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                        "shadow-veto: v12 wanted a trade but cross-market/fusion opposed it (fusion={0:F3} vx-risk={1}) — stood aside.",
                                        fusionProbability, vxRisk ? 1 : 0));
                                if (regimeGated)
                                    Console.WriteLine("regime-gate: stood aside — " + regimeReason + ".");
                                lastPaperSignal = paperSignal;
                            }
                            // Direct order emission (fast path). Evaluated every
                            // frame so hold/gap timers advance; acts only when the
                            // signal it would send differs from the last one sent.
                            if (orderRing != null && (paperSignal == "LONG" || paperSignal == "SHORT" || paperSignal == "NO-TRADE"))
                            {
                                long nowT = frame.UtcTicks;
                                if (paperSignal != orderObservedSignal) { orderObservedSignal = paperSignal; orderSignalSinceUtc = nowT; }
                                bool changed = paperSignal != orderActedSignal;
                                bool held = orderHoldTicks <= 0 || (nowT - orderSignalSinceUtc) >= orderHoldTicks;
                                bool isEntry = paperSignal == "LONG" || paperSignal == "SHORT";
                                bool gapOk = !isEntry || orderGapTicks <= 0 || orderLastEntryUtc == 0 || (nowT - orderLastEntryUtc) >= orderGapTicks;
                                // Hog agreement gate (off by default): ENTRY orders need fresh hog
                                // evidence that points the same way as the engine. Flattens are never gated.
                                bool hogGateOk = true;
                                if (settings.HogAgreeGate && isEntry)
                                {
                                    bool agree = hogFresh && !double.IsNaN(hogPUpNow) &&
                                        Math.Sign(hogPUpNow - 0.5) == Math.Sign(enginePUp - 0.5) && Math.Abs(hogPUpNow - 0.5) > 1e-9;
                                    if (!agree)
                                    {
                                        hogGateOk = false;
                                        string reason = !hogFresh ? "no fresh hog evidence" : "hog disagrees";
                                        // log on category change, at most once per 5 s (counts are in the 1 Hz hog-fusion line)
                                        if (reason != hogGateLastReason || nowT - hogGateLastLogUtc >= 5 * TimeSpan.TicksPerSecond)
                                        {
                                            hogGateLastReason = reason; hogGateLastLogUtc = nowT;
                                            Console.WriteLine("hog-gate: blocked " + paperSignal + " entry - " + reason + " (p-hog=" + (double.IsNaN(hogPUpNow) ? "n/a" : hogPUpNow.ToString("F3", CultureInfo.InvariantCulture)) + " p-engine=" + enginePUp.ToString("F3", CultureInfo.InvariantCulture) + ")");
                                        }
                                    }
                                    else hogGateLastReason = "";
                                }
                                if (changed && held && gapOk && !hogGateOk) hogGateBlocked++;
                                if (changed && held && gapOk && hogGateOk)
                                {
                                    int op;
                                    if (paperSignal == "LONG") op = orderJoinMode ? 30 : 0;
                                    else if (paperSignal == "SHORT") op = orderJoinMode ? 31 : 1;
                                    else op = 13; // NO-TRADE -> flatten
                                    if (isEntry) orderRing.WriteSignalContext(probability);
                                    orderRing.Write(op, settings.OrderQty);
                                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                        "engine-order signal={0} op={1} qty={2} mode={3} (direct-to-ring)",
                                        paperSignal, op, settings.OrderQty, orderJoinMode ? "join" : "market"));
                                    orderActedSignal = paperSignal;
                                    if (isEntry) orderLastEntryUtc = nowT;
                                }
                            }
                            int paperMaxOpen = Math.Max(1, settings.PaperMaxOpen);
                            if (settings.PaperExternal)
                            {
                                // Paper mirror: entries come from the dashboard's AI Trader (same guards as
                                // a live order). A flatten also drops any entry still inside its latency.
                                int cmd = paperSimulation.PendingCommand;
                                if (cmd != 0)
                                {
                                    paperSimulation.PendingCommand = 0;
                                    if (cmd == 3) { pendingPaperEntries.Clear(); Console.WriteLine("paper-command flatten open=" + openPaperTrades.Count); }
                                    else if (!paperSimulation.Enabled) Console.WriteLine("paper-command ignored reason=paper-sim-disabled");
                                    else if (openPaperTrades.Count + pendingPaperEntries.Count >= 1) Console.WriteLine("paper-command ignored reason=already-in-trade (one at a time)");
                                    else
                                    {
                                        long eligible = paperUtcTicks + (long)settings.PaperEntryLatencyMs * TimeSpan.TicksPerMillisecond;
                                        pendingPaperEntries.Add(new long[] { cmd == 1 ? 1 : 0, eligible });
                                        Console.WriteLine("paper-command accepted direction=" + (cmd == 1 ? "LONG" : "SHORT") + " fills-at-touch-in-ms=" + settings.PaperEntryLatencyMs);
                                    }
                                }
                            }
                            // 1) Enqueue a pending entry if a slot (open + pending) is free.
                            else if (paperSimulation.Enabled && (paperSignal == "LONG" || paperSignal == "SHORT") &&
                                openPaperTrades.Count + pendingPaperEntries.Count < paperMaxOpen)
                            {
                                long eligible = paperUtcTicks + (long)settings.PaperEntryLatencyMs * TimeSpan.TicksPerMillisecond;
                                pendingPaperEntries.Add(new long[] { paperSignal == "LONG" ? 1 : 0, eligible });
                            }
                            // 2) Promote pending entries whose latency delay has elapsed —
                            //    fill at the touch AS IT IS NOW (models the signal->fill lag).
                            for (int q = pendingPaperEntries.Count - 1; q >= 0; q--)
                            {
                                if (openPaperTrades.Count >= paperMaxOpen) break;
                                if (paperUtcTicks < pendingPaperEntries[q][1]) continue;
                                bool isLong = pendingPaperEntries[q][0] == 1;
                                double entryPrice = isLong ? frame.Ask[0] : frame.Bid[0];
                                PaperTrade opened = new PaperTrade(isLong, entryPrice, frames, paperUtcTicks, paperDeadlineUtcTicks, profitTargetTicks);
                                if (settings.PaperExternal && (settings.PaperStopTicks > 0 || settings.PaperTargetTicks > 0 || settings.PaperTrailTicks > 0))
                                { opened.StopTicks = settings.PaperStopTicks; opened.PtTicks = settings.PaperTargetTicks; opened.TrailTicks = settings.PaperTrailTicks; }
                                openPaperTrades.Add(opened);
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "paper-trade-open direction={0} entry-price={1:F6} target-ticks={2:F2} deadline-ms={3} entry-latency-ms={4} exit-check=every-valid-dom-snapshot (internal simulation only){5}",
                                    isLong ? "LONG" : "SHORT", entryPrice, profitTargetTicks, dynamicHorizonMilliseconds, settings.PaperEntryLatencyMs,
                                    opened.StopTicks > 0 || opened.PtTicks > 0 || opened.TrailTicks > 0
                                        ? string.Format(CultureInfo.InvariantCulture, " bracket stop={0:F2} target={1:F2} trail={2:F2} (mirrors the strategy, no time limit)", opened.StopTicks, opened.PtTicks, opened.TrailTicks) : ""));
                                pendingPaperEntries.RemoveAt(q);
                            }

                            if (lastSummaryUtcTicks == 0 || frame.UtcTicks - lastSummaryUtcTicks >= TimeSpan.TicksPerSecond)
                            {
                                lastSummaryUtcTicks = frame.UtcTicks;
                                double onlineAccuracy = labeled > 0 ? 100.0 * correct / labeled : 0.0;
                                double majorityBaseline = labeled > 0 ? 100.0 * Math.Max(upLabels, downLabels) / labeled : 0.0;
                                double upPredictionRate = predictions > 0 ? 100.0 * upPredictions / predictions : 0.0;
                                double downPredictionRate = predictions > 0 ? 100.0 * downPredictions / predictions : 0.0;
                                double validationAccuracy = validationResults.Count > 0 ? 100.0 * validationCorrect / validationResults.Count : 0.0;
                                double validationBaseline = validationResults.Count > 0 ? 100.0 * Math.Max(validationUpLabels, validationDownLabels) / validationResults.Count : 0.0;
                                double validationEdge = validationAccuracy - validationBaseline;
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "frames={0} valid={1} trained={2} no-trade={3} predictions={4} up-predictions={5} up-prediction-rate={6:F2}% down-predictions={7} down-prediction-rate={8:F2}% up-labels={9} down-labels={10} online-label-accuracy={11:F2}% majority-baseline={12:F2}% current-up-probability={13:F3} weight-l2={14:F4} paper-signal={15} paper-long-signals={16} paper-short-signals={17} paper-no-trade-signals={18} dynamic-horizon-ms={19} spread-ticks={20:F2} estimated-cost-ticks={21:F2} profit-target-ticks={22:F2} paper-trades={23} paper-wins={24} paper-losses={25} paper-win-rate={26:F2}% paper-net-ticks={27:F2} paper-max-drawdown-ticks={28:F2} paper-open={29} paper-sim-enabled={30} processed-frames={31} performance-skipped-frames={32} processing-interval-ms={33} validation-samples={34} validation-window={35} validation-accuracy={36:F2}% validation-baseline={37:F2}% validation-edge={38:F2}% best-validation-edge={39:F2}% invalid-books={40} locked-books={41} corrupt-books={42} latency-frames={43} latency-predictions={44} feed-event-age-ms={45} callback-to-publish-ms={46} publish-to-receive-ms={47} receiver-to-prediction-ms={48} callback-to-prediction-ms={49} label-latency-ms={50} latency-armed-labels={51} ring-backlog-events={52}",
                                    frames, valid, trained, skipped, predictions, upPredictions, upPredictionRate, downPredictions, downPredictionRate, upLabels, downLabels, onlineAccuracy, majorityBaseline, probability, model.WeightL2(), paperSignal, paperLongSignals, paperShortSignals, paperNoTradeSignals, dynamicHorizonMilliseconds, spreadTicks, settings.CostTicks, profitTargetTicks, completedPaperTrades, paperWins, paperLosses, completedPaperTrades > 0 ? 100.0 * paperWins / completedPaperTrades : 0.0, paperNetTicks, paperMaxDrawdownTicks, openPaperTrades.Count, paperSimulation.Enabled ? 1 : 0, processedFrames, performanceSkippedFrames, settings.MinProcessingIntervalMilliseconds, validationResults.Count, ValidationWindow, validationAccuracy, validationBaseline, validationEdge, double.IsNegativeInfinity(bestValidationEdge) ? 0.0 : bestValidationEdge, invalidBooks, lockedBooks, corruptBooks, publishToReceiveLatency.Count, callbackToPredictionLatency.Count, feedEventAgeLatency.Percentiles(), callbackToPublishLatency.Percentiles(), publishToReceiveLatency.Percentiles(), receiveToPredictionLatency.Percentiles(), callbackToPredictionLatency.Percentiles(), settings.LabelLatencyMs, labelsArmedWithLatency, directSource != null ? directSource.Backlog.Percentiles() : "n/a"));
                                { string jl = jitter.Line(); if (jl != null) Console.WriteLine(jl); }
                                if (settings.EnableReversalTracker) { Console.WriteLine(reversalTracker.Summary()); Console.WriteLine(reversalFusion.Summary()); }
                                if (hogFusion != null) hogFusion.Save(hogFusionPath); // 1 s silent checkpoint of the fusion head (tiny file)
                                if (hogFusion != null) Console.WriteLine(hogFusion.Line(settings.HogFusionMode, hogEvidenceState, hogAgeMsNow,
                                    double.IsNaN(hogPUpNow) ? 0.5 : hogPUpNow, enginePUp, double.IsNaN(hogPFusedNow) ? enginePUp : hogPFusedNow, settings.HogAgreeGate, hogGateBlocked));
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "absorption-shadow mes-direction=0 es-direction={0} score={1:F3} state={2}",
                                    esMarket != null ? esMarket.AbsorptionDirection : 0, shadowAbsorptionScore,
                                    shadowAbsorptionDirection == 0 ? "NONE" : shadowAbsorptionDirection > 0 ? "SELL_ABSORPTION" : "BUY_ABSORPTION"));
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "lag-shadow es={0:F3} nq={1:F3} spy={2:F3} vx={3:F3} direction={4} score={5:F3} vx-risk={6}",
                                    esMarket != null ? esMarket.MomentumTicks : 0, nqMarket != null ? nqMarket.MomentumTicks : 0,
                                    spyMarket != null ? spyMarket.MomentumTicks : 0, vxMarket != null ? vxMarket.MomentumTicks : 0,
                                    lagDirection, lagScore, vxRisk ? 1 : 0));
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "fusion-shadow probability={0:F4} updates={1} mode={2}",
                                    fusionProbability, fusionHead.Updates, settings.SignalMode));
                                string noveltyState = unfamiliarRegimeActive ? "UNFAMILIAR" :
                                    noveltyScore >= 1.7 || noveltyExtremeFeatures >= 3 ? "ELEVATED" : "NORMAL";
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "market-novelty score={0:F3} extreme-features={1} state={2} (diagnostic only; primary inference continues)",
                                    noveltyScore, noveltyExtremeFeatures, noveltyState));
                                if (lastCheckpointUtcTicks == 0 ||
                                    frame.UtcTicks - lastCheckpointUtcTicks >= AutomaticCheckpointIntervalMilliseconds * TimeSpan.TicksPerMillisecond)
                                {
                                    if (hogFusion != null) hogFusion.Save(hogFusionPath);
                                    if (SaveCheckpoint(settings.CheckpointFile, standardizer, model))
                                        lastCheckpointUtcTicks = frame.UtcTicks;
                                }
                                if (TryAdoptTeacherCandidate(settings.TeacherCandidateFile, ref teacherCandidateVersion, model))
                                    Console.WriteLine("compact-teacher candidate adopted at processed-frame=" + processedFrames);
                            }
                        }
                        Console.WriteLine("Bridge disconnected; waiting to reconnect.");
                    }
                }
            }
            catch (Exception fatalError)
            {
                // Surface the real reason the receiver stopped instead of a
                // silent exit; the dashboard shows lines starting with ERROR.
                Console.Error.WriteLine("ERROR fatal engine exception: " + fatalError);
            }
            finally
            {
                SaveCheckpoint(settings.CheckpointFile, standardizer, model);
                try { fusionHead.Save(fusionPath); } catch { }
                try { if (eventRecorder != null) { eventRecorder.Dispose(); Console.WriteLine("DOM event recording closed: " + eventRecorder.Written + " events."); } } catch { }
                try { if (hogFusion != null) hogFusion.Save(hogFusionPath); if (hogEvidenceReader != null) hogEvidenceReader.Dispose(); } catch { }
                if (paperTradeLog != null) paperTradeLog.Dispose();
                if (weightUpdateLog != null) weightUpdateLog.Dispose();
                if (learningLog != null) learningLog.Dispose();
                if (teacherExamples != null) teacherExamples.Dispose();
                if (capture != null) capture.Dispose();
            }
        }

        private static void WriteTeacherExample(StreamWriter writer, long utcTicks, double label, PendingExample example)
        {
            if (writer == null || example == null || example.Features == null || example.Features.Length != ModelFeatures) return;
            StringBuilder row = new StringBuilder(5000);
            row.Append(utcTicks.ToString(CultureInfo.InvariantCulture));
            row.Append(',').Append(label.ToString("R", CultureInfo.InvariantCulture));
            row.Append(',').Append(example.PredictedProbability.ToString("R", CultureInfo.InvariantCulture));
            row.Append(',');
            for (int index = 0; index < example.Features.Length; index++)
            {
                if (index > 0) row.Append(';');
                row.Append(example.Features[index].ToString("R", CultureInfo.InvariantCulture));
            }
            writer.WriteLine(row.ToString());
        }

        // The Python trainer publishes this small text contract only after a
        // chronological held-out check beats its label baseline.  A bad or
        // partial file is ignored, so the live head keeps its prior weights.
        private static bool TryAdoptTeacherCandidate(string path, ref long acceptedVersion, NestedTemporalModel model)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 7 || lines[0].Trim() != "LNN_COMPACT_TEMPORAL_CANDIDATE_V1") return false;
                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 1; index < lines.Length; index++)
                {
                    int separator = lines[index].IndexOf('=');
                    if (separator > 0) values[lines[index].Substring(0, separator).Trim()] = lines[index].Substring(separator + 1).Trim();
                }
                long version; double bias; double edge;
                string approved, versionText, edgeText, biasText, packedWeights;
                if (!values.TryGetValue("approved", out approved) || approved != "1" ||
                    !values.TryGetValue("version", out versionText) || !long.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version) || version <= acceptedVersion ||
                    !values.TryGetValue("edge_percent", out edgeText) || !double.TryParse(edgeText, NumberStyles.Float, CultureInfo.InvariantCulture, out edge) || edge <= 0.0 ||
                    !values.TryGetValue("bias", out biasText) || !double.TryParse(biasText, NumberStyles.Float, CultureInfo.InvariantCulture, out bias) ||
                    !values.TryGetValue("weights", out packedWeights)) return false;
                string[] fields = packedWeights.Split(new char[] { ';' });
                if (fields.Length != ModelFeatures) return false;
                double[] weights = new double[ModelFeatures];
                for (int index = 0; index < weights.Length; index++)
                    if (!double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out weights[index])) return false;
                model.ReplaceHead(bias, weights);
                acceptedVersion = version;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "compact-teacher adopted version={0} heldout-edge={1:F3}%", version, edge));
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static void WriteLearningLog(StreamWriter writer, long update, PendingExample example, long labelFrame,
            long labelUtcTicks, bool labelUp, double longNetTicks, double shortNetTicks, bool learned)
        {
            if (writer == null) return;
            string originalDirection = example.PredictedUp ? "UP" : "DOWN";
            string label = labelUp ? "LONG" : "SHORT";
            string reason = labelUp ? "long_net_ticks_reached_profit_target" : "short_net_ticks_reached_profit_target";
            long dynamicHorizonMilliseconds = example.HorizonTicks / TimeSpan.TicksPerMillisecond;
            long actualHorizonMilliseconds = Math.Max(0, (labelUtcTicks - example.EntryUtcTicks) / TimeSpan.TicksPerMillisecond);
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:O},{1},{2},{3},{4},{5},{6:F6},{7},{8},{9:F4},{10:F4},{11:F4},{12:F4},{13:F4},{14},{15}",
                DateTime.UtcNow, update, example.FrameIndex, labelFrame, dynamicHorizonMilliseconds, actualHorizonMilliseconds,
                example.PredictedProbability, originalDirection, label, longNetTicks, shortNetTicks, example.EntrySpreadTicks,
                example.EstimatedCostTicks, example.ProfitTargetTicks, reason,
                learned ? "updated_222_current_fast_slow_static_and_order_flow_features" : "validation_only_no_weight_update"));
        }

        private static void AddValidationResult(List<ValidationResult> results, ValidationResult result,
            ref long correct, ref long upLabels, ref long downLabels)
        {
            results.Add(result);
            if (result.LabelUp) upLabels++; else downLabels++;
            if (result.LabelUp == result.PredictedUp) correct++;
            while (results.Count > ValidationWindow)
            {
                ValidationResult removed = results[0]; results.RemoveAt(0);
                if (removed.LabelUp) upLabels--; else downLabels--;
                if (removed.LabelUp == removed.PredictedUp) correct--;
            }
        }

        private static void WriteWeightUpdateLog(StreamWriter writer, long update, PendingExample example, long labelFrame,
            bool labelUp, WeightChange[] changes)
        {
            if (writer == null || changes == null) return;
            string utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            string label = labelUp ? "LONG" : "SHORT";
            string reason = labelUp ? "long_net_ticks_reached_profit_target" : "short_net_ticks_reached_profit_target";
            for (int i = 0; i < changes.Length; i++)
            {
                WeightChange change = changes[i];
                string kind, state, featureName;
                int rawIndex;
                if (change.Index < 0)
                {
                    kind = "bias";
                    state = "bias";
                    rawIndex = -1;
                    featureName = "bias";
                }
                else
                {
                    kind = "weight";
                    state = TemporalStateName(change.Index);
                    rawIndex = change.Index % RawFeatures;
                    featureName = RawFeatureName(rawIndex);
                }
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4:F6},{5},{6},{7},{8},{9},{10},{11},{12:F9},{13:F12},{14:F12},{15:F12},{16:F12},{17:F12}",
                    utc, update, example.FrameIndex, labelFrame, example.PredictedProbability, label, reason, kind,
                    change.Index, state, rawIndex, featureName, change.InputValue, change.WeightBefore,
                    change.DataGradient, change.RegularizationGradient, change.WeightDelta, change.WeightAfter));
            }
            // Buffered audit output is flushed in batches by the caller and on
            // orderly shutdown.  Flushing 223 rows on every update caused disk
            // stalls that are not appropriate for a live receiver.
        }

        private static string TemporalStateName(int featureIndex)
        {
            if (featureIndex < RawFeatures) return "current";
            if (featureIndex < 2 * RawFeatures) return "fast_ema";
            return "slow_ema";
        }

        private static string RawFeatureName(int rawIndex)
        {
            if (rawIndex < 10) return "bid_price_relative_ticks_l" + (rawIndex + 1);
            if (rawIndex < 20) return "ask_price_relative_ticks_l" + (rawIndex - 9);
            if (rawIndex < 30) return "bid_log_volume_normalized_l" + (rawIndex - 19);
            if (rawIndex < 40) return "ask_log_volume_normalized_l" + (rawIndex - 29);
            if (rawIndex == 40) return "ten_level_depth_imbalance";
            if (rawIndex == 41) return "spread_ticks";
            if (rawIndex < 52) return "bid_log_volume_delta_l" + (rawIndex - 41);
            if (rawIndex < 62) return "ask_log_volume_delta_l" + (rawIndex - 51);
            switch (rawIndex)
            {
                case 62: return "bid_depth_net_change";
                case 63: return "ask_depth_net_change";
                case 64: return "imbalance_change_top_1";
                case 65: return "imbalance_change_top_3";
                case 66: return "imbalance_change_top_5";
                case 67: return "imbalance_change_top_10";
                case 68: return "bid_added_liquidity_rate";
                case 69: return "bid_removed_liquidity_rate";
                case 70: return "ask_added_liquidity_rate";
                case 71: return "ask_removed_liquidity_rate";
                case 72: return "microprice_vs_midpoint_ticks";
                case 73: return "microprice_change_ticks";
                default: return "unknown";
            }
        }

        private static void UpdateMarketDynamics(long eventUtcTicks, double midpoint, double tickSize,
            ref long previousEventUtcTicks, ref double previousMidpoint, ref double eventsPerSecond, ref double volatilityTicks)
        {
            if (previousEventUtcTicks > 0 && eventUtcTicks > previousEventUtcTicks)
            {
                double elapsedSeconds = (eventUtcTicks - previousEventUtcTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 0.0 && elapsedSeconds <= 5.0)
                {
                    double instantaneousRate = 1.0 / elapsedSeconds;
                    eventsPerSecond = eventsPerSecond == 0.0 ? instantaneousRate : 0.95 * eventsPerSecond + 0.05 * instantaneousRate;
                }
            }
            if (previousMidpoint != 0.0)
            {
                double movementTicks = Math.Abs(midpoint - previousMidpoint) / tickSize;
                volatilityTicks = 0.95 * volatilityTicks + 0.05 * movementTicks;
            }
            previousEventUtcTicks = eventUtcTicks;
            previousMidpoint = midpoint;
        }

        private static int GetDynamicHorizonMilliseconds(int targetEvents, double eventsPerSecond, int minimumMilliseconds, int maximumMilliseconds)
        {
            double milliseconds = eventsPerSecond > 0.0 ? 1000.0 * targetEvents / eventsPerSecond : 500.0;
            return (int)Math.Round(Clip(milliseconds, minimumMilliseconds, maximumMilliseconds));
        }

        private static bool TryLoadCheckpoint(string path, OnlineStandardizer standardizer, NestedTemporalModel model)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != CheckpointMagic || reader.ReadUInt16() != CheckpointVersion ||
                        reader.ReadInt32() != RawFeatures || reader.ReadInt32() != ModelFeatures)
                        throw new InvalidDataException("Checkpoint format does not match this learner.");
                    standardizer.Load(reader);
                    model.Load(reader);
                }
                Console.WriteLine("Loaded learned weights and normalizer from " + path + "; temporal state reset for the new session.");
                return true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Checkpoint was not loaded: " + error.Message);
                return false;
            }
        }

        private static bool SaveCheckpoint(string path, OnlineStandardizer standardizer, NestedTemporalModel model)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(CheckpointMagic);
                    writer.Write(CheckpointVersion);
                    writer.Write(RawFeatures);
                    writer.Write(ModelFeatures);
                    standardizer.Save(writer);
                    model.Save(writer);
                }
                return true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Checkpoint was not saved: " + error.Message);
                return false;
            }
        }

        private static string BestScoreFile(string bestCheckpointFile)
        {
            return string.IsNullOrWhiteSpace(bestCheckpointFile) ? null : bestCheckpointFile + ".score";
        }

        private static double LoadBestValidationEdge(string bestCheckpointFile)
        {
            string scoreFile = BestScoreFile(bestCheckpointFile);
            if (string.IsNullOrWhiteSpace(scoreFile) || !File.Exists(scoreFile)) return double.NegativeInfinity;
            try
            {
                string[] parts = File.ReadAllText(scoreFile).Trim().Split(new char[] { ',' });
                double value;
                if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    Console.WriteLine("Existing validated-best score=" + value.ToString("F2", CultureInfo.InvariantCulture) + "% from " + scoreFile);
                    return value;
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Validated-best score was not loaded: " + error.Message);
            }
            return double.NegativeInfinity;
        }

        private static void SaveBestValidationRecord(string bestCheckpointFile, double accuracy, double baseline,
            double edge, int samples)
        {
            string scoreFile = BestScoreFile(bestCheckpointFile);
            if (string.IsNullOrWhiteSpace(scoreFile)) return;
            try
            {
                File.WriteAllText(scoreFile, string.Format(CultureInfo.InvariantCulture,
                    "{0:F12},{1:F12},{2:F12},{3},{4:O}", edge, accuracy, baseline, samples, DateTime.UtcNow));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Validated-best score was not saved: " + error.Message);
            }
        }

        private sealed class Frame
        {
            internal ulong Sequence;
            internal long UtcTicks;
            internal long CallbackUtcTicks;
            internal long PipePublishUtcTicks;
            internal bool HasLatencyTimestamps;
            internal readonly double[] Bid = new double[DepthLevels];
            internal readonly double[] Ask = new double[DepthLevels];
            internal readonly long[] BidVolume = new long[DepthLevels];
            internal readonly long[] AskVolume = new long[DepthLevels];
        }

        private enum BookState
        {
            Valid,
            TemporarilyNonTradeable,
            Corrupt
        }

        private static bool TryDecode(byte[] buffer, out Frame frame)
        {
            frame = null;
            ushort version = BitConverter.ToUInt16(buffer, 4);
            if (BitConverter.ToUInt32(buffer, 0) != Magic || (version != LegacyVersion && version != InstrumentedVersion) ||
                (BitConverter.ToUInt16(buffer, 6) & FullBookFlag) == 0)
                return false;
            frame = new Frame();
            frame.Sequence = BitConverter.ToUInt64(buffer, 8);
            frame.UtcTicks = BitConverter.ToInt64(buffer, 16);
            int bookOffset;
            if (version == InstrumentedVersion)
            {
                frame.CallbackUtcTicks = BitConverter.ToInt64(buffer, 24);
                frame.PipePublishUtcTicks = BitConverter.ToInt64(buffer, 32);
                frame.HasLatencyTimestamps = frame.CallbackUtcTicks > 0 && frame.PipePublishUtcTicks >= frame.CallbackUtcTicks;
                bookOffset = 72;
            }
            else
            {
                // Keep the learner usable with an existing version-1 bridge.
                // Its event timestamp alone cannot provide a trustworthy local
                // callback/publish breakdown, so the latency panel stays blank.
                bookOffset = 56;
            }
            for (int i = 0; i < DepthLevels; i++)
            {
                int bidOffset = bookOffset + 16 * i;
                int askOffset = bookOffset + 160 + 16 * i;
                frame.Bid[i] = BitConverter.ToDouble(buffer, bidOffset);
                frame.BidVolume[i] = BitConverter.ToInt64(buffer, bidOffset + 8);
                frame.Ask[i] = BitConverter.ToDouble(buffer, askOffset);
                frame.AskVolume[i] = BitConverter.ToInt64(buffer, askOffset + 8);
            }
            return true;
        }

        // ── Direct shared-memory frame source ──────────────────────────
        // Reads the LnnDomMmfWriter event ring (64B header + 131072 × 48B
        // seqlock slots), maintains the 10-level book, and produces the same
        // Frame objects (and raw v2 frame bytes for --save-file) that the
        // pipe transport would deliver — with one less process in the path.
        private sealed class MmfFrameSource : IDisposable
        {
            private const long RingHeaderBytes = 64;
            private const int RingSlotBytes = 48;
            private const long RingMagic = 0x4C4E4E4D4D463031L;
            private readonly string mapName;
            private System.IO.MemoryMappedFiles.MemoryMappedFile map;
            private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view;
            private EventWaitHandle wake;
            private long readSequence;
            private int capacity;
            private ulong frameSequence;
            private volatile bool disposed;
            private readonly double[] bid = new double[DepthLevels], ask = new double[DepthLevels];
            private readonly long[] bidVolume = new long[DepthLevels], askVolume = new long[DepthLevels];
            // Last-known size + wall-clock per level slot, for transient-gap fill.
            private readonly long[] bidLastVol = new long[DepthLevels], askLastVol = new long[DepthLevels];
            private readonly long[] bidLastVolTicks = new long[DepthLevels], askLastVolTicks = new long[DepthLevels];
            private readonly bool emitPerEvent;
            internal int CoalesceAboveEvents;   // per-event mode: fall back to one frame for the rest of the backlog above this depth
            private readonly long gapStalenessTicks;
            private readonly long restingDepthTicks;
            // Resting-depth filter state: the last size that has proven stable,
            // plus the current candidate size and when it was first seen.
            private readonly long[] bidStableVol = new long[DepthLevels], askStableVol = new long[DepthLevels];
            private readonly long[] bidCandVol = new long[DepthLevels], askCandVol = new long[DepthLevels];
            private readonly long[] bidCandSince = new long[DepthLevels], askCandSince = new long[DepthLevels];
            // Flicker / phantom-liquidity measurement (per-window accumulators).
            private long flickerAdds, flickerCancels, flickerTrades;
            private double flickerAddedSize, flickerCanceledSize, flickerTradedSize;
            // Per-level (per depth position) spoof accumulators, bid+ask combined.
            private readonly long[] posAdds = new long[DepthLevels], posCancels = new long[DepthLevels];
            private readonly double[] posAddedSize = new double[DepthLevels], posCanceledSize = new double[DepthLevels];

            internal DomEventRecorder Recorder;
            private long openFailures, lastOpenFailureLogUtc;
            private readonly bool domShift, domWarmup;
            // Idle watchdog: the receiver must never sit silent. Every 2 s
            // without a produced frame it reports what the ring did meanwhile.
            private long lastFrameOrReportUtc, lastReportWriteSeq;
            private long evBid, evAsk, evTrade, evAdd, evUpd, evRem, evDeep, evBadPos;   // since last report
            private long lastEventTimeTicks, lastCallbackTicks; private byte lastSide, lastOp; private short lastPos; private double lastPrice;
            internal long FramesProduced;
            // Ring backlog: events already published but not yet consumed at the
            // moment a frame is produced. 0-1 = keeping up; tens = queueing inside
            // bursts (the tail of callback->publish latency is that queue).
            internal readonly RollingLatency Backlog = new RollingLatency();
            private long lastBacklog;
            internal MmfFrameSource(string name, bool emitPerEvent, int gapStalenessMs, int restingDepthMs)
                : this(name, emitPerEvent, gapStalenessMs, restingDepthMs, true, true) { }
            internal MmfFrameSource(string name, bool emitPerEvent, int gapStalenessMs, int restingDepthMs, bool shift, bool warmup)
            {
                mapName = name;
                this.emitPerEvent = emitPerEvent;
                gapStalenessTicks = Math.Max(0, gapStalenessMs) * TimeSpan.TicksPerMillisecond;
                restingDepthTicks = Math.Max(0, restingDepthMs) * TimeSpan.TicksPerMillisecond;
                domShift = shift; domWarmup = warmup;
            }

            // NT8 Operation.Add: insert a row at 'position', pushing deeper rows
            // down (row 9 falls off). Every per-level array shifts with the row.
            private void InsertLevel(int position, double[] px, long[] vol, long[] lastVol, long[] lastVolTicks, long[] stable, long[] cand, long[] since)
            {
                for (int i = DepthLevels - 1; i > position; i--)
                {
                    px[i] = px[i - 1]; vol[i] = vol[i - 1]; lastVol[i] = lastVol[i - 1]; lastVolTicks[i] = lastVolTicks[i - 1];
                    stable[i] = stable[i - 1]; cand[i] = cand[i - 1]; since[i] = since[i - 1];
                }
            }
            // NT8 Operation.Remove: delete the row at 'position', pulling deeper
            // rows up (row 9 becomes empty).
            private void RemoveLevel(int position, double[] px, long[] vol, long[] lastVol, long[] lastVolTicks, long[] stable, long[] cand, long[] since)
            {
                for (int i = position; i < DepthLevels - 1; i++)
                {
                    px[i] = px[i + 1]; vol[i] = vol[i + 1]; lastVol[i] = lastVol[i + 1]; lastVolTicks[i] = lastVolTicks[i + 1];
                    stable[i] = stable[i + 1]; cand[i] = cand[i + 1]; since[i] = since[i + 1];
                }
                int last = DepthLevels - 1;
                px[last] = 0; vol[last] = 0; lastVol[last] = 0; lastVolTicks[last] = 0; stable[last] = 0; cand[last] = 0; since[last] = 0;
            }

            // Applies one depth event to the local book. Returns the size delta
            // for flicker accounting (+ added, - pulled).
            private long ApplyDepthEvent(byte side, byte operation, short position, double price, long volume, long nowTicks)
            {
                double[] px = side == 0 ? bid : ask; long[] vol = side == 0 ? bidVolume : askVolume;
                long[] lastVol = side == 0 ? bidLastVol : askLastVol; long[] lastVolTicks = side == 0 ? bidLastVolTicks : askLastVolTicks;
                long[] stable = side == 0 ? bidStableVol : askStableVol; long[] cand = side == 0 ? bidCandVol : askCandVol; long[] since = side == 0 ? bidCandSince : askCandSince;
                long newVol = operation == 2 ? 0 : Math.Max(0, volume);
                long delta;
                if (domShift && operation == 0)
                {
                    // Insert: the whole size is new liquidity at this depth.
                    InsertLevel(position, px, vol, lastVol, lastVolTicks, stable, cand, since);
                    px[position] = price; vol[position] = newVol; lastVol[position] = newVol; lastVolTicks[position] = nowTicks;
                    stable[position] = 0; cand[position] = newVol; since[position] = nowTicks;
                    delta = newVol;
                }
                else if (domShift && operation == 2)
                {
                    // Delete: whatever rested at this depth is gone (a trade that
                    // consumed it was already counted on the trade side).
                    delta = -vol[position];
                    RemoveLevel(position, px, vol, lastVol, lastVolTicks, stable, cand, since);
                }
                else
                {
                    delta = newVol - vol[position];
                    if (operation == 2) { px[position] = 0; vol[position] = 0; }
                    else { px[position] = price; vol[position] = newVol; lastVol[position] = newVol; lastVolTicks[position] = nowTicks; }
                }
                return delta;
            }

            // On attach, replay whatever history the ring still holds so the
            // book is known immediately (and correctly, if the history reaches
            // back to the writer's initial snapshot) instead of waiting for
            // every level to be touched by a fresh event.
            private void WarmFromRing(long writeSequence)
            {
                long available = Math.Min(writeSequence, capacity - 1);
                if (available <= 0) return;
                long start = writeSequence - available, applied = 0, skipped = 0;
                long nowTicks = UtcNowTicks();
                for (long next = start + 1; next <= writeSequence; next++)
                {
                    long offset = RingHeaderBytes + (next & (capacity - 1)) * RingSlotBytes;
                    long first = view.ReadInt64(offset);
                    if ((first & 1) != 0 || first != next * 2) { skipped++; continue; } // torn or already lapped: skip
                    double price = view.ReadDouble(offset + 24);
                    long volume = view.ReadInt64(offset + 32);
                    byte side = view.ReadByte(offset + 40);
                    byte operation = view.ReadByte(offset + 41);
                    short position = view.ReadInt16(offset + 42);
                    long second = view.ReadInt64(offset);
                    if (second != first) { skipped++; continue; }
                    if (side < 2 && position >= 0 && position < DepthLevels) { ApplyDepthEvent(side, operation, position, price, volume, nowTicks); applied++; }
                }
                Console.WriteLine("direct-mmf warmup: replayed " + applied.ToString(CultureInfo.InvariantCulture) + " depth events from ring history (" +
                    (start == 0 ? "complete since writer start" : "partial: ring holds the last " + available.ToString(CultureInfo.InvariantCulture) + " of " + writeSequence.ToString(CultureInfo.InvariantCulture)) + ")" +
                    (skipped > 0 ? " skipped=" + skipped : "") + " book bid0=" + bid[0].ToString("F2", CultureInfo.InvariantCulture) + "x" + bidVolume[0] +
                    " ask0=" + ask[0].ToString("F2", CultureInfo.InvariantCulture) + "x" + askVolume[0]);
            }

            private void ReportIdle(long writeSequence, long nowTicks)
            {
                double seconds = (nowTicks - lastFrameOrReportUtc) / (double)TimeSpan.TicksPerSecond;
                long delta = writeSequence - lastReportWriteSeq;
                string reason;
                if (delta <= 0) reason = "NO EVENTS from the DOM writer in this window: market closed / data feed stopped / indicator not on a depth-enabled MES chart / Playback paused";
                else if (evBid + evAsk == 0 && evDeep > 0) reason = "only rows deeper than 10 changed in this window";
                else if (evBid + evAsk == 0) reason = "trades only, no depth rows: the NT8 chart has no Level II (depth) subscription for this instrument";
                else if (bid[0] <= 0 || ask[0] <= 0) reason = "top-of-book row not populated yet (only deeper rows have been touched since attach)";
                else if (bid[0] >= ask[0]) reason = "book locked/crossed (bid0 >= ask0): stale snapshot or feed transition";
                else if (evBadPos > 0) reason = "depth rows with negative position (writer/engine version mismatch?)";
                else reason = "events arriving but none changed rows 0-9 (see counts)";
                string lastEvent = lastCallbackTicks > 0 && lastCallbackTicks <= DateTime.MaxValue.Ticks
                    ? " last-event=" + (lastSide == 2 ? "trade" : lastSide == 0 ? "bid" : "ask") + "/" + (lastOp == 0 ? "add" : lastOp == 1 ? "upd" : "rem") + "@" + lastPos +
                      " px=" + lastPrice.ToString("F2", CultureInfo.InvariantCulture) + " event-time=" + (lastEventTimeTicks > 0 && lastEventTimeTicks <= DateTime.MaxValue.Ticks ? new DateTime(lastEventTimeTicks).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "?") +
                      " published-age=" + ((nowTicks - lastCallbackTicks) / (double)TimeSpan.TicksPerSecond).ToString("F1", CultureInfo.InvariantCulture) + "s"
                    : " last-event=none-since-attach";
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "direct-mmf idle {0:F1}s: write-seq={1} events={2} (bid={3} ask={4} trade={5} add={6} upd={7} rem={8} deeper-than-10={9} bad-pos={10}) frames-produced={11} book bid0={12:F2}x{13} ask0={14:F2}x{15}{16} -> {17}",
                    seconds, writeSequence, delta, evBid, evAsk, evTrade, evAdd, evUpd, evRem, evDeep, evBadPos, FramesProduced, bid[0], bidVolume[0], ask[0], askVolume[0], lastEvent, reason));
                lastFrameOrReportUtc = nowTicks; lastReportWriteSeq = writeSequence;
                evBid = evAsk = evTrade = evAdd = evUpd = evRem = evDeep = evBadPos = 0;
            }

            // Returns the size to use for a level under the resting-depth filter:
            // the candidate size once it has held long enough, otherwise the last
            // stable size (so freshly-appeared/phantom size is ignored until proven).
            private long RestingVolume(long[] stable, long[] cand, long[] since, int i, long rawVol, long nowTicks)
            {
                if (restingDepthTicks <= 0) return rawVol; // filter off: raw book
                if (cand[i] != rawVol) { cand[i] = rawVol; since[i] = nowTicks; }
                if (nowTicks - since[i] >= restingDepthTicks) stable[i] = cand[i];
                return stable[i];
            }

            private bool Open()
            {
                if (view != null) return true;
                try
                {
                    // "file:<path>" opens a file-backed ring (replay / testing); otherwise
                    // the named shared-memory ring the NT8 bridge publishes.
                    if (mapName.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(mapName.Substring(5), FileMode.Open, null, 0,
                            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    else
                        map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
                            mapName, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                    view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    capacity = view.ReadInt32(8);
                    int recordSize = view.ReadInt32(12);
                    long magic = view.ReadInt64(16);
                    if (magic != RingMagic || capacity < 2 || (capacity & (capacity - 1)) != 0 || recordSize != RingSlotBytes)
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                            "ring layout mismatch in {0}: magic=0x{1:X} capacity={2} slot-bytes={3} (this engine expects magic=0x{4:X}, power-of-two capacity, slot-bytes={5}). The DOM writer in NT8 is a different version - recompile LnnDomMmfWriter from the same package.",
                            mapName, magic, capacity, recordSize, RingMagic, RingSlotBytes));
                    readSequence = view.ReadInt64(0);
                    try { wake = new EventWaitHandle(false, EventResetMode.AutoReset, mapName + "_EVENT"); } catch { wake = null; }
                    Console.WriteLine("Direct MMF connected: " + mapName + " sequence=" + readSequence.ToString(CultureInfo.InvariantCulture) +
                        " capacity=" + capacity + " slot-bytes=" + recordSize + " wake-event=" + (wake != null ? "ok" : "none"));
                    if (domWarmup) WarmFromRing(readSequence);
                    openFailures = 0;
                    lastFrameOrReportUtc = DateTime.UtcNow.Ticks; lastReportWriteSeq = readSequence;
                    return true;
                }
                catch (Exception ex)
                {
                    CloseMap();
                    // Say WHY, once immediately and then every 5 s, so a silent retry loop
                    // can never again look like "connected but no data".
                    openFailures++;
                    long nowT = DateTime.UtcNow.Ticks;
                    if (openFailures == 1 || nowT - lastOpenFailureLogUtc >= 5 * TimeSpan.TicksPerSecond)
                    {
                        lastOpenFailureLogUtc = nowT;
                        Console.WriteLine("ERROR direct-mmf open failed (attempt " + openFailures + ", retrying every 1 s): " + ex.Message +
                            (ex is FileNotFoundException ? " - the map does not exist yet: is the LnnDomMmfWriter indicator running on an MES chart with depth?" : ""));
                    }
                    return false;
                }
            }

            private void CloseMap()
            {
                try { view?.Dispose(); } catch { }
                try { map?.Dispose(); } catch { }
                view = null; map = null;
            }

            // Blocks until a valid, changed book snapshot is available.
            internal bool TryReadFrame(out Frame frame, out byte[] rawFrame)
            {
                frame = null; rawFrame = null;
                while (!disposed)
                {
                    if (view == null)
                    {
                        if (!Open()) { Thread.Sleep(1000); continue; }
                    }
                    try
                    {
                        long writeSequence = view.ReadInt64(0);
                        if (writeSequence < readSequence) readSequence = writeSequence;                    // ring reset (bridge reloaded)
                        if (writeSequence - readSequence > capacity) readSequence = writeSequence - capacity; // overrun: skip to live edge
                        lastBacklog = writeSequence - readSequence;
                        bool bookChanged = false;
                        long eventUtcTicks = 0, callbackUtcTicks = 0;
                        long nowTicks = UtcNowTicks();
                        while (readSequence < writeSequence)
                        {
                            long next = readSequence + 1;
                            long offset = RingHeaderBytes + (next & (capacity - 1)) * RingSlotBytes;
                            long first = view.ReadInt64(offset);
                            if ((first & 1) != 0) break; // writer mid-update
                            long eventTime = view.ReadInt64(offset + 8);
                            long callbackTime = view.ReadInt64(offset + 16);
                            double price = view.ReadDouble(offset + 24);
                            long volume = view.ReadInt64(offset + 32);
                            byte side = view.ReadByte(offset + 40);
                            byte operation = view.ReadByte(offset + 41);
                            short position = view.ReadInt16(offset + 42);
                            long second = view.ReadInt64(offset);
                            if (first != second || second != next * 2)
                            {
                                if (second > next * 2) { readSequence = second / 2 - 1; continue; } // lapped
                                break;
                            }
                            readSequence = next;
                            if (Recorder != null) Recorder.Add(eventTime, callbackTime, price, volume, side, operation, position);
                            lastEventTimeTicks = eventTime; lastCallbackTicks = callbackTime; lastSide = side; lastOp = operation; lastPos = position; lastPrice = price;
                            if (side == 2)
                            {
                                // Executed trade — real, un-cancelable volume.
                                flickerTradedSize += Math.Max(0, volume); flickerTrades++; evTrade++;
                            }
                            else if (position >= DepthLevels) evDeep++;   // real row, deeper than the 10 tracked
                            else if (position < 0) evBadPos++;
                            if (side < 2 && position >= 0 && position < DepthLevels)
                            {
                                if (side == 0) evBid++; else evAsk++;
                                if (operation == 0) evAdd++; else if (operation == 2) evRem++; else evUpd++;
                                // Apply the row operation (NT8 insert/remove shifts when
                                // dom-shift is on) and account added vs pulled size.
                                long deltaVol = ApplyDepthEvent(side, operation, position, price, volume, nowTicks);
                                if (deltaVol > 0) { flickerAdds++; flickerAddedSize += deltaVol; posAdds[position]++; posAddedSize[position] += deltaVol; }
                                else if (deltaVol < 0) { flickerCancels++; flickerCanceledSize += -deltaVol; posCancels[position]++; posCanceledSize[position] += -deltaVol; }
                                bookChanged = true;
                                eventUtcTicks = eventTime;
                                callbackUtcTicks = callbackTime;
                                // Per-event granularity: stop after one book change
                                // so this snapshot reflects a single event; the
                                // next call resumes at the following event.
                                if (emitPerEvent && (CoalesceAboveEvents <= 0 || writeSequence - readSequence <= CoalesceAboveEvents)) break;
                            }
                        }
                        if (bookChanged && bid[0] > 0 && ask[0] > bid[0])
                        {
                            frameSequence++;
                            Frame produced = new Frame
                            {
                                Sequence = frameSequence,
                                UtcTicks = eventUtcTicks,
                                CallbackUtcTicks = callbackUtcTicks,
                                PipePublishUtcTicks = UtcNowTicks(),
                                HasLatencyTimestamps = callbackUtcTicks > 0
                            };
                            // Fill transient holes / unpopulated deep levels so
                            // the snapshot is a valid 10-deep book (CheckBook
                            // rejects any non-positive level price). A level that
                            // vanished only briefly keeps its own last-known size
                            // (best estimate of what's really resting there);
                            // a stale or never-seen level gets the price above at
                            // zero volume, which injects no fake liquidity.
                            for (int i = 0; i < DepthLevels; i++)
                            {
                                // Resting-depth filter: substitute proven-stable
                                // size so freshly-appeared/phantom size is ignored.
                                long restedBid = RestingVolume(bidStableVol, bidCandVol, bidCandSince, i, Math.Max(0, bidVolume[i]), nowTicks);
                                long restedAsk = RestingVolume(askStableVol, askCandVol, askCandSince, i, Math.Max(0, askVolume[i]), nowTicks);
                                double b = bid[i]; long bv = restedBid;
                                if (b <= 0 && i > 0)
                                {
                                    if (bidLastVol[i] > 0 && nowTicks - bidLastVolTicks[i] <= gapStalenessTicks)
                                    { b = produced.Bid[i - 1]; bv = bidLastVol[i]; }   // transient gap → carry last size
                                    else { b = produced.Bid[i - 1]; bv = 0; }          // stale/empty → zero size
                                }
                                produced.Bid[i] = b; produced.BidVolume[i] = Math.Max(0, bv);
                                double a = ask[i]; long av = restedAsk;
                                if (a <= 0 && i > 0)
                                {
                                    if (askLastVol[i] > 0 && nowTicks - askLastVolTicks[i] <= gapStalenessTicks)
                                    { a = produced.Ask[i - 1]; av = askLastVol[i]; }
                                    else { a = produced.Ask[i - 1]; av = 0; }
                                }
                                produced.Ask[i] = a; produced.AskVolume[i] = Math.Max(0, av);
                            }
                            frame = produced;
                            rawFrame = EncodeRawFrame(produced);
                            FramesProduced++;
                            Backlog.AddTicks(0, lastBacklog * TimeSpan.TicksPerMillisecond);   // stored as "events" in the ms slot
                            lastFrameOrReportUtc = nowTicks; lastReportWriteSeq = readSequence;
                            evBid = evAsk = evTrade = evAdd = evUpd = evRem = evDeep = evBadPos = 0;
                            return true;
                        }
                        // No frame from this pass: after 2 s of that, say what the ring did.
                        if (nowTicks - lastFrameOrReportUtc >= 2 * TimeSpan.TicksPerSecond) ReportIdle(writeSequence, nowTicks);
                        // Nothing new: wait for the writer's wake event (or 1 ms).
                        if (wake != null) { try { wake.WaitOne(1); } catch { Thread.Sleep(1); } }
                        else Thread.Sleep(1);
                    }
                    catch (Exception readError)
                    {
                        // Never a silent retry loop: say what failed, then reopen.
                        long nowT = DateTime.UtcNow.Ticks;
                        if (nowT - lastOpenFailureLogUtc >= 5 * TimeSpan.TicksPerSecond)
                        {
                            lastOpenFailureLogUtc = nowT;
                            Console.WriteLine("ERROR direct-mmf read failed (closing and reopening the map): " + readError.Message);
                        }
                        CloseMap();
                        Thread.Sleep(250);
                    }
                }
                return false;
            }

            // Same 400-byte layout the pipe transport carries, so --save-file
            // recordings stay replay-compatible.
            private static byte[] EncodeRawFrame(Frame frame)
            {
                byte[] buffer = new byte[400];
                buffer[0] = 0x4C; buffer[1] = 0x4E; buffer[2] = 0x4E; buffer[3] = 0x31; // "LNN1"
                buffer[4] = 2; buffer[6] = 4;                                            // version 2, full-book flag
                Buffer.BlockCopy(BitConverter.GetBytes(frame.Sequence), 0, buffer, 8, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(frame.UtcTicks), 0, buffer, 16, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(frame.CallbackUtcTicks), 0, buffer, 24, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(frame.PipePublishUtcTicks), 0, buffer, 32, 8);
                for (int i = 0; i < DepthLevels; i++)
                {
                    int bidOffset = 72 + 16 * i, askOffset = 72 + 160 + 16 * i;
                    Buffer.BlockCopy(BitConverter.GetBytes(frame.Bid[i]), 0, buffer, bidOffset, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(frame.BidVolume[i]), 0, buffer, bidOffset + 8, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(frame.Ask[i]), 0, buffer, askOffset, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(frame.AskVolume[i]), 0, buffer, askOffset + 8, 8);
                }
                return buffer;
            }

            // Per-second flicker summary; resets its window each call.
            internal string FlickerLine(double intervalSeconds)
            {
                double s = intervalSeconds > 0 ? intervalSeconds : 1.0;
                double addsPerSec = flickerAdds / s, cancelsPerSec = flickerCancels / s, tradesPerSec = flickerTrades / s;
                double cancelAddRatio = flickerAddedSize > 0 ? flickerCanceledSize / flickerAddedSize : 0.0;
                // Of the size that left the book, how much was CANCELED vs TRADED.
                // High = lots of posted size pulled without ever trading = phantom/spoof-like.
                double cancelWithoutTrade = (flickerCanceledSize + flickerTradedSize) > 0
                    ? 100.0 * flickerCanceledSize / (flickerCanceledSize + flickerTradedSize) : 0.0;
                string line = string.Format(CultureInfo.InvariantCulture,
                    "flicker adds-per-sec={0:F0} cancels-per-sec={1:F0} trades-per-sec={2:F0} cancel-add-ratio={3:F2} cancel-without-trade={4:F1}%",
                    addsPerSec, cancelsPerSec, tradesPerSec, cancelAddRatio, cancelWithoutTrade);
                flickerAdds = flickerCancels = flickerTrades = 0;
                flickerAddedSize = flickerCanceledSize = flickerTradedSize = 0;
                return line;
            }

            // Per-level spoof breakdown for the top 3 depth levels (bid+ask
            // combined at each position). cancel-add-ratio per level: how much
            // posted size was pulled vs added there — high = spoof-like at that
            // level. Resets its window each call.
            internal string FlickerLevelLine(double intervalSeconds)
            {
                double s = intervalSeconds > 0 ? intervalSeconds : 1.0;
                System.Text.StringBuilder sb = new System.Text.StringBuilder("flicker-levels");
                for (int i = 0; i < 3 && i < DepthLevels; i++)
                {
                    double ratio = posAddedSize[i] > 0 ? posCanceledSize[i] / posAddedSize[i] : 0.0;
                    sb.Append(string.Format(CultureInfo.InvariantCulture,
                        " L{0}-adds/s={1:F0} L{0}-cancels/s={2:F0} L{0}-cancel-add-ratio={3:F2}",
                        i + 1, posAdds[i] / s, posCancels[i] / s, ratio));
                }
                for (int i = 0; i < DepthLevels; i++) { posAdds[i] = posCancels[i] = 0; posAddedSize[i] = posCanceledSize[i] = 0; }
                return sb.ToString();
            }

            public void Dispose()
            {
                disposed = true;
                try { wake?.Set(); } catch { }
                try { wake?.Dispose(); } catch { }
                CloseMap();
            }
        }

        private static bool TryReadFrame(Stream stream, out Frame frame, out byte[] rawFrame)
        {
            frame = null;
            rawFrame = null;
            byte[] header = new byte[8];
            if (!ReadExactly(stream, header)) return false;
            if (BitConverter.ToUInt32(header, 0) != Magic) return false;
            ushort version = BitConverter.ToUInt16(header, 4);
            int frameSize = version == LegacyVersion ? LegacyFrameSize :
                version == InstrumentedVersion ? InstrumentedFrameSize : 0;
            if (frameSize == 0) return false;

            rawFrame = new byte[frameSize];
            Buffer.BlockCopy(header, 0, rawFrame, 0, header.Length);
            byte[] remainder = new byte[frameSize - header.Length];
            if (!ReadExactly(stream, remainder)) return false;
            Buffer.BlockCopy(remainder, 0, rawFrame, header.Length, remainder.Length);
            return TryDecode(rawFrame, out frame);
        }

        private static BookState CheckBook(Frame frame, double tickSize)
        {
            if (frame == null || !(tickSize > 0.0)) return BookState.Corrupt;
            if (!IsFinitePositive(frame.Bid[0]) || !IsFinitePositive(frame.Ask[0]))
                return BookState.Corrupt;
            // Locked/crossed top-of-book snapshots are unusable for labels or
            // trading simulation, but are often transient feed states. Skip
            // them without erasing valid temporal context or pending labels.
            if (frame.Bid[0] >= frame.Ask[0])
                return BookState.TemporarilyNonTradeable;
            for (int i = 0; i < DepthLevels; i++)
            {
                if (!IsFinitePositive(frame.Bid[i]) || !IsFinitePositive(frame.Ask[i]) ||
                    frame.BidVolume[i] < 0 || frame.AskVolume[i] < 0)
                    return BookState.Corrupt;
                if (i > 0)
                {
                    // Deeper bids cannot improve above the best bid and deeper
                    // asks cannot improve below the best ask.
                    if (frame.Bid[i] > frame.Bid[i - 1] + tickSize * 0.000001 ||
                        frame.Ask[i] < frame.Ask[i - 1] - tickSize * 0.000001)
                        return BookState.Corrupt;
                }
            }
            return BookState.Valid;
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static readonly double[] rawFeatureBuffer = new double[RawFeatures];
        private static readonly double[] logVolumeBuffer = new double[20];
        private static double[] MakeRawFeatures(Frame frame, Frame previousFrame, double tickSize)
        {
            // Reuses one buffer: the result is consumed within the frame
            // (novelty, standardizer, temporal state) and never retained.
            double[] values = rawFeatureBuffer; Array.Clear(values, 0, values.Length);
            double midpoint = (frame.Bid[0] + frame.Ask[0]) * 0.5;
            double volumeMean = 0.0, bidSum = 0.0, askSum = 0.0;
            double[] logVolumes = logVolumeBuffer; Array.Clear(logVolumes, 0, logVolumes.Length);
            for (int i = 0; i < DepthLevels; i++)
            {
                values[i] = (frame.Bid[i] - midpoint) / tickSize;
                values[10 + i] = (frame.Ask[i] - midpoint) / tickSize;
                logVolumes[i] = Math.Log(1.0 + Math.Max(frame.BidVolume[i], 0));
                logVolumes[10 + i] = Math.Log(1.0 + Math.Max(frame.AskVolume[i], 0));
                volumeMean += logVolumes[i] + logVolumes[10 + i];
                bidSum += frame.BidVolume[i];
                askSum += frame.AskVolume[i];
            }
            volumeMean = Math.Max(volumeMean / 20.0, 1e-6);
            for (int i = 0; i < 20; i++) values[20 + i] = logVolumes[i] / volumeMean;
            values[40] = (bidSum - askSum) / (bidSum + askSum + 1e-6);
            values[41] = (frame.Ask[0] - frame.Bid[0]) / tickSize;

            // Price-aligned order flow. A top-ten array index changes identity
            // whenever the best price moves, so find the previous volume at the
            // same price before measuring the change. Positive bid flow means
            // displayed bid liquidity was added; positive ask flow means ask
            // liquidity was added. This remains a displayed-book proxy, not a
            // complete exchange order-event feed.
            if (previousFrame == null)
                return values;

            double previousBidSum = 0.0, previousAskSum = 0.0;
            double bidAdded = 0.0, bidRemoved = 0.0, askAdded = 0.0, askRemoved = 0.0;
            double[] bidDelta = new double[DepthLevels];
            double[] askDelta = new double[DepthLevels];
            for (int i = 0; i < DepthLevels; i++)
            {
                long previousBidVolume = FindVolumeAtPrice(previousFrame.Bid, previousFrame.BidVolume, frame.Bid[i]);
                long previousAskVolume = FindVolumeAtPrice(previousFrame.Ask, previousFrame.AskVolume, frame.Ask[i]);
                double bidChange = Math.Max(frame.BidVolume[i], 0) - Math.Max(previousBidVolume, 0);
                double askChange = Math.Max(frame.AskVolume[i], 0) - Math.Max(previousAskVolume, 0);
                bidDelta[i] = bidChange;
                askDelta[i] = askChange;
                values[42 + i] = Math.Log(1.0 + Math.Max(frame.BidVolume[i], 0)) - Math.Log(1.0 + Math.Max(previousBidVolume, 0));
                values[52 + i] = Math.Log(1.0 + Math.Max(frame.AskVolume[i], 0)) - Math.Log(1.0 + Math.Max(previousAskVolume, 0));
                if (bidChange >= 0.0) bidAdded += bidChange; else bidRemoved -= bidChange;
                if (askChange >= 0.0) askAdded += askChange; else askRemoved -= askChange;
                previousBidSum += Math.Max(previousFrame.BidVolume[i], 0);
                previousAskSum += Math.Max(previousFrame.AskVolume[i], 0);
            }

            // Net depth change, imbalance changes at 1/3/5/10 levels, and the
            // added-versus-removed liquidity rates give the learner explicit
            // refill/depletion signals beyond the static DOM snapshot.
            values[62] = (bidSum - previousBidSum) / (previousBidSum + 1.0);
            values[63] = (askSum - previousAskSum) / (previousAskSum + 1.0);
            values[64] = ImbalanceChange(frame, previousFrame, 1);
            values[65] = ImbalanceChange(frame, previousFrame, 3);
            values[66] = ImbalanceChange(frame, previousFrame, 5);
            values[67] = ImbalanceChange(frame, previousFrame, 10);
            values[68] = bidAdded / (previousBidSum + 1.0);
            values[69] = bidRemoved / (previousBidSum + 1.0);
            values[70] = askAdded / (previousAskSum + 1.0);
            values[71] = askRemoved / (previousAskSum + 1.0);

            double microprice = Microprice(frame);
            double previousMicroprice = Microprice(previousFrame);
            values[72] = (microprice - midpoint) / tickSize;
            values[73] = (microprice - previousMicroprice) / tickSize;
            return values;
        }

        private static long FindVolumeAtPrice(double[] prices, long[] volumes, double targetPrice)
        {
            for (int i = 0; i < DepthLevels; i++)
                if (prices[i] == targetPrice) return volumes[i];
            return 0;
        }

        private static double ImbalanceChange(Frame current, Frame previous, int levels)
        {
            double currentBid = 0.0, currentAsk = 0.0, previousBid = 0.0, previousAsk = 0.0;
            for (int i = 0; i < levels; i++)
            {
                currentBid += Math.Max(current.BidVolume[i], 0);
                currentAsk += Math.Max(current.AskVolume[i], 0);
                previousBid += Math.Max(previous.BidVolume[i], 0);
                previousAsk += Math.Max(previous.AskVolume[i], 0);
            }
            double currentImbalance = (currentBid - currentAsk) / (currentBid + currentAsk + 1e-6);
            double previousImbalance = (previousBid - previousAsk) / (previousBid + previousAsk + 1e-6);
            return currentImbalance - previousImbalance;
        }

        private static double Microprice(Frame frame)
        {
            double bidVolume = Math.Max(frame.BidVolume[0], 0);
            double askVolume = Math.Max(frame.AskVolume[0], 0);
            double totalVolume = bidVolume + askVolume;
            if (totalVolume <= 0.0) return (frame.Bid[0] + frame.Ask[0]) * 0.5;
            return (frame.Ask[0] * bidVolume + frame.Bid[0] * askVolume) / totalVolume;
        }

        // Live shadow/fusion toggle. When Fusion is false the engine skips the
        // cross-market drain and fusion entirely and emits pure 222-feature
        // threshold signals — identical to classic v12, no shadow influence.
        private sealed class ShadowController { internal volatile bool Fusion; }

        // Scores the actual PAPER SIGNALS against real price movement — did a
        // LONG/SHORT signal's direction come true (price reached +2 ticks the
        // signaled way before 2 ticks against), measured from mid, independent
        // of any live fills or slippage. This isolates signal quality from
        // execution cost.
        private sealed class SignalOutcomeTracker
        {
            private readonly double targetTicks, tickSize;
            private readonly long timeoutTicks, latencyTicks;
            private sealed class Probe { public int Dir; public double EntryMid; public long StartUtc; public double Target; public bool Armed; }
            private readonly List<Probe> open = new List<Probe>();
            private readonly List<int> correctLong = new List<int>(), correctShort = new List<int>();
            private const int Window = 400;
            internal SignalOutcomeTracker(double targetTicks, double tickSize, int timeoutMs, long latencyTicks)
            { this.targetTicks = targetTicks; this.tickSize = tickSize; timeoutTicks = (long)timeoutMs * TimeSpan.TicksPerMillisecond; this.latencyTicks = Math.Max(0, latencyTicks); }

            internal void OnSignal(int dir, double mid, long utc, double target)
            {
                if (dir == 0) return;
                open.Add(new Probe { Dir = dir, EntryMid = mid, StartUtc = utc, Target = target > 0 ? target : targetTicks, Armed = latencyTicks <= 0 });
                if (open.Count > 4096) open.RemoveAt(0);
            }
            internal void Observe(long utc, double mid)
            {
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    Probe p = open[i];
                    if (!p.Armed)
                    {
                        // Latency-aware: reference mid is the one at signal + round trip.
                        if (utc - p.StartUtc < latencyTicks) continue;
                        p.EntryMid = mid; p.StartUtc = utc; p.Armed = true;
                    }
                    double moveTicks = (mid - p.EntryMid) / tickSize;
                    int outcome = 0;
                    if (moveTicks >= p.Target) outcome = 1;        // up-first
                    else if (moveTicks <= -p.Target) outcome = -1; // down-first
                    else if (utc - p.StartUtc >= timeoutTicks) outcome = 2;
                    if (outcome == 0) continue;
                    open.RemoveAt(i);
                    if (outcome == 2) continue; // timed out — not scored
                    bool correct = (p.Dir > 0 && outcome == 1) || (p.Dir < 0 && outcome == -1);
                    List<int> bucket = p.Dir > 0 ? correctLong : correctShort;
                    bucket.Add(correct ? 1 : 0);
                    if (bucket.Count > Window) bucket.RemoveAt(0);
                }
            }
            internal string Line()
            {
                int nL = correctLong.Count, nS = correctShort.Count, n = nL + nS;
                if (n < 10) return "paper-outcome signals-scored=" + n + " (accumulating)";
                int cL = 0; foreach (int v in correctLong) cL += v;
                int cS = 0; foreach (int v in correctShort) cS += v;
                double longHit = nL > 0 ? 100.0 * cL / nL : 0;
                double shortHit = nS > 0 ? 100.0 * cS / nS : 0;
                double overall = 100.0 * (cL + cS) / n;
                return "paper-outcome signals-scored=" + n +
                    " overall-hit=" + overall.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " long-hit=" + longHit.ToString("F1", CultureInfo.InvariantCulture) + "% (n=" + nL + ")" +
                    " short-hit=" + shortHit.ToString("F1", CultureInfo.InvariantCulture) + "% (n=" + nS + ")";
            }
        }

        // Fixed 2-tick first-touch tracker + calibration. For each probe it
        // records the model's P(up) and the entry mid, then waits until price
        // first touches +targetTicks or -targetTicks and records which came
        // first. It reports the realized up-first rate, how well the stated
        // probability matches reality (calibration/reliability), and the median
        // lead time from prediction to the ±target resolution.
        // Command payload — byte-identical to LnnOrderExecStrategy.CommandPayload
        // and LnnLiveDashboard.TradeFillPayload's command twin. Pack=1, Size=64.
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        private struct OrderCommand
        {
            public long CommandId;
            public int Op;
            public int Qty;
            public double Price;
            public double StopPrice;
            public byte Tif;
            public long SendUtcTicks;   // v64k: sender's precise clock (was an unused OrderRefId duplicate)
            public float TrailingStopDistance;
            public double StopLossPrice;
            public double ProfitTargetPrice;
        }

        // Writes join/market/flatten commands into the local order-command ring
        // that LnnOrderExecStrategy reads. Same seqlock ring protocol as the DOM
        // writer: header (64B) + 2^17 slots (72B: 8B seqlock + 64B payload).
        private sealed class DirectOrderRing : IDisposable
        {
            private const long HeaderBytes = 64;
            private const int SlotBytes = 72;
            private const int Capacity = 1 << 17;
            private const long Magic = 0x4C4E4E4F524431L;
            private readonly object gate = new object();
            private System.IO.MemoryMappedFiles.MemoryMappedFile map;
            private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view;
            private EventWaitHandle wake;
            private long sequence;
            private long commandCounter;

            internal DirectOrderRing(string mapName)
            {
                long bytes = HeaderBytes + (long)Capacity * SlotBytes;
                map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(mapName, bytes);
                view = map.CreateViewAccessor(0, bytes, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
                // Initialize the header only if it isn't already a valid ring.
                if (view.ReadInt64(16) != Magic || view.ReadInt32(8) != Capacity || view.ReadInt32(12) != SlotBytes)
                {
                    view.Write(0, 0L); view.Write(8, Capacity); view.Write(12, SlotBytes); view.Write(16, Magic);
                    sequence = 0;
                }
                else sequence = view.ReadInt64(0); // append after whatever's there
                try { wake = new EventWaitHandle(false, EventResetMode.AutoReset, mapName + "_EVENT"); } catch { wake = null; }
            }

            internal void Write(int op, int qty) { Write(op, qty, 0.0); }
            // Signal context (op 46): P(up) of the signal that the next entry carries,
            // so the executioner's EV policy can weigh posting vs crossing.
            internal void WriteSignalContext(double pUp) { Write(46, 1, pUp); }
            internal void Write(int op, int qty, double price)
            {
                if (view == null) return;
                OrderCommand cmd = new OrderCommand { CommandId = ++commandCounter, Op = op, Qty = Math.Max(1, qty), Price = price, SendUtcTicks = UtcNowTicks() };
                lock (gate)
                {
                    long next = sequence + 1;
                    long offset = HeaderBytes + (next & (Capacity - 1)) * SlotBytes;
                    view.Write(offset, next * 2 + 1);
                    Thread.MemoryBarrier();
                    view.Write(offset + 8, ref cmd);
                    Thread.MemoryBarrier();
                    view.Write(offset, next * 2);
                    Thread.MemoryBarrier();
                    view.Write(0, next);
                    sequence = next;
                    if (wake != null) { try { wake.Set(); } catch { } }
                }
            }

            public void Dispose()
            {
                try { if (wake != null) wake.Dispose(); } catch { }
                try { if (view != null) view.Dispose(); } catch { }
                try { if (map != null) map.Dispose(); } catch { }
                wake = null; view = null; map = null;
            }
        }

        private sealed class DirectionalTracker
        {
            private readonly double targetTicks, tickSize;
            private readonly long timeoutTicks, latencyTicks;
            private sealed class Probe
            {
                public double Prob, EntryBid, EntryAsk; public long StartUtc; public double Target; public bool Armed;
                // v64s: same geometry as the training labels - a side that has been
                // stopped can no longer win, so the meter grades the trade the model
                // is actually being trained on rather than a symmetric one.
                public double Stop; public bool UpStopped, DownStopped;
            }
            private readonly List<Probe> open = new List<Probe>();
            // Rolling outcomes: predictedUp flag, realizedUpFirst flag, prob, leadMs.
            private readonly List<double> probs = new List<double>();
            private readonly List<int> upFirst = new List<int>();
            private readonly List<int> predUp = new List<int>();
            private readonly List<double> leadMs = new List<double>();
            private const int Window = 500;
            private long lastProbeUtc;
            private double lastTarget; // most recent dynamic target, for display
            private double lastStop;   // most recent stop (0 = symmetric)
            private double lastCost;   // round-trip cost in ticks, charged to both sides
            private long discardedBothStopped;

            internal DirectionalTracker(double targetTicks, double tickSize, int timeoutMs, long latencyTicks)
            {
                this.targetTicks = targetTicks; this.tickSize = tickSize;
                timeoutTicks = (long)timeoutMs * TimeSpan.TicksPerMillisecond;
                this.latencyTicks = Math.Max(0, latencyTicks);
            }

            // v64v: graded at the TOUCH and net of cost, exactly like the training labels.
            // It used to score mid-to-mid, which hands a real trade a free spread - a whole
            // tick on MES, ~12% of an 8-tick target - so the meter flattered the model.
            internal void Observe(long utc, double bid, double ask, double probability, double target, double stop, double costTicks)
            {
                if (target > 0) lastTarget = target;
                lastStop = stop;
                lastCost = costTicks;
                // Resolve open probes against the current mid.
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    Probe p = open[i];
                    if (!p.Armed)
                    {
                        // Latency-aware: the probe's reference mid is the one at
                        // signal + round trip, so skill is measured on what an
                        // order could actually capture.
                        if (utc - p.StartUtc < latencyTicks) continue;
                        p.EntryBid = bid; p.EntryAsk = ask; p.StartUtc = utc; p.Armed = true;
                    }
                    // A long enters at the ask and marks out at the bid; a short the other
                    // way. Both carry the spread, and cost is charged once per side.
                    double longNet = (bid - p.EntryAsk) / tickSize - costTicks;
                    double shortNet = (p.EntryBid - ask) / tickSize - costTicks;
                    // v64s: an up bracket dies when price goes p.Stop against it, and a
                    // down bracket dies when price goes p.Stop the other way. A side that
                    // is already dead cannot win later, so a move that retraces past the
                    // stop and only then reaches the target scores as a loss - exactly
                    // what the training labels now do. Stop == Target reproduces the old
                    // symmetric "first to +/-Target" behaviour.
                    double stopT = p.Stop > 0 ? p.Stop : p.Target;
                    if (shortNet >= stopT) p.UpStopped = true;    // adverse for a long
                    if (longNet >= stopT) p.DownStopped = true;   // adverse for a short
                    int resolved = 0;
                    if (!p.UpStopped && longNet >= p.Target) resolved = 1;          // up bracket won
                    else if (!p.DownStopped && shortNet >= p.Target) resolved = -1; // down bracket won
                    else if (p.UpStopped && p.DownStopped) resolved = 3;              // both stopped - untradeable
                    else if (utc - p.StartUtc >= timeoutTicks) resolved = 2;          // timed out
                    if (resolved == 0) continue;
                    open.RemoveAt(i);
                    if (resolved == 3) { discardedBothStopped++; continue; }
                    if (resolved == 2) continue; // timeouts don't score direction
                    probs.Add(p.Prob);
                    predUp.Add(p.Prob >= 0.5 ? 1 : 0);
                    upFirst.Add(resolved == 1 ? 1 : 0);
                    leadMs.Add((utc - p.StartUtc) / (double)TimeSpan.TicksPerMillisecond);
                    if (probs.Count > Window) { probs.RemoveAt(0); predUp.RemoveAt(0); upFirst.RemoveAt(0); leadMs.RemoveAt(0); }
                }
                // Launch a fresh probe at a bounded cadence (~every 100ms).
                if (lastProbeUtc == 0 || utc - lastProbeUtc >= 100 * TimeSpan.TicksPerMillisecond)
                {
                    open.Add(new Probe { Prob = probability, EntryBid = bid, EntryAsk = ask, StartUtc = utc,
                        Target = target > 0 ? target : targetTicks, Stop = stop, Armed = latencyTicks <= 0 });
                    lastProbeUtc = utc;
                    if (open.Count > 4096) open.RemoveAt(0);
                }
            }

            internal string Line(double currentProbability)
            {
                int n = upFirst.Count;
                string geom = (lastTarget > 0 ? lastTarget : targetTicks).ToString("F2", CultureInfo.InvariantCulture) + "t" +
                    (lastStop > 0 ? " stop=" + lastStop.ToString("F2", CultureInfo.InvariantCulture) + "t" : "") +
                    " at-touch cost=" + lastCost.ToString("F2", CultureInfo.InvariantCulture) + "t";
                if (n < 20)
                    return "twotick target=" + geom +
                        " p-up=" + currentProbability.ToString("F3", CultureInfo.InvariantCulture) +
                        " samples=" + n + " (accumulating)";
                int ups = 0, correct = 0; double leadSum = 0;
                // Calibration: among probes the model called up (prob>=.5), how
                // often did up come first — and same for down — plus overall
                // directional accuracy vs the realized base rate.
                int calledUp = 0, calledUpCorrect = 0, calledDown = 0, calledDownCorrect = 0;
                for (int i = 0; i < n; i++)
                {
                    ups += upFirst[i];
                    if (predUp[i] == upFirst[i]) correct++;
                    if (predUp[i] == 1) { calledUp++; if (upFirst[i] == 1) calledUpCorrect++; }
                    else { calledDown++; if (upFirst[i] == 0) calledDownCorrect++; }
                    leadSum += leadMs[i];
                }
                double baseRate = 100.0 * ups / n;                 // realized P(up-first) overall
                double accuracy = 100.0 * correct / n;             // model directional accuracy
                // The honest skill number: does the model beat just always
                // guessing the more-common direction? Positive = real edge.
                double majorityBase = Math.Max(baseRate, 100.0 - baseRate);
                double skillVsBase = accuracy - majorityBase;
                // Per-side edge: does calling up beat the base up-first rate,
                // and does calling down beat the base down-first rate? Either
                // can be positive while the other is negative — trade only the
                // side with a real edge.
                double upReliability = calledUp > 0 ? 100.0 * calledUpCorrect / calledUp : 0;   // when it says up, up-first %
                double downReliability = calledDown > 0 ? 100.0 * calledDownCorrect / calledDown : 0;
                double upEdge = upReliability - baseRate;
                double downEdge = downReliability - (100.0 - baseRate);
                double avgLead = leadSum / n;
                return "twotick target=" + geom +
                    " p-up=" + currentProbability.ToString("F3", CultureInfo.InvariantCulture) +
                    " samples=" + n +
                    " untradeable=" + discardedBothStopped.ToString(CultureInfo.InvariantCulture) +
                    " realized-up-first=" + baseRate.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " directional-accuracy=" + accuracy.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " skill-vs-base=" + (skillVsBase >= 0 ? "+" : "") + skillVsBase.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " up-call-reliability=" + upReliability.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " up-edge=" + (upEdge >= 0 ? "+" : "") + upEdge.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " down-call-reliability=" + downReliability.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " down-edge=" + (downEdge >= 0 ? "+" : "") + downEdge.ToString("F1", CultureInfo.InvariantCulture) + "%" +
                    " avg-lead-ms=" + avgLead.ToString("F0", CultureInfo.InvariantCulture);
            }
        }

        private static void StartShadowControlServer(string pipeName, ShadowController controller)
        {
            Thread server = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            pipe.WaitForConnection();
                            int command = pipe.ReadByte();
                            if (command == 'F') { controller.Fusion = EnableCrossMarketAndReversal; Console.WriteLine(EnableCrossMarketAndReversal ? "signal-mode=fusion by dashboard control (shadow stack ON)" : "shadow/fusion disabled in this engine build — ignoring fusion toggle"); }
                            else if (command == 'T') { controller.Fusion = false; Console.WriteLine("signal-mode=thresholds by dashboard control (shadow stack OFF)"); }
                        }
                    }
                    catch (IOException) { }
                }
            });
            server.IsBackground = true;
            server.Name = "LNN shadow/fusion control";
            server.Start();
        }

        private static void StartPaperControlServer(string pipeName, PaperSimulationController controller)
        {
            Thread server = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            pipe.WaitForConnection();
                            int command = pipe.ReadByte();
                            if (command == '1')
                            {
                                controller.Enabled = true;
                                Console.WriteLine("paper-simulation=enabled by dashboard control");
                            }
                            else if (command == '0')
                            {
                                controller.Enabled = false;
                                Console.WriteLine("paper-simulation=disabled by dashboard control");
                            }
                            else if (command == 'L') controller.PendingCommand = 1;
                            else if (command == 'S') controller.PendingCommand = 2;
                            else if (command == 'F') controller.PendingCommand = 3;
                        }
                    }
                    catch (IOException)
                    {
                        // A control client can disconnect before sending. Keep
                        // the background server ready for the next dashboard click.
                    }
                }
            });
            server.IsBackground = true;
            server.Name = "LNN paper simulation control";
            server.Start();
        }

        // This control channel saves only the in-memory learner state. It has
        // no account, broker, or order-related function.
        private static void StartModelSnapshotControlServer(string pipeName, ModelSnapshotController controller)
        {
            Thread server = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            pipe.WaitForConnection();
                            int command = pipe.ReadByte();
                            if (command == 'S')
                            {
                                controller.Requested = true;
                                Console.WriteLine("manual-model-save requested by dashboard; saving at the next processed DOM snapshot.");
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // Keep the server available when a client disconnects
                        // before sending its one-byte save request.
                    }
                }
            });
            server.IsBackground = true;
            server.Name = "LNN manual model snapshot control";
            server.Start();
        }

        private static double GetPaperTradeGrossTicks(PaperTrade trade, Frame frame, double tickSize)
        {
            double exitPrice = trade.IsLong ? frame.Bid[0] : frame.Ask[0];
            return trade.IsLong ? (exitPrice - trade.EntryPrice) / tickSize : (trade.EntryPrice - exitPrice) / tickSize;
        }

        private static void ClosePaperTrade(StreamWriter writer, PaperTrade trade, Frame exitFrame, long exitUtcTicks, double tickSize,
            double extraCostTicks, string reason, ref long completedTrades, ref long wins, ref long losses,
            ref double netTicks, ref double peakTicks, ref double maxDrawdownTicks)
        {
            double exitPrice = trade.IsLong ? exitFrame.Bid[0] : exitFrame.Ask[0];
            double grossTicks = trade.IsLong ? (exitPrice - trade.EntryPrice) / tickSize : (trade.EntryPrice - exitPrice) / tickSize;
            double resultTicks = grossTicks - extraCostTicks;
            completedTrades++;
            if (resultTicks > 0.0) wins++; else losses++;
            netTicks += resultTicks;
            if (netTicks > peakTicks) peakTicks = netTicks;
            double drawdownTicks = peakTicks - netTicks;
            if (drawdownTicks > maxDrawdownTicks) maxDrawdownTicks = drawdownTicks;
            if (writer != null)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:O},{1:O},{2},{3},{4},{5:F6},{6:F6},{7:F4},{8:F4},{9:F4},{10:F4},{11},{12:F4},{13:F4}",
                    new DateTime(trade.EntryUtcTicks, DateTimeKind.Utc), new DateTime(exitUtcTicks, DateTimeKind.Utc),
                    trade.IsLong ? "LONG" : "SHORT", trade.EntryFrame, exitFrame.Sequence, trade.EntryPrice, exitPrice,
                    grossTicks, extraCostTicks, resultTicks, trade.TargetTicks, reason, netTicks, drawdownTicks));
                writer.Flush();
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "paper-trade-close direction={0} net-ticks={1:F2} gross-ticks={2:F2} cost-ticks={3:F2} reason={4} completed={5}",
                trade.IsLong ? "LONG" : "SHORT", resultTicks, grossTicks, extraCostTicks, reason, completedTrades));
        }

        private static string GetPaperSignal(double probability, long trained, Settings settings)
        {
            if (trained < MinTrainingForPaperSignal) return "WARMUP";
            if (probability >= settings.PaperLongThreshold) return "LONG";
            if (probability <= settings.PaperShortThreshold) return "SHORT";
            return "NO-TRADE";
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private static double Sigmoid(double value) { return 1.0 / (1.0 + Math.Exp(-Math.Max(-30.0, Math.Min(30.0, value)))); }
        private static double Clip(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
        private static int ParseInt(string value, string name) { int parsed; if (!int.TryParse(value, out parsed)) throw new ArgumentException("Invalid " + name); return parsed; }
        private static double ParseDouble(string value, string name) { double parsed; if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) throw new ArgumentException("Invalid " + name); return parsed; }
        private static bool ParseBoolean(string value, string name) { if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true; if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false; throw new ArgumentException("Invalid " + name + "; use 0/1 or false/true."); }
        private static void PrintUsage() { Console.WriteLine("Usage: LnnLiveLearner.exe [--pipe LnnDepthBridge] [--tick-size 0.0001] [--horizon-events 20] [--learning-rate 0.002] [--cost-ticks 0] [--minimum-profit-target-ticks 1.00] [--paper-long-threshold 0.60] [--paper-short-threshold 0.40] [--paper-sim-start-enabled 0] [--min-processing-interval-ms 5] [--save-file C:\\capture\\session.bin] [--load-checkpoint-file C:\\capture\\chosen_snapshot.bin] [--checkpoint-file C:\\capture\\m6e_weights.bin] [--best-checkpoint-file C:\\capture\\m6e_best_validated.bin] [--manual-checkpoint-file C:\\capture\\m6e_manual_snapshot.bin] [--learning-log-file C:\\capture\\updates.csv] [--weight-update-log-file C:\\capture\\feature_weight_updates.csv] [--paper-trade-log-file C:\\capture\\paper_trades.csv] [--teacher-example-file C:\\capture\\examples.csv] [--teacher-candidate-file C:\\capture\\candidate.txt]"); }
    }
}
