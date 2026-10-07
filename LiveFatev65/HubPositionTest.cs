using System; using System.Reflection; using System.Windows.Forms; using System.Runtime.InteropServices;

// v64q regression test: the Trading Hub's "Position:" line must reflect an open position
// that the dashboard learned from NT8's heartbeat, not only one it saw a fill record for.
class HubPositionTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static int failures = 0;
    static object form; static Type T;

    static object F(string n) { return T.GetField(n, NP).GetValue(form); }
    static void SetF(string n, object v) { T.GetField(n, NP).SetValue(form, v); }
    static string HubText() { return ((Label)T.GetField("tradePosition", NP).GetValue(form)).Text; }
    static string SimText() { return ((Label)T.GetField("positionLabel", NP).GetValue(form)).Text; }

    static void Check(string what, bool ok, string detail)
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail); if (!ok) failures++; }

    // Build the heartbeat record the strategy publishes once a second (IsExecution = 4):
    // Qty = signed NT8 position, FilledQty = flags | workingEntries << 8.
    static object Heartbeat(int signedPosition, int workingEntries)
    {
        Type payload = T.Assembly.GetType("LnnLiveDashboard.DashboardForm+TradeFillPayload")
                    ?? T.GetNestedType("TradeFillPayload", BindingFlags.NonPublic);
        object hb = Activator.CreateInstance(payload);
        payload.GetField("Qty").SetValue(hb, signedPosition);
        payload.GetField("FilledQty").SetValue(hb, 1 | (workingEntries << 8));
        payload.GetField("IsExecution").SetValue(hb, (byte)4);
        payload.GetField("Price").SetValue(hb, 0.0);
        return hb;
    }

    [STAThread]
    static void Main()
    {
        Assembly a = Assembly.LoadFrom("/tmp/v64line/LnnLiveDashboard.exe");
        T = a.GetType("LnnLiveDashboard.DashboardForm");
        form = Activator.CreateInstance(T, true);
        ((TextBox)T.GetField("tickSizeInput", NP).GetValue(form)).Text = "0.25";

        MethodInfo status = T.GetMethod("ProcessStrategyStatus", NP);

        Check("hub starts unknown", HubText().IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0,
              "\"" + HubText() + "\"");

        // NT8 reports LONG 1 with nothing working - exactly what an engine-direct fill looks
        // like to a dashboard that never saw the execution record. Three beats to pass the
        // resync's debounce.
        object hb = Heartbeat(1, 0);
        for (int i = 0; i < 3; i++) status.Invoke(form, new object[] { hb });

        Console.WriteLine("    sim tab : " + SimText());
        Console.WriteLine("    hub tab : " + HubText());

        Check("hub shows the position from the heartbeat",
              HubText().IndexOf("LONG", StringComparison.Ordinal) >= 0,
              "hub line is now \"" + HubText() + "\" (before v64q it stayed on \"unknown (waiting for fills)\")");

        Check("hub says where the position came from",
              HubText().IndexOf("heartbeat", StringComparison.OrdinalIgnoreCase) >= 0,
              "the line is marked as heartbeat-sourced, so a missing fill record is visible rather than silent");

        Check("sim tab agrees with the hub",
              SimText().IndexOf("LONG", StringComparison.Ordinal) >= 0,
              "both pages now say LONG - they used to disagree, which is the bug that was reported");

        // Now NT8 goes flat; the hub must follow.
        object flat = Heartbeat(0, 0);
        for (int i = 0; i < 3; i++) status.Invoke(form, new object[] { flat });
        Check("hub follows NT8 back to flat",
              HubText().IndexOf("FLAT", StringComparison.Ordinal) >= 0,
              "\"" + HubText() + "\"");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
