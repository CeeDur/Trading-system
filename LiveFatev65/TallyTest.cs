using System; using System.Reflection; using System.Windows.Forms;

// v64o regression test: the W/L tally must not stop counting after a missed fill.
class TallyTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static int failures = 0;
    static object form; static Type T;

    static object F(string n) { return T.GetField(n, NP).GetValue(form); }
    static void SetF(string n, object v) { T.GetField(n, NP).SetValue(form, v); }
    static void Fill(int signedQty, double price)
    { T.GetMethod("TrackLiveTradeFill", NP).Invoke(form, new object[] { signedQty, price }); }
    static void Reconcile(int stratPosition)
    { T.GetMethod("ReconcileTallyPosition", NP).Invoke(form, new object[] { stratPosition }); }

    static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail);
        if (!ok) failures++;
    }
    static string Tally()
    {
        return "trades=" + F("liveTrades") + " W=" + F("liveWins") + " L=" + F("liveLosses") +
               " net=" + ((double)F("liveNetTicks")).ToString("0.##") + "t pos=" + F("fillTrackedPosition");
    }

    [STAThread]
    static void Main()
    {
        Assembly a = Assembly.LoadFrom("/tmp/v64line/LnnLiveDashboard.exe");
        T = a.GetType("LnnLiveDashboard.DashboardForm");
        form = Activator.CreateInstance(T, true);
        // the dashboard's Tick box defaults to the engine's 0.0001; MES is 0.25
        ((TextBox)T.GetField("tickSizeInput", NP).GetValue(form)).Text = "0.25";

        // ---- a normal winning round trip is counted ----
        Fill(+1, 5000.00);
        Fill(-1, 5001.00);
        Check("normal round trip counted",
              (int)F("liveTrades") == 1 && (int)F("liveWins") == 1 && Math.Abs((double)F("liveNetTicks") - 4.0) < 0.01,
              Tally() + "  (5000 -> 5001 at 0.25 tick = +4t)");

        // ---- a losing round trip is counted ----
        Fill(-1, 5001.00);
        Fill(+1, 5001.50);
        Check("losing round trip counted",
              (int)F("liveTrades") == 2 && (int)F("liveLosses") == 1,
              Tally());

        // ---- THE BUG: one missed closing fill and the tally stops forever ----
        Fill(+1, 5002.00);          // entry we saw
        // ... NT8 closed this position, but the dashboard never received the execution ...
        int beforeStuck = (int)F("liveTrades");
        Fill(+1, 5003.00);          // next entry: tracker now thinks we are LONG 2
        Fill(-1, 5004.00);          // its exit: tracker lands on 1, never zero
        Check("desync reproduced (pre-fix behaviour)",
              (int)F("liveTrades") == beforeStuck && (int)F("fillTrackedPosition") == 1,
              Tally() + "  - a completed round trip went uncounted because the counter never returned to zero");

        // ---- the fix: NT8's heartbeat says flat, the tally reconciles ----
        Reconcile(0);
        Check("reconcile counts the stranded trade",
              (int)F("liveTrades") == beforeStuck + 1 && (int)F("fillTrackedPosition") == 0,
              Tally());

        // ---- and counting works normally again afterwards ----
        int beforeResumed = (int)F("liveTrades");
        Fill(+1, 5010.00);
        Fill(-1, 5012.00);
        Check("counting resumes after reconcile",
              (int)F("liveTrades") == beforeResumed + 1 && (int)F("liveWins") >= 2,
              Tally() + "  (5010 -> 5012 = +8t)");

        // ---- adopting a position NT8 knows about that we never saw ----
        Reconcile(0);
        SetF("lastKnownFillPrice", 5020.00);
        Reconcile(-2);
        Check("adopts an unseen NT8 position",
              (int)F("fillTrackedPosition") == -2 && Math.Abs((double)F("fillAveragePrice") - 5020.00) < 0.01,
              "tracker now SHORT 2 seeded at the last known fill price " + F("fillAveragePrice"));

        int beforeAdopted = (int)F("liveTrades");
        Fill(+2, 5019.00);          // covering the adopted short: +4t on 2 lots = +8t
        Check("adopted position closes and counts",
              (int)F("liveTrades") == beforeAdopted + 1 && (int)F("fillTrackedPosition") == 0,
              Tally());

        // ---- the price guard that caused the desync is gone from the fill handler ----
        string src = System.IO.File.ReadAllText("/tmp/v64line/LnnLiveDashboard.cs");
        Check("no price guard skipping the tracker",
              !src.Contains("if (signedFill != 0 && fill.Price > 0) TrackLiveTradeFill"),
              "every execution now advances fillTrackedPosition, priced or not");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
