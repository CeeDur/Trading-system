// LnnOrderExecStrategy — NinjaTrader 8 strategy that executes commands from the
// LNN Dashboard's Trading Hub / AI Trader.
//
// Wire contract (must match LnnLiveDashboard):
//   Command ring: Local\LNN_ORDER_CMD_<SYMBOL>   (header 64B + 131072 slots × 72B)
//   Fill ring:    Local\LNN_ORDER_FILL_<SYMBOL>  (header 64B + 131072 slots × 72B)
//   Wake event:   Local\LNN_ORDER_CMD_<SYMBOL>_EVENT (AutoReset)
//   Header: WriteSequence i64, Capacity i32, RecordSize i32, Magic i64 (0x4C4E4E4F524431)
//   Slot:   seqlock i64 (seq*2+1 while writing, seq*2 when stable) + 64B payload
//
// Install: NinjaTrader 8 → New → NinjaScript Editor → Strategies → paste as
// LnnOrderExecStrategy → compile → add to a chart with the SAME instrument the
// dashboard has selected (e.g. MES front month) → enable on the intended
// account (Sim101 first!).
//
// The strategy is UNMANAGED: it submits exactly what the dashboard commands.
// Flat/CancelAll always work. Optional per-command stop-loss / profit-target
// prices become an OCO exit bracket after the entry fills; a trailing stop
// distance becomes a stop order that only moves in the trade's favor.
#region Using declarations
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class LnnOrderExecStrategy : Strategy
    {
        private const long HeaderBytes = 64;
        private const int SlotBytes = 72;
        private const int Capacity = 1 << 17;
        private const long Magic = 0x4C4E4E4F524431L;
        // Diagnostic message ring (strategy → dashboard text).
        private const int MsgSlotBytes = 128;   // 8 seqlock + 8 ts + 112 text
        private const int MsgCapacity = 1 << 12;
        private const long MsgMagic = 0x4C4E4E4D534731L;

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        private struct CommandPayload
        {
            public long CommandId;
            public int Op;
            public int Qty;
            public double Price;
            public double StopPrice;
            public byte Tif;
            public long SendUtcTicks;   // v64k: sender's precise clock, for the ring-hop measurement
            public float TrailingStopDistance;
            public double StopLossPrice;
            public double ProfitTargetPrice;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        private struct FillPayload
        {
            public long EventTimeTicks;
            public long CallbackUtcTicks;
            public long CommandId;
            public long NtOrderIdLow;
            public double Price;
            public int Qty;
            public int FilledQty;
            public int OrderState;
            public int OrderAction;
            public int MarketPosition;
            public byte IsExecution;
        }

        private MemoryMappedFile cmdMap, fillMap, msgMap;
        private MemoryMappedViewAccessor cmdView, fillView, msgView;
        private long msgSequence;
        private readonly object msgLock = new object();
        private EventWaitHandle wakeEvent;
        private Thread readerThread;
        private volatile bool readerStop;
        private long cmdReadSequence, fillWriteSequence;
        private readonly object fillLock = new object();
        // v64k trade-path observability: heartbeat + per-command acknowledgement
        // (both ride the fill ring as record types 4 and 5, see PublishStatus/PublishAck).
        private DateTime lastHeartbeatUtc = DateTime.MinValue, lastRiskConfigUtc = DateTime.MinValue;
        private long lastCommandIdProcessed, commandsProcessed;
        private volatile int lastGateBlock;   // why the armed executioner has not posted yet (0 none, 1 no book, 2 imbalance, 3 microprice, 4 resting, 5 flicker, 6 aggression)
        private int pendingAck;               // result code the current ProcessCommand will acknowledge with

        // Bracket / trailing state
        private class PendingBracket
        {
            public double StopLoss, ProfitTarget;      // absolute prices (0 = unset)
            public double StopLossTicks, ProfitTargetTicks; // relative defaults (0 = unset)
            public float Trail;                        // absolute price distance
            public double TrailTicks;                  // relative default
            public long CommandId;
        }
        private readonly Dictionary<string, PendingBracket> pendingBrackets = new Dictionary<string, PendingBracket>();
        // v64k barrier exits: op 47 sets the bracket (in ticks from the fill) for the
        // NEXT entry only - the AI Trader sends it right before each entry so live
        // trades exit exactly where the paper sim / labels do (+target / -target).
        private bool nextBracketSet; private double nextBracketSlTicks, nextBracketPtTicks, nextBracketTrailTicks;
        // ── v64k fast order dispatch ──────────────────────────────────────
        // TriggerCustomEvent hands the command to NinjaTrader's dispatcher, and it runs only
        // when that dispatcher gets around to it - measured at ~145 ms on a busy platform,
        // while the order itself then filled in 2 ms. OnMarketData / OnMarketDepth already
        // run on the strategy thread many times a second, and orders may be submitted from
        // them, so a queued command is also drained there. Both paths race; an interlocked
        // claim guarantees the command runs exactly once, whichever arrives first. Turning
        // FastOrderDispatch off leaves only the original TriggerCustomEvent path.
        private sealed class PendingCommand { public CommandPayload Cmd; public long RecvTicks; public int Claimed; }
        private readonly Queue<PendingCommand> pendingCommands = new Queue<PendingCommand>();
        private readonly object pendingLock = new object();
        private volatile int pendingCommandCount;
        private long lastOrderCallDoneTicks;   // when SubmitOrderUnmanaged returned, so the timing measures the order call and not the logging after it
        private readonly long ocoSession = DateTime.UtcNow.Ticks;   // unique per strategy instance, so OCO ids never repeat across restarts
        private long ocoCounter;
        // v64k: command id -> the SENDER's stamp, so every order update/execution can report
        // end-to-end latency from whoever originated it. With engine-direct orders the
        // dashboard never sent the command and has no start time of its own; this supplies it.
        private readonly Dictionary<long, long> commandSendTicks = new Dictionary<long, long>();

        // v65c dispatch telemetry. Which of the two paths actually reaches a command first IS
        // the latency story: NinjaTrader's TriggerCustomEvent dispatcher was measured at ~145 ms
        // on a busy platform, while a market-event drain runs on the very next tick. Counting
        // them separately turns "orders are slow" into "fast dispatch is/isn't working".
        // Order ops only - the config ops would swamp the ratio.
        private long dispatchFastCount, dispatchSlowCount, dispatchFastUsSum, dispatchSlowUsSum;

        private void RunPending(PendingCommand pc, bool viaMarketEvent)
        {
            if (pc == null) return;
            if (Interlocked.CompareExchange(ref pc.Claimed, 1, 0) != 0) return;   // the other path already ran it
            int op = pc.Cmd.Op;
            if (op <= 5 || op == 13 || op == 30 || op == 31)
            {
                long waitUs = (UtcNowTicks() - pc.RecvTicks) / 10;
                if (waitUs < 0) waitUs = 0;
                if (viaMarketEvent) { dispatchFastCount++; dispatchFastUsSum += waitUs; }
                else                { dispatchSlowCount++; dispatchSlowUsSum += waitUs; }
            }
            ProcessCommand(pc.Cmd, pc.RecvTicks);
        }

        // Record type 9 = DISPATCH TELEMETRY (1 s, beside the heartbeat).
        //   Qty       = order commands run by a market event (the fast path)
        //   FilledQty = order commands run by NT8's TriggerCustomEvent dispatcher (the slow path)
        //   Price     = mean wait on the fast path, ms      NtOrderIdLow = mean wait on the slow path, us
        //   OrderState= ms since the last market data of any kind (0 = never)
        //   OrderAction = 1 when FastOrderDispatch is on
        private void PublishDispatchTelemetry()
        {
            if (fillView == null) return;
            long fast = dispatchFastCount, slow = dispatchSlowCount;
            long fastUs = dispatchFastUsSum, slowUs = dispatchSlowUsSum;
            long sinceData = lastMarketDataTicks == 0 ? 0 : (UtcNowTicks() - lastMarketDataTicks) / TimeSpan.TicksPerMillisecond;
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = UtcNowTicks(),
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = 0,
                NtOrderIdLow = slow > 0 ? slowUs / slow : -1,
                Price = fast > 0 ? (fastUs / (double)fast) / 1000.0 : -1.0,
                Qty = (int)Math.Min(int.MaxValue, fast),
                FilledQty = (int)Math.Min(int.MaxValue, slow),
                OrderState = (int)Math.Min(int.MaxValue, sinceData),
                OrderAction = FastOrderDispatch ? 1 : 0,
                MarketPosition = 0,
                IsExecution = 9
            };
            WriteFillRecord(ref payload);
        }

        private void DrainPendingCommands()
        {
            if (pendingCommandCount == 0) return;
            while (true)
            {
                PendingCommand pc = null;
                lock (pendingLock)
                {
                    if (pendingCommands.Count > 0) { pc = pendingCommands.Dequeue(); pendingCommandCount = pendingCommands.Count; }
                }
                if (pc == null) return;
                RunPending(pc, true);
            }
        }
        private Order trailingStopOrder;
        private double trailingDistance;
        private long commandCounter;

        [NinjaScriptProperty]
        public string MapSymbolOverride { get; set; }

        // ==== Safety limits (hard caps enforced in NT8, whatever the dashboard sends) ====
        [NinjaScriptProperty] public int MaxPositionSize { get; set; }
        [NinjaScriptProperty] public int MaxOrdersPerMinute { get; set; }
        [NinjaScriptProperty] public double DailyLossLimitTicks { get; set; }   // 0 = off
        [NinjaScriptProperty] public string TradingStart { get; set; }          // HH:mm local, blank = always
        [NinjaScriptProperty] public string TradingEnd { get; set; }

        // ==== Default exit bracket (ticks, applied when a command carries none; 0 = off) ====
        [NinjaScriptProperty] public double DefaultStopLossTicks { get; set; }
        [NinjaScriptProperty] public double DefaultProfitTargetTicks { get; set; }
        [NinjaScriptProperty] public double DefaultTrailTicks { get; set; }

        // ==== CPU placement ====
        // PinExecutionerCore: pins THIS strategy's command-reader / order-posting
        // thread (the executioner's hot path) to one core; -1 = off. Default 0
        // so it shares the first core with the LNN engine and away from the hog.
        // PinNinjaTraderProcessCore: pins the WHOLE NinjaTrader process to one
        // core; -1 = off (default). Heavy-handed - use only if you know why.
        [NinjaScriptProperty] public bool FastOrderDispatch { get; set; }
        [NinjaScriptProperty] public bool PrintToOutputWindow { get; set; }   // v64k: OFF by default - Print() blocks the order path; the dashboard gets everything via the message ring
        [NinjaScriptProperty] public int OrderSpinMicros { get; set; }        // v64k: busy-poll the command ring this long before sleeping on the wake event (0 = event only)
        [NinjaScriptProperty] public int PinExecutionerCore { get; set; }
        [NinjaScriptProperty] public int PinNinjaTraderProcessCore { get; set; }

        // ==== Fill model / EV policy ====
        // FillModelFile: text model written by LnnExecBacktest --fit-fill-model
        // (P(fill within T | queue/book state)). ExecPolicy 0 = gates (default),
        // 1 = expected value: post/hold/cross by comparing
        //   EV_post  = P(fill) x (half-spread - adverse + edge) + (1-P) x EV_miss
        //   EV_cross = -half-spread + edge,   edge = (2 P(up) - 1) x ExpectedMoveTicks
        // P(up) arrives with op 46 (signal context) sent just before the entry op.
        [NinjaScriptProperty] public string FillModelFile { get; set; }
        [NinjaScriptProperty] public int ExecPolicy { get; set; }
        [NinjaScriptProperty] public double ExpectedMoveTicks { get; set; }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        // v64k: 100 ns wall clock where the OS has it. DateTime.UtcNow only advances on the
        // system timer tick (0.5-15.6 ms), which on a background/VPS process quantises every
        // latency stamp - the dashboard's ack/fill numbers inherit that noise.
        [DllImport("kernel32.dll")] private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
        private static bool preciseClock = true;
        internal static long UtcNowTicks()
        {
            if (preciseClock)
            {
                try { long ft; GetSystemTimePreciseAsFileTime(out ft); return ft + 504911232000000000L; }
                catch { preciseClock = false; }
            }
            return DateTime.UtcNow.Ticks;
        }
        [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

        // Live-tuning state (ops 20-24 from the dashboard)
        private volatile bool executionPaused;
        private readonly Queue<DateTime> recentOrderTimes = new Queue<DateTime>();
        private int riskTrackedPosition; private double riskAveragePrice, riskOpenTicks, realizedTicksToday;
        private bool dailyLossTripped; private DateTime riskDay = DateTime.MinValue;

        // ==================== v64y: entry gates over the MMF (ops 25 / 26 / 46) ====================
        // Every gate the dashboard used to apply BEFORE forwarding now also lives here, at the one
        // choke point every entry passes through - so engine-direct orders (which the dashboard
        // never sees) get exactly the same treatment as forwarded ones. Exits are never gated.
        //
        // Op 25  Qty=max trades/session  Price=trade gap s  StopPrice=post-loss cooldown s
        //        StopLossPrice=confidence margin  ProfitTargetPrice=dead-regime block s
        //        TrailingStopDistance=dead-regime clear s   Tif=direction filter 0 both/1 long/2 short
        // Op 26  Qty=max flips per 10 min  Price=session start (minutes of day, local, -1 off)
        //        StopPrice=session end  StopLossPrice=dead-zone low  ProfitTargetPrice=dead-zone high
        //        TrailingStopDistance=ramp wins per step  Tif=bit0 gates enabled, bit1 barrier mode,
        //        bit2 flip on opposite
        // Op 46  Price = P(up) carried by the entry that follows it (already sent by the engine).
        private volatile bool gatesEnabled;              // master switch: false = behave exactly as v64x
        private int gMaxTradesPerSession;                // 0 = off
        private double gTradeGapSeconds;                 // 0 = off
        private double gCooldownSeconds;                 // 0 = off
        private double gConfidenceMargin;                // 0 = off
        private double gNeutralBlockSeconds;             // 0 = off
        private double gNeutralClearSeconds = 3.0;
        private int gDirectionFilter;                    // 0 both, 1 longs only, 2 shorts only
        private int gMaxFlipsPer10Min;                   // 0 = off
        private int gSessionStartMinutes = -1, gSessionEndMinutes = -1;   // -1 = off
        private double gDeadZoneLow = 0.45, gDeadZoneHigh = 0.55;
        // v64z: the paper thresholds the margin gate measures against. These are editable in
        // the dashboard, so hardcoding 0.60/0.40 here silently blocked every entry for anyone
        // who had changed them. Sent over op 27.
        private double gLongThreshold = 0.60, gShortThreshold = 0.40;
        private int gRampWinsPerStep;                    // 0 = off
        private bool gBarrierMode, gFlipOnOpposite;

        // ==================== v65a: book-quality and health gates (ops 28 / 29) ====================
        // The executioner already computes all of this from OnMarketDepth, but only used it to
        // decide where a PASSIVE limit posts. In market mode nothing looked at the book at all.
        // These apply the same evidence to the decision to enter, whichever order type is used.
        // Every one defaults to 0 = off: a gate that needs depth must never block a feed that
        // has no depth subscription.
        private double gMaxSpreadTicks;      // 0 = off. Refuse to pay more than this to get in.
        private double gMinImbalance;        // 0 = off. Book must lean the way we are entering.
        private bool   gMicropriceGate;      // microprice must sit on our side of the mid
        private double gMaxFlickerPerSec;    // 0 = off. An unstable touch means the price is not real.
        private long   gMaxAggressionInto;   // 0 = off. Someone is already running our side.
        private long   gMinTopOfBookSize;    // 0 = off. Size available on the side we would take.
        private double gStaleSignalMs;       // 0 = off. Age limit on the op 46 context.
        private double gFeedStaleMs;         // 0 = off. No market data for this long = trade blind.
        private double gGiveBackPercent;     // 0 = off. Stop after handing back this % of the day's peak.
        private long   lastMarketDataTicks;  // stamped on every Last print and depth update
        private double peakRealizedTicks;    // high-water mark of realizedTicksToday

        // Gate state the strategy keeps for itself. The dashboard cannot see engine-direct
        // entries, so counting here is the only way these can be right on that path.
        private double lastSignalPUp = double.NaN; private long lastSignalPUpTicks;
        private int entriesThisSession, sessionWins, lastEntrySide;
        private long lastEntryUtcTicks, cooldownUntilTicks;
        private long neutralSinceTicks, directionalSinceTicks; private bool neutralBlocked;
        private readonly Queue<DateTime> entryFlipTimes = new Queue<DateTime>();
        private volatile int lastEntryGateBlock;         // published in the heartbeat, see GateBlockName

        private static string GateBlockName(int code)
        {
            switch (code)
            {
                case 1:  return "execution paused";      case 2:  return "daily loss";
                case 3:  return "trading hours";         case 4:  return "max position";
                case 5:  return "orders per minute";     case 6:  return "session window";
                case 7:  return "dead-regime (neutral)"; case 8:  return "confidence margin";
                case 9:  return "direction filter";      case 10: return "max trades per session";
                case 11: return "trade gap";             case 12: return "post-loss cooldown";
                case 13: return "flip guard";            case 14: return "spread too wide";
                case 15: return "book imbalance";        case 16: return "microprice";
                case 17: return "quote flicker";         case 18: return "aggression into us";
                case 19: return "thin/no book";          case 20: return "stale signal";
                case 21: return "market data stale";     case 22: return "daily give-back";
                default: return "none";
            }
        }

        // ==================== limit executioner ====================
        // When enabled, ops 30/31 (join buy/sell) are routed through a managed
        // controller instead of a fire-and-forget limit. It uses top-of-book
        // features from OnMarketDepth to gate posting, repegs/cancels to avoid
        // adverse fills, and scores every fill by markout so the dashboard can
        // tell whether posting is actually earning the spread. Measure mode runs
        // the whole loop as a simulation (no real orders) so the edge can be
        // proven before a single contract is risked.
        private volatile bool execEnabled;
        private volatile int execMode;        // 0 = measure (simulate), 1 = live (real orders)
        // Gate + management parameters (live-tunable via ops 41-43)
        private double exPostImbalanceMin = 0.0;   // require (bidSz-askSz)/(sum) >= this to post a buy
        private int exRestingMsMin = 200;          // touch size must rest this long before we trust it
        private double exFlickerMaxPerSec = 0.0;   // max touch add/cancel churn/sec to post (0 = off)
        private bool exMicropriceGate = true;      // require microprice on our side of mid
        private int exAggressionCancelQty = 15;    // contracts hitting our side within the window -> cancel
        private int exAggressionWindowMs = 500;
        private int exMaxChaseTicks = 1;           // how far we will repeg after the market
        private int exTimeoutMs = 8000;            // unfilled this long -> cross or abandon
        private int exOnTimeout = 0;               // 0 = abandon, 1 = cross with market
        private int exMarkout1Ms = 1000;
        private int exMarkout2Ms = 5000;
        // Top-of-book state maintained from OnMarketDepth with NinjaTrader's
        // depth semantics (v64j): Operation.Add INSERTS a row at its position
        // (deeper rows shift down), Operation.Remove DELETES it (deeper rows
        // shift up). Row 1 is tracked so a Remove@0 promotes the next level to
        // the touch immediately instead of leaving it blank until row 0 is
        // touched again. Mirrors TouchSide in LnnExecBacktest.
        private double tobBidPrice, tobAskPrice;
        private long tobBidSize, tobAskSize;
        private double row1BidPrice, row1AskPrice;
        private long row1BidSize, row1AskSize;
        private DateTime bidRestingSinceUtc = DateTime.MinValue, askRestingSinceUtc = DateTime.MinValue;
        private double bidRestingPrice, askRestingPrice;
        private long bidRestingSize, askRestingSize;
        // v64k: these are touched on EVERY depth/trade event, on NinjaTrader's data thread -
        // the same thread order submission has to run on. List.RemoveAt(0) is O(n), so trimming
        // a one-second window of a few thousand events memmoved the whole list per expiry and
        // stalled that thread. Queues dequeue in O(1).
        private readonly Queue<DateTime> touchFlickerEvents = new Queue<DateTime>();
        private readonly Queue<KeyValuePair<DateTime, long>> sellAggr = new Queue<KeyValuePair<DateTime, long>>(); // hit the bid
        private readonly Queue<KeyValuePair<DateTime, long>> buyAggr = new Queue<KeyValuePair<DateTime, long>>();  // lifted the ask
        // Active intent (one at a time, matching the AI trader's single-position model)
        private int exState;          // 0 idle/armed-waiting, 1 posting, 2 markout-pending
        private int exSide;           // +1 buy, -1 sell, 0 disarmed
        private int exQty;
        private double exPostPrice;
        private DateTime exPostUtc;
        private long exQueueAhead;    // displayed size ahead at post time (kept for the aggression rule + quality record)
        private long exFilledInto;    // measure mode: cumulative volume traded at/through our post
        // Queue-position ESTIMATOR (MBP-based; the feed has no market-by-order):
        //   trades at/through our price consume what is ahead of us;
        //   size drops at our price with no trade are cancels, assumed spread
        //   uniformly through the queue, so a share (ahead/(ahead+behind)) was
        //   ahead of us; size increases are new orders behind us.
        // Measure-mode fills trigger when the estimated ahead-of-us hits zero.
        private double exQAhead, exQBehind;
        private long exLevelSizeSeen;      // displayed size at our price at the last book event
        private long exTradedSinceBook;    // volume traded at/through our price since that book event
        // Cancel-ahead factor k (op 44, Price field): the share of a cancel assumed to come from
        // ahead of us is k x ahead/(ahead+behind). 0 = legacy (trades only), 1 = uniform cancels
        // (optimistic for a fresh post: the orders ahead are older and cancel less), 0.5 = default.
        private double exCancelAheadFactor = 0.5;
        private FillModel fillModel;                 // loaded from FillModelFile (also reloadable via op 45)
        private volatile int exPolicy;               // 0 gates, 1 EV (op 45 Price)
        private double exExpectedMoveTicks = 2.0;    // op 45 StopPrice
        private double exSignalPUp = 0.5;            // op 46 Price: P(up) of the signal that arms next
        private DateTime exLastEvEvalUtc = DateTime.MinValue;
        private long exEvCrossAtArm, exEvCrossWhilePosting;

        // Fill model: same file format and features as LnnExecBacktest.FillModel.
        private sealed class FillModel
        {
            internal const int F = 8;
            internal int[] Horizons = new int[0]; internal double[] Mean = new double[F], Std = new double[F];
            internal double[] Bias = new double[0]; internal double[][] W = new double[0][];
            internal double AdverseFillTicks; internal long Samples;
            // v2: Weibull hazard on exact fill times + touch-to-touch markouts
            internal bool Hazard; internal double HazardBias, HazardShape = 1.0; internal double[] HazardW = new double[F];
            internal double FillMarkoutTouchTicks = double.NaN, CrossMarkoutTouchTicks = double.NaN;
            internal double LambdaPerSec(double[] x)
            {
                double z = HazardBias; for (int i = 0; i < F; i++) z += HazardW[i] * ((x[i] - Mean[i]) / (Std[i] > 1e-9 ? Std[i] : 1.0));
                if (z > 8) z = 8; if (z < -12) z = -12; return Math.Exp(z);
            }
            internal static double[] Features(double ahead, double oppositeSize, double imbSigned, double tradeRateOurSide, double tradeRateOtherSide, double touchRate, double spreadTicks, double mpOffsetSigned)
            { return new double[] { Math.Log(1 + Math.Max(0, ahead)), Math.Log(1 + Math.Max(0, oppositeSize)), imbSigned, tradeRateOurSide, tradeRateOtherSide, touchRate, spreadTicks, mpOffsetSigned }; }
            private double Logit(int hi, double[] x) { double z = Bias[hi]; for (int i = 0; i < F; i++) z += W[hi][i] * ((x[i] - Mean[i]) / (Std[i] > 1e-9 ? Std[i] : 1.0)); return z; }
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
            private static double Sig(double z) { if (z > 30) z = 30; if (z < -30) z = -30; return 1.0 / (1.0 + Math.Exp(-z)); }
            internal static FillModel Load(string path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
                    FillModel m = new FillModel(); List<int> hs = new List<int>(); List<double> bs = new List<double>(); List<double[]> ws = new List<double[]>();
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                        int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash).Trim();
                        int eq = line.IndexOf('='); if (eq < 0) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        if (k == "mean") m.Mean = Arr(v); else if (k == "std") m.Std = Arr(v);
                        else if (k == "adverse_fill_ticks") m.AdverseFillTicks = Dbl(v); else if (k == "samples") m.Samples = (long)Dbl(v);
                        else if (k == "hazard_bias") { m.Hazard = true; m.HazardBias = Dbl(v); } else if (k == "hazard_w") m.HazardW = Arr(v);
                        else if (k == "hazard_shape") m.HazardShape = Math.Max(0.2, Dbl(v));
                        else if (k == "fill_markout_touch_ticks") m.FillMarkoutTouchTicks = Dbl(v); else if (k == "cross_markout_touch_ticks") m.CrossMarkoutTouchTicks = Dbl(v);
                        else if (k.StartsWith("h") && k.EndsWith("_bias")) { hs.Add(int.Parse(k.Substring(1, k.Length - 6), System.Globalization.CultureInfo.InvariantCulture)); bs.Add(Dbl(v)); }
                        else if (k.StartsWith("h") && k.EndsWith("_w")) ws.Add(Arr(v));
                    }
                    if (!m.Hazard && (hs.Count == 0 || hs.Count != ws.Count)) return null;
                    m.Horizons = hs.ToArray(); m.Bias = bs.ToArray(); m.W = ws.ToArray();
                    return m;
                }
                catch { return null; }
            }
            private static double Dbl(string v) { return double.Parse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture); }
            private static double[] Arr(string v) { string[] p = v.Split(new char[] { ',' }); double[] a = new double[p.Length]; for (int i = 0; i < p.Length; i++) a[i] = Dbl(p[i].Trim()); return a; }
        }

        private double AggrRate(bool sellSide, DateTime now)
        {
            Queue<KeyValuePair<DateTime, long>> src = sellSide ? sellAggr : buyAggr; double v = 0;
            foreach (KeyValuePair<DateTime, long> kv in src) if ((now - kv.Key).TotalMilliseconds <= 2000) v += kv.Value;
            return v / 2.0;
        }

        // EV of holding/posting vs crossing now (fill model + signal P(up)). True = crossing is better.
        private bool EvPrefersCross(bool buy, DateTime now, double remainingMs)
        {
            if (fillModel == null || tobBidPrice <= 0 || tobAskPrice <= 0) return false;
            double tick = Instrument.MasterInstrument.TickSize;
            long ours = buy ? tobBidSize : tobAskSize, opp = buy ? tobAskSize : tobBidSize;
            double mid = Mid(), mp = Microprice();
            double imb = (double)(tobBidSize - tobAskSize) / Math.Max(1, tobBidSize + tobAskSize) * (buy ? 1 : -1);
            double ahead = exState == 1 ? exQAhead : ours;
            double[] x = FillModel.Features(ahead, opp, imb, AggrRate(buy, now), AggrRate(!buy, now), FlickerPerSec(now), (tobAskPrice - tobBidPrice) / tick, (mp - mid) / tick * (buy ? 1 : -1));
            double p = fillModel.PFill(x, Math.Max(50, remainingMs));
            double edge = (2.0 * exSignalPUp - 1.0) * exExpectedMoveTicks, spread = (tobAskPrice - tobBidPrice) / tick;
            // touch-to-touch values from the fitted tape (v1 files fall back to half-spread arithmetic)
            double fillMk = !double.IsNaN(fillModel.FillMarkoutTouchTicks) ? fillModel.FillMarkoutTouchTicks : -fillModel.AdverseFillTicks;
            double crossMk = !double.IsNaN(fillModel.CrossMarkoutTouchTicks) ? fillModel.CrossMarkoutTouchTicks : -spread;
            double evCross = crossMk + edge;
            double evMiss = exOnTimeout == 1 ? (crossMk + edge * 0.5) : 0.0;
            double evPost = p * (fillMk + edge) + (1 - p) * evMiss;
            return evCross > evPost;
        }
        private Order exLiveOrder;
        private int exChasedTicks;
        private double exFillPrice, exMidAtFill;
        private DateTime exFillUtc;
        private double exMarkout1Ticks, exMarkout2Ticks;
        private bool exMark1Done, exMark2Done;
        // Fills whose markout window is still open are scored from this list,
        // independent of the arm state, so a new signal inside the window can
        // no longer drop the sample (found by the offline backtester).
        private sealed class PendingMarkout { public long SampleId; public int Side; public double FillPrice, MidAtFill, Mk1; public DateTime FillUtc; public bool Mk1Done; public long QueueAhead; }
        private readonly List<PendingMarkout> pendingMarkouts = new List<PendingMarkout>();
        private long exSampleId;
        private int exPosts, exFills, exCancels;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "LnnOrderExecStrategy";
                Description = "Executes LNN Dashboard trade-hub commands from shared memory. No signals of its own.";
                Calculate = Calculate.OnEachTick;
                IsUnmanaged = true;
                IsInstantiatedOnEachOptimizationIteration = false;
                MapSymbolOverride = "";
                MaxPositionSize = 2;
                MaxOrdersPerMinute = 12;
                DailyLossLimitTicks = 0;
                TradingStart = "";
                TradingEnd = "";
                DefaultStopLossTicks = 0;
                DefaultProfitTargetTicks = 0;
                DefaultTrailTicks = 0;
                FastOrderDispatch = true;        // v64k: run a queued command on the next market event instead of waiting for NT8's custom-event queue
                PrintToOutputWindow = false;     // v64k: keep NinjaTrader's Output window out of the order path
                OrderSpinMicros = 250;           // v64k: skip the kernel wake for commands that arrive within 250 us
                PinExecutionerCore = 1;          // engine owns core 0; executioner thread on core 1 so the hand-off never waits for a context switch
                PinNinjaTraderProcessCore = -1;
                FillModelFile = "";
                ExecPolicy = 0;
                ExpectedMoveTicks = 2.0;
            }
            else if (State == State.Realtime)
            {
                exPolicy = ExecPolicy; exExpectedMoveTicks = ExpectedMoveTicks > 0 ? ExpectedMoveTicks : 2.0;
                fillModel = FillModel.Load(FillModelFile);
                if (!string.IsNullOrWhiteSpace(FillModelFile)) Print("LnnOrderExecStrategy: fill model " + (fillModel != null ? "loaded (" + (fillModel.Hazard ? "hazard, shape " + fillModel.HazardShape.ToString("0.00") : "v1 horizons") + ", " + fillModel.Samples + " samples, fill markout@touch " + (double.IsNaN(fillModel.FillMarkoutTouchTicks) ? "n/a" : fillModel.FillMarkoutTouchTicks.ToString("0.000") + "t") + ")" : "NOT loaded") + " from " + FillModelFile + "; policy=" + (exPolicy == 1 ? "EV" : "gates") + "; markouts measured at the exit touch");
                if (PinNinjaTraderProcessCore >= 0)
                {
                    try
                    {
                        int n = Environment.ProcessorCount;
                        if (PinNinjaTraderProcessCore < n)
                        {
                            System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)(1L << PinNinjaTraderProcessCore);
                            Print("LnnOrderExecStrategy: NinjaTrader PROCESS pinned to core " + PinNinjaTraderProcessCore + " of " + n);
                        }
                        else Print("LnnOrderExecStrategy: PinNinjaTraderProcessCore " + PinNinjaTraderProcessCore + " out of range (" + n + " cores) - ignored");
                    }
                    catch (Exception ex) { Print("LnnOrderExecStrategy: process pin failed: " + ex.Message); }
                }
                OpenRings();
                readerStop = false;
                readerThread = new Thread(CommandReaderLoop) { IsBackground = true, Name = "LnnCmdReader" };
                readerThread.Start();
                Print("LnnOrderExecStrategy v65c: listening on " + CmdName() + " (account " + Account.Name + ", instrument " + Instrument.FullName + ")" +
                    (FastOrderDispatch ? " [fast dispatch: commands also run on the next market event, not only NT8's custom-event queue]" : " [custom-event dispatch only]") +
                    (OrderSpinMicros > 0 ? " [ring poll " + OrderSpinMicros + " us before blocking]" : " [blocking wait only]"));
            }
            else if (State == State.Terminated)
            {
                readerStop = true;
                try { wakeEvent?.Set(); } catch { }
                try { readerThread?.Join(1000); } catch { }
                try { wakeEvent?.Dispose(); } catch { }
                try { cmdView?.Dispose(); } catch { }
                try { fillView?.Dispose(); } catch { }
                try { msgView?.Dispose(); } catch { }
                try { cmdMap?.Dispose(); } catch { }
                try { fillMap?.Dispose(); } catch { }
                try { msgMap?.Dispose(); } catch { }
                wakeEvent = null; cmdView = null; fillView = null; msgView = null; cmdMap = null; fillMap = null; msgMap = null;
                readerThread = null;
            }
        }

        private string ResolveSymbol()
        {
            if (!string.IsNullOrWhiteSpace(MapSymbolOverride)) return MapSymbolOverride.Trim().ToUpperInvariant();
            string source = Instrument.MasterInstrument.Name; // e.g. "MES"
            char[] safe = new char[source.Length];
            int n = 0;
            foreach (char v in source)
            {
                if (char.IsLetterOrDigit(v)) safe[n++] = char.ToUpperInvariant(v);
                else if (n > 0 && safe[n - 1] != '_') safe[n++] = '_';
            }
            return new string(safe, 0, n).Trim(new char[] { '_' });
        }

        private string CmdName() { return @"Local\LNN_ORDER_CMD_" + ResolveSymbol(); }
        private string FillName() { return @"Local\LNN_ORDER_FILL_" + ResolveSymbol(); }
        private string MsgName() { return @"Local\LNN_ORDER_MSG_" + ResolveSymbol(); }

        // Print to NT8's Output AND forward to the dashboard's message ring.
        // v64k: this used to be the most expensive thing in the order path.
        //  1. Print() writes to NinjaTrader's Output window synchronously and marshals to its
        //     UI - tens of milliseconds when that window has content. It ran on the order path,
        //     and with fast dispatch that path IS NinjaTrader's data thread. The message ring
        //     already carries every one of these lines to the dashboard, so Print is now opt-in.
        //  2. The text was written to shared memory ONE BYTE AT A TIME - 112 bounds-checked
        //     accessor calls per message. One WriteArray does it in a single call.
        private readonly byte[] msgScratch = new byte[112];
        private void Note(string text)
        {
            if (PrintToOutputWindow) Print(text);
            if (msgView == null || string.IsNullOrEmpty(text)) return;
            try
            {
                lock (msgLock)
                {
                    Array.Clear(msgScratch, 0, msgScratch.Length);
                    int len = System.Text.Encoding.UTF8.GetBytes(text, 0, Math.Min(text.Length, 112), msgScratch, 0);
                    long next = msgSequence + 1;
                    long offset = HeaderBytes + (next & (MsgCapacity - 1)) * MsgSlotBytes;
                    msgView.Write(offset, next * 2 + 1);
                    Thread.MemoryBarrier();
                    msgView.Write(offset + 8, UtcNowTicks());
                    msgView.WriteArray(offset + 16, msgScratch, 0, msgScratch.Length);
                    Thread.MemoryBarrier();
                    msgView.Write(offset, next * 2);
                    Thread.MemoryBarrier();
                    msgView.Write(0, next);
                    msgSequence = next;
                }
            }
            catch { }
        }

        private void OpenRings()
        {
            long bytes = HeaderBytes + (long)Capacity * SlotBytes;

            cmdMap = MemoryMappedFile.CreateOrOpen(CmdName(), bytes);
            cmdView = cmdMap.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
            if (cmdView.ReadInt64(16) != Magic || cmdView.ReadInt32(8) != Capacity || cmdView.ReadInt32(12) != SlotBytes)
            {
                cmdView.Write(0, 0L); cmdView.Write(8, Capacity); cmdView.Write(12, SlotBytes); cmdView.Write(16, Magic);
            }
            cmdReadSequence = cmdView.ReadInt64(0); // start at current head: ignore stale commands

            fillMap = MemoryMappedFile.CreateOrOpen(FillName(), bytes);
            fillView = fillMap.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
            if (fillView.ReadInt64(16) != Magic || fillView.ReadInt32(8) != Capacity || fillView.ReadInt32(12) != SlotBytes)
            {
                fillView.Write(0, 0L); fillView.Write(8, Capacity); fillView.Write(12, SlotBytes); fillView.Write(16, Magic);
            }
            fillWriteSequence = fillView.ReadInt64(0);

            try
            {
                long msgBytes = HeaderBytes + (long)MsgCapacity * MsgSlotBytes;
                msgMap = MemoryMappedFile.CreateOrOpen(MsgName(), msgBytes);
                msgView = msgMap.CreateViewAccessor(0, msgBytes, MemoryMappedFileAccess.ReadWrite);
                if (msgView.ReadInt64(16) != MsgMagic || msgView.ReadInt32(8) != MsgCapacity || msgView.ReadInt32(12) != MsgSlotBytes)
                {
                    msgView.Write(0, 0L); msgView.Write(8, MsgCapacity); msgView.Write(12, MsgSlotBytes); msgView.Write(16, MsgMagic);
                }
                msgSequence = msgView.ReadInt64(0);
            }
            catch { msgView = null; msgMap = null; }

            wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CmdName() + "_EVENT");
        }

        // ==================== command intake (background thread) ====================

        private void CommandReaderLoop()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }
            try { TimeBeginPeriod(1); } catch { }
            if (PinExecutionerCore >= 0)
            {
                try
                {
                    int n = Environment.ProcessorCount;
                    if (PinExecutionerCore < n)
                    {
                        UIntPtr prev = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << PinExecutionerCore));
                        Print("LnnOrderExecStrategy: executioner thread pinned to core " + PinExecutionerCore + " of " + n + (prev == UIntPtr.Zero ? " (call failed)" : ""));
                    }
                    else Print("LnnOrderExecStrategy: PinExecutionerCore " + PinExecutionerCore + " out of range (" + n + " cores) - not pinned");
                }
                catch (Exception ex) { Print("LnnOrderExecStrategy: thread pin failed: " + ex.Message); }
            }
            DateTime lastReaderErrorUtc = DateTime.MinValue;
            while (!readerStop)
            {
                // v64k: a fault here must never end the reader thread silently -
                // report it (Output window + dashboard message ring) and keep going.
                try
                {
                    // A set event still costs a thread wake, and the woken thread waits for a
                    // scheduling slot - that wait is the ring-hop latency, not the memory. Poll
                    // the sequence for a short window first: a command that lands inside it is
                    // picked up without ever entering the kernel. Falls back to the blocking
                    // wait so an idle strategy costs nothing.
                    long spinUs = OrderSpinMicros;
                    if (spinUs > 0 && cmdView.ReadInt64(0) == cmdReadSequence)
                    {
                        long spinTicks = (long)(spinUs * (System.Diagnostics.Stopwatch.Frequency / 1000000.0));
                        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + spinTicks;
                        while (cmdView.ReadInt64(0) == cmdReadSequence &&
                               System.Diagnostics.Stopwatch.GetTimestamp() < deadline && !readerStop)
                            Thread.SpinWait(32);
                    }
                    if (cmdView.ReadInt64(0) == cmdReadSequence)
                        try { wakeEvent.WaitOne(250); } catch { }
                    if (readerStop) break;
                    long write = cmdView.ReadInt64(0);
                    if (write - cmdReadSequence > Capacity) cmdReadSequence = write - Capacity;
                    while (cmdReadSequence < write && !readerStop)
                    {
                        long next = cmdReadSequence + 1;
                        long offset = HeaderBytes + (next & (Capacity - 1)) * SlotBytes;
                        long first = cmdView.ReadInt64(offset);
                        if ((first & 1) != 0) break;             // writer mid-update
                        CommandPayload payload;
                        cmdView.Read(offset + 8, out payload);
                        long second = cmdView.ReadInt64(offset);
                        if (first != second || second != next * 2)
                        {
                            if (second > next * 2) { cmdReadSequence = second / 2 - 1; continue; }
                            break;
                        }
                        cmdReadSequence = next;
                        CommandPayload command = payload;
                        // v64k order-path timing. recvTicks is stamped the instant this thread
                        // has the command; ProcessCommand stamps again once NinjaTrader has
                        // marshalled it onto the strategy thread. The difference between the
                        // sender's stamp and recvTicks is the RING HOP (shared-memory write +
                        // event wake); the difference to the strategy thread is NT8's
                        // TriggerCustomEvent queue, which is usually the expensive part.
                        long recvTicks = UtcNowTicks();
                        PendingCommand pc = new PendingCommand { Cmd = command, RecvTicks = recvTicks };
                        if (FastOrderDispatch)
                        {
                            lock (pendingLock)
                            {
                                while (pendingCommands.Count > 0 && pendingCommands.Peek().Claimed != 0) pendingCommands.Dequeue();
                                pendingCommands.Enqueue(pc);
                                pendingCommandCount = pendingCommands.Count;
                            }
                        }
                        TriggerCustomEvent(o => RunPending(pc, false), null);   // v65c: NT8 dispatcher = the slow path
                    }
                    // Heartbeat: once a second the dashboard learns the strategy is
                    // alive, what it has consumed, and what the executioner is doing.
                    DateTime nowUtc = DateTime.UtcNow;
                    if ((nowUtc - lastHeartbeatUtc).TotalMilliseconds >= 1000)
                    {
                        lastHeartbeatUtc = nowUtc; PublishStatus(); PublishDispatchTelemetry();
                        if ((nowUtc - lastRiskConfigUtc).TotalSeconds >= 5) { lastRiskConfigUtc = nowUtc; PublishRiskConfig(); }
                    }
                }
                catch (Exception error)
                {
                    if ((DateTime.UtcNow - lastReaderErrorUtc).TotalSeconds >= 5)
                    {
                        lastReaderErrorUtc = DateTime.UtcNow;
                        Note("LnnOrderExecStrategy reader error (continuing): " + error.Message);
                    }
                    Thread.Sleep(100);
                }
            }
        }

        // ==================== v64k: heartbeat + command acknowledgement ====================
        // Record type 4 = STATUS (1 s heartbeat). Field meanings for this type only:
        //   NtOrderIdLow = commands consumed from the ring   CommandId = last command id processed
        //   Qty = signed NT position                          FilledQty = flags: 1 realtime, 2 exec enabled, 4 exec LIVE, 8 paused, 16 daily-loss tripped
        //   OrderState = executioner 0 idle / 1 armed-waiting / 2 posted / 3 markout   OrderAction = armed side 0 none / 1 long / 2 short
        //   MarketPosition = gate-wait reason while armed (see lastGateBlock)          Price = realized ticks today
        // Record type 5 = ACK for one command:
        //   CommandId = command id   OrderAction = op   Qty = qty   Price = price
        //   OrderState = 0 submitted / 1 rejected / 2 executioner armed MEASURE (simulated, no real order) / 3 executioner armed LIVE
        //                4 ignored (strategy not Realtime) / 5 config applied / 6 flatten-cancel done / 7 unknown op / 8 error / 9 pong
        private void PublishStatus()
        {
            if (fillView == null) return;
            // Bit 32 = simulation account. On Sim101 there is no broker and no exchange: order
            // "latency" is NinjaTrader's own simulator and says nothing about a live route.
            bool simAccount = false;
            try { string an = Account.Name; simAccount = !string.IsNullOrEmpty(an) && an.StartsWith("Sim", StringComparison.OrdinalIgnoreCase); } catch { }
            int flags = (State == State.Realtime ? 1 : 0) | (execEnabled ? 2 : 0) | (execMode == 1 ? 4 : 0) | (executionPaused ? 8 : 0) | (dailyLossTripped ? 16 : 0) | (simAccount ? 32 : 0);
            int wCount = cachedWorkingCount;   // v64n: cached on the strategy thread - see RefreshWorkingEntryCache
            flags |= (Math.Min(255, wCount) << 8);   // bits 8-15: working ENTRY orders (unfilled)
            // v64y bits 16-23: which entry gate is currently blocking (0 = none), so the
            // dashboard can say WHY entries stopped even for orders it never saw.
            int liveGate = lastEntryGateBlock;
            if (gatesEnabled)
            {
                // Report the standing state, not only the last rejection - a gate that is
                // blocking right now should show even if nothing has tried to enter yet.
                if (executionPaused) liveGate = 1;
                else if (dailyLossTripped) liveGate = 2;
                else if (!InsideTradingHours()) liveGate = 3;
                else if (!InsideGateSessionWindow()) liveGate = 6;
                else if (neutralBlocked) liveGate = 7;
                else if (gMaxTradesPerSession > 0 && entriesThisSession >= gMaxTradesPerSession) liveGate = 10;
                else if (gCooldownSeconds > 0 && cooldownUntilTicks != 0 && UtcNowTicks() < cooldownUntilTicks) liveGate = 12;
                // v65a: these two are standing states, not momentary - show them even when
                // nothing has tried to enter, because both mean "stopped" rather than "waiting".
                else if (gFeedStaleMs > 0 && lastMarketDataTicks != 0 &&
                         (UtcNowTicks() - lastMarketDataTicks) > (long)(gFeedStaleMs * TimeSpan.TicksPerMillisecond)) liveGate = 21;
                else if (gGiveBackPercent > 0 && peakRealizedTicks > 0 &&
                         realizedTicksToday <= peakRealizedTicks * (1.0 - gGiveBackPercent / 100.0)) liveGate = 22;
            }
            flags |= (Math.Min(255, Math.Max(0, liveGate)) << 16);
            int signed = 0;
            try { signed = Position.MarketPosition == MarketPosition.Long ? Position.Quantity : Position.MarketPosition == MarketPosition.Short ? -Position.Quantity : 0; } catch { }
            int exStateCode = exSide == 0 ? 0 : exState == 0 ? 1 : exState == 1 ? 2 : 3;
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = UtcNowTicks(),
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = lastCommandIdProcessed,
                NtOrderIdLow = cmdReadSequence,
                Price = realizedTicksToday,
                Qty = signed,
                FilledQty = flags,
                OrderState = exStateCode,
                OrderAction = exSide > 0 ? 1 : exSide < 0 ? 2 : 0,
                MarketPosition = exStateCode == 1 ? lastGateBlock : 0,
                IsExecution = 4
            };
            WriteFillRecord(ref payload);
        }

        // Record type 6 = RISK CONFIG (every 5 s): what the strategy will actually do
        // with an entry. Price = DefaultStopLossTicks, Qty = PT ticks x100,
        // FilledQty = trail ticks x100, OrderState = MaxPositionSize,
        // OrderAction = MaxOrdersPerMinute, NtOrderIdLow = DailyLossLimitTicks x100,
        // MarketPosition = 1 if trading hours are set (else 0), CommandId = 0.
        private void PublishRiskConfig()
        {
            if (fillView == null) return;
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = UtcNowTicks(),
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = 0,
                NtOrderIdLow = (long)Math.Round(DailyLossLimitTicks * 100),
                Price = DefaultStopLossTicks,
                Qty = (int)Math.Round(DefaultProfitTargetTicks * 100),
                FilledQty = (int)Math.Round(DefaultTrailTicks * 100),
                OrderState = MaxPositionSize,
                OrderAction = MaxOrdersPerMinute,
                MarketPosition = string.IsNullOrWhiteSpace(TradingStart) || string.IsNullOrWhiteSpace(TradingEnd) ? 0 : 1,
                IsExecution = 6
            };
            WriteFillRecord(ref payload);
        }

        // Record type 7 = ORDER PATH TIMING, one per order command. Field meanings:
        //   Price      = ring hop ms   (sender wrote -> this strategy's reader thread had it)
        //   Qty        = marshal us    (reader thread -> NinjaTrader's strategy thread)
        //   FilledQty  = submit us     (time inside ProcessCommand, incl. SubmitOrderUnmanaged)
        //   OrderAction= op            NtOrderIdLow = recv ticks   CommandId = command id
        private void PublishOrderPathTiming(CommandPayload command, long recvTicks, long procTicks, long doneTicks)
        {
            if (fillView == null) return;
            double hopMs = command.SendUtcTicks > 0 && recvTicks >= command.SendUtcTicks
                ? (recvTicks - command.SendUtcTicks) / (double)TimeSpan.TicksPerMillisecond : -1.0;
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = UtcNowTicks(),
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = command.CommandId,
                NtOrderIdLow = recvTicks,
                Price = hopMs,
                Qty = (int)Math.Min(int.MaxValue, Math.Max(0, (procTicks - recvTicks) / 10)),
                FilledQty = (int)Math.Min(int.MaxValue, Math.Max(0, (doneTicks - procTicks) / 10)),
                OrderState = 0,
                OrderAction = command.Op,
                MarketPosition = execMode,
                IsExecution = 7
            };
            WriteFillRecord(ref payload);
        }

        // Record type 8 = NT8 EVENT DELIVERY. NinjaTrader stamps each order event with its own
        // time; this reports how long after that stamp our callback actually ran, in NT8's own
        // clock domain (both DateTime.Now, so no cross-clock error). It splits the remaining
        // unexplained latency in two:
        //   small delivery  -> NinjaTrader's order engine / the broker genuinely took that long
        //   large delivery  -> the work was done quickly and NinjaTrader was slow to TELL us
        //   Price = delivery ms   Qty = order state   OrderAction = 0 order update / 1 execution
        private void PublishEventDelivery(long commandId, DateTime ntEventTime, int orderState, bool isExecution)
        {
            if (fillView == null) return;
            double deliveryMs;
            try { deliveryMs = (DateTime.Now - ntEventTime).TotalMilliseconds; } catch { return; }
            if (deliveryMs < 0 || deliveryMs > 600000) return;   // unset / bogus platform timestamp
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = 0,
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = commandId,
                NtOrderIdLow = 0,
                Price = deliveryMs,
                Qty = orderState,
                FilledQty = 0,
                OrderState = 0,
                OrderAction = isExecution ? 1 : 0,
                MarketPosition = 0,
                IsExecution = 8
            };
            WriteFillRecord(ref payload);
        }

        private void PublishAck(CommandPayload command, int result)
        {
            if (fillView == null) return;
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = UtcNowTicks(),
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = command.CommandId,
                NtOrderIdLow = cmdReadSequence,
                Price = command.Price,
                Qty = command.Qty,
                FilledQty = 0,
                OrderState = result,
                OrderAction = command.Op,
                MarketPosition = execMode,
                IsExecution = 5
            };
            WriteFillRecord(ref payload);
        }

        private void WriteFillRecord(ref FillPayload payload)
        {
            lock (fillLock)
            {
                if (fillView == null) return;
                long next = fillWriteSequence + 1;
                long offset = HeaderBytes + (next & (Capacity - 1)) * SlotBytes;
                fillView.Write(offset, next * 2 + 1);
                Thread.MemoryBarrier();
                fillView.Write(offset + 8, ref payload);
                Thread.MemoryBarrier();
                fillView.Write(offset, next * 2);
                Thread.MemoryBarrier();
                fillView.Write(0, next);
                fillWriteSequence = next;
            }
        }

        private static string OpName(int op)
        {
            switch (op)
            {
                case 0: return "buy market"; case 1: return "sell market"; case 2: return "buy limit"; case 3: return "sell limit";
                case 4: return "buy stop"; case 5: return "sell stop"; case 8: return "exit long"; case 9: return "exit short";
                case 12: return "cancel all"; case 13: return "flatten"; case 14: return "cancel entries"; case 20: return "pause"; case 21: return "resume";
                case 22: return "max position"; case 23: return "daily loss limit"; case 24: return "default bracket";
                case 30: return "buy limit join"; case 31: return "sell limit join"; case 40: return "exec enable/mode";
                case 41: return "exec gates"; case 42: return "exec management"; case 43: return "exec markouts"; case 44: return "queue model";
                case 45: return "exec policy"; case 46: return "signal context"; case 47: return "next-entry bracket"; case 99: return "ping";
                default: return "op " + op;
            }
        }

        // ==================== command dispatch (strategy thread) ====================

        private void ProcessCommand(CommandPayload command) { ProcessCommand(command, UtcNowTicks()); }

        private void ProcessCommand(CommandPayload command, long recvTicks)
        {
            long procTicks = UtcNowTicks();
            commandsProcessed++; lastCommandIdProcessed = command.CommandId;
            if (command.SendUtcTicks > 0 && command.CommandId > 0)
            {
                if (commandSendTicks.Count > 512) commandSendTicks.Clear();
                commandSendTicks[command.CommandId] = command.SendUtcTicks;
            }
            if (State != State.Realtime)
            {
                Note("IGNORED #" + command.CommandId + " " + OpName(command.Op) + ": strategy state is " + State + ", not Realtime.");
                PublishAck(command, 4);
                return;
            }
            int qty = Math.Max(1, command.Qty);
            string name = "LNNCMD:" + command.CommandId;
            pendingAck = -1;
            try
            {
                switch (command.Op)
                {
                    case 99:
                        Note("PONG #" + command.CommandId + ": LnnOrderExecStrategy alive on " + Instrument.FullName + " (" + Account.Name + "), listening on " + CmdName() +
                             ", consumed " + commandsProcessed + " commands, executioner " + (execEnabled ? (execMode == 1 ? "ENABLED LIVE" : "ENABLED MEASURE (simulated only)") : "DISABLED") +
                             ", position " + PositionText() + (executionPaused ? ", PAUSED" : "") + (dailyLossTripped ? ", daily-loss TRIPPED" : "") + ".");
                        pendingAck = 9;
                        break;
                    case 0:  SubmitEntryGated(OrderAction.Buy, OrderType.Market, qty, 0, 0, name, command); break;
                    case 1:  SubmitEntryGated(OrderAction.Sell, OrderType.Market, qty, 0, 0, name, command); break;
                    case 2:  SubmitEntryGated(OrderAction.Buy, OrderType.Limit, qty, command.Price, 0, name, command); break;
                    case 3:  SubmitEntryGated(OrderAction.Sell, OrderType.Limit, qty, command.Price, 0, name, command); break;
                    case 4:  SubmitEntryGated(OrderAction.Buy, OrderType.StopMarket, qty, 0, command.Price, name, command); break;
                    case 5:  SubmitEntryGated(OrderAction.Sell, OrderType.StopMarket, qty, 0, command.Price, name, command); break;
                    case 8:  if (Position.MarketPosition == MarketPosition.Long) Submit(OrderAction.Sell, OrderType.Market, Position.Quantity, 0, 0, name, command); break;
                    case 9:  if (Position.MarketPosition == MarketPosition.Short) Submit(OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, name, command); break;
                    case 12: DisarmExecutioner("cancel-all"); CancelAllWorking(); break;
                    case 14: { DisarmExecutioner("cancel-entries"); int n = CancelWorkingEntries(); Note("ACK #" + command.CommandId + " cancel entries: " + n + " working entry order(s) cancelled; exits untouched."); pendingAck = 6; break; }
                    case 13: DisarmExecutioner("flatten"); GoFlat(name); break;
                    // Passive limit entries priced from NT8's live book (join the
                    // touch so you post liquidity / earn the spread instead of
                    // paying it). Optional tick offset carried in StopPrice.
                    // When the executioner is enabled these ARM the managed
                    // controller instead of firing a one-shot limit.
                    case 45:
                        // policy: Price = 0 gates / 1 EV; StopPrice = expected move ticks (>0); Qty = 1 -> reload the model file
                        exPolicy = command.Price >= 0.5 ? 1 : 0;
                        if (command.StopPrice > 0) exExpectedMoveTicks = command.StopPrice;
                        if (command.Qty == 1) fillModel = FillModel.Load(FillModelFile);
                        if (exPolicy == 1 && fillModel == null) { exPolicy = 0; Note("LnnOrderExecStrategy: EV policy requested but no fill model loaded (set FillModelFile) - staying on gates."); }
                        else Note("LnnOrderExecStrategy: exec policy = " + (exPolicy == 1 ? "EV (fill model + P(up), expected move " + exExpectedMoveTicks.ToString("0.0") + "t)" : "gates") + ".");
                        break;
                    case 47:
                        // bracket for the NEXT entry, in ticks from the fill: Price = stop, StopPrice = target, StopLossPrice = trail (0 = none)
                        nextBracketSlTicks = Math.Max(0, command.Price); nextBracketPtTicks = Math.Max(0, command.StopPrice); nextBracketTrailTicks = Math.Max(0, command.StopLossPrice);
                        nextBracketSet = nextBracketSlTicks > 0 || nextBracketPtTicks > 0 || nextBracketTrailTicks > 0;
                        break;
                    case 28:   // v65a book-quality gates
                        gMinTopOfBookSize = (long)Math.Max(0, command.Qty);
                        gMaxSpreadTicks   = Math.Max(0, command.Price);
                        gMinImbalance     = Math.Max(0, command.StopPrice);
                        gMaxFlickerPerSec = Math.Max(0, command.StopLossPrice);
                        gMaxAggressionInto = (long)Math.Max(0, command.ProfitTargetPrice);
                        gStaleSignalMs    = Math.Max(0, command.TrailingStopDistance);
                        gMicropriceGate   = (command.Tif & 1) != 0;
                        Note("LnnOrderExecStrategy: gates C (book) — spread <= " + (gMaxSpreadTicks > 0 ? gMaxSpreadTicks.ToString("0.#") + "t" : "off") +
                             ", imbalance >= " + (gMinImbalance > 0 ? gMinImbalance.ToString("0.00") : "off") +
                             ", microprice " + (gMicropriceGate ? "on" : "off") +
                             ", flicker <= " + (gMaxFlickerPerSec > 0 ? gMaxFlickerPerSec.ToString("0.#") + "/s" : "off") +
                             ", aggression < " + (gMaxAggressionInto > 0 ? gMaxAggressionInto.ToString() : "off") +
                             ", book >= " + (gMinTopOfBookSize > 0 ? gMinTopOfBookSize.ToString() : "off") +
                             ", signal age <= " + (gStaleSignalMs > 0 ? gStaleSignalMs.ToString("0") + "ms" : "off") + ".");
                        break;
                    case 29:   // v65a health / give-back gates
                        gFeedStaleMs     = Math.Max(0, command.Price);
                        gGiveBackPercent = Math.Max(0, command.StopPrice);
                        Note("LnnOrderExecStrategy: gates D (health) — feed stale > " +
                             (gFeedStaleMs > 0 ? gFeedStaleMs.ToString("0") + "ms blocks" : "off") +
                             ", give-back " + (gGiveBackPercent > 0 ? gGiveBackPercent.ToString("0.#") + "% of peak stops the day" : "off") + ".");
                        break;
                    case 27:   // v64z: the paper thresholds the confidence margin is measured against
                        if (command.Price > 0.5 && command.Price <= 1.0) gLongThreshold = command.Price;
                        if (command.StopPrice >= 0.0 && command.StopPrice < 0.5) gShortThreshold = command.StopPrice;
                        Note("LnnOrderExecStrategy: gate thresholds — long >= " + gLongThreshold.ToString("0.###") +
                             ", short <= " + gShortThreshold.ToString("0.###") + ".");
                        break;
                    case 46:
                        // signal context for the NEXT entry: Price = P(up) (0..1)
                        if (command.Price >= 0 && command.Price <= 1)
                        {
                            exSignalPUp = command.Price;
                            // v64y: the same number drives the dead-regime and confidence-margin
                            // gates. The engine already sends this before every direct entry, so
                            // those gates work on the engine-direct path with no engine change.
                            lastSignalPUp = command.Price; lastSignalPUpTicks = UtcNowTicks();
                            TrackNeutralRegime(lastSignalPUp, lastSignalPUpTicks);
                        }
                        break;
                    case 44:
                        exCancelAheadFactor = Math.Max(0, Math.Min(1, command.Price));
                        Note("LnnOrderExecStrategy: queue model cancel-ahead factor = " + exCancelAheadFactor.ToString("0.00") + (exCancelAheadFactor <= 0 ? " (legacy: trades only)" : exCancelAheadFactor >= 1 ? " (uniform cancels)" : "") + ".");
                        break;
                    case 30:
                        if (execEnabled) { ArmExecutioner(+1, qty); pendingAck = execMode == 1 ? 3 : 2; Note("ACK #" + command.CommandId + " buy limit join: executioner ARMED LONG x" + qty + (execMode == 1 ? " (LIVE: will post a real limit once the gate passes)" : " (MEASURE mode: simulated only - NO real order will be placed)") + "."); }
                        else SubmitEntryGated(OrderAction.Buy, OrderType.Limit, qty, JoinPrice(true, command.StopPrice), 0, name, command);
                        break;
                    case 31:
                        if (execEnabled) { ArmExecutioner(-1, qty); pendingAck = execMode == 1 ? 3 : 2; Note("ACK #" + command.CommandId + " sell limit join: executioner ARMED SHORT x" + qty + (execMode == 1 ? " (LIVE: will post a real limit once the gate passes)" : " (MEASURE mode: simulated only - NO real order will be placed)") + "."); }
                        else SubmitEntryGated(OrderAction.Sell, OrderType.Limit, qty, JoinPrice(false, command.StopPrice), 0, name, command);
                        break;
                    // ==== Limit executioner control ====
                    case 40: // enable + mode
                        execEnabled = command.Qty != 0;
                        execMode = command.Price >= 0.5 ? 1 : 0;
                        if (!execEnabled) DisarmExecutioner("disabled");
                        Note("LnnOrderExecStrategy: executioner " + (execEnabled ? ("ENABLED (" + (execMode == 1 ? "LIVE — real orders" : "MEASURE — simulate only") + ")") : "DISABLED") + ".");
                        break;
                    case 41: // posting gates
                        exPostImbalanceMin = command.Price;
                        exRestingMsMin = (int)Math.Max(0, command.StopPrice);
                        exFlickerMaxPerSec = Math.Max(0, command.StopLossPrice);
                        exMicropriceGate = command.ProfitTargetPrice >= 0.5;
                        Note("LnnOrderExecStrategy: exec gates — imbalance>=" + exPostImbalanceMin.ToString("0.00") + ", resting>=" + exRestingMsMin + "ms, flicker<=" + exFlickerMaxPerSec.ToString("0.#") + "/s, microprice-gate=" + exMicropriceGate + ".");
                        break;
                    case 42: // management
                        exAggressionCancelQty = (int)Math.Max(0, command.Price);
                        exAggressionWindowMs = (int)Math.Max(50, command.StopPrice);
                        exMaxChaseTicks = (int)Math.Max(0, command.StopLossPrice);
                        exTimeoutMs = (int)Math.Max(0, command.ProfitTargetPrice);
                        exOnTimeout = command.TrailingStopDistance >= 0.5f ? 1 : 0;
                        Note("LnnOrderExecStrategy: exec mgmt — cancel@" + exAggressionCancelQty + " in " + exAggressionWindowMs + "ms, chase<=" + exMaxChaseTicks + "t, timeout " + exTimeoutMs + "ms -> " + (exOnTimeout == 1 ? "cross" : "abandon") + ".");
                        break;
                    case 43: // markout windows
                        exMarkout1Ms = (int)Math.Max(0, command.Price);
                        exMarkout2Ms = (int)Math.Max(0, command.StopPrice);
                        Note("LnnOrderExecStrategy: exec markout windows " + exMarkout1Ms + "ms / " + exMarkout2Ms + "ms.");
                        break;
                    // ==== Live tuning from the dashboard ====
                    case 20: executionPaused = true;  Note("LnnOrderExecStrategy: execution PAUSED by dashboard (entries blocked; Flat/Cancel still work)."); break;
                    case 21:
                        executionPaused = false;
                        // v64z: ARM starts a fresh session for the counting gates, exactly as the
                        // dashboard does on its own side. Without this, a strategy left running
                        // stays blocked on "max trades per session" until the calendar day rolls,
                        // and a re-arm looks like it does nothing.
                        entriesThisSession = 0; sessionWins = 0; cooldownUntilTicks = 0;
                        lastEntrySide = 0; lastEntryUtcTicks = 0; entryFlipTimes.Clear();
                        neutralBlocked = false; neutralSinceTicks = 0; directionalSinceTicks = 0;
                        lastEntryGateBlock = 0;
                        Note("LnnOrderExecStrategy: execution RESUMED by dashboard (entry-gate counters reset).");
                        break;
                    case 22: MaxPositionSize = Math.Max(1, command.Qty); Note("LnnOrderExecStrategy: max position set to " + MaxPositionSize + "."); break;
                    case 23: DailyLossLimitTicks = Math.Max(0, command.Price); dailyLossTripped = false;
                             Note("LnnOrderExecStrategy: daily loss limit set to " + DailyLossLimitTicks + " ticks" + (DailyLossLimitTicks <= 0 ? " (off)." : ".")); break;
                    case 24: DefaultStopLossTicks = Math.Max(0, command.StopLossPrice);
                             DefaultProfitTargetTicks = Math.Max(0, command.ProfitTargetPrice);
                             DefaultTrailTicks = Math.Max(0, command.TrailingStopDistance);
                             Note("LnnOrderExecStrategy: default bracket set — SL " + DefaultStopLossTicks + "t, PT " + DefaultProfitTargetTicks + "t, trail " + DefaultTrailTicks + "t."); break;
                    // ==== v64y: entry gates pushed over the ring ====
                    case 25:
                        gMaxTradesPerSession = Math.Max(0, command.Qty);
                        gTradeGapSeconds     = Math.Max(0, command.Price);
                        gCooldownSeconds     = Math.Max(0, command.StopPrice);
                        gConfidenceMargin    = Math.Max(0, command.StopLossPrice);
                        gNeutralBlockSeconds = Math.Max(0, command.ProfitTargetPrice);
                        gNeutralClearSeconds = command.TrailingStopDistance > 0 ? command.TrailingStopDistance : 3.0;
                        gDirectionFilter     = command.Tif > 2 ? 0 : command.Tif;
                        Note("LnnOrderExecStrategy: gates A — max trades " + (gMaxTradesPerSession > 0 ? gMaxTradesPerSession.ToString() : "off") +
                             ", gap " + gTradeGapSeconds.ToString("0.#") + "s, cooldown " + gCooldownSeconds.ToString("0.#") +
                             "s, margin " + gConfidenceMargin.ToString("0.###") + ", dead-regime " + gNeutralBlockSeconds.ToString("0.#") +
                             "s/clear " + gNeutralClearSeconds.ToString("0.#") + "s, direction " +
                             (gDirectionFilter == 1 ? "longs only" : gDirectionFilter == 2 ? "shorts only" : "both") + ".");
                        break;
                    case 26:
                        gMaxFlipsPer10Min   = Math.Max(0, command.Qty);
                        gSessionStartMinutes = command.Price   >= 0 ? (int)command.Price     : -1;
                        gSessionEndMinutes   = command.StopPrice >= 0 ? (int)command.StopPrice : -1;
                        gDeadZoneLow  = command.StopLossPrice     > 0 ? command.StopLossPrice     : 0.45;
                        gDeadZoneHigh = command.ProfitTargetPrice > 0 ? command.ProfitTargetPrice : 0.55;
                        gRampWinsPerStep = (int)Math.Max(0, command.TrailingStopDistance);
                        gatesEnabled     = (command.Tif & 1) != 0;
                        gBarrierMode     = (command.Tif & 2) != 0;
                        gFlipOnOpposite  = (command.Tif & 4) != 0;
                        if (!gatesEnabled) { neutralBlocked = false; neutralSinceTicks = 0; directionalSinceTicks = 0; lastEntryGateBlock = 0; }
                        Note("LnnOrderExecStrategy: gates B — " + (gatesEnabled ? "ENABLED" : "disabled (v64x behaviour)") +
                             ", max flips " + (gMaxFlipsPer10Min > 0 ? gMaxFlipsPer10Min + "/10min" : "off") +
                             ", session " + (gSessionStartMinutes < 0 ? "off" : MinutesText(gSessionStartMinutes) + "-" + MinutesText(gSessionEndMinutes)) +
                             ", dead zone " + gDeadZoneLow.ToString("0.###") + "-" + gDeadZoneHigh.ToString("0.###") +
                             ", ramp " + (gRampWinsPerStep > 0 ? "+1 per " + gRampWinsPerStep + " wins" : "off") +
                             (gBarrierMode ? ", barrier mode" : "") + (gFlipOnOpposite ? ", flip on opposite" : "") + ".");
                        break;
                    default: Note("LnnOrderExecStrategy: unknown op " + command.Op + " ignored."); pendingAck = 7; break;
                }
                if (command.Op <= 5 || command.Op == 13 || command.Op == 30 || command.Op == 31 || command.Op == 99)
                    PublishOrderPathTiming(command, recvTicks, procTicks,
                        lastOrderCallDoneTicks > procTicks ? lastOrderCallDoneTicks : UtcNowTicks());
                if (pendingAck < 0)
                {
                    // Per-op default when no handler set a result: order ops are 'submitted',
                    // exits 'flatten/cancel done', everything else 'config applied'.
                    pendingAck = command.Op <= 5 || command.Op == 30 || command.Op == 31 ? 0 : command.Op <= 13 ? 6 : 5;
                }
                PublishAck(command, pendingAck);
            }
            catch (Exception error)
            {
                Note("LnnOrderExecStrategy command #" + command.CommandId + " (" + OpName(command.Op) + ") FAILED: " + error.Message);
                PublishAck(command, 8);
            }
        }

        private string PositionText()
        {
            try
            {
                return Position.MarketPosition == MarketPosition.Long ? "LONG " + Position.Quantity :
                       Position.MarketPosition == MarketPosition.Short ? "SHORT " + Position.Quantity : "FLAT";
            }
            catch { return "?"; }
        }

        // Entry path with all safety gates. Exits (Flat, CancelAll, ExitLong/Short)
        // bypass the gates on purpose — reducing risk is always allowed.
        private void SubmitEntryGated(OrderAction action, OrderType type, int qty, double limitPrice, double stopPrice, string name, CommandPayload command)
        {
            RollRiskDay();
            if (executionPaused) { lastEntryGateBlock = 1; Reject(name, "execution paused"); return; }
            if (dailyLossTripped) { lastEntryGateBlock = 2; Reject(name, "daily loss limit hit (" + realizedTicksToday.ToString("0.##") + "t)"); return; }
            if (!InsideTradingHours()) { lastEntryGateBlock = 3; Reject(name, "outside trading hours " + TradingStart + "-" + TradingEnd); return; }

            int signedNow = Position.MarketPosition == MarketPosition.Long ? Position.Quantity :
                            Position.MarketPosition == MarketPosition.Short ? -Position.Quantity : 0;
            bool buying = action == OrderAction.Buy || action == OrderAction.BuyToCover;

            // v64y: the gates the dashboard used to apply before forwarding. They run here so
            // engine-direct entries - which the dashboard never sees - are held to the same rules.
            //
            // v64z FIX: an order OPPOSITE an open position reduces or reverses it, and blocking
            // that traps the position - the exact failure the "exits are never gated" rule exists
            // to prevent. Ops 0/1 are used both to open and to flip, so the side alone does not
            // say which. Only gate orders that open or ADD TO exposure.
            bool reducesExposure = signedNow != 0 && (buying ? signedNow < 0 : signedNow > 0);
            if (!reducesExposure)
            {
                int gateBlock = EntryGateBlock(buying);
                if (gateBlock != 0)
                {
                    lastEntryGateBlock = gateBlock;
                    Reject(name, GateBlockName(gateBlock) + " (entry gate)");
                    return;
                }
            }

            // Position-size cap: what would the position be if this fully fills?
            // v64k: unfilled entry orders count as if filled, so a second entry cannot
            // stack on a resting first one (the cause of "more than one trade at a time").
            int workingCount, workingSigned; WorkingEntries(out workingCount, out workingSigned);
            cachedWorkingCount = workingCount; cachedWorkingSigned = workingSigned;   // v64n: keep the heartbeat's copy fresh
            int projected = signedNow + workingSigned + (buying ? qty : -qty);
            if (Math.Abs(projected) > Math.Max(1, MaxPositionSize))
            { lastEntryGateBlock = 4; Reject(name, "would exceed max position " + MaxPositionSize + (workingCount > 0 ? " (" + workingCount + " entry order(s) still working: " + workingSigned + ")" : "")); return; }

            // Order-rate cap
            DateTime now = DateTime.UtcNow;
            while (recentOrderTimes.Count > 0 && (now - recentOrderTimes.Peek()).TotalSeconds > 60) recentOrderTimes.Dequeue();
            if (MaxOrdersPerMinute > 0 && recentOrderTimes.Count >= MaxOrdersPerMinute) { lastEntryGateBlock = 5; Reject(name, "max " + MaxOrdersPerMinute + " orders/minute"); return; }
            recentOrderTimes.Enqueue(now);

            // v64y: this entry is going through. Record it for the gates that count entries,
            // and apply the size ramp. Everything above must have passed first.
            // v64z: a reducing/reversing order is not a new entry - counting it would burn the
            // session budget and register a phantom flip every time a position is closed.
            if (!reducesExposure)
            {
                lastEntryGateBlock = 0;
                qty = RampQuantity(qty);
                entriesThisSession++;
                lastEntryUtcTicks = UtcNowTicks();
                int side = buying ? 1 : -1;
                if (lastEntrySide != 0 && side != lastEntrySide) entryFlipTimes.Enqueue(DateTime.UtcNow);
                lastEntrySide = side;
            }

            Submit(action, type, qty, limitPrice, stopPrice, name, command);
        }

        // Price a passive limit at the current touch (± an optional tick offset,
        // positive = more passive / deeper in the book). Falls back to last
        // price if the book isn't available yet.
        private double JoinPrice(bool buy, double offsetTicks)
        {
            double tick = Instrument.MasterInstrument.TickSize;
            double touch;
            try { touch = buy ? GetCurrentBid() : GetCurrentAsk(); }
            catch { touch = Close[0]; }
            if (touch <= 0) touch = Close[0];
            double offset = (offsetTicks > 0 ? offsetTicks : 0) * tick;
            double price = buy ? touch - offset : touch + offset;
            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        private void Reject(string name, string reason)
        {
            pendingAck = 1;
            Note("REJECTED " + name + ": " + reason);
        }

        private bool InsideTradingHours()
        {
            TimeSpan start, end;
            if (!TimeSpan.TryParse(TradingStart, out start) || !TimeSpan.TryParse(TradingEnd, out end)) return true;
            TimeSpan now = DateTime.Now.TimeOfDay;
            return start <= end ? now >= start && now <= end : now >= start || now <= end;
        }

        // ==================== v64y gate helpers ====================
        private static string MinutesText(int minutes)
        {
            if (minutes < 0) return "off";
            return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
        }

        // The dashboard's session window, pushed over op 26. Separate from TradingStart/End
        // (the strategy's own hard limit) so both can apply; either one can be off.
        private bool InsideGateSessionWindow()
        {
            if (gSessionStartMinutes < 0 || gSessionEndMinutes < 0) return true;
            int now = (int)DateTime.Now.TimeOfDay.TotalMinutes;
            return gSessionStartMinutes <= gSessionEndMinutes
                ? now >= gSessionStartMinutes && now <= gSessionEndMinutes
                : now >= gSessionStartMinutes || now <= gSessionEndMinutes;   // window crosses midnight
        }

        // Dead-regime hysteresis, run on every op 46 the engine sends. Blocking takes
        // sustained neutrality; clearing takes a HELD directional read, so one spike out
        // of a flat tape does not re-open the gate.
        private void TrackNeutralRegime(double pUp, long nowTicks)
        {
            if (!gatesEnabled || gNeutralBlockSeconds <= 0 || double.IsNaN(pUp))
            { neutralBlocked = false; neutralSinceTicks = 0; directionalSinceTicks = 0; return; }

            bool neutral = pUp >= gDeadZoneLow && pUp <= gDeadZoneHigh;
            if (neutral)
            {
                directionalSinceTicks = 0;
                if (neutralSinceTicks == 0) neutralSinceTicks = nowTicks;
                else if (!neutralBlocked &&
                         (nowTicks - neutralSinceTicks) >= (long)(gNeutralBlockSeconds * TimeSpan.TicksPerSecond))
                {
                    neutralBlocked = true;
                    Note("LnnOrderExecStrategy: dead-regime gate ON — no directional read for " +
                         gNeutralBlockSeconds.ToString("0") + "s; entries blocked, exits and brackets unaffected.");
                }
            }
            else
            {
                neutralSinceTicks = 0;
                if (directionalSinceTicks == 0) directionalSinceTicks = nowTicks;
                if (neutralBlocked &&
                    (nowTicks - directionalSinceTicks) >= (long)(gNeutralClearSeconds * TimeSpan.TicksPerSecond))
                {
                    neutralBlocked = false;
                    Note("LnnOrderExecStrategy: dead-regime gate OFF — directional read held " +
                         gNeutralClearSeconds.ToString("0") + "s; entries re-enabled.");
                }
            }
        }

        // The v64y gate chain. Returns 0 to allow, or the block code (see GateBlockName).
        // Entries only - every caller has already established this is an entry.
        private int EntryGateBlock(bool buying)
        {
            if (!gatesEnabled) return 0;
            long now = UtcNowTicks();

            if (!InsideGateSessionWindow()) return 6;
            if (neutralBlocked) return 7;

            // Confidence margin: the entry must beat the paper threshold by this much.
            // Uses the P(up) the engine stamped on this signal (op 46). Stale or absent
            // context does NOT block - the gate is opt-in and must not strand a live setup.
            if (gConfidenceMargin > 0 && !double.IsNaN(lastSignalPUp) &&
                (now - lastSignalPUpTicks) <= 5 * TimeSpan.TicksPerSecond)
            {
                bool confident = buying
                    ? lastSignalPUp >= gLongThreshold + gConfidenceMargin
                    : lastSignalPUp <= gShortThreshold - gConfidenceMargin;
                if (!confident) return 8;
            }

            if (gDirectionFilter == 1 && !buying) return 9;
            if (gDirectionFilter == 2 && buying) return 9;
            if (gMaxTradesPerSession > 0 && entriesThisSession >= gMaxTradesPerSession) return 10;
            if (gTradeGapSeconds > 0 && lastEntryUtcTicks != 0 &&
                (now - lastEntryUtcTicks) < (long)(gTradeGapSeconds * TimeSpan.TicksPerSecond)) return 11;
            if (gCooldownSeconds > 0 && cooldownUntilTicks != 0 && now < cooldownUntilTicks) return 12;

            if (gMaxFlipsPer10Min > 0)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-10);
                while (entryFlipTimes.Count > 0 && entryFlipTimes.Peek() < cutoff) entryFlipTimes.Dequeue();
                if (entryFlipTimes.Count >= gMaxFlipsPer10Min) return 13;
            }

            // ===== v65a: health first - a gate that fires because we are blind beats one that
            // fires on numbers we can no longer trust. =====
            if (gFeedStaleMs > 0 && lastMarketDataTicks != 0 &&
                (now - lastMarketDataTicks) > (long)(gFeedStaleMs * TimeSpan.TicksPerMillisecond)) return 21;

            if (gGiveBackPercent > 0 && peakRealizedTicks > 0 &&
                realizedTicksToday <= peakRealizedTicks * (1.0 - gGiveBackPercent / 100.0)) return 22;

            if (gStaleSignalMs > 0 &&
                (double.IsNaN(lastSignalPUp) || lastSignalPUpTicks == 0 ||
                 (now - lastSignalPUpTicks) > (long)(gStaleSignalMs * TimeSpan.TicksPerMillisecond))) return 20;

            // ===== v65a: book quality. Everything here is already maintained by the executioner
            // from OnMarketDepth; until now it only decided where a passive limit posted. =====
            bool needsBook = gMaxSpreadTicks > 0 || gMinImbalance > 0 || gMicropriceGate || gMinTopOfBookSize > 0;
            if (needsBook && (tobBidPrice <= 0 || tobAskPrice <= 0)) return 19;

            if (gMaxSpreadTicks > 0)
            {
                double tick = Instrument.MasterInstrument.TickSize;
                if (tick > 0 && (tobAskPrice - tobBidPrice) / tick > gMaxSpreadTicks) return 14;
            }
            // Size on the side we would actually take: a market buy lifts the ask.
            if (gMinTopOfBookSize > 0 && (buying ? tobAskSize : tobBidSize) < gMinTopOfBookSize) return 19;

            if (gMinImbalance > 0)
            {
                double imbalance = Imbalance();       // +1 all bid, -1 all ask
                if ((buying ? imbalance : -imbalance) < gMinImbalance) return 15;
            }
            if (gMicropriceGate)
            {
                double mp = Microprice(), mid = Mid();
                if (buying ? mp < mid : mp > mid) return 16;
            }

            // The flicker and aggression queues are stamped from NinjaTrader's event times,
            // which are LOCAL - they must be compared against DateTime.Now, not UtcNow.
            DateTime nowLocal = DateTime.Now;
            if (gMaxFlickerPerSec > 0 && FlickerPerSec(nowLocal) > gMaxFlickerPerSec) return 17;
            // Unlike the executioner's posting gate (which fears the OTHER side hitting a resting
            // order), a marketable entry is hurt by aggression on OUR OWN side running the price
            // away from us - so the side asked for here is inverted relative to ShouldPost.
            if (gMaxAggressionInto > 0 && AggrInto(!buying, nowLocal) >= gMaxAggressionInto) return 18;

            return 0;
        }

        // Trade-size ramp: start at 1 and add a contract per N wins, capped at what was asked for.
        private int RampQuantity(int requested)
        {
            if (!gatesEnabled || gRampWinsPerStep <= 0) return requested;
            return Math.Max(1, Math.Min(requested, 1 + sessionWins / gRampWinsPerStep));
        }

        private void RollRiskDay()
        {
            if (riskDay.Date == DateTime.Now.Date) return;
            riskDay = DateTime.Now;
            realizedTicksToday = 0;
            dailyLossTripped = false;
            // v64y: the per-session gate counters roll with the risk day too, otherwise
            // "max trades per session" would never reset on a strategy left running overnight.
            entriesThisSession = 0; sessionWins = 0; cooldownUntilTicks = 0;
            lastEntrySide = 0; entryFlipTimes.Clear(); lastEntryGateBlock = 0;
            peakRealizedTicks = 0;   // v65a: the give-back gate measures a single day
        }

        private void Submit(OrderAction action, OrderType type, int qty, double limitPrice, double stopPrice, string name, CommandPayload command)
        {
            // Normalize direction against the current position so a plain Sell
            // while flat becomes a short entry, and a Buy while short covers.
            if (action == OrderAction.Buy && Position.MarketPosition == MarketPosition.Short) action = OrderAction.BuyToCover;
            if (action == OrderAction.Sell && Position.MarketPosition != MarketPosition.Long) action = OrderAction.SellShort;
            bool commandHasBracket = command.StopLossPrice > 0 || command.ProfitTargetPrice > 0 || command.TrailingStopDistance > 0;
            bool defaultsExist = DefaultStopLossTicks > 0 || DefaultProfitTargetTicks > 0 || DefaultTrailTicks > 0;
            bool isEntry = type != OrderType.Market || action == OrderAction.Buy || action == OrderAction.SellShort ||
                (action == OrderAction.BuyToCover && Position.MarketPosition != MarketPosition.Short) ||
                (action == OrderAction.Sell && Position.MarketPosition != MarketPosition.Long);
            if (commandHasBracket)
                pendingBrackets[name] = new PendingBracket
                {
                    StopLoss = command.StopLossPrice,
                    ProfitTarget = command.ProfitTargetPrice,
                    Trail = command.TrailingStopDistance,
                    CommandId = command.CommandId
                };
            else if (nextBracketSet && isEntry)
            {
                pendingBrackets[name] = new PendingBracket
                {
                    StopLossTicks = nextBracketSlTicks,
                    ProfitTargetTicks = nextBracketPtTicks,
                    TrailTicks = nextBracketTrailTicks,
                    CommandId = command.CommandId
                };
                nextBracketSet = false;
            }
            else if (defaultsExist && isEntry)
                pendingBrackets[name] = new PendingBracket
                {
                    StopLossTicks = DefaultStopLossTicks,
                    ProfitTargetTicks = DefaultProfitTargetTicks,
                    TrailTicks = DefaultTrailTicks,
                    CommandId = command.CommandId
                };
            SubmitOrderUnmanaged(0, action, type, qty, limitPrice, stopPrice, string.Empty, name);
            lastOrderCallDoneTicks = UtcNowTicks();   // the order is on its way; everything after this is bookkeeping
            Note("SUBMITTED " + name + ": " + action + " " + type + " x" + qty + (limitPrice > 0 ? " @" + limitPrice.ToString("0.00") : "") + (stopPrice > 0 ? " stop " + stopPrice.ToString("0.00") : "") + " on " + Account.Name + ".");
        }

        // Working ENTRY orders (LNNCMD entries, LNNEXEC posts) - not exits (:SL :PT :FLAT :TRAIL).
        // Used to (a) stop a second entry stacking on an unfilled first one and (b) tell the
        // dashboard an entry is still resting.
        private static bool IsEntryOrderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.EndsWith(":SL", StringComparison.Ordinal) || name.EndsWith(":PT", StringComparison.Ordinal) ||
                name.EndsWith(":FLAT", StringComparison.Ordinal) || name.EndsWith(":TRAIL", StringComparison.Ordinal)) return false;
            return name.StartsWith("LNNCMD:", StringComparison.Ordinal) || name.StartsWith("LNNEXEC:", StringComparison.Ordinal);
        }

        // v64n: PublishStatus() runs on the command-reader thread and used to call
        // WorkingEntries() directly, enumerating NT8's Orders collection while the
        // strategy thread was mutating it. The InvalidOperationException that throws
        // was swallowed by the catch below, so the heartbeat silently reported ZERO
        // working entries - and the dashboard, believing nothing was resting, let a
        // second entry through. That is the "more than one trade at a time" failure.
        // The scan now happens only on the strategy thread (order submit, order
        // update, execution update) and publishes into these two fields; the reader
        // thread reads the cached values and never touches Orders.
        private volatile int cachedWorkingCount;
        private volatile int cachedWorkingSigned;

        private void RefreshWorkingEntryCache()
        {
            int c, q; WorkingEntries(out c, out q);
            cachedWorkingCount = c; cachedWorkingSigned = q;
        }

        private void WorkingEntries(out int count, out int signedQty)
        {
            count = 0; signedQty = 0;
            try
            {
                foreach (Order order in Orders)
                {
                    if (order == null || !(order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted)) continue;
                    if (!IsEntryOrderName(order.Name)) continue;
                    count++;
                    bool buying = order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover;
                    signedQty += buying ? order.Quantity : -order.Quantity;
                }
            }
            catch { }
        }

        // Cancel only working ENTRY orders (exits stay so an open position keeps its bracket).
        private int CancelWorkingEntries()
        {
            int n = 0;
            foreach (Order order in Orders)
                if (order != null && (order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted) && IsEntryOrderName(order.Name))
                { try { CancelOrder(order); n++; } catch { } }
            return n;
        }

        private void CancelAllWorking()
        {
            foreach (Order order in Orders)
                if (order != null && (order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted))
                    CancelOrder(order);
            trailingStopOrder = null;
            pendingBrackets.Clear();
        }

        // Executioner entries do not go through Submit(); give them the same
        // next-entry bracket (op 47) so barrier exits work for posted fills too.
        private void AttachNextBracket(string orderName)
        {
            if (nextBracketSet)
            {
                pendingBrackets[orderName] = new PendingBracket { StopLossTicks = nextBracketSlTicks, ProfitTargetTicks = nextBracketPtTicks, TrailTicks = nextBracketTrailTicks, CommandId = 0 };
                nextBracketSet = false;
            }
            else if (DefaultStopLossTicks > 0 || DefaultProfitTargetTicks > 0 || DefaultTrailTicks > 0)
            {
                // v64k: the strategy owns the exits - a filled executioner post gets the
                // same default SL / PT / trail bracket as a market entry.
                pendingBrackets[orderName] = new PendingBracket { StopLossTicks = DefaultStopLossTicks, ProfitTargetTicks = DefaultProfitTargetTicks, TrailTicks = DefaultTrailTicks, CommandId = 0 };
            }
        }

        private void GoFlat(string name)
        {
            CancelAllWorking();
            if (Position.MarketPosition == MarketPosition.Long)
                SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, Position.Quantity, 0, 0, string.Empty, name + ":FLAT");
            else if (Position.MarketPosition == MarketPosition.Short)
                SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, string.Empty, name + ":FLAT");
        }

        // ==================== exits: bracket + trailing ====================

        private void PlaceExitBracket(Order entryOrder, double fillPrice)
        {
            PendingBracket bracket;
            if (!pendingBrackets.TryGetValue(entryOrder.Name, out bracket)) return;
            pendingBrackets.Remove(entryOrder.Name);
            bool isLong = entryOrder.OrderAction == OrderAction.Buy || entryOrder.OrderAction == OrderAction.BuyToCover;
            int qty = entryOrder.Filled;
            // NinjaTrader refuses a reused OCO id. The command id is NOT unique: executioner
            // brackets carry 0, and both the dashboard and the engine restart their counters at
            // 1 every session, so a restart replayed ids NT8 had already seen. Seed from this
            // strategy instance's start time and add a monotonic counter.
            string oco = "LNNOCO:" + ocoSession.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                         "-" + Interlocked.Increment(ref ocoCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            double tick = Instrument.MasterInstrument.TickSize;
            // Resolve relative (default-ticks) values into absolute prices now
            // that the actual fill price is known.
            if (bracket.StopLoss <= 0 && bracket.StopLossTicks > 0)
                bracket.StopLoss = isLong ? fillPrice - bracket.StopLossTicks * tick : fillPrice + bracket.StopLossTicks * tick;
            if (bracket.ProfitTarget <= 0 && bracket.ProfitTargetTicks > 0)
                bracket.ProfitTarget = isLong ? fillPrice + bracket.ProfitTargetTicks * tick : fillPrice - bracket.ProfitTargetTicks * tick;
            if (bracket.Trail <= 0 && bracket.TrailTicks > 0)
                bracket.Trail = (float)(bracket.TrailTicks * tick);
            if (bracket.StopLoss > 0)
                SubmitOrderUnmanaged(0, isLong ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.StopMarket, qty, 0,
                    Instrument.MasterInstrument.RoundToTickSize(bracket.StopLoss), oco, entryOrder.Name + ":SL");
            if (bracket.ProfitTarget > 0)
                SubmitOrderUnmanaged(0, isLong ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Limit, qty,
                    Instrument.MasterInstrument.RoundToTickSize(bracket.ProfitTarget), 0, oco, entryOrder.Name + ":PT");
            if (bracket.Trail > 0 && bracket.StopLoss <= 0)
            {
                trailingDistance = bracket.Trail;
                double stop = isLong ? fillPrice - trailingDistance : fillPrice + trailingDistance;
                trailingStopOrder = SubmitOrderUnmanaged(0, isLong ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.StopMarket, qty, 0,
                    Instrument.MasterInstrument.RoundToTickSize(stop), string.Empty, entryOrder.Name + ":TRAIL");
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            DrainPendingCommands();     // v64k: run any waiting order command now, on this thread
            lastMarketDataTicks = UtcNowTicks();   // v65a: feed-health gate
            if (e.MarketDataType == MarketDataType.Last)
            {
                DateTime now = e.Time;
                // Classify the aggressor from where the print landed vs the touch.
                if (tobAskPrice > 0 && e.Price >= tobAskPrice) buyAggr.Enqueue(new KeyValuePair<DateTime, long>(now, (long)e.Volume));
                else if (tobBidPrice > 0 && e.Price <= tobBidPrice) sellAggr.Enqueue(new KeyValuePair<DateTime, long>(now, (long)e.Volume));
                TrimAggr(now);
                if (execEnabled) ExecOnTrade(e.Price, (long)e.Volume, now);

                // Existing trailing-stop maintenance
                if (trailingStopOrder != null && trailingDistance > 0 &&
                    (trailingStopOrder.OrderState == OrderState.Working || trailingStopOrder.OrderState == OrderState.Accepted) &&
                    Position.MarketPosition != MarketPosition.Flat)
                {
                    bool isLong = Position.MarketPosition == MarketPosition.Long;
                    double desired = isLong ? e.Price - trailingDistance : e.Price + trailingDistance;
                    desired = Instrument.MasterInstrument.RoundToTickSize(desired);
                    bool improves = isLong ? desired > trailingStopOrder.StopPrice : desired < trailingStopOrder.StopPrice;
                    if (improves) ChangeOrder(trailingStopOrder, trailingStopOrder.Quantity, 0, desired);
                }
                else if (Position.MarketPosition == MarketPosition.Flat) trailingStopOrder = null;
            }
        }

        // Applies one depth row operation to the two tracked rows of a side.
        // Returns true when the touch (row 0) may have changed.
        private static bool ApplyTouch(Operation op, int position, double price, long volume,
            ref double tob, ref long tobSize, ref double row1, ref long row1Size)
        {
            if (position == 0)
            {
                if (op == Operation.Add) { row1 = tob; row1Size = tobSize; tob = price; tobSize = Math.Max(0, volume); }
                else if (op == Operation.Remove) { tob = row1; tobSize = row1Size; row1 = 0; row1Size = 0; }
                else { tob = price; tobSize = Math.Max(0, volume); }
                return true;
            }
            if (position == 1)
            {
                if (op == Operation.Remove) { row1 = 0; row1Size = 0; }
                else { row1 = price; row1Size = Math.Max(0, volume); }
            }
            return false;
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            DrainPendingCommands();     // v64k: run any waiting order command now, on this thread
            lastMarketDataTicks = UtcNowTicks();   // v65a: feed-health gate
            if (e.Position > 1) return; // rows 0 and 1 only (row 1 exists to back-fill the touch on Remove@0)
            DateTime now = e.Time;
            if (e.MarketDataType == MarketDataType.Bid)
            {
                double newPrice = tobBidPrice; long newSize = tobBidSize;
                if (!ApplyTouch(e.Operation, e.Position, e.Price, (long)e.Volume, ref newPrice, ref newSize, ref row1BidPrice, ref row1BidSize)) return;
                // Resting-depth clock: reset when the touch price changes or size shrinks.
                if (newPrice != bidRestingPrice || newSize < bidRestingSize)
                { bidRestingPrice = newPrice; bidRestingSinceUtc = now; }
                bidRestingSize = newSize;
                tobBidPrice = newPrice; tobBidSize = newSize;
                TrimFlicker(now); touchFlickerEvents.Enqueue(now);
            }
            else if (e.MarketDataType == MarketDataType.Ask)
            {
                double newPrice = tobAskPrice; long newSize = tobAskSize;
                if (!ApplyTouch(e.Operation, e.Position, e.Price, (long)e.Volume, ref newPrice, ref newSize, ref row1AskPrice, ref row1AskSize)) return;
                if (newPrice != askRestingPrice || newSize < askRestingSize)
                { askRestingPrice = newPrice; askRestingSinceUtc = now; }
                askRestingSize = newSize;
                tobAskPrice = newPrice; tobAskSize = newSize;
                TrimFlicker(now); touchFlickerEvents.Enqueue(now);
            }
            if (execEnabled) ExecOnBook(now);
        }

        protected override void OnBarUpdate()
        {
            DrainPendingCommands();   // v65c: one more wake point, free - this callback was empty
        }

        // ==================== executioner engine ====================

        private void TrimAggr(DateTime now)
        {
            int keep = Math.Max(exAggressionWindowMs, 2000);
            while (sellAggr.Count > 0 && (now - sellAggr.Peek().Key).TotalMilliseconds > keep) sellAggr.Dequeue();
            while (buyAggr.Count > 0 && (now - buyAggr.Peek().Key).TotalMilliseconds > keep) buyAggr.Dequeue();
        }

        private double Mid()
        {
            if (tobBidPrice > 0 && tobAskPrice > 0) return (tobBidPrice + tobAskPrice) / 2.0;
            return Close[0];
        }

        private double Microprice()
        {
            long s = tobBidSize + tobAskSize;
            if (s <= 0 || tobBidPrice <= 0 || tobAskPrice <= 0) return Mid();
            // Weight each side by the OPPOSITE size (heavier bid pulls the fair price up).
            return (tobBidPrice * tobAskSize + tobAskPrice * tobBidSize) / s;
        }

        private double Imbalance()
        {
            long s = tobBidSize + tobAskSize;
            return s <= 0 ? 0.0 : (double)(tobBidSize - tobAskSize) / s;
        }

        // v64n: the 1 s window is now trimmed where events are ADDED, not only where
        // they are read. FlickerPerSec is only reached when exFlickerMaxPerSec > 0 or
        // the EV policy is on - both off by default - so on stock settings this queue
        // was never drained and grew for the life of the NT8 session, with each
        // Queue capacity doubling memcpying the whole array on the data thread inside
        // OnMarketDepth. Trimming on enqueue returns the identical count (FlickerPerSec
        // trimmed before counting anyway) and keeps the queue at one second of events.
        private void TrimFlicker(DateTime now)
        {
            while (touchFlickerEvents.Count > 0 && (now - touchFlickerEvents.Peek()).TotalMilliseconds > 1000) touchFlickerEvents.Dequeue();
        }

        private double FlickerPerSec(DateTime now)
        {
            TrimFlicker(now);
            return touchFlickerEvents.Count;
        }

        private long AggrInto(bool sellSide, DateTime now)
        {
            Queue<KeyValuePair<DateTime, long>> src = sellSide ? sellAggr : buyAggr;
            long v = 0;
            foreach (KeyValuePair<DateTime, long> kv in src)
                if ((now - kv.Key).TotalMilliseconds <= exAggressionWindowMs) v += kv.Value;
            return v;
        }

        private void ArmExecutioner(int side, int qty)
        {
            // A previous LIVE post still working must be cancelled, not forgotten.
            if (execMode == 1 && exLiveOrder != null &&
                (exLiveOrder.OrderState == OrderState.Working || exLiveOrder.OrderState == OrderState.Accepted))
                try { CancelOrder(exLiveOrder); } catch { }
            // A fill still inside its markout window keeps being scored from the list.
            if (exState == 2) MoveMarkoutToList();
            exSide = side; exQty = Math.Max(1, qty);
            exState = 0; exChasedTicks = 0; exFilledInto = 0;
            exMark1Done = exMark2Done = false; exMarkout1Ticks = exMarkout2Ticks = 0;
            exLiveOrder = null; exFillPrice = 0; exMidAtFill = 0;
            exSampleId++;
            // Post is attempted on the next book event once the gate passes.
        }

        private void DisarmExecutioner(string why)
        {
            if (exSide == 0 && exState == 0) return;
            if (execMode == 1 && exLiveOrder != null &&
                (exLiveOrder.OrderState == OrderState.Working || exLiveOrder.OrderState == OrderState.Accepted))
                try { CancelOrder(exLiveOrder); } catch { }
            exSide = 0; exState = 0; exLiveOrder = null;
        }

        // Gate check + first post (runs on book events while armed and idle).
        private bool PostGatePasses(DateTime now)
        {
            // lastGateBlock records WHY we are still waiting (reported in the heartbeat).
            if (tobBidPrice <= 0 || tobAskPrice <= 0) { lastGateBlock = 1; return false; }
            bool buy = exSide > 0;
            double imb = Imbalance();
            if (buy && imb < exPostImbalanceMin) { lastGateBlock = 2; return false; }
            if (!buy && -imb < exPostImbalanceMin) { lastGateBlock = 2; return false; }
            if (exMicropriceGate)
            {
                double mp = Microprice(), mid = Mid();
                if (buy && mp < mid) { lastGateBlock = 3; return false; }
                if (!buy && mp > mid) { lastGateBlock = 3; return false; }
            }
            if (exRestingMsMin > 0)
            {
                DateTime since = buy ? bidRestingSinceUtc : askRestingSinceUtc;
                if (since == DateTime.MinValue || (now - since).TotalMilliseconds < exRestingMsMin) { lastGateBlock = 4; return false; }
            }
            if (exFlickerMaxPerSec > 0 && FlickerPerSec(now) > exFlickerMaxPerSec) { lastGateBlock = 5; return false; }
            // Don't post into a burst of aggression already hitting our side.
            if (exAggressionCancelQty > 0 && AggrInto(buy, now) >= exAggressionCancelQty) { lastGateBlock = 6; return false; }
            lastGateBlock = 0;
            return true;
        }

        private void ExecOnBook(DateTime now)
        {
            MarkoutTick(now);
            if (exSide == 0) return;
            if (exState == 2) return;
            bool buy = exSide > 0;
            double tick = Instrument.MasterInstrument.TickSize;

            if (exState == 0) // armed, waiting to post
            {
                if (exPolicy == 1 && fillModel != null)
                {
                    if (tobBidPrice <= 0 || tobAskPrice <= 0) return;
                    if (EvPrefersCross(buy, now, exTimeoutMs > 0 ? exTimeoutMs : 5000)) { exEvCrossAtArm++; CrossFill(now); return; }
                }
                else if (!PostGatePasses(now)) return;
                exPostPrice = buy ? tobBidPrice : tobAskPrice;
                exQueueAhead = buy ? tobBidSize : tobAskSize;
                exPostUtc = now; exFilledInto = 0; exChasedTicks = 0;
                exQAhead = exQueueAhead; exQBehind = 0; exLevelSizeSeen = exQueueAhead; exTradedSinceBook = 0;
                exPosts++;
                if (execMode == 1)
                {
                    string nm = "LNNEXEC:" + exSampleId;
                    AttachNextBracket(nm);
                    try { exLiveOrder = SubmitOrderUnmanaged(0, buy ? OrderAction.Buy : OrderAction.SellShort, OrderType.Limit, exQty, exPostPrice, 0, string.Empty, nm); }
                    catch (Exception err) { Note("LnnOrderExecStrategy exec post failed: " + err.Message); exLiveOrder = null; }
                }
                exState = 1;
                return;
            }

            if (exState == 1) // posting
            {
                double touch = buy ? tobBidPrice : tobAskPrice;
                // Market moved toward us / away — repeg within the chase budget.
                bool movedAway = buy ? touch > exPostPrice : touch < exPostPrice;
                if (movedAway && touch > 0)
                {
                    int ticksMoved = (int)Math.Round(Math.Abs(touch - exPostPrice) / tick);
                    if (exChasedTicks + ticksMoved > exMaxChaseTicks) { CompleteNoFill(4 /*missed-chase*/, now); return; }
                    exChasedTicks += ticksMoved;
                    exPostPrice = touch; exQueueAhead = buy ? tobBidSize : tobAskSize; exFilledInto = 0;
                    exQAhead = exQueueAhead; exQBehind = 0; exLevelSizeSeen = exQueueAhead; exTradedSinceBook = 0;
                    if (execMode == 1 && exLiveOrder != null &&
                        (exLiveOrder.OrderState == OrderState.Working || exLiveOrder.OrderState == OrderState.Accepted))
                        try { ChangeOrder(exLiveOrder, exQty, exPostPrice, 0); } catch { }
                }
                // Queue estimator update from the displayed size at our price.
                if (!movedAway && touch > 0)
                {
                    long sizeNow = buy ? tobBidSize : tobAskSize;
                    if (Math.Abs(touch - exPostPrice) < tick * 0.5)
                    {
                        long delta = sizeNow - exLevelSizeSeen;
                        if (delta < 0)
                        {
                            long cancels = Math.Max(0, -delta - exTradedSinceBook);   // decrease not explained by prints
                            if (cancels > 0 && exCancelAheadFactor > 0)
                            {
                                double total = exQAhead + exQBehind;
                                double aheadShare = total > 0 ? exQAhead / total : 0.0;
                                double cancelAhead = Math.Min(exQAhead, cancels * aheadShare * exCancelAheadFactor);
                                exQAhead -= cancelAhead; exQBehind = Math.Max(0, exQBehind - (cancels - cancelAhead));
                            }
                        }
                        else if (delta > 0) exQBehind += delta;
                        exLevelSizeSeen = sizeNow; exTradedSinceBook = 0;
                    }
                    else
                    {
                        // Our price is no longer the touch on our side and the market did not move
                        // away: the level at our price was emptied — nothing is ahead of us now.
                        exQAhead = 0; exQBehind = 0; exLevelSizeSeen = 0; exTradedSinceBook = 0;
                    }
                }
                // EV re-evaluation while posting (at most every 100 ms).
                if (exPolicy == 1 && fillModel != null && (now - exLastEvEvalUtc).TotalMilliseconds >= 100)
                {
                    exLastEvEvalUtc = now;
                    double remaining = exTimeoutMs > 0 ? exTimeoutMs - (now - exPostUtc).TotalMilliseconds : 5000;
                    if (EvPrefersCross(buy, now, remaining)) { exEvCrossWhilePosting++; CrossFill(now); return; }
                }
                // Aggression into our side + thinning queue = toxic; pull.
                long aggr = AggrInto(buy, now);
                long touchSize = buy ? tobBidSize : tobAskSize;
                if (exAggressionCancelQty > 0 && aggr >= exAggressionCancelQty && touchSize < exQueueAhead)
                { CompleteNoFill(1 /*cancel-aggression*/, now); return; }
                // Timeout
                if (exTimeoutMs > 0 && (now - exPostUtc).TotalMilliseconds >= exTimeoutMs)
                {
                    if (exOnTimeout == 1) CrossFill(now);
                    else CompleteNoFill(2 /*timeout-abandon*/, now);
                    return;
                }
            }
        }

        private void ExecOnTrade(double price, long volume, DateTime now)
        {
            MarkoutTick(now);
            if (exState != 1) return;
            bool buy = exSide > 0;
            // A resting buy fills when sells trade at/through our price; a resting
            // sell fills when buys trade at/through it. Model queue priority:
            // we only fill once the size that was ahead of us is consumed.
            bool tradedThrough = buy ? price <= exPostPrice : price >= exPostPrice;
            if (!tradedThrough) return;
            exFilledInto += volume; exTradedSinceBook += volume;
            exQAhead = Math.Max(0, exQAhead - volume);
            if (execMode == 1) return; // live fills come from OnExecutionUpdate
            bool filled = exCancelAheadFactor > 0 ? exQAhead <= 0 : exFilledInto >= exQueueAhead;
            if (filled)
                RecordFill(exPostPrice, now);
        }

        private void CrossFill(DateTime now)
        {
            bool buy = exSide > 0;
            double cross = buy ? tobAskPrice : tobBidPrice;
            if (cross <= 0) cross = Close[0];
            if (execMode == 1)
            {
                string nm = "LNNEXEC:" + exSampleId + ":X";
                try
                {
                    if (exLiveOrder != null && (exLiveOrder.OrderState == OrderState.Working || exLiveOrder.OrderState == OrderState.Accepted)) CancelOrder(exLiveOrder);
                    if (pendingBrackets.ContainsKey("LNNEXEC:" + exSampleId)) { pendingBrackets[nm] = pendingBrackets["LNNEXEC:" + exSampleId]; pendingBrackets.Remove("LNNEXEC:" + exSampleId); }
                    else AttachNextBracket(nm);
                    SubmitOrderUnmanaged(0, buy ? OrderAction.Buy : OrderAction.SellShort, OrderType.Market, exQty, 0, 0, string.Empty, nm);
                }
                catch (Exception err) { Note("LnnOrderExecStrategy exec cross failed: " + err.Message); }
                // markout is measured off the recorded RecordFill via OnExecutionUpdate
                RecordFill(cross, now); // approximate for scoring; real avg comes from fill events
            }
            else RecordFill(cross, now);
        }

        private void RecordFill(double fillPrice, DateTime now)
        {
            exFillPrice = fillPrice;
            exMidAtFill = Mid();
            exFillUtc = now;
            exFills++;
            exState = 2;
            exMark1Done = exMark2Done = false;
            MoveMarkoutToList();           // score from the list; the executioner is free for the next signal
        }

        private void MoveMarkoutToList()
        {
            if (exState != 2) return;
            pendingMarkouts.Add(new PendingMarkout { SampleId = exSampleId, Side = exSide, FillPrice = exFillPrice, MidAtFill = exMidAtFill, FillUtc = exFillUtc, QueueAhead = exQueueAhead });
            exSide = 0; exState = 0; exLiveOrder = null;
        }

        private void MarkoutTick(DateTime now)
        {
            if (pendingMarkouts.Count == 0) return;
            double tick = Instrument.MasterInstrument.TickSize;
            double mid = Mid(); if (mid <= 0) return;
            for (int i = pendingMarkouts.Count - 1; i >= 0; i--)
            {
                PendingMarkout m = pendingMarkouts[i];
                // Markouts are measured at the EXIT TOUCH (a buy exits at the bid, a sell at the ask), not the mid.
                double exitPx = m.Side > 0 ? tobBidPrice : tobAskPrice; if (exitPx <= 0) exitPx = mid;
                if (!m.Mk1Done && (now - m.FillUtc).TotalMilliseconds >= exMarkout1Ms) { m.Mk1 = (exitPx - m.FillPrice) / tick * m.Side; m.Mk1Done = true; }
                if ((now - m.FillUtc).TotalMilliseconds >= exMarkout2Ms)
                {
                    double mk2 = (exitPx - m.FillPrice) / tick * m.Side;
                    double effSpread = (m.MidAtFill - m.FillPrice) / tick * m.Side; // +ve = filled better than mid
                    long savedSample = exSampleId, savedQueue = exQueueAhead;
                    exSampleId = m.SampleId; exQueueAhead = m.QueueAhead;   // publish under the fill's own ids
                    PublishQuality(true, 0 /*filled*/, effSpread, m.Mk1, mk2);
                    exSampleId = savedSample; exQueueAhead = savedQueue;
                    pendingMarkouts.RemoveAt(i);
                }
            }
        }

        private void CompleteNoFill(int outcome, DateTime now)
        {
            if (execMode == 1 && exLiveOrder != null &&
                (exLiveOrder.OrderState == OrderState.Working || exLiveOrder.OrderState == OrderState.Accepted))
                try { CancelOrder(exLiveOrder); } catch { }
            exCancels++;
            PublishQuality(false, outcome, double.NaN, double.NaN, double.NaN);
            exSide = 0; exState = 0; exLiveOrder = null;
        }

        // Quality summary to the dashboard, reusing the fill ring with a dedicated
        // record type (IsExecution = 3). Existing fill/exec records (0/1) are
        // untouched. Field meanings for this record type only:
        //   Price     = effective spread ticks at fill (+ve = beat mid)
        //   Qty       = markout@window1 ticks x100   FilledQty = markout@window2 ticks x100
        //   OrderState= filled?1:0                   OrderAction = outcome code
        //   MarketPosition = mode (0/1)              NtOrderIdLow = queue-ahead at post
        private void PublishQuality(bool filled, int outcome, double effSpreadTicks, double m1, double m2)
        {
            if (fillView == null) return;
            FillPayload payload = new FillPayload
            {
                EventTimeTicks = UtcNowTicks(),
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = exSampleId,
                NtOrderIdLow = exQueueAhead,
                Price = double.IsNaN(effSpreadTicks) ? 0 : effSpreadTicks,
                Qty = double.IsNaN(m1) ? -100000 : (int)Math.Round(m1 * 100),
                FilledQty = double.IsNaN(m2) ? -100000 : (int)Math.Round(m2 * 100),
                OrderState = filled ? 1 : 0,
                OrderAction = outcome,
                MarketPosition = execMode,
                IsExecution = 3
            };
            lock (fillLock)
            {
                long next = fillWriteSequence + 1;
                long offset = HeaderBytes + (next & (Capacity - 1)) * SlotBytes;
                fillView.Write(offset, next * 2 + 1);
                Thread.MemoryBarrier();
                fillView.Write(offset + 8, ref payload);
                Thread.MemoryBarrier();
                fillView.Write(offset, next * 2);
                Thread.MemoryBarrier();
                fillView.Write(0, next);
                fillWriteSequence = next;
            }
        }

        // ==================== fill reporting back to the dashboard ====================

        // "LNNCMD:123" is the ENTRY. "LNNCMD:123:SL" / ":PT" / ":FLAT" / ":TRAIL" are its exit
        // orders and inherit the same id, so they must never be timed against the entry's send
        // stamp - doing that measures how long the trade stayed open, not latency.
        private static bool IsPrimaryOrder(string orderName)
        {
            if (string.IsNullOrEmpty(orderName)) return false;
            if (!orderName.StartsWith("LNNCMD:", StringComparison.Ordinal) && !orderName.StartsWith("LNNEXEC:", StringComparison.Ordinal)) return false;
            return orderName.IndexOf(':', orderName.IndexOf(':') + 1) < 0;   // no second colon = the entry itself
        }

        private static long ParseCommandId(string orderName)
        {
            if (string.IsNullOrEmpty(orderName) || !orderName.StartsWith("LNNCMD:", StringComparison.Ordinal)) return 0;
            int end = orderName.IndexOf(':', 7);
            string idText = end < 0 ? orderName.Substring(7) : orderName.Substring(7, end - 7);
            long id;
            return long.TryParse(idText, out id) ? id : 0;
        }

        private int MapAction(OrderAction action)
        {
            // Dashboard mapping: 0 Buy, 1 Sell (close long), 2 SellShort, 3 BuyToCover
            switch (action)
            {
                case OrderAction.Buy: return 0;
                case OrderAction.Sell: return 1;
                case OrderAction.SellShort: return 2;
                case OrderAction.BuyToCover: return 3;
                default: return -1;
            }
        }

        private void PublishFill(Order order, double price, int filledQty, byte isExecution)
        {
            if (fillView == null || order == null) return;
            long originId = ParseCommandId(order.Name);
            long originSent; if (!commandSendTicks.TryGetValue(originId, out originSent)) originSent = 0;
            FillPayload payload = new FillPayload
            {
                // EventTimeTicks carries the ORIGINATING command's send stamp, and ONLY for the
                // entry order itself (0 for its stop/target/flatten children, whose events can
                // arrive minutes later and would otherwise be reported as latency).
                EventTimeTicks = IsPrimaryOrder(order.Name) ? originSent : 0,
                CallbackUtcTicks = UtcNowTicks(),
                CommandId = originId,
                NtOrderIdLow = order.Id,
                Price = price,
                Qty = order.Quantity,
                FilledQty = filledQty,
                OrderState = (int)order.OrderState,
                OrderAction = MapAction(order.OrderAction),
                MarketPosition = (int)Position.MarketPosition,
                IsExecution = isExecution
            };
            lock (fillLock)
            {
                long next = fillWriteSequence + 1;
                long offset = HeaderBytes + (next & (Capacity - 1)) * SlotBytes;
                fillView.Write(offset, next * 2 + 1);
                Thread.MemoryBarrier();
                fillView.Write(offset + 8, ref payload);
                Thread.MemoryBarrier();
                fillView.Write(offset, next * 2);
                Thread.MemoryBarrier();
                fillView.Write(0, next);
                fillWriteSequence = next;
            }
        }

        // Strategy-side realized-ticks tracker for the daily loss cutoff.
        private void TrackDailyLoss(OrderAction action, int quantity, double price)
        {
            RollRiskDay();
            int signed = action == OrderAction.Buy || action == OrderAction.BuyToCover ? quantity : -quantity;
            double tick = Instrument.MasterInstrument.TickSize; if (tick <= 0) return;
            int previous = riskTrackedPosition, next = previous + signed;
            if (previous == 0 || Math.Sign(previous) == Math.Sign(signed))
            {
                int prevAbs = Math.Abs(previous), addAbs = Math.Abs(signed);
                riskAveragePrice = prevAbs + addAbs > 0 ? (riskAveragePrice * prevAbs + price * addAbs) / (prevAbs + addAbs) : price;
                if (previous == 0) riskOpenTicks = 0;
            }
            else
            {
                int closed = Math.Min(Math.Abs(signed), Math.Abs(previous));
                riskOpenTicks += (price - riskAveragePrice) / tick * Math.Sign(previous) * closed;
                if (next == 0 || Math.Sign(next) != Math.Sign(previous))
                {
                    realizedTicksToday += riskOpenTicks;
                    // v65a: high-water mark for the give-back gate.
                    if (realizedTicksToday > peakRealizedTicks) peakRealizedTicks = realizedTicksToday;
                    // v64y: a trade just closed. This is the only place the strategy learns the
                    // outcome of one, so the post-loss cooldown and the size ramp are driven here.
                    if (riskOpenTicks < 0)
                    {
                        if (gCooldownSeconds > 0)
                        {
                            cooldownUntilTicks = UtcNowTicks() + (long)(gCooldownSeconds * TimeSpan.TicksPerSecond);
                            Note("LnnOrderExecStrategy: losing trade (" + riskOpenTicks.ToString("0.##") +
                                 "t) — entries on cooldown for " + gCooldownSeconds.ToString("0.#") + "s.");
                        }
                    }
                    else if (riskOpenTicks > 0) sessionWins++;
                    riskOpenTicks = 0;
                    if (Math.Sign(next) != Math.Sign(previous) && next != 0) riskAveragePrice = price;
                    if (DailyLossLimitTicks > 0 && realizedTicksToday <= -DailyLossLimitTicks && !dailyLossTripped)
                    {
                        dailyLossTripped = true;
                        // v64n: Note(), not Print() - this is immediately in front of the
                        // flatten, and Print marshals synchronously to NT8's Output window.
                        Note("LnnOrderExecStrategy: DAILY LOSS LIMIT hit (" + realizedTicksToday.ToString("0.##") +
                            "t ≤ -" + DailyLossLimitTicks.ToString("0.##") + "t) — flattening; entries blocked until tomorrow or a new limit is set.");
                        GoFlat("LNNCMD:0");
                    }
                }
            }
            riskTrackedPosition = next;
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            PublishEventDelivery(ParseCommandId(order.Name), time, (int)orderState, false);
            PublishFill(order, averageFillPrice > 0 ? averageFillPrice : (limitPrice > 0 ? limitPrice : stopPrice), filled, 0);

            // v64n: a pending bracket is released only when NT8 CONFIRMS the entry is
            // dead. CompleteNoFill() calls CancelOrder() optimistically and used to
            // leave the entry in pendingBrackets forever (one leak per executioner
            // timeout / chase-miss / aggression cancel). Dropping it there instead
            // would be worse: if that cancel loses the race and the order fills
            // anyway, the fill arrives with no stop and no target. Waiting for
            // Cancelled/Rejected keeps the bracket available right up until NT8 says
            // the order can no longer fill.
            if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                pendingBrackets.Remove(order.Name);

            RefreshWorkingEntryCache();   // v64n: strategy thread - the heartbeat reads this

            if (error != ErrorCode.NoError)
                Note("LnnOrderExecStrategy order error " + error + " on " + order.Name + ": " + comment);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution?.Order == null) return;
            PublishEventDelivery(ParseCommandId(execution.Order.Name), time, (int)execution.Order.OrderState, true);
            PublishFill(execution.Order, price, quantity, 1);
            TrackDailyLoss(execution.Order.OrderAction, quantity, price);
            // Live executioner fill → start markout scoring off the real fill price.
            if (execMode == 1 && exState == 1 && execution.Order.Name != null &&
                execution.Order.Name.StartsWith("LNNEXEC:", StringComparison.Ordinal) &&
                execution.Order.OrderState == OrderState.Filled)
                RecordFill(price, execution.Time);
            // Entry filled → place its exit bracket / trailing stop
            if (execution.Order.OrderState == OrderState.Filled && pendingBrackets.ContainsKey(execution.Order.Name))
                PlaceExitBracket(execution.Order, price);
            RefreshWorkingEntryCache();   // v64n: a fill retires a working entry
        }
    }
}
