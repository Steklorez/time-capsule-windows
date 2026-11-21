using System.Diagnostics;
using System.Text;
using TimeCapsuleDrive.Afp;

namespace TimeCapsuleDrive;

/// <summary>
/// Checks, against the real server, the behaviours the drive's safety depends on:
/// write protection between clients, durable flush, session take-over after a cut connection,
/// and clean-up of files left open by a crashed run.
/// Works in a scratch folder (\tmp\_tcd_selftest) and removes it afterwards.
/// </summary>
static class SelfTest
{
    const string Dir = "\\tmp\\_tcd_selftest";

    public static int Run(Config cfg, Action<string> log, bool waitForZombie)
    {
        AfpClient New(string? id) => new(cfg.Host, cfg.Port, cfg.Volume, cfg.Login, cfg.Password, id, log);
        int failed = 0;
        void Report(string what, bool ok, string detail = "")
        {
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        }
        int? OpenCode(AfpClient c, string path, bool write, out AfpFork? fork)
        {
            fork = null;
            try { fork = c.OpenFork(path, write); return null; }
            catch (AfpException e) { return e.Code; }
        }

        Console.WriteLine(AfpClient.Probe(cfg.Host, cfg.Port));
        using var b = New(null);
        if (b.Stat("\\tmp") == null) b.CreateDir("\\tmp");
        if (b.Stat(Dir) != null) Wipe(b);
        b.CreateDir(Dir);
        var data = Encoding.ASCII.GetBytes("hello from selftest");
        var buf = new byte[64];

        // 1. write protection between two clients
        string f1 = Dir + "\\deny.txt";
        b.CreateFile(f1);
        using (var a = New(null))
        {
            var wa = a.OpenFork(f1, true);
            a.Write(wa, 0, data, data.Length);
            int? code = OpenCode(b, f1, true, out var wb);
            Report("second writer is refused while the first has the file open", code == AfpError.DenyConflict, "code " + code);
            if (wb != null) b.CloseFork(wb);
            code = OpenCode(b, f1, false, out var rb);
            Report("a reader is still allowed", code == null, "code " + code);
            if (rb != null)
            {
                int n = b.EndRead(b.BeginRead(rb, 0, 64, buf));
                Report("reader sees the written data", n == data.Length && buf.AsSpan(0, n).SequenceEqual(data), n + " bytes");
                b.CloseFork(rb);
            }
            // 2. durable flush
            bool full = a.Sync(wa);
            Report("server supports a real write-to-disk-now request (FPSyncFork)", full);
            a.CloseFork(wa);
            code = OpenCode(b, f1, true, out wb);
            Report("writer is admitted after the first one closed", code == null, "code " + code);
            if (wb != null) b.CloseFork(wb);
        }

        // 3. session take-over after a cut connection
        string f2 = Dir + "\\takeover.txt";
        b.CreateFile(f2);
        using (var a = New("selftest-takeover-" + Environment.ProcessId))
        {
            var wa = a.OpenFork(f2, true);
            a.Write(wa, 0, data, data.Length);
            a.DebugCutConnection();
            Thread.Sleep(500);
            int n = -1; string err = "";
            try { n = a.EndRead(a.BeginRead(wa, 0, 64, buf)); } catch (Exception e) { err = e.Message; }
            Report("open file keeps working after the connection was cut", n == data.Length, err.Length > 0 ? err : n + " bytes");
            // ...including writing, several times in a row
            string werr = "";
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    a.DebugCutConnection();
                    a.Write(wa, 0, data, data.Length);
                }
            }
            catch (Exception e) { werr = e.Message; }
            Report("writing continues across repeated connection cuts", werr.Length == 0, werr);
            int? code = OpenCode(b, f2, true, out var wb);
            Report("...and it is still protected from other writers", code == AfpError.DenyConflict, "code " + code);
            if (wb != null) b.CloseFork(wb);
            a.CloseFork(wa);
        }

        // 4. files left open by a crashed run of the same installation
        string f3 = Dir + "\\crash.txt", id = "selftest-crash-" + Environment.ProcessId;
        b.CreateFile(f3);
        var dead = New(id);
        dead.OpenFork(f3, true);
        dead.DebugCutConnection(); // "crash": never says goodbye, never comes back
        Thread.Sleep(1500);
        int? stale = OpenCode(b, f3, true, out var w3);
        if (w3 != null) b.CloseFork(w3);
        Console.WriteLine($"INFO  right after the crash the file is {(stale == AfpError.DenyConflict ? "still locked by the dead session" : "free (code " + stale + ")")}");
        using (var restarted = New(id))
        {
            restarted.Stat("\\"); // logs in and announces itself with a new start time
            int? code = OpenCode(b, f3, true, out w3);
            Report("restart of the same installation frees what its crashed run left open", code == null, "code " + code);
            if (w3 != null) b.CloseFork(w3);
        }

        // 5. how long does a dead session without an id keep a file locked?
        string f4 = Dir + "\\zombie.txt";
        b.CreateFile(f4);
        var anon = New(null);
        anon.OpenFork(f4, true);
        anon.DebugCutConnection();
        var sw = Stopwatch.StartNew();
        int? z = AfpError.DenyConflict;
        int limit = waitForZombie ? 400 : 3;
        while (true)
        {
            z = OpenCode(b, f4, true, out var w4);
            if (w4 != null) b.CloseFork(w4);
            if (z != AfpError.DenyConflict || sw.Elapsed.TotalSeconds > limit) break;
            Thread.Sleep(5000);
        }
        Console.WriteLine($"INFO  a dead session without an id kept its file locked for {(z == AfpError.DenyConflict ? "more than " : "")}{sw.Elapsed.TotalSeconds:F0} s (last code {z})");

        Wipe(b);
        Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        return failed == 0 ? 0 : 1;

        void Wipe(AfpClient c)
        {
            foreach (var e in c.List(Dir))
                try { c.Delete(Dir + "\\" + e.Name); } catch (AfpException ex) { Console.WriteLine("INFO  cannot delete " + e.Name + ": " + ex.Message); }
            try { c.Delete(Dir); } catch (AfpException) { }
        }
    }
}
