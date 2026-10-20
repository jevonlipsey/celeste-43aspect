using System;
using System.Collections.Generic;
using System.IO;

namespace Celeste43.Verify;

/// collects pass/fail results and prints them in a stable order. the patcher gates its output on
/// failed == 0.
public sealed class Checks
{
    private readonly List<string> failures = new();
    private readonly List<string> passes = new();
    private readonly List<string> skips = new();
    private readonly List<string> notes = new();
    private readonly TextWriter? writer;

    // lines go to the console unless a host hands in its own writer (PatchApi captures them)
    private TextWriter Out => writer ?? Console.Out;

    public Checks(TextWriter? writer = null) => this.writer = writer;

    public int Passed => passes.Count;
    public int Failed => failures.Count;
    public int Skipped => skips.Count;
    public IReadOnlyList<string> Failures => failures;

    public string Summary => FormatSummary(Passed, Failed, Skipped);

    public static string FormatSummary(int passed, int failed, int skipped) =>
        $"checks: {passed} passed, {failed} failed, {skipped} skipped";

    public void Pass(string message)
    {
        passes.Add(message);
        Out.WriteLine($"  [pass] {message}");
    }

    public void Fail(string message)
    {
        failures.Add(message);
        Out.WriteLine($"  [FAIL] {message}");
    }

    /// a check that could not run in this mode (needs assemblies the host does not have).
    /// counted apart from pass and fail so a reference-free run never looks fully verified.
    public void Skip(string message)
    {
        skips.Add(message);
        Out.WriteLine($"  [skip] {message}");
    }

    public void Check(bool condition, string message)
    {
        if (condition) Pass(message);
        else Fail(message);
    }

    public void Note(string message)
    {
        notes.Add(message);
        Out.WriteLine($"  [note] {message}");
    }

    public void Section(string name)
    {
        Out.WriteLine();
        Out.WriteLine($"=== {name} ===");
    }

    public void Summarize()
    {
        Out.WriteLine();
        Out.WriteLine("==================================================");
        Out.WriteLine($"  {Summary}");
        if (failures.Count > 0)
        {
            Out.WriteLine("  failures:");
            foreach (var f in failures) Out.WriteLine($"    - {f}");
        }
        Out.WriteLine("==================================================");
    }
}
