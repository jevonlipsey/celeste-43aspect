using System.Reflection;
using Celeste43.Verify;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Celeste43;

/// static port of Source/CelesteFitModule.cs: every ILHook becomes an in-place rewrite of a
/// vanilla Celeste.exe, and the runtime half (Fit43) is cloned into the exe. nothing here resolves
/// an assembly, so the same code runs in the browser.
public static class Program
{
    // exit codes: 0 patched and verified, 1 verify failed or error, 3 already patched,
    // 4 not Celeste, 5 Everest installed, 6 unknown build
    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        Console.WriteLine($"Celeste 4:3 patcher {PatchApi.Version}");
        if (args.Length == 2 && args[0] == "--hits")
            return DumpHits(args[1]);
        if (args.Length == 2 && args[0] == "--verify")
            return VerifyFile(args[1]);
        if (args.Length != 2)
        {
            Console.WriteLine("usage: patcher <in Celeste.exe> <out Celeste.exe>");
            Console.WriteLine("       patcher --hits <in Celeste.exe>     print the Fit43 call counts (dev)");
            Console.WriteLine("       patcher --verify <patched exe>      run the verifier alone");
            return 2;
        }
        if (!File.Exists(args[0]))
        {
            Console.Error.WriteLine($"[!] cannot find {args[0]}");
            return 1;
        }

