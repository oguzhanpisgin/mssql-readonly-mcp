using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MssqlReadOnlyMcp.IntegrationTests;

/// <summary>
/// Writes, without a compiler, a .NET Framework 4 assembly holding one CLR user-defined type, so the fixture can
/// create a user assembly and a CLR type in SQL Server (the permission check asks rights on both). The assembly is
/// written from code, not checked in as a binary. The type is <c>Pt</c>, a native-format struct:
/// <code>
/// [Serializable, SqlUserDefinedType(Format.Native)]
/// public struct Pt : INullable
/// {
///     public int X;
///     private bool n;
///     public bool IsNull => n;
///     public static Pt Null => new Pt { n = true };
///     public static Pt Parse(SqlString s) => new Pt { X = 21 };
///     public override string ToString() => "Pt";
///     public int Twice() => X * 2;
/// }
/// </code>
/// </summary>
internal static class ClrTestAssembly
{
    /// <summary>The CLR type name inside the assembly.</summary>
    public const string TypeName = "Pt";

    // The public key token of mscorlib and System.Data (b77a5c561934e089).
    private static readonly byte[] FrameworkKeyToken = [0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89];

    /// <summary>The assembly's bytes. The same name always gives the same bytes.</summary>
    public static byte[] Build(string assemblyName)
    {
        var metadata = new MetadataBuilder();
        var il = new BlobBuilder();
        var bodies = new MethodBodyStreamEncoder(il);
        var mvid = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(assemblyName)).AsSpan(0, 16));

        metadata.AddModule(0, metadata.GetOrAddString(assemblyName + ".dll"), metadata.GetOrAddGuid(mvid), default, default);
        metadata.AddAssembly(
            metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);

        var token = metadata.GetOrAddBlob(FrameworkKeyToken);
        var mscorlib = metadata.AddAssemblyReference(
            metadata.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default, token, default, default);
        var systemData = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Data"), new Version(4, 0, 0, 0), default, token, default, default);

        TypeReferenceHandle Reference(AssemblyReferenceHandle scope, string ns, string name) =>
            metadata.AddTypeReference(scope, metadata.GetOrAddString(ns), metadata.GetOrAddString(name));

        var valueType = Reference(mscorlib, "System", "ValueType");
        var nullable = Reference(systemData, "System.Data.SqlTypes", "INullable");
        var sqlString = Reference(systemData, "System.Data.SqlTypes", "SqlString");
        var udtAttribute = Reference(systemData, "Microsoft.SqlServer.Server", "SqlUserDefinedTypeAttribute");
        var format = Reference(systemData, "Microsoft.SqlServer.Server", "Format");

        // SqlUserDefinedTypeAttribute(Format format)
        var attributeCtor = metadata.AddMemberReference(
            udtAttribute,
            metadata.GetOrAddString(".ctor"),
            Blob(b => new BlobEncoder(b).MethodSignature(isInstanceMethod: true)
                .Parameters(1, r => r.Void(), p => p.AddParameter().Type().Type(format, isValueType: true))));

        // <Module> comes first; the struct's fields and methods follow it.
        metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var x = metadata.AddFieldDefinition(
            FieldAttributes.Public, metadata.GetOrAddString("X"), Blob(b => new BlobEncoder(b).Field().Type().Int32()));
        var n = metadata.AddFieldDefinition(
            FieldAttributes.Private, metadata.GetOrAddString("n"), Blob(b => new BlobEncoder(b).Field().Type().Boolean()));

        // The struct's own handle is known before it is added: it is the second type definition.
        var pt = MetadataTokens.TypeDefinitionHandle(2);
        var locals = metadata.AddStandaloneSignature(
            Blob(b => new BlobEncoder(b).LocalVariableSignature(1).AddVariable().Type().Type(pt, isValueType: true)));

        int Body(Action<InstructionEncoder> emit, bool withLocal)
        {
            var encoder = new InstructionEncoder(new BlobBuilder());
            emit(encoder);
            return bodies.AddMethodBody(encoder, maxStack: 8, localVariablesSignature: withLocal ? locals : default);
        }

        const MethodAttributes PublicStatic = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
        var firstMethod = MetadataTokens.MethodDefinitionHandle(metadata.GetRowCount(TableIndex.MethodDef) + 1);
        var firstParameter = MetadataTokens.ParameterHandle(1);

        var getIsNull = metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Virtual |
                MethodAttributes.Final | MethodAttributes.NewSlot,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("get_IsNull"),
            Blob(b => new BlobEncoder(b).MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Type().Boolean(), _ => { })),
            Body(e => { e.LoadArgument(0); e.OpCode(ILOpCode.Ldfld); e.Token(n); e.OpCode(ILOpCode.Ret); }, withLocal: false),
            firstParameter);

        var getNull = metadata.AddMethodDefinition(
            PublicStatic | MethodAttributes.SpecialName,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("get_Null"),
            Blob(b => new BlobEncoder(b).MethodSignature().Parameters(0, r => r.Type().Type(pt, isValueType: true), _ => { })),
            Body(
                e =>
                {
                    e.LoadLocalAddress(0); e.OpCode(ILOpCode.Initobj); e.Token(pt);
                    e.LoadLocalAddress(0); e.LoadConstantI4(1); e.OpCode(ILOpCode.Stfld); e.Token(n);
                    e.LoadLocal(0); e.OpCode(ILOpCode.Ret);
                },
                withLocal: true),
            firstParameter);

        metadata.AddMethodDefinition(
            PublicStatic,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Parse"),
            Blob(b => new BlobEncoder(b).MethodSignature().Parameters(
                1, r => r.Type().Type(pt, isValueType: true), p => p.AddParameter().Type().Type(sqlString, isValueType: true))),
            Body(
                e =>
                {
                    e.LoadLocalAddress(0); e.OpCode(ILOpCode.Initobj); e.Token(pt);
                    e.LoadLocalAddress(0); e.LoadConstantI4(21); e.OpCode(ILOpCode.Stfld); e.Token(x);
                    e.LoadLocal(0); e.OpCode(ILOpCode.Ret);
                },
                withLocal: true),
            firstParameter);
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("s"), 1);

        var afterParse = MetadataTokens.ParameterHandle(2);
        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("ToString"),
            Blob(b => new BlobEncoder(b).MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Type().String(), _ => { })),
            Body(e => { e.LoadString(metadata.GetOrAddUserString(TypeName)); e.OpCode(ILOpCode.Ret); }, withLocal: false),
            afterParse);

        metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Twice"),
            Blob(b => new BlobEncoder(b).MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Type().Int32(), _ => { })),
            Body(
                e => { e.LoadArgument(0); e.OpCode(ILOpCode.Ldfld); e.Token(x); e.LoadConstantI4(2); e.OpCode(ILOpCode.Mul); e.OpCode(ILOpCode.Ret); },
                withLocal: false),
            afterParse);

        // SQL Server requires [Serializable] on a user-defined type; the flag is obsolete only for .NET itself.
