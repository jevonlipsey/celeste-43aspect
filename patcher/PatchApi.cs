using System.Diagnostics;
using System.Security.Cryptography;
using Celeste43;
using Celeste43.Verify;
using Mono.Cecil;

/// result of one PatchApi.Patch call. Output is non-null only when Reason is "ok", which also
/// means the verifier ran on exactly these bytes and Failed is 0.
public sealed class PatchResult
{
    /// the patched Celeste.exe, or null when nothing should be written
    public byte[]? Output { get; init; }
    /// "ok" | "already_patched" | "not_celeste" | "everest_installed" | "unknown_build" | "verify_failed" | "error"
    public string Reason { get; init; } = "error";
    /// the patcher's progress log (what the cli prints)
    public string Log { get; init; } = "";
    /// every line the verifier printed, one per check
    public string VerifyLog { get; init; } = "";
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    /// verifier failures, or for unknown_build one line per mismatched method
    public string[] Failures { get; init; } = Array.Empty<string>();
    public string Version { get; init; } = PatchApi.Version;
    public long PatchMillis { get; init; }
    public long VerifyMillis { get; init; }
    /// sha256 of Output as lowercase hex, or null
    public string? Sha256 { get; init; }

    public bool Ok => Reason == "ok";
    public string Summary => Checks.FormatSummary(Passed, Failed, Skipped);
}

namespace Celeste43
{
    /// in-memory entry point for the patcher: bytes in, bytes out, verified. used by the cli and
    /// by the browser (wasm) host, so it does no file or console i/o of its own beyond the log.
    public static class PatchApi
    {
        public const string Version = "v1.0.0";

        // the patcher keeps its working refs in statics and the log goes through Console, so
        // one patch at a time
        private static readonly object Gate = new();

        /// patches a vanilla Celeste.exe (1.4.0.0, FNA) and verifies the result before returning it
        public static PatchResult Patch(byte[] input)
        {
            lock (Gate)
            {
                var log = new StringWriter();
                var previous = Console.Out;
                Console.SetOut(log);
                try
                {
                    return PatchCore(input, log);
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
                    log.WriteLine(ex.StackTrace);
                    return Fail("error", log);
                }
                finally
                {
                    Console.SetOut(previous);
                }
            }
        }

        private static PatchResult PatchCore(byte[] input, StringWriter log)
        {
            if (input == null || input.Length == 0)
            {
                log.WriteLine("[!] input is empty");
                return Fail("error", log);
            }

            log.WriteLine($"[+] patching Celeste.exe ({input.Length} bytes)");
            var clock = Stopwatch.StartNew();

            AssemblyDefinition asm;
            try
            {
                asm = AssemblyDefinition.ReadAssembly(new MemoryStream(input),
                    new ReaderParameters { AssemblyResolver = new NullResolver() });
            }
            catch (Exception ex)
            {
                log.WriteLine($"[!] input is not a readable .net assembly ({ex.GetType().Name}: {ex.Message})");
                return Fail("not_celeste", log);
            }

            byte[] patched;
            using (asm)
            {
                var module = asm.MainModule;
                if (module.GetType(CelesteSpec.EverestType) != null)
                {
                    log.WriteLine("[!] this Celeste.exe has Everest installed (Celeste.Mod.Everest). use the CelesteFit mod instead, or patch a vanilla exe");
                    return Fail("everest_installed", log);
                }
                if (module.GetType(CelesteSpec.HelperType) != null || module.GetType(CelesteSpec.StaleType) != null)
                {
                    log.WriteLine("[!] this Celeste.exe is already patched (it contains Fit43 / ScreenFit). patch the original Celeste.exe");
                    return Fail("already_patched", log);
                }
                if (module.GetType("Celeste.Celeste") == null || module.GetType("Monocle.Engine") == null
                    || module.GetType("Celeste.GameplayRenderer") == null)
                {
                    log.WriteLine("[!] this is not Celeste.exe (no Celeste.Celeste / Monocle.Engine / Celeste.GameplayRenderer)");
                    return Fail("not_celeste", log);
                }

                var mismatches = new List<string>();
                patched = Program.PatchAssembly(asm, mismatches);
                if (mismatches.Count > 0)
                {
                    log.WriteLine($"[!] unknown build: {mismatches.Count} method(s) do not match a vanilla Celeste 1.4.0.0 FNA exe");
                    foreach (var m in mismatches) log.WriteLine($"    - {m}");
                    return new PatchResult { Reason = "unknown_build", Log = log.ToString(), Failures = mismatches.ToArray() };
                }
            }
            long patchMs = clock.ElapsedMilliseconds;

            // gate on the verifier, run on the bytes we are about to hand out
            clock.Restart();
            var verifyLog = new StringWriter();
            var checks = new Checks(verifyLog);
            using (var ms = new MemoryStream(patched))
            using (var module = ModuleDefinition.ReadModule(ms,
                       new ReaderParameters { AssemblyResolver = new NullResolver() }))
            {
                VerifyRunner.Run(checks, module, Program.ReadHelperAssembly());
            }
            checks.Summarize();
            long verifyMs = clock.ElapsedMilliseconds;

            bool ok = checks.Failed == 0;
            if (!ok) log.WriteLine($"[!] verification failed ({checks.Failed} check(s)); no output");
            return new PatchResult
            {
                Output = ok ? patched : null,
                Reason = ok ? "ok" : "verify_failed",
                Log = log.ToString(),
                VerifyLog = verifyLog.ToString(),
                Passed = checks.Passed,
                Failed = checks.Failed,
                Skipped = checks.Skipped,
                Failures = checks.Failures.ToArray(),
                PatchMillis = patchMs,
                VerifyMillis = verifyMs,
                Sha256 = ok ? Convert.ToHexString(SHA256.HashData(patched)).ToLowerInvariant() : null,
            };
        }

        private static PatchResult Fail(string reason, StringWriter log) =>
            new() { Reason = reason, Log = log.ToString() };
    }
}
