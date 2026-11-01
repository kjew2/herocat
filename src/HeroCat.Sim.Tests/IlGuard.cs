using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace HeroCat.Sim.Tests
{
    /// <summary>
    /// 00 contract 1 as a scan of compiled IL (10 section 1.1, issue #139). It walks every method body of the chosen types
    /// and reports: any float (System.Single) in a signature, local or opcode; calls to Math.Pow/Exp/Log*; the wall clock
    /// (DateTime/DateTimeOffset Now/UtcNow/Today); System.Random, MathF, Stopwatch; UnityEngine, JSON and reflection APIs.
    /// Built on System.Reflection.Metadata (in the SDK) instead of Mono.Cecil, so the test project needs no new package.
    /// </summary>
    internal static class IlGuard
    {
        static readonly string[] BannedTypes =
        {
            "System.Single", "System.MathF", "System.Random", "System.Diagnostics.Stopwatch", "System.Activator",
        };

        static readonly string[] BannedNamespaces = { "UnityEngine", "Newtonsoft", "System.Text.Json", "System.Reflection", "System.IO", "System.Net" };

        static readonly (string Type, string[] Members)[] BannedMembers =
        {
            ("System.Math", new[] { "Pow", "Exp", "Log", "Log10", "Log2", "Cbrt" }),
            ("System.DateTime", new[] { "get_Now", "get_UtcNow", "get_Today" }),
            ("System.DateTimeOffset", new[] { "get_Now", "get_UtcNow" }),
            ("System.Environment", new[] { "get_TickCount", "get_TickCount64" }),
            // reflection through System.Type; typeof (GetTypeFromHandle) and record equality are fine
            ("System.Type", new[] { "GetType", "GetMethod", "GetMethods", "GetProperty", "GetProperties", "GetField", "GetFields",
                                    "GetMember", "GetMembers", "InvokeMember", "GetConstructor", "GetConstructors" }),
        };

        /// <summary>Reflection that reads only a type's name (ContentDb's E021 messages): deterministic, allowed.</summary>
        const string AllowedReflectionType = "System.Reflection.MemberInfo";
        static readonly string[] AllowedReflectionMembers = { "get_Name" };

        static readonly Dictionary<short, OperandType> Operands = BuildOperandTable();

        static Dictionary<short, OperandType> BuildOperandTable()
        {
            var t = new Dictionary<short, OperandType>();
            foreach (var f in typeof(OpCodes).GetFields())
                if (f.GetValue(null) is OpCode op) t[op.Value] = op.OperandType;
            return t;
        }

        /// <summary>Violations as "Type::Method: what", for the types <paramref name="include"/> accepts (by full name).</summary>
        public static List<string> Scan(string assemblyPath, Func<string, bool> include, bool assemblyWideTypeRefs)
        {
            var hits = new List<string>();
            using var fs = File.OpenRead(assemblyPath);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            var floats = new FloatFinder();

            if (assemblyWideTypeRefs)
                foreach (var h in md.TypeReferences)
                {
                    string full = TypeName(md, h);
                    if (full == AllowedReflectionType) continue;   // its members are checked call by call below
                    string why = BannedType(full);
                    if (why != null) hits.Add("<assembly>: " + why);
                }

            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                string typeName = TypeDefName(md, th);
                if (!include(typeName)) continue;

                foreach (var fh in td.GetFields())
                    if (md.GetFieldDefinition(fh).DecodeSignature(floats, null))
                        hits.Add($"{typeName}::{md.GetString(md.GetFieldDefinition(fh).Name)}: float field");

                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    string where = typeName + "::" + md.GetString(m.Name);
                    var sig = m.DecodeSignature(floats, null);
                    if (sig.ReturnType) hits.Add(where + ": float return");
                    foreach (var p in sig.ParameterTypes) if (p) { hits.Add(where + ": float parameter"); break; }
                    if (m.RelativeVirtualAddress == 0) continue;
                    var body = pe.GetMethodBody(m.RelativeVirtualAddress);
                    if (!body.LocalSignature.IsNil)
                    {
                        var locals = md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(floats, null);
                        foreach (var l in locals) if (l) { hits.Add(where + ": float local"); break; }
                    }
                    ScanIl(md, body.GetILReader(), where, floats, hits);
                }
            }
            return hits;
        }

        static void ScanIl(MetadataReader md, BlobReader il, string where, FloatFinder floats, List<string> hits)
        {
            while (il.RemainingBytes > 0)
            {
                short code = il.ReadByte();
                if (code == 0xFE) code = unchecked((short)(0xFE00 | il.ReadByte()));
                var op = (ILOpCode)(ushort)code;
                switch (op)
                {
                    case ILOpCode.Ldc_r4: case ILOpCode.Conv_r4: case ILOpCode.Ldind_r4: case ILOpCode.Stind_r4:
                    case ILOpCode.Ldelem_r4: case ILOpCode.Stelem_r4:
                        hits.Add($"{where}: float opcode {op}");
                        break;
                }
                if (!Operands.TryGetValue(code, out var kind)) throw new InvalidDataException($"{where}: unknown opcode 0x{code:X}");
                switch (kind)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: il.Offset += 1; break;
                    case OperandType.InlineVar: il.Offset += 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: il.Offset += 8; break;
                    case OperandType.InlineSwitch: { int n = il.ReadInt32(); il.Offset += 4 * n; break; }
                    case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineTok: case OperandType.InlineType:
                    {
                        string why = BannedToken(md, MetadataTokens.EntityHandle(il.ReadInt32()), floats);
                        if (why != null) hits.Add(where + ": " + why);
                        break;
                    }
                    default: il.Offset += 4; break;   // InlineI, InlineBrTarget, InlineSig, InlineString, ShortInlineR
                }
            }
        }

        static string BannedToken(MetadataReader md, EntityHandle h, FloatFinder floats)
        {
            switch (h.Kind)
            {
                case HandleKind.MethodSpecification:
                    return BannedToken(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method, floats);
                case HandleKind.MemberReference:
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)h);
                    string owner = OwnerName(md, mr.Parent);
                    string name = md.GetString(mr.Name);
                    bool allowed = owner == AllowedReflectionType && Array.IndexOf(AllowedReflectionMembers, name) >= 0;
                    string why = allowed ? null : BannedType(owner);
                    if (why != null) return why + "::" + name;
                    foreach (var (type, members) in BannedMembers)
                        if (owner == type && Array.IndexOf(members, name) >= 0) return $"banned call {owner}::{name}";
                    bool isFloat = mr.GetKind() == MemberReferenceKind.Method
                        ? HasFloat(mr.DecodeMethodSignature(floats, null))
                        : mr.DecodeFieldSignature(floats, null);
                    return isFloat ? $"float in signature of {owner}::{name}" : null;
                }
                case HandleKind.TypeReference: return BannedType(TypeName(md, (TypeReferenceHandle)h));
                case HandleKind.TypeSpecification:
                    return md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(floats, null) ? "float type spec" : null;
                default: return null;   // definitions inside the scanned assembly are scanned on their own
            }
        }

        static bool HasFloat(MethodSignature<bool> s)
        {
            if (s.ReturnType) return true;
            foreach (var p in s.ParameterTypes) if (p) return true;
            return false;
        }

        static string BannedType(string full)
        {
            if (full == null) return null;
            if (Array.IndexOf(BannedTypes, full) >= 0) return "banned type " + full;
            foreach (var ns in BannedNamespaces)
                if (full.StartsWith(ns + ".", StringComparison.Ordinal) && !full.EndsWith("Attribute", StringComparison.Ordinal))
                    return "banned namespace " + full;
            return null;
        }

        static string OwnerName(MetadataReader md, EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeReference: return TypeName(md, (TypeReferenceHandle)parent);
                case HandleKind.TypeDefinition: return TypeDefName(md, (TypeDefinitionHandle)parent);
                case HandleKind.TypeSpecification:
                {
                    // a generic instantiation: name its generic type definition
                    var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                    blob.ReadSignatureTypeCode();
                    var gh = blob.ReadTypeHandle();
                    return gh.Kind == HandleKind.TypeReference ? TypeName(md, (TypeReferenceHandle)gh)
                         : gh.Kind == HandleKind.TypeDefinition ? TypeDefName(md, (TypeDefinitionHandle)gh) : null;
                }
                default: return null;
            }
        }

        static string TypeName(MetadataReader md, TypeReferenceHandle h)
        {
            var tr = md.GetTypeReference(h);
            string name = md.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                return TypeName(md, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
            string ns = md.GetString(tr.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        static string TypeDefName(MetadataReader md, TypeDefinitionHandle h)
        {
            var td = md.GetTypeDefinition(h);
            string name = md.GetString(td.Name);
            var outer = td.GetDeclaringType();
            if (!outer.IsNil) return TypeDefName(md, outer) + "+" + name;
            string ns = md.GetString(td.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// <summary>Decodes a signature to "does it mention float32 anywhere".</summary>
        sealed class FloatFinder : ISignatureTypeProvider<bool, object>
        {
            public bool GetPrimitiveType(PrimitiveTypeCode c) => c == PrimitiveTypeCode.Single;
            public bool GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => false;
            public bool GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => TypeName(r, h) == "System.Single";
            public bool GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
            public bool GetSZArrayType(bool e) => e;
            public bool GetArrayType(bool e, ArrayShape s) => e;
            public bool GetByReferenceType(bool e) => e;
            public bool GetPointerType(bool e) => e;
            public bool GetPinnedType(bool e) => e;
            public bool GetGenericInstantiation(bool g, ImmutableArray<bool> args) { if (g) return true; foreach (var a in args) if (a) return true; return false; }
            public bool GetGenericMethodParameter(object c, int i) => false;
            public bool GetGenericTypeParameter(object c, int i) => false;
            public bool GetFunctionPointerType(MethodSignature<bool> s) => HasFloat(s);
            public bool GetModifiedType(bool m, bool u, bool req) => u;
        }
    }
}