#pragma warning disable SYSLIB0050
        const TypeAttributes Serializable = TypeAttributes.Serializable;
#pragma warning restore SYSLIB0050
        var type = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | Serializable |
                TypeAttributes.BeforeFieldInit,
            default,
            metadata.GetOrAddString(TypeName),
            valueType,
            x,
            firstMethod);
        metadata.AddInterfaceImplementation(type, nullable);
        metadata.AddCustomAttribute(type, attributeCtor, Blob(b =>
        {
            b.WriteUInt16(1); // prolog
            b.WriteInt32(1); // Format.Native
            b.WriteUInt16(0); // no named arguments
        }));

        metadata.AddPropertyMap(type, MetadataTokens.PropertyDefinitionHandle(1));
        var isNullProperty = metadata.AddProperty(
            PropertyAttributes.None,
            metadata.GetOrAddString("IsNull"),
            Blob(b => new BlobEncoder(b).PropertySignature(isInstanceProperty: true).Parameters(0, r => r.Type().Boolean(), _ => { })));
        metadata.AddMethodSemantics(isNullProperty, MethodSemanticsAttributes.Getter, getIsNull);
        var nullProperty = metadata.AddProperty(
            PropertyAttributes.None,
            metadata.GetOrAddString("Null"),
            Blob(b => new BlobEncoder(b).PropertySignature().Parameters(0, r => r.Type().Type(pt, isValueType: true), _ => { })));
        metadata.AddMethodSemantics(nullProperty, MethodSemanticsAttributes.Getter, getNull);

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            il,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: _ => new BlobContentId(mvid, 0x5A000000));
        var output = new BlobBuilder();
        pe.Serialize(output);
        return output.ToArray();

        BlobHandle Blob(Action<BlobBuilder> write)
        {
            var builder = new BlobBuilder();
            write(builder);
            return metadata.GetOrAddBlob(builder);
        }
    }
}
