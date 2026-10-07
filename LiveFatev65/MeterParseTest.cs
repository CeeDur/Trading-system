using System; using System.Reflection; using System.Text.RegularExpressions; using System.IO;
class RegexCheck
{
    static string Grab(string path)
    {
        string last = null;
        foreach (string l in File.ReadAllLines(path))
            if (l.StartsWith("twotick ") && l.Contains("skill-vs-base")) last = l;
        return last;
    }
    static void Main()
    {
        Assembly a = Assembly.LoadFrom("/tmp/v64line/LnnLiveDashboard.exe");
        Type t = a.GetType("LnnLiveDashboard.DashboardForm");
        Regex re = (Regex)t.GetField("TwoTickMetrics", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        string dir = "/tmp/claude-0/-home-claude/6b308920-910e-583c-8694-c20595a00169/scratchpad/";
        string[] samples = { Grab(dir + "m_sym.log"), Grab(dir + "m_asym.log") };
        int fail = 0;
        foreach (string s in samples)
        {
            if (string.IsNullOrEmpty(s)) { Console.WriteLine("FAIL  no sample line"); fail++; continue; }
            Match m = re.Match(s);
            bool ok = m.Success && m.Groups["realized"].Success;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") +
                "target=" + m.Groups["target"].Value +
                "  stop=" + (m.Groups["stop"].Success ? m.Groups["stop"].Value : "(symmetric)") +
                "  untradeable=" + (m.Groups["untradeable"].Success ? m.Groups["untradeable"].Value : "(none)") +
                "  skill=" + m.Groups["skill"].Value + "%");
            if (!ok) fail++;
        }
        Console.WriteLine(fail == 0 ? "REGEX OK - the dashboard parses both geometries" : fail + " FAILED");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
