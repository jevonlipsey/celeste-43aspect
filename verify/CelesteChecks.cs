using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Celeste43.Verify;

/// celeste-specific semantic checks on the patched module: the hit table, the swept bodies, the
/// helper clone and the Engine.UpdateView prefix. reference-free, like IlVerifier.
public sealed class CelesteChecks
{
    private readonly Checks checks;
    private readonly ModuleDefinition module;
    private readonly byte[]? payload;

    public CelesteChecks(Checks checks, ModuleDefinition module, byte[]? helperPayload)
    {
        this.checks = checks;
        this.module = module;
        this.payload = helperPayload;
    }

    public static TypeDefinition? FindType(ModuleDefinition module, string name) =>
        module.GetType(name.StartsWith("Monocle.", StringComparison.Ordinal) ? name : "Celeste." + name);

    // the Fit43 methods a body calls
    public static IEnumerable<Instruction> Fit43Calls(MethodDefinition method)
    {
        if (!method.HasBody) yield break;
        foreach (var ins in method.Body.Instructions)
            if ((ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt)
                && ins.Operand is MethodReference mr && mr.DeclaringType.FullName == CelesteSpec.HelperType)
                yield return ins;
    }

    /// Fit43 call count per "Type::method" over the whole module, helper type excluded
    public static SortedDictionary<string, int> CountHits(ModuleDefinition module)
    {
        var hits = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Walk(TypeDefinition t)
        {
            foreach (var n in t.NestedTypes) Walk(n);
            if (t.FullName == CelesteSpec.HelperType) return;
            foreach (var m in t.Methods)
            {
                int c = Fit43Calls(m).Count();
                if (c == 0) continue;
                var key = $"{t.FullName}::{m.Name}";
                hits[key] = hits.GetValueOrDefault(key) + c;
            }
        }
        foreach (var t in module.Types) Walk(t);
        return hits;
    }

    public void Run()
    {
        checks.Section("celeste semantics");
        var helper = module.GetType(CelesteSpec.HelperType);
        checks.Check(helper != null, "Celeste.Fit43 is in the module");
        if (helper == null) return;

        CheckHelper(helper);
        CheckUpdateView();
        CheckHits();
        CheckRawConstants();
        CheckCovers();
        CheckCulls();
        CheckCallsResolve(helper);
    }

    private void CheckHelper(TypeDefinition helper)
    {
        // the clone must not drag the helper assembly (or the game's own name) along
        var bad = new List<string>();
        foreach (var m in helper.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                TypeReference? scopeType = ins.Operand switch
                {
                    MethodReference mr => mr.DeclaringType,
                    FieldReference fr => fr.DeclaringType,
                    TypeReference tr => tr,
                    _ => null,
                };
                if (scopeType == null) continue;
                var name = IlVerifier.ScopeName(Element(scopeType));
                if (name != null && (name.StartsWith("Fit43", StringComparison.Ordinal) || name == module.Assembly.Name.Name))
                    bad.Add($"{m.Name}: {name}");
            }
        }
        checks.Check(bad.Count == 0,
            bad.Count == 0 ? "the helper clone has no references back to Fit43.dll" : $"helper clone references: {string.Join(", ", bad.Take(5))}");

