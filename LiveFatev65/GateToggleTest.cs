using System; using System.Collections.Generic; using System.Reflection; using System.Windows.Forms;

// v65b: each gate has its own enable box. The contract this proves:
//   1. unticking a gate sends 0 (= off) but does NOT clear the number you typed
//   2. re-ticking it brings the same number back
//   3. every gate control actually made it onto the Trading Hub panel - the refactor moved
//      controls between tabs, and a control that is declared but never added is invisible
//      and silently unreachable
//   4. the summary line tells you what can actually fire
class GateToggleTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static int failures = 0;
    static object form; static Type T;

    static object F(string n)
    {
        FieldInfo f = T.GetField(n, NP);
        if (f == null) { Console.WriteLine("FAIL  missing field " + n); failures++; return null; }
        return f.GetValue(form);
    }
    static CheckBox Box(string n) { return (CheckBox)F(n); }
    static TextBox  Txt(string n) { return (TextBox)F(n); }
    static double GateValue(string box, string field)
    { return (double)T.GetMethod("GateValue", NP).Invoke(form, new object[] { Box(box), Txt(field) }); }
    static void Refresh() { T.GetMethod("UpdateActiveGatesLabel", NP).Invoke(form, null); }
    static string Summary() { return ((Label)F("activeGatesLabel")).Text; }

    static void Check(string what, bool ok, string detail)
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail); if (!ok) failures++; }

    static void CountBoxes(Control parent, List<string> found)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is CheckBox) found.Add(((CheckBox)c).Text);
            CountBoxes(c, found);
        }
    }

    [STAThread]
    static void Main()
    {
        Assembly a = Assembly.LoadFrom("/tmp/v64line/LnnLiveDashboard.exe");
        T = a.GetType("LnnLiveDashboard.DashboardForm");
        form = Activator.CreateInstance(T, true);

        // ---- the value survives being switched off ----
        Txt("maxSpreadTicksInput").Text = "3";
        Box("gateOnSpread").Checked = false;
        Check("an unticked gate sends 0", GateValue("gateOnSpread", "maxSpreadTicksInput") == 0.0,
              "the strategy reads 0 as \"off\"");
        Check("...but the typed value is untouched", Txt("maxSpreadTicksInput").Text == "3",
              "box still reads \"" + Txt("maxSpreadTicksInput").Text + "\"");

        Box("gateOnSpread").Checked = true;
        Check("ticking it sends the number back", GateValue("gateOnSpread", "maxSpreadTicksInput") == 3.0,
              "3t, without having to retype it");
        Box("gateOnSpread").Checked = false;
        Check("round trip loses nothing", Txt("maxSpreadTicksInput").Text == "3" &&
              GateValue("gateOnSpread", "maxSpreadTicksInput") == 0.0, "off again, value kept");

        // ---- every gate reached the panel ----
        GroupBox panel = (GroupBox)F("entryGatePanel");
        List<string> onPanel = new List<string>();
        CountBoxes(panel, onPanel);
        string[] expected = {
            "Session window", "Max trades / session", "Trade gap (s)", "Post-loss cooldown (s)",
            "Flip guard (per 10 min)", "Size ramp (wins per +1)", "Dead regime (s neutral)",
            "Confidence margin", "Direction filter", "Signal age (ms)", "Max spread (ticks)",
            "Min imbalance", "Microprice", "Max flicker / s", "Max aggression into us",
            "Min book size", "Feed stale (ms)", "Daily give-back (%)"
        };
        foreach (string want in expected)
            Check("panel has \"" + want + "\"", onPanel.Contains(want),
                  onPanel.Contains(want) ? "" : "NOT on the Trading Hub panel - declared but never added");

        Check("master switch is on the panel too", onPanel.Contains("Enforce entry gates inside NT8 (works with engine-direct)"),
              "found " + onPanel.Count + " check boxes on the panel");

        // ---- the value boxes were re-parented, not duplicated ----
        Control parentOfSpread = Txt("maxSpreadTicksInput").Parent;
        bool insidePanel = false;
        for (Control c = parentOfSpread; c != null; c = c.Parent) if (ReferenceEquals(c, panel)) insidePanel = true;
        Check("value boxes live inside the gate panel", insidePanel,
              "one control, one value - the Sim tab no longer holds a second copy");

        // ---- the summary line ----
        Box("mmfGates").Checked = false; Refresh();
        Check("summary warns when enforcement is off",
              Summary().IndexOf("NOT being enforced", StringComparison.Ordinal) >= 0, "\"" + Summary() + "\"");

        Box("mmfGates").Checked = true;
        foreach (string n in new[] { "gateOnSession","gateOnMaxTrades","gateOnTradeGap","gateOnCooldown",
                                     "gateOnFlips","gateOnRamp","gateOnDeadRegime","gateOnMargin",
                                     "gateOnDirection","gateOnStaleSignal","gateOnSpread","gateOnImbalance",
                                     "gateOnFlicker","gateOnAggression","gateOnBookSize","gateOnFeedStale",
                                     "gateOnGiveBack","micropriceEntryGate" })
            Box(n).Checked = false;
        Refresh();
        Check("summary calls out enforcement with nothing ticked",
              Summary().IndexOf("no individual gate", StringComparison.Ordinal) >= 0, "\"" + Summary() + "\"");

        Box("gateOnSpread").Checked = true;
        Txt("feedStaleMsInput").Text = "2000"; Box("gateOnFeedStale").Checked = true;
        Refresh();
        Check("summary names the active gates",
              Summary().IndexOf("spread<=3t", StringComparison.Ordinal) >= 0 &&
              Summary().IndexOf("feed<=2000ms", StringComparison.Ordinal) >= 0, "\"" + Summary() + "\"");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
