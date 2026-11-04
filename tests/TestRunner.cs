using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Clawdock.Tests
{
    // A test is any public static void method without parameters in a class named *Tests.
    // Pass words on the command line to run only the tests whose names contain one of them.
    // Tests must leave the real machine alone: no ~/.claude, no settings file, no Claude app.
    static class TestRunner
    {
        static int Main(string[] filters)
        {
            var tests = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.Namespace == "Clawdock.Tests" && t.Name.EndsWith("Tests", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 0))
                .Where(m => filters.Length == 0 || filters.Any(f => Name(m).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(Name).ToList();

            var clock = Stopwatch.StartNew();
            int failed = 0;
            foreach (var test in tests)
            {
                try { test.Invoke(null, null); }
                catch (TargetInvocationException e)
                {
                    failed++;
                    Console.WriteLine("FAIL " + Name(test) + "\n     " + e.InnerException.Message.Replace("\n", "\n     "));
                }
            }
            Console.WriteLine("{0} tests, {1} failed, in {2:0.0} s", tests.Count, failed, clock.Elapsed.TotalSeconds);
            return failed == 0 && tests.Count > 0 ? 0 : 1;
        }

        static string Name(MethodInfo m) { return m.DeclaringType.Name + "." + m.Name; }
    }

    static class Check
    {
        public static void Equal(object expected, object actual, string what = "")
        {
            if (!object.Equals(expected, actual))
                throw new Exception((what == "" ? "" : what + ": ") + "expected [" + expected + "] but got [" + actual + "]");
        }

        public static void That(bool condition, string what) { if (!condition) throw new Exception(what); }

        public static void Near(double expected, double actual, string what = "")
        {
            if (Math.Abs(expected - actual) > 0.0001) Equal(expected, actual, what);
        }

        public static void Has(string text, string part)
        {
            if ((text ?? "").IndexOf(part, StringComparison.Ordinal) < 0) throw new Exception("expected [" + part + "] in [" + text + "]");
        }

        public static void HasNot(string text, string part)
        {
            if ((text ?? "").IndexOf(part, StringComparison.Ordinal) >= 0) throw new Exception("did not expect [" + part + "] in [" + text + "]");
        }

        // JSON written with single quotes, so tests stay readable.
        public static Dictionary<string, object> Obj(string json) { return Json.ParseObject(json.Replace('\'', '"')); }
    }
}
