using Anchor.Core;
using Anchor.Tests;

// Tiny test runner (no packages needed, runs anywhere .NET 8 does).
//   Anchor.Tests            -> run all tests (real-window tests only on Windows)
//   Anchor.Tests --host N   -> internal: open N test windows in this process (used by the real-window tests)

if (args.Length >= 2 && args[0] == "--host")
    return TestWindowHost.Run(int.Parse(args[1]));
if (args.Length >= 2 && args[0] == "--e2e")
    return EndToEnd.Run(args[1]);

var t = new Runner();
PureTests.All(t);
WatcherTests.All(t);
if (OperatingSystem.IsWindows()) RealWindowTests.All(t);
else Console.WriteLine("(not Windows: skipping real-window tests)");
return t.Finish();

namespace Anchor.Tests
{
    public sealed class Runner
    {
        int _pass, _fail;
        public void Test(string name, Action body)
        {
            try { body(); _pass++; Console.WriteLine($"  PASS  {name}"); }
            catch (Exception ex) { _fail++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
        }
        public int Finish()
        {
            Console.WriteLine($"\n{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }
    }

    public static class Assert
    {
        public static void True(bool c, string what) { if (!c) throw new Exception(what); }
        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what}: expected {expected}, got {actual}");
        }
        public static void Near(RectI expected, RectI actual, string what, int tol = 2)
        {
            if (!expected.Near(actual, tol)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }
}
