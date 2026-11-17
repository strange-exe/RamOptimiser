// Sample suite that proves TestMain finds per-package test files by reflection.
// New test files copy this shape: a public static class in RamOptimiser.Tests whose name ends in "Tests",
// with public static int Run() returning its failure count and public static int Passed.
using System;

namespace RamOptimiser.Tests
{
    public static class DiscoverySampleTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            Check("discovery sample: runs once per test run", passed == 0 && failed == 0);
            return failed;
        }
    }
}
