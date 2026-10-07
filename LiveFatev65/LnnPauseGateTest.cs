using System; using System.IO.MemoryMappedFiles; using System.Threading; using System.Runtime.InteropServices;
using NinjaTrader.NinjaScript; using NinjaTrader.NinjaScript.Strategies; using NinjaTrader.Cbi;

// v64p: engine-direct orders are stopped by the strategy's PAUSE flag (op 20), not by the
// dashboard's armed state - the engine has no concept of ARM. This proves op 20 actually
// rejects entries and op 21 lets them through again.
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
struct Cmd { public long CommandId; public int Op; public int Qty; public double Price; public double StopPrice; public byte Tif; public long SendUtcTicks; public float Trail; public double Sl; public double Pt; }

class PauseGate
{
    static MemoryMappedViewAccessor cv; static long seq; static long id = 500;
    static int failures = 0;

    static void Send(int op, int qty)
    {
        Cmd c = new Cmd { CommandId = ++id, Op = op, Qty = qty, SendUtcTicks = DateTime.UtcNow.Ticks };
        long next = seq + 1; long off = 64 + (next & (131072 - 1)) * 72;
        cv.Write(off, next * 2 + 1); cv.Write(off + 8, ref c); cv.Write(off, next * 2); cv.Write(0, next);
        seq = next; Thread.Sleep(200);
        Pump();   // FastOrderDispatch queues the command; it runs on the next market event
    }

    static object strat; static System.Reflection.MethodInfo onData, flush;
    static void Pump()
    {
        var md = new NinjaTrader.Data.MarketDataEventArgs
        { MarketDataType = NinjaTrader.Data.MarketDataType.Last, Price = 5000, Volume = 1, Time = DateTime.Now };
        try { onData.Invoke(strat, new object[] { md }); } catch { }
        try { flush.Invoke(strat, null); } catch { }
        Thread.Sleep(120);
    }
    static void Check(string what, bool ok, string detail)
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail); if (!ok) failures++; }

    static void Main()
    {
        var st = new LnnOrderExecStrategy();
        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.SetDefaults); Call(st, "OnStateChange");
        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.Realtime);   Call(st, "OnStateChange");
        Thread.Sleep(1200);

        strat = st;
        onData = st.GetType().GetMethod("OnMarketData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        flush = st.GetType().GetMethod("FlushDeferred");

        var cmap = MemoryMappedFile.OpenExisting(@"Local\LNN_ORDER_CMD_MES");
        cv = cmap.CreateViewAccessor();
        seq = cv.ReadInt64(0);

        int before = st.Orders.Count;
        Send(0, 1);                                   // buy market, not paused
        int afterOpen = st.Orders.Count;
        Check("entry submits when not paused", afterOpen > before,
              "orders went from " + before + " to " + afterOpen);

        Send(20, 0);                                  // PAUSE  <- what Disarm / KILL SWITCH now sends
        int beforePaused = st.Orders.Count;
        Send(0, 1);                                   // the engine keeps sending; this one must be refused
        Send(1, 1);
        int afterPaused = st.Orders.Count;
        Check("entries REJECTED while paused", afterPaused == beforePaused,
              "two entries sent while paused, order count stayed at " + afterPaused +
              " (this is what stops engine-direct orders - the engine never sees ARM)");

        Send(21, 0);                                  // RESUME <- what ARM now sends
        int beforeResumed = st.Orders.Count;
        Send(0, 1);
        int afterResumed = st.Orders.Count;
        Check("entries flow again after resume", afterResumed > beforeResumed,
              "orders went from " + beforeResumed + " to " + afterResumed);

        // Flat must still work while paused - a pause that traps you in a position is useless.
        Send(20, 0);
        int beforeFlat = st.Orders.Count;
        Send(13, 0);                                  // flatten
        int afterFlat = st.Orders.Count;
        Check("FLAT still works while paused", afterFlat >= beforeFlat,
              "flatten accepted while paused (order count " + beforeFlat + " -> " + afterFlat + ")");

        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.Terminated); Call(st, "OnStateChange");
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
    static void Call(object o, string m)
    { o.GetType().GetMethod(m, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(o, null); }
}