        var result = PatchApi.Patch(File.ReadAllBytes(args[0]));
        Console.Write(result.Log);
        if (result.Passed + result.Failed + result.Skipped > 0)
        {
            Console.WriteLine(result.Summary);
            foreach (var f in result.Failures) Console.WriteLine($"    - {f}");
        }
        if (result.Output == null)
        {
            Console.Error.WriteLine($"[!] nothing written: {result.Reason}");
            return result.Reason switch
            {
                "already_patched" => 3,
                "not_celeste" => 4,
                "everest_installed" => 5,
                "unknown_build" => 6,
                _ => 1,
            };
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1]));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(args[1], result.Output);
        Console.WriteLine($"[+] saved {args[1]}");
        Console.WriteLine($"[+] sha256 {result.Sha256}");
        return 0;
    }

    // dev: patch in memory and print the hit table as c# for CelesteSpec.ExpectedHits
    static int DumpHits(string path)
    {
        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(path)),
            new ReaderParameters { AssemblyResolver = new NullResolver() });
        PatchAssembly(asm, new List<string>());
        foreach (var kv in CelesteChecks.CountHits(asm.MainModule))
            Console.WriteLine($"        [\"{kv.Key}\"] = {kv.Value},");
        return 0;
    }

    static int VerifyFile(string path)
    {
        var checks = new Checks();
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = new NullResolver() });
        VerifyRunner.Run(checks, module, ReadHelperAssembly());
        checks.Summarize();
        return checks.Failed == 0 ? 0 : 1;
    }

    // ---- the embedded helper ----

    /// the Fit43 helper ships inside the patcher binary as an embedded resource
    internal static byte[]? ReadHelperAssembly()
    {
        var asm = typeof(Program).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("Fit43.dll", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) continue;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        return null;
    }

    // ---- the patch ----

    /// runs every rewrite on an open module and returns the written bytes. mismatches gets one
    /// line per method whose Fit43 call count differs from CelesteSpec.ExpectedHits; the bytes
    /// are only meaningful when it stays empty. i/o stays with the caller.
    internal static byte[] PatchAssembly(AssemblyDefinition asm, List<string> mismatches)
    {
        var rw = new Rewriter(asm.MainModule);
        rw.Run();
        mismatches.AddRange(CelesteChecks.Diff(CelesteChecks.CountHits(asm.MainModule), CelesteSpec.ExpectedHits));

        using var ms = new MemoryStream();
        asm.Write(ms);
        var bytes = ms.ToArray();
        VerifyBranches(bytes);
        Console.WriteLine($"[+] wrote patched assembly ({bytes.Length} bytes)");
        return bytes;
    }

    /// re-reads what we just wrote. a branch whose target does not land on an instruction
    /// boundary comes back with a null operand, so this catches a corrupt body cheaply.
    static void VerifyBranches(byte[] assemblyBytes)
    {
        var broken = new List<string>();
        using (var ms = new MemoryStream(assemblyBytes))
        using (var asm = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { AssemblyResolver = new NullResolver() }))
        {
            void Walk(TypeDefinition type)
            {
                foreach (var n in type.NestedTypes) Walk(n);
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.OpCode.OperandType != OperandType.ShortInlineBrTarget &&
                            ins.OpCode.OperandType != OperandType.InlineBrTarget) continue;
                        if (ins.Operand == null)
                            broken.Add($"{type.Name}::{method.Name}@{ins.Offset:x4}");
                    }
                }
            }
            foreach (var type in asm.MainModule.Types) Walk(type);
        }

        if (broken.Count > 0)
            throw new InvalidOperationException(
                $"patched IL is corrupt: {broken.Count} branch(es) point into the middle of an instruction ({string.Join(", ", broken.Take(8))})");
        Console.WriteLine("    [OK] every branch target in the output still resolves");
    }

    // ---- the rewriter ----

    sealed class Rewriter
    {
        readonly ModuleDefinition module;
        TypeDefinition fit = null!;
        readonly HashSet<MethodDefinition> touched = new();
        MethodReference cameraTop = null!;

        public Rewriter(ModuleDefinition module) { this.module = module; }

        MethodDefinition H(string name) => fit.Methods.First(m => m.Name == name);

        public void Run()
        {
            fit = InjectHelper(module);
            var camera = module.GetType("Monocle.Camera");
            cameraTop = camera.Methods.First(m => m.Name == "get_Top" && m.Parameters.Count == 0);

            // the order below is the mod's registration order: the hooks of one method stack up in
            // it, and a few of them (the cover rects before the 182 swap) depend on that

            // 1. Engine.UpdateView
            Step("engine.UpdateView");
            Hook("Monocle.Engine", "UpdateView", m => Prefix(m, Ldarg0(), Call("UpdateViewPrefix")));

            // 3. GameplayRenderer constructor (camera viewport height)
            Step("gameplayrenderer ctor");
            Hook("Celeste.GameplayRenderer", ".ctor", m => Replace180(m));

            // 4. Parallax.Render. the Everest detour and the PointClamp change are motion smoothing
            // workarounds and have no vanilla counterpart
            Step("parallax.Render");
            Hook("Celeste.Parallax", "Render", PatchParallaxRender);

            // 4b. full-screen cover rects
            Step("screen fade rects");
            foreach (var (type, method) in CelesteSpec.CoverMethods)
                Hook("Celeste." + type, method, PatchScreenFadeRects);

            // 4d. bottom anchored hud
            Step("bottom anchored hud");
            Hook("Celeste.ReturnMapHint", "Render", m => ReanchorToBottom(m, 730f, 350f));
            Hook("Celeste.QuickResetHint", "Render", m => ReanchorToBottom(m, 980f, 100f));
            Hook("Celeste.SaveLoadIcon", "Render", m => ReanchorToBottom(m, 920f, 160f));

            // 5. MountainRenderer.Render
            Step("mountainrenderer.Render");
            Hook("Celeste.MountainRenderer", "Render", PatchMountainRender);

            // 6. DustEdges
            Step("dustedges");
            Hook("Celeste.DustEdges", "BeforeRender", PatchDustEdgesBeforeRender);
            Hook("Celeste.DustEdges", "Render", PatchDustEdgesRender);

            // 7. wipes
            Step("wipes");
            Hook("Celeste.ScreenWipe", "get_Bottom", m => Constant(m, 1090f, SH(10)));
            Hook("Celeste.KeyDoorWipe", "Render", m => Constant(m, 1090f, SH(10)));
            Hook("Celeste.CurtainWipe", "Render", m => Constant(m, 1090f, SH(10)));
            Hook("Celeste.AngledWipe", "Render", m => Constant(m, 183.33333f, (f) => Expr(false, f, (OpCodes.Add, 20f), (OpCodes.Div, 6f))));
            Hook("Celeste.FallWipe", "Render", m => Constant(m, 2160f, (f) => Expr(false, f, (OpCodes.Mul, 2f))));
            Hook("Celeste.OuiTitleScreen", "Render", m => Constant(m, 1100f, SH(20)));

            // 7b. level flash and zoom anchor
            Step("level flash");
            Hook("Celeste.Level", "Render", m => Constant(m, 182f, (f) => Expr(true, f, (OpCodes.Add, 2f))));
            Hook("Celeste.Level", "Render", m => Constant(m, 180f, GH));

            // 7c. lighting and wall culls
            Step("lighting/wall culls");
            Hook("Celeste.LightingRenderer", "BeforeRender", m => Constant(m, 180f, GH));
            Hook("Celeste.FakeWall", "Render", m => Constant(m, 180f, GH));
            Hook("Celeste.ExitBlock", "Render", m => Constant(m, 180f, GH));

            // 7e. target center, bloom, bounds, zoom
            Step("target center + bloom + bounds");
            Hook("Celeste.Celeste", "get_TargetCenter", m => Constant(m, 1080f, SH(0)));
            Hook("Celeste.LightningRenderer", "OnRenderBloom", m => Constant(m, 180f, GH));
            Hook("Celeste.Level", "EnforceBounds", m => Constant(m, 180f, GH));
            Hook("Celeste.Level", "ResetZoom", m => Constant(m, 180f, GH));
            Hook("Celeste.Level", "ScreenToWorld", PatchScreenPaddingAspect);
            Hook("Celeste.Level", "WorldToScreen", PatchScreenPaddingAspect);
            Hook("Celeste.Level", "Render", PatchScreenPaddingAspect);
            Hook("Celeste.HiresRenderer", "get_DrawToBuffer", m => Constant(m, 1080f, SH(0)));

            // 8. culling
            Step("culling");
            Hook("Celeste.Level", "IsInCamera", PatchCulling);
            Hook("Celeste.Lightning", "InView", PatchCulling);
            Hook("Celeste.CrystalStaticSpinner", "InView", PatchCulling);

            // 9. low and ui types
            Step("low types");
            PatchLowTypes();
            Step("ui types");
            PatchUiTypes();

            // 10. hires ui buffers
            Step("ui buffers");
            Hook("Celeste.Celeste", "LoadContent", PatchHudBufferHeight);

            foreach (var m in touched) ExpandShortBranches(m);
            Console.WriteLine($"[+] rewrote {touched.Count} methods");
        }

        static void Step(string what) => Console.WriteLine($"--- {what}");

        // ---- finding and editing ----

        TypeDefinition? FindType(string fullName)
        {
            var t = module.GetType(fullName);
            if (t == null) Console.WriteLine($"    [skip] type {fullName} not found");
            return t;
        }

        /// applies fn to every method of the type with that name (all overloads; the mod's
        /// name-based hook only ever reached the first). .ctor is every instance ctor, .cctor the
        /// type initializer.
        void Hook(string typeName, string methodName, Action<MethodDefinition> fn)
        {
            var type = FindType(typeName);
            if (type == null) return;
            HookIn(type, methodName, fn);
        }

        void HookIn(TypeDefinition type, string methodName, Action<MethodDefinition> fn)
        {
            bool any = false;
            foreach (var m in type.Methods.Where(m => m.Name == methodName && m.HasBody).ToList())
            {
                any = true;
                fn(m);
            }
            if (!any) Console.WriteLine($"    [skip] {type.FullName}::{methodName} not found");
        }

        void Touch(MethodDefinition m) => touched.Add(m);

        // swaps the instruction in place (branches and handlers that pointed at it still do) and
        // inserts the rest of the sequence after it. returns the last instruction of the sequence
        Instruction Swap(MethodDefinition m, Instruction ins, Instruction[] seq)
        {
            var il = m.Body.GetILProcessor();
            ins.OpCode = seq[0].OpCode;
            ins.Operand = seq[0].Operand;
            var cur = ins;
            for (int k = 1; k < seq.Length; k++)
            {
                il.InsertAfter(cur, seq[k]);
                cur = seq[k];
            }
            Touch(m);
            return cur;
        }

        // walks the body once; rule returns the replacement sequence for an instruction or null
        void Scan(MethodDefinition m, Func<Instruction, Instruction[]?> rule)
        {
            var ins = m.Body.Instructions.FirstOrDefault();
            while (ins != null)
            {
                var seq = rule(ins);
                ins = seq == null ? ins.Next : Swap(m, ins, seq).Next;
            }
        }

        void Prefix(MethodDefinition m, params Instruction[] seq)
        {
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            foreach (var i in seq) il.InsertBefore(first, i);
            Touch(m);
        }

        // ---- instruction builders ----

        Instruction Ldarg0() => Instruction.Create(OpCodes.Ldarg_0);
        Instruction Call(string helper) => Instruction.Create(OpCodes.Call, H(helper));
        static Instruction I4(int v) => Instruction.Create(OpCodes.Ldc_I4, v);
        static Instruction R4(float v) => Instruction.Create(OpCodes.Ldc_R4, v);

        /// the height-based expressions, one Fit43 call each. gh picks GameHeight over
        /// Engine.Height; isFloat follows the kind of the constant being replaced, so the stack
        /// type stays what the surrounding code expects.
        Instruction[] Expr(bool gh, bool isFloat, params (OpCode op, float k)[] ops)
        {
            var seq = new List<Instruction> { Call(gh ? "get_GameHeight" : "get_ScreenHeight") };
            if (isFloat) seq.Add(Instruction.Create(OpCodes.Conv_R4));
            foreach (var (op, k) in ops)
            {
                seq.Add(isFloat ? R4(k) : I4((int)k));
                seq.Add(Instruction.Create(op));
            }
            return seq.ToArray();
        }

        Func<bool, Instruction[]> GH => f => Expr(true, f);
        Func<bool, Instruction[]> SH(int add) => f => add == 0 ? Expr(false, f) : Expr(false, f, (OpCodes.Add, (float)add));

        static bool IsI4(Instruction i, int v) => i.OpCode.Code == Code.Ldc_I4 && (int)i.Operand == v;
        static bool IsR4(Instruction i, float v) => i.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)i.Operand - v) < 0.01f;

        /// the same constant is an int in some methods and a float in others, so the replacement
        /// follows the original instruction's kind
        void Constant(MethodDefinition m, float original, Func<bool, Instruction[]> make)
        {
            Scan(m, i => IsR4(i, original) ? make(true) : IsI4(i, (int)original) ? make(false) : null);
        }

        void Replace180(MethodDefinition m) => Constant(m, 180f, GH);

        // ---- the passes ----

        void PatchParallaxRender(MethodDefinition m)
        {
            Replace180(m);
            // the stretch draw replaces the 6 argument MTexture.Draw
            Scan(m, i => i.OpCode.Code == Code.Callvirt && i.Operand is MethodReference mr
                      && mr.DeclaringType.Name == "MTexture" && mr.Name == "Draw" && mr.Parameters.Count == 6
                ? new[] { Call("ParallaxDraw") } : null);
            Prefix(m, Ldarg0(), Call("SetParallax"));
        }

        void PatchMountainRender(MethodDefinition m)
        {
            Scan(m, i => i.OpCode.Code == Code.Callvirt && i.Operand is MethodReference mr
                      && mr.DeclaringType.Name == "MTexture" && mr.Name == "Draw" && mr.Parameters.Count == 3
                ? new[] { Call("MountainDraw") } : null);
        }

        void PatchDustEdgesBeforeRender(MethodDefinition m)
        {
            Scan(m, i => IsR4(i, 180f) ? Expr(true, true) : null);
            Scan(m, i => i.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)i.Operand - 1f / 180f) < 0.0001f
                ? new[] { R4(1f), Call("get_GameHeight"), Instruction.Create(OpCodes.Conv_R4), Instruction.Create(OpCodes.Div) } : null);
        }

        void PatchDustEdgesRender(MethodDefinition m)
        {
            // FlooredCamera is an instance method: the call already consumes `this`, so the
            // replacement is a plain one for one swap
            bool done = false;
            Scan(m, i =>
            {
                if (done || i.OpCode.Code != Code.Call || i.Operand is not MethodReference mr
                    || mr.DeclaringType.Name != "DustEdges" || mr.Name != "FlooredCamera") return null;
                done = true;
                return new[] { Call("DustEdgesCamera") };
            });
        }

        void ReanchorToBottom(MethodDefinition m, float original, float fromBottom)
        {
            Scan(m, i => IsR4(i, original) ? new[] { R4(fromBottom), Call("BottomAnchor") } : null);
        }

        // guarded on the width: the height constants (200, 190) are common numbers
        void PatchScreenFadeRects(MethodDefinition m)
        {
            Scan(m, i =>
            {
                if (i.OpCode.Code != Code.Ldc_R4) return null;
                float height = (float)i.Operand;
                if (!CelesteSpec.ScreenCovers.Any(c => Math.Abs(c.height - height) < 0.01f)) return null;
                var width = i.Previous;
                if (width == null || width.OpCode.Code != Code.Ldc_R4) return null;
                float w = (float)width.Operand;
                if (!CelesteSpec.ScreenCovers.Any(c => Math.Abs(c.width - w) < 0.01f && Math.Abs(c.height - height) < 0.01f)) return null;
                float extra = CelesteSpec.ScreenCovers.First(c => Math.Abs(c.height - height) < 0.01f).extra;
                return Expr(true, true, (OpCodes.Add, extra));
            });
        }

        void PatchScreenPaddingAspect(MethodDefinition m)
        {
            Scan(m, i => i.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)i.Operand - 0.5625f) < 0.0001f
                ? Expr(true, true, (OpCodes.Div, 320f)) : null);
            // only the ScreenPadding * 9f / 16f pattern: a 9 followed by a multiply by 16
            Scan(m, i =>
            {
                if (!IsR4(i, 9f)) return null;
                var mul = i.Next;
                if (mul == null || mul.OpCode.Code != Code.Mul) return null;
                var sixteen = mul.Next;
                if (sixteen == null || !IsR4(sixteen, 16f)) return null;
                return Expr(true, true, (OpCodes.Div, 320f), (OpCodes.Mul, 16f));
            });
        }

        void PatchCulling(MethodDefinition m)
        {
            Scan(m, i => i.OpCode.Code == Code.Callvirt && i.Operand is MethodReference mr
                      && mr.DeclaringType.Name == "Camera" && mr.Name == "get_Y"
                ? new[] { Instruction.Create(OpCodes.Callvirt, cameraTop) } : null);
            // swapping get_Y is not a Fit43 call, but the method is edited all the same
            Touch(m);
            Replace180(m);
        }

        void PatchHudBufferHeight(MethodDefinition m)
        {
            Scan(m, i => IsI4(i, 1082) ? new[] { Call("UIHeightFromBackbuffer") } : null);
        }

        void PatchLowTypes()
        {
            foreach (var name in CelesteSpec.LowTypes)
            {
                var type = FindType("Celeste." + name);
                if (type == null) continue;
                foreach (var m in type.Methods.Where(m => m.HasBody && !m.IsAbstract && !m.IsConstructor).ToList())
                    PatchLowConstants(m, name);
                if (Array.IndexOf(CelesteSpec.CtorTypes, name) < 0) continue;
                foreach (var m in type.Methods.Where(m => m.HasBody && m.IsConstructor).ToList())
                    PatchLowConstants(m, name);
            }
        }

        void PatchLowConstants(MethodDefinition m, string typeName)
        {
            Replace180(m);
            if (typeName == "GameplayBuffers")
                Scan(m, i => IsI4(i, 244) ? Expr(true, false, (OpCodes.Add, 64f)) : null);
        }

        void PatchUiTypes()
        {
            foreach (var name in CelesteSpec.UiTypes)
            {
                var type = FindType("Celeste." + name);
                if (type == null) continue;
                foreach (var t in new[] { type }.Concat(type.NestedTypes))
                    foreach (var m in t.Methods.Where(m => m.HasBody && !m.IsAbstract && !m.IsConstructor).ToList())
                        PatchUiConstants(m);
            }
        }

        // the ui is laid out in a 1920x1080 space, so every 1080 based constant follows Engine.Height
        void PatchUiConstants(MethodDefinition m)
        {
            Scan(m, i =>
            {
                bool isFloat = i.OpCode.Code == Code.Ldc_R4;
                if (!isFloat && i.OpCode.Code != Code.Ldc_I4) return null;
                int val = isFloat ? (int)(float)i.Operand : (int)i.Operand;
                if (isFloat ? !CelesteSpec.UiConstants.Any(c => Math.Abs((float)i.Operand - c) < 0.01f)
                            : Array.IndexOf(CelesteSpec.UiConstants, val) < 0) return null;
                if (isFloat)
                {
                    // the nearest screen constant, as the mod's tolerance check did
                    val = CelesteSpec.UiConstants.First(c => Math.Abs((float)i.Operand - c) < 0.01f);
                }
                return val switch
                {
                    540 => Expr(false, isFloat, (OpCodes.Div, 2f)),
                    1080 => Expr(false, isFloat),
                    1082 => Expr(false, isFloat, (OpCodes.Add, 2f)),
                    1100 => Expr(false, isFloat, (OpCodes.Add, 20f)),
                    _ => null,
                };
            });
        }

        // ---- branches ----

        /// cecil writes a short branch as a single signed byte and never widens it, so inserting
        /// code between a branch and its target silently overflows the operand. widen every short
        /// branch in any method we edit before cecil writes the body.
        static void ExpandShortBranches(MethodDefinition method)
        {
            foreach (var ins in method.Body.Instructions)
            {
                var wide = LongBranch(ins.OpCode);
                if (wide.HasValue) ins.OpCode = wide.Value;
            }
        }

        static OpCode? LongBranch(OpCode op) => op.Code switch
        {
            Code.Br_S => OpCodes.Br,
            Code.Brfalse_S => OpCodes.Brfalse,
            Code.Brtrue_S => OpCodes.Brtrue,
            Code.Beq_S => OpCodes.Beq,
            Code.Bge_S => OpCodes.Bge,
            Code.Bgt_S => OpCodes.Bgt,
            Code.Ble_S => OpCodes.Ble,
            Code.Blt_S => OpCodes.Blt,
            Code.Bne_Un_S => OpCodes.Bne_Un,
            Code.Bge_Un_S => OpCodes.Bge_Un,
            Code.Bgt_Un_S => OpCodes.Bgt_Un,
            Code.Ble_Un_S => OpCodes.Ble_Un,
            Code.Blt_Un_S => OpCodes.Blt_Un,
            Code.Leave_S => OpCodes.Leave,
            _ => null,
        };
    }

    // ---- helper cloning ----

    /// clones the Fit43 type from the embedded Fit43.dll into the target module as Celeste.Fit43.
    ///
    /// the helper is compiled against Celeste.dll, so its references to the game are scoped to an
    /// assembly called Celeste. importing them as they are would add a reference from Celeste.exe
    /// to itself. every game type, method and field is therefore mapped onto the module's own
    /// definition by name and signature, and only mscorlib and FNA members are imported. self
    /// calls go to the clone, never to Fit43.dll, and exception handlers are copied with the
    /// instructions.
    static TypeDefinition InjectHelper(ModuleDefinition target)
    {
        var bytes = ReadHelperAssembly() ?? throw new FileNotFoundException("embedded Fit43.dll payload missing from the patcher");
        using var ms = new MemoryStream(bytes);
        using var helperAsm = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { AssemblyResolver = new NullResolver() });
        var src = helperAsm.MainModule.Types.First(t => t.Name == "Fit43");
        var map = new Mapper(target, src);

        var dst = new TypeDefinition(src.Namespace, src.Name, src.Attributes, target.TypeSystem.Object);
        map.Dst = dst;
        target.Types.Add(dst);

        foreach (var f in src.Fields)
        {
            if (f.IsLiteral) continue;
            var nf = new FieldDefinition(f.Name, f.Attributes, map.Type(f.FieldType));
            dst.Fields.Add(nf);
            map.Fields[f.Name] = nf;
        }

        // pass 1: declare every method before any body is copied, so self calls can bind
        foreach (var m in src.Methods)
        {
            var decl = new MethodDefinition(m.Name, m.Attributes, map.Type(m.ReturnType)) { ImplAttributes = m.ImplAttributes };
            foreach (var p in m.Parameters)
                decl.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, map.Type(p.ParameterType)));
            dst.Methods.Add(decl);
        }

        // pass 2: bodies
        for (int mi = 0; mi < src.Methods.Count; mi++)
        {
            var m = src.Methods[mi];
            var nm = dst.Methods[mi];
            if (!m.HasBody) continue;
            nm.Body.InitLocals = m.Body.InitLocals;
            nm.Body.MaxStackSize = m.Body.MaxStackSize;

            var varMap = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var v in m.Body.Variables)
            {
                var nv = new VariableDefinition(map.Type(v.VariableType));
                nm.Body.Variables.Add(nv);
                varMap[v] = nv;
            }

            var il = nm.Body.GetILProcessor();
            var insMap = new Dictionary<Instruction, Instruction>();
            foreach (var ins in m.Body.Instructions)
            {
                var dummy = Instruction.Create(OpCodes.Nop);
                insMap[ins] = dummy;
                il.Append(dummy);
            }

            for (int idx = 0; idx < m.Body.Instructions.Count; idx++)
            {
                var ins = m.Body.Instructions[idx];
                var ni = nm.Body.Instructions[idx];
                ni.OpCode = ins.OpCode;
                ni.Operand = ins.Operand switch
                {
                    null => null,
                    FieldReference fr => map.Field(fr),
                    MethodReference mr => map.Method(mr),
                    TypeReference tr => map.Type(tr),
                    VariableDefinition vd => varMap[vd],
                    ParameterDefinition pd => nm.Parameters[pd.Index],
                    Instruction t => insMap[t],
                    Instruction[] ts => ts.Select(t => insMap[t]).ToArray(),
                    _ => ins.Operand,
                };
            }

            // the try/catch blocks live in the exception table, not the instruction stream
            Instruction? MapIns(Instruction? i) => i == null ? null : insMap[i];
            foreach (var eh in m.Body.ExceptionHandlers)
            {
                nm.Body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
                {
                    TryStart = MapIns(eh.TryStart),
                    TryEnd = MapIns(eh.TryEnd),
                    HandlerStart = MapIns(eh.HandlerStart),
                    HandlerEnd = MapIns(eh.HandlerEnd),
                    FilterStart = MapIns(eh.FilterStart),
                    CatchType = eh.CatchType == null ? null : map.Type(eh.CatchType),
                });
            }
        }

        Console.WriteLine($"    [OK] {dst.FullName} cloned ({dst.Methods.Count} methods, {dst.Fields.Count} fields)");
        return dst;
    }

    /// maps helper-side references onto the target module
    sealed class Mapper
    {
        readonly ModuleDefinition target;
        readonly TypeDefinition src;
        public TypeDefinition Dst = null!;
        public readonly Dictionary<string, FieldDefinition> Fields = new();

        public Mapper(ModuleDefinition target, TypeDefinition src) { this.target = target; this.src = src; }

        static TypeReference Element(TypeReference t)
        {
            while (t is TypeSpecification s) t = s.ElementType;
            return t;
        }

        // true when the reference is scoped to the game assembly (the helper's "Celeste" reference)
        static bool IsGame(TypeReference t)
        {
            t = Element(t);
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t.Scope is AssemblyNameReference a && a.Name == "Celeste";
        }

        public TypeReference Type(TypeReference tr)
        {
            if (tr.FullName == src.FullName) return Dst;
            switch (tr)
            {
                case GenericInstanceType git:
                {
                    var g = new GenericInstanceType(Type(git.ElementType));
                    foreach (var a in git.GenericArguments) g.GenericArguments.Add(Type(a));
                    return g;
                }
                case ArrayType at when at.Rank == 1: return new ArrayType(Type(at.ElementType));
                case ByReferenceType br: return new ByReferenceType(Type(br.ElementType));
                case GenericParameter: return tr;
                case TypeSpecification: throw new NotSupportedException($"helper uses unsupported type shape {tr.FullName}");
            }
            if (IsGame(tr))
                return target.GetType(tr.FullName) ?? throw new InvalidOperationException($"game type {tr.FullName} not found");
            return target.ImportReference(tr);
        }

        static IEnumerable<TypeDefinition> Chain(ModuleDefinition module, TypeDefinition t)
        {
            while (t != null)
            {
                yield return t;
                var b = t.BaseType;
                t = b == null ? null! : module.GetType(b.FullName);
            }
        }

        public FieldReference Field(FieldReference fr)
        {
            if (fr.DeclaringType.FullName == src.FullName) return Fields[fr.Name];
            if (IsGame(fr.DeclaringType))
            {
                var decl = (TypeDefinition)Type(fr.DeclaringType);
                foreach (var t in Chain(target, decl))
                {
                    var f = t.Fields.FirstOrDefault(x => x.Name == fr.Name);
                    if (f != null) return f;
                }
                throw new InvalidOperationException($"game field {fr.FullName} not found");
            }
            return target.ImportReference(fr);
        }

        public MethodReference Method(MethodReference mr)
        {
            if (mr is GenericInstanceMethod gim)
            {
                var g = new GenericInstanceMethod(Method(gim.ElementMethod));
                foreach (var a in gim.GenericArguments) g.GenericArguments.Add(Type(a));
                return g;
            }
            if (mr.DeclaringType.FullName == src.FullName)
            {
                // overloads share a name, so the parameter types have to match too
                var match = Dst.Methods.FirstOrDefault(m => m.Name == mr.Name && SameParams(m, mr));
                return match ?? throw new InvalidOperationException($"helper method {mr.FullName} not found on the clone");
            }
            if (IsGame(mr.DeclaringType))
            {
                if (mr.DeclaringType is TypeSpecification) throw new NotSupportedException($"helper calls generic game member {mr.FullName}");
                var decl = (TypeDefinition)Type(mr.DeclaringType);
                foreach (var t in Chain(target, decl))
                {
                    var m = t.Methods.FirstOrDefault(x => x.Name == mr.Name && SameParams(x, mr));
                    if (m != null) return m;
                }
                throw new InvalidOperationException($"game method {mr.FullName} not found");
            }
            return target.ImportReference(mr);
        }

        static bool SameParams(MethodReference a, MethodReference b) =>
            a.Parameters.Count == b.Parameters.Count
            && a.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(b.Parameters.Select(p => p.ParameterType.FullName));
    }
}
