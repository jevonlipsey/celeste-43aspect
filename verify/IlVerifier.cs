using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Celeste43.Verify;

/// structural verifier for the patched assembly. it re-derives every stack height from the
/// actual instruction stream instead of trusting the writer, checks every branch lands on an
/// instruction boundary, and rejects any reference that would drag the patcher's runtime (or the
/// helper dll) into the game. there is no resolver anywhere: a member in the module resolves, a
/// member in another assembly must be scoped to one of the game's own references.
public sealed class IlVerifier
{
    // the assemblies Celeste.exe already references. nothing patched in may add to this list.
    public static readonly string[] AllowedAssemblies =
    {
        "mscorlib", "FNA", "System", "System.Core", "System.Xml", "Steamworks.NET",
    };

    private readonly Checks checks;
    private readonly ModuleDefinition module;
    private int externalMethodRefs;
    private int externalFieldRefs;
    private int externalTypeRefs;
    private readonly HashSet<Instruction> instructionSet = new();
    private readonly List<string> bodyErrors = new();

    public long InstructionCount { get; private set; }
    public long MethodCount { get; private set; }
    public long MemberRefCount { get; private set; }
    public int MaxComputedStack { get; private set; }
    public int MaxDeclaredStack { get; private set; }

    public IlVerifier(Checks checks, ModuleDefinition module)
    {
        this.checks = checks;
        this.module = module;
    }

    public void Run()
    {
        checks.Section("assembly structure");

        var refs = module.AssemblyReferences.Select(r => r.Name).ToList();
        checks.Check(!refs.Contains("System.Private.CoreLib"),
            "no System.Private.CoreLib assembly reference");
        checks.Check(!refs.Contains("netstandard"),
            "no netstandard assembly reference");
        checks.Check(!refs.Contains("System.Runtime"),
            "no System.Runtime assembly reference");
        checks.Check(refs.All(r => !r.StartsWith("Fit43", StringComparison.Ordinal)),
            "no Fit43 assembly reference");
        checks.Check(!refs.Contains(module.Assembly.Name.Name),
            "no reference back to the module's own assembly");
        var stray = refs.Where(r => Array.IndexOf(AllowedAssemblies, r) < 0).ToList();
        checks.Check(stray.Count == 0,
            stray.Count == 0
                ? "every assembly reference is one of the game's own (mscorlib, FNA, System, ...)"
                : $"unexpected assembly reference(s): {string.Join(", ", stray)}");
        checks.Check(refs.Contains("mscorlib"), "mscorlib present");

        var unbound = FindUnboundHelperReferences();
        checks.Check(unbound.Count == 0,
            unbound.Count == 0
                ? "every helper type/member reference points inside Celeste.exe"
                : $"helper references still point at another module: {string.Join(", ", unbound.Take(6))}");

        foreach (var type in module.Types)
            WalkType(type);

        checks.Check(bodyErrors.Count == 0,
            bodyErrors.Count == 0
                ? $"{MethodCount} methods / {InstructionCount} instructions: stack, branch, local and member checks clean"
                : $"{bodyErrors.Count} body error(s): {string.Join(" | ", bodyErrors.Take(8))}");
        checks.Note($"max computed stack {MaxComputedStack}, max declared {MaxDeclaredStack}");
        checks.Note($"{MemberRefCount - externalMethodRefs - externalFieldRefs} members resolved inside the module; " +
                    $"{externalMethodRefs} method / {externalFieldRefs} field / {externalTypeRefs} type references to allowed assemblies");
    }

    // true when the type lives in another assembly. specs (arrays, generic instances,
    // by-refs) take the scope of their element type.
    private static TypeReference Element(TypeReference t)
    {
        while (t is TypeSpecification spec) t = spec.ElementType;
        return t;
    }

    private static bool IsExternal(TypeReference type)
    {
        while (type is TypeSpecification spec) type = spec.ElementType;
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type.Scope is AssemblyNameReference;
    }