        if (payload == null)
        {
            checks.Fail("helper payload (Fit43.dll) missing, cannot compare the clone");
            return;
        }
        using var ms = new System.IO.MemoryStream(payload);
        using var src = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { AssemblyResolver = new NullResolver() });
        var srcType = src.MainModule.Types.FirstOrDefault(t => t.Name == "Fit43");
        if (srcType == null) { checks.Fail("helper payload has no Fit43 type"); return; }

        var diffs = new List<string>();
        if (srcType.Methods.Count != helper.Methods.Count) diffs.Add($"method count {srcType.Methods.Count} vs {helper.Methods.Count}");
        foreach (var sm in srcType.Methods)
        {
            var cm = helper.Methods.FirstOrDefault(m => m.Name == sm.Name && m.Parameters.Count == sm.Parameters.Count);
            if (cm == null) { diffs.Add($"missing {sm.Name}"); continue; }
            if (!sm.HasBody) continue;
            if (sm.Body.Instructions.Count != cm.Body.Instructions.Count) diffs.Add($"{sm.Name}: instructions {sm.Body.Instructions.Count} vs {cm.Body.Instructions.Count}");
            if (sm.Body.ExceptionHandlers.Count != cm.Body.ExceptionHandlers.Count) diffs.Add($"{sm.Name}: handlers {sm.Body.ExceptionHandlers.Count} vs {cm.Body.ExceptionHandlers.Count}");
            if (sm.Body.Variables.Count != cm.Body.Variables.Count) diffs.Add($"{sm.Name}: locals");
        }
        if (srcType.Fields.Count(f => !f.IsLiteral) != helper.Fields.Count) diffs.Add("field count");
        checks.Check(diffs.Count == 0,
            diffs.Count == 0 ? "the helper clone matches the embedded Fit43.dll (methods, bodies, exception handlers)" : $"helper clone differs: {string.Join("; ", diffs.Take(5))}");
    }

    private static TypeReference Element(TypeReference t)
    {
        while (t is TypeSpecification spec) t = spec.ElementType;
        return t;
    }

    private void CheckUpdateView()
    {
        var engine = module.GetType(CelesteSpec.UpdateViewType);
        var uv = engine?.Methods.FirstOrDefault(m => m.Name == "UpdateView" && m.Parameters.Count == 0);
        if (uv == null || !uv.HasBody) { checks.Fail("Engine.UpdateView not found"); return; }
        var i = uv.Body.Instructions;
        bool ok = i.Count > 2 && i[0].OpCode.Code == Code.Ldarg_0
            && i[1].OpCode.Code == Code.Call && i[1].Operand is MethodReference mr
            && mr.DeclaringType.FullName == CelesteSpec.HelperType && mr.Name == "UpdateViewPrefix";
        checks.Check(ok, "Engine.UpdateView starts with ldarg.0; call Fit43.UpdateViewPrefix");
    }

    private void CheckHits()
    {
        var actual = CountHits(module);
        var diffs = Diff(actual, CelesteSpec.ExpectedHits);
        checks.Check(diffs.Count == 0,
            diffs.Count == 0
                ? $"{actual.Count} hooked methods hold exactly the expected Fit43 call counts ({actual.Values.Sum()} calls)"
                : $"Fit43 call counts differ: {string.Join("; ", diffs.Take(8))}");
    }

    public static List<string> Diff(IDictionary<string, int> actual, IDictionary<string, int> expected)
    {
        var diffs = new List<string>();
        foreach (var kv in expected)
        {
            actual.TryGetValue(kv.Key, out var got);
            if (got != kv.Value) diffs.Add($"{kv.Key} expected {kv.Value} got {got}");
        }
        foreach (var kv in actual)
            if (!expected.ContainsKey(kv.Key)) diffs.Add($"{kv.Key} unexpected {kv.Value}");
        return diffs;
    }

    // every instruction that pushes one of these constants, as ldc.i4 or ldc.r4
    private static bool PushesAny(Instruction ins, IReadOnlyCollection<float> values)
    {
        if (ins.OpCode.Code == Code.Ldc_I4)
        {
            int v = (int)ins.Operand;
            return values.Any(x => v == (int)x);
        }
        if (ins.OpCode.Code == Code.Ldc_R4)
        {
            float v = (float)ins.Operand;
            return values.Any(x => Math.Abs(v - x) < 0.01f);
        }
        return false;
    }

    private IEnumerable<MethodDefinition> Methods(TypeDefinition t, bool ctors) =>
        t.Methods.Where(m => m.HasBody && !m.IsAbstract && (ctors || !m.IsConstructor));

    private void CheckRawConstants()
    {
        var left = new List<string>();

        foreach (var (type, method, value) in CelesteSpec.ConstantHooks)
        {
            var t = FindType(module, type == "Celeste" ? "Celeste" : type);
            if (t == null) { left.Add($"{type} missing"); continue; }
            foreach (var m in t.Methods.Where(m => m.Name == method && m.HasBody))
                foreach (var ins in m.Body.Instructions)
                    if (PushesAny(ins, new[] { value })) left.Add($"{t.FullName}::{m.Name}@{ins.Offset:x4} still pushes {value}");
        }

        foreach (var name in CelesteSpec.LowTypes)
        {
            var t = FindType(module, name);
            if (t == null) { left.Add($"{name} missing"); continue; }
            var consts = name == "GameplayBuffers" ? new[] { 180f, 244f } : new[] { 180f };
            bool ctors = Array.IndexOf(CelesteSpec.CtorTypes, name) >= 0 || name == "GameplayRenderer";
            foreach (var m in Methods(t, ctors))
                foreach (var ins in m.Body.Instructions)
                    if (PushesAny(ins, consts)) left.Add($"{t.FullName}::{m.Name}@{ins.Offset:x4} still pushes 180/244");
        }

        var ui = CelesteSpec.UiConstants.Select(c => (float)c).ToArray();
        foreach (var name in CelesteSpec.UiTypes)
        {
            var t = FindType(module, name);
            if (t == null) { left.Add($"{name} missing"); continue; }
            foreach (var type in new[] { t }.Concat(t.NestedTypes))
                foreach (var m in Methods(type, false))
                    foreach (var ins in m.Body.Instructions)
                        if (PushesAny(ins, ui)) left.Add($"{type.FullName}::{m.Name}@{ins.Offset:x4} still pushes a 1080-space constant");
        }

        checks.Check(left.Count == 0,
            left.Count == 0
                ? "no raw 180/244/1080/1082/540/1100 constants left in the swept bodies"
                : $"{left.Count} raw constant(s) left: {string.Join("; ", left.Take(6))}");
    }

    private void CheckCovers()
    {
        var left = new List<string>();
        foreach (var (type, method) in CelesteSpec.CoverMethods)
        {
            var t = FindType(module, type);
            if (t == null) continue;
            foreach (var m in t.Methods.Where(m => m.Name == method && m.HasBody))
            {
                var ins = m.Body.Instructions;
                for (int i = 1; i < ins.Count; i++)
                {
                    if (ins[i].OpCode.Code != Code.Ldc_R4 || ins[i - 1].OpCode.Code != Code.Ldc_R4) continue;
                    float w = (float)ins[i - 1].Operand, h = (float)ins[i].Operand;
                    foreach (var c in CelesteSpec.ScreenCovers)
                        if (Math.Abs(c.width - w) < 0.01f && Math.Abs(c.height - h) < 0.01f)
                            left.Add($"{t.FullName}::{m.Name}@{ins[i].Offset:x4} cover {w}x{h}");
                }
            }
        }
        checks.Check(left.Count == 0,
            left.Count == 0 ? "no 16:9 screen cover rects left" : $"cover rects left: {string.Join("; ", left.Take(6))}");
    }

    private void CheckCulls()
    {
        var left = new List<string>();
        foreach (var (type, method) in CelesteSpec.CullMethods)
        {
            var t = FindType(module, type);
            if (t == null) { left.Add($"{type} missing"); continue; }
            foreach (var m in t.Methods.Where(m => m.Name == method && m.HasBody))
                foreach (var ins in m.Body.Instructions)
                    if (ins.Operand is MethodReference mr && mr.Name == "get_Y" && mr.DeclaringType.Name == "Camera")
                        left.Add($"{t.FullName}::{m.Name}@{ins.Offset:x4}");
        }
        checks.Check(left.Count == 0,
            left.Count == 0 ? "the culls compare Camera.Top, not Camera.Y" : $"Camera.get_Y left in: {string.Join("; ", left)}");
    }

    // every call into Fit43 must hit a method that exists on the clone with a matching arity
    private void CheckCallsResolve(TypeDefinition helper)
    {
        var bad = new List<string>();
        void Walk(TypeDefinition t)
        {
            foreach (var n in t.NestedTypes) Walk(n);
            if (t == helper) return;
            foreach (var m in t.Methods)
                foreach (var ins in Fit43Calls(m))
                {
                    var mr = (MethodReference)ins.Operand;
                    var def = mr as MethodDefinition;
                    if (def == null || def.DeclaringType != helper)
                        bad.Add($"{t.Name}::{m.Name}@{ins.Offset:x4} -> {mr.Name}");
                }
        }
        foreach (var t in module.Types) Walk(t);
        checks.Check(bad.Count == 0,
            bad.Count == 0 ? "every Fit43 call binds to the cloned type" : $"unbound Fit43 calls: {string.Join("; ", bad.Take(6))}");
    }
}

/// resolves nothing; used to read the helper payload and the patched output
public sealed class NullResolver : IAssemblyResolver
{
    public AssemblyDefinition Resolve(AssemblyNameReference name) => throw new AssemblyResolutionException(name);
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => throw new AssemblyResolutionException(name);
    public void Dispose() { }
}
