using System; using System.IO.MemoryMappedFiles; using System.Threading; using System.Runtime.InteropServices;
using System.Reflection;
using NinjaTrader.NinjaScript; using NinjaTrader.NinjaScript.Strategies; using NinjaTrader.Cbi;

// v64y: every entry gate now travels over the command ring (ops 25/26) and is applied in
// SubmitEntryGated, so engine-direct orders - which the dashboard never sees - obey them too.
// This test drives the REAL MMF ring, not the methods directly, so it proves the wire format
// as well as the logic. Each gate must block its own case, block NOTHING else, and never
// touch exits.
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
struct GCmd { public long CommandId; public int Op; public int Qty; public double Price; public double StopPrice; public byte Tif; public long SendUtcTicks; public float Trail; public double Sl; public double Pt; }

class MmfGateTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static MemoryMappedViewAccessor cv; static long seq; static long id = 9000;
    static int failures = 0;
    static object strat; static MethodInfo onData, flush; static Type T;
    static LnnOrderExecStrategy S;

    static void Send(int op, int qty, double price, double stop, double sl, double pt, float trail, byte tif)
    {
        GCmd c = new GCmd { CommandId = ++id, Op = op, Qty = qty, Price = price, StopPrice = stop,
                            Sl = sl, Pt = pt, Trail = trail, Tif = tif, SendUtcTicks = DateTime.UtcNow.Ticks };
        long next = seq + 1; long off = 64 + (next & (131072 - 1)) * 72;
        cv.Write(off, next * 2 + 1); cv.Write(off + 8, ref c); cv.Write(off, next * 2); cv.Write(0, next);
        seq = next;
        // Wake the reader the way the dashboard does. Without this the reader only
        // notices on its 250 ms blocking-wait timeout, and a command can still be in
        // the ring when the next one is written - which reads as a one-command lag.
        if (wake != null) { try { wake.Set(); } catch { } }
        Thread.Sleep(120);
        Pump();
    }
    static EventWaitHandle wake;
    static void Order(int op) { Send(op, 1, 0, 0, 0, 0, 0f, 0); }
    static void SignalContext(double pUp) { Send(46, 1, pUp, 0, 0, 0, 0f, 0); }

    // Op 25: Qty=max trades, Price=gap s, StopPrice=cooldown s, Sl=margin, Pt=dead-regime s, Trail=clear s, Tif=direction
    static void GatesA(int maxTrades, double gapS, double cooldownS, double margin, double deadS, float clearS, byte direction)
    { Send(25, maxTrades, gapS, cooldownS, margin, deadS, clearS, direction); }
    // Op 26: Qty=max flips, Price=session start min, StopPrice=session end min, Sl=dead low, Pt=dead high, Trail=ramp, Tif=flags
    static void GatesB(int maxFlips, int startMin, int endMin, double low, double high, float ramp, byte flags)
    { Send(26, maxFlips, startMin, endMin, low, high, ramp, flags); }

    static void Pump()
    {
        var md = new NinjaTrader.Data.MarketDataEventArgs
        { MarketDataType = NinjaTrader.Data.MarketDataType.Last, Price = 5000, Volume = 1, Time = DateTime.Now };
        try { onData.Invoke(strat, new object[] { md }); } catch { }
        try { flush.Invoke(strat, null); } catch { }
        Thread.Sleep(100);
    }

    // Op 28: Qty=min book size, Price=max spread t, StopPrice=min imbalance,
    //        Sl=max flicker/s, Pt=max aggression, Trail=signal age ms, Tif=bit0 microprice
    static void BookGates(int minSize, double maxSpread, double minImbalance, double maxFlicker,
                          double maxAggression, float signalAgeMs, bool microprice)
    { Send(28, minSize, maxSpread, minImbalance, maxFlicker, maxAggression, signalAgeMs, (byte)(microprice ? 1 : 0)); }

    static void SetBook(double bid, long bidSize, double ask, long askSize)
    {
        T.GetField("tobBidPrice", NP).SetValue(strat, bid);
        T.GetField("tobBidSize",  NP).SetValue(strat, bidSize);
        T.GetField("tobAskPrice", NP).SetValue(strat, ask);
        T.GetField("tobAskSize",  NP).SetValue(strat, askSize);
    }

    static int GateCode() { return (int)T.GetField("lastEntryGateBlock", NP).GetValue(strat); }
    static string GateName(int c) { return (string)T.GetMethod("GateBlockName", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { c }); }

    static void Check(string what, bool ok, string detail)
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail); if (!ok) failures++; }

    // Runs one entry and reports whether it reached NT8.
    static bool EntrySubmitted(int op)
    {
        int before = S.Orders.Count;
        Order(op);
        return S.Orders.Count > before;
    }

    static void Main()
    {
        var st = new LnnOrderExecStrategy();
        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.SetDefaults); Call(st, "OnStateChange");
        // Take the pre-existing NT8-side caps out of the picture so each check isolates
        // exactly one v64y gate - otherwise a max-position rejection would masquerade as one.
        st.MaxPositionSize = 1000; st.MaxOrdersPerMinute = 0; st.PrintToOutputWindow = false;
        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.Realtime); Call(st, "OnStateChange");
        Thread.Sleep(1200);

        strat = st; S = st; T = st.GetType();
        onData = T.GetMethod("OnMarketData", NP);
        flush  = T.GetMethod("FlushDeferred");

        var cmap = MemoryMappedFile.OpenExisting(@"Local\LNN_ORDER_CMD_MES");
        cv = cmap.CreateViewAccessor(); seq = cv.ReadInt64(0);
        try { wake = EventWaitHandle.OpenExisting(@"Local\LNN_ORDER_CMD_MES_EVENT"); } catch { wake = null; }

        // ---- gates enabled, everything switched off: nothing may block ----
        GatesB(0, -1, -1, 0.45, 0.55, 0f, 1);          // bit0 = gates enabled
        GatesA(0, 0, 0, 0, 0, 3f, 0);
        Check("baseline: an entry passes with every gate off", EntrySubmitted(0),
              "gates enabled but all thresholds 0 - nothing should block");
        Check("baseline reports no block", GateCode() == 0, "gate code " + GateCode() + " (" + GateName(GateCode()) + ")");

        // ---- direction filter ----
        GatesA(0, 0, 0, 0, 0, 3f, 1);                  // longs only
        bool shortBlocked = !EntrySubmitted(1);
        int shortCode = GateCode();
        Check("direction filter blocks the wrong side", shortBlocked && shortCode == 9,
              "SHORT with 'longs only' -> " + GateName(shortCode));
        Check("direction filter lets the right side through", EntrySubmitted(0),
              "LONG with 'longs only' still submits");
        GatesA(0, 0, 0, 0, 0, 3f, 0);                  // back to both

        // ---- max trades per session ----
        // The counter is already above 1 from the entries above, so 1 must now block.
        GatesA(1, 0, 0, 0, 0, 3f, 0);
        bool maxBlocked = !EntrySubmitted(0);
        Check("max trades per session blocks", maxBlocked && GateCode() == 10,
              "limit 1 with entries already taken -> " + GateName(GateCode()));
        GatesA(0, 0, 0, 0, 0, 3f, 0);

        // ---- trade gap ----
        GatesA(0, 30, 0, 0, 0, 3f, 0);
        bool gapBlocked = !EntrySubmitted(0);
        Check("trade gap blocks a too-soon entry", gapBlocked && GateCode() == 11,
              "30 s gap right after an entry -> " + GateName(GateCode()));
        GatesA(0, 0, 0, 0, 0, 3f, 0);

        // ---- session window ----
        int nowMin = (int)DateTime.Now.TimeOfDay.TotalMinutes;
        int farStart = (nowMin + 120) % 1440, farEnd = (nowMin + 180) % 1440;
        GatesB(0, farStart, farEnd, 0.45, 0.55, 0f, 1);
        bool windowBlocked = !EntrySubmitted(0);
        Check("session window blocks outside its hours", windowBlocked && GateCode() == 6,
              "window " + farStart / 60 + ":" + (farStart % 60).ToString("00") + "-" +
              farEnd / 60 + ":" + (farEnd % 60).ToString("00") + " -> " + GateName(GateCode()));

        // FLATTEN must still work while a gate is blocking - a gate that traps an open
        // position is worse than no gate at all.
        int beforeFlat = S.Orders.Count;
        Order(13);
        Check("flatten still passes while entries are gated", S.Orders.Count >= beforeFlat,
              "op 13 accepted with the session window shut");
        GatesB(0, -1, -1, 0.45, 0.55, 0f, 1);          // window off again

        // ---- confidence margin (uses the P(up) the engine stamps via op 46) ----
        GatesA(0, 0, 0, 0.05, 0, 3f, 0);               // needs >= 0.65 for a long
        SignalContext(0.61);
        bool marginBlocked = !EntrySubmitted(0);
        Check("confidence margin blocks a marginal read", marginBlocked && GateCode() == 8,
              "p=0.610 against 0.60+0.05 -> " + GateName(GateCode()));
        SignalContext(0.70);
        Check("confidence margin passes a strong read", EntrySubmitted(0),
              "p=0.700 clears 0.60+0.05");
        GatesA(0, 0, 0, 0, 0, 3f, 0);

        // ---- dead-regime hysteresis ----
        GatesA(0, 0, 0, 0, 1, 3f, 0);                  // block after 1 s neutral, clear after 3 s
        SignalContext(0.50);
        Check("one neutral reading does not block", EntrySubmitted(0),
              "blocking must take SUSTAINED neutrality, not a single print");
        Thread.Sleep(1200);
        SignalContext(0.50);
        bool deadBlocked = !EntrySubmitted(0);
        Check("dead regime blocks after sustained neutrality", deadBlocked && GateCode() == 7,
              "1 s inside the 0.45-0.55 dead zone -> " + GateName(GateCode()));
        SignalContext(0.72);
        Check("one directional spike does not re-open", !EntrySubmitted(0),
              "conviction has to be HELD for the clear window first");

        // ================= v64z fixes =================

        // ARM (op 21) must start a fresh session for the counting gates. Without this a
        // strategy left running stays stuck on "max trades per session" until midnight,
        // and re-arming looks like it does nothing.
        GatesA(1, 0, 0, 0, 0, 3f, 0);                  // limit 1, already exceeded
        bool blockedBeforeArm = !EntrySubmitted(0);
        Send(21, 0, 0, 0, 0, 0, 0f, 0);                // ARM / resume
        bool flowsAfterArm = EntrySubmitted(0);
        Check("ARM resets the counting gates", blockedBeforeArm && flowsAfterArm,
              "blocked on max-trades, then ARM, then the next entry goes through");
        GatesA(0, 0, 0, 0, 0, 3f, 0);

        // A gate must never block an order that REDUCES an open position - ops 0/1 are used
        // to flip as well as to open, so a blocked cover would trap the position.
        GatesA(0, 0, 0, 0, 0, 3f, 1);                  // longs only: a new SHORT is blocked
        Check("longs-only blocks opening a short", !EntrySubmitted(1),
              "-> " + GateName(GateCode()));
        S.Position.MarketPosition = MarketPosition.Long; S.Position.Quantity = 1;
        Check("the same SELL passes when it closes a long", EntrySubmitted(1),
              "reducing exposure is never gated - this is what stops a gate trapping a position");
        S.Position.MarketPosition = MarketPosition.Flat; S.Position.Quantity = 0;
        GatesA(0, 0, 0, 0, 0, 3f, 0);

        // The confidence margin must measure against the thresholds actually in use, not a
        // hardcoded 0.60/0.40 - anyone who had changed them was silently blocked on every entry.
        Send(27, 0, 0.55, 0.45, 0, 0, 0f, 0);
        GatesA(0, 0, 0, 0.02, 0, 3f, 0);
        SignalContext(0.58);
        Check("margin measured against the configured thresholds", EntrySubmitted(0),
              "p=0.580 clears 0.55+0.02; against the old hardcoded 0.60 it would have been blocked");
        GatesA(0, 0, 0, 0, 0, 3f, 0);

        // ================= v65a book-quality and health gates =================
        // Config still travels over the real ring (ops 28/29). The BOOK is set directly so each
        // check is deterministic - the depth plumbing that normally fills it belongs to the
        // executioner and is exercised by its own tests.

        // spread
        SetBook(5000.00, 50, 5000.75, 50);                 // 3 ticks wide
        BookGates(0, 2, 0, 0, 0, 0f, false);               // max spread 2t
        Check("spread gate blocks a wide market", !EntrySubmitted(0) && GateCode() == 14,
              "3t spread against a 2t cap -> " + GateName(GateCode()));
        SetBook(5000.00, 50, 5000.25, 50);                 // 1 tick
        Check("spread gate passes a tight market", EntrySubmitted(0), "1t spread clears a 2t cap");

        // no book at all - a book gate must say so rather than silently allowing
        SetBook(0, 0, 0, 0);
        Check("book gate blocks when there is no book", !EntrySubmitted(0) && GateCode() == 19,
              "-> " + GateName(GateCode()));
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // thin book: size on the side we would TAKE (ask for a buy)
        SetBook(5000.00, 500, 5000.25, 10);
        BookGates(100, 0, 0, 0, 0, 0f, false);
        Check("thin book blocks a buy on a 10-lot ask", !EntrySubmitted(0) && GateCode() == 19,
              "ask size 10 against a floor of 100 -> " + GateName(GateCode()));
        SetBook(5000.00, 500, 5000.25, 400);
        Check("thick book passes", EntrySubmitted(0), "ask size 400 clears the floor");
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // imbalance must lean the way we are entering
        BookGates(0, 0, 0.30, 0, 0, 0f, false);
        SetBook(5000.00, 20, 5000.25, 80);                 // imbalance -0.60, against a buy
        Check("imbalance gate blocks a buy into an offered book", !EntrySubmitted(0) && GateCode() == 15,
              "-> " + GateName(GateCode()));
        SetBook(5000.00, 80, 5000.25, 20);                 // imbalance +0.60, with the buy
        Check("imbalance gate passes a buy into a bid book", EntrySubmitted(0), "imbalance +0.60 clears 0.30");
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // microprice must sit on our side of the mid
        BookGates(0, 0, 0, 0, 0, 0f, true);
        SetBook(5000.00, 10, 5000.25, 90);                 // microprice below mid
        Check("microprice gate blocks a buy when the touch leans down", !EntrySubmitted(0) && GateCode() == 16,
              "-> " + GateName(GateCode()));
        SetBook(5000.00, 90, 5000.25, 10);                 // microprice above mid
        Check("microprice gate passes a buy when the touch leans up", EntrySubmitted(0), "microprice above mid");
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // quote flicker
        var flicker = (System.Collections.Generic.Queue<DateTime>)T.GetField("touchFlickerEvents", NP).GetValue(strat);
        flicker.Clear(); for (int i = 0; i < 6; i++) flicker.Enqueue(DateTime.Now);
        BookGates(0, 0, 0, 3, 0, 0f, false);               // max 3 touch changes/s
        Check("flicker gate blocks an unstable touch", !EntrySubmitted(0) && GateCode() == 17,
              "6 touch changes in the last second against a cap of 3 -> " + GateName(GateCode()));
        flicker.Clear();
        Check("flicker gate passes a steady touch", EntrySubmitted(0), "queue drained");
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // aggression already running our side
        var buyAggr = (System.Collections.Generic.Queue<System.Collections.Generic.KeyValuePair<DateTime, long>>)
                      T.GetField("buyAggr", NP).GetValue(strat);
        buyAggr.Clear();
        buyAggr.Enqueue(new System.Collections.Generic.KeyValuePair<DateTime, long>(DateTime.Now, 250));
        BookGates(0, 0, 0, 0, 100, 0f, false);             // block at 100 lots into our side
        Check("aggression gate blocks chasing a runaway", !EntrySubmitted(0) && GateCode() == 18,
              "250 lots already lifted the ask against a cap of 100 -> " + GateName(GateCode()));
        buyAggr.Clear();
        Check("aggression gate passes a quiet tape", EntrySubmitted(0), "queue drained");
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // stale signal context
        BookGates(0, 0, 0, 0, 0, 200f, false);             // op 46 must be < 200 ms old
        SignalContext(0.72);
        Thread.Sleep(500);
        Check("stale signal blocks an entry on an old context", !EntrySubmitted(0) && GateCode() == 20,
              "context 500 ms old against a 200 ms limit -> " + GateName(GateCode()));
        BookGates(0, 0, 0, 0, 0, 0f, false);

        // Health gates are pure state, so they are checked at the gate function directly
        // rather than through a market-data pump that would refresh the very clock under test.
        MethodInfo gate = T.GetMethod("EntryGateBlock", NP);
        Send(29, 0, 500, 0, 0, 0, 0f, 0);                  // feed stale > 500 ms blocks
        T.GetField("lastMarketDataTicks", NP).SetValue(strat, DateTime.UtcNow.AddSeconds(-5).Ticks);
        Check("feed-health gate blocks on a stale feed", (int)gate.Invoke(strat, new object[] { true }) == 21,
              "no market data for 5 s against a 500 ms limit");
        T.GetField("lastMarketDataTicks", NP).SetValue(strat, DateTime.UtcNow.Ticks);
        Check("feed-health gate passes on a live feed", (int)gate.Invoke(strat, new object[] { true }) == 0,
              "data just arrived");

        Send(29, 0, 0, 30, 0, 0, 0f, 0);                   // give back 30% of peak stops the day
        T.GetField("peakRealizedTicks", NP).SetValue(strat, 100.0);
        T.GetField("realizedTicksToday", NP).SetValue(strat, 65.0);
        Check("give-back gate stops the day", (int)gate.Invoke(strat, new object[] { true }) == 22,
              "peak +100t, now +65t, limit 30% -> stopped");
        T.GetField("realizedTicksToday", NP).SetValue(strat, 85.0);
        Check("give-back gate allows a shallow pullback", (int)gate.Invoke(strat, new object[] { true }) == 0,
              "peak +100t, now +85t -> still trading");
        Send(29, 0, 0, 0, 0, 0, 0f, 0);
        T.GetField("peakRealizedTicks", NP).SetValue(strat, 0.0);
        T.GetField("realizedTicksToday", NP).SetValue(strat, 0.0);

        // ---- master switch off: the strategy must behave exactly as it did before v64y ----
        GatesB(0, -1, -1, 0.45, 0.55, 0f, 0);          // bit0 clear = gates disabled
        Check("disabling the gates re-opens everything", EntrySubmitted(0),
              "still inside the dead regime, but the master switch is off");
        Check("disabled gates report no block", GateCode() == 0, "gate code " + GateCode());

        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.Terminated); Call(st, "OnStateChange");
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
    static void Call(object o, string m) { o.GetType().GetMethod(m, NP).Invoke(o, null); }
}