    // an external member is only accepted if its assembly is one of the game's own references.
    // returns true when the caller should skip the resolve (there is no resolver to ask).
    private bool SkipExternal(string label, Instruction ins, TypeReference declaring, ref int counter)
    {
        if (!IsExternal(declaring)) return false;
        var type = declaring;
        while (type is TypeSpecification spec) type = spec.ElementType;
        while (type.DeclaringType != null) type = type.DeclaringType;
        var scope = (AssemblyNameReference)type.Scope;
        if (Array.IndexOf(AllowedAssemblies, scope.Name) < 0 ||
            !module.AssemblyReferences.Any(r => ReferenceEquals(r, scope) || r.FullName == scope.FullName))
            bodyErrors.Add($"{label}@{ins.Offset:x4}: {declaring.FullName} is scoped to {scope.Name}, which is not one of the game's references");
        counter++;
        return true;
    }

    private void WalkType(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes) WalkType(nested);
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            MethodCount++;
            ValidateBody(type, method);
        }
    }

    private List<string> FindUnboundHelperReferences()
    {
        var found = new List<string>();
        foreach (var tr in module.GetTypeReferences())
        {
            var scope = ScopeName(tr);
            if (scope != null && scope.StartsWith("Fit43", StringComparison.Ordinal))
                found.Add(tr.FullName);
        }
        return found;
    }

    private void ValidateBody(TypeDefinition type, MethodDefinition method)
    {
        var body = method.Body;
        var label = $"{type.FullName}::{method.Name}";
        InstructionCount += body.Instructions.Count;

        if (body.Instructions.Count == 0)
        {
            bodyErrors.Add($"{label}: empty body");
            return;
        }
        if (method.IsAbstract || method.IsPInvokeImpl) return;

        instructionSet.Clear();
        foreach (var ins in body.Instructions) instructionSet.Add(ins);

        ValidateVariables(label, method);
        ValidateHandlers(label, method);
        ValidateBranches(label, method);
        ValidateMembers(label, method);
        ComputeStack(label, method);
    }

    private void ValidateVariables(string label, MethodDefinition method)
    {
        var body = method.Body;
        foreach (var ins in body.Instructions)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloca:
                case Code.Ldloca_S:
                    if (ins.Operand is VariableDefinition v && v.Index >= body.Variables.Count)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: local index {v.Index} outside {body.Variables.Count}");
                    break;
                case Code.Stloc:
                case Code.Stloc_S:
                    if (ins.Operand is VariableDefinition s && s.Index >= body.Variables.Count)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: local index {s.Index} outside {body.Variables.Count}");
                    break;
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarga:
                case Code.Ldarga_S:
                case Code.Starg:
                case Code.Starg_S:
                    if (ins.Operand is ParameterDefinition p && p.Index >= method.Parameters.Count)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: arg index {p.Index} outside {method.Parameters.Count}");
                    break;
            }
        }
    }

    // a `leave` only exists to exit a protected region. a body with leaves and an empty
    // exception table is a try/catch whose handlers were dropped on the way in - exactly
    // how a helper clone can lose every catch (it copied
    // instructions but not Body.ExceptionHandlers). the handler checks below only look at
    // handlers that exist, so this is the only thing that can notice them missing.
    private void ValidateHandlers(string label, MethodDefinition method)
    {
        var body = method.Body;
        if (body.ExceptionHandlers.Count > 0) return;
        var leave = body.Instructions.FirstOrDefault(i => i.OpCode.Code is Code.Leave or Code.Leave_S);
        if (leave != null)
            bodyErrors.Add($"{label}@{leave.Offset:x4}: {leave.OpCode} in a body with no exception handlers (try/catch stripped?)");
    }

    private void ValidateBranches(string label, MethodDefinition method)
    {
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.TryStart != null && !instructionSet.Contains(handler.TryStart))
                bodyErrors.Add($"{label}: handler try start is not an instruction");
            if (handler.HandlerStart != null && !instructionSet.Contains(handler.HandlerStart))
                bodyErrors.Add($"{label}: handler start is not an instruction");
            if (handler.FilterStart != null && !instructionSet.Contains(handler.FilterStart))
                bodyErrors.Add($"{label}: filter start is not an instruction");
            if (handler.TryEnd != null && !instructionSet.Contains(handler.TryEnd))
                bodyErrors.Add($"{label}: handler try end is not an instruction");
            if (handler.HandlerEnd != null && !instructionSet.Contains(handler.HandlerEnd))
                bodyErrors.Add($"{label}: handler end is not an instruction");
        }

        foreach (var ins in method.Body.Instructions)
        {
            if (ins.Operand is Instruction target)
            {
                if (!instructionSet.Contains(target))
                    bodyErrors.Add($"{label}@{ins.Offset:x4}: {ins.OpCode} target is not an instruction");
                else if (IsShortBranch(ins.OpCode))
                {
                    long distance = (long)target.Offset - (ins.Offset + ins.GetSize());
                    if (distance < sbyte.MinValue || distance > sbyte.MaxValue)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: short branch distance {distance} overflows");
                }
            }
            else if (ins.Operand is Instruction[] targets)
            {
                foreach (var t in targets)
                    if (!instructionSet.Contains(t))
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: switch target is not an instruction");
            }
        }
    }

    private void ValidateMembers(string label, MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
        {
            switch (ins.Operand)
            {
                case MethodReference mr:
                    MemberRefCount++;
                    ValidateMethodRef(label, ins, mr);
                    break;
                case FieldReference fr:
                    MemberRefCount++;
                    ValidateFieldRef(label, ins, fr);
                    break;
                case TypeReference tr:
                    ValidateTypeRef(label, ins, tr);
                    break;
            }
        }
    }

    private void ValidateMethodRef(string label, Instruction ins, MethodReference mr)
    {
        var element = (mr as GenericInstanceMethod)?.ElementMethod ?? mr;
        var scope = ScopeName(element.DeclaringType);
        if (scope != null && (scope.StartsWith("Fit43", StringComparison.Ordinal) ||
                              scope.StartsWith("System.Private.CoreLib", StringComparison.Ordinal)))
        {
            bodyErrors.Add($"{label}@{ins.Offset:x4}: forbidden method scope {scope} for {element.FullName}");
            return;
        }

        if (SkipExternal(label, ins, element.DeclaringType, ref externalMethodRefs)) return;

        try
        {
            var def = element.Resolve();
            if (def == null)
            {
                // multi-dim arrays expose cli-generated .ctor/Get/Set/Address members on
                // the array type itself, which cecil cannot resolve. accept them once the
                // element type is known and the arity matches the rank.
                if (element.DeclaringType is ArrayType at && IsWellFormedArrayMember(at, element)) return;
                bodyErrors.Add($"{label}@{ins.Offset:x4}: cannot resolve {element.FullName}");
                return;
            }
            if ((element.CallingConvention & MethodCallingConvention.VarArg) == 0 &&
                element.Parameters.Count != def.Parameters.Count)
                bodyErrors.Add($"{label}@{ins.Offset:x4}: arity mismatch {element.FullName} ({element.Parameters.Count} vs {def.Parameters.Count})");
            if (element.Name == ".ctor" && !def.IsConstructor)
                bodyErrors.Add($"{label}@{ins.Offset:x4}: {element.FullName} marked .ctor but resolves to a non-constructor");
        }
        catch (AssemblyResolutionException)
        {
            // a member of a module type that is only found on an external base class
            externalMethodRefs++;
        }
        catch (Exception ex)
        {
            bodyErrors.Add($"{label}@{ins.Offset:x4}: resolve {element.FullName} threw {ex.GetType().Name}");
        }
    }

    // an array of rank n carries .ctor/Get/Address with n int32 args and Set with the
    // n indices plus the element value, all generated by the runtime; they only need
    // the element type to be resolvable
    private static bool IsWellFormedArrayMember(ArrayType at, MethodReference element)
    {
        if (at.Rank < 1) return false;
        if (element.Name is not (".ctor" or "Get" or "Set" or "Address")) return false;
        int expected = element.Name == "Set" ? at.Rank + 1 : at.Rank;
        if (element.Parameters.Count != expected) return false;
        for (int i = 0; i < at.Rank; i++)
            if (element.Parameters[i].ParameterType.MetadataType != MetadataType.Int32) return false;
        if (element.Name == "Set" && element.Parameters[at.Rank].ParameterType.FullName != at.ElementType.FullName) return false;
        // no resolver: an external or generic element type cannot be resolved, the shape is all we have
        var elem = Element(at.ElementType);
        if (elem.IsGenericParameter || IsExternal(at.ElementType)) return true;
        try { return at.ElementType.Resolve() != null; }
        catch { return false; }
    }

    private void ValidateFieldRef(string label, Instruction ins, FieldReference fr)
    {
        var scope = ScopeName(fr.DeclaringType);
        if (scope != null && (scope.StartsWith("Fit43", StringComparison.Ordinal) ||
                              scope.StartsWith("System.Private.CoreLib", StringComparison.Ordinal)))
        {
            bodyErrors.Add($"{label}@{ins.Offset:x4}: forbidden field scope {scope} for {fr.FullName}");
            return;
        }
        if (SkipExternal(label, ins, fr.DeclaringType, ref externalFieldRefs)) return;
        try
        {
            if (fr.Resolve() == null)
                bodyErrors.Add($"{label}@{ins.Offset:x4}: cannot resolve field {fr.FullName}");
        }
        catch (AssemblyResolutionException)
        {
            externalFieldRefs++;
        }
        catch (Exception ex)
        {
            bodyErrors.Add($"{label}@{ins.Offset:x4}: resolve field {fr.FullName} threw {ex.GetType().Name}");
        }
    }

    private void ValidateTypeRef(string label, Instruction ins, TypeReference tr)
    {
        var element = tr is GenericInstanceType git ? git.ElementType : tr;
        var scope = ScopeName(element);
        if (scope != null && (scope.StartsWith("Fit43", StringComparison.Ordinal) ||
                              scope.StartsWith("System.Private.CoreLib", StringComparison.Ordinal)))
        {
            bodyErrors.Add($"{label}@{ins.Offset:x4}: forbidden type scope {scope} for {tr.FullName}");
            return;
        }
        if (tr.IsGenericParameter || tr.MetadataType == MetadataType.Void) return;
        if (SkipExternal(label, ins, element, ref externalTypeRefs)) return;
        try
        {
            if (element.Resolve() == null)
                bodyErrors.Add($"{label}@{ins.Offset:x4}: cannot resolve type {tr.FullName}");
        }
        catch (AssemblyResolutionException)
        {
            externalTypeRefs++;
        }
        catch (Exception ex)
        {
            bodyErrors.Add($"{label}@{ins.Offset:x4}: resolve type {tr.FullName} threw {ex.GetType().Name}");
        }
    }

    private void ComputeStack(string label, MethodDefinition method)
    {
        var body = method.Body;
        var heights = new Dictionary<Instruction, int>();
        var queue = new Queue<Instruction>();
        var maxStack = 0;

        void Seed(Instruction? ins, int height)
        {
            if (ins == null || !instructionSet.Contains(ins)) return;
            if (heights.TryGetValue(ins, out var existing))
            {
                if (existing != height)
                    bodyErrors.Add($"{label}@{ins.Offset:x4}: stack merge {existing} vs {height}");
                return;
            }
            heights[ins] = height;
            queue.Enqueue(ins);
        }

        Seed(body.Instructions[0], 0);
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.HandlerType == ExceptionHandlerType.Finally || h.HandlerType == ExceptionHandlerType.Fault)
                Seed(h.HandlerStart, 0);
            else
                Seed(h.HandlerStart, 1);
            if (h.FilterStart != null) Seed(h.FilterStart, 1);
        }

        while (queue.Count > 0)
        {
            var ins = queue.Dequeue();
            int height = heights[ins];
            var (pop, push) = StackEffect(ins, height);
            if (pop < 0)
            {
                bodyErrors.Add($"{label}@{ins.Offset:x4}: unsupported stack behaviour {ins.OpCode.StackBehaviourPop}");
                continue;
            }
            if (height < pop)
            {
                bodyErrors.Add($"{label}@{ins.Offset:x4}: {ins.OpCode} needs {pop} stack slot(s), has {height}");
                continue;
            }

            int after = height - pop + push;
            maxStack = Math.Max(maxStack, Math.Max(height, after));

            switch (ins.OpCode.Code)
            {
                case Code.Ret:
                {
                    int expected = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                    if (height != expected)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: ret with {height} stack slot(s), expected {expected}");
                    break;
                }
                case Code.Endfilter:
                    if (height != 1)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: endfilter with {height} stack slot(s), expected 1");
                    break;
                case Code.Endfinally:
                    if (height != 0)
                        bodyErrors.Add($"{label}@{ins.Offset:x4}: endfinally with {height} stack slot(s), expected 0");
                    break;
            }

            foreach (var next in Successors(ins))
                Seed(next, next == ins.Operand && ins.OpCode.Code is Code.Leave or Code.Leave_S ? 0 : after);
        }

        if (maxStack > body.MaxStackSize)
            bodyErrors.Add($"{label}: computed max stack {maxStack} exceeds declared {body.MaxStackSize}");

        MaxComputedStack = Math.Max(MaxComputedStack, maxStack);
        MaxDeclaredStack = Math.Max(MaxDeclaredStack, body.MaxStackSize);
    }

    private static IEnumerable<Instruction> Successors(Instruction ins)
    {
        var code = ins.OpCode.Code;
        switch (code)
        {
            case Code.Ret:
            case Code.Throw:
            case Code.Rethrow:
            case Code.Endfinally:
            case Code.Endfilter:
            case Code.Jmp:
                yield break;
        }

        bool unconditional = code is Code.Br or Code.Br_S or Code.Leave or Code.Leave_S;
        bool conditional = ins.OpCode.FlowControl == FlowControl.Cond_Branch && code != Code.Switch;

        if (code == Code.Switch)
        {
            if (ins.Operand is Instruction[] targets)
                foreach (var t in targets) yield return t;
            if (ins.Next != null) yield return ins.Next;
            yield break;
        }

        if (unconditional)
        {
            if (ins.Operand is Instruction target) yield return target;
            yield break;
        }

        if (conditional && ins.Operand is Instruction ctarget)
            yield return ctarget;

        if (ins.Next != null) yield return ins.Next;
    }

    private static (int pop, int push) StackEffect(Instruction ins, int currentHeight)
    {
        var code = ins.OpCode.Code;
        switch (code)
        {
            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
            {
                if (ins.Operand is not MethodReference mr) return (-1, 0);
                int args = mr.Parameters.Count;
                int pop = args + (code == Code.Newobj ? 0 : mr.HasThis ? 1 : 0);
                int push = code == Code.Newobj || mr.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
                return (pop, push);
            }
            case Code.Calli:
            {
                if (ins.Operand is not CallSite site) return (-1, 0);
                int pop = site.Parameters.Count + (site.HasThis ? 1 : 0) + 1;
                int push = site.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                return (pop, push);
            }
            case Code.Ret:
                return (0, 0);
            case Code.Endfilter:
                return (1, 0);
            case Code.Pop:
                return (1, 0);
            case Code.Dup:
                return (1, 2);
        }

        int popCount = PopCount(ins.OpCode.StackBehaviourPop);
        if (popCount == int.MaxValue) return (-1, 0);
        if (ins.OpCode.StackBehaviourPop == StackBehaviour.PopAll) popCount = currentHeight;
        int pushCount = PushCount(ins.OpCode.StackBehaviourPush);
        if (pushCount == int.MaxValue) return (-1, 0);
        return (popCount, pushCount);
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 => 1,
        StackBehaviour.Pop1_pop1 => 2,
        StackBehaviour.Popi => 1,
        StackBehaviour.Popi_pop1 => 2,
        StackBehaviour.Popi_popi => 2,
        StackBehaviour.Popi_popi8 => 2,
        StackBehaviour.Popi_popi_popi => 3,
        StackBehaviour.Popi_popr4 => 2,
        StackBehaviour.Popi_popr8 => 2,
        StackBehaviour.Popref => 1,
        StackBehaviour.Popref_pop1 => 2,
        StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popref_popi_popi => 3,
        StackBehaviour.Popref_popi_popi8 => 3,
        StackBehaviour.Popref_popi_popr4 => 3,
        StackBehaviour.Popref_popi_popr8 => 3,
        StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.PopAll => int.MaxValue - 1,
        StackBehaviour.Varpop => int.MaxValue,
        _ => int.MaxValue,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 => 1,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Pushi => 1,
        StackBehaviour.Pushi8 => 1,
        StackBehaviour.Pushr4 => 1,
        StackBehaviour.Pushr8 => 1,
        StackBehaviour.Pushref => 1,
        _ => int.MaxValue,
    };

    private static bool IsShortBranch(OpCode op) => op.OperandType == OperandType.ShortInlineBrTarget;

    internal static string? ScopeName(TypeReference type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition m => m.Name,
            ModuleReference mr => mr.Name,
            _ => null,
        };
    }
}
