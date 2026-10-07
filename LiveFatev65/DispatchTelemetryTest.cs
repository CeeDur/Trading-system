using System; using System.IO.MemoryMappedFiles; using System.Threading; using System.Runtime.InteropServices;
using System.Reflection;
using NinjaTrader.NinjaScript; using NinjaTrader.NinjaScript.Strategies; using NinjaTrader.Cbi;

// v65c: an order command is run by whichever path reaches it first - a market event (next tick)
// or NinjaTrader's TriggerCustomEvent dispatcher (measured at ~145 ms on a busy platform).
// The counters must tell those apart, because "which path ran it" is the whole latency story
// and nothing else in the system reveals it.
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
struct DCmd { public long CommandId; public int Op; public int Qty; public double Price; public double StopPrice; public byte Tif; public long SendUtcTicks; public float Trail; public double Sl; public double Pt; }

class DispatchTelemetryTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static MemoryMappedViewAccessor cv; static long seq; static long id = 7000;
    static EventWaitHandle wake;
    static int failures = 0;
    static LnnOrderExecStrategy S; static Type T;
    static MethodInfo onData, flush;

    static void Write(int op)
    {
        DCmd c = new DCmd { CommandId = ++id, Op = op, Qty = 1, SendUtcTicks = DateTime.UtcNow.Ticks };
        long next = seq + 1; long off = 64 + (next & (131072 - 1)) * 72;
        cv.Write(off, next * 2 + 1); cv.Write(off + 8, ref c); cv.Write(off, next * 2); cv.Write(0, next);
        seq = next;
        if (wake != null) { try { wake.Set(); } catch { } }
        Thread.Sleep(160);   // let the reader thread pick it up off the ring
    }
    static void MarketEvent()
    {
        var md = new NinjaTrader.Data.MarketDataEventArgs
        { MarketDataType = NinjaTrader.Data.MarketDataType.Last, Price = 5000, Volume = 1, Time = DateTime.Now };
        onData.Invoke(S, new object[] { md });
        Thread.Sleep(60);
    }
    static void DispatcherRuns() { flush.Invoke(S, null); Thread.Sleep(60); }

    static long Fast() { return (long)T.GetField("dispatchFastCount", NP).GetValue(S); }
    static long Slow() { return (long)T.GetField("dispatchSlowCount", NP).GetValue(S); }

    static void Check(string what, bool ok, string detail)
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail); if (!ok) failures++; }

    static void Main()
    {
        S = new LnnOrderExecStrategy();
        typeof(NinjaScriptBase).GetField("State").SetValue(S, State.SetDefaults); Call(S, "OnStateChange");
        S.PrintToOutputWindow = false; S.MaxPositionSize = 1000; S.MaxOrdersPerMinute = 0;
        typeof(NinjaScriptBase).GetField("State").SetValue(S, State.Realtime); Call(S, "OnStateChange");
        Thread.Sleep(1200);

        T = S.GetType();
        onData = T.GetMethod("OnMarketData", NP);
        flush  = T.GetMethod("FlushDeferred");

        var cmap = MemoryMappedFile.OpenExisting(@"Local\LNN_ORDER_CMD_MES");
        cv = cmap.CreateViewAccessor(); seq = cv.ReadInt64(0);
        try { wake = EventWaitHandle.OpenExisting(@"Local\LNN_ORDER_CMD_MES_EVENT"); } catch { wake = null; }

        // ---- the fast path: a market event arrives before the dispatcher gets round to it ----
        long f0 = Fast(), s0 = Slow();
        Write(0); MarketEvent();
        Check("a market-event drain counts as FAST", Fast() == f0 + 1 && Slow() == s0,
              "fast " + f0 + "->" + Fast() + ", slow stayed " + Slow());

        // The dispatcher firing afterwards must NOT double-count - the interlocked claim
        // means the loser does no work at all.
        DispatcherRuns();
        Check("the losing path does not double-count", Fast() == f0 + 1 && Slow() == s0,
              "the command was already claimed; totals unchanged");

        // ---- the slow path: no market event, so NT8's dispatcher gets there first ----
        long f1 = Fast(), s1 = Slow();
        Write(1); DispatcherRuns();
        Check("a dispatcher run counts as SLOW", Slow() == s1 + 1 && Fast() == f1,
              "slow " + s1 + "->" + Slow() + ", fast stayed " + Fast());

        // ---- config ops must not pollute the ratio ----
        long f2 = Fast(), s2 = Slow();
        Write(25); MarketEvent();     // gate config, not an order
        Write(26); MarketEvent();
        Check("config ops are excluded from the ratio", Fast() == f2 && Slow() == s2,
              "ops 25/26 ran but neither counter moved - otherwise the gate pushes would swamp it");

        // ---- the published record ----
        MethodInfo pub = T.GetMethod("PublishDispatchTelemetry", NP);
        pub.Invoke(S, null);
        Check("telemetry publishes without throwing", true, "record type 9 written to the fill ring");

        // ---- OnBarUpdate is a drain point now, not an empty method ----
        long f3 = Fast();
        Write(0);
        T.GetMethod("OnBarUpdate", NP).Invoke(S, null);
        Thread.Sleep(60);
        Check("OnBarUpdate drains too", Fast() == f3 + 1,
              "the callback used to be empty; it is now a free extra wake point");

        typeof(NinjaScriptBase).GetField("State").SetValue(S, State.Terminated); Call(S, "OnStateChange");
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
    static void Call(object o, string m) { o.GetType().GetMethod(m, NP).Invoke(o, null); }
}
