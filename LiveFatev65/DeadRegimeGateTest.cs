using System; using System.Globalization; using System.Reflection; using System.Windows.Forms;

// v64x: the dead-zone rule is now an ENTRY GATE with hysteresis, not an auto-disarm.
// It must block after sustained neutrality, stay armed, and only re-open once the model
// has HELD a directional read - so a single spike out of a flat regime is not traded.
class DeadRegimeGateTest
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static int failures = 0;
    static object form; static Type T;

    static object F(string n) { return T.GetField(n, NP).GetValue(form); }
    static void SetF(string n, object v) { T.GetField(n, NP).SetValue(form, v); }
    static void Feed(double p)
    { T.GetMethod("TrackProbabilityRegime", NP).Invoke(form, new object[] { p.ToString("R", CultureInfo.InvariantCulture) }); }
    static bool Blocked() { return (bool)F("neutralRegimeBlock"); }
    static string GateReason()
    { return (string)T.GetMethod("CurrentEntryGateReason", NP).Invoke(form, null); }

    static void Check(string what, bool ok, string detail)
    { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + "   " + detail); if (!ok) failures++; }

    [STAThread]
    static void Main()
    {
        Assembly a = Assembly.LoadFrom("/tmp/v64line/LnnLiveDashboard.exe");
        T = a.GetType("LnnLiveDashboard.DashboardForm");
        form = Activator.CreateInstance(T, true);
        // 1-second block window so the test does not have to wait two minutes
        ((TextBox)T.GetField("neutralBlockSecondsInput", NP).GetValue(form)).Text = "1";
        SetF("tradeArmed", true);

        Feed(0.50);
        Check("not blocked immediately", !Blocked(),
              "one neutral reading must not block - it takes sustained neutrality");

        System.Threading.Thread.Sleep(1200);
        Feed(0.50);
        Check("blocks after sustained neutrality", Blocked(),
              "1s of dead-zone readings -> entries blocked");

        Check("stays ARMED", (bool)F("tradeArmed"),
              "the old behaviour disarmed here; the trader must stay armed so exits and brackets still work");

        Check("entry gate says why", GateReason().IndexOf("dead-regime", StringComparison.OrdinalIgnoreCase) >= 0,
              "\"" + GateReason() + "\"");

        // A single spike past the threshold must NOT re-open the gate - that is the
        // whole point of the hysteresis.
        Feed(0.72);
        Check("one directional spike does not re-open", Blocked(),
              "p jumped to 0.72 but conviction has not been held for 3 s yet");

        // Held conviction re-opens it.
        DateTime until = DateTime.UtcNow.AddSeconds(3.4);
        while (DateTime.UtcNow < until) { Feed(0.72); System.Threading.Thread.Sleep(100); }
        Check("held conviction re-opens", !Blocked(),
              "3 s of readings outside the dead zone -> entries re-enabled");

        // Falling back into the dead zone restarts the clock, and one neutral tick
        // must not immediately re-block.
        Feed(0.50);
        Check("re-entering the dead zone does not block instantly", !Blocked(),
              "the block timer restarts from zero");

        // 0 turns it off entirely.
        ((TextBox)T.GetField("neutralBlockSecondsInput", NP).GetValue(form)).Text = "0";
        System.Threading.Thread.Sleep(1200);
        for (int i = 0; i < 20; i++) Feed(0.50);
        Check("0 disables the gate", !Blocked(), "never blocks with the field at 0");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
