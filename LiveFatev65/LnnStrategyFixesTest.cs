using System; using System.Collections.Generic; using System.Reflection; using System.Threading;
using NinjaTrader.NinjaScript; using NinjaTrader.NinjaScript.Strategies; using NinjaTrader.Cbi;
using NinjaTrader.Data;

// v64n regression test: the four strategy fixes.
class FixesTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static int failures = 0;

    static object Fld(object o, string name)
    {
        FieldInfo f = o.GetType().GetField(name, NP);
        if (f == null) { Console.WriteLine("!! no field " + name); return null; }
        return f.GetValue(o);
    }
    static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail);
        if (!ok) failures++;
    }

    static void Main()
    {
        var st = new LnnOrderExecStrategy();
        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.SetDefaults); Call(st, "OnStateChange");
        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.Realtime);   Call(st, "OnStateChange");
        Thread.Sleep(1200);

        MethodInfo onDepth = st.GetType().GetMethod("OnMarketDepth", NP);
        MethodInfo onOrder = st.GetType().GetMethod("OnOrderUpdate", NP);
        MethodInfo flicker = st.GetType().GetMethod("FlickerPerSec", NP);

        // ---------------------------------------------------------------
        // FIX 1 - touchFlickerEvents must stay bounded to a 1 s window even
        // though nothing ever calls FlickerPerSec on default settings.
        // ---------------------------------------------------------------
        DateTime t0 = new DateTime(2026, 9, 8, 14, 0, 0, DateTimeKind.Utc);
        int events = 20000;
        var sent = new List<DateTime>();
        for (int i = 0; i < events; i++)
        {
            DateTime when = t0.AddMilliseconds(i);            // 20 000 events over 20 s
            var e = new MarketDepthEventArgs
            {
                Position = 0,
                Operation = Operation.Update,
                MarketDataType = (i % 2 == 0) ? MarketDataType.Bid : MarketDataType.Ask,
                Price = (i % 2 == 0) ? 5000.00 : 5000.25,
                Volume = 10 + (i % 7),
                Time = when
            };
            onDepth.Invoke(st, new object[] { e });
            sent.Add(when);
        }
        var q = (Queue<DateTime>)Fld(st, "touchFlickerEvents");
        DateTime last = t0.AddMilliseconds(events - 1);
        Check("flicker queue bounded",
              q.Count <= 1100,
              "held " + q.Count + " after " + events + " depth events over 20 s (pre-fix it held all " + events + ")");

        // FIX 1 - and the value FlickerPerSec reports must be UNCHANGED.
        int expected = 0;
        foreach (DateTime d in sent) if ((last - d).TotalMilliseconds <= 1000) expected++;
        double got = (double)flicker.Invoke(st, new object[] { last });
        Check("flicker value unchanged",
              Math.Abs(got - expected) < 0.5,
              "FlickerPerSec=" + got + ", independently counted " + expected + " events inside the 1 s window");

        // ---------------------------------------------------------------
        // FIX 3 - a pending bracket survives until NT8 CONFIRMS the entry is
        // dead, and is released on Cancelled / Rejected.
        // ---------------------------------------------------------------
        var brackets = (System.Collections.IDictionary)Fld(st, "pendingBrackets");
        MethodInfo attach = st.GetType().GetMethod("AttachNextBracket", NP);
        FieldInfo slF = st.GetType().GetField("DefaultStopLossTicks");
        FieldInfo ptF = st.GetType().GetField("DefaultProfitTargetTicks");
        if (slF == null) { var p1 = st.GetType().GetProperty("DefaultStopLossTicks"); p1.SetValue(st, 8.0, null); }
        else slF.SetValue(st, 8.0);
        if (ptF == null) { var p2 = st.GetType().GetProperty("DefaultProfitTargetTicks"); p2.SetValue(st, 8.0, null); }
        else ptF.SetValue(st, 8.0);

        brackets.Clear();
        attach.Invoke(st, new object[] { "LNNEXEC:9001" });
        Check("bracket registered", brackets.Contains("LNNEXEC:9001"), "pendingBrackets has the entry after AttachNextBracket");

        // Working / Accepted must NOT release it (the cancel may still lose the race).
        var live = new Order { Name = "LNNEXEC:9001", OrderAction = OrderAction.Buy, Quantity = 1, OrderState = OrderState.Working };
        onOrder.Invoke(st, new object[] { live, 5000.0, 0.0, 1, 0, 0.0, OrderState.Working, DateTime.Now, ErrorCode.NoError, "" });
        Check("bracket kept while working", brackets.Contains("LNNEXEC:9001"),
              "still present after a Working update - a late fill would still get its stop/target");

        // Cancelled releases it.
        live.OrderState = OrderState.Cancelled;
        onOrder.Invoke(st, new object[] { live, 5000.0, 0.0, 1, 0, 0.0, OrderState.Cancelled, DateTime.Now, ErrorCode.NoError, "" });
        Check("bracket released on cancel", !brackets.Contains("LNNEXEC:9001"),
              "removed once NT8 confirmed Cancelled (pre-fix this entry leaked for the session)");

        // Rejected releases it too.
        attach.Invoke(st, new object[] { "LNNEXEC:9002" });
        var rej = new Order { Name = "LNNEXEC:9002", OrderAction = OrderAction.Buy, Quantity = 1, OrderState = OrderState.Rejected };
        onOrder.Invoke(st, new object[] { rej, 5000.0, 0.0, 1, 0, 0.0, OrderState.Rejected, DateTime.Now, ErrorCode.NoError, "" });
        Check("bracket released on reject", !brackets.Contains("LNNEXEC:9002"), "removed on Rejected");

        // ---------------------------------------------------------------
        // FIX 2 - the working-entry count the heartbeat publishes is cached on
        // the strategy thread and is correct.
        // ---------------------------------------------------------------
        st.Orders.Clear();
        st.Orders.Add(new Order { Name = "LNNCMD:501", OrderAction = OrderAction.Buy,  Quantity = 2, OrderState = OrderState.Working });
        st.Orders.Add(new Order { Name = "LNNCMD:502", OrderAction = OrderAction.Sell, Quantity = 1, OrderState = OrderState.Accepted });
        st.Orders.Add(new Order { Name = "LNNCMD:501:SL", OrderAction = OrderAction.Sell, Quantity = 2, OrderState = OrderState.Working }); // exit, not an entry
        st.Orders.Add(new Order { Name = "LNNCMD:499", OrderAction = OrderAction.Buy,  Quantity = 1, OrderState = OrderState.Filled });     // done
        var tick = new Order { Name = "LNNCMD:501", OrderAction = OrderAction.Buy, Quantity = 2, OrderState = OrderState.Working };
        onOrder.Invoke(st, new object[] { tick, 5000.0, 0.0, 2, 0, 0.0, OrderState.Working, DateTime.Now, ErrorCode.NoError, "" });

        int cachedCount  = (int)Fld(st, "cachedWorkingCount");
        int cachedSigned = (int)Fld(st, "cachedWorkingSigned");
        Check("working-entry cache count",  cachedCount == 2,
              "cachedWorkingCount=" + cachedCount + " (expect 2: the Working buy and the Accepted sell; the :SL exit and the Filled entry excluded)");
        Check("working-entry cache signed", cachedSigned == 1,
              "cachedWorkingSigned=" + cachedSigned + " (expect +1: buy 2 minus sell 1)");

        // FIX 2 - and PublishStatus, which runs on the reader thread, must not
        // enumerate Orders at all any more.
        string src = System.IO.File.ReadAllText("/tmp/v64line/LnnOrderExecStrategy.cs");
        int ps = src.IndexOf("private void PublishStatus()");
        int psEnd = src.IndexOf("private ", ps + 30);
        string body = src.Substring(ps, psEnd - ps);
        Check("heartbeat does not touch Orders",
              !body.Contains("WorkingEntries(") && body.Contains("cachedWorkingCount"),
              "PublishStatus reads the cached value instead of walking NT8's Orders from the reader thread");

        // ---------------------------------------------------------------
        // FIX 4 - no raw Print() left on the command or order path.
        // ---------------------------------------------------------------
        string[] mustNotPrint = { "case 22:", "case 23:", "case 24:", "DAILY LOSS LIMIT hit", "order error " };
        foreach (string marker in mustNotPrint)
        {
            int at = src.IndexOf(marker);
            bool ok = at >= 0;
            if (ok)
            {
                string window = src.Substring(at, Math.Min(400, src.Length - at));
                int nextCase = window.IndexOf("case ", 6); if (nextCase > 0) window = window.Substring(0, nextCase);
                ok = !window.Contains("Print(");
            }
            Check("no Print() at '" + marker.Trim() + "'", ok, ok ? "uses Note()" : "still calls Print() on a latency path");
        }

        typeof(NinjaScriptBase).GetField("State").SetValue(st, State.Terminated); Call(st, "OnStateChange");
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    static void Call(object o, string m) { o.GetType().GetMethod(m, NP).Invoke(o, null); }
}
