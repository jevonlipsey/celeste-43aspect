using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// a tiny mono/.net assembly inspector: reads the metadata table directly, so it works on
// assemblies this machine cannot load (wine-mono builds, x86 assemblies on arm, ...).
//
// usage:
//   mdinfo types <assembly> <typeNameFilter>   list the methods of matching types
//   mdinfo il    <assembly> <typeNameFilter>   disassemble those methods
//   mdinfo vis   <assembly> <typeNameFilter>   print each type's TypeAttributes visibility
//                                              (0 = not public, 1 = public, 2 = nested public,
//                                               3 = nested private, 5 = nested assembly/internal)

string mode = args[0];
string path = args[1];
string filter = args.Length > 2 ? args[2] : "";

using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var ad = md.GetAssemblyDefinition();
Console.WriteLine($"== {Path.GetFileName(path)} [assembly: {md.GetString(ad.Name)} v{ad.Version}]");

var opcodes = new Dictionary<int, OpCode>();
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
    var oc = (OpCode)f.GetValue(null)!;
    opcodes[oc.Value & 0xFFFF] = oc;
}

foreach (var th in md.TypeDefinitions) {
    var td = md.GetTypeDefinition(th);
    string ns = md.GetString(td.Namespace);
    string tn = md.GetString(td.Name);
    if (!tn.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

    string full = TypeName(td);
    if (mode == "vis") {
        Console.WriteLine($"{(int)td.Attributes & 7}\t{full}");
        continue;
    }
    foreach (var mh in td.GetMethods()) {
        var m = md.GetMethodDefinition(mh);
        string name = md.GetString(m.Name);
        var sig = md.GetBlobReader(m.Signature);
        sig.ReadSignatureHeader();
        int pc = sig.ReadCompressedInteger();
        string ret = PrimType(sig.ReadByte());

        if (mode == "types") {
            Console.WriteLine($"   {ret,8} {full}::{name}({pc})");
            continue;
        }

        Console.WriteLine($"### {full}::{name} -> {ret} params={pc}");
        int rva = m.RelativeVirtualAddress;
        if (rva == 0) {
            Console.WriteLine("   (no body)");
            continue;
        }
        Disasm(pe.GetMethodBody(rva).GetILBytes()!, md, opcodes);
    }
}

return;

string TypeName(TypeDefinition td) {
    var d = td.GetDeclaringType();
    string n = md.GetString(td.Namespace) + "." + md.GetString(td.Name);
    if (d.IsNil) return n;
    return TypeName(md.GetTypeDefinition(d)) + "/" + md.GetString(td.Name);
}

string PrimType(byte b) => b switch {
    0x01 => "void",
    0x02 => "bool",
    0x03 => "char",
    0x04 => "sbyte",
    0x05 => "byte",
    0x06 => "short",
    0x07 => "ushort",
    0x08 => "int",
    0x09 => "uint",
    0x0a => "long",
    0x0b => "ulong",
    0x0c => "float",
    0x0d => "double",
    0x0e => "string",
    0x0f => "IntPtr",
    0x10 => "UIntPtr",
    0x11 => "valuetype",
    0x12 => "class",
    0x1c => "object",
    _ => $"0x{b:x2}"
};

void Disasm(byte[] il, MetadataReader md, Dictionary<int, OpCode> opcodes) {
    int i = 0;
    while (i < il.Length) {
        int start = i;
        int b = il[i++];
        int key = b;
        if (b == 0xFE) key = 0xFE00 | il[i++];
        if (!opcodes.TryGetValue(key, out var oc)) {
            Console.WriteLine($"   IL_{start:x4}: <unknown 0x{key:x}>");
            break;
        }
        string operand = "";
        switch (oc.OperandType) {
            case OperandType.ShortInlineI:
                operand = ((sbyte)il[i]).ToString();
                i += 1;
                break;
            case OperandType.InlineI:
                operand = BitConverter.ToInt32(il, i).ToString();
                i += 4;
                break;
            case OperandType.InlineI8:
                operand = BitConverter.ToInt64(il, i).ToString();
                i += 8;
                break;
            case OperandType.ShortInlineR:
                operand = BitConverter.ToSingle(il, i).ToString("R");
                i += 4;
                break;
            case OperandType.InlineR:
                operand = BitConverter.ToDouble(il, i).ToString("R");
                i += 8;
                break;
            case OperandType.ShortInlineVar:
                operand = il[i].ToString();
                i += 1;
                break;
            case OperandType.InlineVar:
                operand = BitConverter.ToUInt16(il, i).ToString();
                i += 2;
                break;
            case OperandType.ShortInlineBrTarget: {
                int d = (sbyte)il[i];
                i += 1;
                operand = $"IL_{i + d:x4}";
                break;
            }
            case OperandType.InlineBrTarget: {
                int d = BitConverter.ToInt32(il, i);
                i += 4;
                operand = $"IL_{i + d:x4}";
                break;
            }
            case OperandType.InlineSwitch: {
                int n = BitConverter.ToInt32(il, i);
                i += 4;
                int baseOff = i + n * 4;
                var parts = new List<string>();
                for (int k = 0; k < n; k++) {
                    parts.Add($"IL_{baseOff + BitConverter.ToInt32(il, i):x4}");
                    i += 4;
                }
                operand = string.Join(", ", parts);
                break;
            }
            case OperandType.InlineString: {
                int tok = BitConverter.ToInt32(il, i);
                i += 4;
                operand = $"\"{md.GetUserString(MetadataTokens.UserStringHandle(tok))}\"";
                break;
            }
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineTok:
            case OperandType.InlineType: {
                int tok = BitConverter.ToInt32(il, i);
                i += 4;
                operand = TokName(tok, md);
                break;
            }
        }
        Console.WriteLine($"   IL_{start:x4}: {oc.Name,-14} {operand}");
    }
}

string TokName(int tok, MetadataReader md) {
    var h = MetadataTokens.EntityHandle(tok);
    try {
        switch (h.Kind) {
            case HandleKind.MemberReference: {
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                return $"{ParentName(mr.Parent, md)}::{md.GetString(mr.Name)}";
            }
            case HandleKind.MethodDefinition: {
                var m2 = md.GetMethodDefinition((MethodDefinitionHandle)h);
                var td2 = md.GetTypeDefinition(m2.GetDeclaringType());
                return $"{md.GetString(td2.Namespace)}.{md.GetString(td2.Name)}::{md.GetString(m2.Name)}";
            }
            case HandleKind.FieldDefinition: {
                var f2 = md.GetFieldDefinition((FieldDefinitionHandle)h);
                var td2 = md.GetTypeDefinition(f2.GetDeclaringType());
                return $"{md.GetString(td2.Namespace)}.{md.GetString(td2.Name)}::{md.GetString(f2.Name)}";
            }
            case HandleKind.TypeReference:
                return md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
            case HandleKind.TypeDefinition:
                return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
            case HandleKind.TypeSpecification:
                return "<typespec>";
            case HandleKind.MethodSpecification: {
                var ms = md.GetMethodSpecification((MethodSpecificationHandle)h);
                return TokName(MetadataTokens.GetToken(ms.Method), md) + "<gen>";
            }
            case HandleKind.StandaloneSignature:
                return "<sig>";
            default:
                return h.Kind.ToString();
        }
    } catch (Exception e) {
        return $"<err {e.Message}>";
    }
}

string ParentName(EntityHandle h, MetadataReader md) {
    switch (h.Kind) {
        case HandleKind.TypeReference:
            return md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
        case HandleKind.TypeDefinition:
            return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
        case HandleKind.TypeSpecification:
            return "<typespec>";
        default:
            return h.Kind.ToString();
    }
}
