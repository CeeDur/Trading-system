// Read-only dashboard for LnnLiveLearner and the NinjaTrader LNN Depth Bridge.
// It has no account, broker, or order-submission functionality.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.IO.MemoryMappedFiles;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading;

namespace LnnLiveDashboard
{
    internal sealed class Program
    {
        [STAThread]
        private static void Main()
        {
            // Startup diagnostics: any unhandled exception is shown in a
            // dialog and written to crash.log next to the executable, so a
            // failed launch is never silent.
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                ReportCrash(e.ExceptionObject as Exception);
            };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                ReportCrash(e.Exception);
            };
            try
            {
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new DashboardForm());
            }
            catch (Exception ex)
            {
                ReportCrash(ex);
            }
        }

        private static void ReportCrash(Exception ex)
        {
            string text = ex == null ? "Unknown error." : ex.ToString();
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" + text + "\r\n\r\n");
            }
            catch { }
            try
            {
                MessageBox.Show(text, "LnnLiveDashboard — startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }

    internal sealed class DashboardForm : Form
    {
        // Bump on every rebuild so the running version is visible in the
        // title bar; stale-exe confusion has bitten more than once.
        private const string BuildStamp = "2026-09-11.65c dispatch telemetry";
        // Keep the Windows text-box work bounded. Detailed recurring telemetry
        // is still written to the optional dashboard log; it does not need to
        // be rendered indefinitely for the live metrics to stay responsive.
        private const int MaximumOnScreenLogCharacters = 100000;
        private const int OnScreenLogTrimCharacters = 50000; // trim in bigger chunks: half as many expensive re-layouts
        // Win32 edit-control messages used only to preserve a user's reading
        // position when a live status box refreshes in the background.
        private const int EmGetFirstVisibleLine = 0x00CE;
        private const int EmLineScroll = 0x00B6;
        private static readonly string InputSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LnnLiveDashboard",
            "editable-number-inputs.txt");

        // ===== Trading Hub — constants and structs (must match LnnOrderExecStrategy) =====

        private const long TradeHeaderBytes    = 64;
        private const int  TradeCmdSlotBytes    = 72;
        private const int  TradeFillSlotBytes   = 72;
        private const int  TradeCapacity        = 1 << 17;
        private const long TradeMagic            = 0x4C4E4E4F524431L;
        // Strategy → dashboard diagnostic message ring (text). Separate from the
        // fill ring so the fill contract is unchanged.
        private const int  TradeMsgSlotBytes    = 128;   // 8 seqlock + 8 ts + 112 text
        private const int  TradeMsgCapacity     = 1 << 12;
        private const long TradeMsgMagic         = 0x4C4E4E4D534731L; // "LNNMSG1"

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        private struct TradeHeader
        {
            public long WriteSequence;
            public int  Capacity;
            public int  RecordSize;
            public long Magic;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        private struct TradeCommandPayload
        {
            public long   CommandId;
            public int    Op;
            public int    Qty;
            public double Price;
            public double StopPrice;
            public byte   Tif;
            public long   SendUtcTicks;   // v64k: sender's precise clock, for the ring-hop measurement
            public float  TrailingStopDistance;
            public double StopLossPrice;
            public double ProfitTargetPrice;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        private struct TradeFillPayload
        {
            public long   EventTimeTicks;
            public long   CallbackUtcTicks;
            public long   CommandId;
            public long   NtOrderIdLow;
            public double Price;
            public int    Qty;
            public int    FilledQty;
            public int    OrderState;
            public int    OrderAction;
            public int    MarketPosition;
            public byte   IsExecution;
        }

        private enum TradeCmdOp
        {
            BuyMarket       = 0,
            SellMarket      = 1,
            BuyLimit        = 2,
            SellLimit       = 3,
            BuyStop         = 4,
            SellStop        = 5,
            ExitLongMarket  = 8,
            ExitShortMarket = 9,
            CancelAll       = 12,
            Flat            = 13,
            // Live strategy tuning (LnnOrderExecStrategy ops 20-24)
            PauseExecution      = 20,
            ResumeExecution     = 21,
            SetMaxPosition      = 22,
            SetDailyLossLimit   = 23,
            SetDefaultBracket   = 24,
            // v64y: the entry gates, pushed into the strategy so engine-direct orders
            // (which the dashboard never sees) are held to the same rules.
            SetEntryGatesA      = 25,   // Qty=max trades, Price=gap s, StopPrice=cooldown s, SL=margin, PT=dead-regime s, Trail=clear s, Tif=direction
            SetEntryGatesB      = 26,   // Qty=max flips, Price=session start min, StopPrice=session end min, SL=dead low, PT=dead high, Trail=ramp, Tif=flags
            SetGateThresholds   = 27,   // Price=paper long threshold, StopPrice=paper short threshold
            // v65a: book-quality and health gates. The strategy already computes all the book
            // numbers for the executioner; these apply them to the entry decision itself.
            SetBookGates        = 28,   // Qty=min book size, Price=max spread t, StopPrice=min imbalance, SL=max flicker/s, PT=max aggression, Trail=signal age ms, Tif=bit0 microprice
            SetHealthGates      = 29,   // Price=feed stale ms, StopPrice=give-back % of peak
            // Passive limit entries priced at NT8's live touch (ops 30-31)
            BuyLimitJoin        = 30,
            SellLimitJoin       = 31,
            // Limit executioner control (ops 40-43)
            ExecEnableMode      = 40,
            ExecGates           = 41,
            ExecManagement      = 42,
            ExecMarkout         = 43,
            ExecQueueModel      = 44,   // queue-position estimator cancel-ahead factor (Price = k)
            ExecPolicy          = 45,   // Price = 0 gates / 1 EV, StopPrice = expected move ticks, Qty = 1 reload model file
            SignalContext       = 46,   // Price = P(up) of the signal carried by the next entry
            CancelEntries       = 14,   // v64k: cancel working ENTRY orders only (exits of an open position stay)
            NextEntryBracket    = 47,   // v64k barrier exits: Price = stop ticks, StopPrice = target ticks, StopLossPrice = trail ticks, for the NEXT entry
            Ping                = 99,   // v64k: strategy replies with a PONG message + ack record (trade-path test)
        }

        // ===== Trading Hub — UI controls =====

        private readonly GroupBox tradingHub       = new GroupBox();
        private readonly Label   tradingHubIntro   = new Label();

        // v64m: the live AI controls, on the same tab as the manual order desk, so
        // arming and the panic buttons are never more than one glance apart. These
        // are views onto the existing AI Trader state - they call the same
        // StartAiTrader()/StopAiTrader() as the Sim tab's buttons, nothing new.
        private readonly GroupBox aiHub           = new GroupBox();
        private readonly Button  hubArmBtn        = new Button();
        private readonly Button  hubDisarmBtn     = new Button();
        private readonly Label   hubAiState       = new Label();
        private readonly Label   hubAiGate        = new Label();
        private readonly Label   hubAiLast        = new Label();
        private readonly Label   hubAiTally       = new Label();
        private readonly Button gotoTradingHub    = new Button();   // jumps to the Trading Hub tab
        private readonly TabPage liveTabPage      = new TabPage("Live learning");
        private readonly TabPage tradingHubTab    = new TabPage("Trading Hub");   // v64l: the hub owns a tab so the order buttons are always visible
        private TabControl modeTabsRef;                                            // set during BuildUi so the jump button can select the hub tab
        private readonly Label   tradeHubStatus    = new Label();
        private readonly Button  connectTradeHub   = new Button();
        private readonly Button  disconnectTradeHub = new Button();
        private readonly TextBox tradeQty          = new TextBox();
        private readonly TextBox tradePrice        = new TextBox();
        private readonly TextBox tradeStopLossPrice = new TextBox();
        private readonly TextBox tradeTargetPrice   = new TextBox();
        private readonly TextBox tradeTrailingStopDistance = new TextBox();
        private readonly Button  buyMarketBtn      = new Button();
        private readonly Button  sellMarketBtn     = new Button();
        private readonly Button  buyLimitBtn      = new Button();
        private readonly Button  sellLimitBtn     = new Button();
        private readonly Button  buyStopBtn        = new Button();
        private readonly Button  sellStopBtn       = new Button();
        private readonly Button  flatBtn           = new Button();
        private readonly Button  cancelAllBtn      = new Button();
        private readonly Label   tradePosition     = new Label();
        private readonly TextBox tradeFillLog      = new TextBox();

        // ===== Trading Hub — MMF state =====

        private MemoryMappedFile tradeCmdMap;
        private MemoryMappedFile tradeFillMap;
        private MemoryMappedViewAccessor tradeCmdAccessor;
        private MemoryMappedViewAccessor tradeFillAccessor;
        private EventWaitHandle tradeSignal;
        private Thread tradeFillThread;
        private CancellationTokenSource tradeCts;
        private long tradeCmdSeq;
        private long tradeFillCursor;
        private MemoryMappedFile tradeMsgMap;
        private MemoryMappedViewAccessor tradeMsgAccessor;
        private long tradeMsgCursor;
        private readonly TextBox strategyMsgBox = new TextBox();
        private long tradeCmdId;
        private int  tradeNetPosition;
        private readonly object tradeCmdLock = new object();

        private static readonly string TradeArmedFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LnnLiveDashboard", "trade_armed.txt");
        private readonly Button killSwitchBtn = new Button();
        private bool tradeArmed;
        private string lastForwardedAiSignal;
        private readonly Label simTradeBridgeStatus = new Label();

        // ===== Quality-of-life state =====
        private readonly Label armedBanner = new Label();                 // always-visible ARMED strip at the top of the form
        private readonly Label strategyAliveLabel = new Label();          // NT8 strategy activity indicator
        // v64k trade-path panel: where does a signal stop on its way to an order?
        private readonly Label tradePathStatus = new Label();
        private readonly Button testPathBtn = new Button();
        private long lastLiveProbabilityTicks; private string lastLiveProbabilityText = "";
        private string lastSignalSeen = ""; private DateTime lastSignalSeenUtc = DateTime.MinValue; private int lastTrainedCount = -1;
        private string lastDecisionText = "no signal received yet"; private DateTime lastDecisionUtc = DateTime.MinValue; private string lastDecisionLogged = "";
        private string lastCmdSentText = "none"; private DateTime lastCmdSentUtc = DateTime.MinValue; private long lastCmdSentId;
        private DateTime stratHeartbeatUtc = DateTime.MinValue; private long stratConsumed, stratLastCmdId; private int stratFlags, stratExState, stratExSide, stratGate, stratPosition; private double stratRealized;
        private int stratEntryGate;   // v64y: entry-gate block code from the heartbeat (0 = nothing blocking)

        // Mirrors GateBlockName in LnnOrderExecStrategy - keep the two in step.
        private static string EntryGateName(int code)
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
        private string lastAckText = "none"; private DateTime lastAckUtc = DateTime.MinValue; private long lastAckId;
        private DateTime pingSentUtc = DateTime.MinValue; private long pingId; private bool pingAnswered = true;
        private readonly TextBox aiQty = new TextBox();                   // AI Trader order quantity (independent of Trading Hub)
        private readonly TextBox aiTrailTicks = new TextBox();            // AI Trader trailing-stop distance in ticks (blank = strategy default)
        private readonly TextBox dailyLossLimitTicks = new TextBox();     // auto-disarm when paper session P/L falls below -limit
        private readonly TextBox maxTradesPerSession = new TextBox();     // auto-disarm after this many AI entries
        private readonly TextBox regimeMaxFlips = new TextBox();          // regime guard: max signal flips per 10 minutes
        private readonly CheckBox soundAlerts = new CheckBox();
        private readonly ComboBox profileSelector = new ComboBox();
        private readonly Panel pnlSpark = new Panel();
        private readonly Button checkStream = new Button();
        private bool streamCheckRunning;

        // ===== Trading-discipline additions =====
        private readonly TextBox sessionStartInput = new TextBox();        // HH:mm local; blank = always
        private readonly TextBox sessionEndInput = new TextBox();
        private readonly TextBox cooldownSecondsInput = new TextBox();     // no entry for N s after a losing trade
        private readonly TextBox maxConsecutiveLossesInput = new TextBox();// auto-disarm after N losers in a row
        private readonly TextBox signalHoldSecondsInput = new TextBox();   // signal must persist before entry
        private readonly TextBox liveConfidenceMarginInput = new TextBox();// extra probability beyond threshold for live entries
        private readonly TextBox rampWinsPerStepInput = new TextBox();     // +1 contract per N wins, capped at AI qty
        private readonly Label tallyLabel = new Label();
        private readonly Label positionLabel = new Label();
        private readonly Label divergenceLabel = new Label();
        private readonly TextBox tradeHistoryBox = new TextBox();
        private readonly Panel signalStrip = new Panel();
        private readonly List<KeyValuePair<DateTime, string>> signalTimeline = new List<KeyValuePair<DateTime, string>>();
        private string pendingEntrySignal; private DateTime pendingEntrySinceUtc;
        private DateTime cooldownUntilUtc = DateTime.MinValue;
        private bool sessionWindowFlattened;
        private double latestProbability = 0.5;
        private int liveTrades, liveWins, liveLosses, consecutiveLosses;
        private double liveNetTicks;
        // Round-trip tracking from executions
        private int fillTrackedPosition; private double fillAveragePrice, fillOpenTradeTicks; private double fillEntryPrice;
        // v64o: the W/L tally is driven entirely by fillTrackedPosition returning to zero.
        // It is a SECOND position counter, separate from tradeNetPosition, and it was never
        // resynced from NT8 - so one missed execution record desynced it permanently and no
        // further round trip ever closed to zero, which is why trades stopped appearing in
        // the tally while trading itself carried on normally.
        private double lastKnownFillPrice;
        private bool tallyPriceEstimated;
        private volatile float forwarderLastBid, forwarderLastAsk;
        private readonly TextBox ntMaxPosInput = new TextBox();
        private readonly TextBox ntLossLimitInput = new TextBox();
        private readonly TextBox ntDefSlInput = new TextBox();
        private readonly TextBox ntDefPtInput = new TextBox();
        private readonly TextBox ntDefTrailInput = new TextBox();
        private readonly Label latencyGateLabel = new Label();
        private readonly TextBox minSecondsBetweenTradesInput = new TextBox();
        private readonly ComboBox signalModeSelector = new ComboBox();
        private readonly ComboBox tradeDirectionSelector = new ComboBox();
        private readonly ComboBox entryOrderTypeSelector = new ComboBox();
        // v64k barrier exits: live trades exit where the paper sim / labels do.
        private readonly ComboBox exitModeSelector = new ComboBox();        // 0 = barrier (TP/SL at +/-target, flatten at horizon), 1 = signal change (legacy)
        private readonly CheckBox flipOnOpposite = new CheckBox();          // barrier mode: opposite signal flips instead of holding
        private int barrierOpenSide;                                         // +1 long, -1 short, 0 none (dashboard's view of the bracketed trade)
        // v64k paper mirror: the paper sim takes the SAME trade a live order would - same
        // guards, same one-at-a-time rule, same bracket - so paper and live are comparable
        // 1:1. paperOpenSide tracks the engine's open paper trade from its lifecycle lines.
        private readonly CheckBox paperMirror = new CheckBox();
        private int paperOpenSide;
        private string lastHostJitter = "";
        private int positionDisagreeTicks;
        private int blockedRepeatCount; private DateTime blockedSinceUtc = DateTime.UtcNow;
        // One-trade-at-a-time latch: an entry that has been sent but not yet filled
        // blocks further entries until a fill/ack/heartbeat resolves it.
        private DateTime entrySentUtc = DateTime.MinValue; private long entrySentCmdId;
        private int stratWorkingEntries;                                     // unfilled entry orders the strategy reports (heartbeat bits 8-15)
        private int latestHorizonMs = 5000;                                  // engine's dynamic-horizon-ms from the frames line (display only)
        private bool BarrierMode { get { return exitModeSelector.SelectedIndex != 1; } }
        // Strategy risk config from heartbeat record type 6 (what an entry will actually get).
        private DateTime stratRiskUtc = DateTime.MinValue; private double stratSlTicks, stratPtTicks, stratTrailTicks, stratDailyLoss; private int stratMaxPos, stratOrdersPerMin; private bool stratHoursSet;
        private string SelectedSignalMode()
        {
            return signalModeSelector.SelectedIndex == 1 ? "shadow" : signalModeSelector.SelectedIndex == 2 ? "thresholds" :
                signalModeSelector.SelectedIndex == 3 ? "fusion" : "both";
        }
        private DateTime lastEntryUtc = DateTime.MinValue;
        private sealed class CommandTiming { public long SentTicks; public bool Acked; public int Op; public double QuoteBid, QuoteAsk; }
        // v64k: 100 ns wall clock. DateTime.UtcNow only advances on the system timer tick
        // (0.5-15.6 ms), so on a VPS every latency stamp is quantised by that much.
        [DllImport("kernel32.dll")] private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState state, int size);
        [StructLayout(LayoutKind.Sequential)] private struct ProcessPowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }
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
        private static void ApplyDashboardClockHygiene()
        {
            try
            {
                ProcessPowerThrottlingState st = new ProcessPowerThrottlingState { Version = 1, ControlMask = 0x4 /*IgnoreTimerResolution*/, StateMask = 0 };
                SetProcessInformation(GetCurrentProcess(), 4 /*ProcessPowerThrottling*/, ref st, Marshal.SizeOf(typeof(ProcessPowerThrottlingState)));
            }
            catch { }
            try { long probe; GetSystemTimePreciseAsFileTime(out probe); preciseClock = probe > 0; } catch { preciseClock = false; }
        }
        // Latency legs, measured separately so a slow number can be attributed.
        private readonly List<double> ntAckMs = new List<double>();      // dashboard send -> NT8 published the order event (the real one)
        private readonly List<double> ntFillMs = new List<double>();     // dashboard send -> NT8 published the execution
        private readonly List<double> uiOverheadMs = new List<double>(); // NT8 published -> this dashboard read it (our own overhead)
        private readonly List<double> realizedSlippageTicks = new List<double>();
        private readonly Dictionary<long, CommandTiming> commandTimings = new Dictionary<long, CommandTiming>();
        private readonly List<DateTime> entryTimestamps = new List<DateTime>();
        private readonly List<double> ackLatencyMs = new List<double>();
        private readonly List<double> fillLatencyMs = new List<double>();
        // ===== Limit executioner controls + quality =====
        private readonly CheckBox execEnable = new CheckBox();
        private readonly ComboBox execModeSelector = new ComboBox();
        // Engine-direct orders: engine writes to the command ring itself,
        // bypassing the dashboard forward hop. When on, the dashboard MUST NOT
        // also forward (would double-fire).
        private readonly CheckBox engineDirectOrders = new CheckBox();
        private readonly CheckBox mmfGates = new CheckBox();   // v64y: enforce the entry gates inside NT8
        // v65a book-quality + health gates. All default to 0 = off, because a book gate must
        // never block a data feed that carries no depth.
        private readonly TextBox  maxSpreadTicksInput  = new TextBox();
        private readonly TextBox  minImbalanceInput    = new TextBox();
        private readonly CheckBox micropriceEntryGate  = new CheckBox();
        private readonly TextBox  maxFlickerEntryInput = new TextBox();
        private readonly TextBox  maxAggrIntoInput     = new TextBox();
        private readonly TextBox  minBookSizeInput     = new TextBox();
        private readonly TextBox  staleSignalMsInput   = new TextBox();
        private readonly TextBox  feedStaleMsInput     = new TextBox();
        private readonly TextBox  giveBackPercentInput = new TextBox();

        // v65b: one enable box per gate. These decide what SendEntryGateConfig SENDS - the value
        // in the box beside it is never cleared, so unticking a gate and re-ticking it later
        // brings the same number back. All default OFF: upgrading must not silently arm a gate.
        private readonly GroupBox entryGatePanel   = new GroupBox();
        private readonly CheckBox gateOnSession    = new CheckBox();
        private readonly CheckBox gateOnMaxTrades  = new CheckBox();
        private readonly CheckBox gateOnTradeGap   = new CheckBox();
        private readonly CheckBox gateOnCooldown   = new CheckBox();
        private readonly CheckBox gateOnFlips      = new CheckBox();
        private readonly CheckBox gateOnRamp       = new CheckBox();
        private readonly CheckBox gateOnDeadRegime = new CheckBox();
        private readonly CheckBox gateOnMargin     = new CheckBox();
        private readonly CheckBox gateOnDirection  = new CheckBox();
        private readonly CheckBox gateOnStaleSignal= new CheckBox();
        private readonly CheckBox gateOnSpread     = new CheckBox();
        private readonly CheckBox gateOnImbalance  = new CheckBox();
        private readonly CheckBox gateOnFlicker    = new CheckBox();
        private readonly CheckBox gateOnAggression = new CheckBox();
        private readonly CheckBox gateOnBookSize   = new CheckBox();
        private readonly CheckBox gateOnFeedStale  = new CheckBox();
        private readonly CheckBox gateOnGiveBack   = new CheckBox();
        private readonly Label    activeGatesLabel = new Label();
        private readonly ComboBox engineOrderModeSelector = new ComboBox();
        private bool engineDirectActive; // latched at Start from the checkbox
        private readonly CheckBox paperNoTimeout = new CheckBox();
        private readonly TextBox paperMaxOpenInput = new TextBox();
        private readonly TextBox paperEntryLatencyInput = new TextBox();
        private readonly TextBox labelLatencyInput = new TextBox();
        private readonly CheckBox pinEngineCore0 = new CheckBox();
        // Hog -> engine plumbing: evidence record read by the engine's hog-fusion head,
        // optional live fusion, optional entry agreement gate (engine-direct + dashboard forwarding).
        private readonly CheckBox hogEvidenceToEngine = new CheckBox();
        private readonly ComboBox hogFusionModeSelector = new ComboBox();
        private readonly CheckBox hogAgreeGate = new CheckBox();
        private readonly Label hogFusionStatus = new Label();
        private double lastHogProbability = double.NaN; private DateTime lastHogProbabilityUtc = DateTime.MinValue; private DateTime lastHogGateLogUtc = DateTime.MinValue;
        private const string HogEvidenceMapName = @"Local\LNN_HOG_EVIDENCE_MES";
        private static readonly Regex HogFusionMetrics = new Regex(
            @"^hog-fusion mode=(?<mode>[a-z]+) evidence=(?<ev>[a-z]+) age-ms=(?<age>-?[0-9.]+) p-hog=(?<phog>[0-9.]+) p-engine=(?<peng>[0-9.]+) p-fused=(?<pf>[0-9.]+) w-engine=(?<we>-?[0-9.]+) w-hog=(?<wh>-?[0-9.]+) w-agree=(?<wa>-?[0-9.]+) labels=(?<labels>\d+) fused-acc=(?<fa>[0-9.]+)% engine-acc=(?<ea>[0-9.]+)% hog-acc=(?<ha>[0-9.]+)% agree-rate=(?<ar>[0-9.]+)% acc-when-agree=(?<aa>[0-9.]+)% acc-when-disagree=(?<ad>[0-9.]+)% gate=(?<gate>on|off) gate-blocked=(?<blocked>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Nested hog tab: a separate lightweight process (LnnNestedHog.exe), NOT
        // a second trading engine. Runs the nested cells multi-core, off core 0.
        private Process hogProcess;
        private readonly Label hogStatus = new Label();
        private readonly Label hogProbability = new Label();
        private readonly Label hogPlanLabel = new Label();
        private readonly TextBox hogSummaryBox = new TextBox();
        private readonly TextBox hogExeInput = new TextBox();
        private readonly TextBox hogMapInput = new TextBox();
        private readonly TextBox hogHiddenInput = new TextBox();
        private readonly TextBox hogPassMsInput = new TextBox();
        private readonly TextBox hogTargetParamsInput = new TextBox();
        private readonly TextBox hogExcludeCoreInput = new TextBox();
        private readonly ComboBox hogTimescalePreset = new ComboBox();   // v64k: time-frame presets
        private readonly TextBox hogTimescalesInput = new TextBox();     // 8 seconds, ascending (blank = scalp default)
        private readonly TextBox hogMinHorizonInput = new TextBox();     // blank = Live tab value
        private readonly TextBox hogMaxHorizonInput = new TextBox();
        private static readonly string[][] HogTimescalePresets = {
            new[] { "Scalp 50 ms - 5 min (default)", "0.05,0.25,1,5,10,30,60,300" },
            new[] { "Ultra 20 ms - 2 min (x0.4)",    "0.02,0.1,0.4,2,4,12,24,120" },
            new[] { "Fast 25 ms - 2.5 min (x0.5)",   "0.025,0.125,0.5,2.5,5,15,30,150" },
            new[] { "Slow 100 ms - 10 min (x2)",     "0.1,0.5,2,10,20,60,120,600" },
            new[] { "Swing 200 ms - 20 min (x4)",    "0.2,1,4,20,40,120,240,1200" },
            new[] { "Custom (edit the box)",         "" }
        };
        private readonly CheckBox hogParallel = new CheckBox();
        private readonly CheckBox hogForce = new CheckBox();
        private readonly Button hogStartButton = new Button();
        private readonly Button hogStopButton = new Button();
        private readonly TextBox hogLog = new TextBox();
        private string hogMetricsText = "", hogCellsText = "", hogTsText = "", hogTimescaleLine = "";
        private readonly TextBox exImbalanceInput = new TextBox();
        private readonly TextBox exRestingMsInput = new TextBox();
        private readonly TextBox exFlickerInput = new TextBox();
        private readonly CheckBox exMicropriceGate = new CheckBox();
        private readonly TextBox exAggrQtyInput = new TextBox();
        private readonly TextBox exAggrWindowInput = new TextBox();
        private readonly TextBox exChaseInput = new TextBox();
        private readonly TextBox exTimeoutInput = new TextBox();
        private readonly ComboBox exOnTimeoutSelector = new ComboBox();
        private readonly TextBox exMark1Input = new TextBox();
        private readonly TextBox exMark2Input = new TextBox();
        private readonly TextBox execQualityPanel = new TextBox();
        private readonly TextBox learnedCostPanel = new TextBox();
        private readonly List<double> exEffSpread = new List<double>();
        private readonly List<double> exMk1 = new List<double>();
        private readonly List<double> exMk2 = new List<double>();
        private int exSamples, exFillsSeen, exNoFills;
        private readonly int[] exOutcomeCounts = new int[5]; // 0 filled,1 cancel-aggr,2 timeout-abandon,3 timeout-cross,4 missed-chase
        private readonly System.Windows.Forms.Timer hubWatchdog = new System.Windows.Forms.Timer();

        // ---- v64m: coalesced UI painting -------------------------------------
        // Every log line and every panel refresh used to repaint the instant its
        // event arrived, on the fill-ring drain thread's callback. During a busy
        // run that is hundreds of TextBox.AppendText calls a second, each one a
        // re-layout and a scroll-to-end, and it made the whole window sluggish and
        // unreadable. Lines are now buffered and painted at LogFlushIntervalMs.
        private readonly System.Windows.Forms.Timer uiPaintTimer = new System.Windows.Forms.Timer();
        private readonly StringBuilder pendingOutputLog = new StringBuilder();
        private readonly StringBuilder pendingFillLog = new StringBuilder();
        private bool tradePathDirty, latencyGateDirty;
        private const int LogFlushIntervalMs = 500;

        private readonly List<DateTime> aiSignalFlipTimes = new List<DateTime>();
        private readonly List<double> paperPnlHistory = new List<double>();
        private int aiEntriesThisSession;
        private double paperNetTicksAtArm;
        private bool paperNetTicksAtArmSet;
        private double latestPaperNetTicks;
        private int latestPaperTradeCount;
        private long lastFillRingSequenceSeen = -1;
        private DateTime lastStrategyActivityUtc = DateTime.MinValue;
        private static readonly string UiStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LnnLiveDashboard", "ui-state.txt");
        private static readonly string ProfilesFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LnnLiveDashboard", "profiles");
        private static readonly string SessionSummaryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LnnLiveDashboard", "session-summaries.log");

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        private readonly System.Windows.Forms.Timer inputPersistenceTimer = new System.Windows.Forms.Timer();
        private bool inputSettingsDirty;
        private readonly ComboBox instrument = new ComboBox();
        private readonly ComboBox engine = new ComboBox();
        private readonly Button browseEngine = new Button();
        private readonly Label runtimeLabel = new Label();
        private readonly TextBox pythonExecutable = new TextBox();
        private readonly Button browsePython = new Button();
        private readonly TextBox tickSizeInput = new TextBox();
        private readonly TextBox estimatedCostTicks = new TextBox();
        private readonly TextBox minimumProfitTargetTicks = new TextBox();
        private readonly TextBox labelStopTicks = new TextBox();   // v64r: adverse distance that kills a label (blank/0 = symmetric)
        private readonly TextBox neutralBlockSecondsInput = new TextBox();  // v64x: dead-regime entry gate, 0 = off
        private readonly TextBox paperLongThreshold = new TextBox();
        private readonly TextBox paperShortThreshold = new TextBox();
        private readonly TextBox processingIntervalMilliseconds = new TextBox();
        private readonly TextBox minimumLabelDeadlineMilliseconds = new TextBox();
        private readonly TextBox maximumLabelDeadlineMilliseconds = new TextBox();
        private readonly TextBox reversalTriggerTicks = new TextBox();
        private readonly TextBox reversalDistanceTicks = new TextBox();
        private readonly TextBox reversalTrendTicks = new TextBox();
        private readonly TextBox reversalTimeoutMilliseconds = new TextBox();
        private readonly TextBox reversalProbabilityThreshold = new TextBox();
        private readonly CheckBox reversalEnable = new CheckBox();
        private readonly TextBox reversalSummaryBox = new TextBox();
        private readonly TextBox reversalEventLog = new TextBox();
        private readonly CheckBox saveRawFrames = new CheckBox();
        private readonly CheckBox recordDomEvents = new CheckBox();   // --save-events (executioner backtest / replay)
        private string lastDomEventsPath;
        private Process backtestProcess;
        private readonly Button runExecBacktest = new Button();
        private readonly TextBox backtestSweepInput = new TextBox();
        private readonly TextBox exCancelAheadInput = new TextBox();
        private readonly ComboBox exPolicySelector = new ComboBox();
        private readonly TextBox exExpectedMoveInput = new TextBox();
        private readonly Button fitFillModelButton = new Button();
        private Process fitProcess;
        private readonly CheckBox saveDashboardLog = new CheckBox();
        private readonly CheckBox emitPerEvent = new CheckBox();
        private readonly CheckBox domShift = new CheckBox();   // --dom-shift (NT8 Add=insert / Remove=delete row semantics)
        private readonly TextBox coalesceAboveInput = new TextBox();   // --coalesce-above (per-event mode burst guard)
        private readonly TextBox learningRateInput = new TextBox();
        private readonly TextBox validationStrideInput = new TextBox();
        private readonly TextBox restingDepthInput = new TextBox();
        private readonly TextBox probSmoothingInput = new TextBox();
        private readonly TextBox regimeMinRangeInput = new TextBox();
        private readonly TextBox regimeMaxNoveltyInput = new TextBox();
        private readonly CheckBox saveFullWeightAudit = new CheckBox();
        private readonly CheckBox saveWeights = new CheckBox();
        private readonly TextBox loadCheckpointFile = new TextBox();
        private readonly Button browseLoadCheckpoint = new Button();
        private readonly TextBox captureFolder = new TextBox();
        private readonly Button browseCapture = new Button();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Button startPaperSimulation = new Button();
        private readonly Button stopPaperSimulation = new Button();
        private readonly Button saveModelNow = new Button();
        private readonly Button loadHorizonStudy = new Button();
        private readonly Button runHistoricalStudy = new Button();
        private readonly Button runMesAdapter = new Button();
        private readonly ComboBox trainingProfile = new ComboBox();
        private readonly TextBox weightCategory = new TextBox();
        private readonly TextBox trainingHorizonSeconds = new TextBox();
        private readonly TextBox trainingSymbol = new TextBox();
        private readonly TextBox trainingInstrumentId = new TextBox();
        private readonly TextBox customTrainerFolder = new TextBox();
        private readonly Button browseCustomTrainerFolder = new Button();
        private readonly ComboBox customTrainerScript = new ComboBox();
        private readonly TextBox activeWeightRoot = new TextBox();
        private readonly Button browseActiveWeightRoot = new Button();
        private readonly CheckBox autoAdoptActiveWeights = new CheckBox();
        private readonly Button trainPythonProfile = new Button();
        private readonly Button exportOnnxPackage = new Button();
        private readonly TextBox onnxRegistryRoot = new TextBox();
        private readonly Button browseOnnxRegistryRoot = new Button();
        private readonly Button refreshOnnxRegistry = new Button();
        private readonly Button useSelectedOnnxPackage = new Button();
        private readonly ListView onnxRegistryList = new ListView();
        private readonly Label onnxRegistryStatus = new Label();
        private readonly Label activeOnnxPackage = new Label();
        private readonly Label horizonStudyStatus = new Label();
        private readonly Label status = new Label();
        private readonly Label frames = MakeMetric("Frames", "0");
        private readonly Label valid = MakeMetric("Valid books", "0");
        private readonly Label trained = MakeMetric("Online updates", "0");
        private readonly Label noTrade = MakeMetric("No-trade outcomes", "0");
        private readonly Label accuracy = MakeMetric("Online label accuracy", "Waiting");
        private readonly Label probability = MakeMetric("Current up probability", "Waiting");
        private readonly Label weightNorm = MakeMetric("Weight L2 norm", "0");
        private readonly Label bestValidation = MakeMetric("Validated best weights", "Waiting");
        private readonly Label liveValidation = MakeMetric("Medium unseen-label check", "Waiting");
        private readonly Label majorityBaseline = MakeMetric("Majority baseline", "Waiting");
        private readonly Label upPredictionCount = MakeMetric("Up predictions", "0");
        private readonly Label upPredictionRate = MakeMetric("Up prediction rate", "Waiting");
        private readonly Label downPredictionCount = MakeMetric("Down predictions", "0");
        private readonly Label downPredictionRate = MakeMetric("Down prediction rate", "Waiting");
        private readonly Label paperSignal = MakeMetric("Current paper signal", "WARMUP");
        private readonly Label paperSignalCounts = MakeMetric("Paper signal counts", "0 / 0 / 0");
        private readonly Label paperTradeSummary = MakeMetric("Paper trades", "0 completed");
        private readonly Label paperTradePnl = MakeMetric("Paper net P/L", "0.00 ticks");
        private readonly TextBox latencyPanel = new TextBox();
        private readonly TextBox outcomePanel = new TextBox();
        private readonly TextBox analystHistory = new TextBox();
        private readonly TextBox analystQuestion = new TextBox();
        private readonly Button askAnalyst = new Button();
        private readonly TextBox output = new TextBox();
        private readonly TextBox learningMonitorSummary = new TextBox();
        private readonly TextBox learningMonitorHistory = new TextBox();
        private int lastMonitorTrainedMilestone = -1;
        private readonly Label twoTickStatus = new Label();
        private readonly Label flickerStatus = new Label();
        private string flickerBaseText = "Order flicker: waiting for engine...";
        private readonly Label paperOutcomeStatus = new Label();
        private readonly TextBox shadowTeacherSummary = new TextBox();
        private readonly TextBox shadowTeacherHistory = new TextBox();
        private readonly TextBox temporalObserverSummary = new TextBox();
        private readonly TextBox temporalObserverHistory = new TextBox();
        private readonly TextBox observerFastTicks = new TextBox();
        private readonly TextBox observerFastMilliseconds = new TextBox();
        private readonly TextBox observerMediumTicks = new TextBox();
        private readonly TextBox observerMediumMilliseconds = new TextBox();
        private readonly TextBox observerSlowTicks = new TextBox();
        private readonly TextBox observerSlowMilliseconds = new TextBox();
        private Process learner;
        private Process mmfForwarder;
        private Process compactTeacher;
        private Process temporalObserver;
        private Process crossMarketShadowLogger;
        private string activeControlPipe;
        private string activePaperControlPipe;
        private string activeShadowControlPipe;
        private string activeModelSavePipe;
        private StreamWriter logWriter;
        private string activeWeightUpdateLogPath;
        private string latestMetricsLine;
        private string latestPaperMetricsLine;
        private string latestOutcomeTimeFramesLine;
        private string activeOnnxPackagePath;
        private string shadowReversalStatus = "Waiting for reversal scenarios.";
        private string shadowReversalFusionStatus = "Reversal fusion: waiting for a triggered scenario and 100 resolved reversal labels.";
        private string shadowTeacherStatus = "Waiting for compact-teacher samples.";
        private string shadowNoveltyStatus = "Waiting for enough frames to establish the live distribution.";
        private string temporalObserverMarketStatus = "Waiting for temporal MMF market state.";
        private string temporalObserverFastStatus = "FAST head: waiting for labels.";
        private string temporalObserverMediumStatus = "MEDIUM head: waiting for labels.";
        private string temporalObserverSlowStatus = "SLOW head: waiting for labels.";
        private readonly object telemetryThrottleLock = new object();
        private DateTime lastTelemetryDispatchUtc = DateTime.MinValue;
        private DateTime lastLiveProbabilityDispatchUtc = DateTime.MinValue;
        private DateTime lastLearnerErrorDispatchUtc = DateTime.MinValue;
        private readonly List<string> learnerStderrTail = new List<string>();
        private DateTime learnerStartedUtc = DateTime.MinValue;

        private sealed class FeatureAuditItem
        {
            internal string TemporalState;
            internal string FeatureName;
            internal double WeightDelta;
        }

        private sealed class OnnxPackage
        {
            internal string Folder;
            internal string Symbol;
            internal double TickSize;
            internal int DepthLevels;
            internal int DepthFeatureCount;
            internal double TargetTicks;
            internal double HorizonSeconds;
            internal string Edge;
            internal string Compatibility;
            internal string Detail;
        }

        private sealed class EngineChoice
        {
            internal readonly string Name;
            internal readonly string Path;
            internal readonly bool IsPythonScript;
            internal readonly bool IsMojoScript;
            internal readonly bool IsNativeLearner;
            internal readonly bool IsDashboardDirectMmfLearner;
            internal readonly bool IsDashboardPipeNativeTemporal;

            // A plug-in engine receives the stable dashboard-owned frame pipe.
            // Native LnnLiveLearner executables retain their richer, backwards-
            // compatible command line and can read MMF directly.
            internal bool UsesDashboardPipeTransport { get { return (!IsNativeLearner && !IsDashboardDirectMmfLearner) || IsDashboardPipeNativeTemporal; } }
            internal bool NeedsRuntime { get { return IsPythonScript || IsMojoScript; } }

            internal EngineChoice(string name, string path)
            {
                Name = name;
                Path = path;
                IsPythonScript = string.Equals(System.IO.Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase);
                IsMojoScript = string.Equals(System.IO.Path.GetExtension(path), ".mojo", StringComparison.OrdinalIgnoreCase);
                IsDashboardDirectMmfLearner = string.Equals(System.IO.Path.GetFileName(path),
                    "LnnLiveLearner_Mmf130Feature_ReadOnly.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path),
                    "LnnLiveLearner_MmfUnifiedShadow.exe", StringComparison.OrdinalIgnoreCase);
                IsDashboardPipeNativeTemporal = string.Equals(System.IO.Path.GetFileName(path),
                    "LnnLiveLearner_HybridTemporalMmf.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v2.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v3.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v4.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v5.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v6.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v7.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v8.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v9.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v10.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v11.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(path), "LnnLiveLearner_HybridTemporalMmf_v12.exe", StringComparison.OrdinalIgnoreCase);
                IsNativeLearner = string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Path.GetFileName(path).StartsWith("LnnLiveLearner", StringComparison.OrdinalIgnoreCase) &&
                    !IsDashboardDirectMmfLearner;
            }

            public override string ToString() { return Name; }
        }

        private static readonly Regex Metrics = new Regex(
            @"frames=(?<frames>\d+) valid=(?<valid>\d+) trained=(?<trained>\d+) no-trade=(?<skipped>\d+) predictions=(?<predictions>\d+) up-predictions=(?<up_predictions>\d+) up-prediction-rate=(?<up_rate>[0-9.]+)% down-predictions=(?<down_predictions>\d+) down-prediction-rate=(?<down_rate>[0-9.]+)% up-labels=(?<up>\d+) down-labels=(?<down>\d+)(?: no-move-labels=(?<no_move>\d+))? online-label-accuracy=(?<accuracy>[0-9.]+)% majority-baseline=(?<baseline>[0-9.]+)% current-up-probability=(?<probability>[0-9.]+) weight-l2=(?<weight>[0-9.]+) paper-signal=(?<signal>LONG|SHORT|NO-TRADE|WARMUP) paper-long-signals=(?<long_signals>\d+) paper-short-signals=(?<short_signals>\d+) paper-no-trade-signals=(?<no_trade_signals>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Per-second summary of the direct-MMF unified-shadow learner.
        private static readonly Regex PaperOutcomeMetrics = new Regex(
            @"^paper-outcome signals-scored=(?<n>\d+)(?: overall-hit=(?<overall>[0-9.]+)% long-hit=(?<longhit>[0-9.]+)% \(n=(?<nl>\d+)\) short-hit=(?<shorthit>[0-9.]+)% \(n=(?<ns>\d+)\))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex FlickerMetrics = new Regex(
            @"^flicker adds-per-sec=(?<adds>[0-9.]+) cancels-per-sec=(?<cancels>[0-9.]+) trades-per-sec=(?<trades>[0-9.]+) cancel-add-ratio=(?<ratio>[0-9.]+) cancel-without-trade=(?<cwt>[0-9.]+)%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HogMetrics = new Regex(
            @"^hog frames=(?<frames>\d+) labels=(?<labels>\d+) up-labels=(?<up>\d+) down-labels=(?<down>\d+) online-accuracy=(?<acc>[0-9.]+)% majority-baseline=(?<base>[0-9.]+)% skill=(?<skill>[+-]?[0-9.]+)% up-probability=(?<prob>[0-9.]+) open-labels=(?<open>\d+) latency-armed=(?<armed>\d+) vol-ticks=(?<vol>[0-9.]+) events-per-sec=(?<eps>[0-9.]+) pass-ms=(?<pass>[0-9.]+) hidden=(?<hidden>\d+) params=(?<params>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HogUpProbability = new Regex(@"^hog-up-probability=(?<probability>[0-9.]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex FlickerLevelMetrics = new Regex(
            @"^flicker-levels L1-adds/s=(?<a1>[0-9.]+) L1-cancels/s=(?<c1>[0-9.]+) L1-cancel-add-ratio=(?<r1>[0-9.]+) L2-adds/s=(?<a2>[0-9.]+) L2-cancels/s=(?<c2>[0-9.]+) L2-cancel-add-ratio=(?<r2>[0-9.]+) L3-adds/s=(?<a3>[0-9.]+) L3-cancels/s=(?<c3>[0-9.]+) L3-cancel-add-ratio=(?<r3>[0-9.]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex TwoTickMetrics = new Regex(
            @"^twotick target=(?<target>[0-9.]+)t?(?: stop=(?<stop>[0-9.]+)t)?(?: at-touch cost=(?<cost>[0-9.]+)t)? p-up=(?<pup>[0-9.]+) samples=(?<samples>\d+)(?: untradeable=(?<untradeable>\d+))?(?: realized-up-first=(?<realized>[0-9.]+)% directional-accuracy=(?<accuracy>[0-9.]+)% skill-vs-base=(?<skill>[+-]?[0-9.]+)% up-call-reliability=(?<uprel>[0-9.]+)% up-edge=(?<upedge>[+-]?[0-9.]+)% down-call-reliability=(?<downrel>[0-9.]+)% down-edge=(?<downedge>[+-]?[0-9.]+)% avg-lead-ms=(?<lead>[0-9.]+))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UnifiedShadowMetrics = new Regex(
            @"^frames=(?<frames>\d+) valid=(?<valid>\d+) trained=(?<trained>\d+) labels=(?<labels>\d+) online-label-accuracy=(?<accuracy>[0-9.]+)% current-up-probability=(?<probability>[0-9.]+) weight-l2=(?<weight>[0-9.]+) paper-signal=(?<signal>[A-Z-]+) paper-long-signals=(?<long_signals>\d+) paper-short-signals=(?<short_signals>\d+) paper-no-trade-signals=(?<no_trade_signals>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Mmf130FeatureMetrics = new Regex(
            @"std=(?<standardizer>\d+) trained=(?<trained>\d+) labeled=(?<labeled>\d+) acc=(?<accuracy>[0-9.]+)% valEdge=(?<edge>-?[0-9.]+)% pred=(?<prediction>-?[0-9.]+)t sig=(?<signal>LONG|SHORT|NO-TRADE|WARMUP|HALTED) L=(?<long>\d+) S=(?<short>\d+) vol=(?<volatility>[0-9.]+) tgt=(?<target>[0-9.]+)t batch=(?<batch>\d+)ev",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex OnnxInferenceMetrics = new Regex(
            @"^onnx frames=(?<frames>\d+) valid=(?<valid>\d+) skipped=(?<skipped>\d+) warmup=(?<warmup>\d+/\d+) p_fast=(?<fast>[0-9.]+|nan) p_medium=(?<medium>[0-9.]+|nan) p_slow=(?<slow>[0-9.]+|nan) latency_ms=(?<latency>[0-9.]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex AdaptiveMultiTaskMetrics = new Regex(
            @"^adaptive frames=(?<frames>\d+) valid=(?<valid>\d+) skipped=(?<skipped>\d+) candidates=(?<candidates>\d+) pending=(?<pending>\d+) resolved=(?<resolved>\d+) updates=(?<updates>\d+) acc=(?<accuracy>[0-9.]+)% loss=(?<loss>-?[0-9.]+|nan) latency_ms=(?<latency>[0-9.]+) p_fast=(?<fast>[0-9.]+) p_medium=(?<medium>[0-9.]+) p_slow=(?<slow>[0-9.]+)(?: queue=(?<queue>\d+) dropped=(?<dropped>\d+))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex AdaptiveLearningUpdate = new Regex(
            @"^adaptive learning-update update=(?<update>\d+) batch=(?<batch>\d+) resolved=(?<resolved>\d+) loss=(?<loss>-?[0-9.]+|nan) queue=(?<queue>\d+) dropped=(?<dropped>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex ReversalMetrics = new Regex(
            @"^reversal-scenarios=(?<scenarios>\d+) reversal-resolved=(?<resolved>\d+) reversal-count=(?<reversals>\d+) trend-count=(?<trends>\d+) reversal-timeouts=(?<timeouts>\d+) reversal-predictions=(?<predictions>\d+) reversal-accuracy=(?<accuracy>[0-9.]+)% reversal-recall-proxy=(?<recall>[0-9.]+)% current-reversal-probability=(?<probability>[0-9.]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex ReversalFusionMetrics = new Regex(
            @"^reversal-fusion mode=shadow state=(?<state>[A-Z_]+) main=(?<main>UP|DOWN) local=(?<local>UP|DOWN|NONE) main-probability=(?<main_probability>[0-9.]+) reversal-probability=(?<reversal_probability>[0-9.]+) hypothetical-probability=(?<hypothetical_probability>[0-9.]+) reversal-resolved=(?<resolved>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LiveUpProbability = new Regex(
            @"^live-up-probability=(?<probability>[0-9.]+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MarketNovelty = new Regex(
            @"^market-novelty score=(?<score>[0-9.]+) extreme-features=(?<extreme>\d+) state=(?<state>NORMAL|ELEVATED|UNFAMILIAR)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PreUnfamiliarCheckpoint = new Regex(
            @"^pre-unfamiliar-checkpoint-saved file=(?<file>.+) score=(?<score>[0-9.]+) extremes=(?<extreme>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TemporalObserverMetrics = new Regex(
            @"^tssc-observer status=(?<status>[^ ]+) frames=(?<frames>\d+) gaps=(?<gaps>\d+) mm-confidence=(?<confidence>[0-9.]+) bias=(?<bias>-?[0-9.]+) absorption=(?<absorption>[0-9.]+) speed-tps=(?<speed>[0-9.]+) spread-ticks=(?<spread>[0-9.]+) fast=(?<fast>-?[0-9.]+) medium=(?<medium>-?[0-9.]+) slow=(?<slow>-?[0-9.]+) consistency-error=(?<error>[0-9.]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TemporalObserverHead = new Regex(
            @"^tssc-head name=(?<name>FAST|MEDIUM|SLOW) target-ticks=(?<target>[0-9.]+) deadline-ms=(?<deadline>\d+) updates=(?<updates>\d+) labels-up=(?<up>\d+) labels-down=(?<down>\d+) expired=(?<expired>\d+) accuracy=(?<accuracy>[0-9.]+)% up-probability=(?<probability>[0-9.]+) weight-l2=(?<weight>[0-9.]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SignalUpdate = new Regex(
            @"paper-signal=(?<signal>LONG|SHORT|NO-TRADE|WARMUP) current-up-probability=(?<probability>[0-9.]+) trained=(?<trained>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PaperMetrics = new Regex(
            @"paper-trades=(?<trades>\d+) paper-wins=(?<wins>\d+) paper-losses=(?<losses>\d+) paper-win-rate=(?<win_rate>[0-9.]+)% paper-net-ticks=(?<net_ticks>-?[0-9.]+) paper-max-drawdown-ticks=(?<drawdown>[0-9.]+) paper-open=(?<open>[01]) paper-sim-enabled=(?<enabled>[01])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PerformanceMetrics = new Regex(
            @"processed-frames=(?<processed>\d+) performance-skipped-frames=(?<skipped>\d+) processing-interval-ms=(?<interval>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ValidationMetrics = new Regex(
            @"validation-samples=(?<samples>\d+) validation-window=(?<window>\d+) validation-accuracy=(?<accuracy>-?[0-9.]+)% validation-baseline=(?<baseline>-?[0-9.]+)% validation-edge=(?<edge>-?[0-9.]+)% best-validation-edge=(?<best>-?[0-9.]+)%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ThreeOutcomeTimeFrames = new Regex(
            @"three-outcome-timeframes fast-horizon-ms=(?<fast_horizon>\d+) fast-updates=(?<fast_updates>\d+) fast-labels-up=(?<fast_up>\d+) fast-labels-down=(?<fast_down>\d+) fast-labels-no-move=(?<fast_no_move>\d+) fast-label-accuracy=(?<fast_accuracy>[0-9.]+)% medium-horizon-ms=(?<medium_horizon>\d+) medium-updates=(?<medium_updates>\d+) medium-labels-up=(?<medium_up>\d+) medium-labels-down=(?<medium_down>\d+) medium-labels-no-move=(?<medium_no_move>\d+) medium-label-accuracy=(?<medium_accuracy>[0-9.]+)% slow-horizon-ms=(?<slow_horizon>\d+) slow-updates=(?<slow_updates>\d+) slow-labels-up=(?<slow_up>\d+) slow-labels-down=(?<slow_down>\d+) slow-labels-no-move=(?<slow_no_move>\d+) slow-label-accuracy=(?<slow_accuracy>[0-9.]+)%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // New long-horizon learners may emit one line per head.  The dashboard
        // accepts this additive format without disturbing legacy three-head logs.
        private static readonly Regex SessionHorizonMetrics = new Regex(
            @"session-horizon name=(?<name>[^ ]+) target-ticks=(?<target>[0-9.]+) pending=(?<pending>\d+) up=(?<up>\d+) down=(?<down>\d+) censored=(?<censored>\d+) updates=(?<updates>\d+) accuracy=(?<accuracy>[0-9.]+)% baseline=(?<baseline>[0-9.]+)% median-hit-s=(?<median>[0-9.]+|n/a) mean-hit-s=(?<mean>[0-9.]+|n/a)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex InvalidBookMetrics = new Regex(
            @"invalid-books=(?<invalid>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex IntegrityMetrics = new Regex(
            @"invalid-books=(?<invalid>\d+) locked-books=(?<locked>\d+) corrupt-books=(?<corrupt>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LatencyMetrics = new Regex(
            @"latency-frames=(?<frames>\d+) latency-predictions=(?<predictions>\d+) feed-event-age-ms=(?<feed>[0-9.]+/[0-9.]+/[0-9.]+|waiting) callback-to-publish-ms=(?<publish>[0-9.]+/[0-9.]+/[0-9.]+) publish-to-receive-ms=(?<receive>[0-9.]+/[0-9.]+/[0-9.]+) receiver-to-prediction-ms=(?<compute>[0-9.]+/[0-9.]+/[0-9.]+) callback-to-prediction-ms=(?<total>[0-9.]+/[0-9.]+/[0-9.]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ManualModelSaved = new Regex(
            @"manual-model-saved file=(?<file>.+) processed-frame=(?<frame>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal DashboardForm()
        {
            Text = "LNN Dashboard — Live + AI Trader (build " + BuildStamp + ")";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi; // scale fixed-pixel layout on high-DPI displays
            ClientSize = new Size(1180, 1860);
            MinimumSize = new Size(980, 1060);
            StartPosition = FormStartPosition.CenterScreen;

            Label instrumentLabel = new Label { Text = "Instrument", AutoSize = true, Location = new Point(18, 20) };
            instrument.DropDownStyle = ComboBoxStyle.DropDownList;
            instrument.Items.Add("M6E — Micro EUR/USD");
            instrument.Items.Add("MES — Micro E-mini S&P 500");
            instrument.Items.Add("MES — Every depth/trade event");
            instrument.Items.Add("MNQ — Micro E-mini Nasdaq-100");
            instrument.Items.Add("MNQ — Every depth/trade event");
            instrument.Items.Add("ES — E-mini S&P 500");
            instrument.Items.Add("M6E — Every depth/trade event");
            instrument.Items.Add("MNQ — MMF event stream (fast)");
            instrument.Items.Add("MES — MMF event stream (fast)");
            instrument.SelectedIndex = -1; // no default selection: the user must pick an instrument
            instrument.Location = new Point(88, 16);
            instrument.Width = 220;
            instrument.SelectedIndexChanged += InstrumentChanged;

            Label tickSizeLabel = new Label { Text = "Tick", AutoSize = true, Location = new Point(315, 20) };
            tickSizeInput.Text = "0.0001";
            tickSizeInput.Location = new Point(347, 16);
            tickSizeInput.Width = 68;

            Label engineLabel = new Label { Text = "LNN engine", AutoSize = true, Location = new Point(425, 20) };
            engine.DropDownStyle = ComboBoxStyle.DropDownList;
            engine.Location = new Point(493, 16);
            engine.Width = 220;
            browseEngine.Text = "Browse...";
            browseEngine.Location = new Point(720, 15);
            browseEngine.AutoSize = true;
            browseEngine.Click += BrowseEngineClick;
            engine.SelectedIndexChanged += EngineSelectionChanged;
            LoadEngines();

            runtimeLabel.Text = "Runtime";
            runtimeLabel.AutoSize = true;
            runtimeLabel.Location = new Point(18, 57);
            string bundledPython = @"C:\Users\abuel\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe";
            pythonExecutable.Text = File.Exists(bundledPython) ? bundledPython : "python.exe";
            pythonExecutable.Location = new Point(66, 53);
            pythonExecutable.Width = 567;
            browsePython.Text = "Runtime...";
            browsePython.Location = new Point(640, 52);
            browsePython.AutoSize = true;
            browsePython.Click += BrowsePythonClick;
            Label costLabel = new Label { Text = "RT fee ($)", AutoSize = true, Location = new Point(704, 57) };
            estimatedCostTicks.Text = "0.00";
            estimatedCostTicks.Location = new Point(784, 53);
            estimatedCostTicks.Width = 74;

            saveRawFrames.Text = "Save 10-level historical frames";
            recordDomEvents.Text = "Record DOM events"; recordDomEvents.AutoSize = true; recordDomEvents.Checked = false;
            Tip(recordDomEvents, "Records every DOM ring event (depth add/update/remove with level position, and trades; 40-byte records) to <capture folder>\\<symbol>_dom_events_<time>.bin. Feed it to LnnExecBacktest.exe (executioner gate sweeps in seconds) or RingReplayer.exe (replay the tape into the engine/hog/dashboard). Written off the hot path. Applied at Start.");
            saveRawFrames.AutoSize = true;
            saveRawFrames.Checked = false; // lean live build: no raw DOM capture by default
            saveRawFrames.Location = new Point(18, 91);
            captureFolder.Text = @"D:\NT8_m6e_DOMdata";
            captureFolder.Location = new Point(155, 88);
            captureFolder.Width = 478;
            browseCapture.Text = "Folder...";
            browseCapture.Location = new Point(640, 87);
            browseCapture.AutoSize = true;
            browseCapture.Click += BrowseCaptureClick;
            saveDashboardLog.Text = "Save log";
            saveDashboardLog.AutoSize = true;
            saveDashboardLog.Checked = false; // lean live build: minimal logging by default
            saveDashboardLog.Location = new Point(720, 91);
            saveWeights.Text = "Weights";
            saveWeights.AutoSize = true;
            saveWeights.Checked = true;
            saveWeights.Location = new Point(800, 91);
            Label learningRateLabel = new Label { Text = "Learn rate", AutoSize = true, Location = new Point(18, 116) };
            learningRateInput.Text = "0.002"; learningRateInput.Location = new Point(85, 112); learningRateInput.Width = 60;
            Label validationStrideLabel = new Label { Text = "Val stride", AutoSize = true, Location = new Point(160, 116) };
            validationStrideInput.Text = "10"; validationStrideInput.Location = new Point(220, 112); validationStrideInput.Width = 40;
            Label restingDepthLabel = new Label { Text = "Rest ms", AutoSize = true, Location = new Point(275, 116) };
            restingDepthInput.Text = "0"; restingDepthInput.Location = new Point(325, 112); restingDepthInput.Width = 45;
            Tip(restingDepthInput, "Resting-depth filter (hybrid MMF): size must stay put this many ms before it counts toward features — ignores phantom/spoof size. 0 = off. Try 300.");
            Label probSmoothLabel = new Label { Text = "Prob smooth", AutoSize = true, Location = new Point(380, 116) };
            probSmoothingInput.Text = "0"; probSmoothingInput.Location = new Point(452, 112); probSmoothingInput.Width = 45;
            Tip(probSmoothingInput, "EMA smoothing of the signal probability: 0 = off (raw). Lower = calmer/less noise but more lag. Try 0.2. Model training + 2-tick calibration stay on the raw value.");
            Label regimeRangeLabel = new Label { Text = "Min range t", AutoSize = true, Location = new Point(510, 116) };
            regimeMinRangeInput.Text = "0"; regimeMinRangeInput.Location = new Point(578, 112); regimeMinRangeInput.Width = 40;
            Tip(regimeMinRangeInput, "Regime gate: don't take entries when price moved less than this many ticks over the last 5s (flat tape, no edge). 0 = off. Try 2-3.");
            Label regimeNoveltyLabel = new Label { Text = "Max novelty", AutoSize = true, Location = new Point(625, 116) };
            regimeMaxNoveltyInput.Text = "0"; regimeMaxNoveltyInput.Location = new Point(697, 112); regimeMaxNoveltyInput.Width = 40;
            Tip(regimeMaxNoveltyInput, "Regime gate: don't take entries when novelty score exceeds this (regime transition the model is still adapting to). 0 = off. Try 2.0.");
            Tip(learningRateInput, "Online gradient step size. Higher = faster adaptation to recent tape but noisier/more forgetful. Default 0.002.");
            Tip(validationStrideInput, "1 label in N is held out for unbiased validation; the rest train. Lower N = faster/steadier validation but less training data. 10-15 recommended.");

            emitPerEvent.Text = "Per-event frames";
            emitPerEvent.AutoSize = true;
            emitPerEvent.Checked = false;
            emitPerEvent.Location = new Point(720, 112);
            Tip(emitPerEvent, "Direct-MMF engines only: emit a snapshot per depth event (finest resolution, higher CPU) instead of once per read cycle.");
            coalesceAboveInput.Text = "8"; coalesceAboveInput.Width = 34;
            Tip(coalesceAboveInput, "Per-event frames only: burst guard. While the ring backlog (events published but not yet consumed) is at or below this number, every event gets its own frame and prediction. Above it, the rest of the backlog is drained into ONE frame carrying the latest book, so a burst never queues up behind per-event model runs (that queue is the tail of the callback->publish latency). 0 = pure per-event (no guard). The 'Ring backlog' line in the latency panel shows what you are getting.");
            domShift.Text = "NT8 row shift"; domShift.AutoSize = true; domShift.Checked = true;
            Tip(domShift, "v64j: rebuild the 10-level book with NinjaTrader's depth semantics - Operation.Add INSERTS a row at its position (deeper rows shift down) and Operation.Remove DELETES it (deeper rows shift up). Before v64j the engine only set/zeroed the slot, so after every add/remove the deeper rows were stale until each was touched again. Untick to reproduce the old reconstruction (--dom-shift 0). Applied at Start.");
            saveFullWeightAudit.Text = "Full audit (slow)";
            saveFullWeightAudit.AutoSize = true;
            saveFullWeightAudit.Checked = false;
            saveFullWeightAudit.Location = new Point(720, 110);
            Label loadCheckpointLabel = new Label { Text = "Load weights", AutoSize = true, Location = new Point(18, 116) };
            loadCheckpointFile.Location = new Point(100, 112);
            loadCheckpointFile.Width = 532;
            browseLoadCheckpoint.Text = "File...";
            browseLoadCheckpoint.Location = new Point(640, 111);
            browseLoadCheckpoint.AutoSize = true;
            browseLoadCheckpoint.Click += BrowseLoadCheckpointClick;
            Label targetLabel = new Label { Text = "Target ticks", AutoSize = true, Location = new Point(18, 146) };
            minimumProfitTargetTicks.Text = "1.00";
            minimumProfitTargetTicks.Location = new Point(116, 142);
            minimumProfitTargetTicks.Width = 58;
            Tip(minimumProfitTargetTicks, "The move, in ticks, that defines a win: labels resolve UP/DOWN when the mid first reaches +/- this many ticks within the horizon; with 'Paper mirrors live' OFF the paper sim takes profit at +target and stops at -target (measured at the exit touch, so the spread is inside the number); the 2-tick meter grades the same move; the hog uses it too. " +
                "v64k: this is now a FIXED value. Before v64k it was labelled a minimum with a '2 x recent volatility' rule on top, but that rule measured volatility per DOM event (thousandths of a tick on MES) and could never raise the target above this floor - it was always this number.");
            Label deadlineLabel = new Label { Text = "Label deadline ms", AutoSize = true, Location = new Point(195, 146) };
            minimumLabelDeadlineMilliseconds.Text = "2000";
            minimumLabelDeadlineMilliseconds.Location = new Point(301, 142);
            minimumLabelDeadlineMilliseconds.Width = 54;
            Label deadlineDash = new Label { Text = "to", AutoSize = true, Location = new Point(362, 146), ForeColor = Color.DimGray };
            maximumLabelDeadlineMilliseconds.Text = "30000";
            maximumLabelDeadlineMilliseconds.Location = new Point(382, 142);
            maximumLabelDeadlineMilliseconds.Width = 58;

            Label reversalLabel = new Label { Text = "Reversal shadow", AutoSize = true, Location = new Point(18, 190) };
            Label reversalTriggerLabel = new Label { Text = "trig", AutoSize = true, Location = new Point(112, 190), ForeColor = Color.DimGray };
            reversalTriggerTicks.Text = "2";
            reversalTriggerTicks.Location = new Point(139, 186);
            reversalTriggerTicks.Width = 32;
            Label reversalDistanceLabel = new Label { Text = "back", AutoSize = true, Location = new Point(178, 190), ForeColor = Color.DimGray };
            reversalDistanceTicks.Text = "2";
            reversalDistanceTicks.Location = new Point(210, 186);
            reversalDistanceTicks.Width = 32;
            Label reversalTrendLabel = new Label { Text = "trend", AutoSize = true, Location = new Point(249, 190), ForeColor = Color.DimGray };
            reversalTrendTicks.Text = "6";
            reversalTrendTicks.Location = new Point(285, 186);
            reversalTrendTicks.Width = 32;
            Label reversalTimeoutLabel = new Label { Text = "timeout ms", AutoSize = true, Location = new Point(325, 190), ForeColor = Color.DimGray };
            reversalTimeoutMilliseconds.Text = "5000";
            reversalTimeoutMilliseconds.Location = new Point(389, 186);
            reversalTimeoutMilliseconds.Width = 54;
            Label reversalThresholdLabel = new Label { Text = "threshold", AutoSize = true, Location = new Point(451, 190), ForeColor = Color.DimGray };
            reversalProbabilityThreshold.Text = "0.50";
            reversalProbabilityThreshold.Location = new Point(509, 186);
            reversalProbabilityThreshold.Width = 48;

            Label thresholdsLabel = new Label { Text = "Paper thresholds", AutoSize = true, Location = new Point(18, 52) };
            Label longThresholdLabel = new Label { Text = "Long ≥", AutoSize = true, Location = new Point(122, 52) };
            paperLongThreshold.Text = "0.60";
            paperLongThreshold.Location = new Point(170, 48);
            paperLongThreshold.Width = 65;
            Label shortThresholdLabel = new Label { Text = "Short ≤", AutoSize = true, Location = new Point(250, 52) };
            paperShortThreshold.Text = "0.40";
            paperShortThreshold.Location = new Point(303, 48);
            paperShortThreshold.Width = 65;
            Label thresholdHelp = new Label { Text = "NO-TRADE in between.", AutoSize = true, Location = new Point(385, 52), ForeColor = Color.DimGray };
            tradeDirectionSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            tradeDirectionSelector.Items.Add("Both directions");
            tradeDirectionSelector.Items.Add("Longs only");
            tradeDirectionSelector.Items.Add("Shorts only");
            tradeDirectionSelector.SelectedIndex = 0;
            tradeDirectionSelector.Location = new Point(565, 16);
            tradeDirectionSelector.Width = 130;
            Tip(tradeDirectionSelector, "Only forward AI entries on this side (use when only one direction shows a positive per-side edge). Exits/flatten always work.");

            Button shadowLiveOnBtn = new Button { Text = "Shadow ON", Location = new Point(530, 90), Size = new Size(85, 24) };
            shadowLiveOnBtn.Click += delegate { SendShadowControl('F'); };
            Button shadowLiveOffBtn = new Button { Text = "Shadow OFF", Location = new Point(618, 90), Size = new Size(90, 24) };
            shadowLiveOffBtn.Click += delegate { SendShadowControl('T'); };
            Label shadowLiveHelp = new Label { Text = "Live: toggle cross-market/fusion without restart (v12). OFF = pure 222-feature thresholds.", AutoSize = true, Location = new Point(712, 94), ForeColor = Color.DimGray };

            Label signalModeLabel = new Label { Text = "Signal mode", AutoSize = true, Location = new Point(530, 52) };
            signalModeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            signalModeSelector.Items.Add("Both (shadow priority + thresholds)");
            signalModeSelector.Items.Add("Shadow fusion/reversal entries only");
            signalModeSelector.Items.Add("Probability thresholds only");
            signalModeSelector.Items.Add("Learned fusion (trained blend of all evidence)");
            signalModeSelector.SelectedIndex = 0;
            signalModeSelector.Location = new Point(605, 48);
            signalModeSelector.Width = 220;
            Label intervalLabel = new Label { Text = "Process ms", AutoSize = true, Location = new Point(625, 162) };
            processingIntervalMilliseconds.Text = "0";
            processingIntervalMilliseconds.Location = new Point(695, 158);
            processingIntervalMilliseconds.Width = 45;
            Label intervalHelp = new Label { Text = "0 = every frame; 5 if CPU limited", AutoSize = true, Location = new Point(748, 162), ForeColor = Color.DimGray };

            start.Text = "Start receiver + bridge";
            start.Location = new Point(18, 220);
            start.AutoSize = true;
            start.Click += StartClick;
            stop.Text = "Stop";
            stop.Location = new Point(188, 220);
            stop.AutoSize = true;
            stop.Enabled = false;
            stop.Click += StopClick;
            saveModelNow.Text = "Save model now";
            saveModelNow.Location = new Point(280, 220);
            saveModelNow.AutoSize = true;
            saveModelNow.Enabled = false;
            saveModelNow.Click += SaveModelNowClick;
            checkStream.Text = "Check stream health";
            checkStream.Location = new Point(420, 220);
            checkStream.AutoSize = true;
            checkStream.Click += CheckStreamHealthClick;
            startPaperSimulation.Text = "Start AI Trader";
            startPaperSimulation.Location = new Point(18, 90);
            startPaperSimulation.AutoSize = true;
            startPaperSimulation.Enabled = false;
            startPaperSimulation.Click += StartAiTraderClick;
            stopPaperSimulation.Text = "Stop AI Trader";
            stopPaperSimulation.Location = new Point(138, 90);
            stopPaperSimulation.AutoSize = true;
            stopPaperSimulation.Enabled = false;
            stopPaperSimulation.Click += StopAiTraderClick;
            loadHorizonStudy.Text = "Load horizon study...";
            loadHorizonStudy.Location = new Point(18, 50);
            loadHorizonStudy.AutoSize = true;
            loadHorizonStudy.Click += LoadHorizonStudyClick;
            runHistoricalStudy.Text = "Run historical study...";
            runHistoricalStudy.Location = new Point(175, 50);
            runHistoricalStudy.AutoSize = true;
            runHistoricalStudy.Click += RunHistoricalStudyClick;
            runMesAdapter.Text = "Run legacy MES test...";
            runMesAdapter.Location = new Point(345, 50);
            runMesAdapter.AutoSize = true;
            runMesAdapter.Click += RunMesAdapterClick;

            Label trainingProfileLabel = new Label { Text = "Python training", AutoSize = true, Location = new Point(18, 110) };
            trainingProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            trainingProfile.Items.Add("MultiTask LNN");
            trainingProfile.Items.Add("Tick-augmented MultiTask LNN");
            trainingProfile.Items.Add("Hidden Tick Depth CfC-v10");
            trainingProfile.Items.Add("Custom Python trainer");
            trainingProfile.SelectedIndex = 0;
            trainingProfile.Location = new Point(108, 106);
            trainingProfile.Width = 180;
            Label categoryLabel = new Label { Text = "Weight category", AutoSize = true, Location = new Point(300, 110) };
            weightCategory.Text = "MES-4t-120s";
            weightCategory.Location = new Point(390, 106);
            weightCategory.Width = 125;
            Label trainingHorizonLabel = new Label { Text = "Horizon s", AutoSize = true, Location = new Point(525, 110) };
            trainingHorizonSeconds.Text = "120";
            trainingHorizonSeconds.Location = new Point(585, 106);
            trainingHorizonSeconds.Width = 55;
            trainPythonProfile.Text = "Train profile...";
            trainPythonProfile.Location = new Point(655, 105);
            trainPythonProfile.AutoSize = true;
            trainPythonProfile.Click += TrainPythonProfileClick;
            Label trainingSymbolLabel = new Label { Text = "Symbol", AutoSize = true, Location = new Point(18, 140) };
            trainingSymbol.Text = "MES";
            trainingSymbol.Location = new Point(62, 136);
            trainingSymbol.Width = 92;
            Label trainingInstrumentLabel = new Label { Text = "DBN ID", AutoSize = true, Location = new Point(164, 140) };
            trainingInstrumentId.Text = "42003239";
            trainingInstrumentId.Location = new Point(211, 136);
            trainingInstrumentId.Width = 96;
            Label customTrainerFolderLabel = new Label { Text = "Trainer folder", AutoSize = true, Location = new Point(18, 175) };
            customTrainerFolder.Location = new Point(104, 171);
            customTrainerFolder.Width = 465;
            browseCustomTrainerFolder.Text = "Folder...";
            browseCustomTrainerFolder.Location = new Point(577, 170);
            browseCustomTrainerFolder.AutoSize = true;
            browseCustomTrainerFolder.Click += BrowseCustomTrainerFolderClick;
            Label customTrainerScriptLabel = new Label { Text = "Trainer script", AutoSize = true, Location = new Point(18, 205) };
            customTrainerScript.DropDownStyle = ComboBoxStyle.DropDownList;
            customTrainerScript.Location = new Point(104, 201);
            customTrainerScript.Width = 465;
            Label customTrainerHelp = new Label
            {
                Text = "Use Custom Python trainer to run the selected .py script with the dashboard training contract.",
                AutoSize = true,
                Location = new Point(18, 232),
                ForeColor = Color.DimGray
            };
            Label activeWeightRootLabel = new Label { Text = "Weights", AutoSize = true, Location = new Point(18, 270) };
            activeWeightRoot.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model_categories");
            activeWeightRoot.Location = new Point(68, 266);
            activeWeightRoot.Width = 235;
            browseActiveWeightRoot.Text = "Folder...";
            browseActiveWeightRoot.Location = new Point(310, 265);
            browseActiveWeightRoot.AutoSize = true;
            browseActiveWeightRoot.Click += BrowseActiveWeightRootClick;
            autoAdoptActiveWeights.Text = "Auto-adopt";
            autoAdoptActiveWeights.AutoSize = true;
            autoAdoptActiveWeights.Checked = true;
            autoAdoptActiveWeights.Location = new Point(390, 268);
            exportOnnxPackage.Text = "Export ONNX...";
            exportOnnxPackage.Location = new Point(500, 265);
            exportOnnxPackage.AutoSize = true;
            exportOnnxPackage.Click += ExportOnnxPackageClick;
            horizonStudyStatus.Text = "Historical study: not loaded";
            horizonStudyStatus.Location = new Point(18, 305);
            horizonStudyStatus.Size = new Size(840, 18);
            horizonStudyStatus.ForeColor = Color.DimGray;

            status.Text = "Stopped. NinjaTrader must be open with the matching bridge compiled.";
            status.Location = new Point(18, 284);
            status.Size = new Size(840, 16);
            flickerStatus.Text = "Order flicker: waiting for engine...";
            flickerStatus.Location = new Point(18, 301);
            flickerStatus.Size = new Size(840, 16);
            flickerStatus.ForeColor = Color.DarkSlateBlue;
            twoTickStatus.Text = "Skill meter: waiting for engine...";
            twoTickStatus.Location = new Point(18, 318);
            twoTickStatus.Size = new Size(840, 16);
            twoTickStatus.ForeColor = Color.DarkSlateBlue;
            twoTickStatus.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            status.ForeColor = Color.DimGray;

            hogFusionStatus.Text = "Hog fusion: off (enable 'Hog evidence → engine fusion head' on the Sim tab and start the hog).";
            hogFusionStatus.Location = new Point(18, 335);
            hogFusionStatus.Size = new Size(840, 16);
            hogFusionStatus.ForeColor = Color.DarkSlateBlue;

            TableLayoutPanel metrics = new TableLayoutPanel
            {
                Location = new Point(18, 353),
                Size = new Size(840, 260),
                ColumnCount = 2,
                RowCount = 6,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            for (int i = 0; i < 6; i++) metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            metrics.Controls.Add(trained, 0, 0);
            metrics.Controls.Add(noTrade, 1, 0);
            metrics.Controls.Add(upPredictionCount, 0, 1);
            metrics.Controls.Add(upPredictionRate, 1, 1);
            metrics.Controls.Add(downPredictionCount, 0, 2);
            metrics.Controls.Add(downPredictionRate, 1, 2);
            metrics.Controls.Add(accuracy, 0, 3);
            metrics.Controls.Add(majorityBaseline, 1, 3);
            metrics.Controls.Add(probability, 0, 4);
            metrics.Controls.Add(weightNorm, 1, 4);
            metrics.Controls.Add(bestValidation, 0, 5);
            metrics.Controls.Add(liveValidation, 1, 5);

            GroupBox latency = new GroupBox
            {
                Text = "Latency panel — rolling p50 / p95 / p99 (milliseconds)",
                Location = new Point(18, 725),
                Size = new Size(840, 132),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            latencyPanel.Multiline = true;
            latencyPanel.ReadOnly = true;
            latencyPanel.ScrollBars = ScrollBars.None;
            latencyPanel.WordWrap = false;
            latencyPanel.BackColor = Color.White;
            latencyPanel.Font = new Font("Consolas", 8.5F);
            latencyPanel.Location = new Point(12, 22);
            latencyPanel.Size = new Size(816, 98);
            latencyPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            latency.Controls.Add(latencyPanel);

            GroupBox outcomes = new GroupBox
            {
                Text = "Three forecast horizons — independent outcome labels and updates",
                Location = new Point(18, 865),
                Size = new Size(840, 112),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            outcomePanel.Multiline = true;
            outcomePanel.ReadOnly = true;
            outcomePanel.ScrollBars = ScrollBars.None;
            outcomePanel.WordWrap = false;
            outcomePanel.BackColor = Color.White;
            outcomePanel.Font = new Font("Consolas", 8.5F);
            outcomePanel.Location = new Point(12, 22);
            outcomePanel.Size = new Size(816, 78);
            outcomePanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            outcomes.Controls.Add(outcomePanel);

            Label note = new Label
            {
                Text = "Medium horizon drives the read-only paper signal: LONG at ≥0.60, SHORT at ≤0.40, only after 100 medium labels. The fast and slow heads are diagnostic. No order action exists.",
                Location = new Point(18, 985),
                Size = new Size(840, 32),
                ForeColor = Color.DimGray,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            GroupBox analyst = new GroupBox
            {
                Text = "LNN System Analyst — local feature and training explanation",
                Location = new Point(18, 1025),
                Size = new Size(840, 196),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            analystHistory.Multiline = true;
            analystHistory.ReadOnly = true;
            analystHistory.ScrollBars = ScrollBars.Vertical;
            analystHistory.WordWrap = true;
            analystHistory.BackColor = Color.White;
            analystHistory.Location = new Point(12, 22);
            analystHistory.Size = new Size(816, 126);
            analystHistory.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            analystHistory.Text = "Ask about the current prediction, accuracy, baseline, or why the last learning update changed weights. This is a local explanation of measured state, not an order or trade recommendation.";
            analystQuestion.Location = new Point(12, 158);
            analystQuestion.Size = new Size(690, 23);
            analystQuestion.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            analystQuestion.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                AskAnalyst();
            };
            askAnalyst.Text = "Ask";
            askAnalyst.Location = new Point(710, 157);
            askAnalyst.Size = new Size(118, 25);
            askAnalyst.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            askAnalyst.Click += delegate { AskAnalyst(); };
            analyst.Controls.AddRange(new Control[] { analystHistory, analystQuestion, askAnalyst });
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.WordWrap = false;
            output.BackColor = Color.White;
            output.Font = new Font("Consolas", 9F);
            output.Location = new Point(18, 1233);
            output.Size = new Size(840, 270);
            output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            CreateTradingHub();

            armedBanner.Dock = DockStyle.Top;
            armedBanner.Height = 26;
            armedBanner.TextAlign = ContentAlignment.MiddleCenter;
            armedBanner.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            armedBanner.BackColor = Color.DarkRed;
            armedBanner.ForeColor = Color.White;
            armedBanner.Text = "AI TRADER ARMED — live commands are being forwarded to NT8";
            armedBanner.Visible = false;

            TabControl modeTabs = new TabControl
            {
                Dock = DockStyle.Fill
            };
            TabPage liveLearningTab = liveTabPage;
            TabPage offlineLearningTab = new TabPage("Offline learning");
            liveLearningTab.AutoScroll = true;
            offlineLearningTab.AutoScroll = true;

            Label liveIntro = new Label
            {
                Text = "Live path: NinjaTrader bridge → selected engine → live metrics. Configure an external Python/Mojo runtime here only when the selected live engine needs one.",
                AutoSize = true,
                Location = new Point(18, 250),
                ForeColor = Color.DimGray
            };
            Label offlineIntro = new Label
            {
                Text = "Offline path: choose historical data in a study/training dialog. Training never connects to the NinjaTrader live bridge.",
                AutoSize = true,
                Location = new Point(18, 18),
                ForeColor = Color.DimGray
            };
            Label offlineRuntimeNote = new Label
            {
                Text = "Python runtime for offline studies/training is selected on the Live learning tab.",
                AutoSize = true,
                Location = new Point(18, 32),
                ForeColor = Color.DimGray
            };

            // Config controls laid out as auto-spacing rows (no hand-placed
            // pixel coordinates → labels and boxes can't overlap or clip).
            FlowLayoutPanel configStack = new FlowLayoutPanel
            {
                Location = new Point(12, 8),
                Size = new Size(1150, 240),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Padding = new Padding(0)
            };
            configStack.Controls.Add(ConfigRow(instrumentLabel, instrument, tickSizeLabel, tickSizeInput, engineLabel, engine, browseEngine));
            configStack.Controls.Add(ConfigRow(runtimeLabel, pythonExecutable, browsePython, costLabel, estimatedCostTicks));
            configStack.Controls.Add(ConfigRow(saveRawFrames, captureFolder, browseCapture, saveDashboardLog, saveWeights));
            configStack.Controls.Add(ConfigRow(learningRateLabel, learningRateInput, validationStrideLabel, validationStrideInput, restingDepthLabel, restingDepthInput, probSmoothLabel, probSmoothingInput));
            Label coalesceLabel = new Label { Text = "coalesce >", AutoSize = true, ForeColor = Color.DimGray };
            configStack.Controls.Add(ConfigRow(regimeRangeLabel, regimeMinRangeInput, regimeNoveltyLabel, regimeMaxNoveltyInput, emitPerEvent, coalesceLabel, coalesceAboveInput, domShift, saveFullWeightAudit));
            configStack.Controls.Add(ConfigRow(loadCheckpointLabel, loadCheckpointFile, browseLoadCheckpoint, recordDomEvents));
            Label stopLabel = new Label { Text = "Stop ticks", AutoSize = true, ForeColor = Color.DimGray };
            labelStopTicks.Text = "0"; labelStopTicks.Width = 48;
            Tip(labelStopTicks, "v64r. How far a trade may go AGAINST you before the label counts it as a loss, in ticks, net of spread and cost. 0 (or the same value as Target ticks) keeps the old symmetric label - 'which side reaches the target first'.\n" +
                "Set it to your actual stop and the model starts answering the question you are actually trading: with Target 8 and Stop 4, P(up) means 'does +8 arrive BEFORE -4'. A move that first retraces past the stop and only later reaches the target is scored as a LOSS, because that is what happens to a live order.\n" +
                "Both brackets are scored off the same entry; when both get stopped the example is skipped rather than labelled, so chop stops teaching the model a direction it could not have traded.\n" +
                "Reward:risk follows from the pair - 8 against 4 is 2:1, which needs better than a 33% hit rate to break even before fees. CHANGING THIS CHANGES WHAT THE MODEL PREDICTS: retrain from scratch, existing checkpoints were fitted to the old geometry. Applied at Start.");
            configStack.Controls.Add(ConfigRow(targetLabel, minimumProfitTargetTicks, stopLabel, labelStopTicks, deadlineLabel, minimumLabelDeadlineMilliseconds, deadlineDash, maximumLabelDeadlineMilliseconds, intervalLabel, processingIntervalMilliseconds, intervalHelp));
            reversalEnable.Text = "Reversal tab"; reversalEnable.AutoSize = true; reversalEnable.Checked = true;
            Tip(reversalEnable, "Run the reversal shadow tracker and feed the Reversal tab. Diagnostic only — never changes the live prediction or places orders. Applied at Start.");
            configStack.Controls.Add(ConfigRow(reversalLabel, reversalTriggerLabel, reversalTriggerTicks, reversalDistanceLabel, reversalDistanceTicks, reversalTrendLabel, reversalTrendTicks, reversalTimeoutLabel, reversalTimeoutMilliseconds, reversalThresholdLabel, reversalProbabilityThreshold, reversalEnable));
            gotoTradingHub.Text = "Open Trading Hub »"; gotoTradingHub.AutoSize = true;
            Tip(gotoTradingHub, "Switches to the Trading Hub tab. That tab has the manual order buttons - Buy Mkt / Sell Mkt / Buy Lmt / Sell Lmt / Buy Stop / Sell Stop / FLAT / Cancel All - plus the quantity box, the SL/PT/trail boxes and the fill log. They send through the same command ring the AI Trader uses, so a manual order is timed exactly like an automated one (ack, fill, event->callback, slippage all populate). Use them with the AI Trader DISARMED to take a latency baseline. They bypass the dashboard's own guards; the strategy's max position, orders/minute, daily loss, trading hours and SL/PT bracket still apply.");
            gotoTradingHub.Click += delegate
            {
                try
                {
                    if (modeTabsRef != null) modeTabsRef.SelectedTab = tradingHubTab;
                    connectTradeHub.Focus();
                }
                catch { }
            };
            configStack.Controls.Add(ConfigRow(start, stop, saveModelNow, checkStream, gotoTradingHub));

            liveLearningTab.Controls.AddRange(new Control[]
            {
                configStack,
                liveIntro, activeOnnxPackage, status, flickerStatus, twoTickStatus, hogFusionStatus, metrics, latency, outcomes, note, analyst, output
            });
            // v64l: the Trading Hub used to live at y=1520 on this tab, far below
            // the scroll floor most people ever reach, so the manual order buttons
            // were effectively invisible. It now owns its own tab.
            tradingHubTab.AutoScroll = true;
            tradingHubTab.Controls.Add(tradingHubIntro);
            tradingHubTab.Controls.Add(aiHub);
            tradingHubTab.Controls.Add(tradingHub);
            FlowLayoutPanel offlineStack = new FlowLayoutPanel
            {
                Location = new Point(12, 8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            offlineStack.Controls.Add(offlineIntro);
            offlineStack.Controls.Add(offlineRuntimeNote);
            offlineStack.Controls.Add(ConfigRow(loadHorizonStudy, runHistoricalStudy, runMesAdapter));
            offlineStack.Controls.Add(ConfigRow(trainingProfileLabel, trainingProfile, categoryLabel, weightCategory, trainingHorizonLabel, trainingHorizonSeconds, trainPythonProfile));
            offlineStack.Controls.Add(ConfigRow(trainingSymbolLabel, trainingSymbol, trainingInstrumentLabel, trainingInstrumentId));
            offlineStack.Controls.Add(ConfigRow(customTrainerFolderLabel, customTrainerFolder, browseCustomTrainerFolder));
            offlineStack.Controls.Add(ConfigRow(customTrainerScriptLabel, customTrainerScript));
            offlineStack.Controls.Add(customTrainerHelp);
            offlineStack.Controls.Add(ConfigRow(activeWeightRootLabel, activeWeightRoot, browseActiveWeightRoot, autoAdoptActiveWeights, exportOnnxPackage));
            offlineStack.Controls.Add(horizonStudyStatus);
            offlineLearningTab.Controls.Add(offlineStack);
            // ===== Sim trading tab =====
            TabPage simTradingTab = new TabPage("Sim trading");
            simTradingTab.AutoScroll = true;
            Label simIntro = new Label
            {
                Text = "AI paper simulation. When armed via Start AI Trader, LONG/SHORT/NO-TRADE signals are forwarded to the NT8 command MMF (Local\\LNN_ORDER_CMD_<symbol>) read by LnnOrderExecStrategy.",
                AutoSize = true,
                Location = new Point(18, 18),
                ForeColor = Color.DimGray
            };
            // AI order sizing (independent of the Trading Hub's manual Qty box)
            Label aiQtyLabel = new Label { Text = "AI qty", AutoSize = true, Location = new Point(280, 96) };
            aiQty.Text = "1";
            aiQty.Location = new Point(325, 92);
            aiQty.Width = 45;
            Label aiTrailLabel = new Label { Text = "Trail SL (ticks)", AutoSize = true, Location = new Point(385, 96) };
            aiTrailTicks.Location = new Point(470, 92);
            aiTrailTicks.Width = 45;
            Label aiTrailHelp = new Label { Text = "blank = strategy default", AutoSize = true, Location = new Point(525, 96), ForeColor = Color.DimGray };
            Label entryTypeLabel = new Label { Text = "Entry", AutoSize = true, Location = new Point(280, 68) };
            exitModeSelector.DropDownStyle = ComboBoxStyle.DropDownList; exitModeSelector.Width = 300;
            exitModeSelector.Items.Add("Exit: strategy bracket — SL / PT from the strategy's default ticks, no time limit");
            exitModeSelector.Items.Add("Exit: signal change (legacy — NO-TRADE flattens, no TP)");
            exitModeSelector.SelectedIndex = 0;
            Tip(exitModeSelector, "How a live trade ends.\nSTRATEGY BRACKET (v64k, default): the strategy owns the exits. Every AI entry - market, or a filled executioner post - gets the strategy's own bracket: stop at DefaultStopLossTicks, target at DefaultProfitTargetTicks (or trail at DefaultTrailTicks), as OCO orders in NT8. Set them in the NT8 strategy parameters or in the Strategy control row below (Apply). There is NO time limit and NO-TRADE does not flatten: the trade ends at its stop or target, and if the signal still says LONG/SHORT when it closes, a new one is entered (trade gap / cooldown / max trades still apply). The engine only decides entries. Arming is refused while the strategy reports neither a stop nor a target.\nSIGNAL CHANGE: the old rule - hold until the probability crosses back inside the thresholds (NO-TRADE -> flatten) or flips; no take-profit; only the strategy's default stop protects the position.");
            flipOnOpposite.Text = "Flip on opposite signal"; flipOnOpposite.AutoSize = true; flipOnOpposite.Checked = false;
            Tip(flipOnOpposite, "Strategy-bracket mode only. OFF: an opposite signal while a trade is open is ignored; the trade runs to its stop or target. ON: an opposite signal flattens and enters the other way immediately.");
            entryOrderTypeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            entryOrderTypeSelector.Items.Add("Market (pay spread, always fills)");
            entryOrderTypeSelector.Items.Add("Limit at touch (earn spread, may miss)");
            entryOrderTypeSelector.SelectedIndex = 0;
            entryOrderTypeSelector.Location = new Point(320, 64);
            entryOrderTypeSelector.Width = 210;
            Tip(entryOrderTypeSelector, "Market = cross the spread, guaranteed fill. Limit at touch = post at bid (buy) / ask (sell), earn the spread but the entry may not fill if price runs away.");
            Button diagnosticsBtn = new Button { Text = "Copy diagnostics", Location = new Point(680, 90), AutoSize = true };
            diagnosticsBtn.Click += CopyDiagnosticsClick;

            simTradeBridgeStatus.Text = "NT8 command MMF: not connected. Start AI Trader connects it automatically (or use Trading Hub Connect).";
            simTradeBridgeStatus.Location = new Point(18, 128);
            simTradeBridgeStatus.Size = new Size(840, 18);
            simTradeBridgeStatus.ForeColor = Color.DimGray;

            strategyAliveLabel.Text = "NT8 strategy activity: none observed yet.";
            strategyAliveLabel.Location = new Point(18, 148);
            strategyAliveLabel.Size = new Size(840, 18);
            strategyAliveLabel.ForeColor = Color.DimGray;
            tradePathStatus.AutoSize = true; tradePathStatus.MaximumSize = new Size(1140, 0);
            tradePathStatus.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            tradePathStatus.ForeColor = Color.FromArgb(120, 60, 0);
            tradePathStatus.Margin = new Padding(2, 2, 0, 6);
            tradePathStatus.Text = "Trade path: (arm the AI Trader or press Test trade path)";
            testPathBtn.Text = "Test trade path"; testPathBtn.AutoSize = true; testPathBtn.Click += TestTradePathClick;
            Tip(testPathBtn, "Sends a ping through the command ring. LnnOrderExecStrategy (v64k+) answers with a PONG message and an ack record, proving dashboard -> ring -> strategy -> ring -> dashboard end to end. The panel below shows where a signal stops: engine probability, AI Trader decision, command ring, strategy heartbeat (state, executioner mode, position), last ack.");

            // Safety guards
            Label lossLimitLabel = new Label { Text = "Loss limit (ticks)", AutoSize = true, Location = new Point(18, 262) };
            dailyLossLimitTicks.Text = "50";
            dailyLossLimitTicks.Location = new Point(118, 258);
            dailyLossLimitTicks.Width = 50;
            maxTradesPerSession.Text = "20";
            maxTradesPerSession.Location = new Point(258, 258);
            maxTradesPerSession.Width = 45;
            regimeMaxFlips.Text = "8";
            regimeMaxFlips.Location = new Point(430, 258);
            regimeMaxFlips.Width = 40;
            Label regimeHelp = new Label { Text = "regime guard: rapid flip-flopping or a dead-zone probability auto-disarms", AutoSize = true, Location = new Point(482, 262), ForeColor = Color.DimGray };
            soundAlerts.Text = "Sound alerts";
            soundAlerts.AutoSize = true;
            soundAlerts.Location = new Point(18, 288);

            // Signal timeline + paper P/L sparkline
            Label sparkLabel = new Label { Text = "Signal timeline (last hour) and paper net P/L history (ticks)", AutoSize = true, Location = new Point(18, 318) };
            signalStrip.Location = new Point(18, 338);
            signalStrip.Size = new Size(840, 20);
            signalStrip.BackColor = Color.White;
            signalStrip.BorderStyle = BorderStyle.FixedSingle;
            signalStrip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            signalStrip.Paint += PaintSignalStrip;
            signalStrip.Resize += delegate { signalStrip.Invalidate(); };
            pnlSpark.Location = new Point(18, 362);
            pnlSpark.Size = new Size(840, 96);
            pnlSpark.BackColor = Color.White;
            pnlSpark.BorderStyle = BorderStyle.FixedSingle;
            pnlSpark.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pnlSpark.Paint += PaintPnlSpark;
            pnlSpark.Resize += delegate { pnlSpark.Invalidate(); };

            // Trading-discipline row
            sessionStartInput.Location = new Point(70, 466); sessionStartInput.Width = 44;
            sessionEndInput.Location = new Point(128, 466); sessionEndInput.Width = 44;
            cooldownSecondsInput.Text = "60"; cooldownSecondsInput.Location = new Point(258, 466); cooldownSecondsInput.Width = 38;
            Label consecLabel = new Label { Text = "Max consec losses", AutoSize = true, Location = new Point(308, 470) };
            maxConsecutiveLossesInput.Text = "3"; maxConsecutiveLossesInput.Location = new Point(418, 466); maxConsecutiveLossesInput.Width = 30;
            Label holdLabel = new Label { Text = "Hold s", AutoSize = true, Location = new Point(462, 470) };
            signalHoldSecondsInput.Text = "0"; signalHoldSecondsInput.Location = new Point(505, 466); signalHoldSecondsInput.Width = 32;
            liveConfidenceMarginInput.Text = "0.00"; liveConfidenceMarginInput.Location = new Point(620, 466); liveConfidenceMarginInput.Width = 40;
            rampWinsPerStepInput.Text = "0"; rampWinsPerStepInput.Location = new Point(768, 466); rampWinsPerStepInput.Width = 32;
            minSecondsBetweenTradesInput.Text = "30";
            minSecondsBetweenTradesInput.Location = new Point(90, 490); minSecondsBetweenTradesInput.Width = 40;
            Label disciplineHelp = new Label
            {
                Text = "Trade gap: minimum seconds between entries. Session HH:mm local (blank = always). Hold: signal must persist. Live margin: extra probability for real entries. Ramp 0 = full AI qty.",
                AutoSize = true, Location = new Point(145, 494), ForeColor = Color.DimGray
            };

            tallyLabel.Text = "Session: no AI trades yet.";
            tallyLabel.Location = new Point(18, 514); tallyLabel.Size = new Size(840, 18); tallyLabel.ForeColor = Color.DimGray;
            positionLabel.Text = "AI position: flat.";
            positionLabel.Location = new Point(18, 534); positionLabel.Size = new Size(840, 18); positionLabel.ForeColor = Color.DimGray;
            divergenceLabel.Text = "Paper vs live divergence: waiting for trades.";
            divergenceLabel.Location = new Point(18, 554); divergenceLabel.Size = new Size(840, 18); divergenceLabel.ForeColor = Color.DimGray;
            paperOutcomeStatus.Text = "Paper signal vs real price: waiting for engine...";
            paperOutcomeStatus.Location = new Point(18, 250); paperOutcomeStatus.Size = new Size(840, 16);
            paperOutcomeStatus.ForeColor = Color.DarkSlateBlue; paperOutcomeStatus.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);

            // NT8 strategy remote controls (ops 20-24; require a connected hub)
            Label strategyCtlLabel = new Label { Text = "NT8 strategy:", AutoSize = true, Location = new Point(18, 750), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            Button pauseStrategyBtn = new Button { Text = "Pause", Location = new Point(105, 746), Size = new Size(60, 25) };
            pauseStrategyBtn.Click += delegate { SendSimpleTradeCommand(TradeCmdOp.PauseExecution); };
            Button resumeStrategyBtn = new Button { Text = "Resume", Location = new Point(170, 746), Size = new Size(65, 25) };
            resumeStrategyBtn.Click += delegate { SendSimpleTradeCommand(TradeCmdOp.ResumeExecution); };
            Label ntMaxPosLabel = new Label { Text = "Max pos", AutoSize = true, Location = new Point(250, 752) };
            ntMaxPosInput.Text = "2"; ntMaxPosInput.Location = new Point(303, 748); ntMaxPosInput.Width = 32;
            Label ntLossLabel = new Label { Text = "NT8 loss ticks", AutoSize = true, Location = new Point(347, 752) };
            ntLossLimitInput.Text = "0"; ntLossLimitInput.Location = new Point(430, 748); ntLossLimitInput.Width = 40;
            Label ntSlLabel = new Label { Text = "Def SL", AutoSize = true, Location = new Point(482, 752) };
            ntDefSlInput.Text = "0"; ntDefSlInput.Location = new Point(524, 748); ntDefSlInput.Width = 34;
            Label ntPtLabel = new Label { Text = "PT", AutoSize = true, Location = new Point(566, 752) };
            ntDefPtInput.Text = "0"; ntDefPtInput.Location = new Point(588, 748); ntDefPtInput.Width = 34;
            Label ntTrailLabel = new Label { Text = "Trail", AutoSize = true, Location = new Point(630, 752) };
            ntDefTrailInput.Text = "0"; ntDefTrailInput.Location = new Point(660, 748); ntDefTrailInput.Width = 34;
            Button applyStrategyBtn = new Button { Text = "Apply to NT8", Location = new Point(706, 746), AutoSize = true };
            applyStrategyBtn.Click += ApplyStrategySettingsClick;
            Label strategyCtlHelp = new Label
            {
                Text = "Pause blocks entries in NT8 (Flat/Cancel still work). Ticks: 0 = off. Apply sends max position, NT8-side daily loss limit, and default SL/PT/trail brackets to the running strategy.",
                AutoSize = true, Location = new Point(18, 776), ForeColor = Color.DimGray
            };

            latencyGateLabel.Text = "Entry gate: (disarmed) | Order latency: no commands yet.";
            latencyGateLabel.Location = new Point(18, 574); latencyGateLabel.Size = new Size(840, 18); latencyGateLabel.ForeColor = Color.DimGray;

            Label historyLabel = new Label { Text = "AI trade history (newest first)", AutoSize = true, Location = new Point(18, 596) };
            tradeHistoryBox.Multiline = true; tradeHistoryBox.ReadOnly = true;
            tradeHistoryBox.ScrollBars = ScrollBars.Vertical; tradeHistoryBox.WordWrap = false;
            tradeHistoryBox.BackColor = Color.White; tradeHistoryBox.Font = new Font("Consolas", 8.5F);
            tradeHistoryBox.Location = new Point(18, 616); tradeHistoryBox.Size = new Size(840, 122);
            tradeHistoryBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            tradeHistoryBox.Text = "No completed AI trades yet.";

            // Settings profiles
            Label profileLabel = new Label { Text = "Profile", AutoSize = true, Location = new Point(540, 262) };
            profileSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            profileSelector.Location = new Point(585, 258);
            profileSelector.Width = 140;
            profileSelector.SelectedIndexChanged += ProfileSelected;
            Button saveProfileBtn = new Button { Text = "Save profile...", Location = new Point(733, 256), AutoSize = true };
            saveProfileBtn.Click += SaveProfileClick;
            TableLayoutPanel simMetrics = new TableLayoutPanel
            {
                Location = new Point(18, 160),
                Size = new Size(840, 90),
                ColumnCount = 2,
                RowCount = 2,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            simMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            simMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            simMetrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            simMetrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            simMetrics.Controls.Add(paperSignal, 0, 0);
            simMetrics.Controls.Add(paperSignalCounts, 1, 0);
            simMetrics.Controls.Add(paperTradeSummary, 0, 1);
            simMetrics.Controls.Add(paperTradePnl, 1, 1);
            // Whole Sim tab rebuilt as a top-down flow of auto-spacing rows so
            // no label, box, table or graph can overlap another (was hand-placed
            // pixel coordinates with several collisions).
            simMetrics.Width = 1140; signalStrip.Width = 1140; pnlSpark.Width = 1140; tradeHistoryBox.Width = 1140;
            simMetrics.Margin = new Padding(2, 4, 0, 6);
            signalStrip.Margin = new Padding(2, 2, 0, 6);
            pnlSpark.Margin = new Padding(2, 0, 0, 8);
            tradeHistoryBox.Margin = new Padding(2, 2, 0, 8);
            paperOutcomeStatus.AutoSize = true;
            FlowLayoutPanel simStack = new FlowLayoutPanel
            {
                Location = new Point(12, 8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            simStack.Controls.Add(simIntro);
            simStack.Controls.Add(ConfigRow(thresholdsLabel, longThresholdLabel, paperLongThreshold, shortThresholdLabel, paperShortThreshold, thresholdHelp));
            simStack.Controls.Add(ConfigRow(signalModeLabel, signalModeSelector));   // v65b: direction filter moved to the Trading Hub gate panel
            simStack.Controls.Add(ConfigRow(entryTypeLabel, entryOrderTypeSelector, aiQtyLabel, aiQty, aiTrailLabel, aiTrailTicks, aiTrailHelp));
            simStack.Controls.Add(ConfigRow(exitModeSelector, flipOnOpposite));
            simStack.Controls.Add(ConfigRow(startPaperSimulation, stopPaperSimulation, diagnosticsBtn, testPathBtn, shadowLiveOnBtn, shadowLiveOffBtn));
            simStack.Controls.Add(shadowLiveHelp);
            simStack.Controls.Add(simTradeBridgeStatus);
            simStack.Controls.Add(strategyAliveLabel);
            simStack.Controls.Add(tradePathStatus);
            simStack.Controls.Add(simMetrics);
            simStack.Controls.Add(paperOutcomeStatus);
            simStack.Controls.Add(ConfigRow(lossLimitLabel, dailyLossLimitTicks, profileLabel, profileSelector, saveProfileBtn));   // v65b: max trades / flip guard moved to the gate panel
            simStack.Controls.Add(regimeHelp);
            simStack.Controls.Add(ConfigRow(soundAlerts));
            simStack.Controls.Add(ConfigRow(consecLabel, maxConsecutiveLossesInput, holdLabel, signalHoldSecondsInput));   // v65b: the rest of this row moved to the Trading Hub gate panel
            simStack.Controls.Add(disciplineHelp);
            simStack.Controls.Add(tallyLabel);
            simStack.Controls.Add(positionLabel);
            simStack.Controls.Add(divergenceLabel);
            simStack.Controls.Add(latencyGateLabel);
            simStack.Controls.Add(sparkLabel);
            simStack.Controls.Add(signalStrip);
            simStack.Controls.Add(pnlSpark);
            simStack.Controls.Add(historyLabel);
            simStack.Controls.Add(tradeHistoryBox);

            // ===== Limit executioner =====
            Label execHeader = new Label { Text = "Limit executioner (buy/sell join, managed) — routes AI join-entries through NT8's book-aware poster", AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = Color.DarkSlateBlue };
            execEnable.Text = "Enable executioner"; execEnable.AutoSize = true; execEnable.Checked = false;
            Label execModeLabel = new Label { Text = "Mode", AutoSize = true };
            execModeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            execModeSelector.Items.Add("Measure only (simulate, no orders)");
            execModeSelector.Items.Add("Live (place & manage real orders)");
            execModeSelector.SelectedIndex = 0; execModeSelector.Width = 240;
            Tip(execModeSelector, "Measure = run the whole post/cancel/markout loop as a simulation and score fill quality without placing an order. Live = place and manage real limit orders. Prove positive net edge in Measure before switching to Live.");
            Button applyExecBtn = new Button { Text = "Apply executioner", AutoSize = true };
            applyExecBtn.Click += ApplyExecutionerSettings;
            Label exGatesLabel = new Label { Text = "Post gates:", AutoSize = true, ForeColor = Color.DimGray };
            Label exImbLabel = new Label { Text = "Min imbalance", AutoSize = true };
            exImbalanceInput.Text = "0.00"; exImbalanceInput.Width = 46;
            Tip(exImbalanceInput, "Only post a buy when (bidSize-askSize)/(sum) at the touch ≥ this (a wall under you). 0 = neutral book OK. Try 0.10-0.30.");
            Label exRestLabel = new Label { Text = "Resting ms", AutoSize = true };
            exRestingMsInput.Text = "200"; exRestingMsInput.Width = 46;
            Tip(exRestingMsInput, "Touch size must hold this long before it's trusted (ignores phantom/spoof size). Try 200-400.");
            Label exFlickLabel = new Label { Text = "Max flicker/s", AutoSize = true };
            exFlickerInput.Text = "0"; exFlickerInput.Width = 40;
            Tip(exFlickerInput, "Max touch add/cancel churn per second to post. 0 = off. High churn = unstable level.");
            exMicropriceGate.Text = "Microprice gate"; exMicropriceGate.AutoSize = true; exMicropriceGate.Checked = true;
            Tip(exMicropriceGate, "Require the size-weighted microprice on your side of the mid before posting (don't rest a buy while pressure is down).");
            Label exMgmtLabel = new Label { Text = "Management:", AutoSize = true, ForeColor = Color.DimGray };
            Label exAggrLabel = new Label { Text = "Cancel on aggr qty", AutoSize = true };
            exAggrQtyInput.Text = "15"; exAggrQtyInput.Width = 40;
            Tip(exAggrQtyInput, "If this many contracts hit your side within the window AND the queue is thinning, pull the order — you're about to be filled into a move against you. 0 = off.");
            Label exWinLabel = new Label { Text = "in ms", AutoSize = true };
            exAggrWindowInput.Text = "500"; exAggrWindowInput.Width = 44;
            Label exChaseLabel = new Label { Text = "Max chase ticks", AutoSize = true };
            exChaseInput.Text = "1"; exChaseInput.Width = 34;
            Tip(exChaseInput, "How far the order will repeg after the market before giving up. 0 = never chase.");
            Label exTimeoutLabel = new Label { Text = "Timeout ms", AutoSize = true };
            exTimeoutInput.Text = "8000"; exTimeoutInput.Width = 50;
            Label exOnTimeoutLabel = new Label { Text = "on timeout", AutoSize = true };
            exOnTimeoutSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            exOnTimeoutSelector.Items.Add("Abandon"); exOnTimeoutSelector.Items.Add("Cross (market)");
            exOnTimeoutSelector.SelectedIndex = 0; exOnTimeoutSelector.Width = 120;
            Label exMkLabel = new Label { Text = "Markout ms", AutoSize = true };
            exMark1Input.Text = "1000"; exMark1Input.Width = 50;
            Label exMkDash = new Label { Text = "/", AutoSize = true };
            exMark2Input.Text = "5000"; exMark2Input.Width = 50;
            Label exPolicyLabel = new Label { Text = "Policy", AutoSize = true };
            exPolicySelector.DropDownStyle = ComboBoxStyle.DropDownList; exPolicySelector.Items.AddRange(new object[] { "Gates (imbalance/resting/flicker/microprice)", "EV (fill model + P(up))" }); exPolicySelector.SelectedIndex = 0; exPolicySelector.Width = 250;
            Tip(exPolicySelector, "Gates: post only when the book gates pass (current behaviour). EV: post/hold/cross by expected value — P(fill within the remaining time) from the fitted hazard model (exact fill times) x (fill markout at the exit touch + signal edge) vs crossing now (cross markout at the exit touch + edge). Everything touch-to-touch, no mid flattery. Needs FillModelFile set on the strategy (fit it with the button below). Applied on Apply (op 45).");
            Label exMoveLabel = new Label { Text = "expected move t", AutoSize = true };
            exExpectedMoveInput.Text = "2"; exExpectedMoveInput.Width = 36;
            Tip(exExpectedMoveInput, "Edge term for the EV policy: edge = (2 x P(up) - 1) x this many ticks. Use your typical target (1-3 t).");
            fitFillModelButton.Text = "Fit fill model from latest DOM capture"; fitFillModelButton.AutoSize = true;
            Tip(fitFillModelButton, "Runs LnnExecBacktest --fit-fill-model on the newest *_dom_events_*.bin in the capture folder (k from the box) and writes <capture folder>\\fill_model.txt. Point the strategy's FillModelFile parameter at that file; Apply with Qty=1 reload (the Apply button does this) picks up a refit without restarting.");
            fitFillModelButton.Click += delegate { FitFillModel(); };
            Label exQueueLabel = new Label { Text = "queue cancel-ahead k", AutoSize = true };
            exCancelAheadInput.Text = "0.5"; exCancelAheadInput.Width = 40;
            Tip(exCancelAheadInput, "Queue-position estimator (there is no market-by-order in the feed). When size at your price drops with no print, that was a cancel; k x ahead/(ahead+behind) of it is assumed to have been ahead of you. 0 = legacy (only trades consume the queue; pessimistic), 1 = uniform cancels (optimistic for a fresh post), 0.5 = default. Measure-mode fills trigger when estimated ahead-of-you reaches zero. Sweep it in the backtester (--cancel-ahead) and calibrate against real fills.");
            Tip(exMark1Input, "Fill-quality is scored by where price is this long after each fill. Negative markout = adverse selection.");
            execQualityPanel.Multiline = true; execQualityPanel.ReadOnly = true; execQualityPanel.WordWrap = false;
            execQualityPanel.ScrollBars = ScrollBars.Vertical; execQualityPanel.BackColor = Color.White;
            execQualityPanel.Font = new Font("Consolas", 8.5F); execQualityPanel.Width = 1140; execQualityPanel.Height = 96;
            execQualityPanel.Margin = new Padding(2, 4, 0, 8);
            execQualityPanel.Text = "Fill-quality (executioner): no samples yet. Enable in Measure mode and Apply; each simulated fill scores effective spread + markout here.";
            learnedCostPanel.Multiline = true; learnedCostPanel.ReadOnly = true; learnedCostPanel.WordWrap = false;
            learnedCostPanel.ScrollBars = ScrollBars.Vertical; learnedCostPanel.BackColor = Color.White;
            learnedCostPanel.Font = new Font("Consolas", 8.5F); learnedCostPanel.Width = 1140; learnedCostPanel.Height = 74;
            learnedCostPanel.Margin = new Padding(2, 2, 0, 8);
            learnedCostPanel.Text = "Learned execution cost: no real market fills yet. Route market entries live and this fills in with measured slippage, latency, and a suggested paper cost.";

            engineDirectOrders.Text = "Engine writes orders directly (lowest latency)"; engineDirectOrders.AutoSize = true; engineDirectOrders.Checked = false;
            Tip(engineDirectOrders, "The engine writes trade commands straight to the NT8 command ring, skipping the dashboard forward hop. Applied at Start (engine restart required to change). While ON, the dashboard's own AI-trader forwarding is suppressed so orders can't double-fire.");
            Label engineOrderModeLabel = new Label { Text = "Order path", AutoSize = true };
            engineOrderModeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            engineOrderModeSelector.Items.Add("Market — fastest fill, pays spread");
            engineOrderModeSelector.Items.Add("Join via executioner — best price");
            engineOrderModeSelector.SelectedIndex = 0; engineOrderModeSelector.Width = 260;
            Tip(engineOrderModeSelector, "Market = op 0/1, immediate fill crossing the spread (fastest). Join = op 30/31, routed through the book-aware executioner for a better price but may wait in queue or miss. Uses qty / trade-gap / hold from the fields above.");
            Label engineDirectHelp = new Label { Text = "applied at Start; engine restart to change", AutoSize = true, ForeColor = Color.DimGray };
            paperNoTimeout.Text = "Paper exit: target/stop only (no time limit)"; paperNoTimeout.AutoSize = true; paperNoTimeout.Checked = true;
            Tip(paperNoTimeout, "ON: a paper trade closes only when it hits its profit target or stop — never on the label-horizon deadline. Makes the win-rate a clean ±target outcome. OFF: legacy behavior (trades also close at the horizon deadline). Applied at Start.");
            paperMirror.Text = "Paper mirrors live (1:1)"; paperMirror.AutoSize = true; paperMirror.Checked = true;
            Tip(paperMirror, "ON (default): the paper sim takes exactly the trade a live order would take. The AI Trader runs its whole decision chain - hog gate, session window, signal hold, regime guard, max trades, trade gap, post-loss cooldown, live margin, direction filter, one-trade-at-a-time - and only then tells the engine to open a paper trade, at the touch after the paper entry latency. The paper trade then exits on the SAME bracket the strategy uses (NT default stop / target / trail ticks from the Strategy control row below), with no time limit. Paper runs whether or not the AI Trader is armed, so you can measure the exact live strategy without risking money; when it IS armed both fire from the same decision.\nOFF (legacy): the paper sim drives itself - it opens a trade on every LONG/SHORT signal the engine emits, ignores every AI Trader guard, and exits at +/- Target ticks. Useful for judging raw signal quality, not for predicting live P&L.\nApplied at Start. Note paper subtracts your cost ticks while the live tally is gross from fills.");
            Label paperNoTimeoutHelp = new Label { Text = "applied at Start; does not affect training labels", AutoSize = true, ForeColor = Color.DimGray };
            neutralBlockSecondsInput.Text = "120"; neutralBlockSecondsInput.Width = 44;
            Tip(neutralBlockSecondsInput, "v64x. Seconds the model may sit in the 0.45-0.55 dead zone before ENTRIES are blocked. It stays ARMED and open positions keep their bracket - only new entries stop. Entries resume once the probability holds outside the dead zone for 3 s, so a single spike out of a flat regime does not get traded.\n" +
                "This replaced an auto-disarm that counted 120 READINGS. Because the 50 ms probability line feeds the same counter, that was about six seconds of an undecided model, which is why a freshly retrained model kept switching itself off.\n" +
                "0 = gate off. Applied live, not at Start.");
            Label paperMaxOpenLabel = new Label { Text = "Max open paper trades", AutoSize = true };
            paperMaxOpenInput.Text = "1"; paperMaxOpenInput.Width = 40;
            Tip(paperMaxOpenInput, "How many paper trades can be open at once. 1 = one-at-a-time (each signal waits for the slot). Higher = the sim takes every signal concurrently, so the win-rate sample fills faster. Applied at Start.");
            Label paperLatencyLabel = new Label { Text = "Paper entry latency ms", AutoSize = true };
            paperEntryLatencyInput.Text = "0"; paperEntryLatencyInput.Width = 44;
            Tip(paperEntryLatencyInput, "Models the signal→fill delay in the paper sim: the entry fills at the touch this many ms AFTER the signal, not instantly. Set it to your measured fill p50 (Sim tab latency line) so the paper win-rate reflects real reaction lag. 0 = instant (legacy). Applied at Start.");
            Label labelLatencyLabel = new Label { Text = "Learner label latency ms", AutoSize = true };
            labelLatencyInput.Text = "0"; labelLatencyInput.Width = 44;
            Tip(labelLatencyInput, "Round-trip latency the LEARNER assumes. Every training label, the 2-tick skill meter and the paper-outcome scorer measure from the touch/mid this many ms AFTER the signal — the price an order could actually get — instead of at the signal itself. Makes the model learn what is still capturable after your lag, and makes SKILL honest w.r.t. execution. Set to your measured round trip (~50 ms); usually the same value as paper entry latency. 0 = legacy (assumes instant execution). Applied at Start.");
            Label labelLatencyHelp = new Label { Text = "(learner labels; paper latency = sim fills only)", AutoSize = true, ForeColor = Color.DimGray };
            pinEngineCore0.Text = "Pin engine to core 0"; pinEngineCore0.AutoSize = true; pinEngineCore0.Checked = true;
            Tip(pinEngineCore0, "Starts the LNN engine with --affinity-core 0 so the trading path (engine + executioner thread in NT8, see the strategy's PinExecutionerCore) keeps the first core, while the Nested hog runs on the other cores. Applied at Start.");

            simStack.Controls.Add(execHeader);
            simStack.Controls.Add(ConfigRow(execEnable, execModeLabel, execModeSelector, applyExecBtn));
            mmfGates.Text = "Enforce entry gates inside NT8 (works with engine-direct)"; mmfGates.AutoSize = true; mmfGates.Checked = false;
            Tip(mmfGates, "v64y. Sends every entry gate to the strategy over the command ring (ops 25/26), where they are applied in SubmitEntryGated - the one point EVERY entry passes through.\n" +
                "That is the only way these rules can cover engine-direct orders: those are written to the ring by the engine and the dashboard never sees them, so its own guards cannot run.\n" +
                "Covers: session window, dead-regime, confidence margin, direction filter, max trades per session, trade gap, post-loss cooldown, flip guard, size ramp.\n" +
                "The gates that already lived in NT8 (max position, orders/minute, daily loss, trading hours) are unaffected and always apply.\n" +
                "Exits, flattens and cancels are never gated. OFF = exactly the old behaviour. Requires the v64y strategy compiled in NT8; older builds ignore ops 25/26 and log 'unknown op'.");
            simStack.Controls.Add(ConfigRow(engineDirectOrders, engineOrderModeLabel, engineOrderModeSelector, engineDirectHelp));
            simStack.Controls.Add(ConfigRow(mmfGates));

            // v65a: book-quality and health gates. Every field is 0 = off by default.
            foreach (TextBox z in new[] { maxSpreadTicksInput, minImbalanceInput, maxFlickerEntryInput,
                                          maxAggrIntoInput, minBookSizeInput, staleSignalMsInput,
                                          feedStaleMsInput, giveBackPercentInput })
            { z.Text = "0"; z.Width = 44; }
            micropriceEntryGate.Text = "Microprice"; micropriceEntryGate.AutoSize = true; micropriceEntryGate.Checked = false;
            Tip(maxSpreadTicksInput, "v65a. Refuse a market entry when the bid/ask spread is wider than this many ticks. 0 = off.\nYou pay the spread on every market order and nothing checked it before this. On a 8t target a 2t spread is a quarter of the move.");
            Tip(minImbalanceInput, "v65a. Require the book to lean the way you are entering: (bidSize-askSize)/(bidSize+askSize) >= this for a long, and the mirror for a short. 0 = off.\nSame number the executioner uses to decide where to post - this applies it to whether to trade at all.");
            Tip(micropriceEntryGate, "v65a. Require the size-weighted microprice to sit on your side of the mid. It is the cheapest read on which way the touch is about to move.");
            Tip(maxFlickerEntryInput, "v65a. Block entries when the touch changes more than this many times per second. 0 = off.\nA flickering touch means the price you are about to pay is not the price you see.");
            Tip(maxAggrIntoInput, "v65a. Block when this much aggressive volume has already traded on the side you are about to take, inside the executioner's aggression window. 0 = off.\nThis is the price running away from you - you would be the last one in.");
            Tip(minBookSizeInput, "v65a. Require at least this much size on the side you would take (ask for a buy). 0 = off.\nThin book means slippage past the touch.");
            Tip(staleSignalMsInput, "v65a. Reject an entry whose signal context (op 46, the engine's P(up) for this trade) is older than this. 0 = off.\nCatches the case where the engine has stalled but a queued order still arrives.");
            Tip(feedStaleMsInput, "v65a. Block entries when no market data has arrived for this long. 0 = off.\nTrading on a dead feed is trading blind. This also surfaces the problem where the strategy never reaches Realtime - no market events means no data stamps.");
            Tip(giveBackPercentInput, "v65a. Once the day is up, stop entering after handing back this percentage of the day's PEAK realized ticks. 0 = off.\nExample: peak +100t, set 30 -> entries stop if the day falls to +70t. Measured on the strategy's own realized ticks, so it covers engine-direct trades.\nDeliberately NOT reset by ARM - only by the calendar day - so re-arming cannot be used to trade through it.");
            micropriceEntryGate.CheckedChanged += GateFieldChanged;
            foreach (TextBox z in new[] { maxSpreadTicksInput, minImbalanceInput, maxFlickerEntryInput,
                                          maxAggrIntoInput, minBookSizeInput, staleSignalMsInput,
                                          feedStaleMsInput, giveBackPercentInput })
                z.TextChanged += GateFieldChanged;
            // v64y: any gate field change re-pushes the config (debounced in FlushGateConfigIfDirty).
            mmfGates.CheckedChanged += GateFieldChanged;
            flipOnOpposite.CheckedChanged += GateFieldChanged;
            exitModeSelector.SelectedIndexChanged += GateFieldChanged;
            tradeDirectionSelector.SelectedIndexChanged += GateFieldChanged;
            foreach (TextBox gateBox in new[] { sessionStartInput, sessionEndInput, cooldownSecondsInput,
                                                liveConfidenceMarginInput, rampWinsPerStepInput, minSecondsBetweenTradesInput,
                                                maxTradesPerSession, regimeMaxFlips, neutralBlockSecondsInput,
                                                paperLongThreshold, paperShortThreshold })
                gateBox.TextChanged += GateFieldChanged;
            BuildEntryGatePanel();   // v65b: every gate, with its own on/off, on the Trading Hub tab
            simStack.Controls.Add(ConfigRow(paperMirror, paperNoTimeout, paperNoTimeoutHelp));
            simStack.Controls.Add(ConfigRow(paperMaxOpenLabel, paperMaxOpenInput, paperLatencyLabel, paperEntryLatencyInput, labelLatencyLabel, labelLatencyInput, labelLatencyHelp));   // v65b: dead-regime moved to the gate panel
            simStack.Controls.Add(ConfigRow(pinEngineCore0));
            hogEvidenceToEngine.Text = "Hog evidence → engine fusion head"; hogEvidenceToEngine.AutoSize = true; hogEvidenceToEngine.Checked = false;
            Tip(hogEvidenceToEngine, "The Nested hog publishes its latest P(up)/meta/agreement into a shared-memory record; the engine reads it every frame and trains a SEPARATE hog-fusion head from the same labels. Shadow = scored only (engine probability untouched). Live = the fused probability drives the paper signal. Applied at engine Start; start the hog too.");
            Label hogFusionModeLabel = new Label { Text = "Fusion mode", AutoSize = true };
            hogFusionModeSelector.DropDownStyle = ComboBoxStyle.DropDownList; hogFusionModeSelector.Items.AddRange(new object[] { "Shadow (score only)", "Live (fused probability)" }); hogFusionModeSelector.SelectedIndex = 0; hogFusionModeSelector.Width = 180;
            hogAgreeGate.Text = "Hog agreement gate for entries (engine-direct + dashboard forwarding)"; hogAgreeGate.AutoSize = true; hogAgreeGate.Checked = false;
            Tip(hogAgreeGate, "ENTRY orders require fresh hog evidence (<2 s) pointing the same way as the engine. Flattens are never gated. Off by default — turn on only after the hog-fusion line shows the hog is complementary (acc-when-agree > acc-when-disagree).");
            simStack.Controls.Add(ConfigRow(hogEvidenceToEngine, hogFusionModeLabel, hogFusionModeSelector));
            simStack.Controls.Add(ConfigRow(hogAgreeGate));
            runExecBacktest.Text = "Run executioner backtest on latest DOM capture"; runExecBacktest.AutoSize = true;
            Tip(runExecBacktest, "Runs LnnExecBacktest.exe (next to the engine exe) on the newest *_dom_events_*.bin in the capture folder with the sweep below; results stream into the log and a CSV lands beside the capture. Uses the CURRENT executioner values as the sweep centre. Signals are synthetic (every 5 s, alternating) unless a *_signals.csv with the same stem exists.");
            Label sweepLabel = new Label { Text = "Sweep args", AutoSize = true };
            backtestSweepInput.Width = 520; backtestSweepInput.Text = "--imbalance 0,0.1,0.2 --resting-ms 0,100,200,500 --flicker 0,20,50 --microprice 0,1 --chase 0,1,2 --timeout-ms 3000,8000 --on-timeout 0,1 --cancel-ahead 0,0.5,1";
            runExecBacktest.Click += delegate { RunExecBacktest(); };
            simStack.Controls.Add(ConfigRow(runExecBacktest, sweepLabel, backtestSweepInput));
            simStack.Controls.Add(ConfigRow(exGatesLabel, exImbLabel, exImbalanceInput, exRestLabel, exRestingMsInput, exFlickLabel, exFlickerInput, exMicropriceGate));
            simStack.Controls.Add(ConfigRow(exMgmtLabel, exAggrLabel, exAggrQtyInput, exWinLabel, exAggrWindowInput, exChaseLabel, exChaseInput, exTimeoutLabel, exTimeoutInput, exOnTimeoutLabel, exOnTimeoutSelector, exMkLabel, exMark1Input, exMkDash, exMark2Input, exQueueLabel, exCancelAheadInput));
            simStack.Controls.Add(ConfigRow(exPolicyLabel, exPolicySelector, exMoveLabel, exExpectedMoveInput, fitFillModelButton));
            simStack.Controls.Add(execQualityPanel);
            simStack.Controls.Add(learnedCostPanel);

            simStack.Controls.Add(ConfigRow(strategyCtlLabel, pauseStrategyBtn, resumeStrategyBtn, ntMaxPosLabel, ntMaxPosInput, ntLossLabel, ntLossLimitInput, ntSlLabel, ntDefSlInput, ntPtLabel, ntDefPtInput, ntTrailLabel, ntDefTrailInput, applyStrategyBtn));
            simStack.Controls.Add(strategyCtlHelp);
            Label strategyMsgLabel = new Label { Text = "Strategy messages (from NT8 — enable/gates/rejections/posts)", AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = Color.DarkSlateBlue };
            strategyMsgBox.Multiline = true; strategyMsgBox.ReadOnly = true; strategyMsgBox.WordWrap = false;
            strategyMsgBox.ScrollBars = ScrollBars.Vertical; strategyMsgBox.BackColor = Color.White;
            strategyMsgBox.Font = new Font("Consolas", 8.5F); strategyMsgBox.Width = 1140; strategyMsgBox.Height = 150;
            strategyMsgBox.Margin = new Padding(2, 2, 0, 8);
            strategyMsgBox.Text = strategyMsgPlaceholder;
            simStack.Controls.Add(strategyMsgLabel);
            simStack.Controls.Add(strategyMsgBox);
            simTradingTab.Controls.Add(simStack);

            TabPage modelRegistryTab = CreateModelRegistryTab();
            TabPage learningMonitorTab = CreateLearningMonitorTab();
            TabPage shadowTeacherTab = CreateShadowTeacherTab();
            TabPage temporalObserverTab = CreateTemporalObserverTab();
            // Lean live build: Temporal observer, Offline learning and Model
            // registry tabs are constructed (their controls are referenced by
            // telemetry/args) but not shown.
            TabPage reversalTab = CreateReversalTab();
            TabPage hogTab = CreateHogTab();
            modeTabsRef = modeTabs;
            modeTabs.TabPages.Add(liveLearningTab);
            modeTabs.TabPages.Add(tradingHubTab);
            modeTabs.TabPages.Add(simTradingTab);
            modeTabs.TabPages.Add(learningMonitorTab);
            modeTabs.TabPages.Add(shadowTeacherTab);
            modeTabs.TabPages.Add(reversalTab);
            modeTabs.TabPages.Add(hogTab);
            GC.KeepAlive(temporalObserverTab); GC.KeepAlive(offlineLearningTab); GC.KeepAlive(modelRegistryTab);
            Controls.Add(modeTabs);
            Controls.Add(armedBanner);
            modeTabs.BringToFront(); // dock order: banner takes the top strip, tabs fill the rest
            LoadPersistedNumberInputs();
            ApplyDashboardClockHygiene();
            LoadUiState();
            CheckForOrphanedEngines();
            RefreshProfileList();
            ConfigureInputPersistence();
            ConfigureHubWatchdog();
            FormClosing += DashboardFormClosing;
            // Force every tab's controls to build their window handles up front.
            // Otherwise a control on a never-opened tab has no handle, and the
            // per-second telemetry update that touches it throws — silently
            // aborting the whole update (including the Live-learning metrics)
            // until the user happens to open that tab. Realizing them here makes
            // the metrics update from the first frame, no tab visit required.
            Load += delegate
            {
                TabControl tabs = null;
                foreach (Control c in Controls) { tabs = c as TabControl; if (tabs != null) break; }
                if (tabs == null) return;
                int previous = tabs.SelectedIndex;
                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    try { tabs.SelectedIndex = i; tabs.TabPages[i].CreateControl(); RealizeChildren(tabs.TabPages[i]); } catch { }
                }
                try { tabs.SelectedIndex = previous < 0 ? 0 : previous; } catch { }
            };
        }

        // ====================================================================
        // Trading Hub — UI creation
        // ====================================================================

        private void CreateTradingHub()
        {
            tradingHubIntro.Text = "Manual order desk. Every button here goes out over the same MMF command ring the AI Trader uses, so a manual order is timed exactly like an automated one. Connect first, then start LnnOrderExecStrategy on a NinjaTrader chart with the matching instrument.";
            tradingHubIntro.Location = new Point(14, 10);
            tradingHubIntro.Size = new Size(880, 34);
            tradingHubIntro.ForeColor = Color.DimGray;

            // ---- AI Trader strip (v64m) ----
            aiHub.Text = "AI Trader — live";
            aiHub.Location = new Point(12, 48);
            aiHub.Size = new Size(880, 122);
            aiHub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            hubAiState.Text = "Disarmed.";
            hubAiState.Location = new Point(12, 24);
            hubAiState.Size = new Size(640, 20);
            hubAiState.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            hubAiState.ForeColor = Color.DimGray;

            hubArmBtn.Text = "ARM";
            hubArmBtn.Location = new Point(672, 20);
            hubArmBtn.Size = new Size(90, 28);
            hubArmBtn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            hubArmBtn.Enabled = false;
            hubArmBtn.Click += StartAiTraderClick;

            hubDisarmBtn.Text = "Disarm";
            hubDisarmBtn.Location = new Point(768, 20);
            hubDisarmBtn.Size = new Size(100, 28);
            hubDisarmBtn.Enabled = false;
            hubDisarmBtn.Click += StopAiTraderClick;

            hubAiGate.Text = "Entry gate: disarmed";
            hubAiGate.Location = new Point(12, 52);
            hubAiGate.Size = new Size(856, 18);
            hubAiGate.ForeColor = Color.DimGray;

            hubAiLast.Text = "Last decision: none yet";
            hubAiLast.Location = new Point(12, 74);
            hubAiLast.Size = new Size(856, 18);
            hubAiLast.ForeColor = Color.DimGray;

            hubAiTally.Text = "Session: 0 trades";
            hubAiTally.Location = new Point(12, 96);
            hubAiTally.Size = new Size(856, 18);
            hubAiTally.ForeColor = Color.DimGray;

            aiHub.Controls.AddRange(new Control[] { hubAiState, hubArmBtn, hubDisarmBtn, hubAiGate, hubAiLast, hubAiTally });

            Tip(hubArmBtn, "Arms the AI Trader: from here on the engine's LONG/SHORT signals are forwarded as real orders to LnnOrderExecStrategy on the selected instrument. Identical to Start AI Trader on the Sim trading tab - same confirmation, same pre-arm checks (it refuses to arm if the strategy reports no stop, no target and no trail, since a trade would never close). Enabled only once the engine is running.");
            Tip(hubDisarmBtn, "Disarms the AI Trader AND sends FLAT + Cancel All, so you end up out of the market with nothing working. This is the normal way to stop. KILL SWITCH below only disarms - it leaves an open position alone.");
            Tip(hubAiState, "Whether signals are being forwarded right now, and to which instrument's command ring. The red banner across the top of the window says the same thing - if either shows ARMED, real orders can leave this machine.");
            Tip(hubAiGate, "Why the next entry would or would not be taken: session window, cooldown, trade gap, signal hold, max trades, one-trade-at-a-time. OPEN means the next qualifying signal trades. This is the first place to look when the AI seems idle during live hours.");
            Tip(hubAiLast, "The most recent signal the AI Trader acted on and what it did with it - forwarded, held, or blocked and why. If this stops changing while the engine is running, the engine has stopped emitting signals.");
            Tip(hubAiTally, "Live session tally from real fills: trades, wins/losses, net ticks, and the current losing streak. Net ticks is gross - it does not subtract commission. The daily loss limit, when set, is shown here and trips an auto-disarm.");

            tradingHub.Text = "Trading Hub — LnnOrderExecStrategy command bridge";
            tradingHub.Location = new Point(12, 180);     // v64l: own tab; v64m: below the AI strip
            tradingHub.Size = new Size(880, 420);
            tradingHub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            tradeHubStatus.Text = "Not connected. Click Connect, then start LnnOrderExecStrategy on a NinjaTrader chart with the matching instrument.";
            tradeHubStatus.Location = new Point(12, 22);
            tradeHubStatus.Size = new Size(620, 32);
            tradeHubStatus.ForeColor = Color.DimGray;

            // v64l: the kill switch used to sit at (620,18) 200px wide, directly
            // underneath Connect/Disconnect. It now has the right end of the
            // order-button row to itself.
            killSwitchBtn.Text = "KILL SWITCH";
            killSwitchBtn.Location = new Point(672, 92);
            killSwitchBtn.Size = new Size(196, 28);
            killSwitchBtn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            killSwitchBtn.BackColor = Color.LightCoral;
            killSwitchBtn.Enabled = false;
            killSwitchBtn.Click += delegate { StopAiTrader(); };

            connectTradeHub.Text = "Connect";
            connectTradeHub.Location = new Point(672, 20);
            connectTradeHub.Size = new Size(90, 25);
            connectTradeHub.Click += ConnectTradeHubClick;

            disconnectTradeHub.Text = "Disconnect";
            disconnectTradeHub.Location = new Point(768, 20);
            disconnectTradeHub.Size = new Size(100, 25);
            disconnectTradeHub.Enabled = false;
            disconnectTradeHub.Click += DisconnectTradeHubClick;

            // --- Row: Qty, Price, SL, PT ---
            Label qtyLabel = new Label { Text = "Qty", AutoSize = true, Location = new Point(12, 62) };
            tradeQty.Text = "1";
            tradeQty.Location = new Point(40, 58);
            tradeQty.Width = 45;

            Label priceLabel = new Label { Text = "Price", AutoSize = true, Location = new Point(100, 62) };
            tradePrice.Location = new Point(140, 58);
            tradePrice.Width = 70;

            Label slLabel = new Label { Text = "Stop loss", AutoSize = true, Location = new Point(225, 62) };
            tradeStopLossPrice.Location = new Point(285, 58);
            tradeStopLossPrice.Width = 70;

            Label ptLabel = new Label { Text = "Target", AutoSize = true, Location = new Point(370, 62) };
            tradeTargetPrice.Location = new Point(410, 58);
            tradeTargetPrice.Width = 70;

            Label trailLabel = new Label { Text = "Trail SL", AutoSize = true, Location = new Point(495, 62) };
            tradeTrailingStopDistance.Location = new Point(555, 58);
            tradeTrailingStopDistance.Width = 70;

            Label slptHelp = new Label
            {
                Text = "Stop loss / Target are absolute prices; Trail SL is a distance from price. Blank = the strategy's own bracket.",
                AutoSize = false,
                Location = new Point(12, 126),
                Size = new Size(856, 18),
                ForeColor = Color.DimGray
            };

            // --- Row: order buttons ---
            buyMarketBtn.Text = "Buy Mkt";
            buyMarketBtn.Location = new Point(12, 92);
            buyMarketBtn.Size = new Size(72, 28);
            buyMarketBtn.Click += delegate { SendSimpleTradeCommand(TradeCmdOp.BuyMarket); };

            sellMarketBtn.Text = "Sell Mkt";
            sellMarketBtn.Location = new Point(90, 92);
            sellMarketBtn.Size = new Size(72, 28);
            sellMarketBtn.Click += delegate { SendSimpleTradeCommand(TradeCmdOp.SellMarket); };

            buyLimitBtn.Text = "Buy Lmt";
            buyLimitBtn.Location = new Point(180, 92);
            buyLimitBtn.Size = new Size(72, 28);
            buyLimitBtn.Click += delegate { SendPriceTradeCommand(TradeCmdOp.BuyLimit); };

            sellLimitBtn.Text = "Sell Lmt";
            sellLimitBtn.Location = new Point(258, 92);
            sellLimitBtn.Size = new Size(72, 28);
            sellLimitBtn.Click += delegate { SendPriceTradeCommand(TradeCmdOp.SellLimit); };

            buyStopBtn.Text = "Buy Stop";
            buyStopBtn.Location = new Point(348, 92);
            buyStopBtn.Size = new Size(72, 28);
            buyStopBtn.Click += delegate { SendPriceTradeCommand(TradeCmdOp.BuyStop); };

            sellStopBtn.Text = "Sell Stop";
            sellStopBtn.Location = new Point(426, 92);
            sellStopBtn.Size = new Size(72, 28);
            sellStopBtn.Click += delegate { SendPriceTradeCommand(TradeCmdOp.SellStop); };

            flatBtn.Text = "FLAT";
            flatBtn.Location = new Point(515, 92);
            flatBtn.Size = new Size(60, 28);
            flatBtn.BackColor = Color.LightPink;
            flatBtn.Click += delegate { SendSimpleTradeCommand(TradeCmdOp.Flat); };

            cancelAllBtn.Text = "Cancel All";
            cancelAllBtn.Location = new Point(581, 92);
            cancelAllBtn.Size = new Size(75, 28);
            cancelAllBtn.Click += delegate { SendSimpleTradeCommand(TradeCmdOp.CancelAll); };

            // --- Position display ---
            tradePosition.Text = "Position: unknown (waiting for fills)";
            tradePosition.Location = new Point(12, 148);
            tradePosition.Size = new Size(856, 20);
            tradePosition.ForeColor = Color.DimGray;

            // --- Fill log ---
            Label fillLogLabel = new Label { Text = "Fills / order updates from LnnOrderExecStrategy", AutoSize = true, Location = new Point(12, 176) };
            tradeFillLog.Multiline = true;
            tradeFillLog.ReadOnly = true;
            tradeFillLog.ScrollBars = ScrollBars.Vertical;
            tradeFillLog.WordWrap = false;
            tradeFillLog.BackColor = Color.White;
            tradeFillLog.Font = new Font("Consolas", 8.5F);
            tradeFillLog.Location = new Point(12, 196);
            tradeFillLog.Size = new Size(856, 210);
            tradeFillLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            tradeFillLog.Text = "No trade events yet.";

            tradingHub.Controls.AddRange(new Control[]
            {
                tradeHubStatus, connectTradeHub, disconnectTradeHub, killSwitchBtn,
                qtyLabel, tradeQty, priceLabel, tradePrice,
                slLabel, tradeStopLossPrice, ptLabel, tradeTargetPrice, trailLabel, tradeTrailingStopDistance, slptHelp,
                buyMarketBtn, sellMarketBtn, buyLimitBtn, sellLimitBtn,
                buyStopBtn, sellStopBtn, flatBtn, cancelAllBtn,
                tradePosition, fillLogLabel, tradeFillLog
            });

            // v64l: tooltips for the manual order desk, same sticky behaviour as
            // the rest of the dashboard.
            Tip(connectTradeHub, "Opens the MMF command ring to LnnOrderExecStrategy for the instrument selected on the Live learning tab. Nothing on this tab can send an order until this says connected. The strategy must be enabled on a NinjaTrader chart for the SAME instrument, or the commands go into a ring nobody is reading.");
            Tip(disconnectTradeHub, "Closes the command ring. Does NOT flatten or cancel anything already working in NinjaTrader - use FLAT and Cancel All first if you want to be flat.");
            Tip(killSwitchBtn, "Stops the AI Trader immediately: disarms it so no new signal can send an order. It does not by itself close an open position - hit FLAT after it if you are in a trade. Enabled only while the AI Trader is armed.");
            Tip(tradeQty, "Contracts per manual order. This is separate from the AI Trader's own size. The strategy's max position still caps you - an order that would push you past it is rejected at the strategy, not here.");
            Tip(tradePrice, "Limit or stop price for the Lmt / Stop buttons. Ignored by the Mkt buttons. Absolute price, e.g. 5842.25.");
            Tip(tradeStopLossPrice, "Absolute stop-loss price for this order's bracket, e.g. 5840.00. Leave blank and the strategy applies its own StopLossTicks instead.");
            Tip(tradeTargetPrice, "Absolute take-profit price for this order's bracket, e.g. 5845.00. Leave blank and the strategy applies its own ProfitTargetTicks instead.");
            Tip(tradeTrailingStopDistance, "Trailing stop distance in price (not ticks) from the current price. Leave blank for a fixed stop. If set, it replaces the fixed stop on this order.");
            Tip(buyMarketBtn, "Market buy at the size in Qty. Goes out over the same command ring the AI Trader uses, so ack / fill / event->callback / slippage all populate on the trade-path panel - this is the cleanest way to take a latency baseline with the AI Trader DISARMED.");
            Tip(sellMarketBtn, "Market sell at the size in Qty. Same path and timing as Buy Mkt.");
            Tip(buyLimitBtn, "Buy limit at the Price box. Rests until filled or cancelled - it counts as a working entry, so the one-trade-at-a-time rule blocks a second entry until it fills or you Cancel All.");
            Tip(sellLimitBtn, "Sell limit at the Price box. Same resting behaviour as Buy Lmt.");
            Tip(buyStopBtn, "Buy stop at the Price box - triggers when price trades at or above it, then goes to market. Use above the current price.");
            Tip(sellStopBtn, "Sell stop at the Price box - triggers at or below it, then goes to market. Use below the current price.");
            Tip(flatBtn, "Closes the position at market and cancels the bracket. This is the panic button for position; KILL SWITCH is the panic button for the AI. Hitting both, in that order, gets you fully stopped.");
            Tip(cancelAllBtn, "Cancels every working order for this instrument - resting entries and bracket children. Does not close an existing position; use FLAT for that. Also clears a stuck working-entry latch that is blocking new entries.");
            Tip(tradePosition, "Position as the strategy reports it from NinjaTrader, not as the dashboard guesses. If this disagrees with NinjaTrader for three seconds the dashboard resyncs to NinjaTrader, since NinjaTrader is authoritative.");
            Tip(tradeFillLog, "Every order update and execution the strategy sends back, newest last: submissions, acks, fills, rejections and bracket attachments. If an order seems to vanish, its rejection reason lands here.");
        }

        // v64m: the AI strip on the Trading Hub tab is a read-only view of state
        // the AI Trader already owns. It is refreshed from the single UI-paint
        // tick, so it costs one pass every LogFlushIntervalMs no matter how many
        // signals or fills arrive in between.
        private void SyncHubAiPanel()
        {
            try
            {
                hubArmBtn.Enabled = startPaperSimulation.Enabled;
                hubDisarmBtn.Enabled = stopPaperSimulation.Enabled;

                if (tradeArmed)
                {
                    string symbol = ResolveTradeSymbol();
                    hubAiState.Text = "ARMED — signals are being sent live to " +
                        (string.IsNullOrEmpty(symbol) ? "(no instrument selected)" : symbol) +
                        (engineDirectActive ? "  [engine-direct: the engine sends, not this dashboard]" : "");
                    hubAiState.ForeColor = Color.Firebrick;
                }
                else
                {
                    hubAiState.Text = startPaperSimulation.Enabled
                        ? "Disarmed — no orders are being sent."
                        : "Disarmed — start the engine on the Live learning tab before you can arm.";
                    hubAiState.ForeColor = Color.DimGray;
                }

                string gate = CurrentEntryGateReason();
                hubAiGate.Text = "Entry gate: " + gate;
                hubAiGate.ForeColor = gate == "OPEN" ? Color.DarkGreen : gate == "disarmed" ? Color.DimGray : Color.DarkOrange;

                hubAiLast.Text = "Last decision: " + lastDecisionText +
                    (lastDecisionUtc == DateTime.MinValue ? "" : "  (" + Age(lastDecisionUtc) + ")");

                hubAiTally.Text = tallyLabel.Text;
                hubAiTally.ForeColor = tallyLabel.ForeColor;
            }
            catch { }
        }

        // ====================================================================
        // Trading Hub — connect / disconnect
        // ====================================================================

        // v64r: asymmetric label geometry. Blank, 0, or the same value as the target keeps the
        // old symmetric label, so leaving the box alone changes nothing.
        private string LabelStopArgument()
        {
            double stop = ParseDoubleInput(labelStopTicks, 0);
            if (stop <= 0) return string.Empty;
            double target = ParseDoubleInput(minimumProfitTargetTicks, 1);
            if (Math.Abs(stop - target) < 1e-9) return string.Empty;
            return " --label-stop-ticks " + stop.ToString("R", CultureInfo.InvariantCulture);
        }

        private string ResolveTradeSymbol()
        {
            string symbol = SelectedInstrumentSymbol();
            if (string.IsNullOrEmpty(symbol)) return string.Empty;
            char[] safe = new char[symbol.Length];
            int n = 0;
            foreach (char v in symbol)
            {
                if (char.IsLetterOrDigit(v)) safe[n++] = char.ToUpperInvariant(v);
                else if (n > 0 && safe[n - 1] != '_') safe[n++] = '_';
            }
            return new string(safe, 0, n).Trim(new char[] { '_' });
        }

        private void ConnectTradeHubClick(object sender, EventArgs e)
        {
            ConnectTradeHub();
        }

        private void DisconnectTradeHubClick(object sender, EventArgs e)
        {
            DisconnectTradeHub();
        }

        private void ConnectTradeHub()
        {
            string symbol = ResolveTradeSymbol();
            if (string.IsNullOrEmpty(symbol))
            {
                tradeHubStatus.Text = "Select an instrument first.";
                tradeHubStatus.ForeColor = Color.Firebrick;
                return;
            }

            string cmdName  = @"Local\LNN_ORDER_CMD_"  + symbol;
            string fillName = @"Local\LNN_ORDER_FILL_" + symbol;

            try
            {
                long cmdBytes  = TradeHeaderBytes + (long)TradeCapacity * TradeCmdSlotBytes;
                tradeCmdMap = MemoryMappedFile.CreateOrOpen(cmdName, cmdBytes);
                tradeCmdAccessor = tradeCmdMap.CreateViewAccessor(0, cmdBytes, MemoryMappedFileAccess.ReadWrite);

                TradeHeader h;
                tradeCmdAccessor.Read<TradeHeader>(0, out h);
                if (h.Magic == TradeMagic && h.Capacity == TradeCapacity && h.RecordSize == TradeCmdSlotBytes && h.WriteSequence >= 0)
                    tradeCmdSeq = h.WriteSequence;
                else
                {
                    h = new TradeHeader { WriteSequence = 0, Capacity = TradeCapacity, RecordSize = TradeCmdSlotBytes, Magic = TradeMagic };
                    tradeCmdAccessor.Write<TradeHeader>(0, ref h);
                    tradeCmdSeq = 0;
                }

                long fillBytes = TradeHeaderBytes + (long)TradeCapacity * TradeFillSlotBytes;
                tradeFillMap = MemoryMappedFile.CreateOrOpen(fillName, fillBytes);
                tradeFillAccessor = tradeFillMap.CreateViewAccessor(0, fillBytes, MemoryMappedFileAccess.ReadWrite);

                TradeHeader fh;
                tradeFillAccessor.Read<TradeHeader>(0, out fh);
                if (fh.Magic == TradeMagic && fh.Capacity == TradeCapacity && fh.RecordSize == TradeFillSlotBytes && fh.WriteSequence >= 0)
                    tradeFillCursor = fh.WriteSequence;
                else
                {
                    fh = new TradeHeader { WriteSequence = 0, Capacity = TradeCapacity, RecordSize = TradeFillSlotBytes, Magic = TradeMagic };
                    tradeFillAccessor.Write<TradeHeader>(0, ref fh);
                    tradeFillCursor = 0;
                }

                // Diagnostic message ring (strategy Print lines → dashboard).
                try
                {
                    string msgName = @"Local\LNN_ORDER_MSG_" + symbol;
                    long msgBytes = TradeHeaderBytes + (long)TradeMsgCapacity * TradeMsgSlotBytes;
                    tradeMsgMap = MemoryMappedFile.CreateOrOpen(msgName, msgBytes);
                    tradeMsgAccessor = tradeMsgMap.CreateViewAccessor(0, msgBytes, MemoryMappedFileAccess.ReadWrite);
                    TradeHeader mh;
                    tradeMsgAccessor.Read<TradeHeader>(0, out mh);
                    if (mh.Magic == TradeMsgMagic && mh.Capacity == TradeMsgCapacity && mh.RecordSize == TradeMsgSlotBytes && mh.WriteSequence >= 0)
                        tradeMsgCursor = mh.WriteSequence;
                    else
                    {
                        mh = new TradeHeader { WriteSequence = 0, Capacity = TradeMsgCapacity, RecordSize = TradeMsgSlotBytes, Magic = TradeMsgMagic };
                        tradeMsgAccessor.Write<TradeHeader>(0, ref mh);
                        tradeMsgCursor = 0;
                    }
                }
                catch (Exception ex) { Append("Trade hub: message ring unavailable: " + ex.Message); tradeMsgAccessor = null; }

                try
                {
                    tradeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, cmdName + "_EVENT");
                }
                catch (Exception ex)
                {
                    Append("Trade hub: could not open event signal: " + ex.Message);
                    tradeSignal = null;
                }

                tradeCts = new CancellationTokenSource();
                tradeFillThread = new Thread(() => TradeFillReader(tradeCts.Token)) { IsBackground = true };
                tradeFillThread.Start();

                tradeCmdId = 0;
                tradeNetPosition = 0;

                connectTradeHub.Enabled = false;
                disconnectTradeHub.Enabled = true;
                killSwitchBtn.Enabled = true;
                EnableTradeButtons(true);

                tradeHubStatus.Text = "Connected: " + cmdName + " / " + fillName + ". Start LnnOrderExecStrategy on a NinjaTrader chart with instrument " + symbol + ".";
                tradeHubStatus.ForeColor = Color.DarkGreen;
                Append("Trading hub connected. Command ring: " + cmdName + ", fill ring: " + fillName);
                // v64y: the strategy starts with gates disabled, so it must be told the
                // current settings as soon as there is a ring to tell it on.
                SendEntryGateConfig();
                if (mmfGates.Checked)
                    Append("Entry gates: sent to the strategy (ops 25/26) — they now apply to engine-direct orders too. " +
                           "NT8's Output window will echo \"gates A\" and \"gates B\"; if it says \"unknown op 25 ignored\", the strategy is older than v64y.");
            }
            catch (Exception ex)
            {
                tradeHubStatus.Text = "Connection failed: " + ex.Message;
                tradeHubStatus.ForeColor = Color.Firebrick;
                DisconnectTradeHub();
            }
        }

        private void DisconnectTradeHub()
        {
            try { tradeCts?.Cancel(); } catch { }
            try { tradeSignal?.Set(); } catch { }

            if (tradeFillThread != null && tradeFillThread.IsAlive)
            {
                try { tradeFillThread.Join(1000); } catch { }
            }

            try { tradeSignal?.Dispose(); }        catch { }
            try { tradeCmdAccessor?.Dispose(); }   catch { }
            try { tradeFillAccessor?.Dispose(); }  catch { }
            try { tradeMsgAccessor?.Dispose(); }   catch { }
            try { tradeCmdMap?.Dispose(); }        catch { }
            try { tradeFillMap?.Dispose(); }       catch { }
            try { tradeMsgMap?.Dispose(); }        catch { }

            tradeSignal = null;
            tradeCmdAccessor = null;
            tradeFillAccessor = null;
            tradeMsgAccessor = null;
            tradeCmdMap = null;
            tradeFillMap = null;
            tradeMsgMap = null;
            tradeCts = null;
            tradeFillThread = null;

            connectTradeHub.Enabled = true;
            disconnectTradeHub.Enabled = false;
            killSwitchBtn.Enabled = false;
            EnableTradeButtons(false);

            tradeHubStatus.Text = "Disconnected.";
            tradeHubStatus.ForeColor = Color.DimGray;
        }

        private void EnableTradeButtons(bool enabled)
        {
            buyMarketBtn.Enabled = enabled;
            sellMarketBtn.Enabled = enabled;
            buyLimitBtn.Enabled = enabled;
            sellLimitBtn.Enabled = enabled;
            buyStopBtn.Enabled = enabled;
            sellStopBtn.Enabled = enabled;
            flatBtn.Enabled = enabled;
            cancelAllBtn.Enabled = enabled;
        }

        // ====================================================================
        // Trading Hub — send commands
        // ====================================================================

        private void SendSimpleTradeCommand(TradeCmdOp op)
        {
            int qty;
            if (!int.TryParse(tradeQty.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out qty) || qty <= 0)
            {
                MessageBox.Show(this, "Quantity must be a positive whole number.", "Trading hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            double trail = 0.0;
            double tmp;
            if (double.TryParse(tradeTrailingStopDistance.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tmp) && tmp > 0.0) trail = tmp;
            SendTradeCommand(op, qty, 0.0, 0.0, 0.0, 0.0, trail);
        }

        private void SendPriceTradeCommand(TradeCmdOp op)
        {
            int qty;
            if (!int.TryParse(tradeQty.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out qty) || qty <= 0)
            {
                MessageBox.Show(this, "Quantity must be a positive whole number.", "Trading hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            double price;
            if (!double.TryParse(tradePrice.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out price) || price <= 0.0)
            {
                MessageBox.Show(this, "Price must be a positive number.", "Trading hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            double sl = 0.0, pt = 0.0, trail = 0.0;
            double tmp;
            if (double.TryParse(tradeStopLossPrice.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tmp) && tmp > 0.0) sl = tmp;
            if (double.TryParse(tradeTargetPrice.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tmp) && tmp > 0.0) pt = tmp;
            if (double.TryParse(tradeTrailingStopDistance.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tmp) && tmp > 0.0) trail = tmp;
            SendTradeCommand(op, qty, price, 0.0, sl, pt, trail);
        }

        // ==================== v64y: push the entry gates into the strategy ====================
        // The dashboard's gates only ever ran on signals the dashboard forwarded. Engine-direct
        // orders skip that path entirely, so the same rules are sent to the strategy and applied
        // in SubmitEntryGated - the one point every entry passes through, whoever wrote it.
        private static int MinutesOfDay(TextBox box)
        {
            if (box == null) return -1;
            string text = box.Text.Trim();
            if (text.Length == 0) return -1;
            TimeSpan span;
            if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out span)) return -1;
            // v64z: TimeSpan.TryParse("0930") succeeds as 930 DAYS. Sending that as a session
            // window would shut the gate permanently, so anything outside a real time of day
            // is treated as "not set" rather than trusted.
            double minutes = span.TotalMinutes;
            if (minutes < 0 || minutes > 1439) return -1;
            return (int)minutes;
        }

        // v65b: an unticked gate sends 0 (= off) but keeps whatever is typed in its box, so a
        // gate can be switched off and back on without losing the number.
        private double GateValue(CheckBox on, TextBox box) { return on.Checked ? ParseDoubleInput(box, 0) : 0.0; }
        private int GateInt(CheckBox on, TextBox box)
        {
            if (!on.Checked) return 0;
            int v;
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0) return 0;
            return v;
        }

        private void SendEntryGateConfig()
        {
            UpdateActiveGatesLabel();
            if (tradeCmdAccessor == null) return;

            int start = -1, end = -1;
            if (gateOnSession.Checked)
            {
                start = MinutesOfDay(sessionStartInput); end = MinutesOfDay(sessionEndInput);
                if (start < 0 || end < 0) { start = -1; end = -1; }   // either blank or malformed = window off
            }

            byte direction = 0;
            if (gateOnDirection.Checked)
                direction = (byte)(tradeDirectionSelector.SelectedIndex == 1 ? 1 :
                                   tradeDirectionSelector.SelectedIndex == 2 ? 2 : 0);

            SendTradeCommand(TradeCmdOp.SetEntryGatesA,
                GateInt(gateOnMaxTrades, maxTradesPerSession),
                GateValue(gateOnTradeGap, minSecondsBetweenTradesInput),
                GateValue(gateOnCooldown, cooldownSecondsInput),
                GateValue(gateOnMargin, liveConfidenceMarginInput),
                gateOnDeadRegime.Checked ? NeutralBlockSeconds : 0.0,
                NeutralClearSeconds,
                direction);

            byte flags = (byte)((mmfGates.Checked ? 1 : 0) | (BarrierMode ? 2 : 0) | (flipOnOpposite.Checked ? 4 : 0));
            SendTradeCommand(TradeCmdOp.SetEntryGatesB,
                GateInt(gateOnFlips, regimeMaxFlips), start, end,
                0.45, 0.55,
                GateValue(gateOnRamp, rampWinsPerStepInput),
                flags);

            // v64z: the confidence margin is measured against THESE, not a hardcoded 0.60/0.40.
            SendTradeCommand(TradeCmdOp.SetGateThresholds, 0,
                ParseDoubleInput(paperLongThreshold, 0.60),
                ParseDoubleInput(paperShortThreshold, 0.40), 0, 0, 0, 0);

            // v65a: book-quality gates, then health gates.
            SendTradeCommand(TradeCmdOp.SetBookGates,
                GateInt(gateOnBookSize, minBookSizeInput),
                GateValue(gateOnSpread, maxSpreadTicksInput),
                GateValue(gateOnImbalance, minImbalanceInput),
                GateValue(gateOnFlicker, maxFlickerEntryInput),
                GateValue(gateOnAggression, maxAggrIntoInput),
                GateValue(gateOnStaleSignal, staleSignalMsInput),
                (byte)(micropriceEntryGate.Checked ? 1 : 0));

            SendTradeCommand(TradeCmdOp.SetHealthGates, 0,
                GateValue(gateOnFeedStale, feedStaleMsInput),
                GateValue(gateOnGiveBack, giveBackPercentInput), 0, 0, 0, 0);
        }

        // Re-push whenever a gate field changes, but never more than a few times a second -
        // these are TextBoxes and every keystroke raises TextChanged.
        private void GateFieldChanged(object sender, EventArgs e)
        {
            // v65b: the summary line updates straight away even with no ring connected - it is
            // how you check what is armed before arming anything.
            try { UpdateActiveGatesLabel(); } catch { }
            if (tradeCmdAccessor == null) return;
            gateConfigDirtyUtc = DateTime.UtcNow;
        }
        private DateTime gateConfigDirtyUtc = DateTime.MinValue;
        private void FlushGateConfigIfDirty()
        {
            if (gateConfigDirtyUtc == DateTime.MinValue) return;
            if ((DateTime.UtcNow - gateConfigDirtyUtc).TotalMilliseconds < 750) return;
            gateConfigDirtyUtc = DateTime.MinValue;
            SendEntryGateConfig();
        }

        private void SendTradeCommand(TradeCmdOp op, int qty, double price, double stopPrice, double sl, double pt, double trail)
        { SendTradeCommand(op, qty, price, stopPrice, sl, pt, trail, 0); }

        // v64y: same sender with the Tif byte exposed - ops 25/26 pack their flags there.
        private void SendTradeCommand(TradeCmdOp op, int qty, double price, double stopPrice, double sl, double pt, double trail, byte tif)
        {
            if (tradeCmdAccessor == null)
            {
                MessageBox.Show(this, "Click Connect first.", "Trading hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            long cmdId;
            lock (tradeCmdLock)
            {
                cmdId = ++tradeCmdId;
            }

            TradeCommandPayload payload = new TradeCommandPayload
            {
                CommandId          = cmdId,
                Op                 = (int)op,
                Qty                = qty,
                Price              = price,
                StopPrice          = stopPrice,
                Tif                = tif,
                SendUtcTicks       = UtcNowTicks(),
                TrailingStopDistance = (float)trail,
                StopLossPrice      = sl,
                ProfitTargetPrice  = pt
            };

            lock (tradeCmdLock)
            {
                long next   = tradeCmdSeq + 1;
                long offset = TradeHeaderBytes + (next & (TradeCapacity - 1)) * TradeCmdSlotBytes;

                tradeCmdAccessor.Write(offset, next * 2 + 1);
                Thread.MemoryBarrier();
                tradeCmdAccessor.Write<TradeCommandPayload>(offset + 8, ref payload);
                Thread.MemoryBarrier();
                tradeCmdAccessor.Write(offset, next * 2);
                Thread.MemoryBarrier();
                tradeCmdAccessor.Write(0, next);
                tradeCmdSeq = next;
            }

            if (tradeSignal != null)
            {
                try { tradeSignal.Set(); } catch { }
            }

            // Latency tracking: remember when this command left, so the ack
            // (first order update) and the fill can be timed against it.
            if ((int)op <= 13 || op == TradeCmdOp.BuyLimitJoin || op == TradeCmdOp.SellLimitJoin) // real order ops (30/31 were missing: limit entries were never timed)
            {
                commandTimings[cmdId] = new CommandTiming
                {
                    SentTicks = UtcNowTicks(),
                    Op = (int)op,
                    QuoteBid = forwarderLastBid,   // book at signal time (built-in forwarder)
                    QuoteAsk = forwarderLastAsk
                };
                if (commandTimings.Count > 500) commandTimings.Clear(); // safety bound
            }

            string desc = op.ToString() + " qty=" + qty.ToString(CultureInfo.InvariantCulture);
            if (price > 0.0) desc += " price=" + price.ToString(CultureInfo.InvariantCulture);
            if (sl > 0.0) desc += " SL=" + sl.ToString(CultureInfo.InvariantCulture);
            if (pt > 0.0) desc += " PT=" + pt.ToString(CultureInfo.InvariantCulture);
            if (trail > 0.0) desc += " TrailSL=" + trail.ToString(CultureInfo.InvariantCulture);
            // Deferred: an entry is two ring writes (signal context, then the order). Appending to
            // the log TextBox between them put WinForms layout work inside the order path.
            string logLine = "Trade hub sent: " + desc + " (cmdId=" + cmdId + ")";
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { Append(logLine); }); } catch { }
            lastCmdSentText = "#" + cmdId.ToString(CultureInfo.InvariantCulture) + " " + desc; lastCmdSentUtc = DateTime.UtcNow; lastCmdSentId = cmdId;
            if (op == TradeCmdOp.Ping) { pingSentUtc = DateTime.UtcNow; pingId = cmdId; pingAnswered = false; }
        }

        // ====================================================================
        // Trading Hub — fill reader (background thread)
        // ====================================================================

        private void TradeFillReader(CancellationToken ct)
        {
            try
            {
                long localCursor = tradeFillCursor;

                while (!ct.IsCancellationRequested)
                {
                    bool gotAny = false;

                    while (true)
                    {
                        long next   = localCursor + 1;
                        long offset = TradeHeaderBytes + (next & (TradeCapacity - 1)) * TradeFillSlotBytes;

                        long seqlock = tradeFillAccessor.ReadInt64(offset);
                        if (seqlock != next * 2)
                            break;

                        TradeFillPayload p;
                        tradeFillAccessor.Read<TradeFillPayload>(offset + 8, out p);

                        localCursor = next;
                        gotAny = true;
                        lastStrategyActivityUtc = DateTime.UtcNow;
                        lastFillRingSequenceSeen = next;

                        TradeFillPayload fill = p;
                        long rxTicks = UtcNowTicks();   // stamped on the READER thread, before any UI marshal
                        try
                        {
                            if (!IsDisposed && IsHandleCreated)
                                BeginInvoke((MethodInvoker)delegate { ProcessTradeFill(fill, rxTicks); });
                        }
                        catch { }
                    }

                    // Diagnostic messages from the strategy (text ring).
                    if (tradeMsgAccessor != null)
                    {
                        while (true)
                        {
                            long mnext = tradeMsgCursor + 1;
                            long moff = TradeHeaderBytes + (mnext & (TradeMsgCapacity - 1)) * TradeMsgSlotBytes;
                            long mseq;
                            try { mseq = tradeMsgAccessor.ReadInt64(moff); } catch { break; }
                            if (mseq != mnext * 2) break;
                            long tsTicks; byte[] textBuf = new byte[112];
                            try { tsTicks = tradeMsgAccessor.ReadInt64(moff + 8); tradeMsgAccessor.ReadArray<byte>(moff + 16, textBuf, 0, 112); }
                            catch { break; }
                            tradeMsgCursor = mnext; gotAny = true; lastStrategyActivityUtc = DateTime.UtcNow;
                            int len = 0; while (len < textBuf.Length && textBuf[len] != 0) len++;
                            string msg = System.Text.Encoding.UTF8.GetString(textBuf, 0, len);
                            try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { AppendStrategyMessage(msg); }); } catch { }
                        }
                    }

                    if (!gotAny)
                        Thread.Sleep(1);   // v64k: was 5 ms; this delay lands inside the measured fill latency
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { Append("Trade hub fill reader error: " + ex.Message); });
            }
        }

        private void AppendStrategyMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg;
            if (strategyMsgBox.Text == strategyMsgPlaceholder) strategyMsgBox.Clear();
            if (strategyMsgBox.TextLength > 40000)
            {
                int trim = Math.Min(10000, strategyMsgBox.TextLength);
                strategyMsgBox.Select(0, trim); strategyMsgBox.SelectedText = string.Empty;
            }
            strategyMsgBox.AppendText(line + Environment.NewLine);
        }

        private const string strategyMsgPlaceholder = "Strategy messages (executioner enable/gates, rejections, post/cancel reasons) appear here once the hub is connected.";

        // ==================== v64k: trade-path observability ====================
        // Heartbeat record (IsExecution = 4) from LnnOrderExecStrategy, once a second.
        // v64q: the Trading Hub's "Position:" line used to be written in exactly ONE place -
        // inside the execution branch of ProcessTradeFill. So a position the dashboard learned
        // from NT8's heartbeat instead of from a fill record (an engine-direct order, a fill
        // that arrived before the hub connected, a manual trade, a restart mid-position) showed
        // on the Sim tab's "AI position" line, which the resync does update, and NEVER on the
        // Trading Hub - which sat on "unknown (waiting for fills)" while a trade was open.
        private string hubLastFillText = string.Empty;
        private bool hubPositionFromHeartbeat;

        private void UpdateTradeHubPositionLabel(bool fromHeartbeat)
        {
            hubPositionFromHeartbeat = fromHeartbeat;
            int shown = tradeNetPosition;
            string posStr = shown > 0 ? "LONG " + shown.ToString(CultureInfo.InvariantCulture)
                          : shown < 0 ? "SHORT " + (-shown).ToString(CultureInfo.InvariantCulture)
                          : "FLAT";
            tradePosition.Text = "Position: " + posStr + hubLastFillText +
                (fromHeartbeat && shown != 0 ? "    (from NT8's heartbeat — no fill record for it)" : "");
            tradePosition.ForeColor = shown != 0 ? Color.DarkBlue : Color.DimGray;
        }

        private void ProcessStrategyStatus(TradeFillPayload st)
        {
            stratHeartbeatUtc = DateTime.UtcNow;
            stratConsumed = st.NtOrderIdLow; stratLastCmdId = st.CommandId;
            stratPosition = st.Qty; stratFlags = st.FilledQty & 0xFF; stratWorkingEntries = (st.FilledQty >> 8) & 0xFF; stratExState = st.OrderState; stratExSide = st.OrderAction; stratGate = st.MarketPosition; stratRealized = st.Price;
            stratEntryGate = (st.FilledQty >> 16) & 0xFF;   // v64y: which entry gate is blocking inside NT8 (0 = none)
            // A flat + no-working-entry heartbeat a few seconds after a send clears a stale latch
            // (the entry was rejected or never reached NT8).
            if (entrySentUtc != DateTime.MinValue && stratWorkingEntries == 0 && stratPosition == 0 && (DateTime.UtcNow - entrySentUtc).TotalSeconds > 3)
            { entrySentUtc = DateTime.MinValue; Append("Entry latch cleared: NT8 reports flat with no working entry (cmd #" + entrySentCmdId + " never filled / was rejected)."); }
            // v64k: NT8 is the source of truth for the position. The dashboard's own count is
            // built from fill records, so it drifts whenever fills are missed - the strategy or
            // dashboard restarted mid-position, the hub connected after a fill, a manual trade.
            // A drifted count used to block EVERY entry forever ("already counts a LONG
            // position"), and since NO-TRADE no longer flattens, nothing cleared it. Resync
            // after 3 consecutive disagreeing heartbeats (~3 s) so a transient in-flight fill
            // is not mistaken for drift.
            if (stratPosition != tradeNetPosition && entrySentUtc == DateTime.MinValue && stratWorkingEntries == 0)
            {
                if (++positionDisagreeTicks >= 3)
                {
                    Append("Position resync: NT8 says " + (stratPosition == 0 ? "FLAT" : stratPosition > 0 ? "LONG " + stratPosition : "SHORT " + (-stratPosition)) +
                        " but this dashboard counted " + tradeNetPosition + ". Trusting NT8 — entries are unblocked.");
                    tradeNetPosition = stratPosition;
                    ReconcileTallyPosition(stratPosition);   // v64o: the tally counter was left behind here
                    if (barrierOpenSide != 0 && stratPosition == 0) BarrierClosed("position resync (NT8 is flat)");
                    positionDisagreeTicks = 0;
                    UpdatePositionLabel();                 // Sim tab
                    UpdateTradeHubPositionLabel(true);     // v64q: Trading Hub - was left behind here
                }
            }
            else positionDisagreeTicks = 0;
            // v64q: refresh the hub line every heartbeat so it can never sit on
            // "unknown (waiting for fills)" while NT8 reports an open position.
            UpdateTradeHubPositionLabel(hubPositionFromHeartbeat);
            RefreshTradePath();
        }

        // Record type 7: where the time between "an order was decided" and "NT8 was told"
        // actually goes. hop = shared-memory ring + event wake; marshal = NinjaTrader's
        // TriggerCustomEvent queue onto its strategy thread; submit = the call itself.
        private readonly List<double> hopMs = new List<double>(), marshalMs = new List<double>(), submitMs = new List<double>();
        private void ProcessOrderPathTiming(TradeFillPayload t)
        {
            if (t.Price >= 0) { hopMs.Add(t.Price); if (hopMs.Count > 500) hopMs.RemoveRange(0, 250); }
            marshalMs.Add(t.Qty / 1000.0); if (marshalMs.Count > 500) marshalMs.RemoveRange(0, 250);       // strategy sends microseconds
            submitMs.Add(t.FilledQty / 1000.0); if (submitMs.Count > 500) submitMs.RemoveRange(0, 250);
            orderPathText = "Order path p50/p95 — ring hop " + LatencyPercentilesFine(hopMs) +
                " | NT8 marshal " + LatencyPercentilesFine(marshalMs) +
                " | submit " + LatencyPercentilesFine(submitMs);
            RefreshTradePath();
        }
        private string orderPathText = "";

        // v65c: record type 9. NinjaTrader runs a queued order command by whichever of two paths
        // reaches it first: a market event (next tick, fast) or its own TriggerCustomEvent
        // dispatcher (measured at ~145 ms on a busy platform). The ratio says whether fast
        // dispatch is actually doing its job, which no other number here reveals.
        private string dispatchText = "";
        private int dispatchFast, dispatchSlow, dispatchDataAgeMs;
        private void ProcessDispatchTelemetry(TradeFillPayload t)
        {
            dispatchFast = t.Qty; dispatchSlow = t.FilledQty; dispatchDataAgeMs = t.OrderState;
            int total = dispatchFast + dispatchSlow;
            if (total <= 0)
            {
                dispatchText = "Dispatch: no orders yet" + (t.OrderAction == 1 ? "" : "  — FastOrderDispatch is OFF in the strategy parameters");
                RefreshTradePath();
                return;
            }
            double fastShare = 100.0 * dispatchFast / total;
            StringBuilder b = new StringBuilder();
            b.Append("Dispatch: ").Append(fastShare.ToString("0", CultureInfo.InvariantCulture)).Append("% fast (")
             .Append(dispatchFast).Append(" market-event");
            if (t.Price >= 0) b.Append(" @ ").Append(t.Price.ToString("0.#", CultureInfo.InvariantCulture)).Append("ms");
            b.Append(") / ").Append(dispatchSlow).Append(" via NT8 dispatcher");
            if (t.NtOrderIdLow >= 0) b.Append(" @ ").Append((t.NtOrderIdLow / 1000.0).ToString("0", CultureInfo.InvariantCulture)).Append("ms");
            if (t.OrderAction != 1) b.Append("  — FastOrderDispatch is OFF");
            else if (fastShare < 80) b.Append("  — SLOW PATH DOMINATES: market events are not reaching the strategy (Realtime? data feed? chart instrument?)");
            b.Append("  | last market data ").Append(dispatchDataAgeMs).Append("ms ago");
            dispatchText = b.ToString();
            RefreshTradePath();
        }
        private static string LatencyPercentilesFine(List<double> samples)
        {
            if (samples.Count == 0) return "n/a";
            double[] sorted = samples.ToArray(); Array.Sort(sorted);
            Func<double, double> pick = delegate(double q) { return sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))]; };
            return pick(.50).ToString("0.00", CultureInfo.InvariantCulture) + "/" + pick(.95).ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }

        // Record type 8: how long after NinjaTrader's OWN timestamp for an order event our
        // callback ran. Large here means NT8 did the work but was slow to report it.
        private readonly List<double> ntDeliveryMs = new List<double>();
        private readonly HashSet<string> originCounted = new HashSet<string>();
        private void ProcessEventDelivery(TradeFillPayload d)
        {
            ntDeliveryMs.Add(d.Price); if (ntDeliveryMs.Count > 500) ntDeliveryMs.RemoveRange(0, 250);
            UpdateLatencyGateLabel();
        }

        private static string AckName(int code)
        {
            switch (code)
            {
                case 0: return "SUBMITTED to NT8";
                case 1: return "REJECTED by the strategy's risk gates (see strategy messages)";
                case 2: return "executioner ARMED - MEASURE mode: simulated only, NO real order";
                case 3: return "executioner ARMED - LIVE: posts a real limit when the gate passes";
                case 4: return "IGNORED - strategy is not in Realtime state";
                case 5: return "config applied";
                case 6: return "flatten / cancel done";
                case 7: return "unknown op (strategy older than the dashboard?)";
                case 8: return "FAILED with an exception (see strategy messages)";
                case 9: return "PONG";
                default: return "result " + code;
            }
        }

        // Acknowledgement record (IsExecution = 5): one per command the strategy processed.
        private void ProcessCommandAck(TradeFillPayload ack)
        {
            lastAckUtc = DateTime.UtcNow; lastAckId = ack.CommandId;
            string opText = Enum.IsDefined(typeof(TradeCmdOp), ack.OrderAction) ? ((TradeCmdOp)ack.OrderAction).ToString() : "op " + ack.OrderAction;
            if (ack.OrderAction == (int)TradeCmdOp.NextEntryBracket) { lastAckUtc = DateTime.UtcNow; return; }   // bracket set: silent, the entry's ack follows
            lastAckText = "#" + ack.CommandId.ToString(CultureInfo.InvariantCulture) + " " + opText + " -> " + AckName(ack.OrderState);
            if ((ack.OrderState == 1 || ack.OrderState == 4 || ack.OrderState == 8) && (ack.OrderAction <= 5 || ack.OrderAction == 30 || ack.OrderAction == 31))
            {
                if (ack.CommandId == entrySentCmdId) entrySentUtc = DateTime.MinValue;   // entry never went live
                if (barrierOpenSide != 0 && tradeNetPosition == 0) BarrierClosed("entry " + AckName(ack.OrderState).ToLowerInvariant());
            }
            if (ack.OrderAction == (int)TradeCmdOp.Ping)
            {
                long sent;
                if (pingSentTicks.TryGetValue(ack.CommandId, out sent))
                {
                    pingRoundTripMs.Add((UtcNowTicks() - sent) / (double)TimeSpan.TicksPerMillisecond);
                    pingSentTicks.Remove(ack.CommandId);
                }
                if (ack.CommandId == pingId) pingAnswered = true;
            }
            // v64z: gate-config acks are housekeeping, not events - they would flood the log
            // every time a gate field is edited.
            else if (ack.OrderAction != (int)TradeCmdOp.SignalContext &&
                     ack.OrderAction != (int)TradeCmdOp.SetEntryGatesA &&
                     ack.OrderAction != (int)TradeCmdOp.SetEntryGatesB &&
                     ack.OrderAction != (int)TradeCmdOp.SetGateThresholds &&
                     ack.OrderAction != (int)TradeCmdOp.SetBookGates &&
                     ack.OrderAction != (int)TradeCmdOp.SetHealthGates)
            {
                pendingFillLog.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                              .Append("  ACK   ").Append(lastAckText).Append(Environment.NewLine);
            }
            RefreshTradePath();
        }

        private static string Age(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "never";
            double s = (DateTime.UtcNow - utc).TotalSeconds;
            return s < 1 ? "<1 s ago" : s < 120 ? s.ToString("0", CultureInfo.InvariantCulture) + " s ago" : (s / 60).ToString("0.0", CultureInfo.InvariantCulture) + " min ago";
        }

        private static string GateName(int code)
        {
            switch (code)
            {
                case 1: return "no top-of-book yet";
                case 2: return "imbalance gate";
                case 3: return "microprice gate";
                case 4: return "resting-depth gate";
                case 5: return "flicker gate";
                case 6: return "aggression gate";
                default: return "";
            }
        }

        // Records the AI Trader's verdict on the latest signal (shown in the panel;
        // logged once per distinct verdict so a blocked signal is never silent).
        private void NoteDecision(string signal, string outcome)
        {
            lastDecisionText = signal + ": " + outcome; lastDecisionUtc = DateTime.UtcNow;
            if (lastDecisionText != lastDecisionLogged)
            {
                lastDecisionLogged = lastDecisionText;
                blockedRepeatCount = 0; blockedSinceUtc = DateTime.UtcNow;
                Append("AI Trader decision - " + lastDecisionText);
            }
            else if (outcome.IndexOf("FORWARDED", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // The same block, over and over, is easy to miss when it is logged once.
                // Re-announce it every 30 s with a count so a stuck gate is obvious.
                blockedRepeatCount++;
                if ((DateTime.UtcNow - blockedSinceUtc).TotalSeconds >= 30)
                {
                    Append("AI Trader: STILL BLOCKED — " + blockedRepeatCount.ToString(CultureInfo.InvariantCulture) +
                        " signals in the last " + ((int)(DateTime.UtcNow - blockedSinceUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture) +
                        " s, all with the same reason: " + lastDecisionText);
                    blockedRepeatCount = 0; blockedSinceUtc = DateTime.UtcNow;
                }
            }
            RefreshTradePath();
        }

        // v64m: callers just mark the panel dirty; uiPaintTimer repaints it at most
        // twice a second. It used to rebuild and repaint on every single ring
        // record, which is where a lot of the dashboard's churn came from.
        private void RefreshTradePath() { tradePathDirty = true; }

        private void RefreshTradePathNow()
        {
            try
            {
                StringBuilder b = new StringBuilder();
                // 1. engine
                long pTicks = Interlocked.Read(ref lastLiveProbabilityTicks);
                string pAge = pTicks == 0 ? "never" : Age(new DateTime(pTicks, DateTimeKind.Utc));
                b.Append("1 Engine:   P(up) ").Append(pTicks == 0 ? "not received" : lastLiveProbabilityText + " (" + pAge + ")");
                b.Append(" | signal ").Append(string.IsNullOrEmpty(lastSignalSeen) ? "none yet" : lastSignalSeen + " (" + Age(lastSignalSeenUtc) + ")");
                if (lastTrainedCount >= 0) b.Append(" | trained ").Append(lastTrainedCount).Append(lastTrainedCount < 100 ? "/100 - WARMUP: no LONG/SHORT until 100 labels resolve this session" : "");
                b.AppendLine();
                // 2. dashboard decision
                b.Append("2 AI Trader: ").Append(tradeArmed ? "ARMED" : "not armed (Start AI Trader)").Append(engineDirectActive ? " | engine-direct orders ON (dashboard does not forward)" : "");
                b.Append(" | last decision: ").Append(lastDecisionText).Append(lastDecisionUtc == DateTime.MinValue ? "" : " (" + Age(lastDecisionUtc) + ")");
                if (BarrierMode && barrierOpenSide != 0) b.Append(" | bracketed ").Append(barrierOpenSide > 0 ? "LONG" : "SHORT").Append(" open - waiting for its stop or target");
                if (paperMirror.Checked) b.Append(" | paper mirror ").Append(paperOpenSide == 0 ? "flat" : (paperOpenSide > 0 ? "LONG" : "SHORT") + " open");
                b.AppendLine();
                // 3. command ring
                string ringName = @"Local\LNN_ORDER_CMD_" + ResolveTradeSymbol();
                b.Append("3 Cmd ring: ").Append(tradeCmdAccessor == null ? "NOT connected" : ringName + " connected");
                b.Append(" | last sent ").Append(lastCmdSentText).Append(lastCmdSentUtc == DateTime.MinValue ? "" : " (" + Age(lastCmdSentUtc) + ")").AppendLine();
                // 4. strategy
                b.Append("4 Strategy: ");
                if (stratHeartbeatUtc == DateTime.MinValue)
                    b.Append(tradeCmdAccessor == null ? "unknown (connect the hub)" : "NO HEARTBEAT ever - LnnOrderExecStrategy (v64k+) is not running on a " + ResolveTradeSymbol() +
                        " chart, has not reached Realtime (it opens its rings only at State.Realtime), or is listening on another symbol " +
                        "(check MapSymbolOverride is blank). Expects " + ringName + ". NT8's Output window prints \"listening on ...\" when it is up.");
                else
                {
                    double hb = (DateTime.UtcNow - stratHeartbeatUtc).TotalSeconds;
                    b.Append(hb < 4 ? "ALIVE" : "STALE heartbeat " + Age(stratHeartbeatUtc) + " (strategy stopped?)");
                    b.Append((stratFlags & 1) != 0 ? " | Realtime" : " | NOT Realtime");
                    b.Append(" | consumed ").Append(stratConsumed).Append(" cmds");
                    if (tradeCmdSeq > stratConsumed) b.Append(" (dashboard sent ").Append(tradeCmdSeq).Append(" - ").Append(tradeCmdSeq - stratConsumed).Append(" NOT consumed)");
                    b.Append(" | NT position ").Append(stratPosition == 0 ? "FLAT" : stratPosition > 0 ? "LONG " + stratPosition : "SHORT " + (-stratPosition));
                    if (stratWorkingEntries > 0) b.Append(" | ").Append(stratWorkingEntries).Append(" entry order(s) WORKING");
                    b.Append(" | realized ").Append(stratRealized.ToString("0.##", CultureInfo.InvariantCulture)).Append("t");
                    // v64y: the strategy's own entry-gate verdict. This is the only place the
                    // engine-direct path reports why an entry did not happen.
                    if (mmfGates.Checked)
                        b.Append(stratEntryGate > 0
                            ? " | NT8 GATES: entries BLOCKED - " + EntryGateName(stratEntryGate)
                            : " | NT8 gates: open");
                    if ((stratFlags & 32) != 0) b.Append(" | SIMULATION ACCOUNT");
                    if ((stratFlags & 8) != 0) b.Append(" | PAUSED");
                    if ((stratFlags & 16) != 0) b.Append(" | DAILY-LOSS TRIPPED");
                    b.AppendLine();
                    b.Append("  Strategy risk: ").Append(StrategyExitText());
                    if (BarrierMode && stratRiskUtc != DateTime.MinValue && stratSlTicks <= 0 && stratPtTicks <= 0 && stratTrailTicks <= 0) b.Append("  <- NO EXIT AT ALL in strategy-bracket mode: set NT default SL/PT (strategy control row -> Apply) before arming");
                    b.AppendLine();
                    b.Append("  Executioner: ");
                    if ((stratFlags & 2) == 0) b.Append("DISABLED in the strategy (join orders become one-shot limits; entries go straight to NT8)");
                    else
                    {
                        b.Append((stratFlags & 4) != 0 ? "ENABLED LIVE" : "ENABLED MEASURE - simulated only, it will NEVER place a real order in this mode");
                        b.Append(" | state ").Append(stratExState == 0 ? "idle" : stratExState == 1 ? "ARMED " + (stratExSide == 1 ? "LONG" : "SHORT") + " waiting" + (stratGate > 0 ? " on " + GateName(stratGate) : "") : stratExState == 2 ? "POSTED " + (stratExSide == 1 ? "LONG" : "SHORT") : "markout pending");
                    }
                }
                b.AppendLine();
                if (orderPathText.Length > 0) b.Append("  ").Append(orderPathText).AppendLine();
                if (dispatchText.Length > 0) b.Append("  ").Append(dispatchText).AppendLine();   // v65c
                // 5. ack
                b.Append("5 Last ack: ").Append(lastAckText).Append(lastAckUtc == DateTime.MinValue ? "" : " (" + Age(lastAckUtc) + ")");
                if (lastCmdSentId > 0 && lastCmdSentId != lastAckId && lastCmdSentUtc != DateTime.MinValue && (DateTime.UtcNow - lastCmdSentUtc).TotalSeconds > 2)
                    b.Append("  <- command #").Append(lastCmdSentId).Append(" has NO ack after ").Append(Age(lastCmdSentUtc));
                if (!pingAnswered && (DateTime.UtcNow - pingSentUtc).TotalSeconds > 2)
                    b.Append("  <- ping #").Append(pingId).Append(" unanswered for ").Append(Age(pingSentUtc));
                tradePathStatus.Text = b.ToString();
                bool healthy = stratHeartbeatUtc != DateTime.MinValue && (DateTime.UtcNow - stratHeartbeatUtc).TotalSeconds < 4 && (stratFlags & 1) != 0;
                tradePathStatus.ForeColor = healthy ? Color.FromArgb(20, 90, 20) : Color.FromArgb(120, 60, 0);
            }
            catch { }
        }

        private readonly Dictionary<long, long> pingSentTicks = new Dictionary<long, long>();
        private readonly List<double> pingRoundTripMs = new List<double>();
        private int pingBurstLeft;

        private void TestTradePathClick(object sender, EventArgs e)
        {
            if (tradeCmdAccessor == null) ConnectTradeHub();
            if (tradeCmdAccessor == null) { Append("Trade path test: could not connect the command ring - see Trading Hub status."); return; }
            // A burst, not a single sample: this is the ONLY measurement that isolates
            // dashboard -> ring -> strategy -> ring -> dashboard from NinjaTrader's order
            // engine and the broker. No order is placed. If this comes back in single-digit
            // milliseconds, the transport is fine and a slow ack is NT8's order path.
            pingRoundTripMs.Clear(); pingSentTicks.Clear(); pingBurstLeft = 20;
            Append("Trade path test: sending 20 pings over ~5 s (no orders placed). This measures ring + strategy dispatch only.");
            System.Windows.Forms.Timer burst = new System.Windows.Forms.Timer();
            burst.Interval = 250;
            burst.Tick += delegate
            {
                if (pingBurstLeft <= 0 || tradeCmdAccessor == null)
                {
                    burst.Stop(); burst.Dispose();
                    if (pingRoundTripMs.Count == 0)
                        Append("Trade path test: NO replies to 20 pings. The strategy is not reading " + @"Local\LNN_ORDER_CMD_" + ResolveTradeSymbol() +
                            " — it is not running, not in Realtime, on another instrument, or is an older build without the ping (op 99). Recompile LnnOrderExecStrategy from this package.");
                    else
                    {
                        double[] a = pingRoundTripMs.ToArray(); Array.Sort(a);
                        Append("Trade path test: " + a.Length + "/20 replies — round trip p50 " + a[a.Length / 2].ToString("0.0", CultureInfo.InvariantCulture) +
                            " ms, p95 " + a[Math.Min(a.Length - 1, (int)(0.95 * a.Length))].ToString("0.0", CultureInfo.InvariantCulture) +
                            " ms, worst " + a[a.Length - 1].ToString("0.0", CultureInfo.InvariantCulture) + " ms. " +
                            (a[a.Length / 2] < 10 ? "TRANSPORT IS FINE — the ring and the strategy dispatch are fast, so a slow ack/fill is NinjaTrader's order engine or the broker, not this software."
                                                  : "TRANSPORT IS SLOW — the command ring or NinjaTrader's dispatch is the bottleneck; the order engine is not even involved in this test."));
                    }
                    return;
                }
                pingBurstLeft--;
                SendTradeCommand(TradeCmdOp.Ping, 0, 0, 0, 0, 0, 0.0);
                pingSentTicks[pingId] = UtcNowTicks();
                if (pingSentTicks.Count > 100) pingSentTicks.Clear();
            };
            burst.Start();
            RefreshTradePath();
        }

        private void ProcessTradeFill(TradeFillPayload fill) { ProcessTradeFill(fill, UtcNowTicks()); }

        private void ProcessTradeFill(TradeFillPayload fill, long rxTicks)
        {
            if (fill.IsExecution == 3) { ProcessExecutionerQuality(fill); return; }
            if (fill.IsExecution == 4) { ProcessStrategyStatus(fill); return; }   // v64k heartbeat
            if (fill.IsExecution == 5) { ProcessCommandAck(fill); return; }       // v64k command acknowledgement
            if (fill.IsExecution == 6) { ProcessStrategyRiskConfig(fill); return; } // v64k strategy risk config
            if (fill.IsExecution == 7) { ProcessOrderPathTiming(fill); return; }   // v64k ring hop / marshal / submit
            if (fill.IsExecution == 8) { ProcessEventDelivery(fill); return; }     // v64k NT8 event stamp -> callback
            if (fill.IsExecution == 9) { ProcessDispatchTelemetry(fill); return; } // v65c which dispatch path ran each order
            long displayTicks = fill.EventTimeTicks > 0 ? fill.EventTimeTicks : fill.CallbackUtcTicks;
            if (displayTicks <= 0 || displayTicks > DateTime.MaxValue.Ticks) displayTicks = UtcNowTicks();
            DateTime eventTime = new DateTime(displayTicks, DateTimeKind.Utc);
            string timeStr = eventTime.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string typeStr = fill.IsExecution == 1 ? "EXEC" : "ORD ";
            string stateStr = fill.OrderState.ToString(CultureInfo.InvariantCulture);

            if (fill.IsExecution == 1) PlayAlert();
            string line = timeStr + "  " + typeStr + "  price=" + fill.Price.ToString(CultureInfo.InvariantCulture) +
                "  qty=" + fill.Qty.ToString(CultureInfo.InvariantCulture) +
                "  filled=" + fill.FilledQty.ToString(CultureInfo.InvariantCulture) +
                "  state=" + stateStr +
                "  action=" + fill.OrderAction.ToString(CultureInfo.InvariantCulture) +
                "  mpos=" + fill.MarketPosition.ToString(CultureInfo.InvariantCulture) +
                "  cmdId=" + fill.CommandId.ToString(CultureInfo.InvariantCulture);

            pendingFillLog.Append(line).Append(Environment.NewLine);   // v64m: painted by FlushPendingLogs()

            // Track simple net position from executions
            if (fill.IsExecution == 1 && fill.FilledQty > 0)
            {
                // OrderAction: 0=Buy, 1=Sell, 2=SellShort, 3=BuyToCover (NT8 standard)
                if (fill.OrderAction == 0)       tradeNetPosition += fill.FilledQty;  // Buy
                else if (fill.OrderAction == 2)  tradeNetPosition -= fill.FilledQty;  // SellShort
                else if (fill.OrderAction == 1)  tradeNetPosition -= fill.FilledQty;  // Sell (close long)
                else if (fill.OrderAction == 3)  tradeNetPosition += fill.FilledQty;  // BuyToCover (close short)
                entrySentUtc = DateTime.MinValue;   // a fill resolved the in-flight entry
                if (barrierOpenSide != 0 && tradeNetPosition == 0)
                    BarrierClosed("exit filled (TP, SL or flatten)");
                int signedFill = fill.OrderAction == 0 || fill.OrderAction == 3 ? fill.FilledQty : -fill.FilledQty;
                if (signedFill != 0)
                {
                    // v64o: track EVERY execution. This used to be skipped when the record
                    // carried no price, which advanced tradeNetPosition but not
                    // fillTrackedPosition - a guaranteed, permanent desync of the tally.
                    // A missing price costs one trade's tick accuracy; skipping the fill
                    // costs every trade after it.
                    if (fill.Price > 0) lastKnownFillPrice = fill.Price;
                    else tallyPriceEstimated = true;
                    double px = fill.Price > 0 ? fill.Price
                              : lastKnownFillPrice > 0 ? lastKnownFillPrice
                              : fillAveragePrice;
                    TrackLiveTradeFill(signedFill, px);
                }
                RecordCommandLatency(fill.CommandId, fill.IsExecution == 1, fill.Price, fill.CallbackUtcTicks, rxTicks, fill.EventTimeTicks);

                hubLastFillText = "    Last fill: " + fill.Price.ToString(CultureInfo.InvariantCulture) +
                    " x " + fill.FilledQty.ToString(CultureInfo.InvariantCulture);
                UpdateTradeHubPositionLabel(false);
            }
        }

        private TabPage CreateLearningMonitorTab()
        {
            TabPage page = new TabPage("Learning monitor");
            page.AutoScroll = true;
            Label intro = new Label
            {
                Text = "Read-only view of the adaptive learner. Values appear only after the engine receives live frames; updates begin after labels mature at their forecast horizons.",
                AutoSize = true,
                Location = new Point(18, 18),
                ForeColor = Color.DimGray
            };
            GroupBox current = new GroupBox
            {
                Text = "Current adaptive state",
                Location = new Point(18, 52),
                Size = new Size(840, 222),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            learningMonitorSummary.Multiline = true;
            learningMonitorSummary.ReadOnly = true;
            learningMonitorSummary.WordWrap = false;
            learningMonitorSummary.ScrollBars = ScrollBars.Vertical;
            learningMonitorSummary.BackColor = Color.White;
            learningMonitorSummary.Font = new Font("Consolas", 9F);
            learningMonitorSummary.Location = new Point(12, 23);
            learningMonitorSummary.Size = new Size(816, 186);
            learningMonitorSummary.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            learningMonitorSummary.Text = "Waiting for adaptive-engine telemetry.\r\n\r\nThe MMF transports data only. The background PyTorch learner updates after its delayed labels resolve.";
            current.Controls.Add(learningMonitorSummary);

            GroupBox events = new GroupBox
            {
                Text = "Learning events — most recent first-to-last",
                Location = new Point(18, 290),
                Size = new Size(840, 560),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            learningMonitorHistory.Multiline = true;
            learningMonitorHistory.ReadOnly = true;
            learningMonitorHistory.WordWrap = false;
            learningMonitorHistory.ScrollBars = ScrollBars.Vertical;
            learningMonitorHistory.BackColor = Color.White;
            learningMonitorHistory.Font = new Font("Consolas", 9F);
            learningMonitorHistory.Location = new Point(12, 23);
            learningMonitorHistory.Size = new Size(816, 523);
            learningMonitorHistory.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            learningMonitorHistory.Text = "No live learner events yet.";
            events.Controls.Add(learningMonitorHistory);
            page.Controls.AddRange(new Control[] { intro, current, events });
            return page;
        }

        private TabPage CreateShadowTeacherTab()
        {
            TabPage page = new TabPage("Shadow + teacher");
            page.AutoScroll = true;
            Label intro = new Label
            {
                Text = "Diagnostics only: reversal tracker, unfamiliar-market detector, and compact background teacher. None can submit orders or directly change a prediction.",
                AutoSize = true,
                Location = new Point(18, 18),
                ForeColor = Color.DimGray
            };
            GroupBox current = new GroupBox { Text = "Current diagnostic state", Location = new Point(18, 52), Size = new Size(840, 190), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            shadowTeacherSummary.Multiline = true;
            shadowTeacherSummary.ReadOnly = true;
            shadowTeacherSummary.WordWrap = false;
            shadowTeacherSummary.ScrollBars = ScrollBars.Vertical;
            shadowTeacherSummary.BackColor = Color.White;
            shadowTeacherSummary.Font = new Font("Consolas", 9F);
            shadowTeacherSummary.Location = new Point(12, 23);
            shadowTeacherSummary.Size = new Size(816, 154);
            shadowTeacherSummary.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            shadowTeacherSummary.Text = "Waiting for shadow reversal, novelty, and compact-teacher telemetry.";
            current.Controls.Add(shadowTeacherSummary);

            GroupBox events = new GroupBox { Text = "Shadow + teacher events", Location = new Point(18, 258), Size = new Size(840, 592), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            shadowTeacherHistory.Multiline = true;
            shadowTeacherHistory.ReadOnly = true;
            shadowTeacherHistory.WordWrap = false;
            shadowTeacherHistory.ScrollBars = ScrollBars.Vertical;
            shadowTeacherHistory.BackColor = Color.White;
            shadowTeacherHistory.Font = new Font("Consolas", 9F);
            shadowTeacherHistory.Location = new Point(12, 23);
            shadowTeacherHistory.Size = new Size(816, 555);
            shadowTeacherHistory.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            shadowTeacherHistory.Text = "No shadow or teacher events yet.";
            events.Controls.Add(shadowTeacherHistory);
            page.Controls.AddRange(new Control[] { intro, current, events });
            return page;
        }

        private TabPage CreateReversalTab()
        {
            TabPage page = new TabPage("Reversal");
            page.AutoScroll = true;
            Label intro = new Label
            {
                Text = "Reversal shadow tracker: detects a trend that pokes past a trigger then snaps back, and scores whether it predicts the reversal. Diagnostic only — it does not place orders or change the live prediction. Set trig/back/trend/timeout/threshold on the Live learning tab.",
                AutoSize = true, Location = new Point(18, 18), ForeColor = Color.DimGray
            };
            GroupBox current = new GroupBox { Text = "Current reversal state", Location = new Point(18, 52), Size = new Size(840, 200), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            reversalSummaryBox.Multiline = true; reversalSummaryBox.ReadOnly = true; reversalSummaryBox.WordWrap = false;
            reversalSummaryBox.ScrollBars = ScrollBars.Vertical; reversalSummaryBox.BackColor = Color.White; reversalSummaryBox.Font = new Font("Consolas", 9F);
            reversalSummaryBox.Location = new Point(12, 23); reversalSummaryBox.Size = new Size(816, 164);
            reversalSummaryBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            reversalSummaryBox.Text = "Reversal tracker off, or waiting for scenarios. Tick 'Enable reversal tracker' on the Live learning tab and restart the engine.";
            current.Controls.Add(reversalSummaryBox);
            GroupBox events = new GroupBox { Text = "Reversal events (newest first)", Location = new Point(18, 268), Size = new Size(840, 582), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            reversalEventLog.Multiline = true; reversalEventLog.ReadOnly = true; reversalEventLog.WordWrap = false;
            reversalEventLog.ScrollBars = ScrollBars.Vertical; reversalEventLog.BackColor = Color.White; reversalEventLog.Font = new Font("Consolas", 9F);
            reversalEventLog.Location = new Point(12, 23); reversalEventLog.Size = new Size(816, 545);
            reversalEventLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            reversalEventLog.Text = "No reversal events yet.";
            events.Controls.Add(reversalEventLog);
            page.Controls.AddRange(new Control[] { intro, current, events });
            return page;
        }

        private TabPage CreateHogTab()
        {
            TabPage page = new TabPage("Nested hog");
            page.AutoScroll = true;
            Label intro = new Label
            {
                Text = "Nested multi-timescale LNN hog (8 sub-cells G0–G7 on the time frame chosen below — default 50ms · 250ms · 1s · 5s · 10s · 30s · 60s · 300s — → 5 ensemble cells → meta-cell; anticipation on G0–G3, injection on G2–G7), MES-only, fed by the same DOM ring. " +
                       "Runs as its OWN lightweight process (LnnNestedHog.exe) — not a second trading engine — with a simple online read-out trained on first-touch labels. " +
                       "Multi-core by default and kept OFF core 0, which the engine + executioner own. Paper only; it never sends orders.",
                AutoSize = false, Size = new Size(840, 64), Location = new Point(18, 14), ForeColor = Color.DimGray
            };

            FlowLayoutPanel row1 = new FlowLayoutPanel { Location = new Point(18, 84), Size = new Size(840, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            Label exeLbl = new Label { Text = "Hog exe:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogExeInput.Text = ""; hogExeInput.Width = 380; hogExeInput.Margin = new Padding(0, 4, 12, 0);
            Tip(hogExeInput, "Path to LnnNestedHog.exe. Blank = look next to the selected engine exe, then next to the dashboard.");
            Label mapLbl = new Label { Text = "DOM map:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogMapInput.Text = @"Local\LNN_DOM_MMF_MES"; hogMapInput.Width = 190; hogMapInput.Margin = new Padding(0, 4, 0, 0);
            row1.Controls.AddRange(new Control[] { exeLbl, hogExeInput, mapLbl, hogMapInput });

            FlowLayoutPanel row2 = new FlowLayoutPanel { Location = new Point(18, 116), Size = new Size(840, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            Label hidLbl = new Label { Text = "Hidden (H):", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogHiddenInput.Text = "600"; hogHiddenInput.Width = 52; hogHiddenInput.Margin = new Padding(0, 4, 12, 0);
            Label tpLbl = new Label { Text = "Target params (overrides H):", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogTargetParamsInput.Text = ""; hogTargetParamsInput.Width = 120; hogTargetParamsInput.Margin = new Padding(0, 4, 12, 0);
            Tip(hogTargetParamsInput, "Blank = use H. Otherwise the hog solves for the hidden size whose parameter count hits this target and prints params / weight GB / MACs per pass. Refuses sizes that cannot fit in RAM unless Force is ticked.");
            Label passLbl = new Label { Text = "Pass cadence ms:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogPassMsInput.Text = "50"; hogPassMsInput.Width = 48; hogPassMsInput.Margin = new Padding(0, 4, 12, 0);
            hogStartButton.Text = "Start hog"; hogStartButton.Width = 90; hogStartButton.Margin = new Padding(0, 3, 6, 0);
            hogStopButton.Text = "Stop"; hogStopButton.Width = 60; hogStopButton.Margin = new Padding(0, 3, 0, 0); hogStopButton.Enabled = false;
            hogStartButton.Click += delegate { StartHog(); };
            hogStopButton.Click += delegate { StopHog(true); };
            row2.Controls.AddRange(new Control[] { hidLbl, hogHiddenInput, tpLbl, hogTargetParamsInput, passLbl, hogPassMsInput, hogStartButton, hogStopButton });

            FlowLayoutPanel row3 = new FlowLayoutPanel { Location = new Point(18, 148), Size = new Size(840, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            hogParallel.Text = "Parallel cells (multi-core)"; hogParallel.AutoSize = true; hogParallel.Checked = true; hogParallel.Margin = new Padding(0, 7, 12, 0);
            Tip(hogParallel, "ON = the 5 ensemble cells run on separate cores. OFF = the whole pass on one core.");
            Label exLbl = new Label { Text = "Keep off core:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogExcludeCoreInput.Text = "0"; hogExcludeCoreInput.Width = 36; hogExcludeCoreInput.Margin = new Padding(0, 4, 12, 0);
            Tip(hogExcludeCoreInput, "The hog's affinity mask excludes this core (default 0 = the engine/executioner core). -1 = use all cores.");
            hogForce.Text = "Force (ignore RAM check)"; hogForce.AutoSize = true; hogForce.Margin = new Padding(0, 7, 0, 0);
            Tip(hogForce, "Skips the RAM feasibility check in the planner. The hog will try to allocate the weights even if the plan says they will not fit - expect paging or an OutOfMemory exit.");
            row3.Controls.AddRange(new Control[] { hogParallel, exLbl, hogExcludeCoreInput, hogForce });

            // v64k: time frame (the 8 sub-cell timescales) + the hog's own label horizon.
            FlowLayoutPanel row4 = new FlowLayoutPanel { Location = new Point(18, 180), Size = new Size(840, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            Label tfLbl = new Label { Text = "Time frame:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogTimescalePreset.DropDownStyle = ComboBoxStyle.DropDownList; hogTimescalePreset.Width = 210; hogTimescalePreset.Margin = new Padding(0, 4, 6, 0);
            foreach (string[] preset in HogTimescalePresets) hogTimescalePreset.Items.Add(preset[0]);
            hogTimescalePreset.SelectedIndex = 0;
            hogTimescalePreset.SelectedIndexChanged += delegate
            {
                int i = hogTimescalePreset.SelectedIndex;
                if (i >= 0 && i < HogTimescalePresets.Length && HogTimescalePresets[i][1].Length > 0) hogTimescalesInput.Text = HogTimescalePresets[i][1];
            };
            Tip(hogTimescalePreset, "Chooses the time frame the hog thinks on: the 8 sub-cell timescales G0..G7 in seconds. The default is the scalping ladder (50 ms, 250 ms, 1 s, 5 s, 10 s, 30 s, 60 s, 300 s). " +
                "Multiplied presets keep the same shape but faster or slower. Everything derived from the ladder scales with it: G2-G7 inject once per their own timescale, G0-G3 anticipate ahead by the same proportion of their timescale as the default (3x for G0, 1x for G1-G3), " +
                "and the per-neuron time constants are relative to G0. Pick Custom to type your own 8 ascending values. Applied at Start hog (the hog prints 'hog-timescales ...' to confirm).");
            hogTimescalesInput.Text = HogTimescalePresets[0][1]; hogTimescalesInput.Width = 230; hogTimescalesInput.Margin = new Padding(0, 4, 12, 0);
            hogTimescalesInput.TextChanged += delegate
            {
                // Typing a ladder that is not a preset flips the selector to Custom.
                string t = hogTimescalesInput.Text.Replace(" ", "");
                int match = -1;
                for (int i = 0; i < HogTimescalePresets.Length; i++) if (HogTimescalePresets[i][1] == t) match = i;
                int want = match >= 0 ? match : HogTimescalePresets.Length - 1;
                if (hogTimescalePreset.SelectedIndex != want) hogTimescalePreset.SelectedIndex = want;
            };
            Tip(hogTimescalesInput, "The 8 timescales in seconds, comma-separated and ascending, e.g. 0.05,0.25,1,5,10,30,60,300. Blank = default. Fewer or more than 8 values, or a non-ascending list, is refused at start (the hog exits with the reason in the log). " +
                "Shortest sensible value is about 0.005 s (5 ms); the hog still only predicts once per pass cadence, so timescales far below the cadence mostly shape the state dynamics rather than the output rate.");
            Label hzLbl = new Label { Text = "Hog horizon ms:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            hogMinHorizonInput.Text = ""; hogMinHorizonInput.Width = 52; hogMinHorizonInput.Margin = new Padding(0, 4, 2, 0);
            Label hzDash = new Label { Text = "-", AutoSize = true, Margin = new Padding(0, 8, 2, 0) };
            hogMaxHorizonInput.Text = ""; hogMaxHorizonInput.Width = 52; hogMaxHorizonInput.Margin = new Padding(0, 4, 0, 0);
            string hzTip = "The hog's OWN label horizon (how far ahead its P(up) is judged): min - max milliseconds. Blank = follow the Live learning tab's label deadline inputs, which is what the engine uses. " +
                "The actual horizon each label gets is 20 events at the current event rate, clipped into this range; the target it must reach is the Target ticks setting on the Live learning tab. " +
                "Set a shorter range (e.g. 500 - 5000) to make the hog predict the next few seconds when its time frame is fast, or longer for a slow ladder. Applied at Start hog.";
            Tip(hogMinHorizonInput, hzTip); Tip(hogMaxHorizonInput, hzTip);
            row4.Controls.AddRange(new Control[] { tfLbl, hogTimescalePreset, hogTimescalesInput, hzLbl, hogMinHorizonInput, hzDash, hogMaxHorizonInput });

            hogPlanLabel.Text = "Plan: (hog prints params / weight GB / MACs per pass / affinity at start)";
            hogPlanLabel.Location = new Point(18, 214); hogPlanLabel.Size = new Size(840, 16); hogPlanLabel.ForeColor = Color.DarkSlateBlue; hogPlanLabel.Font = new Font("Consolas", 8.5F);
            Tip(hogPlanLabel, "What the hog planned at start: hidden size H, total parameters, weight memory in GB, multiply-adds per pass, and the CPU affinity mask it took. Red = the planner refused (weights would not fit in RAM) - lower H / target params, or tick Force.");
            Label hint = new Label
            {
                Text = "H=600 is ~138M params / ~1 GB and ~1 G multiply-adds per pass; expect pass-ms well above the cadence (every DOM frame is still drained, it just predicts once per pass). " +
                       "Target/learning-rate/label-latency follow the Live learning + Sim tab inputs at start; the time frame and horizon above are the hog's own.",
                AutoSize = false, Size = new Size(840, 44), Location = new Point(18, 232), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8F)
            };
            hogStatus.Text = "Nested hog: stopped."; hogStatus.Location = new Point(18, 278); hogStatus.Size = new Size(840, 16); hogStatus.ForeColor = Color.DimGray;
            hogProbability.Text = "P(up) —"; hogProbability.Location = new Point(18, 296); hogProbability.Size = new Size(840, 34);
            hogProbability.Font = new Font("Segoe UI", 18F, FontStyle.Bold); hogProbability.ForeColor = Color.DarkSlateBlue;
            Tip(hogProbability, "The hog's live probability that the next first-touch label resolves UP (price reaches +target ticks before -target ticks within the horizon). It is the online logistic read-out over the reservoir state, refreshed every prob-ms. " +
                ">= 0.60 is shown as LONG-side, <= 0.40 as SHORT-side, between is neutral. This is paper only - the hog never sends orders; the engine can consume it as fusion evidence (Sim tab) and the AI Trader can require it to agree before an entry (hog agreement gate).");

            // Tooltips for the controls defined above.
            Tip(hogMapInput, "The DOM event ring the hog reads - the same one the engine reads (Local\\LNN_DOM_MMF_MES from the LnnDomMmfWriter indicator). Both readers see every event; neither consumes it for the other.");
            Tip(hogHiddenInput, "Hidden size H of every sub-cell. Parameters grow roughly with H squared: H=600 is about 138M params / 1 GB. Smaller H = faster passes and less RAM, larger H = more capacity. Ignored when Target params is set.");
            Tip(hogPassMsInput, "How often the hog runs a full pass (all sub-cells, ensemble, meta) and refreshes its prediction, in milliseconds. Every DOM event is still drained into the book between passes. If pass-ms in the read-out is above this cadence the hog is CPU-bound and simply runs back to back.");
            Tip(hogStartButton, "Starts LnnNestedHog.exe as its own process with the settings on this tab (time frame, horizon, H, cadence, cores) plus target/learning-rate/label-latency from the Live learning and Sim tabs. Needs the DOM ring live in NT8.");
            Tip(hogStopButton, "Stops the hog process. The engine's fusion head keeps its last saved weights; hog evidence simply goes stale and the fusion falls back to the engine alone.");

            GroupBox summary = new GroupBox { Text = "Hog read-out summary (1 Hz)", Location = new Point(18, 336), Size = new Size(840, 250), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            hogSummaryBox.Multiline = true; hogSummaryBox.ReadOnly = true; hogSummaryBox.WordWrap = true;
            hogSummaryBox.ScrollBars = ScrollBars.Vertical; hogSummaryBox.BackColor = Color.White; hogSummaryBox.Font = new Font("Consolas", 9F);
            hogSummaryBox.Location = new Point(12, 23); hogSummaryBox.Size = new Size(816, 214);
            hogSummaryBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            hogSummaryBox.Text = "Not running. Click 'Start hog' (needs the DOM ring live in NT8).";
            Tip(hogSummaryBox,
                "HOW TO READ THE HOG SUMMARY\n" +
                "CELLS: one entry per ensemble cell - sta(ndard) fas(t) slo(w) flo(w) vol - as DIRECTION:CONF:cohX. DIRECTION is UP/DOWN/NEUTRAL from the mean of that cell's 8 sub-cell votes; CONF is the size of that mean (0..1); coh is coherence = the share of its 8 sub-cells that agree with the cell's own direction.\n" +
                "meta=DIR:score:conf is the meta-cell that reads all cells: score is tanh-bounded (-1..+1), conf its confidence. ensemble is the weight-blended vote of the 5 cells (weights 0.30/0.25/0.20/0.15/0.10). agreement is the percentage of cells voting the majority side. pass-ms is the wall time of the last full pass.\n" +
                "SCALES: the mean vote per timescale across the 5 cells - U (up), D (down) or - (flat), from the fastest sub-cell G0 to the slowest G7, labelled with the time frame in use. A row like 50ms=U 250ms=U 1s=D says the fast structure leans up while the 1 s flow leans down.\n" +
                "READOUT: labels = first-touch labels resolved so far (up/down counts); accuracy = the read-out's online hit rate on those labels, majority = what always guessing the more common side would score, SKILL = accuracy minus majority (the only number that means edge). " +
                "open = labels still waiting for the price to reach +/- target; latency-armed = labels whose entry was taken at the touch after the label-latency delay; vol = recent volatility in ticks; events/s = DOM event rate; pass = last pass wall time; H and params = the network size; frames = DOM snapshots seen.");
            summary.Controls.Add(hogSummaryBox);

            GroupBox logBox = new GroupBox { Text = "Hog log", Location = new Point(18, 598), Size = new Size(840, 252), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            hogLog.Multiline = true; hogLog.ReadOnly = true; hogLog.WordWrap = false;
            hogLog.ScrollBars = ScrollBars.Vertical; hogLog.BackColor = Color.White; hogLog.Font = new Font("Consolas", 8.5F);
            hogLog.Location = new Point(12, 23); hogLog.Size = new Size(816, 216);
            hogLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Tip(hogLog, "Everything the hog process prints that is not the 1 Hz read-out: the start line with its arguments, 'hog-timescales' (the ladder it is actually using), the planner line, affinity, evidence-map status, and any error (ERR: lines are stderr).");
            logBox.Controls.Add(hogLog);

            page.Controls.AddRange(new Control[] { intro, row1, row2, row3, row4, hogPlanLabel, hint, hogStatus, hogProbability, summary, logBox });
            return page;
        }

        private string ResolveHogExe()
        {
            string t = hogExeInput.Text.Trim();
            if (!string.IsNullOrEmpty(t) && File.Exists(t)) return t;
            try
            {
                EngineChoice sel = engine.SelectedItem as EngineChoice;
                if (sel != null && !string.IsNullOrWhiteSpace(sel.Path))
                {
                    string beside = Path.Combine(Path.GetDirectoryName(sel.Path) ?? "", "LnnNestedHog.exe");
                    if (File.Exists(beside)) return beside;
                }
                string local = Path.Combine(Application.StartupPath, "LnnNestedHog.exe");
                if (File.Exists(local)) return local;
            }
            catch { }
            return null;
        }

        private void StartHog()
        {
            if (hogProcess != null && !hogProcess.HasExited) { AppendHog("Already running."); return; }
            string exe = ResolveHogExe();
            if (exe == null) { hogStatus.Text = "Nested hog: LnnNestedHog.exe not found — compile it and set the path above."; hogStatus.ForeColor = Color.Firebrick; return; }
            double tickSize = ParseDoubleInput(tickSizeInput, 0.25); if (tickSize <= 0) tickSize = 0.25;
            int hidden; if (!int.TryParse(hogHiddenInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hidden) || hidden < 8) hidden = 600;
            int passMs; if (!int.TryParse(hogPassMsInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out passMs) || passMs < 10) passMs = 50;
            long targetParams = 0; long.TryParse(hogTargetParamsInput.Text.Trim().Replace(",", "").Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out targetParams);
            int excl; if (!int.TryParse(hogExcludeCoreInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out excl)) excl = 0;
            double minTarget; if (!double.TryParse(minimumProfitTargetTicks.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out minTarget) || minTarget <= 0) minTarget = 1.0;
            int minH, maxH;
            if (!int.TryParse(minimumLabelDeadlineMilliseconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minH) || minH < 1) minH = 2000;
            if (!int.TryParse(maximumLabelDeadlineMilliseconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxH) || maxH < minH) maxH = Math.Max(minH, 30000);
            int labelLat; if (!int.TryParse(labelLatencyInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out labelLat) || labelLat < 0) labelLat = 0;
            // v64k: the hog's own horizon overrides the Live tab values when both boxes hold numbers.
            int hogMinH, hogMaxH;
            if (int.TryParse(hogMinHorizonInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hogMinH) && hogMinH >= 1 &&
                int.TryParse(hogMaxHorizonInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hogMaxH) && hogMaxH >= hogMinH)
            { minH = hogMinH; maxH = hogMaxH; }
            string timescales = hogTimescalesInput.Text.Replace(" ", "").Trim();
            if (timescales.Length > 0)
            {
                string[] tsParts = timescales.Split(new char[] { ',' });
                double prev = 0; bool tsOk = tsParts.Length == 8;
                for (int i = 0; tsOk && i < tsParts.Length; i++) { double v; tsOk = double.TryParse(tsParts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > prev; prev = v; }
                if (!tsOk) { hogStatus.Text = "Nested hog: time frame must be 8 ascending seconds, e.g. 0.05,0.25,1,5,10,30,60,300 (got '" + timescales + "')."; hogStatus.ForeColor = Color.Firebrick; return; }
            }
            string map = hogMapInput.Text.Trim(); if (string.IsNullOrEmpty(map)) map = @"Local\LNN_DOM_MMF_MES";

            string args = string.Format(CultureInfo.InvariantCulture,
                "--map {0} --tick-size {1} --hidden {2} --pass-ms {3} --parallel {4} --exclude-core {5} --min-target-ticks {6} --min-horizon-ms {7} --max-horizon-ms {8} --label-latency-ms {9} --evidence-map " + QuoteArgument(HogEvidenceMapName),
                QuoteArgument(map), tickSize.ToString("R", CultureInfo.InvariantCulture), hidden, passMs, hogParallel.Checked ? 1 : 0, excl,
                minTarget.ToString("R", CultureInfo.InvariantCulture), minH, maxH, labelLat);
            if (targetParams > 0) args += " --target-params " + targetParams.ToString(CultureInfo.InvariantCulture);
            if (hogForce.Checked) args += " --force 1";
            if (timescales.Length > 0) args += " --timescales " + timescales;

            try
            {
                hogProcess = new Process();
                hogProcess.StartInfo.FileName = exe;
                hogProcess.StartInfo.Arguments = args;
                hogProcess.StartInfo.UseShellExecute = false;
                hogProcess.StartInfo.CreateNoWindow = true;
                hogProcess.StartInfo.RedirectStandardOutput = true;
                hogProcess.StartInfo.RedirectStandardError = true;
                hogProcess.EnableRaisingEvents = true;
                hogProcess.OutputDataReceived += HogOutput;
                hogProcess.ErrorDataReceived += delegate(object s2, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) BeginInvoke((MethodInvoker)delegate { AppendHog("ERR: " + e.Data); }); };
                hogProcess.Exited += delegate { try { BeginInvoke((MethodInvoker)delegate { hogStatus.Text = "Nested hog: exited (code " + SafeExitCode(hogProcess) + ")."; hogStatus.ForeColor = Color.Firebrick; hogStartButton.Enabled = true; hogStopButton.Enabled = false; }); } catch { } };
                hogProcess.Start();
                hogProcess.BeginOutputReadLine();
                hogProcess.BeginErrorReadLine();
                hogStatus.Text = "Nested hog: running (PID " + hogProcess.Id.ToString(CultureInfo.InvariantCulture) + ", H=" + (targetParams > 0 ? "from target" : hidden.ToString(CultureInfo.InvariantCulture)) + ", pass " + passMs + " ms, " + (hogParallel.Checked ? "parallel cells" : "single core") + ", off core " + excl +
                    ", time frame " + (timescales.Length > 0 ? timescales : "default") + ", horizon " + minH + "-" + maxH + " ms).";
                hogStatus.ForeColor = Color.DarkGreen;
                hogStartButton.Enabled = false; hogStopButton.Enabled = true;
                hogMetricsText = hogCellsText = hogTsText = "";
                hogSummaryBox.Text = "Starting... allocating weights, then waiting for first labels.";
                AppendHog("Started: " + Path.GetFileName(exe) + " " + args);
            }
            catch (Exception ex)
            {
                hogStatus.Text = "Nested hog: failed to start — " + ex.Message; hogStatus.ForeColor = Color.Firebrick;
                AppendHog("Start failed: " + ex.Message);
            }
        }

        private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(CultureInfo.InvariantCulture); } catch { return "?"; } }

        private void StopHog(bool announce)
        {
            Process p = hogProcess; hogProcess = null;
            if (p != null)
            {
                try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
                try { p.Dispose(); } catch { }
                if (announce) AppendHog("Stopped.");
            }
            hogStatus.Text = "Nested hog: stopped."; hogStatus.ForeColor = Color.DimGray;
            hogStartButton.Enabled = true; hogStopButton.Enabled = false;
        }

        private void HogOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            string line = e.Data;
            try { BeginInvoke((MethodInvoker)delegate { HandleHogLine(line); }); } catch { }
        }

        private void HandleHogLine(string line)
        {
            Match live = HogUpProbability.Match(line);
            if (live.Success)
            {
                double p; if (double.TryParse(live.Groups["probability"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                {
                    lastHogProbability = p; lastHogProbabilityUtc = DateTime.UtcNow;
                    hogProbability.Text = "P(up) " + p.ToString("F3", CultureInfo.InvariantCulture) + (p >= 0.6 ? "   ▲ LONG-side" : p <= 0.4 ? "   ▼ SHORT-side" : "   — neutral");
                    hogProbability.ForeColor = p >= 0.6 ? Color.DarkGreen : p <= 0.4 ? Color.Firebrick : Color.DarkSlateBlue;
                }
                return;
            }
            Match m = HogMetrics.Match(line);
            if (m.Success)
            {
                hogMetricsText = "labels " + m.Groups["labels"].Value + " (up " + m.Groups["up"].Value + " / down " + m.Groups["down"].Value + ")" +
                    "  |  accuracy " + m.Groups["acc"].Value + "%  vs majority " + m.Groups["base"].Value + "%  SKILL " + m.Groups["skill"].Value + "%" +
                    Environment.NewLine + "open " + m.Groups["open"].Value + "  latency-armed " + m.Groups["armed"].Value + "  vol " + m.Groups["vol"].Value + "t  events/s " + m.Groups["eps"].Value +
                    "  |  pass " + m.Groups["pass"].Value + " ms  H=" + m.Groups["hidden"].Value + "  params " + m.Groups["params"].Value + "  frames " + m.Groups["frames"].Value;
                RefreshHogSummary(); return;
            }
            if (line.StartsWith("hog-timescales ")) { hogTimescaleLine = line.Substring(15); RefreshHogSummary(); AppendHog(line); return; }
            if (line.StartsWith("hog-cells ")) { hogCellsText = line.Substring(10); RefreshHogSummary(); return; }
            if (line.StartsWith("hog-ts ")) { hogTsText = line.Substring(7); RefreshHogSummary(); return; }
            if (line.StartsWith("nested-hog ") || line.StartsWith("hog affinity"))
            {
                if (line.Contains("WILL NOT FIT") || line.Contains("efusing")) { hogPlanLabel.ForeColor = Color.Firebrick; hogPlanLabel.Text = line; }
                else if (line.StartsWith("nested-hog hidden=")) { hogPlanLabel.ForeColor = Color.DarkSlateBlue; hogPlanLabel.Text = "Plan: " + line.Substring(11); }
                AppendHog(line); return;
            }
            AppendHog(line);
        }

        private void RefreshHogSummary()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CELLS   " + (hogCellsText.Length > 0 ? hogCellsText : "(waiting)"));
            sb.AppendLine("SCALES  " + (hogTsText.Length > 0 ? hogTsText : "(waiting)") + "   [U=up D=down -=flat, mean over 5 cells]");
            if (hogTimescaleLine.Length > 0) sb.AppendLine("LADDER  " + hogTimescaleLine + "   [ant = anticipation look-ahead, inj = periodic injection]");
            sb.AppendLine();
            sb.AppendLine("READOUT " + (hogMetricsText.Length > 0 ? hogMetricsText : "(waiting for labels)"));
            hogSummaryBox.Text = sb.ToString();
        }

        private void AppendHog(string line)
        {
            try
            {
                if (hogLog.TextLength > 200000) hogLog.Text = hogLog.Text.Substring(hogLog.TextLength - 100000);
                hogLog.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        private void FitFillModel()
        {
            if (fitProcess != null && !fitProcess.HasExited) { Append("Fill-model fit already running."); return; }
            string folder = captureFolder.Text.Trim();
            string events = null;
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    DateTime newestTime = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(folder, "*_dom_events_*.bin")) { DateTime t = File.GetLastWriteTimeUtc(f); if (t > newestTime) { newestTime = t; events = f; } }
                }
            }
            catch { }
            if (events == null) { Append("Fill model: no *_dom_events_*.bin capture in " + folder + ". Tick 'Record DOM events' and run the engine first."); return; }
            string exe = null;
            try
            {
                EngineChoice sel = engine.SelectedItem as EngineChoice;
                if (sel != null && !string.IsNullOrWhiteSpace(sel.Path)) { string beside = Path.Combine(Path.GetDirectoryName(sel.Path) ?? "", "LnnExecBacktest.exe"); if (File.Exists(beside)) exe = beside; }
                if (exe == null) { string local = Path.Combine(Application.StartupPath, "LnnExecBacktest.exe"); if (File.Exists(local)) exe = local; }
            }
            catch { }
            if (exe == null) { Append("Fill model: LnnExecBacktest.exe not found — compile LnnExecBacktest.cs next to the engine."); return; }
            double tickSize = ParseDoubleInput(tickSizeInput, 0.25); if (tickSize <= 0) tickSize = 0.25;
            string outFile = Path.Combine(folder, "fill_model.txt");
            string args = QuoteArgument(events) + " --tick " + tickSize.ToString("R", CultureInfo.InvariantCulture) + " --fit-fill-model " + QuoteArgument(outFile) +
                " --cancel-ahead " + Math.Max(0, Math.Min(1, ParseDoubleInput(exCancelAheadInput, 0.5))).ToString("R", CultureInfo.InvariantCulture) + " --fit-every-ms 250 --fit-horizons 500,1000,2000,5000";
            try
            {
                fitProcess = new Process();
                fitProcess.StartInfo.FileName = exe; fitProcess.StartInfo.Arguments = args;
                fitProcess.StartInfo.UseShellExecute = false; fitProcess.StartInfo.CreateNoWindow = true;
                fitProcess.StartInfo.RedirectStandardOutput = true; fitProcess.StartInfo.RedirectStandardError = true;
                fitProcess.OutputDataReceived += delegate(object s2, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) BeginInvoke((MethodInvoker)delegate { Append("fill-model: " + e.Data); }); };
                fitProcess.ErrorDataReceived += delegate(object s2, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) BeginInvoke((MethodInvoker)delegate { Append("fill-model error: " + e.Data); }); };
                fitProcess.EnableRaisingEvents = true;
                fitProcess.Exited += delegate { try { BeginInvoke((MethodInvoker)delegate { Append("Fill model fit finished -> " + outFile + ". Set the strategy's FillModelFile to this path (once), then Apply executioner settings to (re)load it."); }); } catch { } };
                fitProcess.Start(); fitProcess.BeginOutputReadLine(); fitProcess.BeginErrorReadLine();
                Append("Fill model fit started: " + Path.GetFileName(exe) + " " + args);
            }
            catch (Exception ex) { Append("Fill model fit failed to start: " + ex.Message); }
        }

        private void RunExecBacktest()
        {
            if (backtestProcess != null && !backtestProcess.HasExited) { Append("Backtest already running."); return; }
            string folder = captureFolder.Text.Trim();
            string events = lastDomEventsPath;
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    string newest = null; DateTime newestTime = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(folder, "*_dom_events_*.bin"))
                    { DateTime t = File.GetLastWriteTimeUtc(f); if (t > newestTime) { newestTime = t; newest = f; } }
                    if (newest != null) events = newest;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(events) || !File.Exists(events)) { Append("Backtest: no *_dom_events_*.bin capture found in " + folder + ". Tick 'Record DOM events' and run the engine first."); return; }
            string exe = null;
            try
            {
                EngineChoice sel = engine.SelectedItem as EngineChoice;
                if (sel != null && !string.IsNullOrWhiteSpace(sel.Path)) { string beside = Path.Combine(Path.GetDirectoryName(sel.Path) ?? "", "LnnExecBacktest.exe"); if (File.Exists(beside)) exe = beside; }
                if (exe == null) { string local = Path.Combine(Application.StartupPath, "LnnExecBacktest.exe"); if (File.Exists(local)) exe = local; }
            }
            catch { }
            if (exe == null) { Append("Backtest: LnnExecBacktest.exe not found next to the engine or the dashboard — compile LnnExecBacktest.cs."); return; }
            double tickSize = ParseDoubleInput(tickSizeInput, 0.25); if (tickSize <= 0) tickSize = 0.25;
            string signals = Path.ChangeExtension(events, null) + "_signals.csv";
            string outCsv = Path.ChangeExtension(events, null) + "_exec_backtest.csv";
            string args = QuoteArgument(events) + " --tick " + tickSize.ToString("R", CultureInfo.InvariantCulture) +
                (File.Exists(signals) ? " --signals-csv " + QuoteArgument(signals) : " --signal-every-ms 5000") +
                " --aggr-qty " + ((int)ParseDoubleInput(exAggrQtyInput, 15)).ToString(CultureInfo.InvariantCulture) +
                " --aggr-window-ms " + ((int)ParseDoubleInput(exAggrWindowInput, 500)).ToString(CultureInfo.InvariantCulture) +
                " --markout1-ms " + ((int)ParseDoubleInput(exMark1Input, 1000)).ToString(CultureInfo.InvariantCulture) +
                " --markout2-ms " + ((int)ParseDoubleInput(exMark2Input, 5000)).ToString(CultureInfo.InvariantCulture) +
                " " + backtestSweepInput.Text.Trim() +
                (File.Exists(Path.Combine(folder, "fill_model.txt")) ? " --fill-model " + QuoteArgument(Path.Combine(folder, "fill_model.txt")) + " --policy gates,ev --expected-move-ticks " + Math.Max(0.25, ParseDoubleInput(exExpectedMoveInput, 2)).ToString("R", CultureInfo.InvariantCulture) : "") +
                " --out " + QuoteArgument(outCsv);
            try
            {
                backtestProcess = new Process();
                backtestProcess.StartInfo.FileName = exe; backtestProcess.StartInfo.Arguments = args;
                backtestProcess.StartInfo.UseShellExecute = false; backtestProcess.StartInfo.CreateNoWindow = true;
                backtestProcess.StartInfo.RedirectStandardOutput = true; backtestProcess.StartInfo.RedirectStandardError = true;
                backtestProcess.OutputDataReceived += delegate(object s2, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) BeginInvoke((MethodInvoker)delegate { Append("backtest: " + e.Data); }); };
                backtestProcess.ErrorDataReceived += delegate(object s2, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) BeginInvoke((MethodInvoker)delegate { Append("backtest error: " + e.Data); }); };
                backtestProcess.Start(); backtestProcess.BeginOutputReadLine(); backtestProcess.BeginErrorReadLine();
                Append("Backtest started: " + Path.GetFileName(exe) + " " + args);
            }
            catch (Exception ex) { Append("Backtest failed to start: " + ex.Message); }
        }

        private TabPage CreateTemporalObserverTab()
        {
            TabPage page = new TabPage("Temporal MMF observer");
            Label intro = new Label { Text = "Read-only companion to the supplied temporal self-consistency/MMF idea. It reads the NinjaTrader ring separately and cannot change the live learner, weights, signals, bridge, or orders.", AutoSize = true, Location = new Point(18, 18), ForeColor = Color.DimGray };
            Label controls = new Label { Text = "Isolated outcome heads (set before Start; changing them applies on the next Stop/Start):", AutoSize = true, Location = new Point(18, 48), ForeColor = Color.DimGray };
            ConfigureObserverOptionRow(page, "Fast", 72, observerFastTicks, observerFastMilliseconds, "1.00", "1000");
            ConfigureObserverOptionRow(page, "Medium", 98, observerMediumTicks, observerMediumMilliseconds, "2.00", "5000");
            ConfigureObserverOptionRow(page, "Slow", 124, observerSlowTicks, observerSlowMilliseconds, "4.00", "30000");
            temporalObserverSummary.Multiline = true; temporalObserverSummary.ReadOnly = true; temporalObserverSummary.WordWrap = false;
            temporalObserverSummary.ScrollBars = ScrollBars.Vertical; temporalObserverSummary.BackColor = Color.White; temporalObserverSummary.Font = new Font("Consolas", 9F);
            temporalObserverSummary.Location = new Point(18, 158); temporalObserverSummary.Size = new Size(840, 170);
            temporalObserverSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            temporalObserverSummary.Text = "Waiting for the passive temporal MMF observer.";
            temporalObserverHistory.Multiline = true; temporalObserverHistory.ReadOnly = true; temporalObserverHistory.WordWrap = false;
            temporalObserverHistory.ScrollBars = ScrollBars.Vertical; temporalObserverHistory.BackColor = Color.White; temporalObserverHistory.Font = new Font("Consolas", 9F);
            temporalObserverHistory.Location = new Point(18, 344); temporalObserverHistory.Size = new Size(840, 506);
            temporalObserverHistory.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            temporalObserverHistory.Text = "No observer events yet.";
            page.Controls.AddRange(new Control[] { intro, controls, temporalObserverSummary, temporalObserverHistory });
            return page;
        }

        private static void ConfigureObserverOptionRow(TabPage page, string name, int y, TextBox targetTicks, TextBox deadlineMilliseconds, string defaultTicks, string defaultDeadline)
        {
            Label nameLabel = new Label { Text = name + " head", AutoSize = true, Location = new Point(18, y + 4) };
            Label ticksLabel = new Label { Text = "target ticks", AutoSize = true, Location = new Point(115, y + 4) };
            targetTicks.Text = defaultTicks; targetTicks.Location = new Point(189, y); targetTicks.Size = new Size(70, 23);
            Label deadlineLabel = new Label { Text = "deadline ms", AutoSize = true, Location = new Point(277, y + 4) };
            deadlineMilliseconds.Text = defaultDeadline; deadlineMilliseconds.Location = new Point(351, y); deadlineMilliseconds.Size = new Size(78, 23);
            Label note = new Label { Text = "Labels are delayed until either side reaches target, or the deadline expires.", AutoSize = true, Location = new Point(447, y + 4), ForeColor = Color.DimGray };
            page.Controls.AddRange(new Control[] { nameLabel, ticksLabel, targetTicks, deadlineLabel, deadlineMilliseconds, note });
        }

        private TabPage CreateModelRegistryTab()
        {
            TabPage page = new TabPage("Model registry");
            page.AutoScroll = true;
            Label intro = new Label
            {
                Text = "ONNX packages are portable inference artifacts. The dashboard checks the package manifest before a model can be selected for the live engine.",
                AutoSize = true,
                Location = new Point(18, 18),
                ForeColor = Color.DimGray
            };
            Label rootLabel = new Label { Text = "ONNX packages", AutoSize = true, Location = new Point(18, 52) };
            onnxRegistryRoot.Text = Path.Combine(activeWeightRoot.Text, "onnx_packages");
            onnxRegistryRoot.Location = new Point(122, 48);
            onnxRegistryRoot.Width = 460;
            browseOnnxRegistryRoot.Text = "Folder...";
            browseOnnxRegistryRoot.Location = new Point(590, 47);
            browseOnnxRegistryRoot.AutoSize = true;
            browseOnnxRegistryRoot.Click += BrowseOnnxRegistryRootClick;
            refreshOnnxRegistry.Text = "Refresh";
            refreshOnnxRegistry.Location = new Point(670, 47);
            refreshOnnxRegistry.AutoSize = true;
            refreshOnnxRegistry.Click += delegate { RefreshOnnxRegistry(); };
            useSelectedOnnxPackage.Text = "Use selected model for live";
            useSelectedOnnxPackage.Location = new Point(18, 82);
            useSelectedOnnxPackage.AutoSize = true;
            useSelectedOnnxPackage.Click += UseSelectedOnnxPackageClick;
            onnxRegistryStatus.Text = "Registry: choose an ONNX package folder, then Refresh.";
            onnxRegistryStatus.Location = new Point(210, 85);
            onnxRegistryStatus.Size = new Size(640, 18);
            onnxRegistryStatus.ForeColor = Color.DimGray;

            onnxRegistryList.Location = new Point(18, 115);
            onnxRegistryList.Size = new Size(840, 380);
            onnxRegistryList.View = View.Details;
            onnxRegistryList.FullRowSelect = true;
            onnxRegistryList.GridLines = true;
            onnxRegistryList.HideSelection = false;
            onnxRegistryList.Columns.Add("Status", 105);
            onnxRegistryList.Columns.Add("Symbol", 72);
            onnxRegistryList.Columns.Add("Tick", 65);
            onnxRegistryList.Columns.Add("Target", 65);
            onnxRegistryList.Columns.Add("Horizon", 75);
            onnxRegistryList.Columns.Add("Edge", 75);
            onnxRegistryList.Columns.Add("Package", 350);
            onnxRegistryList.SelectedIndexChanged += delegate
            {
                if (onnxRegistryList.SelectedItems.Count == 0) return;
                OnnxPackage package = onnxRegistryList.SelectedItems[0].Tag as OnnxPackage;
                if (package != null) onnxRegistryStatus.Text = package.Compatibility + ": " + package.Detail;
            };

            activeOnnxPackage.Text = "Active ONNX package: none selected";
            activeOnnxPackage.Location = new Point(18, 225);
            activeOnnxPackage.Size = new Size(840, 18);
            activeOnnxPackage.ForeColor = Color.DimGray;
            page.Controls.AddRange(new Control[] { intro, rootLabel, onnxRegistryRoot, browseOnnxRegistryRoot,
                refreshOnnxRegistry, useSelectedOnnxPackage, onnxRegistryStatus, onnxRegistryList });
            return page;
        }

        private void BrowseOnnxRegistryRootClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the folder containing ONNX model-package folders";
                dialog.SelectedPath = Directory.Exists(onnxRegistryRoot.Text) ? onnxRegistryRoot.Text : activeWeightRoot.Text;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                onnxRegistryRoot.Text = dialog.SelectedPath;
                RefreshOnnxRegistry();
            }
        }

        private static string JsonString(string json, string property)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"(?<v>[^\\\"]*)\\\"",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["v"].Value : string.Empty;
        }

        private static double JsonNumber(string json, string property, double fallback)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*(?<v>-?[0-9]+(?:\\.[0-9]+)?)",
                RegexOptions.CultureInvariant);
            double value;
            return match.Success && double.TryParse(match.Groups["v"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private string SelectedInstrumentSymbol()
        {
            string choice = instrument.SelectedItem as string ?? string.Empty;
            int split = choice.IndexOf(' ');
            return (split < 0 ? choice : choice.Substring(0, split)).Trim().ToUpperInvariant();
        }

        private string ValidateOnnxPackage(OnnxPackage package)
        {
            if (package.DepthLevels != 10) return "Invalid — requires full 10-level DOM";
            if (package.DepthFeatureCount != 43) return "Invalid — unsupported depth feature contract";
            if (!string.Equals(package.Symbol, SelectedInstrumentSymbol(), StringComparison.OrdinalIgnoreCase))
                return "Incompatible — symbol differs from live selection";
            double selectedTick;
            if (!double.TryParse(tickSizeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out selectedTick) || selectedTick <= 0.0)
                return "Invalid — live tick size is not configured";
            if (Math.Abs(package.TickSize - selectedTick) > Math.Max(1e-9, selectedTick * 1e-6))
                return "Incompatible — tick size differs from live selection";
            return "Compatible";
        }

        private void RefreshOnnxRegistry()
        {
            onnxRegistryList.Items.Clear();
            string root = onnxRegistryRoot.Text.Trim();
            if (!Directory.Exists(root))
            {
                onnxRegistryStatus.Text = "Registry folder does not exist yet.";
                onnxRegistryStatus.ForeColor = Color.Firebrick;
                return;
            }
            int discovered = 0;
            try
            {
                foreach (string manifestPath in Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories))
                {
                    string json = File.ReadAllText(manifestPath);
                    string folder = Path.GetDirectoryName(manifestPath);
                    OnnxPackage package = new OnnxPackage();
                    package.Folder = folder;
                    package.Symbol = JsonString(json, "symbol");
                    package.TickSize = JsonNumber(json, "tick_size", double.NaN);
                    package.DepthLevels = (int)JsonNumber(json, "depth_levels", 0.0);
                    package.DepthFeatureCount = (int)JsonNumber(json, "depth_feature_count", 0.0);
                    package.TargetTicks = JsonNumber(json, "target_ticks", double.NaN);
                    package.HorizonSeconds = JsonNumber(json, "horizon_seconds", double.NaN);
                    package.Edge = JsonString(json, "holdout_edge_over_baseline");
                    string modelFile = JsonString(json, "model_file");
                    package.Detail = string.IsNullOrWhiteSpace(modelFile) ? "manifest has no model_file" : modelFile;
                    if (!string.Equals(JsonString(json, "format"), "lnn-onnx-package/v1", StringComparison.Ordinal))
                        package.Compatibility = "Invalid — unknown manifest format";
                    else if (string.IsNullOrWhiteSpace(modelFile) || !File.Exists(Path.Combine(folder, modelFile)))
                        package.Compatibility = "Invalid — model.onnx missing";
                    else
                        package.Compatibility = ValidateOnnxPackage(package);
                    ListViewItem item = new ListViewItem(package.Compatibility);
                    item.SubItems.Add(package.Symbol);
                    item.SubItems.Add(double.IsNaN(package.TickSize) ? "?" : package.TickSize.ToString("G", CultureInfo.InvariantCulture));
                    item.SubItems.Add(double.IsNaN(package.TargetTicks) ? "?" : package.TargetTicks.ToString("G", CultureInfo.InvariantCulture));
                    item.SubItems.Add(double.IsNaN(package.HorizonSeconds) ? "?" : package.HorizonSeconds.ToString("G", CultureInfo.InvariantCulture) + " s");
                    item.SubItems.Add(string.IsNullOrWhiteSpace(package.Edge) ? "n/a" : package.Edge);
                    item.SubItems.Add(folder);
                    item.Tag = package;
                    onnxRegistryList.Items.Add(item);
                    discovered++;
                }
                onnxRegistryStatus.Text = "Registry: " + discovered + " package(s) found. Compatible models match the live symbol, tick size, and full depth contract.";
                onnxRegistryStatus.ForeColor = Color.DimGray;
            }
            catch (Exception error)
            {
                onnxRegistryStatus.Text = "Registry scan failed: " + error.Message;
                onnxRegistryStatus.ForeColor = Color.Firebrick;
            }
        }

        private void UseSelectedOnnxPackageClick(object sender, EventArgs e)
        {
            if (onnxRegistryList.SelectedItems.Count == 0)
            {
                onnxRegistryStatus.Text = "Select one compatible ONNX package first.";
                onnxRegistryStatus.ForeColor = Color.Firebrick;
                return;
            }
            OnnxPackage package = onnxRegistryList.SelectedItems[0].Tag as OnnxPackage;
            if (package == null || !string.Equals(package.Compatibility, "Compatible", StringComparison.Ordinal))
            {
                onnxRegistryStatus.Text = "Only a compatible package can be selected for live use.";
                onnxRegistryStatus.ForeColor = Color.Firebrick;
                return;
            }
            activeOnnxPackagePath = package.Folder;
            activeOnnxPackage.Text = "Active ONNX package: " + package.Folder + " (passed only to an ONNX-compatible engine)";
            activeOnnxPackage.ForeColor = Color.DarkGreen;
            onnxRegistryStatus.Text = "Selected for the next compatible live-engine start.";
            onnxRegistryStatus.ForeColor = Color.DarkGreen;
        }

        // Version-aware engine naming/ranking. New HybridTemporalMmf_vN builds
        // are recognized automatically — no dashboard edit needed per release.
        private static readonly Regex HybridMmfVersionPattern = new Regex(
            @"^LnnLiveLearner_HybridTemporalMmf(?:_v(?<v>\d+))?\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Dictionary<int, string> HybridMmfVersionNotes = new Dictionary<int, string>
        {
            { 1,  "native fast + background trainer" },
            { 2,  "native fast + reversal tracker" },
            { 3,  "primary model only" },
            { 4,  "session checkpoints" },
            { 5,  "shadow reversal" },
            { 6,  "bounded UI + adjustable deadline" },
            { 7,  "live probability + shadow reversal" },
            { 8,  "configurable shadow reversal" },
            { 9,  "novelty + diagnostics" },
            { 10, "pre-unfamiliar snapshots" },
            { 11, "stable novelty + live labels" },
            { 12, "shadow reversal fusion" },
        };

        private static string EngineDisplayName(string filename)
        {
            if (filename.Equals("LnnLiveLearner_MmfUnifiedShadow.exe", StringComparison.OrdinalIgnoreCase))
                return "Live native MMF — absorption + cross-market + reversal (no paper / no bridge)";
            if (filename.Equals("LnnLiveLearner_HybridTemporal.exe", StringComparison.OrdinalIgnoreCase))
                return "Hybrid compact temporal (native fast + background trainer)";
            if (filename.Equals("LnnLiveLearner.exe", StringComparison.OrdinalIgnoreCase))
                return "Compact temporal v1 (current)";
            Match m = HybridMmfVersionPattern.Match(filename);
            if (m.Success)
            {
                int v = m.Groups["v"].Success ? int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) : 1;
                string note;
                string versionText = v == 1 && !m.Groups["v"].Success ? "MMF" : "MMF v" + v.ToString(CultureInfo.InvariantCulture);
                return HybridMmfVersionNotes.TryGetValue(v, out note)
                    ? "Hybrid compact temporal — " + versionText + " (" + note + ")"
                    : "Hybrid compact temporal — " + versionText;
            }
            return Path.GetFileNameWithoutExtension(filename);
        }

        // Higher rank wins the default engine selection. The unified-shadow
        // build outranks everything; hybrid MMF builds rank by version number
        // (so a future _v13 automatically becomes the preferred default);
        // the remaining legacy builds keep their historical preference order.
        private static readonly string[] LegacyEnginePreferenceOrder = new string[]
        {
            "LnnLiveLearner_MmfThreeHorizon_AdaptiveRisk.exe",
            "LnnLiveLearner_MmfThreeHorizon.exe",
            "LnnLiveLearner_MmfThreeOutcome.exe",
            "LnnLiveLearner_MmfNestedCells_Optimized.exe",
            "LnnLiveLearner_MmfNestedCells.exe",
            "LnnLiveLearner_NestedCellsTimeTarget.exe",
            "LnnLiveLearner_AdaptiveHorizon.exe",
            "LnnLiveLearner_OrderFlowVolume.exe",
            "LnnLiveLearner_DenoisedTwoHead.exe",
            "LnnLiveLearner_TolerantIntegrity_LoadCheckpoint.exe",
            "LnnLiveLearner_TolerantIntegrity_PaperTiming.exe",
            "LnnLiveLearner_TolerantIntegrity.exe",
            "LnnLiveLearner_LatencyPanel.exe",
            "LnnLiveLearner_IntegrityGuard.exe",
            "LnnLiveLearner_AdjustableTarget.exe",
            "LnnLiveLearner_ManualSave.exe",
            "LnnLiveLearner_BestValidated.exe",
            "LnnLiveLearner_Performance.exe",
            "LnnLiveLearner_PaperSimControls.exe",
            "LnnLiveLearner_PaperSim.exe",
            "LnnLiveLearner_FeatureAudit.exe",
        };

        private static int EnginePreferenceRank(string filename)
        {
            // Hybrid temporal MMF builds are the native engine family for this
            // dashboard: the newest version present is always the default.
            Match m = HybridMmfVersionPattern.Match(filename);
            if (m.Success)
                return 1000000 + (m.Groups["v"].Success ? int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) : 0);
            if (filename.Equals("LnnLiveLearner_MmfUnifiedShadow.exe", StringComparison.OrdinalIgnoreCase))
                return 100000;
            if (filename.Equals("LnnLiveLearner_HybridTemporal.exe", StringComparison.OrdinalIgnoreCase))
                return 90000;
            if (filename.Equals("LnnLiveLearner_MmfThreeHorizon_MacroBars.exe", StringComparison.OrdinalIgnoreCase))
                return 80000;
            for (int i = 0; i < LegacyEnginePreferenceOrder.Length; i++)
                if (filename.Equals(LegacyEnginePreferenceOrder[i], StringComparison.OrdinalIgnoreCase))
                    return 1000 - i;
            return -1;
        }

        private void LoadEngines()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string[] executables = Directory.GetFiles(directory, "LnnLiveLearner*.exe");
            // Any Python or Mojo file copied beside this dashboard is a
            // candidate plug-in; models do not need a special filename or a
            // new dashboard build. Other executable plug-ins can be browsed.
            string[] scripts = Directory.GetFiles(directory, "*.py");
            // Development builds keep reusable Python engines one directory
            // above the compiled dashboard.  Include those candidates too so
            // the MMF/pipe ONNX engine is selectable without copying files.
            string sourceDirectory = Path.GetFullPath(Path.Combine(directory, ".."));
            if (Directory.Exists(sourceDirectory) && !string.Equals(sourceDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                List<string> combinedScripts = new List<string>(scripts);
                foreach (string candidate in Directory.GetFiles(sourceDirectory, "*.py"))
                    if (!combinedScripts.Contains(candidate)) combinedScripts.Add(candidate);
                scripts = combinedScripts.ToArray();
            }
            string[] mojoScripts = Directory.GetFiles(directory, "*.mojo");
            Array.Sort(executables, StringComparer.OrdinalIgnoreCase);
            Array.Sort(scripts, StringComparer.OrdinalIgnoreCase);
            Array.Sort(mojoScripts, StringComparer.OrdinalIgnoreCase);
            int preferredIndex = -1;
            int preferredRank = -1;
            foreach (string path in executables)
            {
                string filename = Path.GetFileName(path);
                engine.Items.Add(new EngineChoice(EngineDisplayName(filename), path));
                int rank = EnginePreferenceRank(filename);
                if (rank > preferredRank)
                {
                    preferredRank = rank;
                    preferredIndex = engine.Items.Count - 1;
                }
            }
            foreach (string path in scripts)
                engine.Items.Add(new EngineChoice(Path.GetFileNameWithoutExtension(path) + " (Python)", path));
            foreach (string path in mojoScripts)
                engine.Items.Add(new EngineChoice(Path.GetFileNameWithoutExtension(path) + " (Mojo)", path));
            if (engine.Items.Count > 0) engine.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
        }

        private void EngineSelectionChanged(object sender, EventArgs e)
        {
            EngineChoice choice = engine.SelectedItem as EngineChoice;
            if (choice == null) return;

            if (choice.IsMojoScript)
            {
                runtimeLabel.Text = "Mojo runtime";
                if (string.IsNullOrWhiteSpace(pythonExecutable.Text) ||
                    string.Equals(pythonExecutable.Text.Trim(), "python.exe", StringComparison.OrdinalIgnoreCase))
                    pythonExecutable.Text = "mojo";
            }
            else if (choice.IsPythonScript)
            {
                runtimeLabel.Text = "Python runtime";
                if (string.IsNullOrWhiteSpace(pythonExecutable.Text) ||
                    string.Equals(pythonExecutable.Text.Trim(), "mojo", StringComparison.OrdinalIgnoreCase))
                    pythonExecutable.Text = "python.exe";
            }
            else
            {
                runtimeLabel.Text = choice.UsesDashboardPipeTransport ? "Plug-in runtime" : "Runtime (not used)";
            }
        }

        private void BrowseEngineClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select an LNN engine plug-in";
                dialog.Filter = "LNN plug-ins (*.exe;*.py;*.mojo)|*.exe;*.py;*.mojo|Python scripts (*.py)|*.py|Mojo scripts (*.mojo)|*.mojo|Executable files (*.exe)|*.exe|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                EngineChoice selected = new EngineChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
                engine.Items.Add(selected);
                engine.SelectedItem = selected;
                SetStatus("Selected learner: " + selected.Name, Color.DimGray);
            }
        }

        private void BrowsePythonClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the Python or Mojo runtime executable";
                dialog.Filter = "Runtime executable (*.exe)|*.exe|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                pythonExecutable.Text = dialog.FileName;
            }
        }

        private void BrowseCaptureClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where to save raw DOM session files";
                dialog.SelectedPath = captureFolder.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    captureFolder.Text = dialog.SelectedPath;
            }
        }

        private void BrowseCustomTrainerFolderClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the folder containing your Python training script";
                dialog.SelectedPath = Directory.Exists(customTrainerFolder.Text.Trim())
                    ? customTrainerFolder.Text.Trim()
                    : AppDomain.CurrentDomain.BaseDirectory;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                customTrainerFolder.Text = dialog.SelectedPath;
                LoadCustomTrainerScripts();
            }
        }

        private void LoadCustomTrainerScripts()
        {
            customTrainerScript.Items.Clear();
            string folder = customTrainerFolder.Text.Trim();
            if (!Directory.Exists(folder)) return;
            string[] scripts = Directory.GetFiles(folder, "*.py", SearchOption.TopDirectoryOnly);
            Array.Sort(scripts, StringComparer.OrdinalIgnoreCase);
            foreach (string path in scripts) customTrainerScript.Items.Add(Path.GetFileName(path));
            if (customTrainerScript.Items.Count > 0) customTrainerScript.SelectedIndex = 0;
            else horizonStudyStatus.Text = "No .py files were found in the selected trainer folder.";
        }

        private void BrowseActiveWeightRootClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the shared folder containing category-specific active LNN weights";
                dialog.SelectedPath = activeWeightRoot.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    activeWeightRoot.Text = dialog.SelectedPath;
            }
        }

        private void BrowseLoadCheckpointClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select a compatible learned-weights checkpoint";
                dialog.Filter = "LNN checkpoint (*.bin)|*.bin|All files (*.*)|*.*";
                if (File.Exists(loadCheckpointFile.Text.Trim()))
                    dialog.FileName = loadCheckpointFile.Text.Trim();
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                loadCheckpointFile.Text = dialog.FileName;
                SetStatus("Startup weights selected: " + Path.GetFileName(dialog.FileName), Color.DimGray);
            }
        }

        private static bool SupportsSeparateLoadCheckpoint(EngineChoice choice)
        {
            if (choice == null) return false;
            string name = Path.GetFileName(choice.Path);
            return name.Equals("LnnLiveLearner_TolerantIntegrity_LoadCheckpoint.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporal.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v2.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v3.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v4.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v5.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v6.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v7.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v8.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v9.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v10.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v11.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_HybridTemporalMmf_v12.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_DenoisedTwoHead.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_OrderFlowVolume.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_AdaptiveHorizon.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_NestedCellsTimeTarget.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_MmfNestedCells.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_MmfNestedCells_Optimized.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_MmfThreeOutcome.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_MmfThreeHorizon.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_MmfThreeHorizon_AdaptiveRisk.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LnnLiveLearner_MmfThreeHorizon_MacroBars.exe", StringComparison.OrdinalIgnoreCase);
        }

        private void InstrumentChanged(object sender, EventArgs e)
        {
            // Defaults only: the field stays editable for a non-standard market.
            if (instrument.SelectedIndex < 0) return;
            tickSizeInput.Text = instrument.SelectedIndex == 0 || instrument.SelectedIndex == 6 ? "0.0001" : "0.25";
        }

        // The user enters the broker/data-plan round-trip fee in dollars.  The
        // learner works in ticks, so conversion happens only at its boundary.
        // M6E is priced from its 12,500 EUR contract multiplier, allowing a
        // user-selected 0.00005 or 0.00010 price increment.
        private static double GetTickValueDollars(int instrumentIndex, double tickSize)
        {
            if (instrumentIndex == 0 || instrumentIndex == 6) return 12500.0 * tickSize;
            if (instrumentIndex == 3 || instrumentIndex == 4 || instrumentIndex == 7) return 2.0 * tickSize; // MNQ
            if (instrumentIndex == 5) return 50.0 * tickSize; // ES
            return 5.0 * tickSize; // MES and its MMF/event variants
        }

        // One horizontal auto-spacing row of config controls. Labels/inputs get
        // margins so their text can never collide regardless of font metrics,
        // and the row grows to fit its content on a single line (the tab
        // scrolls if the window is narrower). Vertical margins align label
        // baselines with the taller text boxes.
        // ==================== v65b: the entry-gate panel (Trading Hub tab) ====================
        // Every gate that is enforced inside NT8, in one place, each with its own on/off box.
        // The checkbox decides what gets SENT; the value beside it is never cleared, so a gate
        // can be switched off and back on without losing its number.
        private FlowLayoutPanel GateRow(CheckBox on, string caption, string tip, params Control[] values)
        {
            on.Text = caption; on.AutoSize = true; on.Checked = false;
            Tip(on, tip);
            on.CheckedChanged += GateFieldChanged;
            Control[] items = new Control[values.Length + 1];
            items[0] = on;
            Array.Copy(values, 0, items, 1, values.Length);
            FlowLayoutPanel row = ConfigRow(items);
            row.Margin = new Padding(0, 0, 0, 1);
            return row;
        }

        private void BuildEntryGatePanel()
        {
            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 4, 4)
            };

            Font sectionFont = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            Label counting = new Label { Text = "Counting & timing", AutoSize = true, UseMnemonic = false, Font = sectionFont, ForeColor = Color.DarkSlateBlue };
            Label model    = new Label { Text = "Model & signal",    AutoSize = true, UseMnemonic = false, Font = sectionFont, ForeColor = Color.DarkSlateBlue };
            Label book     = new Label { Text = "Book quality (needs depth)", AutoSize = true, UseMnemonic = false, Font = sectionFont, ForeColor = Color.DarkSlateBlue };
            Label health   = new Label { Text = "Health & risk",     AutoSize = true, UseMnemonic = false, Font = sectionFont, ForeColor = Color.DarkSlateBlue };

            stack.Controls.Add(ConfigRow(mmfGates));
            activeGatesLabel.AutoSize = true; activeGatesLabel.UseMnemonic = false; activeGatesLabel.ForeColor = Color.DimGray;
            activeGatesLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Italic);
            stack.Controls.Add(ConfigRow(activeGatesLabel));

            stack.Controls.Add(ConfigRow(counting));
            stack.Controls.Add(GateRow(gateOnSession, "Session window",
                "Entries only between these local times. Outside it the strategy refuses entries; exits and brackets are untouched.\nBlank or malformed times disable it. The window may cross midnight.",
                sessionStartInput, new Label { Text = "to", AutoSize = true }, sessionEndInput));
            stack.Controls.Add(GateRow(gateOnMaxTrades, "Max trades / session",
                "Stop entering after this many entries. Counted INSIDE NT8, so engine-direct trades count too.\nReset by ARM and by the calendar day.",
                maxTradesPerSession));
            stack.Controls.Add(GateRow(gateOnTradeGap, "Trade gap (s)",
                "Minimum seconds between entries, whatever the signal does in between. Flattens are never delayed.",
                minSecondsBetweenTradesInput));
            stack.Controls.Add(GateRow(gateOnCooldown, "Post-loss cooldown (s)",
                "After a losing trade closes, block new entries for this long. Driven by the strategy's own realized ticks, so it sees engine-direct trades.",
                cooldownSecondsInput));
            stack.Controls.Add(GateRow(gateOnFlips, "Flip guard (per 10 min)",
                "Block once this many direction reversals have happened inside ten minutes. Too many flips means the model cannot read the current regime.\nA reversal is an entry on the opposite side to the last one - closing a position does not count.",
                regimeMaxFlips));
            stack.Controls.Add(GateRow(gateOnRamp, "Size ramp (wins per +1)",
                "Start at one contract and add one per this many winning trades, capped at the AI qty. Not a block - it scales size.",
                rampWinsPerStepInput));

            stack.Controls.Add(ConfigRow(model));
            stack.Controls.Add(GateRow(gateOnDeadRegime, "Dead regime (s neutral)",
                "Block entries once the model has sat inside the 0.45-0.55 dead zone for this long. Re-opens only after a directional read HOLDS for 3 s, so one spike out of a flat tape is not traded.\nFed by the P(up) stamped on each signal (op 46).",
                neutralBlockSecondsInput));
            stack.Controls.Add(GateRow(gateOnMargin, "Confidence margin",
                "An entry must beat the paper threshold by this much. Measured against the long/short thresholds you actually set, which are sent alongside.\nExample: threshold 0.60, margin 0.05 -> a long needs P(up) >= 0.65.",
                liveConfidenceMarginInput));
            stack.Controls.Add(GateRow(gateOnDirection, "Direction filter",
                "Take only one side. A blocked-side signal still closes an open position - it just never opens a new one.",
                tradeDirectionSelector));
            stack.Controls.Add(GateRow(gateOnStaleSignal, "Signal age (ms)",
                "Reject an entry whose signal context is older than this. Catches a stalled engine whose queued order still arrives.",
                staleSignalMsInput));

            stack.Controls.Add(ConfigRow(book));
            stack.Controls.Add(GateRow(gateOnSpread, "Max spread (ticks)",
                "Refuse a market entry when the bid/ask spread is wider than this. You pay the spread on every market order and nothing checked it before v65a.\nOn an 8-tick target a 2-tick spread is a quarter of the move.",
                maxSpreadTicksInput));
            stack.Controls.Add(GateRow(gateOnImbalance, "Min imbalance",
                "The book must lean the way you are entering: (bidSize-askSize)/(bidSize+askSize) >= this for a long, mirrored for a short.\nSame number the executioner uses to decide where to post.",
                minImbalanceInput));
            stack.Controls.Add(ConfigRow(micropriceEntryGate));
            stack.Controls.Add(GateRow(gateOnFlicker, "Max flicker / s",
                "Block when the touch changes more than this many times a second. A flickering touch means the price you are about to pay is not the price you can see.",
                maxFlickerEntryInput));
            stack.Controls.Add(GateRow(gateOnAggression, "Max aggression into us",
                "Block when at least this much aggressive volume has already traded on the side you are about to take. This is the price running away - you would be last in.",
                maxAggrIntoInput));
            stack.Controls.Add(GateRow(gateOnBookSize, "Min book size",
                "Require at least this much size on the side you would TAKE (the ask, for a buy). Thin book means slippage past the touch.",
                minBookSizeInput));

            stack.Controls.Add(ConfigRow(health));
            stack.Controls.Add(GateRow(gateOnFeedStale, "Feed stale (ms)",
                "Block entries when no market data has arrived for this long. Trading on a dead feed is trading blind.\nAlso surfaces the strategy never reaching Realtime: no market events means no data stamps.",
                feedStaleMsInput));
            stack.Controls.Add(GateRow(gateOnGiveBack, "Daily give-back (%)",
                "Once the day is up, stop entering after handing back this share of the day's PEAK realized ticks.\nExample: peak +100t at 30% -> entries stop if the day falls to +70t.\nDeliberately NOT reset by ARM, only by the calendar day, so re-arming cannot trade through it.",
                giveBackPercentInput));

            entryGatePanel.Text = "Entry gates — enforced inside NT8 (covers engine-direct)";
            entryGatePanel.Location = new Point(12, 664);   // clear of the hub help label
            entryGatePanel.Size = new Size(880, 560);
            entryGatePanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            entryGatePanel.Controls.Add(stack);
            // The hub box no longer stretches to the bottom, so the gate panel has somewhere to
            // live. Dropping the Bottom anchor is not enough on its own - by the time this runs
            // the box has already been stretched once, and removing the anchor just freezes it
            // at that height - so the height is set back explicitly.
            tradingHub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            tradingHub.Height = 420;
            tradingHubTab.Controls.Add(entryGatePanel);
            UpdateActiveGatesLabel();
        }

        // One line naming every gate that can actually fire, so the panel can be read at a glance.
        private void UpdateActiveGatesLabel()
        {
            if (!mmfGates.Checked)
            {
                activeGatesLabel.Text = "Gates are NOT being enforced — tick the box above to send them to NT8.";
                activeGatesLabel.ForeColor = Color.Firebrick;
                return;
            }
            List<string> on = new List<string>();
            if (gateOnSession.Checked)     on.Add("session " + sessionStartInput.Text.Trim() + "-" + sessionEndInput.Text.Trim());
            if (gateOnMaxTrades.Checked)   on.Add("max " + maxTradesPerSession.Text.Trim() + " trades");
            if (gateOnTradeGap.Checked)    on.Add("gap " + minSecondsBetweenTradesInput.Text.Trim() + "s");
            if (gateOnCooldown.Checked)    on.Add("cooldown " + cooldownSecondsInput.Text.Trim() + "s");
            if (gateOnFlips.Checked)       on.Add("flips " + regimeMaxFlips.Text.Trim() + "/10min");
            if (gateOnRamp.Checked)        on.Add("ramp/" + rampWinsPerStepInput.Text.Trim());
            if (gateOnDeadRegime.Checked)  on.Add("dead-regime " + neutralBlockSecondsInput.Text.Trim() + "s");
            if (gateOnMargin.Checked)      on.Add("margin " + liveConfidenceMarginInput.Text.Trim());
            if (gateOnDirection.Checked)   on.Add(tradeDirectionSelector.SelectedIndex == 1 ? "longs only" : tradeDirectionSelector.SelectedIndex == 2 ? "shorts only" : "direction (both)");
            if (gateOnStaleSignal.Checked) on.Add("signal age " + staleSignalMsInput.Text.Trim() + "ms");
            if (gateOnSpread.Checked)      on.Add("spread<=" + maxSpreadTicksInput.Text.Trim() + "t");
            if (gateOnImbalance.Checked)   on.Add("imbal>=" + minImbalanceInput.Text.Trim());
            if (micropriceEntryGate.Checked) on.Add("microprice");
            if (gateOnFlicker.Checked)     on.Add("flicker<=" + maxFlickerEntryInput.Text.Trim() + "/s");
            if (gateOnAggression.Checked)  on.Add("aggr<" + maxAggrIntoInput.Text.Trim());
            if (gateOnBookSize.Checked)    on.Add("book>=" + minBookSizeInput.Text.Trim());
            if (gateOnFeedStale.Checked)   on.Add("feed<=" + feedStaleMsInput.Text.Trim() + "ms");
            if (gateOnGiveBack.Checked)    on.Add("give-back " + giveBackPercentInput.Text.Trim() + "%");
            activeGatesLabel.ForeColor = on.Count == 0 ? Color.DimGray : Color.DarkGreen;
            activeGatesLabel.Text = on.Count == 0
                ? "Enforcement is on, but no individual gate is ticked — nothing is being blocked."
                : "Active: " + string.Join(", ", on.ToArray());
        }

        private static FlowLayoutPanel ConfigRow(params Control[] items)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 2),
                Padding = new Padding(0)
            };
            foreach (Control c in items)
            {
                if (c is CheckBox) c.Margin = new Padding(6, 5, 10, 0);
                else if (c is Button) c.Margin = new Padding(4, 1, 8, 0);
                else if (c is Label) c.Margin = new Padding(2, 6, 3, 0);
                else c.Margin = new Padding(0, 2, 12, 0); // TextBox / ComboBox
                row.Controls.Add(c);
            }
            return row;
        }

        private static Label MakeMetric(string title, string value)
        {
            return new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10, 4, 4, 2),
                Text = title + "\r\n" + value
            };
        }

        private void StartClick(object sender, EventArgs e)
        {
            if (learner != null && !learner.HasExited) return;
            if (instrument.SelectedIndex < 0)
            {
                SetStatus("Select an instrument first.", Color.Firebrick);
                return;
            }
            // Clear any stale engine/forwarder still holding the ring from a
            // previous run so this Start takes the MMF over cleanly.
            EndLingeringEngineProcesses(false);
            engineDirectActive = false; // set true only if engine-direct orders are wired below

            string pipeName;
            double defaultTickSize;
            string mmfMapName = null;
            bool mmfTransport = false;
            if (instrument.SelectedIndex == 1)
            {
                pipeName = "MesDepthBridge";
                activeControlPipe = "MesDepthBridgeControl";
                activePaperControlPipe = "MesDepthBridgePaperControl";
                activeModelSavePipe = "MesDepthBridgeModelControl";
                defaultTickSize = 0.25;
            }
            else if (instrument.SelectedIndex == 2)
            {
                pipeName = "MesDepthEvents";
                activeControlPipe = "MesDepthEventsControl";
                activePaperControlPipe = "MesDepthEventsPaperControl";
                activeModelSavePipe = "MesDepthEventsModelControl";
                defaultTickSize = 0.25;
            }
            else if (instrument.SelectedIndex == 3)
            {
                pipeName = "MnqDepthBridge";
                activeControlPipe = "MnqDepthBridgeControl";
                activePaperControlPipe = "MnqDepthBridgePaperControl";
                activeModelSavePipe = "MnqDepthBridgeModelControl";
                defaultTickSize = 0.25;
            }
            else if (instrument.SelectedIndex == 4)
            {
                pipeName = "MnqDepthEvents";
                activeControlPipe = "MnqDepthEventsControl";
                activePaperControlPipe = "MnqDepthEventsPaperControl";
                activeModelSavePipe = "MnqDepthEventsModelControl";
                defaultTickSize = 0.25;
            }
            else if (instrument.SelectedIndex == 5)
            {
                pipeName = "EsDepthBridge";
                activeControlPipe = "EsDepthBridgeControl";
                activePaperControlPipe = "EsDepthBridgePaperControl";
                activeModelSavePipe = "EsDepthBridgeModelControl";
                defaultTickSize = 0.25;
            }
            else if (instrument.SelectedIndex == 6)
            {
                pipeName = "M6eDepthEvents";
                activeControlPipe = "M6eDepthEventsControl";
                activePaperControlPipe = "M6eDepthEventsPaperControl";
                activeModelSavePipe = "M6eDepthEventsModelControl";
                defaultTickSize = 0.0001;
            }
            else if (instrument.SelectedIndex == 7)
            {
                // The learner opens the MMF reader and uses this private pipe
                // internally to preserve the existing model/control protocol.
                pipeName = "LnnMmfMnqInternal";
                activeControlPipe = null;
                activePaperControlPipe = pipeName + "PaperControl";
                activeModelSavePipe = pipeName + "ModelControl";
                defaultTickSize = 0.25;
                mmfMapName = @"Local\LNN_DOM_MMF_MNQ";
                mmfTransport = true;
            }
            else if (instrument.SelectedIndex == 8)
            {
                pipeName = "LnnMmfMesInternal";
                activeControlPipe = null;
                activePaperControlPipe = pipeName + "PaperControl";
                activeModelSavePipe = pipeName + "ModelControl";
                defaultTickSize = 0.25;
                mmfMapName = @"Local\LNN_DOM_MMF_MES";
                mmfTransport = true;
            }
            else
            {
                pipeName = "LnnDepthBridge";
                activeControlPipe = "LnnDepthBridgeControl";
                activePaperControlPipe = "LnnDepthBridgePaperControl";
                activeModelSavePipe = "LnnDepthBridgeModelControl";
                defaultTickSize = 0.0001;
            }

            double tickSize;
            if (!double.TryParse(tickSizeInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tickSize) ||
                tickSize <= 0.0 || tickSize > 100.0)
            {
                SetStatus("Tick size must be a positive number, such as " + defaultTickSize.ToString("G", CultureInfo.InvariantCulture) + ".", Color.Firebrick);
                return;
            }

            EngineChoice selectedEngine = engine.SelectedItem as EngineChoice;
            if (selectedEngine == null)
            {
                SetStatus("Choose an LNN learner executable first.", Color.Firebrick);
                return;
            }
            string learnerPath = selectedEngine.Path;
            if (!File.Exists(learnerPath))
            {
                SetStatus("Cannot find the selected learner executable.", Color.Firebrick);
                return;
            }
            bool nativeEngine = selectedEngine.IsNativeLearner;
            bool directMmfReadOnlyEngine = selectedEngine.IsDashboardDirectMmfLearner;
            bool dashboardPipePlugin = selectedEngine.UsesDashboardPipeTransport;
            string selectedEngineFilename = Path.GetFileName(selectedEngine.Path);
            bool directUnifiedMmfLive = selectedEngineFilename.Equals("LnnLiveLearner_MmfUnifiedShadow.exe", StringComparison.OrdinalIgnoreCase);
            bool selectedOnnxEngine = selectedEngineFilename.Equals("multitask_onnx_mmf_engine.py", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("multitask_live_adaptive_mmf_engine.py", StringComparison.OrdinalIgnoreCase);
            bool selectedAdaptiveOnnxEngine = selectedEngineFilename.Equals("multitask_live_adaptive_mmf_engine.py", StringComparison.OrdinalIgnoreCase);
            bool selectedCompactTemporalHybrid = selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporal.exe", StringComparison.OrdinalIgnoreCase);
            bool selectedCompactTemporalHybridMmf = selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v2.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v3.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v4.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v5.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v6.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v7.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v8.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v9.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v10.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v11.exe", StringComparison.OrdinalIgnoreCase) ||
                selectedEngineFilename.Equals("LnnLiveLearner_HybridTemporalMmf_v12.exe", StringComparison.OrdinalIgnoreCase);
            if (selectedCompactTemporalHybrid && mmfTransport)
            {
                SetStatus("Hybrid compact temporal uses the direct NinjaTrader pipe bridge. Choose MES — Every depth/trade event (not the MMF fast source).", Color.Firebrick);
                return;
            }
            if (selectedCompactTemporalHybridMmf && !mmfTransport)
            {
                SetStatus("Hybrid compact temporal MMF requires the MES or MNQ MMF event-stream instrument choice.", Color.Firebrick);
                return;
            }
            if (selectedOnnxEngine && (string.IsNullOrWhiteSpace(activeOnnxPackagePath) || !Directory.Exists(activeOnnxPackagePath)))
            {
                SetStatus("Select a compatible tick-augmented ONNX package in Model Registry before starting this engine.", Color.Firebrick);
                return;
            }
            if (directMmfReadOnlyEngine && !mmfTransport)
            {
                SetStatus("A direct-MMF learner requires the MNQ or MES MMF event-stream instrument choice.", Color.Firebrick);
                return;
            }
            // Each external plug-in gets its own pipe.  This avoids a second
            // dashboard or an old model process accidentally consuming frames.
            if (dashboardPipePlugin && mmfTransport)
            {
                pipeName += "Plugin_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                activePaperControlPipe = null;
                activeModelSavePipe = null;
            }
            string requestedLoadCheckpoint = loadCheckpointFile.Text.Trim();
            if (requestedLoadCheckpoint.Length > 0 && !File.Exists(requestedLoadCheckpoint))
            {
                SetStatus("Selected weight file does not exist. Choose a .bin checkpoint or clear Load weights.", Color.Firebrick);
                return;
            }
            if (requestedLoadCheckpoint.Length > 0 && (!nativeEngine || !SupportsSeparateLoadCheckpoint(selectedEngine)))
            {
                SetStatus("This engine does not use the native .bin checkpoint format. Start it without Load weights, or let that engine load its own model.", Color.Firebrick);
                return;
            }

            double roundTripFeeDollars;
            if (!double.TryParse(estimatedCostTicks.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out roundTripFeeDollars) || roundTripFeeDollars < 0.0)
            {
                SetStatus("Round-trip fee must be a non-negative dollar amount, such as 0.78.", Color.Firebrick);
                return;
            }
            double tickValueDollars = GetTickValueDollars(instrument.SelectedIndex, tickSize);
            double costTicks = roundTripFeeDollars / tickValueDollars;
            double minimumTargetTicks;
            if (!double.TryParse(minimumProfitTargetTicks.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out minimumTargetTicks) ||
                minimumTargetTicks <= 0.0 || minimumTargetTicks > 100.0)
            {
                SetStatus("Target ticks must be above 0 and no greater than 100, for example 1.00 or 2.00.", Color.Firebrick);
                return;
            }
            int minimumLabelDeadline;
            int maximumLabelDeadline;
            if (!int.TryParse(minimumLabelDeadlineMilliseconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minimumLabelDeadline) ||
                !int.TryParse(maximumLabelDeadlineMilliseconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maximumLabelDeadline) ||
                minimumLabelDeadline < 1 || maximumLabelDeadline < minimumLabelDeadline || maximumLabelDeadline > 60000)
            {
                SetStatus("Label deadline must be whole milliseconds: 1 <= minimum <= maximum <= 60000.", Color.Firebrick);
                return;
            }
            int reversalTrigger, reversalDistance, reversalTrend, reversalTimeout;
            double reversalThreshold;
            if (!int.TryParse(reversalTriggerTicks.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out reversalTrigger) ||
                !int.TryParse(reversalDistanceTicks.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out reversalDistance) ||
                !int.TryParse(reversalTrendTicks.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out reversalTrend) ||
                !int.TryParse(reversalTimeoutMilliseconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out reversalTimeout) ||
                !double.TryParse(reversalProbabilityThreshold.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out reversalThreshold) ||
                reversalTrigger < 1 || reversalDistance < 1 || reversalTrend < reversalTrigger || reversalTimeout < 100 || reversalTimeout > 60000 ||
                reversalThreshold <= 0.0 || reversalThreshold >= 1.0)
            {
                SetStatus("Reversal: positive ticks, trend >= trigger, timeout 100-60000 ms, threshold between 0 and 1.", Color.Firebrick);
                return;
            }
            double longThreshold, shortThreshold;
            if (!double.TryParse(paperLongThreshold.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out longThreshold) ||
                !double.TryParse(paperShortThreshold.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out shortThreshold) ||
                longThreshold <= 0.5 || longThreshold > 1.0 || shortThreshold < 0.0 || shortThreshold >= 0.5)
            {
                SetStatus("Use Long above 0.50 to 1.00 and Short from 0.00 to below 0.50, for example 0.60 / 0.40.", Color.Firebrick);
                return;
            }
            int processingInterval;
            if (!int.TryParse(processingIntervalMilliseconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out processingInterval) ||
                processingInterval < 0 || processingInterval > 1000)
            {
                SetStatus("Process ms must be a whole number from 0 to 1000. Use 5 for MES performance mode.", Color.Firebrick);
                return;
            }
            if (saveFullWeightAudit.Checked && !saveDashboardLog.Checked)
            {
                SetStatus("Full audit requires Save log to be enabled.", Color.Firebrick);
                return;
            }

            ResetMetrics();
            learner = new Process();
            string modelArguments = directUnifiedMmfLive
                ? string.Format(CultureInfo.InvariantCulture,
                    "--mes-map {0} --tick-size {1} --learning-rate 0.002 --target-ticks {2} --min-processing-interval-ms {3} --min-label-horizon-ms {4} --max-label-horizon-ms {5} --reversal-trigger-ticks {6} --reversal-distance-ticks {7} --reversal-trend-ticks {8} --reversal-timeout-ms {9} --reversal-probability-threshold {10} --enable-signals 1 --paper-long-threshold {11} --paper-short-threshold {12} --signal-mode {13}",
                    QuoteArgument(mmfMapName), tickSize, minimumTargetTicks, processingInterval, minimumLabelDeadline, maximumLabelDeadline, reversalTrigger, reversalDistance, reversalTrend, reversalTimeout, reversalThreshold.ToString("F4", CultureInfo.InvariantCulture), longThreshold, shortThreshold, SelectedSignalMode())
                : directMmfReadOnlyEngine
                ? string.Format(CultureInfo.InvariantCulture,
                    "--mmf-name {0} --tick-size {1} --horizon-events 40 --learning-rate 0.0015 --cost-ticks {2} --long-threshold 4.55 --short-threshold -4.55 --min-target-ticks 4 --max-target-ticks 8",
                    QuoteArgument(mmfMapName), tickSize, costTicks)
                : dashboardPipePlugin
                ? string.Format(CultureInfo.InvariantCulture, "--pipe {0} --tick-size {1}", pipeName, tickSize)
                : string.Format(CultureInfo.InvariantCulture,
                    "--pipe {0} --tick-size {1} --horizon-events 20 --learning-rate 0.002 --cost-ticks {2} --paper-long-threshold {3} --paper-short-threshold {4} --paper-sim-start-enabled 1 --min-processing-interval-ms {5} --minimum-profit-target-ticks {6} --min-label-horizon-ms {7} --max-label-horizon-ms {8} --reversal-trigger-ticks {9} --reversal-distance-ticks {10} --reversal-trend-ticks {11} --reversal-timeout-ms {12} --reversal-probability-threshold {13}", pipeName, tickSize, costTicks, longThreshold, shortThreshold, processingInterval, minimumTargetTicks, minimumLabelDeadline, maximumLabelDeadline, reversalTrigger, reversalDistance, reversalTrend, reversalTimeout, reversalThreshold.ToString("F4", CultureInfo.InvariantCulture));
            // v64s: the Target ticks box did not reach every engine. The read-only MMF
            // variant hardcodes "--min-target-ticks 4 --max-target-ticks 8" and the pipe
            // plugin variant passes no target at all, so on those selections the box was
            // silently ignored and the learner trained on its own default. These are
            // appended LAST and later arguments win, so the dashboard now always decides
            // what the learner is targeting, whatever engine is picked.
            modelArguments += " --minimum-profit-target-ticks " + minimumTargetTicks.ToString(CultureInfo.InvariantCulture) + LabelStopArgument();

            bool pluginMmfPipeAdapter = dashboardPipePlugin && mmfTransport;
            if (selectedCompactTemporalHybridMmf)
            {
                // Hybrid v12+ reads the DOM ring directly — no forwarder hop.
                if (mmfTransport && !string.IsNullOrWhiteSpace(mmfMapName))
                    modelArguments += " --mmf-map " + QuoteArgument(mmfMapName);
                else
                    modelArguments += " --pipe-client 1";
                // Cross-market shadow rings + signal mode (fusion when selected;
                // v12's other behavior maps to classic thresholds).
                modelArguments += " --es-map " + QuoteArgument(@"Local\LNN_DOM_MMF_ES") +
                    " --nq-map " + QuoteArgument(@"Local\LNN_DOM_MMF_NQ") +
                    " --vx-map " + QuoteArgument(@"Local\LNN_DOM_MMF_VX") +
                    " --spy-map " + QuoteArgument(@"Local\LNN_DOM_MMF_SPY") +
                    " --signal-mode " + (SelectedSignalMode() == "fusion" ? "fusion" : "thresholds") +
                    " --emit-per-event " + (emitPerEvent.Checked ? "1" : "0") +
                    " --dom-shift " + (domShift.Checked ? "1" : "0") +
                    " --coalesce-above " + ((int)ParseDoubleInput(coalesceAboveInput, 8)).ToString(CultureInfo.InvariantCulture);
                // Learning-rate override + validation stride (appended last so
                // they win over the defaults earlier in the argument string).
                double learnRateValue;
                if (double.TryParse(learningRateInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out learnRateValue) && learnRateValue > 0 && learnRateValue < 1)
                    modelArguments += " --learning-rate " + learnRateValue.ToString("R", CultureInfo.InvariantCulture);
                int strideValue;
                if (int.TryParse(validationStrideInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out strideValue) && strideValue >= 2)
                    modelArguments += " --validation-stride " + strideValue.ToString(CultureInfo.InvariantCulture);
                int restMsValue;
                if (int.TryParse(restingDepthInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out restMsValue) && restMsValue > 0)
                    modelArguments += " --resting-depth-ms " + restMsValue.ToString(CultureInfo.InvariantCulture);
                double smoothValue;
                if (double.TryParse(probSmoothingInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out smoothValue) && smoothValue > 0 && smoothValue < 1)
                    modelArguments += " --prob-smoothing " + smoothValue.ToString("R", CultureInfo.InvariantCulture);
                double regimeRange, regimeNovelty;
                if (double.TryParse(regimeMinRangeInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out regimeRange) && regimeRange > 0)
                    modelArguments += " --regime-min-range-ticks " + regimeRange.ToString("R", CultureInfo.InvariantCulture);
                if (double.TryParse(regimeMaxNoveltyInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out regimeNovelty) && regimeNovelty > 0)
                    modelArguments += " --regime-max-novelty " + regimeNovelty.ToString("R", CultureInfo.InvariantCulture);
                activeShadowControlPipe = pipeName + "ShadowControl"; // live shadow/fusion toggle target
                if (paperNoTimeout.Checked)
                    modelArguments += " --paper-no-timeout 1";
                if (paperMirror.Checked)
                {
                    // Paper mirror: entries arrive from the AI Trader through the paper-control
                    // pipe, and exits use the same bracket the strategy will place on a live fill.
                    modelArguments += " --paper-external 1 --paper-no-timeout 1" +
                        " --paper-stop-ticks " + ParseDoubleInput(ntDefSlInput, 0).ToString("R", CultureInfo.InvariantCulture) +
                        " --paper-target-ticks " + ParseDoubleInput(ntDefPtInput, 0).ToString("R", CultureInfo.InvariantCulture) +
                        " --paper-trail-ticks " + ParseDoubleInput(ntDefTrailInput, 0).ToString("R", CultureInfo.InvariantCulture);
                }
                if (reversalEnable.Checked)
                    modelArguments += " --enable-reversal-tracker 1";
                int paperMaxOpenVal;
                if (!paperMirror.Checked &&
                    int.TryParse(paperMaxOpenInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out paperMaxOpenVal) && paperMaxOpenVal > 1)
                    modelArguments += " --paper-max-open " + paperMaxOpenVal.ToString(CultureInfo.InvariantCulture);
                int paperLatVal;
                if (int.TryParse(paperEntryLatencyInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out paperLatVal) && paperLatVal > 0)
                    modelArguments += " --paper-entry-latency-ms " + paperLatVal.ToString(CultureInfo.InvariantCulture);
                int labelLatVal;
                if (int.TryParse(labelLatencyInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out labelLatVal) && labelLatVal > 0)
                    modelArguments += " --label-latency-ms " + labelLatVal.ToString(CultureInfo.InvariantCulture);
                if (pinEngineCore0.Checked)
                    modelArguments += " --affinity-core 0";
                if (hogEvidenceToEngine.Checked)
                    modelArguments += " --hog-evidence-map " + QuoteArgument(HogEvidenceMapName) + " --hog-fusion-mode " + (hogFusionModeSelector.SelectedIndex == 1 ? "live" : "shadow");
                if (hogAgreeGate.Checked)
                    modelArguments += " --hog-agree-gate 1";
                // Engine-direct orders: hand the engine the command ring so it
                // writes join/market intents itself (lowest latency). When on,
                // the dashboard's own forwarding is disabled below to avoid
                // double-firing.
                if (engineDirectOrders.Checked)
                {
                    string sym = ResolveTradeSymbol();
                    if (string.IsNullOrEmpty(sym))
                        Append("Engine-direct orders: no instrument symbol resolved — NOT enabling (select an instrument).");
                    else
                    {
                        string mode = engineOrderModeSelector.SelectedIndex == 1 ? "join" : "market";
                        int oq = (int)Math.Max(1, ParseDoubleInput(aiQty, 1));
                        double gap = ParseDoubleInput(minSecondsBetweenTradesInput, 0);
                        double hold = ParseDoubleInput(signalHoldSecondsInput, 0);
                        modelArguments += " --emit-orders 1 --order-mode " + mode +
                            " --order-cmd-map " + QuoteArgument(@"Local\LNN_ORDER_CMD_" + sym) +
                            " --order-qty " + oq.ToString(CultureInfo.InvariantCulture) +
                            " --order-trade-gap-s " + gap.ToString("R", CultureInfo.InvariantCulture) +
                            " --order-hold-s " + hold.ToString("R", CultureInfo.InvariantCulture);
                        engineDirectActive = true;
                        // v64p: the engine has no idea what ARM means - it writes orders to the
                        // command ring the moment it starts. Without this, ticking the box and
                        // pressing Start began LIVE trading with no ARM, and KILL SWITCH could
                        // not stop it. Hold the strategy in PAUSE (op 20, which rejects entries
                        // in SubmitEntryGated) until the user actually arms.
                        if (tradeCmdAccessor == null) ConnectTradeHub();
                        if (tradeCmdAccessor != null)
                        {
                            SendTradeCommand(TradeCmdOp.PauseExecution, 0, 0, 0, 0, 0, 0.0);
                            Append("Engine-direct orders ON (" + (mode == "join" ? "JOIN via executioner" : "MARKET — immediate, pays spread") +
                                "). The strategy is held PAUSED until you ARM; Disarm and KILL SWITCH pause it again. " +
                                "Dashboard forwarding is suppressed to avoid double-fire. Dashboard guards (cooldown, session window, max trades, direction filter) do NOT apply to engine-direct orders - the strategy's max position, orders/minute, daily loss and trading hours do.");
                        }
                        else
                        {
                            engineDirectActive = false;
                            Append("Engine-direct orders NOT enabled: could not connect the command ring, so the strategy could not be held paused. " +
                                "Start the NT8 strategy first, then restart the engine.");
                            modelArguments = modelArguments.Substring(0, modelArguments.IndexOf(" --emit-orders 1", StringComparison.Ordinal));
                        }
                    }
                }
            }
            if (!selectedCompactTemporalHybridMmf && !directUnifiedMmfLive && !dashboardPipePlugin)
                Append("NOTE: engine file '" + selectedEngineFilename + "' is not in the hybrid (v12) list, so the Sim-tab options (paper latency, label latency, no-timeout, max-open, reversal, engine-direct, resting-depth, prob-smoothing, regime gate) were NOT sent. Name the exe LnnLiveLearner_HybridTemporalMmf_v12.exe to enable them.");
            if (!directUnifiedMmfLive && !string.IsNullOrWhiteSpace(mmfMapName) && !pluginMmfPipeAdapter)
                modelArguments += " --mmf-map " + QuoteArgument(mmfMapName);
            string savePath = null;
            string logPath = null;
            string checkpointPath = null;
            string startupCheckpointPath = requestedLoadCheckpoint;
            string bestCheckpointPath = null;
            string manualCheckpointPath = null;
            string learningLogPath = null;
            string weightUpdateLogPath = null;
            string paperTradeLogPath = null;
            string teacherExamplePath = null;
            string teacherCandidatePath = null;
            activeWeightUpdateLogPath = null;
            bool denoisedTwoHead = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_DenoisedTwoHead.exe", StringComparison.OrdinalIgnoreCase);
            bool orderFlowVolume = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_OrderFlowVolume.exe", StringComparison.OrdinalIgnoreCase);
            bool adaptiveHorizon = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_AdaptiveHorizon.exe", StringComparison.OrdinalIgnoreCase);
            bool nestedCellsTimeTarget = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_NestedCellsTimeTarget.exe", StringComparison.OrdinalIgnoreCase);
            bool mmfNestedCells = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_MmfNestedCells.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(selectedEngine.Path).Equals("LnnLiveLearner_MmfNestedCells_Optimized.exe", StringComparison.OrdinalIgnoreCase);
            bool mmfThreeOutcome = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_MmfThreeOutcome.exe", StringComparison.OrdinalIgnoreCase);
            bool mmfThreeHorizon = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_MmfThreeHorizon.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(selectedEngine.Path).Equals("LnnLiveLearner_MmfThreeHorizon_AdaptiveRisk.exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(selectedEngine.Path).Equals("LnnLiveLearner_MmfThreeHorizon_MacroBars.exe", StringComparison.OrdinalIgnoreCase);
            bool mmfThreeHorizonAdaptiveRisk = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_MmfThreeHorizon_AdaptiveRisk.exe", StringComparison.OrdinalIgnoreCase);
            bool mmfThreeHorizonMacroBars = Path.GetFileName(selectedEngine.Path).Equals(
                "LnnLiveLearner_MmfThreeHorizon_MacroBars.exe", StringComparison.OrdinalIgnoreCase);
            string modelVariant = directUnifiedMmfLive ? "_mmf_unified_shadow" : directMmfReadOnlyEngine ? "_mmf_130feature_readonly" : mmfThreeHorizonMacroBars ? "_mmf_three_horizon_macro_bars" : mmfThreeHorizonAdaptiveRisk ? "_mmf_three_horizon_adaptive_risk" : mmfThreeHorizon ? "_mmf_three_horizon" : mmfThreeOutcome ? "_mmf_three_outcome" : mmfNestedCells ? "_mmf_nested_cells" : nestedCellsTimeTarget ? "_nested_cells_time_target" : adaptiveHorizon ? "_orderflow_adaptive_horizon" : denoisedTwoHead ? "_denoised_twohead" : "_learner";
            string manualFolder = captureFolder.Text.Trim();
            if (nativeEngine && manualFolder.Length > 0)
            {
                string manualSymbol = instrument.SelectedIndex == 1 ? "mes" : instrument.SelectedIndex == 2 ? "mes_events" :
                    instrument.SelectedIndex == 3 ? "mnq" : instrument.SelectedIndex == 4 ? "mnq_events" : instrument.SelectedIndex == 5 ? "es" : instrument.SelectedIndex == 6 ? "m6e_events" : instrument.SelectedIndex == 7 ? "mnq_mmf" : instrument.SelectedIndex == 8 ? "mes_mmf" : "m6e";
                string modelPrefix = manualSymbol + modelVariant;
                manualCheckpointPath = Path.Combine(manualFolder, modelPrefix + "_manual_snapshot.bin");
                modelArguments += " --manual-checkpoint-file " + QuoteArgument(manualCheckpointPath);
            }
            if (saveRawFrames.Checked || saveDashboardLog.Checked || saveWeights.Checked || selectedCompactTemporalHybrid || selectedCompactTemporalHybridMmf)
            {
                string folder = captureFolder.Text.Trim();
                if (folder.Length == 0)
                {
                    SetStatus("Choose a capture folder or turn off saving.", Color.Firebrick);
                    learner.Dispose();
                    learner = null;
                    return;
                }
                try
                {
                    // The MMF forwarder writes complete 392-byte snapshots:
                    // timestamp fields plus bid/ask price and size at levels 0-9.
                    // Create this before launching it so capture never silently
                    // fails merely because a new folder has not been made yet.
                    Directory.CreateDirectory(folder);
                }
                catch (Exception error)
                {
                    SetStatus("Could not create historical capture folder: " + error.Message, Color.Firebrick);
                    learner.Dispose();
                    learner = null;
                    return;
                }
                string symbol = instrument.SelectedIndex == 1 ? "mes" : instrument.SelectedIndex == 2 ? "mes_events" :
                    instrument.SelectedIndex == 3 ? "mnq" : instrument.SelectedIndex == 4 ? "mnq_events" : instrument.SelectedIndex == 5 ? "es" : instrument.SelectedIndex == 6 ? "m6e_events" : instrument.SelectedIndex == 7 ? "mnq_mmf" : instrument.SelectedIndex == 8 ? "mes_mmf" : "m6e";
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                if (selectedCompactTemporalHybridMmf && saveWeights.Checked)
                    modelArguments += " --novelty-checkpoint-dir " + QuoteArgument(folder);
                if (selectedCompactTemporalHybrid || selectedCompactTemporalHybridMmf)
                {
                    teacherExamplePath = Path.Combine(folder, symbol + "_compact_temporal_examples_" + timestamp + ".csv");
                    teacherCandidatePath = Path.Combine(folder, symbol + "_compact_temporal_candidate_" + timestamp + ".txt");
                    modelArguments += " --teacher-example-file " + QuoteArgument(teacherExamplePath) +
                        " --teacher-candidate-file " + QuoteArgument(teacherCandidatePath);
                }
                if (selectedCompactTemporalHybridMmf && recordDomEvents.Checked && mmfTransport)
                {
                    lastDomEventsPath = Path.Combine(folder, symbol + "_dom_events_" + timestamp + ".bin");
                    modelArguments += " --save-events " + QuoteArgument(lastDomEventsPath);
                }
                if ((nativeEngine || pluginMmfPipeAdapter) && saveRawFrames.Checked)
                {
                    savePath = Path.Combine(folder, symbol + "_depth10_historical_" + timestamp + ".bin");
                    if (nativeEngine)
                        modelArguments += " --save-file " + QuoteArgument(savePath);
                    else if (selectedAdaptiveOnnxEngine)
                        // The adaptive engine receives exactly the frames it
                        // learns from, so it is the authoritative capture
                        // writer.  The forwarder stays transport-only.
                        modelArguments += " --save-file " + QuoteArgument(savePath);
                }
                if (saveDashboardLog.Checked)
                {
                    logPath = Path.Combine(folder, symbol + "_dashboard_" + timestamp + ".log");
                    if (directUnifiedMmfLive)
                    {
                        learningLogPath = Path.Combine(folder, symbol + "_live_mmf_learning_" + timestamp + ".csv");
                        string shadowLogPath = Path.Combine(folder, symbol + "_live_mmf_shadow_confluence_" + timestamp + ".csv");
                        modelArguments += " --learning-log-file " + QuoteArgument(learningLogPath);
                        modelArguments += " --shadow-log-file " + QuoteArgument(shadowLogPath);
                    }
                    else if (nativeEngine)
                    {
                        learningLogPath = Path.Combine(folder, symbol + "_learning_updates_" + timestamp + ".csv");
                        paperTradeLogPath = Path.Combine(folder, symbol + "_paper_trades_" + timestamp + ".csv");
                        modelArguments += " --learning-log-file " + QuoteArgument(learningLogPath);
                        modelArguments += " --paper-trade-log-file " + QuoteArgument(paperTradeLogPath);
                        if (saveFullWeightAudit.Checked)
                        {
                            weightUpdateLogPath = Path.Combine(folder, symbol + "_feature_weight_updates_" + timestamp + ".csv");
                            activeWeightUpdateLogPath = weightUpdateLogPath;
                            modelArguments += " --weight-update-log-file " + QuoteArgument(weightUpdateLogPath);
                        }
                    }
                    try
                    {
                        Directory.CreateDirectory(folder);
                        logWriter = new StreamWriter(logPath, false);
                        logWriter.AutoFlush = true;
                    }
                    catch (Exception error)
                    {
                        SetStatus("Could not create dashboard log: " + error.Message, Color.Firebrick);
                        CloseLogWriter();
                        learner.Dispose();
                        learner = null;
                        return;
                    }
                }
                if ((nativeEngine || directMmfReadOnlyEngine) && saveWeights.Checked)
                {
                    string modelPrefix = symbol + modelVariant;
                    // A checkpoint is a session artifact, not a single mutable
                    // "latest" file.  This prevents a later weak session from
                    // overwriting earlier learned state.
                    checkpointPath = Path.Combine(folder, modelPrefix + "_checkpoint_" + timestamp + ".bin");
                    modelArguments += " --checkpoint-file " + QuoteArgument(checkpointPath);
                    if (nativeEngine)
                    {
                        bestCheckpointPath = Path.Combine(folder, modelPrefix + "_best_validated_" + timestamp + ".bin");
                        modelArguments += " --best-checkpoint-file " + QuoteArgument(bestCheckpointPath);
                    }
                }
            }
            if (nativeEngine && startupCheckpointPath.Length > 0)
                modelArguments += " --load-checkpoint-file " + QuoteArgument(startupCheckpointPath);
            if (selectedEngine.NeedsRuntime)
            {
                string runtime = pythonExecutable.Text.Trim();
                if (runtime.Length == 0)
                {
                    SetStatus("Choose a Python or Mojo runtime before starting this script.", Color.Firebrick);
                    CloseLogWriter();
                    learner.Dispose();
                    learner = null;
                    return;
                }
                learner.StartInfo.FileName = runtime;
                learner.StartInfo.Arguments = selectedEngine.IsMojoScript
                    ? "run " + QuoteArgument(learnerPath) + " " + modelArguments
                    : QuoteArgument(learnerPath) + " " + modelArguments;
            }
            else
            {
                learner.StartInfo.FileName = learnerPath;
                learner.StartInfo.Arguments = modelArguments;
            }
            learner.StartInfo.WorkingDirectory = Path.GetDirectoryName(learnerPath);
            learner.StartInfo.UseShellExecute = false;
            learner.StartInfo.CreateNoWindow = true;
            learner.StartInfo.RedirectStandardOutput = true;
            learner.StartInfo.RedirectStandardError = true;
            string liveWeightRoot = activeWeightRoot.Text.Trim();
            string liveWeightCategory = SafeCategory(weightCategory.Text);
            string selectedLiveProfile = trainingProfile.SelectedItem == null ? "MultiTask LNN" : trainingProfile.SelectedItem.ToString();
            string liveWeightProfile = selectedLiveProfile == "Hidden Tick Depth CfC-v10"
                ? "hidden-tick-depth-cfc-v10" : selectedLiveProfile == "Tick-augmented MultiTask LNN"
                ? "tick-augmented-multitask-lnn" : "mes-multitask-lnn";
            if (Directory.Exists(liveWeightRoot))
            {
                learner.StartInfo.EnvironmentVariables["LNN_WEIGHT_ROOT"] = liveWeightRoot;
                learner.StartInfo.EnvironmentVariables["LNN_WEIGHT_CATEGORY"] = liveWeightCategory;
                learner.StartInfo.EnvironmentVariables["LNN_WEIGHT_PROFILE"] = liveWeightProfile;
                learner.StartInfo.EnvironmentVariables["LNN_AUTO_ADOPT"] = autoAdoptActiveWeights.Checked ? "1" : "0";
                learner.StartInfo.EnvironmentVariables["LNN_EXPECTED_SYMBOL"] = trainingSymbol.Text.Trim();
                learner.StartInfo.EnvironmentVariables["LNN_EXPECTED_TICK_SIZE"] = tickSize.ToString("R", CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrWhiteSpace(activeOnnxPackagePath) && Directory.Exists(activeOnnxPackagePath))
            {
                learner.StartInfo.EnvironmentVariables["LNN_ONNX_PACKAGE"] = activeOnnxPackagePath;
                learner.StartInfo.EnvironmentVariables["LNN_ONNX_EXPECTED_SYMBOL"] = SelectedInstrumentSymbol();
                learner.StartInfo.EnvironmentVariables["LNN_ONNX_EXPECTED_TICK_SIZE"] = tickSize.ToString("R", CultureInfo.InvariantCulture);
            }
            string liveEngineFilename = Path.GetFileName(learnerPath);
            bool onnxEngine = liveEngineFilename.Equals("multitask_onnx_mmf_engine.py", StringComparison.OrdinalIgnoreCase) ||
                liveEngineFilename.Equals("multitask_live_adaptive_mmf_engine.py", StringComparison.OrdinalIgnoreCase);
            if (onnxEngine)
            {
                string sourceDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                string onnxPackages = Path.Combine(sourceDir, "onnx_python_packages");
                if (!Directory.Exists(onnxPackages)) onnxPackages = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "onnx_python_packages");
                string torchPackages = Path.GetFullPath(Path.Combine(sourceDir, "..", "torch_runtime"));
                string oldPythonPath = learner.StartInfo.EnvironmentVariables["PYTHONPATH"];
                string pythonPath = Directory.Exists(onnxPackages) ? onnxPackages : string.Empty;
                if (Directory.Exists(torchPackages)) pythonPath += (pythonPath.Length == 0 ? string.Empty : Path.PathSeparator.ToString()) + torchPackages;
                if (!string.IsNullOrWhiteSpace(oldPythonPath)) pythonPath += (pythonPath.Length == 0 ? string.Empty : Path.PathSeparator.ToString()) + oldPythonPath;
                if (pythonPath.Length > 0) learner.StartInfo.EnvironmentVariables["PYTHONPATH"] = pythonPath;
            }
            learner.EnableRaisingEvents = true;
            learner.OutputDataReceived += LearnerOutput;
            learner.ErrorDataReceived += LearnerError;
            learner.Exited += LearnerExited;

            try
            {
                if (pluginMmfPipeAdapter && !selectedCompactTemporalHybridMmf) // hybrid reads the ring itself now
                    StartPluginMmfForwarder(mmfMapName, pipeName, tickSize, selectedAdaptiveOnnxEngine ? null : savePath);
                lock (telemetryThrottleLock) { learnerStderrTail.Clear(); }
                learnerStartedUtc = DateTime.UtcNow;
                learner.Start();
                // Hot path gets CPU priority; diagnostics run below normal so
                // they can never steal cycles from the live pipeline.
                try { learner.PriorityClass = ProcessPriorityClass.High; } catch { }
                learner.BeginOutputReadLine();
                learner.BeginErrorReadLine();
                // Lean live build: the compact teacher, temporal observer and
                // cross-market shadow logger are disabled — they only produced
                // side telemetry/log files and extra processes.
                start.Enabled = false;
                stop.Enabled = true;
                startPaperSimulation.Enabled = true;
                stopPaperSimulation.Enabled = false;
                saveModelNow.Enabled = manualCheckpointPath != null;
                instrument.Enabled = false;
                engine.Enabled = false;
                browseEngine.Enabled = false;
                pythonExecutable.Enabled = false;
                browsePython.Enabled = false;
                tickSizeInput.Enabled = false;
                estimatedCostTicks.Enabled = false;
                minimumProfitTargetTicks.Enabled = false;
                minimumLabelDeadlineMilliseconds.Enabled = false;
                maximumLabelDeadlineMilliseconds.Enabled = false;
                reversalTriggerTicks.Enabled = false;
                reversalDistanceTicks.Enabled = false;
                reversalTrendTicks.Enabled = false;
                reversalTimeoutMilliseconds.Enabled = false;
                reversalProbabilityThreshold.Enabled = false;
                observerFastTicks.Enabled = false;
                observerFastMilliseconds.Enabled = false;
                observerMediumTicks.Enabled = false;
                observerMediumMilliseconds.Enabled = false;
                observerSlowTicks.Enabled = false;
                observerSlowMilliseconds.Enabled = false;
                paperLongThreshold.Enabled = false;
                paperShortThreshold.Enabled = false;
                processingIntervalMilliseconds.Enabled = false;
                saveRawFrames.Enabled = false;
                saveDashboardLog.Enabled = false;
                saveFullWeightAudit.Enabled = false;
                saveWeights.Enabled = false;
                loadCheckpointFile.Enabled = false;
                browseLoadCheckpoint.Enabled = false;
                captureFolder.Enabled = false;
                browseCapture.Enabled = false;
                trainingProfile.Enabled = false;
                weightCategory.Enabled = false;
                trainingHorizonSeconds.Enabled = false;
                trainingSymbol.Enabled = false;
                trainingInstrumentId.Enabled = false;
                activeWeightRoot.Enabled = false;
                browseActiveWeightRoot.Enabled = false;
                autoAdoptActiveWeights.Enabled = false;
                trainPythonProfile.Enabled = false;
                SetStatus("Started " + selectedEngine.Name + (mmfTransport ? ". Connecting to the shared-memory market stream..." : ". Requesting the NinjaTrader bridge..."), Color.DarkGoldenrod);
                if (pluginMmfPipeAdapter && selectedCompactTemporalHybridMmf)
                    Append("Direct shared-memory transport: " + mmfMapName + " is read directly by the hybrid engine (no forwarder, no pipe).");
                else if (pluginMmfPipeAdapter)
                    Append("Dashboard plug-in transport active: " + mmfMapName + " is being forwarded to \\.\\pipe\\" + pipeName + ". The selected engine receives standard 392-byte instrumented frames through --pipe and does not open MMF itself.");
                else if (mmfTransport) Append("MMF transport active: " + mmfMapName + ". The learner reads NinjaTrader depth/trade events directly from shared memory; no external pipe bridge is used.");
                if (nativeEngine)
                {
                    Append("Performance mode: heavy model work every " + processingInterval.ToString(CultureInfo.InvariantCulture) + " ms; all incoming frames are still drained. Adaptive label deadline: " + minimumLabelDeadline.ToString(CultureInfo.InvariantCulture) + "-" + maximumLabelDeadline.ToString(CultureInfo.InvariantCulture) + " ms, target 20 recent events, minimum target " + minimumTargetTicks.ToString("F2", CultureInfo.InvariantCulture) + " ticks (raised automatically in high volatility), round-trip fee $" + roundTripFeeDollars.ToString("F2", CultureInfo.InvariantCulture) + " = " + costTicks.ToString("F3", CultureInfo.InvariantCulture) + " ticks at $" + tickValueDollars.ToString("F3", CultureInfo.InvariantCulture) + "/tick, charged once per completed paper trade. Paper thresholds: long >= " + longThreshold.ToString("F2", CultureInfo.InvariantCulture) + ", short <= " + shortThreshold.ToString("F2", CultureInfo.InvariantCulture) + ".");
                    Append("Paper simulation is disabled until you click Start paper sim. Entries follow the model interval; an open internal paper position is checked on every valid DOM snapshot. It is not Sim101 or broker trading.");
                }
                else if (directUnifiedMmfLive)
                    Append("Direct-MMF live learner active: reads MES plus optional ES/NQ/VX/SPY maps directly. Absorption, cross-market lead/lag, and reversal fusion are shadow diagnostics only; there is no bridge, paper simulator, signal/control map, or order path.");
                else if (directMmfReadOnlyEngine)
                    Append("130-feature read-only learner active: reads " + mmfMapName + " directly, keeps its own virtual paper outcome state, and never creates signal/control MMF or sends orders.");
                else
                    Append("Plug-in contract: the dashboard owns capture and transport. This engine receives only --pipe and --tick-size; it owns its own learning rule, checkpoints, metrics, and any paper logic.");
                if (Directory.Exists(liveWeightRoot))
                    Append("Weight policy available to the main engine through environment: category=" + liveWeightCategory + ", profile=" + liveWeightProfile + ", auto-adopt=" + (autoAdoptActiveWeights.Checked ? "on" : "off") + ". A compatible engine reloads only its matching active.pt after a valid full-book boundary.");
                if (!string.IsNullOrWhiteSpace(activeOnnxPackagePath))
                    Append("ONNX package selected: " + activeOnnxPackagePath + ". It is passed through LNN_ONNX_PACKAGE; the selected engine must implement ONNX Runtime loading.");
                if (savePath != null) Append("Saving full ten-level bid/ask historical snapshots to " + savePath);
                if (logPath != null) Append("Saving dashboard log to " + logPath);
                if (learningLogPath != null) Append("Saving every weight update to " + learningLogPath);
                if (weightUpdateLogPath != null) Append("Saving all " + (nestedCellsTimeTarget || mmfNestedCells || mmfThreeOutcome || mmfThreeHorizon ? "392" : adaptiveHorizon || orderFlowVolume ? "344" : denoisedTwoHead ? "296" : "222") + ((mmfThreeOutcome || mmfThreeHorizon) ? " medium-horizon UP-head" : " direction-head") + " feature weights plus bias for every update to " + weightUpdateLogPath + " (large file; use only when diagnosing).");
                if (paperTradeLogPath != null) Append("Saving internal paper-trade results to " + paperTradeLogPath + " (not a broker or Sim101 order log).");
                if (selectedCompactTemporalHybrid || selectedCompactTemporalHybridMmf) Append("Hybrid mode: native 222-feature temporal inference stays on the DOM loop; the Python teacher validates compatible candidate heads in the background. A candidate needs a positive chronological holdout edge before the native learner adopts it.");
                if (startupCheckpointPath.Length > 0) Append("Loading selected startup weights from " + startupCheckpointPath + ". New learning will save to " + (checkpointPath ?? "no checkpoint file") + ".");
                else if (checkpointPath != null) Append("Loading and saving learned weights at " + checkpointPath);
                if (bestCheckpointPath != null) Append("Saving a separate validated-best checkpoint at " + bestCheckpointPath + " after at least 50 held-out labels; it is only replaced when rolling validation edge beats its majority baseline.");
                if (manualCheckpointPath != null) Append("Save model now writes the current learner state to " + manualCheckpointPath + " without replacing latest or validated-best weights.");
                if (!mmfTransport)
                    BeginInvoke((MethodInvoker)delegate { SendBridgeCommand('S'); });
            }
            catch (Exception error)
            {
                Append("Could not start learner: " + error.Message);
                SetStatus("Could not start receiver.", Color.Firebrick);
                StopLearner();
            }
        }

        private void StopClick(object sender, EventArgs e)
        {
            StopAiTrader();
            SendBridgeCommand('T');
            StopLearner();
            SetStatus("Stopped.", Color.DimGray);
        }

        private void StartPluginMmfForwarder(string mapName, string outputPipe, double tickSize, string saveFile)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LnnDashboardPluginForwarder.exe");
            // Packaged builds keep the forwarder beside the dashboard.  The
            // development dashboard is built in dashboard_horizon_build while
            // the shared MMF transport remains in outputs\ninjatrader8\bin.
            if (!File.Exists(path))
                path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "bin", "LnnDashboardPluginForwarder.exe"));
            if (!File.Exists(path))
            {
                // No external forwarder available — use the built-in MMF→pipe
                // transport instead. It publishes the same 392-byte v2 frames.
                StopPluginMmfForwarder();
                StartBuiltInForwarder(mapName, outputPipe);
                return;
            }
            StopPluginMmfForwarder();
            mmfForwarder = new Process();
            mmfForwarder.StartInfo.FileName = path;
            mmfForwarder.StartInfo.Arguments = "--forward-only 1 --mmf-map " + QuoteArgument(mapName) +
                " --pipe " + QuoteArgument(outputPipe) + " --forward-output-pipe " + QuoteArgument(outputPipe) +
                " --tick-size " + tickSize.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(saveFile))
                mmfForwarder.StartInfo.Arguments += " --save-file " + QuoteArgument(saveFile);
            mmfForwarder.StartInfo.WorkingDirectory = Path.GetDirectoryName(path);
            mmfForwarder.StartInfo.UseShellExecute = false;
            mmfForwarder.StartInfo.CreateNoWindow = true;
            mmfForwarder.Start();
            try { mmfForwarder.PriorityClass = ProcessPriorityClass.High; } catch { } // transport is on the hot path
        }

        private void StopPluginMmfForwarder()
        {
            StopBuiltInForwarder();
            Process local = mmfForwarder;
            mmfForwarder = null;
            if (local == null) return;
            try { if (!local.HasExited) local.Kill(); }
            catch (InvalidOperationException) { }
            local.Dispose();
        }

        // ====================================================================
        // Built-in MMF→pipe forwarder (replaces LnnDashboardPluginForwarder.exe
        // when that helper is not present). Reads LnnDomMmfWriter's event ring,
        // maintains the 10-level book, and serves v2 instrumented frames
        // (8-byte header + 392-byte payload) on the named pipe that pipe-client
        // engines such as HybridTemporalMmf v12 connect to.
        // ====================================================================

        private Thread builtInForwarderThread;
        private CancellationTokenSource builtInForwarderCts;
        private NamedPipeServerStream builtInForwarderPipe;

        private void StartBuiltInForwarder(string mapName, string outputPipe)
        {
            builtInForwarderCts = new CancellationTokenSource();
            CancellationToken ct = builtInForwarderCts.Token;
            string localMap = mapName, localPipe = outputPipe;
            builtInForwarderThread = new Thread(delegate() { BuiltInForwarderLoop(localMap, localPipe, ct); }) { IsBackground = true, Priority = ThreadPriority.Highest };
            builtInForwarderThread.Start();
            Append("Built-in MMF transport active: " + mapName + " → \\\\.\\pipe\\" + outputPipe +
                " (LnnDashboardPluginForwarder.exe not found beside the dashboard; using the dashboard's internal forwarder).");
        }

        private void StopBuiltInForwarder()
        {
            try { builtInForwarderCts?.Cancel(); } catch { }
            NamedPipeServerStream pipe = builtInForwarderPipe;
            builtInForwarderPipe = null;
            try { pipe?.Dispose(); } catch { }
            if (builtInForwarderThread != null && builtInForwarderThread.IsAlive)
            {
                try { builtInForwarderThread.Join(1000); } catch { }
            }
            builtInForwarderThread = null;
            builtInForwarderCts = null;
        }

        private static void PutInt64(byte[] buffer, int offset, long value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 8);
        }

        private static void PutDouble(byte[] buffer, int offset, double value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 8);
        }

        private void BuiltInForwarderLoop(string mapName, string pipeName, CancellationToken ct)
        {
            double[] bidPrice = new double[10], askPrice = new double[10];
            long[] bidVolume = new long[10], askVolume = new long[10];
            byte[] frame = new byte[400];
            frame[0] = 0x4C; frame[1] = 0x4E; frame[2] = 0x4E; frame[3] = 0x31; // "LNN1"
            frame[4] = 2; frame[5] = 0;   // frame version 2 (with latency timestamps)
            frame[6] = 4; frame[7] = 0;   // flags: full instrumented book present
            long frameSequence = 0, readSequence = 0;
            int capacity = 0;
            MemoryMappedFile map = null;
            MemoryMappedViewAccessor view = null;
            NamedPipeServerStream pipe = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (view == null)
                        {
                            try
                            {
                                map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                                view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                                capacity = view.ReadInt32(8);
                                int recordSize = view.ReadInt32(12);
                                if (capacity < 2 || (capacity & (capacity - 1)) != 0 || recordSize != 48)
                                    throw new InvalidOperationException("Unexpected ring layout.");
                                readSequence = view.ReadInt64(0);
                            }
                            catch
                            {
                                try { view?.Dispose(); } catch { }
                                try { map?.Dispose(); } catch { }
                                view = null; map = null;
                                if (ct.IsCancellationRequested) break;
                                Thread.Sleep(1000);
                                continue;
                            }
                        }
                        if (pipe == null)
                        {
                            pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                            builtInForwarderPipe = pipe;
                            pipe.WaitForConnection(); // unblocked by Dispose on stop
                        }

                        long writeSequence = view.ReadInt64(0);
                        if (writeSequence < readSequence) readSequence = writeSequence;          // ring was reset (bridge reloaded)
                        if (writeSequence - readSequence > capacity) readSequence = writeSequence - capacity; // overrun
                        bool bookChanged = false;
                        long eventUtcTicks = 0, callbackUtcTicks = 0;
                        while (readSequence < writeSequence)
                        {
                            long next = readSequence + 1;
                            long offset = 64 + (next & (capacity - 1)) * 48;
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
                            if (side < 2 && position >= 0 && position < 10)
                            {
                                if (side == 0)
                                {
                                    if (operation == 2) { bidPrice[position] = 0; bidVolume[position] = 0; }
                                    else { bidPrice[position] = price; bidVolume[position] = Math.Max(0, volume); }
                                }
                                else
                                {
                                    if (operation == 2) { askPrice[position] = 0; askVolume[position] = 0; }
                                    else { askPrice[position] = price; askVolume[position] = Math.Max(0, volume); }
                                }
                                bookChanged = true;
                                eventUtcTicks = eventTime;
                                callbackUtcTicks = callbackTime;
                            }
                        }
                        if (bookChanged && bidPrice[0] > 0 && askPrice[0] > bidPrice[0])
                        {
                            frameSequence++;
                            PutInt64(frame, 8, frameSequence);
                            PutInt64(frame, 16, eventUtcTicks);
                            PutInt64(frame, 24, callbackUtcTicks);
                            PutInt64(frame, 32, DateTime.UtcNow.Ticks);
                            for (int i = 0; i < 10; i++)
                            {
                                int bidOffset = 72 + 16 * i, askOffset = 72 + 160 + 16 * i;
                                PutDouble(frame, bidOffset, bidPrice[i]);
                                PutInt64(frame, bidOffset + 8, bidVolume[i]);
                                PutDouble(frame, askOffset, askPrice[i]);
                                PutInt64(frame, askOffset + 8, askVolume[i]);
                            }
                            pipe.Write(frame, 0, frame.Length);
                            forwarderLastBid = (float)bidPrice[0];
                            forwarderLastAsk = (float)askPrice[0];
                        }
                        else
                            Thread.Sleep(1);
                    }
                    catch
                    {
                        // Pipe client disconnected or the view broke: recycle the
                        // pipe and let the loop re-establish state.
                        try { pipe?.Dispose(); } catch { }
                        pipe = null;
                        builtInForwarderPipe = null;
                        if (ct.IsCancellationRequested) break;
                        Thread.Sleep(200);
                    }
                }
            }
            finally
            {
                try { pipe?.Dispose(); } catch { }
                try { view?.Dispose(); } catch { }
                try { map?.Dispose(); } catch { }
                builtInForwarderPipe = null;
            }
        }

        private void StartCompactTemporalTeacher(string examplesPath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(examplesPath) || string.IsNullOrWhiteSpace(candidatePath)) return;
            // Packaged builds keep the trainer beside the dashboard; the
            // development tree keeps it one directory above.
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "compact_temporal_background_trainer.py");
            if (!File.Exists(script))
                script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "compact_temporal_background_trainer.py"));
            if (!File.Exists(script)) throw new FileNotFoundException("Compact temporal background trainer was not found.", script);
            StopCompactTemporalTeacher();
            compactTeacher = new Process();
            compactTeacher.StartInfo.FileName = pythonExecutable.Text.Trim();
            compactTeacher.StartInfo.Arguments = QuoteArgument(script) + " --examples " + QuoteArgument(examplesPath) + " --candidate " + QuoteArgument(candidatePath);
            compactTeacher.StartInfo.WorkingDirectory = Path.GetDirectoryName(script);
            compactTeacher.StartInfo.UseShellExecute = false;
            compactTeacher.StartInfo.CreateNoWindow = true;
            compactTeacher.StartInfo.RedirectStandardOutput = true;
            compactTeacher.StartInfo.RedirectStandardError = true;
            compactTeacher.OutputDataReceived += delegate(object sender, DataReceivedEventArgs line)
            {
                if (!string.IsNullOrWhiteSpace(line.Data) && !IsDisposed)
                    BeginInvoke((MethodInvoker)delegate { shadowTeacherStatus = line.Data; RefreshShadowTeacherSummary(); AppendShadowTeacherEvent("compact teacher: " + line.Data); Append("compact teacher: " + line.Data, false); });
            };
            compactTeacher.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs line)
            {
                if (!string.IsNullOrWhiteSpace(line.Data) && !IsDisposed)
                    BeginInvoke((MethodInvoker)delegate { Append("compact teacher error: " + line.Data); });
            };
            compactTeacher.Start();
            try { compactTeacher.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // background trainer must never compete
            compactTeacher.BeginOutputReadLine();
            compactTeacher.BeginErrorReadLine();
        }

        private void StopCompactTemporalTeacher()
        {
            Process local = compactTeacher;
            compactTeacher = null;
            if (local == null) return;
            try { if (!local.HasExited) local.Kill(); }
            catch (InvalidOperationException) { }
            local.Dispose();
        }

        private void StartTemporalObserver(string mapName, double tickSize)
        {
            // Packaged builds keep the observer beside the dashboard; the
            // development tree keeps it one directory above.
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temporal_self_consistency_mmf_mes_observer.py");
            if (!File.Exists(script))
                script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "temporal_self_consistency_mmf_mes_observer.py"));
            if (!File.Exists(script) || string.IsNullOrWhiteSpace(pythonExecutable.Text)) return;
            double fastTicks, mediumTicks, slowTicks;
            int fastMilliseconds, mediumMilliseconds, slowMilliseconds;
            if (!TryReadObserverOptions(out fastTicks, out fastMilliseconds, out mediumTicks, out mediumMilliseconds, out slowTicks, out slowMilliseconds))
                return;
            StopTemporalObserver();
            temporalObserverMarketStatus = "Starting isolated temporal observer on " + mapName + ".";
            temporalObserverFastStatus = "FAST head: waiting for labels.";
            temporalObserverMediumStatus = "MEDIUM head: waiting for labels.";
            temporalObserverSlowStatus = "SLOW head: waiting for labels.";
            RefreshTemporalObserverSummary();
            temporalObserver = new Process();
            temporalObserver.StartInfo.FileName = pythonExecutable.Text.Trim();
            temporalObserver.StartInfo.Arguments = QuoteArgument(script) + " --mmf-map " + QuoteArgument(mapName) + " --tick-size " + tickSize.ToString(CultureInfo.InvariantCulture) +
                " --fast-ticks " + fastTicks.ToString(CultureInfo.InvariantCulture) + " --fast-ms " + fastMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " --medium-ticks " + mediumTicks.ToString(CultureInfo.InvariantCulture) + " --medium-ms " + mediumMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " --slow-ticks " + slowTicks.ToString(CultureInfo.InvariantCulture) + " --slow-ms " + slowMilliseconds.ToString(CultureInfo.InvariantCulture);
            temporalObserver.StartInfo.WorkingDirectory = Path.GetDirectoryName(script);
            temporalObserver.StartInfo.UseShellExecute = false;
            temporalObserver.StartInfo.CreateNoWindow = true;
            temporalObserver.StartInfo.RedirectStandardOutput = true;
            temporalObserver.StartInfo.RedirectStandardError = true;
            temporalObserver.OutputDataReceived += TemporalObserverOutput;
            temporalObserver.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs line)
            {
                if (!string.IsNullOrWhiteSpace(line.Data) && !IsDisposed)
                    BeginInvoke((MethodInvoker)delegate { AppendTemporalObserverEvent("error: " + line.Data); });
            };
            temporalObserver.Start();
            try { temporalObserver.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // diagnostic only
            temporalObserver.BeginOutputReadLine();
            temporalObserver.BeginErrorReadLine();
        }

        private bool TryReadObserverOptions(out double fastTicks, out int fastMilliseconds, out double mediumTicks, out int mediumMilliseconds, out double slowTicks, out int slowMilliseconds)
        {
            fastTicks = mediumTicks = slowTicks = 0.0;
            fastMilliseconds = mediumMilliseconds = slowMilliseconds = 0;
            bool valid = double.TryParse(observerFastTicks.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out fastTicks) && fastTicks > 0.0 &&
                int.TryParse(observerFastMilliseconds.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out fastMilliseconds) && fastMilliseconds >= 100 &&
                double.TryParse(observerMediumTicks.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out mediumTicks) && mediumTicks > 0.0 &&
                int.TryParse(observerMediumMilliseconds.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mediumMilliseconds) && mediumMilliseconds >= 100 &&
                double.TryParse(observerSlowTicks.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out slowTicks) && slowTicks > 0.0 &&
                int.TryParse(observerSlowMilliseconds.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out slowMilliseconds) && slowMilliseconds >= 100;
            if (!valid)
            {
                temporalObserverSummary.Text = "Observer not started: every head needs a positive tick target and a deadline of at least 100 ms.\r\n" +
                    "Use decimal points (for example 1.00) and whole milliseconds.";
                AppendTemporalObserverEvent("observer options invalid; no observer started");
            }
            return valid;
        }

        private void StopTemporalObserver()
        {
            Process local = temporalObserver;
            temporalObserver = null;
            if (local == null) return;
            try { if (!local.HasExited) local.Kill(); }
            catch (InvalidOperationException) { }
            local.Dispose();
        }

        private void StartCrossMarketShadowLogger(string captureDirectory)
        {
            if (String.IsNullOrWhiteSpace(captureDirectory)) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CrossMarketShadowLogger.exe");
            if (!File.Exists(path))
            {
                // Optional research CSV recorder; unrelated to live inference or
                // the MMF read path. The merged engine reads the cross-market
                // rings itself, so its absence is normal — note it quietly once.
                AppendShadowTeacherEvent("Optional cross-market CSV recorder not present (not required; engine reads cross-market rings directly).");
                return;
            }
            Directory.CreateDirectory(captureDirectory);
            StopCrossMarketShadowLogger();
            string outputPath = Path.Combine(captureDirectory,
                "mes_cross_market_shadow_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            crossMarketShadowLogger = new Process();
            crossMarketShadowLogger.StartInfo.FileName = path;
            crossMarketShadowLogger.StartInfo.Arguments = "--output " + QuoteArgument(outputPath) +
                " --mes-map " + QuoteArgument(@"Local\LNN_DOM_MMF_MES") +
                " --es-map " + QuoteArgument(@"Local\LNN_DOM_MMF_ES") +
                " --nq-map " + QuoteArgument(@"Local\LNN_DOM_MMF_NQ") +
                " --vx-map " + QuoteArgument(@"Local\LNN_DOM_MMF_VX") +
                " --spy-map " + QuoteArgument(@"Local\LNN_DOM_MMF_SPY");
            crossMarketShadowLogger.StartInfo.WorkingDirectory = Path.GetDirectoryName(path);
            crossMarketShadowLogger.StartInfo.UseShellExecute = false;
            crossMarketShadowLogger.StartInfo.CreateNoWindow = true;
            crossMarketShadowLogger.StartInfo.RedirectStandardOutput = true;
            crossMarketShadowLogger.StartInfo.RedirectStandardError = true;
            crossMarketShadowLogger.OutputDataReceived += delegate(object sender, DataReceivedEventArgs line)
            {
                if (!String.IsNullOrWhiteSpace(line.Data) && !IsDisposed && IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { AppendShadowTeacherEvent(line.Data); });
            };
            crossMarketShadowLogger.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs line)
            {
                if (!String.IsNullOrWhiteSpace(line.Data) && !IsDisposed && IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { AppendShadowTeacherEvent("cross-market error: " + line.Data); });
            };
            crossMarketShadowLogger.Start();
            crossMarketShadowLogger.BeginOutputReadLine();
            crossMarketShadowLogger.BeginErrorReadLine();
            AppendShadowTeacherEvent("cross-market shadow started: ES/NQ/VX/SPY -> MES. Log: " + outputPath +
                ". It cannot change the primary learner, signals, weights, or orders.");
        }

        private void StopCrossMarketShadowLogger()
        {
            Process local = crossMarketShadowLogger;
            crossMarketShadowLogger = null;
            if (local == null) return;
            try { if (!local.HasExited) local.Kill(); }
            catch (InvalidOperationException) { }
            local.Dispose();
        }

        private void StartAiTraderClick(object sender, EventArgs e) { StartAiTrader(); }
        private void StopAiTraderClick(object sender, EventArgs e) { StopAiTrader(); }

        // Forwards the AI paper signal to the NT8 command MMF ring
        // (Local\LNN_ORDER_CMD_<symbol>) consumed by LnnOrderExecStrategy.
        // Only acts while the AI Trader is armed and the ring is connected,
        // and only on a signal CHANGE, never on repeats of the same signal.
        private void ForwardPaperSignalToNt8(string signal)
        {
            RecordSignalSample(signal);
            lastSignalSeen = signal; lastSignalSeenUtc = DateTime.UtcNow;
            // When the engine is writing orders directly, the dashboard must NOT
            // forward — otherwise every signal fires twice. Keep the timeline
            // sample above so the UI still shows signals.
            if (engineDirectActive) { NoteDecision(signal, "not forwarded - engine-direct orders are on (the engine writes to the ring itself)"); return; }
            // The decision chain below is the ONE place a trade is decided. It runs when the
            // AI Trader is armed (real orders) and/or when the paper sim is mirroring it, so
            // both books get the same trade from the same guards.
            bool live = tradeArmed && tradeCmdAccessor != null;
            bool mirror = paperMirror.Checked && !string.IsNullOrEmpty(activePaperControlPipe);
            if (!live && !mirror)
            {
                NoteDecision(signal, tradeArmed ? "not forwarded - command ring not connected" : "not forwarded - AI Trader is not armed (and paper mirror is off)");
                return;
            }
            if (signal != "LONG" && signal != "SHORT" && signal != "NO-TRADE") { NoteDecision(signal, "not actionable (" + signal + " - engine warming up: LONG/SHORT start after 100 labels resolve)"); return; }
            // Position for the decision: NT8's when live, the engine's open paper trade otherwise.
            int pos = live ? tradeNetPosition : paperOpenSide;
            // Hog agreement gate (dashboard-forwarded path): entries need fresh, agreeing hog evidence.
            if (hogAgreeGate.Checked && (signal == "LONG" || signal == "SHORT"))
            {
                bool fresh = !double.IsNaN(lastHogProbability) && (DateTime.UtcNow - lastHogProbabilityUtc).TotalMilliseconds <= 2000;
                bool agree = fresh && ((signal == "LONG" && lastHogProbability > 0.5) || (signal == "SHORT" && lastHogProbability < 0.5));
                if (!agree)
                {
                    if ((DateTime.UtcNow - lastHogGateLogUtc).TotalSeconds >= 5)
                    { lastHogGateLogUtc = DateTime.UtcNow; Append("Hog gate: " + signal + " entry not forwarded — " + (fresh ? "hog disagrees (P(up) " + lastHogProbability.ToString("F3", CultureInfo.InvariantCulture) + ")" : "no fresh hog evidence (is the hog running?)")); }
                    lastDecisionText = signal + ": blocked by hog agreement gate (" + (fresh ? "hog disagrees" : "no fresh hog evidence") + ")"; lastDecisionUtc = DateTime.UtcNow;
                    return;
                }
            }

            // Session time window: outside it, no entries — and flatten once.
            if (!InsideSessionWindow())
            {
                if (!sessionWindowFlattened)
                {
                    sessionWindowFlattened = true;
                    if (pos != 0)
                    {
                        SendFlatBoth(live);
                        Append("AI Trader: outside session window — flattened; entries paused until the window opens.");
                    }
                    else
                        Append("AI Trader: outside session window — entries paused.");
                }
                lastForwardedAiSignal = signal;
                NoteDecision(signal, "outside the session window " + sessionStartInput.Text.Trim() + "-" + sessionEndInput.Text.Trim() + " - entries paused");
                return;
            }
            sessionWindowFlattened = false;

            // v64x: dead-regime gate. Blocks ENTRIES only - flattens and exits still
            // pass, so an open position is never trapped by it.
            if (neutralRegimeBlock && (signal == "LONG" || signal == "SHORT"))
            {
                NoteDecision(signal, "not entered - model has had no directional read (dead-regime gate); waiting for conviction to hold " +
                    NeutralClearSeconds.ToString("0", CultureInfo.InvariantCulture) + "s");
                return;
            }

            if (string.Equals(signal, lastForwardedAiSignal, StringComparison.Ordinal)) return; // repeat of the signal already acted on

            // Minimum signal-hold: an ENTRY signal must persist before it acts.
            // NO-TRADE (flatten) is never delayed.
            double holdSeconds = ParseDoubleInput(signalHoldSecondsInput, 0);
            if (holdSeconds > 0 && signal != "NO-TRADE")
            {
                if (!string.Equals(signal, pendingEntrySignal, StringComparison.Ordinal))
                {
                    pendingEntrySignal = signal;
                    pendingEntrySinceUtc = DateTime.UtcNow;
                    lastDecisionText = signal + ": holding - must persist " + holdSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s before it acts"; lastDecisionUtc = DateTime.UtcNow;
                    return;
                }
                if ((DateTime.UtcNow - pendingEntrySinceUtc).TotalSeconds < holdSeconds) return;
            }
            pendingEntrySignal = null;
            lastForwardedAiSignal = signal;

            // Regime guard: too many signal flips inside a 10-minute window
            // means the learner cannot read the current market regime.
            DateTime nowUtc = DateTime.UtcNow;
            aiSignalFlipTimes.Add(nowUtc);
            aiSignalFlipTimes.RemoveAll(t => (nowUtc - t).TotalMinutes > 10.0);
            int maxFlips;
            if (!int.TryParse(regimeMaxFlips.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFlips) || maxFlips <= 0)
                maxFlips = 8;
            if (aiSignalFlipTimes.Count > maxFlips)
            {
                NoteDecision(signal, "regime guard tripped (" + aiSignalFlipTimes.Count + " flips in 10 min) - auto-disarmed");
                AutoDisarm("Regime guard: " + aiSignalFlipTimes.Count.ToString(CultureInfo.InvariantCulture) +
                    " signal flips in 10 minutes (limit " + maxFlips.ToString(CultureInfo.InvariantCulture) +
                    "). The learner looks confused about the current market regime.");
                return;
            }

            // Max-trades guard
            int maxTrades;
            if (!int.TryParse(maxTradesPerSession.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxTrades) || maxTrades <= 0)
                maxTrades = int.MaxValue;
            if (signal != "NO-TRADE" && aiEntriesThisSession >= maxTrades)
            {
                NoteDecision(signal, "max trades per session reached (" + maxTrades + ") - auto-disarmed");
                AutoDisarm("Max trades per session reached (" + maxTrades.ToString(CultureInfo.InvariantCulture) + ").");
                return;
            }

            // Trade-gap throttle: minimum spacing between entries, whatever the
            // signal does in between. Flattens are never delayed.
            double tradeGap = ParseDoubleInput(minSecondsBetweenTradesInput, 0);
            if (signal != "NO-TRADE" && tradeGap > 0 && (DateTime.UtcNow - lastEntryUtc).TotalSeconds < tradeGap)
            {
                lastForwardedAiSignal = null; // let this signal retry once the gap has passed
                lastDecisionText = signal + ": waiting for the trade gap (" + tradeGap.ToString("0", CultureInfo.InvariantCulture) + " s since last entry)"; lastDecisionUtc = DateTime.UtcNow;
                return;
            }

            // Cooldown after a losing trade blocks new entries (flatten still works).
            if (signal != "NO-TRADE" && DateTime.UtcNow < cooldownUntilUtc)
            {
                Append("AI Trader: entry skipped — post-loss cooldown for another " +
                    ((cooldownUntilUtc - DateTime.UtcNow).TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "s.");
                lastDecisionText = signal + ": post-loss cooldown"; lastDecisionUtc = DateTime.UtcNow;
                return;
            }

            // Confidence margin: real entries need probability beyond the paper
            // thresholds by the configured extra amount.
            double margin = ParseDoubleInput(liveConfidenceMarginInput, 0);
            if (margin > 0 && signal != "NO-TRADE")
            {
                double longThresholdValue = ParseDoubleInput(paperLongThreshold, 0.60);
                double shortThresholdValue = ParseDoubleInput(paperShortThreshold, 0.40);
                bool confident = signal == "LONG"
                    ? latestProbability >= longThresholdValue + margin
                    : latestProbability <= shortThresholdValue - margin;
                if (!confident)
                {
                    Append("AI Trader: " + signal + " skipped — probability " + latestProbability.ToString("F4", CultureInfo.InvariantCulture) +
                        " inside the live-margin band.");
                    lastDecisionText = signal + ": skipped - probability " + latestProbability.ToString("F3", CultureInfo.InvariantCulture) + " inside the live-margin band"; lastDecisionUtc = DateTime.UtcNow;
                    return;
                }
            }

            int qty;
            if (!int.TryParse(aiQty.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out qty) || qty <= 0)
                qty = 1;
            // Trade-size ramp: start at 1 contract, +1 per N wins, capped at AI qty.
            int rampStep = (int)ParseDoubleInput(rampWinsPerStepInput, 0);
            if (rampStep > 0) qty = Math.Min(qty, 1 + liveWins / rampStep);

            double trail = 0.0;
            double trailTicksValue, tickSizeValue;
            if (double.TryParse(aiTrailTicks.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out trailTicksValue) && trailTicksValue > 0.0 &&
                double.TryParse(tickSizeInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tickSizeValue) && tickSizeValue > 0.0)
                trail = trailTicksValue * tickSizeValue;

            // An unfilled entry that the signal no longer wants must not keep resting
            // (that is how two entries end up working at once when the signal returns).
            if (live && pos == 0 && stratWorkingEntries > 0 && (DateTime.UtcNow - stratHeartbeatUtc).TotalSeconds < 4 &&
                (signal == "NO-TRADE" || (signal == "LONG" && lastForwardedAiSignal == "SHORT") || (signal == "SHORT" && lastForwardedAiSignal == "LONG")))
            {
                SendTradeCommand(TradeCmdOp.CancelEntries, 0, 0, 0, 0, 0, 0.0);
                Append("AI Trader: signal is now " + signal + " while " + stratWorkingEntries + " entry order(s) still rest unfilled — cancelling them.");
                entrySentUtc = DateTime.MinValue;
            }
            if (signal == "NO-TRADE")
            {
                if (BarrierMode)
                    NoteDecision(signal, pos != 0 ? "strategy-bracket mode: holding the open trade to its stop / target (NO-TRADE does not flatten)" : "strategy-bracket mode: flat, waiting for LONG/SHORT");
                else if (pos != 0)
                {
                    SendFlatBoth(live);
                    Append("AI Trader → NT8 MMF: NO-TRADE, flattening position.");
                    NoteDecision(signal, "flatten sent (position " + pos + ")");
                }
                else NoteDecision(signal, "nothing to do (flat)");
            }
            else if (signal == "LONG")
            {
                // Barrier mode: an open short runs to its barrier unless flipping is on.
                if (BarrierMode && pos < 0 && !flipOnOpposite.Checked)
                { NoteDecision(signal, "strategy-bracket mode: holding the open SHORT to its stop / target (flip on opposite signal is off)"); return; }
                // Direction filter: only take the skilled side. A blocked-side
                // signal still flattens any open position — never adds on it.
                if (pos < 0) SendFlatBoth(live);
                if (tradeDirectionSelector.SelectedIndex == 2) // shorts only → don't enter long
                {
                    if (pos != 0) Append("AI Trader: LONG signal but direction filter = shorts only — not entering (flattened any short).");
                    NoteDecision(signal, "not entered - direction filter is shorts only");
                }
                else if (pos <= 0 && live && EntryInFlight() != null)
                {
                    NoteDecision(signal, "not entered - " + EntryInFlight() + " (one trade at a time)");
                    lastForwardedAiSignal = null;   // re-evaluate once it resolves
                }
                else if (pos <= 0)
                {
                    bool limitEntry = entryOrderTypeSelector.SelectedIndex == 1;
                    if (live)
                    {
                        SendTradeCommand(TradeCmdOp.SignalContext, 1, latestProbability, 0, 0, 0, 0.0);
                        SendTradeCommand(limitEntry ? TradeCmdOp.BuyLimitJoin : TradeCmdOp.BuyMarket, qty, 0, 0, 0, 0, trail);
                        MarkEntrySent();
                    }
                    SendPaperCommand('L');
                    if (BarrierMode && live) OpenBarrier(+1);
                    aiEntriesThisSession++;
                    lastEntryUtc = DateTime.UtcNow;
                    entryTimestamps.Add(DateTime.UtcNow);
                    Append("AI Trader → " + (live ? "NT8 MMF" : "paper only") + ": LONG, " + (limitEntry ? "buy limit at bid" : "buy market") + " qty " + qty.ToString(CultureInfo.InvariantCulture) + (mirror ? " (paper mirrors it)" : "") + ".");
                    NoteDecision(signal, (live ? "FORWARDED as " + (limitEntry ? "buy limit join" : "buy market") + " x" + qty + " (cmd " + lastCmdSentText.Split(new char[] { ' ' })[0] + ")" : "PAPER-ONLY entry (AI Trader not armed)") + (mirror ? " + paper" : ""));
                }
                else NoteDecision(signal, "not entered - already counts a LONG position (" + pos + ")" + (live ? "; if NT8 is actually flat, Stop and re-Start the AI Trader to reset" : ""));
            }
            else // SHORT
            {
                if (BarrierMode && pos > 0 && !flipOnOpposite.Checked)
                { NoteDecision(signal, "strategy-bracket mode: holding the open LONG to its stop / target (flip on opposite signal is off)"); return; }
                if (pos > 0) SendFlatBoth(live);
                if (tradeDirectionSelector.SelectedIndex == 1) // longs only → don't enter short
                {
                    if (pos != 0) Append("AI Trader: SHORT signal but direction filter = longs only — not entering (flattened any long).");
                    NoteDecision(signal, "not entered - direction filter is longs only");
                }
                else if (pos >= 0 && live && EntryInFlight() != null)
                {
                    NoteDecision(signal, "not entered - " + EntryInFlight() + " (one trade at a time)");
                    lastForwardedAiSignal = null;
                }
                else if (pos >= 0)
                {
                    bool limitEntry = entryOrderTypeSelector.SelectedIndex == 1;
                    if (live)
                    {
                        SendTradeCommand(TradeCmdOp.SignalContext, 1, latestProbability, 0, 0, 0, 0.0);
                        SendTradeCommand(limitEntry ? TradeCmdOp.SellLimitJoin : TradeCmdOp.SellMarket, qty, 0, 0, 0, 0, trail);
                        MarkEntrySent();
                    }
                    SendPaperCommand('S');
                    if (BarrierMode && live) OpenBarrier(-1);
                    aiEntriesThisSession++;
                    lastEntryUtc = DateTime.UtcNow;
                    entryTimestamps.Add(DateTime.UtcNow);
                    Append("AI Trader → " + (live ? "NT8 MMF" : "paper only") + ": SHORT, " + (limitEntry ? "sell limit at ask" : "sell market") + " qty " + qty.ToString(CultureInfo.InvariantCulture) + (mirror ? " (paper mirrors it)" : "") + ".");
                    NoteDecision(signal, (live ? "FORWARDED as " + (limitEntry ? "sell limit join" : "sell market") + " x" + qty + " (cmd " + lastCmdSentText.Split(new char[] { ' ' })[0] + ")" : "PAPER-ONLY entry (AI Trader not armed)") + (mirror ? " + paper" : ""));
                }
                else NoteDecision(signal, "not entered - already counts a SHORT position (" + pos + ")" + (live ? "; if NT8 is actually flat, Stop and re-Start the AI Trader to reset" : ""));
            }
            PlayAlert();
        }

        // ==================== v64k strategy-bracket exits ====================
        // The strategy owns the exits (its DefaultStopLossTicks / ProfitTarget /
        // Trail bracket, placed in NT8 on every fill). The dashboard only tracks
        // that a bracketed trade is open so the same signal can re-enter once it
        // closes, exactly like the paper sim's back-to-back trades.
        // Non-null while a previous entry is still in flight (sent, not yet filled) or NT8
        // still has an unfilled entry order resting - no second entry may be sent.
        private string EntryInFlight()
        {
            if (entrySentUtc != DateTime.MinValue && (DateTime.UtcNow - entrySentUtc).TotalSeconds < 10) return "entry #" + entrySentCmdId + " sent " + Age(entrySentUtc) + " and not filled yet";
            if (stratWorkingEntries > 0 && (DateTime.UtcNow - stratHeartbeatUtc).TotalSeconds < 4) return stratWorkingEntries + " entry order(s) still working in NT8";
            return null;
        }

        private void MarkEntrySent()
        {
            entrySentUtc = DateTime.UtcNow; entrySentCmdId = lastCmdSentId;
        }

        // Paper mirror control: 'L' open long, 'S' open short, 'F' flatten. Same pipe the
        // paper start/stop buttons use; the engine applies the AI Trader's decision verbatim.
        // v64k latency fix: this used to open and CONNECT a named pipe synchronously on the
        // decision thread, which on a reversal sat between the live flatten and the live entry.
        // The paper book is a measurement, never a reason to delay a real order - so the command
        // is queued and delivered by a background thread. Live orders are already on the wire.
        private readonly Queue<char> paperCommandQueue = new Queue<char>();
        private readonly object paperQueueLock = new object();
        private Thread paperCommandThread;

        private void SendPaperCommand(char command)
        {
            if (!paperMirror.Checked || string.IsNullOrEmpty(activePaperControlPipe)) return;
            lock (paperQueueLock)
            {
                paperCommandQueue.Enqueue(command);
                if (paperCommandThread == null || !paperCommandThread.IsAlive)
                {
                    paperCommandThread = new Thread(PaperCommandPump) { IsBackground = true, Name = "LNN paper-mirror" };
                    paperCommandThread.Start();
                }
                Monitor.Pulse(paperQueueLock);
            }
        }

        private void PaperCommandPump()
        {
            while (true)
            {
                char cmd; string pipe = activePaperControlPipe;
                lock (paperQueueLock)
                {
                    while (paperCommandQueue.Count == 0)
                        if (!Monitor.Wait(paperQueueLock, 5000)) return;   // idle out; re-created on demand
                    cmd = paperCommandQueue.Dequeue();
                }
                if (string.IsNullOrEmpty(pipe)) continue;
                try
                {
                    using (NamedPipeClientStream control = new NamedPipeClientStream(".", pipe, PipeDirection.Out))
                    {
                        control.Connect(500);
                        control.WriteByte((byte)cmd);
                        control.Flush();
                    }
                }
                catch (Exception error)
                {
                    char failed = cmd; string msg = error.Message;
                    try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { Append("Paper mirror: '" + failed + "' not delivered to the engine (" + msg + ")."); }); } catch { }
                }
            }
        }

        // Flatten both books from one decision, so paper never diverges from live.
        private void SendFlatBoth(bool live)
        {
            if (live) SendTradeCommand(TradeCmdOp.Flat, 0, 0, 0, 0, 0, 0.0);
            SendPaperCommand('F');
        }

        private void OpenBarrier(int side)
        {
            barrierOpenSide = side;
            Append("Entry sent; the strategy attaches its bracket on the fill (SL " + (stratRiskUtc == DateTime.MinValue ? "?" : stratSlTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t") +
                " / PT " + (stratRiskUtc == DateTime.MinValue ? "?" : stratPtTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t") + "). No time limit; NO-TRADE will not flatten.");
        }

        // Called from the fill reader when the tracked position returns to flat.
        private void BarrierClosed(string how)
        {
            if (barrierOpenSide == 0) return;
            barrierOpenSide = 0;
            lastForwardedAiSignal = null;   // same signal may re-enter (paper-sim behaviour), guards permitting
            Append("Bracketed trade closed: " + how + " — flat. Re-entry allowed while the signal persists (trade gap / cooldown / max trades still apply).");
            RefreshTradePath();
        }

        // Heartbeat record type 6: the strategy's live risk configuration.
        private void ProcessStrategyRiskConfig(TradeFillPayload r)
        {
            stratRiskUtc = DateTime.UtcNow;
            stratSlTicks = r.Price; stratPtTicks = r.Qty / 100.0; stratTrailTicks = r.FilledQty / 100.0;
            stratMaxPos = r.OrderState; stratOrdersPerMin = r.OrderAction; stratDailyLoss = r.NtOrderIdLow / 100.0; stratHoursSet = r.MarketPosition == 1;
            RefreshTradePath();
        }

        private string StrategyExitText()
        {
            if (stratRiskUtc == DateTime.MinValue) return "exits unknown (no risk-config heartbeat yet - strategy older than v64k?)";
            string sl = stratSlTicks > 0 ? "SL " + stratSlTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t" : "NO STOP";
            string pt = stratPtTicks > 0 ? "PT " + stratPtTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t" : "no target";
            string tr = stratTrailTicks > 0 ? " trail " + stratTrailTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t" : "";
            return "exits " + sl + " / " + pt + tr + " | max pos " + stratMaxPos + " | " + stratOrdersPerMin + "/min | daily loss " + (stratDailyLoss > 0 ? stratDailyLoss.ToString("0.##", CultureInfo.InvariantCulture) + "t" : "OFF") + (stratHoursSet ? "" : " | hours: always");
        }

        private static double ParseDoubleInput(TextBox input, double fallback)
        {
            double value;
            return double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private bool InsideSessionWindow()
        {
            TimeSpan start, end;
            if (!TimeSpan.TryParse(sessionStartInput.Text.Trim(), CultureInfo.InvariantCulture, out start) ||
                !TimeSpan.TryParse(sessionEndInput.Text.Trim(), CultureInfo.InvariantCulture, out end))
                return true; // blank or invalid = no restriction
            TimeSpan now = DateTime.Now.TimeOfDay;
            return start <= end ? now >= start && now <= end : now >= start || now <= end; // overnight windows wrap
        }

        private void RecordSignalSample(string signal)
        {
            DateTime now = DateTime.UtcNow;
            if (signalTimeline.Count == 0 || signalTimeline[signalTimeline.Count - 1].Value != signal)
                signalTimeline.Add(new KeyValuePair<DateTime, string>(now, signal));
            if (signalTimeline.Count > 4000) signalTimeline.RemoveRange(0, 1000);
            signalStrip.Invalidate();
        }

        private void PaintSignalStrip(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            DateTime now = DateTime.UtcNow, windowStart = now.AddMinutes(-60);
            int w = signalStrip.ClientSize.Width, h = signalStrip.ClientSize.Height;
            if (signalTimeline.Count == 0)
            {
                TextRenderer.DrawText(e.Graphics, "No signals yet.", Font, new Point(6, 2), Color.DimGray);
                return;
            }
            for (int i = 0; i < signalTimeline.Count; i++)
            {
                DateTime segmentStart = signalTimeline[i].Key;
                DateTime segmentEnd = i + 1 < signalTimeline.Count ? signalTimeline[i + 1].Key : now;
                if (segmentEnd < windowStart) continue;
                if (segmentStart < windowStart) segmentStart = windowStart;
                int x1 = (int)((segmentStart - windowStart).TotalSeconds / 3600.0 * w);
                int x2 = (int)((segmentEnd - windowStart).TotalSeconds / 3600.0 * w);
                Color c = signalTimeline[i].Value == "LONG" ? Color.MediumSeaGreen :
                          signalTimeline[i].Value == "SHORT" ? Color.IndianRed :
                          signalTimeline[i].Value == "NO-TRADE" ? Color.NavajoWhite : Color.Gainsboro;
                using (SolidBrush b = new SolidBrush(c))
                    e.Graphics.FillRectangle(b, x1, 0, Math.Max(1, x2 - x1), h);
            }
        }

        // Round-trip P/L from executions: maintains average entry price and
        // records a completed AI trade whenever the position returns to flat.
        private void TrackLiveTradeFill(int signedQty, double price)
        {
            double tickSize = ParseDoubleInput(tickSizeInput, 0.25);
            if (tickSize <= 0) tickSize = 0.25;
            int previous = fillTrackedPosition;
            int next = previous + signedQty;
            if (previous == 0 || Math.Sign(previous) == Math.Sign(signedQty))
            {
                // Opening or adding
                int prevAbs = Math.Abs(previous), addAbs = Math.Abs(signedQty);
                fillAveragePrice = prevAbs + addAbs > 0 ? (fillAveragePrice * prevAbs + price * addAbs) / (prevAbs + addAbs) : price;
                if (previous == 0) { fillOpenTradeTicks = 0; fillEntryPrice = price; }
            }
            else
            {
                int closed = Math.Min(Math.Abs(signedQty), Math.Abs(previous));
                fillOpenTradeTicks += (price - fillAveragePrice) / tickSize * Math.Sign(previous) * closed;
                if (next == 0)
                    RecordLiveTrade(previous > 0 ? "LONG" : "SHORT", fillEntryPrice, price, fillOpenTradeTicks);
                else if (Math.Sign(next) != Math.Sign(previous))
                {
                    // Reversal: close old trade, open new at this price
                    RecordLiveTrade(previous > 0 ? "LONG" : "SHORT", fillEntryPrice, price, fillOpenTradeTicks);
                    fillAveragePrice = price; fillEntryPrice = price; fillOpenTradeTicks = 0;
                }
            }
            fillTrackedPosition = next;
            UpdatePositionLabel();
        }

        // v64o: bring the tally's position counter back in line with NT8 when the guard
        // counter is resynced. Without this the two drift apart and the tally silently
        // stops counting: RecordLiveTrade only fires when fillTrackedPosition reaches zero
        // (or flips sign), so a counter that is off by even one contract never closes a
        // trade again. If NT8 says we are flat while we were tracking an open trade, the
        // trade IS counted - with its exit marked estimated, because the closing fill is
        // one we never saw and its real price is unknowable from here.
        private void ReconcileTallyPosition(int stratPosition)
        {
            if (fillTrackedPosition == stratPosition) return;

            int wasTracking = fillTrackedPosition;
            if (wasTracking != 0 && stratPosition == 0)
            {
                double tickSize = ParseDoubleInput(tickSizeInput, 0.25); if (tickSize <= 0) tickSize = 0.25;
                double exit = lastKnownFillPrice > 0 ? lastKnownFillPrice : fillAveragePrice;
                double ticks = fillOpenTradeTicks;
                if (exit > 0 && fillAveragePrice > 0)
                    ticks += (exit - fillAveragePrice) / tickSize * Math.Sign(wasTracking) * Math.Abs(wasTracking);
                tallyPriceEstimated = true;
                RecordLiveTrade(wasTracking > 0 ? "LONG" : "SHORT", fillEntryPrice, exit, ticks);
            }
            else
            {
                Append("Tally resync: NT8 position is " + stratPosition + " but the W/L tracker had " + wasTracking +
                       ". Adopting NT8" + (wasTracking != 0 ? " - the trade in progress could not be closed out and is not counted." : "."));
            }

            fillTrackedPosition = stratPosition;
            fillOpenTradeTicks = 0;
            if (stratPosition == 0) { fillEntryPrice = 0; fillAveragePrice = 0; }
            else
            {
                // Adopting a position means we never saw the fills that opened it, so any
                // average price we are holding belongs to an earlier, unrelated trade -
                // always reseed. Keeping the stale value scored the adopted trade against
                // the previous trade's entry.
                double seed = lastKnownFillPrice > 0 ? lastKnownFillPrice : MidOrZero();
                if (seed > 0) { fillAveragePrice = seed; fillEntryPrice = seed; }
                tallyPriceEstimated = true;
            }
        }

        private double MidOrZero()
        {
            double bid = forwarderLastBid, ask = forwarderLastAsk;
            return (bid > 0 && ask > bid) ? (bid + ask) / 2.0 : 0.0;
        }

        private void RecordLiveTrade(string direction, double entryPrice, double exitPrice, double netTicks)
        {
            liveTrades++;
            liveNetTicks += netTicks;
            bool win = netTicks > 0;
            if (win) { liveWins++; consecutiveLosses = 0; }
            else
            {
                liveLosses++; consecutiveLosses++;
                double cooldown = ParseDoubleInput(cooldownSecondsInput, 0);
                if (cooldown > 0)
                {
                    cooldownUntilUtc = DateTime.UtcNow.AddSeconds(cooldown);
                    Append("AI Trader: losing trade — cooldown " + cooldown.ToString("0", CultureInfo.InvariantCulture) + "s before the next entry.");
                }
            }
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " +
                direction.PadRight(5) + "  " + entryPrice.ToString("0.00###", CultureInfo.InvariantCulture) +
                " → " + exitPrice.ToString("0.00###", CultureInfo.InvariantCulture) +
                "  " + (netTicks >= 0 ? "+" : "") + netTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t" +
                "  cum " + (liveNetTicks >= 0 ? "+" : "") + liveNetTicks.ToString("0.##", CultureInfo.InvariantCulture) + "t" +
                "  signal " + (lastForwardedAiSignal ?? "?") +
                // v64o: say so when the ticks came from a reconstructed price rather than a
                // fill we actually saw. The W/L is right; the tick figure is an estimate.
                (tallyPriceEstimated ? "  [ticks estimated - reconciled from NT8, not from a fill record]" : "");
            tallyPriceEstimated = false;
            tradeHistoryBox.Text = tradeHistoryBox.Text.StartsWith("No completed", StringComparison.Ordinal)
                ? line
                : line + "\r\n" + tradeHistoryBox.Text;
            UpdateTallyLabel();
            UpdateDivergenceLabel();

            int maxConsecutive = (int)ParseDoubleInput(maxConsecutiveLossesInput, 0);
            if (maxConsecutive > 0 && consecutiveLosses >= maxConsecutive)
                AutoDisarm(consecutiveLosses.ToString(CultureInfo.InvariantCulture) + " consecutive losing trades (limit " +
                    maxConsecutive.ToString(CultureInfo.InvariantCulture) + ").");
        }

        private void UpdateTallyLabel()
        {
            double limit = ParseDoubleInput(dailyLossLimitTicks, 0);
            tallyLabel.Text = "Session: " + liveTrades.ToString(CultureInfo.InvariantCulture) + " trades | W " +
                liveWins.ToString(CultureInfo.InvariantCulture) + " / L " + liveLosses.ToString(CultureInfo.InvariantCulture) +
                " | net " + (liveNetTicks >= 0 ? "+" : "") + liveNetTicks.ToString("0.##", CultureInfo.InvariantCulture) + " ticks" +
                (limit > 0 ? " (loss limit -" + limit.ToString("0.##", CultureInfo.InvariantCulture) + ")" : "") +
                " | loss streak " + consecutiveLosses.ToString(CultureInfo.InvariantCulture);
            tallyLabel.ForeColor = liveNetTicks >= 0 ? Color.DarkGreen : Color.Firebrick;
        }

        private void UpdatePositionLabel()
        {
            if (fillTrackedPosition == 0)
            {
                positionLabel.Text = "AI position: flat.";
                positionLabel.ForeColor = Color.DimGray;
                return;
            }
            double tickSize = ParseDoubleInput(tickSizeInput, 0.25); if (tickSize <= 0) tickSize = 0.25;
            double bid = forwarderLastBid, ask = forwarderLastAsk;
            string openPnl = "open P/L n/a";
            if (bid > 0 && ask > bid)
            {
                double mark = fillTrackedPosition > 0 ? bid : ask;
                double ticks = (mark - fillAveragePrice) / tickSize * Math.Sign(fillTrackedPosition) * Math.Abs(fillTrackedPosition);
                openPnl = "open P/L " + (ticks >= 0 ? "+" : "") + ticks.ToString("0.##", CultureInfo.InvariantCulture) + "t (bid/ask " +
                    bid.ToString("0.00###", CultureInfo.InvariantCulture) + "/" + ask.ToString("0.00###", CultureInfo.InvariantCulture) + ")";
            }
            positionLabel.Text = "AI position: " + (fillTrackedPosition > 0 ? "LONG " : "SHORT ") +
                Math.Abs(fillTrackedPosition).ToString(CultureInfo.InvariantCulture) + " @ " +
                fillAveragePrice.ToString("0.00###", CultureInfo.InvariantCulture) + " | " + openPnl;
            positionLabel.ForeColor = Color.DarkBlue;
        }

        private void UpdateDivergenceLabel()
        {
            if (liveTrades < 5)
            {
                divergenceLabel.Text = "Paper vs live divergence: waiting for at least 5 completed trades (" +
                    liveTrades.ToString(CultureInfo.InvariantCulture) + " so far).";
                divergenceLabel.ForeColor = Color.DimGray;
                return;
            }
            double paperSession = paperNetTicksAtArmSet ? latestPaperNetTicks - paperNetTicksAtArm : latestPaperNetTicks;
            double gap = paperSession - liveNetTicks;
            divergenceLabel.Text = "Paper vs live: paper " + (paperSession >= 0 ? "+" : "") + paperSession.ToString("0.##", CultureInfo.InvariantCulture) +
                "t | live " + (liveNetTicks >= 0 ? "+" : "") + liveNetTicks.ToString("0.##", CultureInfo.InvariantCulture) +
                "t | gap " + gap.ToString("0.##", CultureInfo.InvariantCulture) + "t" +
                (Math.Abs(gap) > 10 ? "  ⚠ slippage/latency may be eating the edge" : "");
            divergenceLabel.ForeColor = Math.Abs(gap) > 10 ? Color.DarkOrange : Color.DimGray;
        }

        // ntTicks  = when LnnOrderExecStrategy published the event (its own clock)
        // rxTicks  = when THIS dashboard's reader thread picked it off the ring
        private void RecordCommandLatency(long commandId, bool isExecution, double fillPrice, long ntTicks, long rxTicks, long originSentTicks)
        {
            CommandTiming timing;
            if (commandId <= 0 || !commandTimings.TryGetValue(commandId, out timing))
            {
                // The dashboard did not send this command - engine-direct orders, or a command
                // from a previous dashboard session. The strategy reports the originator's send
                // stamp on the record, so the round trip is still measurable.
                // Count only the FIRST ack and the FIRST fill per command. Without this, every
                // later state change (Working, Accepted, PartFilled, Cancelled) and every exit
                // fill was added as a fresh sample measured from the ENTRY's send time, which is
                // what produced 874 ms "acks" and 23 s "fills" - those were trade durations.
                if (originSentTicks > 0 && ntTicks > originSentTicks && !originCounted.Contains(commandId + (isExecution ? "F" : "A")))
                {
                    double originMs = (ntTicks - originSentTicks) / (double)TimeSpan.TicksPerMillisecond;
                    if (originMs >= 0 && originMs < 60000)
                    {
                        originCounted.Add(commandId + (isExecution ? "F" : "A"));
                        if (originCounted.Count > 400) originCounted.Clear();
                        List<double> bucket = isExecution ? ntFillMs : ntAckMs;
                        bucket.Add(originMs); if (bucket.Count > 500) bucket.RemoveRange(0, 250);
                        UpdateLatencyGateLabel();
                    }
                }
                return;
            }
            // Split the round trip so a big number can be attributed:
            //   NT8 leg  = send -> NT8 published it   (strategy wake + TriggerCustomEvent + NT8 order engine + broker/exchange)
            //   UI leg   = NT8 published -> we read it (our ring poll; pure dashboard overhead, NOT trading latency)
            if (ntTicks > 0 && ntTicks >= timing.SentTicks)
            {
                double ntMs = (ntTicks - timing.SentTicks) / (double)TimeSpan.TicksPerMillisecond;
                if (ntMs >= 0 && ntMs < 60000)
                {
                    List<double> bucket = isExecution ? ntFillMs : ntAckMs;
                    if (isExecution || !timing.Acked) { bucket.Add(ntMs); if (bucket.Count > 500) bucket.RemoveRange(0, 250); }
                }
                if (rxTicks > ntTicks)
                {
                    double uiMs = (rxTicks - ntTicks) / (double)TimeSpan.TicksPerMillisecond;
                    if (uiMs >= 0 && uiMs < 60000) { uiOverheadMs.Add(uiMs); if (uiOverheadMs.Count > 500) uiOverheadMs.RemoveRange(0, 250); }
                }
            }
            // Realized slippage: actual fill vs the touch at signal time. Only
            // meaningful for market entries with a known quote snapshot.
            if (isExecution && fillPrice > 0 && timing.QuoteBid > 0 && timing.QuoteAsk > timing.QuoteBid &&
                (timing.Op == (int)TradeCmdOp.BuyMarket || timing.Op == (int)TradeCmdOp.SellMarket))
            {
                double tickSize = ParseDoubleInput(tickSizeInput, 0.25); if (tickSize <= 0) tickSize = 0.25;
                double slip = timing.Op == (int)TradeCmdOp.BuyMarket
                    ? (fillPrice - timing.QuoteAsk) / tickSize
                    : (timing.QuoteBid - fillPrice) / tickSize;
                realizedSlippageTicks.Add(slip);
                if (realizedSlippageTicks.Count > 500) realizedSlippageTicks.RemoveRange(0, 250);
                Append("Realized slippage cmd " + commandId.ToString(CultureInfo.InvariantCulture) + ": " +
                    (slip >= 0 ? "+" : "") + slip.ToString("0.##", CultureInfo.InvariantCulture) +
                    "t (quote " + (timing.Op == (int)TradeCmdOp.BuyMarket ? timing.QuoteAsk : timing.QuoteBid).ToString("0.00###", CultureInfo.InvariantCulture) +
                    " → fill " + fillPrice.ToString("0.00###", CultureInfo.InvariantCulture) + ")");
            }
            double ms = (DateTime.UtcNow.Ticks - timing.SentTicks) / (double)TimeSpan.TicksPerMillisecond;
            if (ms < 0 || ms > 60000) return;
            if (!timing.Acked)
            {
                timing.Acked = true;
                ackLatencyMs.Add(ms);
                if (ackLatencyMs.Count > 500) ackLatencyMs.RemoveRange(0, 250);
                if (ms > 500) Append("WARNING: NT8 took " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms to acknowledge command " + commandId + " — strategy or platform is slow.");
            }
            if (isExecution)
            {
                fillLatencyMs.Add(ms);
                if (fillLatencyMs.Count > 500) fillLatencyMs.RemoveRange(0, 250);
                commandTimings.Remove(commandId);
            }
            UpdateLatencyGateLabel();
            UpdateLearnedCostPanel();
        }

        // Learned execution cost from REAL fills: turns measured slippage +
        // latency into a suggested paper cost (RT fee) so the paper simulator's
        // win-rate reflects your actual execution, not a guessed number.
        private void UpdateLearnedCostPanel()
        {
            int n = realizedSlippageTicks.Count;
            if (n == 0)
            {
                learnedCostPanel.Text = "Learned execution cost: no real market fills yet. Route market entries live and this fills in with measured slippage, latency, and a suggested paper cost.";
                return;
            }
            double[] s = realizedSlippageTicks.ToArray(); Array.Sort(s);
            double median = s[n / 2];
            double mean = 0; foreach (double v in s) mean += v; mean /= n;
            double p95 = s[Math.Min(n - 1, (int)(0.95 * n))];
            // Per-fill slippage is one leg (entry). A round trip pays it on entry
            // AND exit, so the honest paper cost ≈ 2× the median per-fill slippage.
            double suggestedRoundTrip = Math.Max(0, 2.0 * median);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Learned execution cost — from " + n.ToString(CultureInfo.InvariantCulture) + " real market fills:");
            sb.AppendLine("  Slippage/fill: median " + median.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) +
                "t   mean " + mean.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) +
                "t   p95 " + p95.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "t   [+ = paid worse than the touch]");
            sb.AppendLine("  Fill latency p50/p95: " + LatencyPercentiles(fillLatencyMs));
            sb.Append("  → Suggested paper cost (round trip): ~" + suggestedRoundTrip.ToString("0.00", CultureInfo.InvariantCulture) +
                "t. Convert to $ and set it as RT fee so paper matches live. (Join fills: see the executioner markout panel above.)");
            learnedCostPanel.Text = sb.ToString();
        }

        private static string LatencyPercentiles(List<double> samples)
        {
            if (samples.Count == 0) return "n/a";
            double[] sorted = samples.ToArray(); Array.Sort(sorted);
            Func<double, double> pick = delegate(double q) { return sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))]; };
            return pick(.50).ToString("0", CultureInfo.InvariantCulture) + "/" + pick(.95).ToString("0", CultureInfo.InvariantCulture) +
                " ms (last " + samples[samples.Count - 1].ToString("0", CultureInfo.InvariantCulture) + ")";
        }

        private string CurrentEntryGateReason()
        {
            if (!tradeArmed) return "disarmed";
            if (neutralRegimeBlock) return "BLOCKED: model neutral (dead-regime gate)";
            if (!InsideSessionWindow()) return "BLOCKED: outside session window";
            if (DateTime.UtcNow < cooldownUntilUtc)
                return "BLOCKED: cooldown " + (cooldownUntilUtc - DateTime.UtcNow).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s left";
            double gapSeconds = ParseDoubleInput(minSecondsBetweenTradesInput, 0);
            if (gapSeconds > 0 && lastEntryUtc != DateTime.MinValue)
            {
                double remaining = gapSeconds - (DateTime.UtcNow - lastEntryUtc).TotalSeconds;
                if (remaining > 0)
                    return "BLOCKED: trade gap " + remaining.ToString("0", CultureInfo.InvariantCulture) + "s left";
            }
            if (pendingEntrySignal != null)
            {
                double holdSeconds = ParseDoubleInput(signalHoldSecondsInput, 0);
                double waited = (DateTime.UtcNow - pendingEntrySinceUtc).TotalSeconds;
                if (holdSeconds > 0 && waited < holdSeconds)
                    return "HOLDING " + pendingEntrySignal + " " + waited.ToString("0", CultureInfo.InvariantCulture) + "/" +
                        holdSeconds.ToString("0", CultureInfo.InvariantCulture) + "s";
            }
            return "OPEN";
        }

        // Rolling trades/hour: prune entries older than 60 min, then the count
        // of what remains IS the trailing-hour rate. Also reports a short-window
        // (10 min ×6) projection so the reading responds within one session.
        private string TradesPerHourText()
        {
            DateTime now = DateTime.UtcNow;
            DateTime hourAgo = now.AddMinutes(-60);
            while (entryTimestamps.Count > 0 && entryTimestamps[0] < hourAgo)
                entryTimestamps.RemoveAt(0);
            int lastHour = entryTimestamps.Count;
            DateTime tenAgo = now.AddMinutes(-10);
            int last10 = 0;
            for (int i = entryTimestamps.Count - 1; i >= 0; i--)
            {
                if (entryTimestamps[i] >= tenAgo) last10++;
                else break;
            }
            return lastHour.ToString(CultureInfo.InvariantCulture) + "/hr (last 60m); " +
                (last10 * 6).ToString(CultureInfo.InvariantCulture) + "/hr proj (last 10m)";
        }

        private void UpdateLatencyGateLabel() { latencyGateDirty = true; }   // v64m: coalesced, see uiPaintTimer

        private void UpdateLatencyGateLabelNow()
        {
            string gate = CurrentEntryGateReason();
            string slipText = "n/a";
            if (realizedSlippageTicks.Count > 0)
            {
                double sum = 0; foreach (double s in realizedSlippageTicks) sum += s;
                slipText = (sum / realizedSlippageTicks.Count).ToString("0.##", CultureInfo.InvariantCulture) + "t avg (" +
                    realizedSlippageTicks.Count.ToString(CultureInfo.InvariantCulture) + " fills)";
            }
            latencyGateLabel.Text = ((stratFlags & 32) != 0 && (DateTime.UtcNow - stratHeartbeatUtc).TotalSeconds < 10
                    ? "[SIM ACCOUNT — these latencies are NinjaTrader's simulator, not a live route] " : "") +
                "Entry gate: " + gate +
                " | NT8 ack p50/p95: " + LatencyPercentiles(ntAckMs.Count > 0 ? ntAckMs : ackLatencyMs) +
                " | NT8 fill p50/p95: " + LatencyPercentiles(ntFillMs.Count > 0 ? ntFillMs : fillLatencyMs) +
                " | NT8 event->callback: " + LatencyPercentiles(ntDeliveryMs) +
                " | dashboard overhead: " + LatencyPercentiles(uiOverheadMs) +
                " | slip " + slipText +
                " | rate " + TradesPerHourText();
            Tip(latencyGateLabel, "NT8 ack / NT8 fill measure from the moment this dashboard wrote the command into the ring to the moment LnnOrderExecStrategy published the order event / the execution back. That leg contains: the strategy's reader thread waking, NinjaTrader's TriggerCustomEvent marshal onto its strategy thread, NT8's order engine, and - for the fill only - the round trip to your broker and the exchange. It is the number that matters.\n" +
                "Dashboard overhead is separate: from the strategy publishing to this dashboard reading it off the ring. It is this app's own polling delay and is NOT part of your trading latency - before v64k it was silently included in the ack/fill numbers.\n" +
                "A large ACK (before any exchange round trip) points at something local: NinjaTrader busy (charts, indicators, Market Analyzer), a shared vCPU on the VPS being descheduled, or coarse timer resolution. A large FILL with a small ack is the broker/exchange route.");
            latencyGateLabel.ForeColor = gate == "OPEN" ? Color.DarkGreen : gate == "disarmed" ? Color.DimGray : Color.DarkOrange;
        }

        // ==================== limit executioner ====================

        // Push all executioner settings (enable+mode, gates, management, markout)
        // to LnnOrderExecStrategy via ops 40-43.
        private void ApplyExecutionerSettings(object sender, EventArgs e)
        {
            if (tradeCmdAccessor == null)
            {
                Append("Limit executioner: connect the Trading Hub first (or Start AI Trader).");
                return;
            }
            bool enabled = execEnable.Checked;
            bool live = execModeSelector.SelectedIndex == 1;
            // op40: enable(Qty) + mode(Price)
            SendTradeCommand(TradeCmdOp.ExecEnableMode, enabled ? 1 : 0, live ? 1.0 : 0.0, 0, 0, 0, 0.0);
            // op41: gates — Price=imbalance, StopPrice=restingMs, sl=flicker/s, pt=microprice-gate
            SendTradeCommand(TradeCmdOp.ExecGates, 1,
                ParseDoubleInput(exImbalanceInput, 0.0),
                ParseDoubleInput(exRestingMsInput, 200),
                ParseDoubleInput(exFlickerInput, 0.0),
                exMicropriceGate.Checked ? 1.0 : 0.0, 0.0);
            // op42: management — Price=aggrQty, StopPrice=windowMs, sl=chaseTicks, pt=timeoutMs, trail=onTimeout
            SendTradeCommand(TradeCmdOp.ExecManagement, 1,
                ParseDoubleInput(exAggrQtyInput, 15),
                ParseDoubleInput(exAggrWindowInput, 500),
                ParseDoubleInput(exChaseInput, 1),
                ParseDoubleInput(exTimeoutInput, 8000),
                exOnTimeoutSelector.SelectedIndex == 1 ? 1.0f : 0.0f);
            // op43: markout windows — Price=win1ms, StopPrice=win2ms
            SendTradeCommand(TradeCmdOp.ExecMarkout, 1,
                ParseDoubleInput(exMark1Input, 1000),
                ParseDoubleInput(exMark2Input, 5000), 0, 0, 0.0);
            // op44: queue estimator — Price = cancel-ahead factor k (0 legacy .. 1 uniform)
            SendTradeCommand(TradeCmdOp.ExecQueueModel, 1,
                Math.Max(0, Math.Min(1, ParseDoubleInput(exCancelAheadInput, 0.5))), 0, 0, 0, 0.0);
            // op45: policy — Price = 0 gates / 1 EV, StopPrice = expected move ticks, Qty = 1 -> reload the strategy's FillModelFile
            SendTradeCommand(TradeCmdOp.ExecPolicy, 1,
                exPolicySelector.SelectedIndex == 1 ? 1.0 : 0.0, Math.Max(0.25, ParseDoubleInput(exExpectedMoveInput, 2)), 0, 0, 0.0);
            Append("Limit executioner → NT8: " + (enabled ? (live ? "ENABLED (LIVE — real orders)" : "ENABLED (MEASURE — simulate only)") : "disabled") +
                "; gates/management/markout applied.");
            if (enabled && live)
                Append("⚠ Executioner is LIVE: joins will place and manage REAL limit orders on the connected account.");
        }

        // Parse a quality summary (IsExecution == 3). Field semantics match the
        // strategy's PublishQuality: Price=eff spread ticks, Qty=markout1×100,
        // FilledQty=markout2×100, OrderState=filled?, OrderAction=outcome,
        // MarketPosition=mode, NtOrderIdLow=queue-ahead.
        private void ProcessExecutionerQuality(TradeFillPayload q)
        {
            exSamples++;
            bool filled = q.OrderState == 1;
            if (filled) exFillsSeen++; else exNoFills++;
            if (q.OrderAction >= 0 && q.OrderAction < exOutcomeCounts.Length) exOutcomeCounts[q.OrderAction]++;
            if (filled)
            {
                exEffSpread.Add(q.Price);
                if (q.Qty > -100000) exMk1.Add(q.Qty / 100.0);
                if (q.FilledQty > -100000) exMk2.Add(q.FilledQty / 100.0);
                Cap(exEffSpread); Cap(exMk1); Cap(exMk2);
            }
            UpdateExecQualityPanel(q.MarketPosition == 1);
        }

        private static void Cap(List<double> l) { if (l.Count > 1000) l.RemoveRange(0, 500); }

        private static string Avg(List<double> l)
        {
            if (l.Count == 0) return "n/a";
            double s = 0; foreach (double v in l) s += v;
            return (s / l.Count).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "t";
        }

        private void UpdateExecQualityPanel(bool live)
        {
            double fillRate = exSamples > 0 ? 100.0 * exFillsSeen / exSamples : 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Mode: " + (live ? "LIVE (real orders)" : "MEASURE (simulated)") +
                "    samples: " + exSamples.ToString(CultureInfo.InvariantCulture) +
                "    fills: " + exFillsSeen.ToString(CultureInfo.InvariantCulture) +
                " (" + fillRate.ToString("0", CultureInfo.InvariantCulture) + "% fill rate)");
            sb.AppendLine("Effective spread captured (fill vs mid): " + Avg(exEffSpread) +
                "   [+ = you bought below / sold above mid]");
            sb.AppendLine("Markout +win1: " + Avg(exMk1) + "    Markout +win2: " + Avg(exMk2) +
                "   [+ = price moved your way after fill; NEGATIVE = adverse selection]");
            double net1 = 0, net2 = 0; int n = Math.Min(exEffSpread.Count, Math.Min(exMk1.Count, exMk2.Count));
            if (n > 0)
            {
                for (int i = exEffSpread.Count - n, j = exMk1.Count - n, k = exMk2.Count - n; i < exEffSpread.Count; i++, j++, k++)
                { net1 += exEffSpread[i] + exMk1[j]; net2 += exEffSpread[i] + exMk2[k]; }
                sb.AppendLine("Net edge/fill (spread + markout): " + (net1 / n).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) +
                    "t @win1, " + (net2 / n).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "t @win2   [the number to get positive before trusting live]");
            }
            sb.Append("Outcomes — filled " + exOutcomeCounts[0] + ", cancelled(aggression) " + exOutcomeCounts[1] +
                ", timeout-abandon " + exOutcomeCounts[2] + ", timeout-cross " + exOutcomeCounts[3] + ", missed(chase) " + exOutcomeCounts[4]);
            execQualityPanel.Text = sb.ToString();
        }

        private void ApplyStrategySettingsClick(object sender, EventArgs e)
        {
            if (tradeCmdAccessor == null)
            {
                MessageBox.Show(this, "Connect the trade hub first (Connect button or Start AI Trader).", "NT8 strategy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int maxPos = (int)ParseDoubleInput(ntMaxPosInput, 0);
            if (maxPos > 0) SendTradeCommand(TradeCmdOp.SetMaxPosition, maxPos, 0, 0, 0, 0, 0.0);
            double lossTicks = ParseDoubleInput(ntLossLimitInput, -1);
            if (lossTicks >= 0) SendTradeCommand(TradeCmdOp.SetDailyLossLimit, 1, lossTicks, 0, 0, 0, 0.0);
            double sl = ParseDoubleInput(ntDefSlInput, 0), pt = ParseDoubleInput(ntDefPtInput, 0), trail = ParseDoubleInput(ntDefTrailInput, 0);
            SendTradeCommand(TradeCmdOp.SetDefaultBracket, 1, 0, 0, sl, pt, trail);
            Append("Sent strategy settings to NT8: max-pos=" + maxPos + " loss-ticks=" + lossTicks +
                " default SL/PT/trail=" + sl + "/" + pt + "/" + trail + " ticks. Check the NT8 output window for confirmation.");
        }

        private void CopyDiagnosticsClick(object sender, EventArgs e)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("=== LNN Dashboard diagnostics — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
            report.AppendLine("build: " + BuildStamp);
            report.AppendLine("instrument: index=" + instrument.SelectedIndex.ToString(CultureInfo.InvariantCulture) +
                " '" + (instrument.SelectedItem as string ?? "(none)") + "' symbol=" + ResolveTradeSymbol());
            report.AppendLine("engine: " + (engine.SelectedItem != null ? engine.SelectedItem.ToString() : "(none)") +
                " | learner running=" + (learner != null && !learner.HasExited));
            report.AppendLine("trade hub: connected=" + (tradeCmdAccessor != null) +
                " armed=" + tradeArmed +
                " cmd-seq=" + tradeCmdSeq.ToString(CultureInfo.InvariantCulture) +
                " fill-cursor=" + tradeFillCursor.ToString(CultureInfo.InvariantCulture) +
                " net-position=" + tradeNetPosition.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("cmd ring: " + @"Local\LNN_ORDER_CMD_" + ResolveTradeSymbol() + " | fill ring: " + @"Local\LNN_ORDER_FILL_" + ResolveTradeSymbol());
            long fillWriteSeq = -1;
            try { if (tradeFillAccessor != null) fillWriteSeq = tradeFillAccessor.ReadInt64(0); } catch { }
            report.AppendLine("fill ring write-seq now=" + fillWriteSeq.ToString(CultureInfo.InvariantCulture) +
                " | last activity seq seen=" + lastFillRingSequenceSeen.ToString(CultureInfo.InvariantCulture) +
                " | last activity utc=" + (lastStrategyActivityUtc == DateTime.MinValue ? "never" : lastStrategyActivityUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
            report.AppendLine("signal: current-shown='" + paperSignal.Text.Replace("\r\n", " / ") + "' last-forwarded=" + (lastForwardedAiSignal ?? "(none)") +
                " pending-entry=" + (pendingEntrySignal ?? "(none)") + " probability=" + latestProbability.ToString("F4", CultureInfo.InvariantCulture));
            report.AppendLine("gates: inside-session=" + InsideSessionWindow() +
                " cooldown-until=" + (cooldownUntilUtc == DateTime.MinValue ? "off" : cooldownUntilUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)) +
                " entries=" + aiEntriesThisSession.ToString(CultureInfo.InvariantCulture) +
                " live trades=" + liveTrades.ToString(CultureInfo.InvariantCulture) + " W/L=" + liveWins + "/" + liveLosses);
            report.AppendLine("status line: " + status.Text);
            report.AppendLine("bridge status: " + simTradeBridgeStatus.Text);
            report.AppendLine("strategy activity: " + strategyAliveLabel.Text);
            if (tradePathDirty) { tradePathDirty = false; RefreshTradePathNow(); }
            report.AppendLine("trade path:");
            report.AppendLine(tradePathStatus.Text);
            report.AppendLine("last metrics line: " + (latestMetricsLine ?? "(none)"));
            report.AppendLine("last paper metrics: " + (latestPaperMetricsLine ?? "(none)"));
            FlushPendingLogs();   // v64m: the on-screen log is buffered; paint it before we snapshot it
            string logText = output.Text ?? string.Empty;
            if (logText.Length > 4000) logText = logText.Substring(logText.Length - 4000);
            report.AppendLine("=== log tail ===");
            report.AppendLine(logText);
            string reportText = report.ToString();
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LnnLiveDashboard", "diagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, reportText); } catch { }
            bool copied = false;
            try { Clipboard.SetText(reportText); copied = true; } catch { }
            MessageBox.Show(this,
                (copied ? "Diagnostics copied to the clipboard — paste it into the chat.\n\n" : "") + "Also saved to:\n" + path,
                "Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void AutoDisarm(string reason)
        {
            Append("AUTO-DISARM: " + reason);
            StopAiTrader();
            simTradeBridgeStatus.Text = "AUTO-DISARMED — " + reason;
            simTradeBridgeStatus.ForeColor = Color.Firebrick;
            try { if (soundAlerts.Checked) SystemSounds.Hand.Play(); } catch { }
        }

        private void PlayAlert()
        {
            try { if (soundAlerts.Checked) SystemSounds.Exclamation.Play(); } catch { }
        }

        // Loss-limit and probability-dead-zone guards, fed from telemetry.
        private void CheckPaperGuards()
        {
            if (!tradeArmed) return;
            if (!paperNetTicksAtArmSet) { paperNetTicksAtArm = latestPaperNetTicks; paperNetTicksAtArmSet = true; }
            double limit;
            if (double.TryParse(dailyLossLimitTicks.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out limit) && limit > 0.0)
            {
                double sessionPnl = latestPaperNetTicks - paperNetTicksAtArm;
                if (sessionPnl <= -limit)
                    AutoDisarm("Session loss limit hit (" + sessionPnl.ToString("0.##", CultureInfo.InvariantCulture) +
                        " ticks ≤ -" + limit.ToString("0.##", CultureInfo.InvariantCulture) + ").");
            }
        }

        // v64x: this used to AUTO-DISARM after 120 consecutive dead-zone readings.
        // Two problems. First, the count was calibrated against the 1 Hz frames line
        // but the 50 ms probability line feeds it too, so "120 readings" was about
        // SIX SECONDS of an undecided model, not two minutes - a freshly retrained
        // model disarmed itself almost immediately. Second, disarming is too blunt:
        // the trader should stand aside during a dead regime, not switch off.
        //
        // It is now an ENTRY GATE with hysteresis. A plain "is p neutral right now"
        // test would do nothing at all - GetPaperSignal already returns NO-TRADE for
        // anything between the 0.40/0.60 thresholds, so 0.45-0.55 never produces a
        // signal anyway. What this blocks is the single spike OUT of a dead regime:
        // once the model has been undecided for NeutralBlockSeconds, entries stay
        // blocked until it holds a directional read for NeutralClearSeconds.
        private DateTime neutralSinceUtc = DateTime.MinValue;
        private DateTime directionalSinceUtc = DateTime.MinValue;
        private bool neutralRegimeBlock;

        private double NeutralBlockSeconds { get { return ParseDoubleInput(neutralBlockSecondsInput, 120); } }
        private const double NeutralClearSeconds = 3.0;

        private void TrackProbabilityRegime(string probabilityText)
        {
            double p;
            if (!double.TryParse(probabilityText, NumberStyles.Float, CultureInfo.InvariantCulture, out p)) return;
            latestProbability = p;

            double blockAfter = NeutralBlockSeconds;
            if (blockAfter <= 0) { neutralRegimeBlock = false; neutralSinceUtc = DateTime.MinValue; return; }   // 0 = gate off

            DateTime now = DateTime.UtcNow;
            bool neutral = p >= 0.45 && p <= 0.55;
            if (neutral)
            {
                directionalSinceUtc = DateTime.MinValue;
                if (neutralSinceUtc == DateTime.MinValue) neutralSinceUtc = now;
                else if (!neutralRegimeBlock && (now - neutralSinceUtc).TotalSeconds >= blockAfter)
                {
                    neutralRegimeBlock = true;
                    Append("Entry gate: no directional read for " + blockAfter.ToString("0", CultureInfo.InvariantCulture) +
                        "s - entries blocked until conviction returns. The AI Trader stays ARMED and open positions keep their bracket.");
                }
            }
            else
            {
                neutralSinceUtc = DateTime.MinValue;
                if (directionalSinceUtc == DateTime.MinValue) directionalSinceUtc = now;
                if (neutralRegimeBlock && (now - directionalSinceUtc).TotalSeconds >= NeutralClearSeconds)
                {
                    neutralRegimeBlock = false;
                    Append("Entry gate: directional read held for " + NeutralClearSeconds.ToString("0", CultureInfo.InvariantCulture) +
                        "s - entries re-enabled.");
                }
            }
        }

        private static Color SignalColor(string signal)
        {
            if (signal == "LONG") return Color.DarkGreen;
            if (signal == "SHORT") return Color.Firebrick;
            if (signal == "NO-TRADE") return Color.DarkOrange;
            return Color.DimGray;
        }

        private void PaintPnlSpark(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            if (paperPnlHistory.Count < 2)
            {
                TextRenderer.DrawText(e.Graphics, "No paper P/L samples yet.", Font, new Point(8, 8), Color.DimGray);
                return;
            }
            double min = double.MaxValue, max = double.MinValue;
            foreach (double v in paperPnlHistory) { if (v < min) min = v; if (v > max) max = v; }
            if (max - min < 1.0) { max += 0.5; min -= 0.5; }
            int w = pnlSpark.ClientSize.Width, h = pnlSpark.ClientSize.Height;
            // zero line
            if (min < 0 && max > 0)
            {
                int zy = h - 4 - (int)((0 - min) / (max - min) * (h - 8));
                using (Pen zp = new Pen(Color.Gainsboro)) e.Graphics.DrawLine(zp, 0, zy, w, zy);
            }
            Point[] pts = new Point[paperPnlHistory.Count];
            for (int i = 0; i < paperPnlHistory.Count; i++)
            {
                int x = 4 + (int)((long)i * (w - 8) / Math.Max(1, paperPnlHistory.Count - 1));
                int y = h - 4 - (int)((paperPnlHistory[i] - min) / (max - min) * (h - 8));
                pts[i] = new Point(x, y);
            }
            double last = paperPnlHistory[paperPnlHistory.Count - 1];
            using (Pen p = new Pen(last >= 0 ? Color.DarkGreen : Color.Firebrick, 1.6f))
                e.Graphics.DrawLines(p, pts);
            TextRenderer.DrawText(e.Graphics,
                last.ToString("0.##", CultureInfo.InvariantCulture) + " ticks (min " + min.ToString("0.#", CultureInfo.InvariantCulture) +
                " / max " + max.ToString("0.#", CultureInfo.InvariantCulture) + ")",
                Font, new Point(8, 4), Color.DimGray);
        }

        private void StartAiTrader()
        {
            DialogResult confirm = MessageBox.Show(this,
                "Arm the AI Trader?\n\nSignals will be forwarded as live commands to LnnOrderExecStrategy on NT8 (" +
                (string.IsNullOrEmpty(ResolveTradeSymbol()) ? "no instrument selected" : ResolveTradeSymbol()) +
                ").\nMake sure NT8 is on the intended (Sim101 vs live) account.",
                "Confirm ARM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            if (tradeCmdAccessor == null) ConnectTradeHub();
            if (tradeCmdAccessor == null)
            {
                simTradeBridgeStatus.Text = "NT8 command MMF: connection FAILED — see Trading Hub status on the Live learning tab.";
                simTradeBridgeStatus.ForeColor = Color.Firebrick;
                return;
            }
            if (BarrierMode)
            {
                // The strategy owns the exits: refuse to arm if it reports none (a trade would never end).
                if (stratRiskUtc != DateTime.MinValue && (DateTime.UtcNow - stratRiskUtc).TotalSeconds < 20 && stratSlTicks <= 0 && stratPtTicks <= 0 && stratTrailTicks <= 0)
                {
                    MessageBox.Show(this, "The strategy reports no stop, no target and no trail (DefaultStopLossTicks / DefaultProfitTargetTicks / DefaultTrailTicks are all 0).\n\nIn strategy-bracket exit mode a trade would never close. Set them in the NT8 strategy parameters or in the Strategy control row (Apply), or switch the exit mode.",
                        "Not armed - no exits", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (stratRiskUtc == DateTime.MinValue)
                    Append("WARNING: no risk-config heartbeat from the strategy yet - cannot verify it has a stop/target. Check NT8 parameters (DefaultStopLossTicks / DefaultProfitTargetTicks) yourself.");
            }
            lastForwardedAiSignal = null;
            aiSignalFlipTimes.Clear();
            aiEntriesThisSession = 0;
            paperNetTicksAtArmSet = false;
            neutralRegimeBlock = false; neutralSinceUtc = DateTime.MinValue; directionalSinceUtc = DateTime.MinValue;   // v64x: fresh arm, fresh gate
            SendEntryGateConfig();   // v64y: the strategy gets the current gate settings on every arm
            liveTrades = liveWins = liveLosses = consecutiveLosses = 0;
            liveNetTicks = 0;
            cooldownUntilUtc = DateTime.MinValue;
            pendingEntrySignal = null;
            sessionWindowFlattened = false;
            barrierOpenSide = 0; entrySentUtc = DateTime.MinValue;
            UpdateTallyLabel();
            UpdateDivergenceLabel();
            // v64p: engine-direct orders are gated by the strategy's PAUSE flag, not by
            // tradeArmed (the engine has never heard of tradeArmed). Arming lifts it.
            if (engineDirectActive && tradeCmdAccessor != null)
            {
                SendTradeCommand(TradeCmdOp.ResumeExecution, 0, 0, 0, 0, 0, 0.0);
                Append("Engine-direct: strategy RESUMED - the engine's orders can now reach NT8.");
            }
            armedBanner.Visible = true;
            simTradeBridgeStatus.Text = "NT8 command MMF: connected + ARMED (" + @"Local\LNN_ORDER_CMD_" + ResolveTradeSymbol() + "). Signals are forwarded to LnnOrderExecStrategy.";
            simTradeBridgeStatus.ForeColor = Color.DarkGreen;
            tradeArmed = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(TradeArmedFilePath)); File.WriteAllText(TradeArmedFilePath, "1"); } catch { }
            startPaperSimulation.Enabled = false;
            stopPaperSimulation.Enabled = true;
            killSwitchBtn.Enabled = true;
            tradeHubStatus.Text = "AI Trader ARMED.";
            tradeHubStatus.ForeColor = Color.DarkGreen;
            Append("AI Trader started. Trade MMF connected and armed.");
            SyncHubAiPanel();
        }

        private void StopAiTrader()
        {
            bool wasArmed = tradeArmed;
            tradeArmed = false;
            armedBanner.Visible = false;
            lastForwardedAiSignal = null;
            barrierOpenSide = 0; entrySentUtc = DateTime.MinValue;
            if (wasArmed) WriteSessionSummary();
            simTradeBridgeStatus.Text = "NT8 command MMF: disarmed. Signals are no longer forwarded.";
            simTradeBridgeStatus.ForeColor = Color.DimGray;
            try { Directory.CreateDirectory(Path.GetDirectoryName(TradeArmedFilePath)); File.WriteAllText(TradeArmedFilePath, "0"); } catch { }
            if (tradeCmdAccessor != null)
            {
                // v64p: PAUSE first. With engine-direct on, the engine keeps writing orders
                // regardless of this dashboard's armed state, so flattening without pausing
                // just leaves a gap for the next signal to open a fresh position. Op 20 is
                // what actually stops it (SubmitEntryGated rejects entries while paused).
                if (engineDirectActive) SendTradeCommand(TradeCmdOp.PauseExecution, 0, 0, 0, 0, 0, 0.0);
                SendTradeCommand(TradeCmdOp.Flat, 0, 0, 0, 0, 0, 0.0);
                SendTradeCommand(TradeCmdOp.CancelAll, 0, 0, 0, 0, 0, 0.0);
            }
            startPaperSimulation.Enabled = learner != null && !learner.HasExited;
            stopPaperSimulation.Enabled = false;
            killSwitchBtn.Enabled = false;
            tradeHubStatus.Text = "AI Trader stopped.";
            tradeHubStatus.ForeColor = Color.DimGray;
            Append("AI Trader stopped. Disarmed + FLAT + CancelAll sent.");
            SyncHubAiPanel();
        }

        private void SaveModelNowClick(object sender, EventArgs e)
        {
            if (!SendModelSaveCommand()) return;
            saveModelNow.Enabled = false;
            Append("Manual model save requested. The learner will save at the next processed DOM snapshot.");
        }

        private bool SendModelSaveCommand()
        {
            if (string.IsNullOrEmpty(activeModelSavePipe)) return false;
            try
            {
                using (NamedPipeClientStream control = new NamedPipeClientStream(".", activeModelSavePipe, PipeDirection.Out))
                {
                    control.Connect(1000);
                    control.WriteByte((byte)'S');
                    control.Flush();
                }
                return true;
            }
            catch (Exception error)
            {
                Append("Manual model save was not delivered: " + error.Message);
                SetStatus("Manual model save is unavailable. Confirm the BestValidated learner is running.", Color.Firebrick);
                return false;
            }
        }

        private void SendShadowControl(char command)
        {
            if (string.IsNullOrEmpty(activeShadowControlPipe))
            {
                Append("Live shadow toggle is only available while a hybrid MMF engine is running.");
                return;
            }
            try
            {
                using (NamedPipeClientStream control = new NamedPipeClientStream(".", activeShadowControlPipe, PipeDirection.Out))
                {
                    control.Connect(1000);
                    control.WriteByte((byte)command);
                    control.Flush();
                }
                Append(command == 'F'
                    ? "Live shadow/fusion turned ON — engine now blends cross-market evidence."
                    : "Live shadow/fusion turned OFF — engine now runs pure 222-feature thresholds.");
            }
            catch (Exception error)
            {
                Append("Shadow toggle not delivered: " + error.Message);
            }
        }

        private void SendBridgeCommand(char command)
        {
            if (string.IsNullOrEmpty(activeControlPipe)) return;
            try
            {
                using (NamedPipeClientStream control = new NamedPipeClientStream(".", activeControlPipe, PipeDirection.Out))
                {
                    control.Connect(1000);
                    control.WriteByte((byte)command);
                    control.Flush();
                }
                if (command == 'S')
                    SetStatus("Bridge start requested. Waiting for live depth frames...", Color.DarkGoldenrod);
                else
                    Append("Bridge stop requested.");
            }
            catch (Exception error)
            {
                string action = command == 'S' ? "start" : "stop";
                Append("NinjaTrader bridge " + action + " command was not delivered: " + error.Message);
                if (command == 'S')
                    SetStatus("Receiver is waiting. Compile/restart NinjaTrader, then start the matching bridge from Control Center -> New.", Color.Firebrick);
            }
        }

        private void TemporalObserverOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data) || IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                Match match = TemporalObserverMetrics.Match(e.Data);
                if (match.Success)
                {
                    temporalObserverMarketStatus = "Frames / ring gaps: " + match.Groups["frames"].Value + " / " + match.Groups["gaps"].Value +
                        "   MMF confidence: " + match.Groups["confidence"].Value + "   bias: " + match.Groups["bias"].Value + "\r\n" +
                        "Absorption: " + match.Groups["absorption"].Value + "   speed: " + match.Groups["speed"].Value + " ticks/s   spread: " + match.Groups["spread"].Value + " ticks\r\n" +
                        "Temporal states fast / medium / slow: " + match.Groups["fast"].Value + " / " + match.Groups["medium"].Value + " / " + match.Groups["slow"].Value +
                        "   consistency error: " + match.Groups["error"].Value;
                    RefreshTemporalObserverSummary();
                }
                else
                {
                    Match head = TemporalObserverHead.Match(e.Data);
                    if (head.Success)
                    {
                        string headStatus = head.Groups["name"].Value + " target " + head.Groups["target"].Value + " ticks / " + head.Groups["deadline"].Value +
                            " ms | updates " + head.Groups["updates"].Value + " | labels U/D " + head.Groups["up"].Value + "/" + head.Groups["down"].Value +
                            " | expired " + head.Groups["expired"].Value + " | accuracy " + head.Groups["accuracy"].Value + "% | up probability " + head.Groups["probability"].Value +
                            " | weight L2 " + head.Groups["weight"].Value;
                        if (head.Groups["name"].Value == "FAST") temporalObserverFastStatus = headStatus;
                        else if (head.Groups["name"].Value == "MEDIUM") temporalObserverMediumStatus = headStatus;
                        else temporalObserverSlowStatus = headStatus;
                        RefreshTemporalObserverSummary();
                    }
                }
                AppendTemporalObserverEvent(e.Data);
            });
        }

        private void RefreshTemporalObserverSummary()
        {
            temporalObserverSummary.Text = "Passive temporal self-consistency/MMF observer\r\n" +
                temporalObserverMarketStatus + "\r\n\r\n" +
                temporalObserverFastStatus + "\r\n" +
                temporalObserverMediumStatus + "\r\n" +
                temporalObserverSlowStatus + "\r\n\r\n" +
                "Observer-only learning: its three heads cannot feed, train, gate, save into, or place orders through the primary engine.";
        }

        private void LearnerOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            // This is deliberately the only rapid repaint path. It carries a
            // single number, is capped at 20 Hz, and never appends text or
            // refreshes the heavier metrics/log panels.
            Match liveProbability = LiveUpProbability.Match(e.Data);
            if (liveProbability.Success)
            {
                Interlocked.Exchange(ref lastLiveProbabilityTicks, DateTime.UtcNow.Ticks);
                lastLiveProbabilityText = liveProbability.Groups["probability"].Value;
                bool dispatchProbability = false;
                lock (telemetryThrottleLock)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastLiveProbabilityDispatchUtc).TotalMilliseconds >= 50.0)
                    {
                        lastLiveProbabilityDispatchUtc = now;
                        dispatchProbability = true;
                    }
                }
                if (dispatchProbability && !IsDisposed && IsHandleCreated)
                {
                    string value = liveProbability.Groups["probability"].Value;
                    BeginInvoke((MethodInvoker)delegate { SetMetric(probability, "Current up probability", value); });
                }
                return;
            }
            Match novelty = MarketNovelty.Match(e.Data);
            if (novelty.Success)
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    string text = "Novelty: " + novelty.Groups["state"].Value + " | score " + novelty.Groups["score"].Value +
                        " | extreme features " + novelty.Groups["extreme"].Value +
                        "\r\nThis is a warning that the current DOM differs from the session distribution; it does not block inference.";
                    BeginInvoke((MethodInvoker)delegate { shadowNoveltyStatus = text; RefreshShadowTeacherSummary(); AppendShadowTeacherEvent("market novelty state=" + novelty.Groups["state"].Value + " score=" + novelty.Groups["score"].Value + " extremes=" + novelty.Groups["extreme"].Value); });
                }
                return;
            }
            Match preUnfamiliar = PreUnfamiliarCheckpoint.Match(e.Data);
            if (preUnfamiliar.Success)
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        AppendShadowTeacherEvent("saved pre-unfamiliar weights: " + preUnfamiliar.Groups["file"].Value +
                            " | novelty " + preUnfamiliar.Groups["score"].Value + " | extremes " + preUnfamiliar.Groups["extreme"].Value);
                        Append(e.Data);
                    });
                }
                return;
            }
            // A chart interval change can create a burst of forwarded depth
            // data and, with some plug-ins, a burst of text telemetry.  UI
            // state only needs a recent sample; bound it to two updates/sec
            // so BeginInvoke work cannot starve the Windows message pump.
            if (IsHighFrequencyTelemetry(e.Data))
            {
                lock (telemetryThrottleLock)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastTelemetryDispatchUtc).TotalMilliseconds < 500.0) return;
                    lastTelemetryDispatchUtc = now;
                }
            }
            BeginInvoke((MethodInvoker)delegate
            {
              try
              {
                Append(e.Data, !IsHighFrequencyTelemetry(e.Data));
                Match signalUpdate = SignalUpdate.Match(e.Data);
                if (signalUpdate.Success)
                {
                    SetMetric(paperSignal, "Current paper signal", signalUpdate.Groups["signal"].Value);
                    paperSignal.ForeColor = SignalColor(signalUpdate.Groups["signal"].Value);
                    SetMetric(probability, "Current up probability", signalUpdate.Groups["probability"].Value);
                    TrackProbabilityRegime(signalUpdate.Groups["probability"].Value);
                    ForwardPaperSignalToNt8(signalUpdate.Groups["signal"].Value);
                }
                // Paper mirror lifecycle: keep the dashboard's view of the open paper trade in
                // step with the engine, and let the same signal re-enter once one closes.
                if (e.Data.StartsWith("host-jitter", StringComparison.Ordinal))
                {
                    // Host scheduling health: a 1 ms sleep that really takes tens of ms means
                    // the whole VM is being descheduled, which caps order latency everywhere.
                    lastHostJitter = "Host CPU: 1 ms sleep took " + e.Data.Substring("host-jitter sleep1-ms ".Length);
                    return;
                }
                if (e.Data.StartsWith("paper-trade-open", StringComparison.Ordinal))
                {
                    paperOpenSide = e.Data.Contains("direction=LONG") ? 1 : -1;
                    RefreshTradePath();
                }
                else if (e.Data.StartsWith("paper-trade-close", StringComparison.Ordinal))
                {
                    paperOpenSide = 0;
                    if (paperMirror.Checked && !tradeArmed) lastForwardedAiSignal = null;   // paper-only: allow re-entry like live does
                    RefreshTradePath();
                }
                Match manualSave = ManualModelSaved.Match(e.Data);
                if (manualSave.Success)
                {
                    saveModelNow.Enabled = learner != null && !learner.HasExited;
                    SetStatus("Manual model snapshot saved at processed frame " + manualSave.Groups["frame"].Value + ".", Color.DarkGreen);
                }
                Match sessionHorizon = SessionHorizonMetrics.Match(e.Data);
                if (sessionHorizon.Success)
                {
                    outcomePanel.AppendText("\r\n" + sessionHorizon.Groups["name"].Value.ToUpperInvariant() +
                        " target " + sessionHorizon.Groups["target"].Value + "t | pending " + sessionHorizon.Groups["pending"].Value +
                        " | U/D/C " + sessionHorizon.Groups["up"].Value + "/" + sessionHorizon.Groups["down"].Value + "/" + sessionHorizon.Groups["censored"].Value +
                        " | updates " + sessionHorizon.Groups["updates"].Value + " | acc/base " + sessionHorizon.Groups["accuracy"].Value + "%/" + sessionHorizon.Groups["baseline"].Value +
                        "% | median/avg hit " + sessionHorizon.Groups["median"].Value + "s/" + sessionHorizon.Groups["mean"].Value + "s");
                    return;
                }
                Match horizons = ThreeOutcomeTimeFrames.Match(e.Data);
                if (horizons.Success)
                {
                    latestOutcomeTimeFramesLine = e.Data;
                    outcomePanel.Text = "FAST   horizon " + horizons.Groups["fast_horizon"].Value + " ms | updates " + horizons.Groups["fast_updates"].Value +
                        " | labels U " + horizons.Groups["fast_up"].Value + " / D " + horizons.Groups["fast_down"].Value + " / N " + horizons.Groups["fast_no_move"].Value +
                        " | snapshot accuracy " + horizons.Groups["fast_accuracy"].Value + "%\r\n" +
                        "MEDIUM horizon " + horizons.Groups["medium_horizon"].Value + " ms | updates " + horizons.Groups["medium_updates"].Value +
                        " | labels U " + horizons.Groups["medium_up"].Value + " / D " + horizons.Groups["medium_down"].Value + " / N " + horizons.Groups["medium_no_move"].Value +
                        " | snapshot accuracy " + horizons.Groups["medium_accuracy"].Value + "%  ← paper signal\r\n" +
                        "SLOW   horizon " + horizons.Groups["slow_horizon"].Value + " ms | updates " + horizons.Groups["slow_updates"].Value +
                        " | labels U " + horizons.Groups["slow_up"].Value + " / D " + horizons.Groups["slow_down"].Value + " / N " + horizons.Groups["slow_no_move"].Value +
                        " | snapshot accuracy " + horizons.Groups["slow_accuracy"].Value + "%";
                }
                Match paperOutcome = PaperOutcomeMetrics.Match(e.Data);
                if (paperOutcome.Success)
                {
                    if (paperOutcome.Groups["overall"].Success)
                    {
                        double oh = 0; double.TryParse(paperOutcome.Groups["overall"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out oh);
                        paperOutcomeStatus.Text = "Paper signal vs real price: overall " + paperOutcome.Groups["overall"].Value + "% correct" +
                            "  |  long " + paperOutcome.Groups["longhit"].Value + "% (n=" + paperOutcome.Groups["nl"].Value + ")" +
                            "  |  short " + paperOutcome.Groups["shorthit"].Value + "% (n=" + paperOutcome.Groups["ns"].Value + ")" +
                            "  — direction only, ignores slippage";
                        paperOutcomeStatus.ForeColor = oh > 50 ? Color.DarkGreen : Color.Firebrick;
                    }
                    else
                        paperOutcomeStatus.Text = "Paper signal vs real price: scoring (" + paperOutcome.Groups["n"].Value + " so far)...";
                    return;
                }
                Match hf = HogFusionMetrics.Match(e.Data);
                if (hf.Success)
                {
                    double aa = 0, ad = 0, fa = 0, ea = 0;
                    double.TryParse(hf.Groups["aa"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out aa);
                    double.TryParse(hf.Groups["ad"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ad);
                    double.TryParse(hf.Groups["fa"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out fa);
                    double.TryParse(hf.Groups["ea"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ea);
                    bool complementary = aa > ad;
                    hogFusionStatus.Text = "Hog fusion [" + hf.Groups["mode"].Value.ToUpperInvariant() + "]: evidence " + hf.Groups["ev"].Value + " (" + hf.Groups["age"].Value + " ms)  |  P hog " + hf.Groups["phog"].Value +
                        "  engine " + hf.Groups["peng"].Value + "  fused " + hf.Groups["pf"].Value + "  |  w engine " + hf.Groups["we"].Value + "  w hog " + hf.Groups["wh"].Value +
                        "  |  " + hf.Groups["labels"].Value + " labels: fused " + hf.Groups["fa"].Value + "%  engine " + hf.Groups["ea"].Value + "%  hog " + hf.Groups["ha"].Value + "%" +
                        "  |  agree " + hf.Groups["ar"].Value + "%  acc agree/disagree " + hf.Groups["aa"].Value + "% / " + hf.Groups["ad"].Value + "%" + (complementary ? " ✓" : "") +
                        "  |  gate " + hf.Groups["gate"].Value + " blocked " + hf.Groups["blocked"].Value;
                    hogFusionStatus.ForeColor = hf.Groups["ev"].Value != "fresh" ? Color.DarkOrange : (fa > ea && complementary) ? Color.DarkGreen : Color.DarkSlateBlue;
                    return;
                }
                Match flicker = FlickerMetrics.Match(e.Data);
                if (flicker.Success)
                {
                    double cwt = 0; double.TryParse(flicker.Groups["cwt"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out cwt);
                    flickerBaseText = "Order flicker: " + flicker.Groups["adds"].Value + " adds/s, " + flicker.Groups["cancels"].Value +
                        " cancels/s, " + flicker.Groups["trades"].Value + " trades/s  |  cancel/add " + flicker.Groups["ratio"].Value +
                        "  |  size pulled-without-trade " + flicker.Groups["cwt"].Value + "%" +
                        (cwt >= 90 ? "  ⚠ heavy phantom/spoof-like churn" : "");
                    flickerStatus.Text = flickerBaseText;
                    flickerStatus.ForeColor = cwt >= 90 ? Color.DarkOrange : Color.DarkSlateBlue;
                    return;
                }
                Match flickerLevels = FlickerLevelMetrics.Match(e.Data);
                if (flickerLevels.Success)
                {
                    double r1 = 0, r2 = 0, r3 = 0;
                    double.TryParse(flickerLevels.Groups["r1"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out r1);
                    double.TryParse(flickerLevels.Groups["r2"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out r2);
                    double.TryParse(flickerLevels.Groups["r3"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out r3);
                    double maxRatio = Math.Max(r1, Math.Max(r2, r3));
                    flickerStatus.Text = flickerBaseText +
                        "   ||  spoof by level (cancel/add) — L1 " + r1.ToString("F2") +
                        "  L2 " + r2.ToString("F2") + "  L3 " + r3.ToString("F2") +
                        (maxRatio >= 3.0 ? "  ⚠ spoof-like at a level" : "");
                    if (maxRatio >= 3.0) flickerStatus.ForeColor = Color.DarkOrange;
                    return;
                }
                Match twoTick = TwoTickMetrics.Match(e.Data);
                if (twoTick.Success)
                {
                    string pUp = twoTick.Groups["pup"].Value;
                    if (twoTick.Groups["realized"].Success)
                    {
                        double skillVal = 0; double.TryParse(twoTick.Groups["skill"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out skillVal);
                        string geomText = (twoTick.Groups["stop"].Success
                            ? "target " + twoTick.Groups["target"].Value + "t / stop " + twoTick.Groups["stop"].Value + "t"
                            : "target " + twoTick.Groups["target"].Value + "t symmetric")
                            + (twoTick.Groups["cost"].Success ? ", at touch, cost " + twoTick.Groups["cost"].Value + "t" : "");
                        twoTickStatus.Text = "Skill meter (" + geomText + "): P(up-first)=" + pUp +
                            "  |  base up-first " + twoTick.Groups["realized"].Value + "%" +
                            "  |  accuracy " + twoTick.Groups["accuracy"].Value + "%" +
                            "  |  SKILL vs base " + twoTick.Groups["skill"].Value + "%" +
                            (skillVal <= 0 ? " (no edge — drift only)" : "") +
                            "  |  up-edge " + twoTick.Groups["upedge"].Value + "% / down-edge " + twoTick.Groups["downedge"].Value + "%" +
                            "  |  lead " + twoTick.Groups["lead"].Value + "ms (n=" + twoTick.Groups["samples"].Value +
                            (twoTick.Groups["untradeable"].Success ? ", " + twoTick.Groups["untradeable"].Value + " untradeable" : "") + ")";
                        twoTickStatus.ForeColor = skillVal > 0 ? Color.DarkGreen : Color.Firebrick;
                        AppendLearningMonitorEvent("2-tick: P(up)=" + pUp +
                            " realized-up-first=" + twoTick.Groups["realized"].Value + "%" +
                            " dir-accuracy=" + twoTick.Groups["accuracy"].Value + "%" +
                            " up-reliability=" + twoTick.Groups["uprel"].Value + "%" +
                            " down-reliability=" + twoTick.Groups["downrel"].Value + "%" +
                            " lead=" + twoTick.Groups["lead"].Value + "ms");
                    }
                    else
                        twoTickStatus.Text = "2-tick (target " + twoTick.Groups["target"].Value + "t): model P(up-first)=" + pUp + "  (calibrating, n=" + twoTick.Groups["samples"].Value + ")";
                    return;
                }
                Match unifiedMatch = UnifiedShadowMetrics.Match(e.Data);
                if (unifiedMatch.Success)
                {
                    latestMetricsLine = e.Data;
                    SetMetric(frames, "Frames", unifiedMatch.Groups["frames"].Value);
                    SetMetric(valid, "Valid books", unifiedMatch.Groups["valid"].Value);
                    SetMetric(trained, "Online updates", unifiedMatch.Groups["trained"].Value);
                    { int t; if (int.TryParse(unifiedMatch.Groups["trained"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) lastTrainedCount = t; }
                    SetMetric(noTrade, "Resolved labels", unifiedMatch.Groups["labels"].Value);
                    SetMetric(accuracy, "Online label accuracy", unifiedMatch.Groups["accuracy"].Value + "%");
                    SetMetric(probability, "Current up probability", unifiedMatch.Groups["probability"].Value);
                    SetMetric(weightNorm, "Weight L2 norm", unifiedMatch.Groups["weight"].Value);
                    string unifiedSignal = unifiedMatch.Groups["signal"].Value;
                    SetMetric(paperSignal, "Current paper signal", unifiedSignal);
                    paperSignal.ForeColor = SignalColor(unifiedSignal);
                    SetMetric(paperSignalCounts, "Paper signal counts",
                        "L " + unifiedMatch.Groups["long_signals"].Value +
                        " / S " + unifiedMatch.Groups["short_signals"].Value +
                        " / N " + unifiedMatch.Groups["no_trade_signals"].Value);
                    TrackProbabilityRegime(unifiedMatch.Groups["probability"].Value);
                    ForwardPaperSignalToNt8(unifiedSignal);
                    SetStatus("Receiving direct MMF depth; unified-shadow learner is updating in memory" +
                        (unifiedSignal == "DISABLED" ? " (signal emission off)." : "."), Color.DarkGreen);
                    return;
                }
                Match mmf130Match = Mmf130FeatureMetrics.Match(e.Data);
                if (mmf130Match.Success)
                {
                    latestMetricsLine = e.Data;
                    SetMetric(frames, "Standardizer frames", mmf130Match.Groups["standardizer"].Value);
                    SetMetric(valid, "Last MMF batch", mmf130Match.Groups["batch"].Value + " events");
                    SetMetric(trained, "Online updates", mmf130Match.Groups["trained"].Value);
                    SetMetric(noTrade, "Resolved labels", mmf130Match.Groups["labeled"].Value);
                    SetMetric(accuracy, "Online direction accuracy", mmf130Match.Groups["accuracy"].Value + "%");
                    SetMetric(majorityBaseline, "Accuracy reference", "50.0% direction baseline");
                    SetMetric(liveValidation, "Validation edge vs 50%", mmf130Match.Groups["edge"].Value + "%");
                    SetMetric(bestValidation, "130-feature MMF learner", "Read-only; no orders or signal/control MMF");
                    SetMetric(probability, "Prediction (ticks)", mmf130Match.Groups["prediction"].Value + " ticks");
                    SetMetric(weightNorm, "Volatility / target", mmf130Match.Groups["volatility"].Value + " / " + mmf130Match.Groups["target"].Value + " ticks");
                    SetMetric(paperSignal, "Current hypothetical signal", mmf130Match.Groups["signal"].Value);
                    SetMetric(paperSignalCounts, "Hypothetical signals", "L " + mmf130Match.Groups["long"].Value + " / S " + mmf130Match.Groups["short"].Value);
                    SetStatus("Receiving direct MMF depth; 130-feature learner is updating in memory.", Color.DarkGreen);
                    return;
                }
                Match onnxMetrics = OnnxInferenceMetrics.Match(e.Data);
                if (onnxMetrics.Success)
                {
                    SetMetric(frames, "ONNX frames", onnxMetrics.Groups["frames"].Value);
                    SetMetric(valid, "Valid / skipped", onnxMetrics.Groups["valid"].Value + " / " + onnxMetrics.Groups["skipped"].Value);
                    SetMetric(trained, "ONNX weight updates", "Offline only");
                    SetMetric(noTrade, "Window warm-up", onnxMetrics.Groups["warmup"].Value);
                    SetMetric(probability, "Medium up probability", onnxMetrics.Groups["medium"].Value);
                    SetMetric(accuracy, "Live label accuracy", "Not scored live");
                    SetMetric(majorityBaseline, "Offline validation", "See package report");
                    SetMetric(liveValidation, "ONNX compute", onnxMetrics.Groups["latency"].Value + " ms");
                    SetMetric(bestValidation, "Tick-augmented ONNX", "Read-only inference");
                    outcomePanel.Text = "FAST up probability " + onnxMetrics.Groups["fast"].Value +
                        "\r\nMEDIUM up probability " + onnxMetrics.Groups["medium"].Value +
                        "\r\nSLOW up probability " + onnxMetrics.Groups["slow"].Value;
                    SetStatus("Receiving dashboard-forwarded MMF frames; tick-augmented ONNX inference is active.", Color.DarkGreen);
                    return;
                }
                Match adaptiveLearningUpdate = AdaptiveLearningUpdate.Match(e.Data);
                if (adaptiveLearningUpdate.Success)
                {
                    AppendLearningMonitorEvent("gradient update=" + adaptiveLearningUpdate.Groups["update"].Value +
                        " batch=" + adaptiveLearningUpdate.Groups["batch"].Value +
                        " resolved=" + adaptiveLearningUpdate.Groups["resolved"].Value +
                        " loss=" + adaptiveLearningUpdate.Groups["loss"].Value +
                        " queue=" + adaptiveLearningUpdate.Groups["queue"].Value +
                        " dropped=" + adaptiveLearningUpdate.Groups["dropped"].Value);
                    return;
                }
                Match adaptiveMetrics = AdaptiveMultiTaskMetrics.Match(e.Data);
                if (adaptiveMetrics.Success)
                {
                    SetMetric(frames, "Adaptive frames", adaptiveMetrics.Groups["frames"].Value);
                    SetMetric(valid, "Valid / skipped", adaptiveMetrics.Groups["valid"].Value + " / " + adaptiveMetrics.Groups["skipped"].Value);
                    SetMetric(trained, "Background updates", adaptiveMetrics.Groups["updates"].Value);
                    SetMetric(noTrade, "Pending / resolved", adaptiveMetrics.Groups["pending"].Value + " / " + adaptiveMetrics.Groups["resolved"].Value);
                    SetMetric(accuracy, "Recent online-label accuracy", adaptiveMetrics.Groups["accuracy"].Value + "%");
                    SetMetric(probability, "Medium up probability", adaptiveMetrics.Groups["medium"].Value);
                    SetMetric(majorityBaseline, "Learning candidates", adaptiveMetrics.Groups["candidates"].Value);
                    SetMetric(liveValidation, "ONNX latency / loss", adaptiveMetrics.Groups["latency"].Value + " ms / " + adaptiveMetrics.Groups["loss"].Value);
                    SetMetric(bestValidation, "Adaptive PyTorch LNN", "Saves local checkpoint");
                    outcomePanel.Text = "FAST up probability " + adaptiveMetrics.Groups["fast"].Value +
                        "\r\nMEDIUM up probability " + adaptiveMetrics.Groups["medium"].Value +
                        "\r\nSLOW up probability " + adaptiveMetrics.Groups["slow"].Value +
                        "\r\nBackground updates " + adaptiveMetrics.Groups["updates"].Value;
                    string queue = adaptiveMetrics.Groups["queue"].Success ? adaptiveMetrics.Groups["queue"].Value : "n/a";
                    string dropped = adaptiveMetrics.Groups["dropped"].Success ? adaptiveMetrics.Groups["dropped"].Value : "n/a";
                    learningMonitorSummary.Text =
                        "Transport: receiving dashboard-forwarded depth frames\r\n" +
                        "Frames: " + adaptiveMetrics.Groups["frames"].Value +
                        "   Valid / skipped: " + adaptiveMetrics.Groups["valid"].Value + " / " + adaptiveMetrics.Groups["skipped"].Value + "\r\n" +
                        "Candidates: " + adaptiveMetrics.Groups["candidates"].Value +
                        "   Pending labels: " + adaptiveMetrics.Groups["pending"].Value +
                        "   Resolved labels: " + adaptiveMetrics.Groups["resolved"].Value + "\r\n" +
                        "Background gradient updates: " + adaptiveMetrics.Groups["updates"].Value +
                        "   Recent label accuracy: " + adaptiveMetrics.Groups["accuracy"].Value + "%\r\n" +
                        "Loss: " + adaptiveMetrics.Groups["loss"].Value +
                        "   ONNX inference: " + adaptiveMetrics.Groups["latency"].Value + " ms\r\n" +
                        "Learning queue / dropped: " + queue + " / " + dropped + "\r\n" +
                        "Up probability fast / medium / slow: " + adaptiveMetrics.Groups["fast"].Value + " / " +
                        adaptiveMetrics.Groups["medium"].Value + " / " + adaptiveMetrics.Groups["slow"].Value;
                    AppendLearningMonitorEvent("telemetry frames=" + adaptiveMetrics.Groups["frames"].Value +
                        " valid=" + adaptiveMetrics.Groups["valid"].Value +
                        " pending=" + adaptiveMetrics.Groups["pending"].Value +
                        " updates=" + adaptiveMetrics.Groups["updates"].Value +
                        " acc=" + adaptiveMetrics.Groups["accuracy"].Value + "% loss=" + adaptiveMetrics.Groups["loss"].Value +
                        " latency=" + adaptiveMetrics.Groups["latency"].Value + "ms");
                    SetStatus("Receiving dashboard-forwarded MMF frames; background MultiTask learning is active.", Color.DarkGreen);
                    return;
                }
                Match reversalFusion = ReversalFusionMetrics.Match(e.Data);
                if (reversalFusion.Success)
                {
                    shadowReversalFusionStatus = "Fusion — shadow only\r\n" +
                        "State: " + reversalFusion.Groups["state"].Value + " | main / local: " + reversalFusion.Groups["main"].Value + " / " + reversalFusion.Groups["local"].Value + "\r\n" +
                        "Main probability: " + reversalFusion.Groups["main_probability"].Value + " | reversal probability: " + reversalFusion.Groups["reversal_probability"].Value + "\r\n" +
                        "Hypothetical fused probability: " + reversalFusion.Groups["hypothetical_probability"].Value + " | reversal labels: " + reversalFusion.Groups["resolved"].Value +
                        " (never changes primary weights or paper signal)";
                    RefreshShadowTeacherSummary();
                    UpdateReversalTab("fusion=" + reversalFusion.Groups["state"].Value +
                        " main=" + reversalFusion.Groups["main"].Value + " local=" + reversalFusion.Groups["local"].Value +
                        " hypothetical=" + reversalFusion.Groups["hypothetical_probability"].Value);
                    AppendShadowTeacherEvent("reversal fusion=" + reversalFusion.Groups["state"].Value +
                        " main=" + reversalFusion.Groups["main"].Value + " local=" + reversalFusion.Groups["local"].Value +
                        " hypothetical=" + reversalFusion.Groups["hypothetical_probability"].Value);
                    return;
                }
                Match reversalMetrics = ReversalMetrics.Match(e.Data);
                if (reversalMetrics.Success)
                {
                    shadowReversalStatus = "Reversal tracker — shadow only\r\n" +
                        "Active / resolved: " + reversalMetrics.Groups["scenarios"].Value + " / " + reversalMetrics.Groups["resolved"].Value +
                        "   Reversal / trend / timeout: " + reversalMetrics.Groups["reversals"].Value + " / " + reversalMetrics.Groups["trends"].Value + " / " + reversalMetrics.Groups["timeouts"].Value + "\r\n" +
                        "Predicted reversals: " + reversalMetrics.Groups["predictions"].Value +
                        "   Accuracy / recall proxy: " + reversalMetrics.Groups["accuracy"].Value + "% / " + reversalMetrics.Groups["recall"].Value + "%\r\n" +
                        "Current reversal probability: " + reversalMetrics.Groups["probability"].Value +
                        (ReversalExtra(e.Data)) +
                        "   (diagnostic; cannot change primary weights or signals)";
                    RefreshShadowTeacherSummary();
                    UpdateReversalTab("resolved=" + reversalMetrics.Groups["resolved"].Value +
                        " reversals=" + reversalMetrics.Groups["reversals"].Value +
                        " trends=" + reversalMetrics.Groups["trends"].Value +
                        " timeouts=" + reversalMetrics.Groups["timeouts"].Value +
                        " accuracy=" + reversalMetrics.Groups["accuracy"].Value + "% rev-prob=" + reversalMetrics.Groups["probability"].Value);
                    AppendShadowTeacherEvent("reversal resolved=" + reversalMetrics.Groups["resolved"].Value +
                        " reversals=" + reversalMetrics.Groups["reversals"].Value +
                        " trends=" + reversalMetrics.Groups["trends"].Value +
                        " timeouts=" + reversalMetrics.Groups["timeouts"].Value +
                        " accuracy=" + reversalMetrics.Groups["accuracy"].Value + "%");
                    return;
                }
                Match match = Metrics.Match(e.Data);
                if (!match.Success) return;
                latestMetricsLine = e.Data;
                SetMetric(frames, "Frames", match.Groups["frames"].Value);
                // Feed the Learning monitor tab from the native learner's own
                // summary (v12 / unified-shadow don't emit "adaptive" lines).
                Match validationForMonitor = ValidationMetrics.Match(e.Data);
                string monitorSummary =
                    "Native learner — live learning progress\r\n\r\n" +
                    "Frames processed : " + match.Groups["frames"].Value + "\r\n" +
                    "Valid books      : " + match.Groups["valid"].Value + "\r\n" +
                    "Online updates   : " + match.Groups["trained"].Value + "\r\n" +
                    "Resolved labels  : U " + match.Groups["up"].Value + " / D " + match.Groups["down"].Value + "\r\n" +
                    "Label accuracy   : " + match.Groups["accuracy"].Value + "%  (baseline " + match.Groups["baseline"].Value + "%)\r\n" +
                    "Up-probability   : " + match.Groups["probability"].Value + "\r\n" +
                    "Weight L2 norm   : " + match.Groups["weight"].Value +
                    (validationForMonitor.Success
                        ? "\r\nValidation edge  : " + validationForMonitor.Groups["edge"].Value + "%  (best " + validationForMonitor.Groups["best"].Value +
                          "%, n=" + validationForMonitor.Groups["samples"].Value + ")"
                        : "\r\nValidation edge  : accumulating held-out labels...") +
                    "\r\nPaper signal     : " + match.Groups["signal"].Value + "  (L " + match.Groups["long_signals"].Value +
                    " / S " + match.Groups["short_signals"].Value + " / N " + match.Groups["no_trade_signals"].Value + ")";
                learningMonitorSummary.Text = monitorSummary;
                int trainedForEvent;
                { int t; if (int.TryParse(match.Groups["trained"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) lastTrainedCount = t; }
                { Match hz = Regex.Match(e.Data, @"dynamic-horizon-ms=(?<h>\d+)"); int h; if (hz.Success && int.TryParse(hz.Groups["h"].Value, out h) && h > 0) latestHorizonMs = h; }
                if (int.TryParse(match.Groups["trained"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out trainedForEvent) &&
                    trainedForEvent > 0 && trainedForEvent % 100 == 0 && trainedForEvent != lastMonitorTrainedMilestone)
                {
                    lastMonitorTrainedMilestone = trainedForEvent;
                    AppendLearningMonitorEvent("trained=" + trainedForEvent + " accuracy=" + match.Groups["accuracy"].Value +
                        "% baseline=" + match.Groups["baseline"].Value + "% prob=" + match.Groups["probability"].Value +
                        (validationForMonitor.Success ? " val-edge=" + validationForMonitor.Groups["edge"].Value + "%" : ""));
                }
                Match performanceMatch = PerformanceMetrics.Match(e.Data);
                Match invalidBookMatch = InvalidBookMetrics.Match(e.Data);
                Match integrityMatch = IntegrityMetrics.Match(e.Data);
                Match latencyMatch = LatencyMetrics.Match(e.Data);
                SetMetric(valid, performanceMatch.Success ? "Valid / processed / skipped" : "Valid books",
                    performanceMatch.Success ? match.Groups["valid"].Value + " / " + performanceMatch.Groups["processed"].Value + " / " +
                    (integrityMatch.Success ? "L " + integrityMatch.Groups["locked"].Value + " / C " + integrityMatch.Groups["corrupt"].Value :
                    invalidBookMatch.Success ? invalidBookMatch.Groups["invalid"].Value : "0") : match.Groups["valid"].Value);
                bool threeOutcome = match.Groups["no_move"].Success;
                SetMetric(trained, threeOutcome ? "Medium 3-outcome updates" : "Online updates", match.Groups["trained"].Value);
                SetMetric(noTrade, threeOutcome ? "Medium no-move labels" : "No-trade outcomes", match.Groups["skipped"].Value);
                SetMetric(upPredictionCount, "Up predictions", match.Groups["up_predictions"].Value + " / " + match.Groups["predictions"].Value);
                SetMetric(upPredictionRate, "Up prediction rate", match.Groups["up_rate"].Value + "%");
                SetMetric(downPredictionCount, "Down predictions", match.Groups["down_predictions"].Value + " / " + match.Groups["predictions"].Value);
                SetMetric(downPredictionRate, "Down prediction rate", match.Groups["down_rate"].Value + "%");
                SetMetric(accuracy, threeOutcome ? "Medium 3-outcome accuracy" : "Online label accuracy", match.Groups["accuracy"].Value + "%");
                SetMetric(majorityBaseline, threeOutcome ? "Medium 3-outcome baseline" : "Majority baseline", match.Groups["baseline"].Value + "%");
                SetMetric(probability, "Current up probability", match.Groups["probability"].Value);
                SetMetric(weightNorm, "Weight L2 norm", match.Groups["weight"].Value);
                Match validationMatch = ValidationMetrics.Match(e.Data);
                if (validationMatch.Success)
                {
                    SetMetric(bestValidation, "Best medium unseen weights", "edge " + validationMatch.Groups["best"].Value + "% | current " + validationMatch.Groups["edge"].Value + "% | " + validationMatch.Groups["samples"].Value + "/" + validationMatch.Groups["window"].Value + " labels");
                    SetMetric(liveValidation, "Medium unseen-label check", validationMatch.Groups["accuracy"].Value + "% | base " + validationMatch.Groups["baseline"].Value + "% | edge " + validationMatch.Groups["edge"].Value + "% | n=" + validationMatch.Groups["samples"].Value);
                }
                SetMetric(paperSignal, "Current paper signal", match.Groups["signal"].Value);
                paperSignal.ForeColor = SignalColor(match.Groups["signal"].Value);
                ForwardPaperSignalToNt8(match.Groups["signal"].Value);
                SetMetric(paperSignalCounts, "Paper signal counts", "L " + match.Groups["long_signals"].Value + " / S " + match.Groups["short_signals"].Value + " / N " + match.Groups["no_trade_signals"].Value);
                Match paperMatch = PaperMetrics.Match(e.Data);
                if (paperMatch.Success)
                {
                    latestPaperMetricsLine = e.Data;
                    SetMetric(paperSignalCounts, "Paper signals / results", "L " + match.Groups["long_signals"].Value + " / S " + match.Groups["short_signals"].Value + " / N " + match.Groups["no_trade_signals"].Value + " | W " + paperMatch.Groups["wins"].Value + " / L " + paperMatch.Groups["losses"].Value + " (" + paperMatch.Groups["win_rate"].Value + "%)");
                    SetMetric(paperTradeSummary, "Paper results (win / loss)", paperMatch.Groups["trades"].Value + " completed; W " + paperMatch.Groups["wins"].Value + " / L " + paperMatch.Groups["losses"].Value + "; " + paperMatch.Groups["win_rate"].Value + "%");
                    SetMetric(paperTradePnl, "Paper net P/L", paperMatch.Groups["net_ticks"].Value + " ticks; DD " + paperMatch.Groups["drawdown"].Value + "; open " + paperMatch.Groups["open"].Value + "; sim " + (paperMatch.Groups["enabled"].Value == "1" ? "ON" : "OFF"));
                    // AI Trader buttons controlled by StartAiTrader/StopAiTrader only.
                    double netTicksParsed;
                    if (double.TryParse(paperMatch.Groups["net_ticks"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out netTicksParsed))
                    {
                        latestPaperNetTicks = netTicksParsed;
                        int tradesParsed;
                        int.TryParse(paperMatch.Groups["trades"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out tradesParsed);
                        latestPaperTradeCount = tradesParsed;
                        if (paperPnlHistory.Count == 0 || Math.Abs(paperPnlHistory[paperPnlHistory.Count - 1] - netTicksParsed) > 0.0001)
                        {
                            paperPnlHistory.Add(netTicksParsed);
                            if (paperPnlHistory.Count > 2000) paperPnlHistory.RemoveRange(0, 500);
                            pnlSpark.Invalidate();
                        }
                        CheckPaperGuards();
                        UpdateDivergenceLabel();
                    }
                }
                if (performanceMatch.Success)
                    SetStatus("Receiving live depth; heavy work every " + performanceMatch.Groups["interval"].Value + " ms, skipped snapshots " + performanceMatch.Groups["skipped"].Value + ".", Color.DarkGreen);
                else
                    SetStatus("Receiving live depth and learning in memory.", Color.DarkGreen);
                Match backlogMatch = Regex.Match(e.Data, @"ring-backlog-events=(?<b>[0-9.]+/[0-9.]+/[0-9.]+|waiting|n/a)");
                if (latencyMatch.Success)
                {
                    latencyPanel.Text = "Samples: frame transport " + latencyMatch.Groups["frames"].Value + " | completed predictions " + latencyMatch.Groups["predictions"].Value + "\r\n" +
                        "Feed event age*       " + latencyMatch.Groups["feed"].Value + " (unavailable when NinjaTrader has no provider timestamp)\r\n" +
                        "NT callback → publish " + latencyMatch.Groups["publish"].Value + " | publish → receiver " + latencyMatch.Groups["receive"].Value + "\r\n" +
                        "Receiver → prediction " + latencyMatch.Groups["compute"].Value + " | callback → prediction " + latencyMatch.Groups["total"].Value + "\r\n" +
                        (backlogMatch.Success ? "Ring backlog (events waiting when a frame was taken, p50/p95/p99): " + backlogMatch.Groups["b"].Value + "  — 0-1 = keeping up; tens = queueing in bursts\r\n" : "") +
                        (lastHostJitter.Length > 0 ? lastHostJitter + "\r\n" : "") +
                        "*Event age depends on the provider timestamp; it is not guaranteed exchange latency.";
                }
              }
              catch (Exception uiError)
              {
                  // One failing sub-update must never abort the whole telemetry
                  // refresh (this is what made metrics freeze until a tab visit).
                  try { Append("UI update skipped: " + uiError.Message, false); } catch { }
              }
            });
        }

        private void LearnerError(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            // ALWAYS buffer the last ~80 stderr lines unthrottled — a startup
            // crash dumps its stack in a burst, and the display throttle below
            // would otherwise drop the one thing we need to see. LearnerExited
            // flushes this tail when the engine dies abnormally.
            bool show;
            lock (telemetryThrottleLock)
            {
                learnerStderrTail.Add(e.Data);
                if (learnerStderrTail.Count > 80) learnerStderrTail.RemoveRange(0, learnerStderrTail.Count - 80);
                DateTime now = DateTime.UtcNow;
                show = (now - lastLearnerErrorDispatchUtc).TotalMilliseconds >= 500.0;
                if (show) lastLearnerErrorDispatchUtc = now;
            }
            if (show && !IsDisposed && IsHandleCreated)
                BeginInvoke((MethodInvoker)delegate { Append("ERROR: " + e.Data); });
        }

        private void LearnerExited(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            // Capture exit info off the process before the UI thread runs.
            int exitCode = 0; bool haveCode = false;
            try { Process pr = sender as Process; if (pr != null) { exitCode = pr.ExitCode; haveCode = true; } } catch { }
            double aliveSeconds = learnerStartedUtc == DateTime.MinValue ? 999 : (DateTime.UtcNow - learnerStartedUtc).TotalSeconds;
            bool abnormal = (haveCode && exitCode != 0) || aliveSeconds < 5.0;
            BeginInvoke((MethodInvoker)delegate
            {
                if (abnormal)
                {
                    Append("──────── ENGINE EXITED" + (haveCode ? " (exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + ")" : "") +
                        " after " + aliveSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s ────────");
                    string[] tail;
                    lock (telemetryThrottleLock) { tail = learnerStderrTail.ToArray(); }
                    if (tail.Length == 0) Append("Engine printed nothing to stderr before exiting (often a missing dependency, a bad --arg, or an incompatible weights checkpoint).");
                    else { Append("Last engine output before it died:"); foreach (string s in tail) Append("   " + s); }
                    try
                    {
                        string crashFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lnn_engine_crash.txt");
                        File.WriteAllText(crashFile, "Engine exit code: " + (haveCode ? exitCode.ToString(CultureInfo.InvariantCulture) : "unknown") +
                            "  alive " + aliveSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s\r\n\r\n" + string.Join("\r\n", tail));
                        Append("Full crash log written to: " + crashFile);
                    }
                    catch { }
                    Append("Most common cause after switching weights: the selected checkpoint/category is incompatible with this engine. Try Start with Load weights blank (or the category that worked) to confirm.");
                }
                if (learner == null || learner.HasExited)
                {
                    StopCompactTemporalTeacher();
                    StopTemporalObserver();
                    StopCrossMarketShadowLogger();
                    StopPluginMmfForwarder();
                    start.Enabled = true;
                    stop.Enabled = false;
                    startPaperSimulation.Enabled = false;
                    stopPaperSimulation.Enabled = false;
                    saveModelNow.Enabled = false;
                    instrument.Enabled = true;
                    engine.Enabled = true;
                    browseEngine.Enabled = true;
                    pythonExecutable.Enabled = true;
                    browsePython.Enabled = true;
                    tickSizeInput.Enabled = true;
                    estimatedCostTicks.Enabled = true;
                    minimumProfitTargetTicks.Enabled = true;
                    minimumLabelDeadlineMilliseconds.Enabled = true;
                    maximumLabelDeadlineMilliseconds.Enabled = true;
                    reversalTriggerTicks.Enabled = true;
                    reversalDistanceTicks.Enabled = true;
                    reversalTrendTicks.Enabled = true;
                    reversalTimeoutMilliseconds.Enabled = true;
                    reversalProbabilityThreshold.Enabled = true;
                    observerFastTicks.Enabled = true;
                    observerFastMilliseconds.Enabled = true;
                    observerMediumTicks.Enabled = true;
                    observerMediumMilliseconds.Enabled = true;
                    observerSlowTicks.Enabled = true;
                    observerSlowMilliseconds.Enabled = true;
                    paperLongThreshold.Enabled = true;
                    paperShortThreshold.Enabled = true;
                    processingIntervalMilliseconds.Enabled = true;
                    saveRawFrames.Enabled = true;
                    saveDashboardLog.Enabled = true;
                    saveFullWeightAudit.Enabled = true;
                    saveWeights.Enabled = true;
                    loadCheckpointFile.Enabled = true;
                    browseLoadCheckpoint.Enabled = true;
                    captureFolder.Enabled = true;
                    browseCapture.Enabled = true;
                    trainingProfile.Enabled = true;
                    weightCategory.Enabled = true;
                    trainingHorizonSeconds.Enabled = true;
                    trainingSymbol.Enabled = true;
                    trainingInstrumentId.Enabled = true;
                    activeWeightRoot.Enabled = true;
                    browseActiveWeightRoot.Enabled = true;
                    autoAdoptActiveWeights.Enabled = true;
                    trainPythonProfile.Enabled = true;
                    CloseLogWriter();
                    SetStatus("Receiver stopped.", Color.DimGray);
                }
            });
        }

        private void StopLearner()
        {
            StopCompactTemporalTeacher();
            StopTemporalObserver();
            StopCrossMarketShadowLogger();
            StopPluginMmfForwarder();
            Process local = learner;
            learner = null;
            if (local != null)
            {
                try { if (!local.HasExited) { local.Kill(); local.WaitForExit(3000); } }
                catch (InvalidOperationException) { }
                local.Dispose();
            }
            engineDirectActive = false;
            // Sweep any lingering engine/forwarder (including orphans from a prior
            // dashboard session) so the MMF + event handle are fully released.
            EndLingeringEngineProcesses(true);
            start.Enabled = true;
            stop.Enabled = false;
            startPaperSimulation.Enabled = false;
            stopPaperSimulation.Enabled = false;
            saveModelNow.Enabled = false;
            instrument.Enabled = true;
            engine.Enabled = true;
            browseEngine.Enabled = true;
            pythonExecutable.Enabled = true;
            browsePython.Enabled = true;
            tickSizeInput.Enabled = true;
            estimatedCostTicks.Enabled = true;
            minimumProfitTargetTicks.Enabled = true;
            minimumLabelDeadlineMilliseconds.Enabled = true;
            maximumLabelDeadlineMilliseconds.Enabled = true;
            reversalTriggerTicks.Enabled = true;
            reversalDistanceTicks.Enabled = true;
            reversalTrendTicks.Enabled = true;
            reversalTimeoutMilliseconds.Enabled = true;
            reversalProbabilityThreshold.Enabled = true;
            observerFastTicks.Enabled = true;
            observerFastMilliseconds.Enabled = true;
            observerMediumTicks.Enabled = true;
            observerMediumMilliseconds.Enabled = true;
            observerSlowTicks.Enabled = true;
            observerSlowMilliseconds.Enabled = true;
            paperLongThreshold.Enabled = true;
            paperShortThreshold.Enabled = true;
            processingIntervalMilliseconds.Enabled = true;
            saveRawFrames.Enabled = true;
            saveDashboardLog.Enabled = true;
            saveFullWeightAudit.Enabled = true;
            saveWeights.Enabled = true;
            loadCheckpointFile.Enabled = true;
            browseLoadCheckpoint.Enabled = true;
            captureFolder.Enabled = true;
            browseCapture.Enabled = true;
            trainingProfile.Enabled = true;
            weightCategory.Enabled = true;
            trainingHorizonSeconds.Enabled = true;
            trainingSymbol.Enabled = true;
            trainingInstrumentId.Enabled = true;
            activeWeightRoot.Enabled = true;
            browseActiveWeightRoot.Enabled = true;
            autoAdoptActiveWeights.Enabled = true;
            trainPythonProfile.Enabled = true;
            CloseLogWriter();
        }

        private static void RealizeChildren(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                try { IntPtr forceHandle = child.Handle; } catch { }
                if (child.HasChildren) RealizeChildren(child);
            }
        }

        // Detect LNN engine processes left running from a previous session (an
        // abrupt dashboard exit can orphan its child engine — still reading the
        // MMF and, if it was the armed AI-trader path, potentially still able to
        // send NT8 commands). Offer to end them so a stale engine can't act.
        private static readonly string[] EngineProcessNames =
        {
            "LnnLiveLearner_HybridTemporalMmf_v12",
            "LnnLiveLearner_MmfUnifiedShadow",
            "LnnDashboardPluginForwarder"
        };

        private void CheckForOrphanedEngines()
        {
            try
            {
                int self = Process.GetCurrentProcess().Id;
                List<Process> orphans = new List<Process>();
                foreach (string name in EngineProcessNames)
                {
                    try
                    {
                        foreach (Process p in Process.GetProcessesByName(name))
                            if (p.Id != self) orphans.Add(p);
                    }
                    catch { }
                }
                if (orphans.Count == 0) return;
                System.Text.StringBuilder list = new System.Text.StringBuilder();
                foreach (Process p in orphans)
                {
                    string started;
                    try { started = p.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture); } catch { started = "unknown"; }
                    list.AppendLine("  • " + p.ProcessName + " (PID " + p.Id.ToString(CultureInfo.InvariantCulture) + ", started " + started + ")");
                }
                DialogResult choice = MessageBox.Show(this,
                    orphans.Count.ToString(CultureInfo.InvariantCulture) + " LNN engine process(es) from a previous run are still running:\r\n\r\n" +
                    list.ToString() + "\r\nThese can still read the MMF and, if armed, may still reach NT8.\r\nEnd them now?",
                    "Orphaned engine detected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                if (choice != DialogResult.Yes) { Append("Left " + orphans.Count + " orphaned engine process(es) running (user choice)."); return; }
                foreach (Process p in orphans)
                {
                    try { if (!p.HasExited) p.Kill(); Append("Ended orphaned engine " + p.ProcessName + " (PID " + p.Id.ToString(CultureInfo.InvariantCulture) + ")."); }
                    catch (Exception ex) { Append("Could not end " + p.ProcessName + " (PID " + p.Id.ToString(CultureInfo.InvariantCulture) + "): " + ex.Message); }
                }
            }
            catch { /* best-effort cleanup; never block startup */ }
        }

        // Forcibly end any lingering engine/forwarder process (from this run OR a
        // previous dashboard instance) and WAIT for it to release the MMF + event
        // handle, so a restart can take the ring over cleanly. Called on Stop and
        // before every Start — this is what prevents the "connects as if it's
        // still running" stale-ring state after a Stop.
        private void EndLingeringEngineProcesses(bool announce)
        {
            try
            {
                int self = Process.GetCurrentProcess().Id;
                foreach (string name in EngineProcessNames)
                {
                    Process[] found;
                    try { found = Process.GetProcessesByName(name); } catch { continue; }
                    foreach (Process p in found)
                    {
                        if (p.Id == self) { p.Dispose(); continue; }
                        try
                        {
                            if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); }
                            if (announce) Append("Ended lingering engine " + p.ProcessName + " (PID " + p.Id.ToString(CultureInfo.InvariantCulture) + ").");
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
            }
            catch { }
        }

        private void DashboardFormClosing(object sender, FormClosingEventArgs e)
        {
            SavePersistedNumberInputs();
            SaveUiState();
            StopAiTrader();
            SendBridgeCommand('T');
            StopLearner();
            StopHog(false);
            DisconnectTradeHub();
        }

        private Dictionary<string, TextBox> EditableNumberInputs()
        {
            return new Dictionary<string, TextBox>(StringComparer.Ordinal)
            {
                { "tick_size", tickSizeInput },
                { "round_trip_fee_dollars", estimatedCostTicks },
                { "minimum_profit_target_ticks", minimumProfitTargetTicks },
                { "minimum_label_deadline_ms", minimumLabelDeadlineMilliseconds },
                { "maximum_label_deadline_ms", maximumLabelDeadlineMilliseconds },
                { "reversal_trigger_ticks", reversalTriggerTicks },
                { "reversal_distance_ticks", reversalDistanceTicks },
                { "reversal_trend_ticks", reversalTrendTicks },
                { "reversal_timeout_ms", reversalTimeoutMilliseconds },
                { "reversal_probability_threshold", reversalProbabilityThreshold },
                { "paper_long_threshold", paperLongThreshold },
                { "paper_short_threshold", paperShortThreshold },
                { "processing_interval_ms", processingIntervalMilliseconds },
                { "observer_fast_ticks", observerFastTicks },
                { "observer_fast_deadline_ms", observerFastMilliseconds },
                { "observer_medium_ticks", observerMediumTicks },
                { "observer_medium_deadline_ms", observerMediumMilliseconds },
                { "observer_slow_ticks", observerSlowTicks },
                { "observer_slow_deadline_ms", observerSlowMilliseconds },
                { "training_horizon_seconds", trainingHorizonSeconds },
                { "training_instrument_id", trainingInstrumentId },
                { "ai_qty", aiQty },
                { "ai_trail_ticks", aiTrailTicks },
                { "daily_loss_limit_ticks", dailyLossLimitTicks },
                { "max_trades_per_session", maxTradesPerSession },
                { "regime_max_flips", regimeMaxFlips },
                { "learning_rate", learningRateInput },
                { "validation_stride", validationStrideInput },
                { "resting_depth_ms", restingDepthInput },
                { "prob_smoothing", probSmoothingInput },
                { "regime_min_range", regimeMinRangeInput },
                { "regime_max_novelty", regimeMaxNoveltyInput },
                { "session_start", sessionStartInput },
                { "session_end", sessionEndInput },
                { "cooldown_seconds", cooldownSecondsInput },
                { "max_consecutive_losses", maxConsecutiveLossesInput },
                { "signal_hold_seconds", signalHoldSecondsInput },
                { "live_confidence_margin", liveConfidenceMarginInput },
                { "ramp_wins_per_step", rampWinsPerStepInput },
                { "nt8_max_position", ntMaxPosInput },
                { "nt8_daily_loss_ticks", ntLossLimitInput },
                { "nt8_default_sl_ticks", ntDefSlInput },
                { "nt8_default_pt_ticks", ntDefPtInput },
                { "nt8_default_trail_ticks", ntDefTrailInput },
                { "min_seconds_between_trades", minSecondsBetweenTradesInput },
                { "exec_min_imbalance", exImbalanceInput },
                { "exec_resting_ms", exRestingMsInput },
                { "exec_max_flicker", exFlickerInput },
                { "exec_aggr_qty", exAggrQtyInput },
                { "exec_aggr_window_ms", exAggrWindowInput },
                { "exec_max_chase_ticks", exChaseInput },
                { "exec_timeout_ms", exTimeoutInput },
                { "exec_markout1_ms", exMark1Input },
                { "exec_markout2_ms", exMark2Input },
                { "exec_cancel_ahead", exCancelAheadInput },
                { "exec_expected_move", exExpectedMoveInput },
                { "paper_max_open", paperMaxOpenInput },
                { "neutral_block_seconds", neutralBlockSecondsInput },
                { "paper_entry_latency_ms", paperEntryLatencyInput },
                { "label_latency_ms", labelLatencyInput },
                { "hog_exe", hogExeInput },
                { "hog_map", hogMapInput },
                { "hog_hidden", hogHiddenInput },
                { "hog_pass_ms", hogPassMsInput },
                { "hog_target_params", hogTargetParamsInput },
                { "hog_exclude_core", hogExcludeCoreInput },
                { "hog_timescales", hogTimescalesInput },
                { "hog_min_horizon_ms", hogMinHorizonInput },
                { "hog_max_horizon_ms", hogMaxHorizonInput },
                { "coalesce_above", coalesceAboveInput }
            };
        }

        // ====================================================================
        // Tooltips that stay up for as long as the mouse is on the control
        // ====================================================================
        // WinForms' built-in ToolTip auto-hides after at most ~32 s (its AutoPopDelay
        // is a 16-bit value inside the common control), which is too short for the
        // long explanations on this dashboard. Tip() shows the text itself, 450 ms
        // after the mouse enters, just below the control, and hides it only when the
        // mouse leaves or a button is pressed - no time limit. Text is word-wrapped
        // at ~120 columns so it never runs off the screen.
        private readonly ToolTip stickyTip = new ToolTip { ShowAlways = true, UseAnimation = false, UseFading = false, InitialDelay = 30000, AutoPopDelay = 32767, ReshowDelay = 30000 };
        private readonly Dictionary<Control, string> stickyTipText = new Dictionary<Control, string>();
        private readonly System.Windows.Forms.Timer stickyTipTimer = new System.Windows.Forms.Timer { Interval = 450 };
        private Control stickyTipPending, stickyTipShown;
        private bool stickyTipWired;

        private readonly Dictionary<Control, string> stickyTipRaw = new Dictionary<Control, string>();

        // v64m: Tip() is idempotent. UpdateLatencyGateLabel() calls Tip() on the
        // latency label every time it repaints - every ack, every fill, and every
        // 2 s from the watchdog - and each call used to add three more mouse
        // handlers to the same control and re-wrap the text. After an hour that is
        // thousands of live delegates on one label, all firing on every mouse move
        // over it. Now the handlers are attached once per control and identical
        // text is a no-op.
        private void Tip(Control c, string text)
        {
            if (c == null) return;
            string prior;
            bool known = stickyTipRaw.TryGetValue(c, out prior);
            if (known && string.Equals(prior, text, StringComparison.Ordinal)) return;
            stickyTipRaw[c] = text;
            stickyTipText[c] = WrapTip(text, 120);
            if (!stickyTipWired) { stickyTipWired = true; stickyTipTimer.Tick += StickyTipTick; }
            if (known) return;   // handlers are already attached; only the text changed
            c.MouseEnter += StickyTipEnter;
            c.MouseLeave += StickyTipLeave;
            c.MouseDown += delegate { StickyTipLeave(c, EventArgs.Empty); };
        }

        private static string WrapTip(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return text;
            StringBuilder sb = new StringBuilder();
            foreach (string paragraph in text.Replace("\r\n", "\n").Split(new char[] { '\n' }))
            {
                int col = 0;
                foreach (string word in paragraph.Split(new char[] { ' ' }))
                {
                    if (col > 0 && col + 1 + word.Length > width) { sb.Append('\n'); col = 0; }
                    else if (col > 0) { sb.Append(' '); col++; }
                    sb.Append(word); col += word.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private void StickyTipEnter(object sender, EventArgs e)
        {
            stickyTipPending = sender as Control;
            stickyTipTimer.Stop(); stickyTipTimer.Start();
        }

        private void StickyTipLeave(object sender, EventArgs e)
        {
            stickyTipTimer.Stop(); stickyTipPending = null;
            HideStickyTip();
        }

        private void HideStickyTip()
        {
            if (stickyTipShown == null) return;
            try { stickyTip.Hide(stickyTipShown); } catch { }
            stickyTipShown = null;
        }

        private void StickyTipTick(object sender, EventArgs e)
        {
            stickyTipTimer.Stop();
            Control c = stickyTipPending; string text;
            if (c == null || c.IsDisposed || !c.Visible || !stickyTipText.TryGetValue(c, out text)) return;
            try
            {
                if (!c.ClientRectangle.Contains(c.PointToClient(Cursor.Position))) return; // mouse already gone
                Size size = TextRenderer.MeasureText(text, c.Font);
                Rectangle work = Screen.FromControl(c).WorkingArea;
                Point at = c.PointToScreen(new Point(0, c.Height + 4));
                if (at.X + size.Width + 24 > work.Right) at.X = Math.Max(work.Left, work.Right - size.Width - 24);
                if (at.Y + size.Height + 16 > work.Bottom) at.Y = Math.Max(work.Top, c.PointToScreen(Point.Empty).Y - size.Height - 16);
                HideStickyTip();
                stickyTip.Show(text, c, c.PointToClient(at));   // no duration: stays until HideStickyTip()
                stickyTipShown = c;
            }
            catch { }
        }

        // ====================================================================
        // Quality-of-life helpers
        // ====================================================================

        // ----- Stream health probe -----
        // Answers "is the bridge actually publishing?" without starting the
        // learner. MMF instruments: open the map and watch for byte changes.
        // Pipe instruments: check whether the named pipe currently exists.

        private void CheckStreamHealthClick(object sender, EventArgs e)
        {
            if (streamCheckRunning) return;
            int idx = instrument.SelectedIndex;
            if (idx < 0)
            {
                SetStatus("Select an instrument first.", Color.Firebrick);
                return;
            }

            string mmfName =
                idx == 7 ? @"Local\LNN_DOM_MMF_MNQ" :
                idx == 8 ? @"Local\LNN_DOM_MMF_MES" : null;
            string pipeName =
                idx == 1 ? "MesDepthBridge" :
                idx == 2 ? "MesDepthEvents" :
                idx == 3 ? "MnqDepthBridge" :
                idx == 4 ? "MnqDepthEvents" :
                idx == 5 ? "EsDepthBridge" :
                idx == 6 ? "M6eDepthEvents" : "LnnDepthBridge";

            if (mmfName == null)
            {
                bool pipeExists = false;
                try { pipeExists = File.Exists(@"\\.\pipe\" + pipeName); } catch { }
                if (pipeExists)
                    SetStatus("Stream check: pipe \\\\.\\pipe\\" + pipeName + " EXISTS — the NT8 bridge is up. (Data flow is confirmed once the receiver starts.)", Color.DarkGreen);
                else
                    SetStatus("Stream check: pipe \\\\.\\pipe\\" + pipeName + " NOT FOUND — the NT8 bridge for this instrument is not running.", Color.Firebrick);
                return;
            }

            streamCheckRunning = true;
            checkStream.Enabled = false;
            SetStatus("Stream check: probing " + mmfName + " for ~2 seconds...", Color.DimGray);
            string mapName = mmfName;
            Thread probe = new Thread(delegate()
            {
                string resultText;
                Color resultColor;
                try
                {
                    using (MemoryMappedFile map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read))
                    using (MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                    {
                        // Validate the ring header the way the engine does, so "updating" can never
                        // hide a writer-version mismatch (the engine rejects an unexpected layout).
                        int cap = view.ReadInt32(8), slot = view.ReadInt32(12); long magic = view.ReadInt64(16);
                        bool layoutOk = magic == 0x4C4E4E4D4D463031L && cap >= 2 && (cap & (cap - 1)) == 0 && slot == 48;
                        string layout = " [magic=0x" + magic.ToString("X") + " capacity=" + cap + " slot-bytes=" + slot + (layoutOk ? " OK]" : " MISMATCH - engine expects magic=0x4C4E4E4D4D463031 slot-bytes=48; recompile LnnDomMmfWriter from this package]");
                        // Count real events: the writer advances the header write-sequence once
                        // per published depth/trade event, so the delta over the probe window is
                        // the event rate the engine will see.
                        long seq0 = view.ReadInt64(0);
                        Thread.Sleep(2000);
                        long seq1 = view.ReadInt64(0);
                        long delta = seq1 - seq0;
                        string last = "";
                        if (layoutOk && seq1 > 0)
                        {
                            // Decode the most recent slot (48 B: seqlock, event-time, callback-utc, price, volume, side, op, position).
                            long off = 64 + (seq1 & (cap - 1)) * slot;
                            long stamp = view.ReadInt64(off);
                            if (stamp == seq1 * 2)
                            {
                                long eventTicks = view.ReadInt64(off + 8), callbackTicks = view.ReadInt64(off + 16);
                                double price = view.ReadDouble(off + 24); long volume = view.ReadInt64(off + 32);
                                byte side = view.ReadByte(off + 40), op = view.ReadByte(off + 41); short pos = view.ReadInt16(off + 42);
                                double ageS = callbackTicks > 0 && callbackTicks <= DateTime.MaxValue.Ticks ? (DateTime.UtcNow.Ticks - callbackTicks) / (double)TimeSpan.TicksPerSecond : double.NaN;
                                string eventTimeText = eventTicks > 0 && eventTicks <= DateTime.MaxValue.Ticks ? new DateTime(eventTicks).ToString("HH:mm:ss.fff") : "?";
                                string ageText = double.IsNaN(ageS) ? "?" : ageS < 120 ? ageS.ToString("F1") + " s" : (ageS / 60).ToString("F0") + " min";
                                last = " last event: " + (side == 2 ? "trade" : side == 0 ? "bid" : "ask") + "/" + (op == 0 ? "add" : op == 1 ? "update" : "remove") + "@" + pos +
                                       " " + price.ToString("F2") + "x" + volume + " event-time " + eventTimeText +
                                       " published " + ageText + " ago.";
                                if (!double.IsNaN(ageS) && delta == 0 && ageS > 60)
                                    last += " The writer is alive but has published nothing for " + ageText + " — market closed (CME: Fri 16:00 CT to Sun 17:00 CT), feed disconnected, or Playback paused?";
                            }
                        }
                        string rate = " write-seq " + seq0 + " -> " + seq1 + " (+" + delta + " events in 2 s, ~" + (delta / 2.0).ToString("F0") + "/s)." + last;
                        if (delta > 0 && !layoutOk)
                        {
                            resultText = "Stream check: " + mapName + " is updating but its ring LAYOUT is wrong — the engine will refuse it." + layout + rate;
                            resultColor = Color.Firebrick;
                        }
                        else if (delta > 0)
                        {
                            resultText = "Stream check: " + mapName + " EXISTS and is UPDATING — the MMF event stream is live." + layout + rate;
                            resultColor = Color.DarkGreen;
                        }
                        else
                        {
                            resultText = "Stream check: " + mapName + " exists but published NO events in 2 s — bridge loaded but idle (market closed/quiet, data feed stopped, or bridge stalled)." + layout + rate;
                            resultColor = Color.DarkOrange;
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    resultText = "Stream check: " + mapName + " NOT FOUND — the NT8 MMF bridge for this instrument is not running.";
                    resultColor = Color.Firebrick;
                }
                catch (Exception ex)
                {
                    resultText = "Stream check failed: " + ex.Message;
                    resultColor = Color.Firebrick;
                }
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate
                        {
                            SetStatus(resultText, resultColor);
                            Append(resultText);
                            checkStream.Enabled = true;
                            streamCheckRunning = false;
                        });
                }
                catch { streamCheckRunning = false; }
            });
            probe.IsBackground = true;
            probe.Start();
        }

        private void ConfigureHubWatchdog()
        {
            // v64m: single UI-paint tick. Everything that used to repaint per event
            // now marks itself dirty and lands here.
            uiPaintTimer.Interval = LogFlushIntervalMs;
            uiPaintTimer.Tick += delegate
            {
                try
                {
                    FlushPendingLogs();
                    if (tradePathDirty)   { tradePathDirty = false;   RefreshTradePathNow(); }
                    if (latencyGateDirty) { latencyGateDirty = false; UpdateLatencyGateLabelNow(); }
                    FlushGateConfigIfDirty();   // v64y: debounced re-push of ops 25/26
                    SyncHubAiPanel();
                }
                catch { }
            };
            uiPaintTimer.Start();

            hubWatchdog.Interval = 2000;
            hubWatchdog.Tick += delegate
            {
                // Strategy-activity indicator (fill ring only moves when
                // LnnOrderExecStrategy writes into it).
                if (tradeCmdAccessor == null)
                {
                    strategyAliveLabel.Text = "NT8 strategy activity: hub not connected.";
                    strategyAliveLabel.ForeColor = Color.DimGray;
                }
                else if (lastStrategyActivityUtc == DateTime.MinValue)
                {
                    strategyAliveLabel.Text = "NT8 strategy activity: none observed yet — verify LnnOrderExecStrategy is running on an NT8 chart.";
                    strategyAliveLabel.ForeColor = tradeArmed ? Color.DarkOrange : Color.DimGray;
                }
                else if (stratHeartbeatUtc != DateTime.MinValue)
                {
                    double hb = (DateTime.UtcNow - stratHeartbeatUtc).TotalSeconds;
                    strategyAliveLabel.Text = hb < 4
                        ? "NT8 strategy: ALIVE (heartbeat " + hb.ToString("0.0", CultureInfo.InvariantCulture) + " s ago, ring seq " + lastFillRingSequenceSeen.ToString(CultureInfo.InvariantCulture) + ")."
                        : "NT8 strategy: heartbeat STOPPED " + Age(stratHeartbeatUtc) + " — the strategy was disabled, NT8 lost the connection, or the chart was closed.";
                    strategyAliveLabel.ForeColor = hb < 4 ? Color.DarkGreen : Color.Firebrick;
                }
                else
                {
                    double age = (DateTime.UtcNow - lastStrategyActivityUtc).TotalSeconds;
                    strategyAliveLabel.Text = "NT8 strategy activity: last fill/order event " +
                        (age < 60 ? age.ToString("0", CultureInfo.InvariantCulture) + "s ago" :
                         (age / 60.0).ToString("0.0", CultureInfo.InvariantCulture) + " min ago") +
                        " (ring seq " + lastFillRingSequenceSeen.ToString(CultureInfo.InvariantCulture) + "; no v64k heartbeat — strategy build older than the dashboard?).";
                    strategyAliveLabel.ForeColor = age < 300 ? Color.DarkGreen : Color.DarkOrange;
                }
                RefreshTradePath();

                UpdateLatencyGateLabel();

                // Auto-reconnect: if the AI Trader is armed but the command
                // accessor vanished (e.g. a failed write disposed it), retry.
                if (tradeArmed && tradeCmdAccessor == null)
                {
                    Append("Trade hub watchdog: connection lost while armed — attempting reconnect.");
                    ConnectTradeHub();
                    if (tradeCmdAccessor == null) AutoDisarm("Trade hub reconnect failed.");
                }
            };
            hubWatchdog.Start();
        }

        private void WriteSessionSummary()
        {
            double sessionPnl = paperNetTicksAtArmSet ? latestPaperNetTicks - paperNetTicksAtArm : latestPaperNetTicks;
            string summary = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "  symbol=" + ResolveTradeSymbol() +
                "  ai-entries=" + aiEntriesThisSession.ToString(CultureInfo.InvariantCulture) +
                "  paper-trades-total=" + latestPaperTradeCount.ToString(CultureInfo.InvariantCulture) +
                "  session-net-ticks=" + sessionPnl.ToString("0.##", CultureInfo.InvariantCulture) +
                "  paper-net-ticks-total=" + latestPaperNetTicks.ToString("0.##", CultureInfo.InvariantCulture);
            Append("SESSION SUMMARY: " + summary);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SessionSummaryPath));
                File.AppendAllText(SessionSummaryPath, summary + Environment.NewLine);
            }
            catch { }
        }

        // ----- Named settings profiles -----

        private void RefreshProfileList()
        {
            try
            {
                profileSelector.Items.Clear();
                if (Directory.Exists(ProfilesFolder))
                    foreach (string f in Directory.GetFiles(ProfilesFolder, "*.txt"))
                        profileSelector.Items.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
        }

        private bool suppressProfileLoad;

        private void ProfileSelected(object sender, EventArgs e)
        {
            if (suppressProfileLoad || profileSelector.SelectedIndex < 0) return;
            string path = Path.Combine(ProfilesFolder, (string)profileSelector.SelectedItem + ".txt");
            try
            {
                if (!File.Exists(path)) return;
                Dictionary<string, TextBox> inputs = EditableNumberInputs();
                foreach (string line in File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('\t');
                    if (separator <= 0) continue;
                    TextBox input;
                    if (!inputs.TryGetValue(line.Substring(0, separator), out input)) continue;
                    input.Text = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(separator + 1)));
                }
                Append("Loaded settings profile '" + (string)profileSelector.SelectedItem + "'.");
            }
            catch (Exception ex) { Append("Profile load failed: " + ex.Message); }
        }

        private void SaveProfileClick(object sender, EventArgs e)
        {
            string name = PromptForText("Save settings profile", "Profile name:",
                profileSelector.SelectedItem as string ?? ResolveTradeSymbol().ToLowerInvariant());
            if (string.IsNullOrEmpty(name)) return;
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            try
            {
                Directory.CreateDirectory(ProfilesFolder);
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, TextBox> entry in EditableNumberInputs())
                    lines.Add(entry.Key + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Value.Text ?? string.Empty)));
                File.WriteAllLines(Path.Combine(ProfilesFolder, name + ".txt"), lines.ToArray());
                suppressProfileLoad = true;
                RefreshProfileList();
                profileSelector.SelectedItem = name;
                suppressProfileLoad = false;
                Append("Saved settings profile '" + name + "'.");
            }
            catch (Exception ex) { Append("Profile save failed: " + ex.Message); }
        }

        private string PromptForText(string title, string caption, string initial)
        {
            using (Form prompt = new Form())
            {
                prompt.Text = title;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.ClientSize = new Size(340, 110);
                prompt.StartPosition = FormStartPosition.CenterParent;
                prompt.MinimizeBox = prompt.MaximizeBox = false;
                Label lbl = new Label { Text = caption, AutoSize = true, Location = new Point(12, 12) };
                TextBox box = new TextBox { Location = new Point(12, 36), Width = 316, Text = initial ?? string.Empty };
                Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(172, 70) };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(253, 70) };
                prompt.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                prompt.AcceptButton = ok;
                prompt.CancelButton = cancel;
                return prompt.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        // ----- Window / instrument / engine state -----

        private void LoadUiState()
        {
            try
            {
                if (!File.Exists(UiStatePath)) return;
                foreach (string line in File.ReadAllLines(UiStatePath))
                {
                    int sep = line.IndexOf('\t');
                    if (sep <= 0) continue;
                    string key = line.Substring(0, sep), val = line.Substring(sep + 1);
                    if (key == "instrument_index")
                    {
                        int idx;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) && idx >= 0 && idx < instrument.Items.Count)
                            instrument.SelectedIndex = idx;
                    }
                    else if (key == "engine_item")
                    {
                        for (int i = 0; i < engine.Items.Count; i++)
                            if (string.Equals(engine.Items[i].ToString(), val, StringComparison.Ordinal)) { engine.SelectedIndex = i; break; }
                    }
                    else if (key == "sound_alerts")
                        soundAlerts.Checked = val == "1";
                    else if (key == "pin_engine_core0")
                        pinEngineCore0.Checked = val == "1";
                    else if (key == "hog_evidence_to_engine")
                        hogEvidenceToEngine.Checked = val == "1";
                    else if (key == "hog_fusion_mode")
                    { int i; if (int.TryParse(val, out i) && i >= 0 && i < hogFusionModeSelector.Items.Count) hogFusionModeSelector.SelectedIndex = i; }
                    else if (key == "hog_agree_gate")
                        hogAgreeGate.Checked = val == "1";
                    else if (key == "mmf_entry_gates")
                        mmfGates.Checked = val == "1";
                    else if (key == "gate_microprice")
                        micropriceEntryGate.Checked = val == "1";
                    else if (key == "gate_on_session") gateOnSession.Checked = val == "1";
                    else if (key == "gate_on_max_trades") gateOnMaxTrades.Checked = val == "1";
                    else if (key == "gate_on_trade_gap") gateOnTradeGap.Checked = val == "1";
                    else if (key == "gate_on_cooldown") gateOnCooldown.Checked = val == "1";
                    else if (key == "gate_on_flips") gateOnFlips.Checked = val == "1";
                    else if (key == "gate_on_ramp") gateOnRamp.Checked = val == "1";
                    else if (key == "gate_on_dead_regime") gateOnDeadRegime.Checked = val == "1";
                    else if (key == "gate_on_margin") gateOnMargin.Checked = val == "1";
                    else if (key == "gate_on_direction") gateOnDirection.Checked = val == "1";
                    else if (key == "gate_on_stale_signal") gateOnStaleSignal.Checked = val == "1";
                    else if (key == "gate_on_spread") gateOnSpread.Checked = val == "1";
                    else if (key == "gate_on_imbalance") gateOnImbalance.Checked = val == "1";
                    else if (key == "gate_on_flicker") gateOnFlicker.Checked = val == "1";
                    else if (key == "gate_on_aggression") gateOnAggression.Checked = val == "1";
                    else if (key == "gate_on_book_size") gateOnBookSize.Checked = val == "1";
                    else if (key == "gate_on_feed_stale") gateOnFeedStale.Checked = val == "1";
                    else if (key == "gate_on_give_back") gateOnGiveBack.Checked = val == "1";
                    else if (key == "gate_max_spread_ticks")   maxSpreadTicksInput.Text = val;
                    else if (key == "gate_min_imbalance")      minImbalanceInput.Text = val;
                    else if (key == "gate_max_flicker")        maxFlickerEntryInput.Text = val;
                    else if (key == "gate_max_aggression")     maxAggrIntoInput.Text = val;
                    else if (key == "gate_min_book_size")      minBookSizeInput.Text = val;
                    else if (key == "gate_stale_signal_ms")    staleSignalMsInput.Text = val;
                    else if (key == "gate_feed_stale_ms")      feedStaleMsInput.Text = val;
                    else if (key == "gate_give_back_percent")  giveBackPercentInput.Text = val;
                    else if (key == "exec_policy")
                    { int i; if (int.TryParse(val, out i) && i >= 0 && i < exPolicySelector.Items.Count) exPolicySelector.SelectedIndex = i; }
                    else if (key == "exit_mode")
                    { int i; if (int.TryParse(val, out i) && i >= 0 && i < exitModeSelector.Items.Count) exitModeSelector.SelectedIndex = i; }
                    else if (key == "flip_on_opposite")
                        flipOnOpposite.Checked = val == "1";
                    else if (key == "paper_mirror")
                        paperMirror.Checked = val == "1";
                    else if (key == "signal_mode")
                    {
                        int mode;
                        if (int.TryParse(val, out mode) && mode >= 0 && mode < signalModeSelector.Items.Count)
                            signalModeSelector.SelectedIndex = mode;
                    }
                    else if (key == "window_bounds")
                    {
                        string[] parts = val.Split(new char[] { ',' });
                        int x, y, w, h;
                        if (parts.Length == 4 &&
                            int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) &&
                            int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h) &&
                            w >= 400 && h >= 300)
                        {
                            Rectangle candidate = new Rectangle(x, y, w, h);
                            foreach (Screen s in Screen.AllScreens)
                                if (s.WorkingArea.IntersectsWith(candidate))
                                {
                                    StartPosition = FormStartPosition.Manual;
                                    Bounds = candidate;
                                    break;
                                }
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveUiState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UiStatePath));
                Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllLines(UiStatePath, new string[]
                {
                    "instrument_index\t" + instrument.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    "engine_item\t" + (engine.SelectedItem != null ? engine.SelectedItem.ToString() : string.Empty),
                    "sound_alerts\t" + (soundAlerts.Checked ? "1" : "0"),
                    "pin_engine_core0\t" + (pinEngineCore0.Checked ? "1" : "0"),
                    "hog_evidence_to_engine\t" + (hogEvidenceToEngine.Checked ? "1" : "0"),
                    "hog_fusion_mode\t" + hogFusionModeSelector.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    "hog_agree_gate\t" + (hogAgreeGate.Checked ? "1" : "0"),
                    "mmf_entry_gates\t" + (mmfGates.Checked ? "1" : "0"),
                    "gate_microprice\t" + (micropriceEntryGate.Checked ? "1" : "0"),
                    "gate_on_session\t" + (gateOnSession.Checked ? "1" : "0"),
                    "gate_on_max_trades\t" + (gateOnMaxTrades.Checked ? "1" : "0"),
                    "gate_on_trade_gap\t" + (gateOnTradeGap.Checked ? "1" : "0"),
                    "gate_on_cooldown\t" + (gateOnCooldown.Checked ? "1" : "0"),
                    "gate_on_flips\t" + (gateOnFlips.Checked ? "1" : "0"),
                    "gate_on_ramp\t" + (gateOnRamp.Checked ? "1" : "0"),
                    "gate_on_dead_regime\t" + (gateOnDeadRegime.Checked ? "1" : "0"),
                    "gate_on_margin\t" + (gateOnMargin.Checked ? "1" : "0"),
                    "gate_on_direction\t" + (gateOnDirection.Checked ? "1" : "0"),
                    "gate_on_stale_signal\t" + (gateOnStaleSignal.Checked ? "1" : "0"),
                    "gate_on_spread\t" + (gateOnSpread.Checked ? "1" : "0"),
                    "gate_on_imbalance\t" + (gateOnImbalance.Checked ? "1" : "0"),
                    "gate_on_flicker\t" + (gateOnFlicker.Checked ? "1" : "0"),
                    "gate_on_aggression\t" + (gateOnAggression.Checked ? "1" : "0"),
                    "gate_on_book_size\t" + (gateOnBookSize.Checked ? "1" : "0"),
                    "gate_on_feed_stale\t" + (gateOnFeedStale.Checked ? "1" : "0"),
                    "gate_on_give_back\t" + (gateOnGiveBack.Checked ? "1" : "0"),
                    "gate_max_spread_ticks\t" + maxSpreadTicksInput.Text.Trim(),
                    "gate_min_imbalance\t" + minImbalanceInput.Text.Trim(),
                    "gate_max_flicker\t" + maxFlickerEntryInput.Text.Trim(),
                    "gate_max_aggression\t" + maxAggrIntoInput.Text.Trim(),
                    "gate_min_book_size\t" + minBookSizeInput.Text.Trim(),
                    "gate_stale_signal_ms\t" + staleSignalMsInput.Text.Trim(),
                    "gate_feed_stale_ms\t" + feedStaleMsInput.Text.Trim(),
                    "gate_give_back_percent\t" + giveBackPercentInput.Text.Trim(),
                    "exec_policy\t" + exPolicySelector.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    "signal_mode\t" + signalModeSelector.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    "exit_mode\t" + exitModeSelector.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    "flip_on_opposite\t" + (flipOnOpposite.Checked ? "1" : "0"),
                    "paper_mirror\t" + (paperMirror.Checked ? "1" : "0"),
                    "window_bounds\t" + b.X.ToString(CultureInfo.InvariantCulture) + "," + b.Y.ToString(CultureInfo.InvariantCulture) + "," +
                        b.Width.ToString(CultureInfo.InvariantCulture) + "," + b.Height.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }

        private void ConfigureInputPersistence()
        {
            inputPersistenceTimer.Interval = 750;
            inputPersistenceTimer.Tick += delegate
            {
                inputPersistenceTimer.Stop();
                SavePersistedNumberInputs();
            };

            foreach (TextBox input in EditableNumberInputs().Values)
            {
                input.TextChanged += delegate
                {
                    inputSettingsDirty = true;
                    inputPersistenceTimer.Stop();
                    inputPersistenceTimer.Start();
                };
            }
        }

        private void LoadPersistedNumberInputs()
        {
            try
            {
                if (!File.Exists(InputSettingsPath))
                    return;

                Dictionary<string, TextBox> inputs = EditableNumberInputs();
                foreach (string line in File.ReadAllLines(InputSettingsPath))
                {
                    int separator = line.IndexOf('\t');
                    if (separator <= 0)
                        continue;
                    TextBox input;
                    if (!inputs.TryGetValue(line.Substring(0, separator), out input))
                        continue;
                    input.Text = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(separator + 1)));
                }
            }
            catch
            {
                // A malformed local preferences file should never prevent the
                // dashboard or read-only market-data monitor from starting.
            }
        }

        private void SavePersistedNumberInputs()
        {
            if (!inputSettingsDirty && File.Exists(InputSettingsPath))
                return;
            try
            {
                string folder = Path.GetDirectoryName(InputSettingsPath);
                if (!String.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, TextBox> entry in EditableNumberInputs())
                    lines.Add(entry.Key + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Value.Text ?? String.Empty)));
                File.WriteAllLines(InputSettingsPath, lines.ToArray());
                inputSettingsDirty = false;
            }
            catch
            {
                // Preferences are optional. Ignore storage failures rather
                // than interrupting the live read-only monitor.
            }
        }

        private void ResetMetrics()
        {
            latestMetricsLine = null;
            latestPaperMetricsLine = null;
            latestOutcomeTimeFramesLine = null;
            SetMetric(frames, "Frames", "0");
            SetMetric(valid, "Valid books", "0");
            SetMetric(trained, "Medium 3-outcome updates", "0");
            SetMetric(noTrade, "Medium no-move labels", "0");
            SetMetric(upPredictionCount, "Up predictions", "0");
            SetMetric(upPredictionRate, "Up prediction rate", "Waiting");
            SetMetric(downPredictionCount, "Down predictions", "0");
            SetMetric(downPredictionRate, "Down prediction rate", "Waiting");
            SetMetric(paperSignal, "Current paper signal", "WARMUP");
            SetMetric(paperSignalCounts, "Paper signal counts", "L 0 / S 0 / N 0");
            SetMetric(paperTradeSummary, "Paper trades", "0 completed");
            SetMetric(paperTradePnl, "Paper net P/L", "0.00 ticks");
            SetMetric(accuracy, "Medium 3-outcome accuracy", "Waiting");
            SetMetric(majorityBaseline, "Medium 3-outcome baseline", "Waiting");
            SetMetric(probability, "Current up probability", "Waiting");
            SetMetric(weightNorm, "Weight L2 norm", "0");
            SetMetric(bestValidation, "Best medium unseen weights", "Waiting");
            SetMetric(liveValidation, "Medium unseen-label check", "Waiting");
            outcomePanel.Text = "Waiting for the three-horizon learner.\r\nFAST / MEDIUM / SLOW will each show independent UP, DOWN and NO-MOVE labels.\r\nOnly MEDIUM drives the research paper signal and unseen-label checkpoint check.";
            latencyPanel.Text = "Waiting for the instrumented version-2 NinjaTrader bridge.\r\n" +
                "The panel will show rolling p50 / p95 / p99 once valid depth frames arrive.";
        }

        private void AskAnalyst()
        {
            string question = analystQuestion.Text.Trim();
            if (question.Length == 0)
            {
                analystQuestion.Focus();
                return;
            }
            analystQuestion.Clear();
            string answer = BuildAnalystAnswer(question);
            analystHistory.AppendText(Environment.NewLine + Environment.NewLine + "You: " + question + Environment.NewLine + "Analyst: " + answer);
            analystHistory.SelectionStart = analystHistory.TextLength;
            analystHistory.ScrollToCaret();
        }

        private string BuildAnalystAnswer(string question)
        {
            string lower = question.ToLowerInvariant();
            if (lower.Contains("performance") || lower.Contains("lag") || lower.Contains("process") || lower.Contains("speed"))
            {
                Match performance = string.IsNullOrEmpty(latestMetricsLine) ? Match.Empty : PerformanceMetrics.Match(latestMetricsLine);
                if (!performance.Success)
                    return "No performance summary has arrived yet. With Process ms set to 5, the receiver drains all frames but runs heavy model work at most once each five milliseconds.";
                return "Performance mode: process interval " + performance.Groups["interval"].Value + " ms; processed " +
                    performance.Groups["processed"].Value + " snapshots and coalesced " + performance.Groups["skipped"].Value +
                    " incoming frames. The bridge feed is still drained; only expensive model work is rate-limited.";
            }
            if (lower.Contains("paper") || lower.Contains("p/l") || lower.Contains("pnl") || lower.Contains("profit") || lower.Contains("loss") || lower.Contains("drawdown"))
            {
                Match paper = string.IsNullOrEmpty(latestPaperMetricsLine) ? Match.Empty : PaperMetrics.Match(latestPaperMetricsLine);
                if (!paper.Success)
                    return "No completed internal paper trades are available yet. The simulator waits for 100 labeled updates, then opens only on LONG or SHORT signals and closes at its target, stop, or time limit.";
                return "Internal paper simulation: " + paper.Groups["trades"].Value + " completed trades; wins " +
                    paper.Groups["wins"].Value + ", losses " + paper.Groups["losses"].Value + ", win rate " +
                    paper.Groups["win_rate"].Value + "%, net " + paper.Groups["net_ticks"].Value +
                    " ticks after the configured round-trip fee, maximum drawdown " + paper.Groups["drawdown"].Value +
                    " ticks, open position " + paper.Groups["open"].Value + ". This is a quote-based research simulation, not a broker fill.";
            }
            bool asksForUpdate = lower.Contains("why") || lower.Contains("update") || lower.Contains("learn") ||
                lower.Contains("weight") || lower.Contains("feature") || lower.Contains("think");
            if (asksForUpdate)
            {
                string auditSummary;
                if (TryGetLatestAuditSummary(out auditSummary)) return auditSummary;
                return "No completed feature/weight update is available yet. The audit is written only after a future LONG or SHORT label reaches its profit target. Ensure Save log is enabled, then wait for at least one Online update.";
            }

            Match match = string.IsNullOrEmpty(latestMetricsLine) ? Match.Empty : Metrics.Match(latestMetricsLine);
            if (!match.Success)
                return "The receiver has not produced a full live summary yet. Start the receiver and bridge, then ask again after valid DOM frames arrive.";

            if (lower.Contains("accuracy") || lower.Contains("baseline"))
                return "Online label accuracy is " + match.Groups["accuracy"].Value + "% across " + match.Groups["trained"].Value +
                    " completed updates. The majority baseline is " + match.Groups["baseline"].Value +
                    "%: an always-long or always-short rule using the common label would score that value. The model must beat it on later unseen data to show useful predictive structure.";

            if (lower.Contains("predict") || lower.Contains("signal") || lower.Contains("long") || lower.Contains("short"))
                return "Current measured state: probability up=" + match.Groups["probability"].Value + ", paper signal=" +
                    match.Groups["signal"].Value + ", trained updates=" + match.Groups["trained"].Value +
                    ". This is a read-only research signal, not an instruction to trade.";

            return "Current measured state: " + match.Groups["frames"].Value + " frames, " + match.Groups["valid"].Value +
                " valid books, " + match.Groups["trained"].Value + " learning updates, up probability " + match.Groups["probability"].Value +
                ", paper signal " + match.Groups["signal"].Value + ", accuracy " + match.Groups["accuracy"].Value +
                "% versus majority baseline " + match.Groups["baseline"].Value + "%. Ask 'why did it update?' to inspect the latest feature-weight audit.";
        }

        private bool TryGetLatestAuditSummary(out string summary)
        {
            summary = null;
            if (string.IsNullOrWhiteSpace(activeWeightUpdateLogPath) || !File.Exists(activeWeightUpdateLogPath)) return false;
            try
            {
                List<string> rows = new List<string>();
                using (FileStream stream = new FileStream(activeWeightUpdateLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - 262144);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        if (start > 0) reader.ReadLine();
                        string row;
                        while ((row = reader.ReadLine()) != null) rows.Add(row);
                    }
                }
                if (rows.Count == 0) return false;
                string[] lastColumns = rows[rows.Count - 1].Split(new char[] { ',' });
                if (lastColumns.Length < 18) return false;
                string update = lastColumns[1];
                List<FeatureAuditItem> largest = new List<FeatureAuditItem>();
                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    string[] columns = rows[rowIndex].Split(new char[] { ',' });
                    if (columns.Length < 18 || columns[1] != update || columns[7] != "weight") continue;
                    double delta;
                    if (!double.TryParse(columns[16], NumberStyles.Float, CultureInfo.InvariantCulture, out delta)) continue;
                    largest.Add(new FeatureAuditItem { TemporalState = columns[9], FeatureName = columns[11], WeightDelta = delta });
                }
                largest.Sort(delegate(FeatureAuditItem left, FeatureAuditItem right)
                {
                    return Math.Abs(right.WeightDelta).CompareTo(Math.Abs(left.WeightDelta));
                });
                string top = "";
                int count = Math.Min(5, largest.Count);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) top += "; ";
                    top += largest[i].TemporalState + "/" + largest[i].FeatureName + " " +
                        (largest[i].WeightDelta >= 0.0 ? "+" : "") + largest[i].WeightDelta.ToString("G4", CultureInfo.InvariantCulture);
                }
                summary = "Latest update #" + update + " labeled " + lastColumns[5] + " because " + lastColumns[6] +
                    ". It started from probability " + lastColumns[4] + ". Largest absolute weight changes: " +
                    (top.Length == 0 ? "none parsed" : top) + ". These are gradient adjustments from one labeled event, not proof that one feature caused the market move.";
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void LoadHorizonStudyClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Horizon study JSON|*.json|All files|*.*";
                dialog.Title = "Load a read-only historical horizon study";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string json = File.ReadAllText(dialog.FileName);
                    string[] targets = { "1", "2", "4", "8", "12" };
                    List<string> summary = new List<string>();
                    foreach (string target in targets)
                    {
                        Match row = Regex.Match(json, "\\\"target_ticks\\\"\\s*:\\s*" + target + "(?<body>[^}]*)", RegexOptions.Singleline);
                        if (!row.Success) continue;
                        Match median = Regex.Match(row.Groups["body"].Value, "\\\"median_seconds\\\"\\s*:\\s*(?<v>[0-9.]+)");
                        Match mean = Regex.Match(row.Groups["body"].Value, "\\\"mean_seconds\\\"\\s*:\\s*(?<v>[0-9.]+)");
                        Match rate = Regex.Match(row.Groups["body"].Value, "\\\"hit_rate_within_5m_pct\\\"\\s*:\\s*(?<v>[0-9.]+)");
                        summary.Add(target + "t: median " + (median.Success ? median.Groups["v"].Value : "n/a") + "s, average " + (mean.Success ? mean.Groups["v"].Value : "n/a") + "s, 5m hit " + (rate.Success ? rate.Groups["v"].Value : "n/a") + "%");
                    }
                    horizonStudyStatus.Text = "Historical horizon study: " + string.Join(" | ", summary.ToArray());
                    horizonStudyStatus.ForeColor = Color.DarkGreen;
                    Append("Loaded historical horizon study: " + dialog.FileName);
                }
                catch (Exception error)
                {
                    horizonStudyStatus.Text = "Historical study could not be loaded.";
                    horizonStudyStatus.ForeColor = Color.Firebrick;
                    Append("Historical study load failed: " + error.Message);
                }
            }
        }

        private void RunHistoricalStudyClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Market data|*.dbn;*.zst;*.csv;*.json;*.jsonl;*.ndjson;*.npz;*.bin|All files|*.*";
                dialog.Multiselect = true;
                dialog.Title = "Choose local historical market-data files (read-only)";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string script = Path.Combine(baseDir, "mes_time_to_move_study.py");
                string sourceScript = Path.GetFullPath(Path.Combine(baseDir, "..", "mes_time_to_move_study.py"));
                if (File.Exists(sourceScript)) script = sourceScript;
                string packageDir = Path.Combine(baseDir, "python_packages");
                if (!File.Exists(script) || !Directory.Exists(packageDir))
                {
                    MessageBox.Show(this, "Historical runner files are missing beside the dashboard: mes_time_to_move_study.py and python_packages.", "Historical study", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                double configuredTickSize;
                if (!double.TryParse(tickSizeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out configuredTickSize) || configuredTickSize <= 0.0)
                {
                    MessageBox.Show(this, "Tick size must be a positive number.", "Historical study", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                int dbnInstrumentId;
                bool hasDbnInstrumentId = int.TryParse(trainingInstrumentId.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dbnInstrumentId);
                string result = Path.Combine(baseDir, "historical_move_study_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                string files = string.Join(" ", Array.ConvertAll(dialog.FileNames, QuoteArgument));
                string command = QuoteArgument(script) + " --files " + files +
                    (hasDbnInstrumentId ? " --instrument-id " + dbnInstrumentId.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                    " --symbol " + QuoteArgument(trainingSymbol.Text.Trim()) + " --tick-size " + configuredTickSize.ToString(CultureInfo.InvariantCulture) +
                    " --package-dir " + QuoteArgument(packageDir) + " --output " + QuoteArgument(result);
                ProcessStartInfo info = new ProcessStartInfo(pythonExecutable.Text.Trim(), command);
                info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                try
                {
                    runHistoricalStudy.Enabled = false;
                    horizonStudyStatus.Text = "Historical study running locally (read-only)...";
                    Process job = new Process(); job.StartInfo = info; job.EnableRaisingEvents = true;
                    job.Exited += delegate { BeginInvoke((MethodInvoker)delegate { runHistoricalStudy.Enabled = true; if (job.ExitCode == 0 && File.Exists(result)) { horizonStudyStatus.Text = "Historical study complete: " + result; horizonStudyStatus.ForeColor = Color.DarkGreen; } else { horizonStudyStatus.Text = "Historical study failed; see dashboard log."; horizonStudyStatus.ForeColor = Color.Firebrick; } }); };
                    job.Start(); job.BeginOutputReadLine(); job.BeginErrorReadLine();
                }
                catch (Exception error) { runHistoricalStudy.Enabled = true; horizonStudyStatus.Text = "Historical study could not start."; horizonStudyStatus.ForeColor = Color.Firebrick; Append(error.Message); }
            }
        }

        private void RunMesAdapterClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Databento depth files|*.dbn;*.zst|All files|*.*";
                dialog.Multiselect = true;
                dialog.Title = "Choose chronological MES MBP-10 files (last file is frozen holdout)";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (dialog.FileNames.Length < 2)
                {
                    MessageBox.Show(this, "Choose at least two files: earlier files train the model and the latest file is a frozen holdout.",
                        "MES LNN test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string adapter = Path.Combine(baseDir, "mes_lnn_adapter", "mes_lnn_adapter_train.py");
                // The packaged build keeps the adapter beside the EXE.  During
                // development it lives one level up, alongside the dashboard
                // source, so both layouts use the same button safely.
                if (!File.Exists(adapter))
                    adapter = Path.GetFullPath(Path.Combine(baseDir, "..", "mes_lnn_adapter", "mes_lnn_adapter_train.py"));
                string packageDir = Path.Combine(baseDir, "python_packages");
                if (!File.Exists(adapter) || !Directory.Exists(packageDir))
                {
                    MessageBox.Show(this, "MES adapter files are missing beside the dashboard. Expected mes_lnn_adapter\\mes_lnn_adapter_train.py and python_packages.",
                        "MES LNN test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string[] selected = (string[])dialog.FileNames.Clone();
                Array.Sort(selected, StringComparer.OrdinalIgnoreCase);
                int targetTicks = 4;
                double configuredTarget;
                if (double.TryParse(minimumProfitTargetTicks.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out configuredTarget) && configuredTarget >= 1.0)
                    targetTicks = Math.Max(1, (int)Math.Round(configuredTarget));
                string[] trainFiles = new string[selected.Length - 1];
                Array.Copy(selected, trainFiles, trainFiles.Length);
                string outputDir = Path.Combine(baseDir, "mes_lnn_adapter", "runs");
                Directory.CreateDirectory(outputDir);
                string arguments = QuoteArgument(adapter) + " --train-files " +
                    string.Join(" ", Array.ConvertAll(trainFiles, QuoteArgument)) +
                    " --holdout-file " + QuoteArgument(selected[selected.Length - 1]) +
                    " --instrument-id 42003239 --package-dir " + QuoteArgument(packageDir) +
                    " --target-ticks " + targetTicks.ToString(CultureInfo.InvariantCulture) +
                    " --horizon-seconds 120 --max-frames-per-file 30000 --epochs 1 --output-dir " + QuoteArgument(outputDir);
                ProcessStartInfo info = new ProcessStartInfo(pythonExecutable.Text.Trim(), arguments);
                info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                // The development bundle contains a CPU Torch package under
                // outputs\\torch_runtime.  A normal installed Python/Torch
                // runtime needs no override; this fallback makes the shipped
                // local research build usable without changing the model code.
                string bundledTorch = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "torch_runtime"));
                if (Directory.Exists(bundledTorch))
                    info.EnvironmentVariables["PYTHONPATH"] = bundledTorch;
                try
                {
                    runMesAdapter.Enabled = false;
                    horizonStudyStatus.Text = "MES LNN test running locally: chronological train, frozen latest-file holdout...";
                    horizonStudyStatus.ForeColor = Color.DarkGoldenrod;
                    Process job = new Process(); job.StartInfo = info; job.EnableRaisingEvents = true;
                    job.OutputDataReceived += delegate(object ignored, DataReceivedEventArgs line)
                    {
                        if (!string.IsNullOrWhiteSpace(line.Data)) BeginInvoke((MethodInvoker)delegate { Append("MES adapter: " + line.Data); });
                    };
                    job.ErrorDataReceived += delegate(object ignored, DataReceivedEventArgs line)
                    {
                        if (!string.IsNullOrWhiteSpace(line.Data)) BeginInvoke((MethodInvoker)delegate { Append("MES adapter error: " + line.Data); });
                    };
                    job.Exited += delegate
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            runMesAdapter.Enabled = true;
                            if (job.ExitCode == 0)
                            {
                                horizonStudyStatus.Text = "MES LNN test complete. Report and research checkpoint saved in mes_lnn_adapter\\runs.";
                                horizonStudyStatus.ForeColor = Color.DarkGreen;
                            }
                            else
                            {
                                horizonStudyStatus.Text = "MES LNN test failed; see dashboard log. Ensure the selected Python runtime has torch.";
                                horizonStudyStatus.ForeColor = Color.Firebrick;
                            }
                        });
                    };
                    job.Start(); job.BeginOutputReadLine(); job.BeginErrorReadLine();
                }
                catch (Exception error)
                {
                    runMesAdapter.Enabled = true;
                    horizonStudyStatus.Text = "MES LNN test could not start.";
                    horizonStudyStatus.ForeColor = Color.Firebrick;
                    Append(error.Message);
                }
            }
        }

        private static string SafeCategory(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "uncategorized";
            string result = string.Empty;
            foreach (char character in value.Trim())
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_') result += character;
            }
            return string.IsNullOrEmpty(result) ? "uncategorized" : result;
        }

        private static bool TryReadHoldoutEdge(string report, out double edge)
        {
            edge = 0.0;
            try
            {
                Match match = Regex.Match(File.ReadAllText(report),
                    "\\\"holdout\\\"\\s*:\\s*\\{.*?\\\"edge_over_baseline\\\"\\s*:\\s*(?<edge>-?[0-9.]+)",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant);
                return match.Success && double.TryParse(match.Groups["edge"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out edge);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void ExportOnnxPackageClick(object sender, EventArgs e)
        {
            using (OpenFileDialog checkpointDialog = new OpenFileDialog())
            {
                checkpointDialog.Title = "Choose a MultiTask LNN .pt checkpoint to export";
                checkpointDialog.Filter = "PyTorch checkpoint (*.pt)|*.pt|All files (*.*)|*.*";
                if (checkpointDialog.ShowDialog(this) != DialogResult.OK) return;
                using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
                {
                    folderDialog.Description = "Choose a parent folder for the new ONNX model package";
                    folderDialog.SelectedPath = Directory.Exists(onnxRegistryRoot.Text) ? onnxRegistryRoot.Text : activeWeightRoot.Text;
                    if (folderDialog.ShowDialog(this) != DialogResult.OK) return;
                    double tickSize;
                    if (!double.TryParse(tickSizeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out tickSize) || tickSize <= 0.0)
                    {
                        MessageBox.Show(this, "Tick size must be a positive number before export.", "ONNX export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    double horizonSeconds;
                    if (!double.TryParse(trainingHorizonSeconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out horizonSeconds) || horizonSeconds <= 0.0)
                    {
                        MessageBox.Show(this, "Horizon seconds must be positive before export.", "ONNX export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    int targetTicks = 4;
                    double targetValue;
                    if (double.TryParse(minimumProfitTargetTicks.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out targetValue) && targetValue >= 1.0)
                        targetTicks = Math.Max(1, (int)Math.Round(targetValue));
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string sourceDir = Path.GetFullPath(Path.Combine(baseDir, ".."));
                    string script = Path.Combine(sourceDir, "export_multitask_lnn_onnx.py");
                    if (!File.Exists(script)) script = Path.Combine(baseDir, "export_multitask_lnn_onnx.py");
                    string onnxPackages = Path.Combine(sourceDir, "onnx_python_packages");
                    if (!Directory.Exists(onnxPackages)) onnxPackages = Path.Combine(baseDir, "onnx_python_packages");
                    if (!File.Exists(script) || !Directory.Exists(onnxPackages))
                    {
                        MessageBox.Show(this, "The ONNX exporter or its local packages are missing beside the dashboard.",
                            "ONNX export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string packageParent = folderDialog.SelectedPath;
                    string packageName = SafeCategory(trainingSymbol.Text) + "_" +
                        SafeCategory(Path.GetFileNameWithoutExtension(checkpointDialog.FileName));
                    string outputDir = Path.Combine(packageParent, packageName);
                    string checkpointFolder = Path.GetDirectoryName(checkpointDialog.FileName);
                    string report = Path.Combine(checkpointFolder, "latest_report.json");
                    if (!File.Exists(report)) report = Path.ChangeExtension(checkpointDialog.FileName, ".json");
                    string command = QuoteArgument(script) + " --checkpoint " + QuoteArgument(checkpointDialog.FileName) +
                        " --output-dir " + QuoteArgument(outputDir) +
                        " --symbol " + QuoteArgument(trainingSymbol.Text.Trim()) +
                        " --tick-size " + tickSize.ToString("R", CultureInfo.InvariantCulture) +
                        " --target-ticks " + targetTicks.ToString(CultureInfo.InvariantCulture) +
                        " --horizon-seconds " + horizonSeconds.ToString("R", CultureInfo.InvariantCulture) +
                        (File.Exists(report) ? " --validation-report " + QuoteArgument(report) : string.Empty);
                    ProcessStartInfo info = new ProcessStartInfo(pythonExecutable.Text.Trim(), command);
                    info.UseShellExecute = false; info.CreateNoWindow = true;
                    info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                    string bundledTorch = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "torch_runtime"));
                    string pythonPath = onnxPackages;
                    if (Directory.Exists(bundledTorch)) pythonPath += Path.PathSeparator + bundledTorch;
                    info.EnvironmentVariables["PYTHONPATH"] = pythonPath;
                    try
                    {
                        exportOnnxPackage.Enabled = false;
                        horizonStudyStatus.Text = "Exporting ONNX package and checking PyTorch/ONNX output parity...";
                        horizonStudyStatus.ForeColor = Color.DarkGoldenrod;
                        Process job = new Process(); job.StartInfo = info; job.EnableRaisingEvents = true;
                        job.OutputDataReceived += delegate(object ignored, DataReceivedEventArgs line)
                        {
                            if (!string.IsNullOrWhiteSpace(line.Data)) BeginInvoke((MethodInvoker)delegate { Append("ONNX export: " + line.Data); });
                        };
                        job.ErrorDataReceived += delegate(object ignored, DataReceivedEventArgs line)
                        {
                            if (!string.IsNullOrWhiteSpace(line.Data)) BeginInvoke((MethodInvoker)delegate { Append("ONNX export error: " + line.Data); });
                        };
                        job.Exited += delegate
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                exportOnnxPackage.Enabled = true;
                                if (job.ExitCode == 0 && File.Exists(Path.Combine(outputDir, "model.onnx")) && File.Exists(Path.Combine(outputDir, "manifest.json")))
                                {
                                    onnxRegistryRoot.Text = packageParent;
                                    RefreshOnnxRegistry();
                                    horizonStudyStatus.Text = "ONNX package exported and verified: " + outputDir;
                                    horizonStudyStatus.ForeColor = Color.DarkGreen;
                                }
                                else
                                {
                                    horizonStudyStatus.Text = "ONNX export failed; see the dashboard log.";
                                    horizonStudyStatus.ForeColor = Color.Firebrick;
                                }
                            });
                        };
                        job.Start(); job.BeginOutputReadLine(); job.BeginErrorReadLine();
                    }
                    catch (Exception error)
                    {
                        exportOnnxPackage.Enabled = true;
                        horizonStudyStatus.Text = "ONNX export could not start.";
                        horizonStudyStatus.ForeColor = Color.Firebrick;
                        Append(error.Message);
                    }
                }
            }
        }

        private void TrainPythonProfileClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Market data|*.dbn;*.zst;*.csv;*.json;*.jsonl;*.ndjson;*.npz;*.bin|All files|*.*";
                dialog.Multiselect = true;
                dialog.Title = "Choose chronological market-data files (latest file is a frozen holdout)";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (dialog.FileNames.Length < 2)
                {
                    MessageBox.Show(this, "Choose two or more files. Earlier files train; the latest file is held out for validation.",
                        "Python LNN training", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                int targetTicks = 4;
                double targetValue;
                if (double.TryParse(minimumProfitTargetTicks.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out targetValue) && targetValue >= 1.0)
                    targetTicks = Math.Max(1, (int)Math.Round(targetValue));
                double horizonSeconds;
                if (!double.TryParse(trainingHorizonSeconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out horizonSeconds) || horizonSeconds <= 0.0)
                {
                    MessageBox.Show(this, "Horizon seconds must be a positive number.", "Python LNN training", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                double configuredTickSize;
                if (!double.TryParse(tickSizeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out configuredTickSize) || configuredTickSize <= 0.0)
                {
                    MessageBox.Show(this, "Tick size must be a positive number.", "Python LNN training", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                int dbnInstrumentId;
                bool hasDbnInstrumentId = int.TryParse(trainingInstrumentId.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dbnInstrumentId);
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string sourceDir = Path.GetFullPath(Path.Combine(baseDir, ".."));
                string selectedProfile = trainingProfile.SelectedItem == null ? "MultiTask LNN" : trainingProfile.SelectedItem.ToString();
                bool customProfile = selectedProfile == "Custom Python trainer";
                string script;
                if (customProfile)
                {
                    string folder = customTrainerFolder.Text.Trim();
                    string scriptName = customTrainerScript.SelectedItem as string;
                    if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(scriptName))
                    {
                        MessageBox.Show(this, "Choose a trainer folder and a Python script first.",
                            "Custom Python trainer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    script = Path.Combine(folder, scriptName);
                }
                else
                {
                    script = selectedProfile == "Hidden Tick Depth CfC-v10"
                        ? Path.Combine(sourceDir, "cfc_v10_mes_chronological_test.py")
                        : selectedProfile == "Tick-augmented MultiTask LNN"
                        ? Path.Combine(sourceDir, "mes_lnn_adapter", "mes_lnn_tick_augmented_train.py")
                        : Path.Combine(sourceDir, "mes_lnn_adapter", "mes_lnn_adapter_train.py");
                    if (!File.Exists(script))
                        script = selectedProfile == "Hidden Tick Depth CfC-v10"
                            ? Path.Combine(baseDir, "cfc_v10_mes_chronological_test.py")
                            : selectedProfile == "Tick-augmented MultiTask LNN"
                            ? Path.Combine(baseDir, "mes_lnn_adapter", "mes_lnn_tick_augmented_train.py")
                            : Path.Combine(baseDir, "mes_lnn_adapter", "mes_lnn_adapter_train.py");
                }
                string packageDir = Path.Combine(baseDir, "python_packages");
                if (!File.Exists(script) || !Directory.Exists(packageDir))
                {
                    MessageBox.Show(this, customProfile
                            ? "The selected custom trainer script is missing, or the dashboard Python package folder is unavailable."
                            : "The selected Python profile or local databento package is missing beside this dashboard.",
                        "Python LNN training", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string category = SafeCategory(weightCategory.Text);
                string profileKey = customProfile
                    ? "custom-" + SafeCategory(Path.GetFileNameWithoutExtension(script))
                    : selectedProfile == "Hidden Tick Depth CfC-v10" ? "hidden-tick-depth-cfc-v10"
                    : selectedProfile == "Tick-augmented MultiTask LNN" ? "tick-augmented-multitask-lnn" : "mes-multitask-lnn";
                string weightsRoot = activeWeightRoot.Text.Trim();
                if (weightsRoot.Length == 0)
                {
                    MessageBox.Show(this, "Choose a weight folder before training.", "Python LNN training", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string categoryDir = Path.Combine(weightsRoot, category, profileKey);
                string trainingCheckpoint = Path.Combine(categoryDir, "training.pt");
                string activeCheckpoint = Path.Combine(categoryDir, "active.pt");
                string report = Path.Combine(categoryDir, "latest_report.json");
                string activeMetadata = Path.Combine(categoryDir, "active.json");
                Directory.CreateDirectory(categoryDir);
                string policyPath = Path.Combine(weightsRoot, "live_adoption_policy.json");
                File.WriteAllText(policyPath,
                    "{\"format\":\"lnn-dashboard-live-adoption-policy/v1\",\"enabled\":" +
                    (autoAdoptActiveWeights.Checked ? "true" : "false") +
                    ",\"minimum_holdout_edge\":0.0,\"require_symbol_match\":true,\"require_tick_size_match\":true," +
                    "\"require_full_ten_level_book\":true,\"minimum_reload_seconds\":60}");
                string[] selected = (string[])dialog.FileNames.Clone();
                Array.Sort(selected, StringComparer.OrdinalIgnoreCase);
                string[] trainFiles = new string[selected.Length - 1];
                Array.Copy(selected, trainFiles, trainFiles.Length);
                string horizonArguments = selectedProfile == "Tick-augmented MultiTask LNN"
                    ? " --horizons-seconds 15 60 " + horizonSeconds.ToString("R", CultureInfo.InvariantCulture)
                    : " --horizon-seconds " + horizonSeconds.ToString(CultureInfo.InvariantCulture);
                string command = QuoteArgument(script) + " --train-files " + string.Join(" ", Array.ConvertAll(trainFiles, QuoteArgument)) +
                    " --holdout-file " + QuoteArgument(selected[selected.Length - 1]) +
                    (hasDbnInstrumentId ? " --instrument-id " + dbnInstrumentId.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                    " --symbol " + QuoteArgument(trainingSymbol.Text.Trim()) + " --tick-size " + configuredTickSize.ToString(CultureInfo.InvariantCulture) +
                    " --package-dir " + QuoteArgument(packageDir) +
                    " --target-ticks " + targetTicks.ToString(CultureInfo.InvariantCulture) +
                    horizonArguments +
                    " --max-frames-per-file 30000 --epochs 2 --checkpoint-path " + QuoteArgument(trainingCheckpoint) +
                    " --report-path " + QuoteArgument(report);
                ProcessStartInfo info = new ProcessStartInfo(pythonExecutable.Text.Trim(), command);
                info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                string bundledTorch = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "torch_runtime"));
                if (Directory.Exists(bundledTorch)) info.EnvironmentVariables["PYTHONPATH"] = bundledTorch;
                try
                {
                    trainPythonProfile.Enabled = false;
                    horizonStudyStatus.Text = "Training " + profileKey + " into category " + category + " (frozen latest-file holdout)...";
                    horizonStudyStatus.ForeColor = Color.DarkGoldenrod;
                    Process job = new Process(); job.StartInfo = info; job.EnableRaisingEvents = true;
                    job.OutputDataReceived += delegate(object ignored, DataReceivedEventArgs line)
                    {
                        if (!string.IsNullOrWhiteSpace(line.Data)) BeginInvoke((MethodInvoker)delegate { Append("training: " + line.Data); });
                    };
                    job.ErrorDataReceived += delegate(object ignored, DataReceivedEventArgs line)
                    {
                        if (!string.IsNullOrWhiteSpace(line.Data)) BeginInvoke((MethodInvoker)delegate { Append("training error: " + line.Data); });
                    };
                    job.Exited += delegate
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            trainPythonProfile.Enabled = true;
                            double edge;
                            if (job.ExitCode == 0 && File.Exists(trainingCheckpoint) && File.Exists(report) && TryReadHoldoutEdge(report, out edge))
                            {
                                if (edge > 0.0)
                                {
                                    File.Copy(trainingCheckpoint, activeCheckpoint, true);
                                    File.WriteAllText(activeMetadata,
                                        "{\"format\":\"lnn-dashboard-active-checkpoint/v1\",\"category\":\"" + category +
                                        "\",\"profile\":\"" + profileKey + "\",\"symbol\":\"" + trainingSymbol.Text.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") +
                                        "\",\"tick_size\":" + configuredTickSize.ToString("R", CultureInfo.InvariantCulture) + ",\"checkpoint\":\"active.pt\",\"holdout_edge\":" +
                                        edge.ToString("R", CultureInfo.InvariantCulture) + ",\"created_utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}");
                                    horizonStudyStatus.Text = "Training complete: active checkpoint promoted for " + category + " (holdout edge " + edge.ToString("+0.00%;-0.00%;0.00%", CultureInfo.InvariantCulture) + ").";
                                    horizonStudyStatus.ForeColor = Color.DarkGreen;
                                }
                                else
                                {
                                    horizonStudyStatus.Text = "Training complete, but holdout edge " + edge.ToString("+0.00%;-0.00%;0.00%", CultureInfo.InvariantCulture) + "; active checkpoint was not replaced.";
                                    horizonStudyStatus.ForeColor = Color.DarkGoldenrod;
                                }
                            }
                            else
                            {
                                horizonStudyStatus.Text = "Python training failed or did not create a readable report; active checkpoint unchanged.";
                                horizonStudyStatus.ForeColor = Color.Firebrick;
                            }
                        });
                    };
                    job.Start(); job.BeginOutputReadLine(); job.BeginErrorReadLine();
                }
                catch (Exception error)
                {
                    trainPythonProfile.Enabled = true;
                    horizonStudyStatus.Text = "Python training could not start.";
                    horizonStudyStatus.ForeColor = Color.Firebrick;
                    Append(error.Message);
                }
            }
        }

        private static void SetMetric(Label label, string title, string value)
        {
            label.Text = title + "\r\n" + value;
        }

        private void SetStatus(string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }

        private void AppendLearningMonitorEvent(string line)
        {
            const int maxCharacters = 60000;
            if (learningMonitorHistory.Text == "No live learner events yet.")
                learningMonitorHistory.Clear();
            if (learningMonitorHistory.TextLength > maxCharacters)
            {
                int trim = Math.Min(12000, learningMonitorHistory.TextLength);
                learningMonitorHistory.Select(0, trim);
                learningMonitorHistory.SelectedText = string.Empty;
            }
            learningMonitorHistory.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }

        private void RefreshShadowTeacherSummary()
        {
            SetStatusTextWithoutMovingReader(shadowTeacherSummary,
                shadowNoveltyStatus + "\r\n\r\n" + shadowReversalStatus + "\r\n\r\n" + shadowReversalFusionStatus + "\r\n\r\nBackground teacher: " + shadowTeacherStatus);
        }

        private void UpdateReversalTab(string eventLine)
        {
            SetStatusTextWithoutMovingReader(reversalSummaryBox, shadowReversalStatus + "\r\n\r\n" + shadowReversalFusionStatus);
            if (reversalEventLog.Text == "No reversal events yet.") reversalEventLog.Clear();
            if (reversalEventLog.TextLength > 40000)
            {
                int trim = Math.Min(10000, reversalEventLog.TextLength);
                reversalEventLog.Select(0, trim); reversalEventLog.SelectedText = string.Empty;
            }
            reversalEventLog.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + eventLine + Environment.NewLine);
        }

        private void AppendShadowTeacherEvent(string line)
        {
            const int maxCharacters = 60000;
            if (shadowTeacherHistory.Text == "No shadow or teacher events yet.") shadowTeacherHistory.Clear();
            if (shadowTeacherHistory.TextLength > maxCharacters)
            {
                int trim = Math.Min(12000, shadowTeacherHistory.TextLength);
                shadowTeacherHistory.Select(0, trim);
                shadowTeacherHistory.SelectedText = string.Empty;
            }
            AppendHistoryWithoutMovingReader(shadowTeacherHistory,
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }

        private static int GetFirstVisibleLine(TextBox box)
        {
            if (!box.IsHandleCreated)
                return 0;
            return SendMessage(box.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }

        private static void RestoreFirstVisibleLine(TextBox box, int firstVisibleLine)
        {
            if (!box.IsHandleCreated)
                return;
            int currentFirstVisibleLine = GetFirstVisibleLine(box);
            int lineDelta = firstVisibleLine - currentFirstVisibleLine;
            if (lineDelta != 0)
                SendMessage(box.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(lineDelta));
        }

        private static void SetStatusTextWithoutMovingReader(TextBox box, string text)
        {
            if (String.Equals(box.Text, text, StringComparison.Ordinal))
                return;

            int firstVisibleLine = GetFirstVisibleLine(box);
            box.Text = text;
            RestoreFirstVisibleLine(box, firstVisibleLine);
        }

        private static void AppendHistoryWithoutMovingReader(TextBox box, string text)
        {
            int firstVisibleLine = GetFirstVisibleLine(box);
            bool wasAtBottom = box.GetLineFromCharIndex(
                box.GetCharIndexFromPosition(new Point(1, Math.Max(1, box.ClientSize.Height - 2)))) >= Math.Max(0, box.Lines.Length - 2);

            box.AppendText(text);
            if (!wasAtBottom)
                RestoreFirstVisibleLine(box, firstVisibleLine);
        }

        private void AppendTemporalObserverEvent(string line)
        {
            const int maxCharacters = 60000;
            if (temporalObserverHistory.Text == "No observer events yet.") temporalObserverHistory.Clear();
            if (temporalObserverHistory.TextLength > maxCharacters)
            {
                int trim = Math.Min(12000, temporalObserverHistory.TextLength);
                temporalObserverHistory.Select(0, trim);
                temporalObserverHistory.SelectedText = string.Empty;
            }
            temporalObserverHistory.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }

        private static bool IsHighFrequencyTelemetry(string line)
        {
            // The learner can emit a burst when NinjaTrader changes chart
            // state or the feed reconnects. The detailed record remains in
            // the engine's file logs; the dashboard only needs a sampled UI
            // update. Preserve material lifecycle/checkpoint messages.
            return !line.StartsWith("paper-signal=", StringComparison.Ordinal) && // signal changes must never be throttled or dropped
                !line.StartsWith("frames=", StringComparison.Ordinal) &&   // once-per-second core metrics — never throttle
                !line.StartsWith("twotick ", StringComparison.Ordinal) &&  // once-per-second 2-tick calibration
                !line.StartsWith("flicker ", StringComparison.Ordinal) &&  // once-per-second phantom-liquidity readout
                !line.StartsWith("flicker-levels ", StringComparison.Ordinal) && // once-per-second per-level spoof gauge
                !line.StartsWith("hog-fusion", StringComparison.Ordinal) &&
                !line.StartsWith("hog-gate", StringComparison.Ordinal) &&
                !line.StartsWith("hog-evidence", StringComparison.Ordinal) &&
                !line.StartsWith("paper-trade-open", StringComparison.Ordinal) && // trade lifecycle must never be dropped
                !line.StartsWith("paper-trade-close", StringComparison.Ordinal) &&
                !line.StartsWith("engine-order", StringComparison.Ordinal) &&
                !line.StartsWith("paper-outcome ", StringComparison.Ordinal) && // once-per-second paper-signal scoring
                !line.StartsWith("validated-best-saved", StringComparison.Ordinal) &&
                !line.StartsWith("session-best-initialized", StringComparison.Ordinal) &&
                !line.StartsWith("manual-model-saved", StringComparison.Ordinal) &&
                !line.StartsWith("compact-teacher candidate adopted", StringComparison.Ordinal) &&
                !line.StartsWith("reversal-tracker=shadow", StringComparison.Ordinal) &&
                !ReversalMetrics.IsMatch(line) &&
                !ReversalFusionMetrics.IsMatch(line) &&
                !line.StartsWith("Bridge connected", StringComparison.Ordinal) &&
                !line.StartsWith("Direct MMF connected", StringComparison.Ordinal) && // attach status: never sample away
                !line.StartsWith("direct-mmf", StringComparison.Ordinal) &&           // granularity / warmup / idle watchdog
                !line.StartsWith("book-rejected", StringComparison.Ordinal) &&        // frames arriving but rejected
                !line.StartsWith("latency-hygiene", StringComparison.Ordinal) &&
                !line.StartsWith("latency-clock", StringComparison.Ordinal) &&
                !line.StartsWith("host-jitter", StringComparison.Ordinal) &&
                !line.StartsWith("Sequence discontinuity", StringComparison.Ordinal) &&
                !line.StartsWith("ERROR", StringComparison.Ordinal);
        }

        // v64: the engine appends baseline / skill / eviction fields to the
        // reversal line; show them when present so accuracy can be judged
        // against the majority rate instead of in isolation.
        private static string ReversalExtra(string line)
        {
            Match m = Regex.Match(line, @"reversal-baseline=(?<b>[0-9.]+)% reversal-rate=(?<r>[0-9.]+)% reversal-skill=(?<s>[+-]?[0-9.]+)% reversal-evicted=(?<e>\d+) reversal-capacity=(?<c>\d+)");
            if (!m.Success) return "";
            double skill = 0; double.TryParse(m.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out skill);
            return "\r\nMajority baseline " + m.Groups["b"].Value + "%   reversal rate " + m.Groups["r"].Value + "%   SKILL vs baseline " + m.Groups["s"].Value + "%" +
                (skill <= 0 ? " (no edge yet)" : "") + "   evicted " + m.Groups["e"].Value + " / capacity " + m.Groups["c"].Value;
        }

        private void Append(string line, bool showOnScreen = true)
        {
            string message = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
            if (showOnScreen)
            {
                // v64m: buffered - FlushPendingLogs() paints it within LogFlushIntervalMs.
                // The file log below still gets every line immediately.
                pendingOutputLog.Append(message).Append(Environment.NewLine);
            }
            if (logWriter != null)
            {
                try { logWriter.WriteLine(message); }
                catch (IOException) { CloseLogWriter(); }
            }
        }

        // ====================================================================
        // v64m — coalesced painting of the two on-screen logs
        // ====================================================================
        // One AppendText per box per tick instead of one per event. Trimming is
        // deferred while the user has scrolled up, so reading back through the log
        // no longer fights with new lines arriving.

        private void FlushPendingLogs()
        {
            FlushLogInto(output, pendingOutputLog, MaximumOnScreenLogCharacters, OnScreenLogTrimCharacters, null);
            FlushLogInto(tradeFillLog, pendingFillLog, 60000, 12000, "No trade events yet.");
        }

        private static void FlushLogInto(TextBox box, StringBuilder pending, int maxChars, int trimChars, string placeholder)
        {
            if (box == null || pending.Length == 0) return;
            if (!box.IsHandleCreated) return;       // nothing to paint into yet - keep the lines buffered
            string text = pending.ToString();
            pending.Length = 0;
            try
            {
                if (placeholder != null && box.Text == placeholder) box.Clear();

                bool following = IsScrolledToBottom(box);
                int firstVisible = following ? 0 : FirstVisibleLine(box);

                // Trim to fit BEFORE appending, sized against what the box is about
                // to hold, so one busy flush cannot leave it over the cap. The box
                // stays bounded whether or not anyone is looking at it.
                int removedLines = 0;
                int projected = box.TextLength + text.Length;
                if (projected > maxChars && box.TextLength > 0)
                {
                    int trim = Math.Min(box.TextLength, Math.Max(trimChars, projected - maxChars));
                    removedLines = box.GetLineFromCharIndex(trim);
                    box.Select(0, trim);
                    box.SelectedText = string.Empty;
                }

                box.AppendText(text);   // AppendText follows the tail

                // If the user has scrolled up to read, put the view back where they
                // left it - allowing for any lines the trim above took off the top.
                // Text moving out from under someone mid-read is the whole reason
                // this coalescing exists.
                if (!following)
                {
                    int target = Math.Max(0, firstVisible - removedLines);
                    int nowVisible = FirstVisibleLine(box);
                    if (nowVisible != target) ScrollLines(box, target - nowVisible);
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;

        private static int FirstVisibleLine(TextBox box)
        {
            try { return (int)SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero); }
            catch { return 0; }
        }

        private static void ScrollLines(TextBox box, int lines)
        {
            if (lines == 0) return;
            try { SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lines); }
            catch { }
        }

        private static bool IsScrolledToBottom(TextBox box)
        {
            try
            {
                if (box.TextLength == 0) return true;
                int lineHeight = box.Font.Height; if (lineHeight <= 0) lineHeight = 14;
                int visible = Math.Max(1, box.ClientSize.Height / lineHeight);
                int last = box.GetLineFromCharIndex(box.TextLength);
                return FirstVisibleLine(box) + visible >= last;   // within one screen of the end counts as following
            }
            catch { return true; }
        }

        private void CloseLogWriter()
        {
            StreamWriter local = logWriter;
            logWriter = null;
            if (local == null) return;
            try { local.Dispose(); }
            catch (IOException) { }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
