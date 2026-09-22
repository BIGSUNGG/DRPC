using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Metadata;

namespace DRPC.CodeGenerator.Emitter;

/// <summary>
/// Generic procedure emission. The first 4 bytes of the payload = instantiation index
/// (the [GenericProcedure] Cartesian-product order). Stubs pick an instantiation via a
/// typeof(T) chain and cast into the closed helper; undeclared type arguments fall through to
/// a final throw (runtime backstop), while call sites are caught at compile time by the
/// DRPCGEN008 analyzer.
/// </summary>
internal static partial class RpcHubEmitter
{
    static string TypeParameterList(GenericMethodMetadata generic)
        => $"<{string.Join(", ", generic.TypeParameterNames)}>";

    static string ClosedTypeArgumentList(ITypeSymbol[] instantiation)
        => string.Join(", ", instantiation.Select(static t => t.ToDisplayString(RpcPayload.Qualified)));

    /// <summary>Casts a stub argument to the closed type argument. Parameters that do not use the type parameter pass through unchanged.</summary>
    static string CastArgument(ITypeSymbol original, ITypeSymbol closed, string name)
        => Microsoft.CodeAnalysis.SymbolEqualityComparer.Default.Equals(original, closed)
            ? name
            : $"(({closed.ToDisplayString(RpcPayload.Qualified)})(object){name}!)";

    static void EmitGenericOutgoing(StringBuilder sb, MethodMetadata method, string indent)
    {
        GenericMethodMetadata generic = method.Generic!;
        string typeParams = TypeParameterList(generic);
        string asyncReturn = method.IsVoidReturn || method.OneWay
            ? "global::System.Threading.Tasks.Task"
            : $"global::System.Threading.Tasks.Task<{method.ReturnTypeDisplay}>";

        // Round-trip calls take a caller cancellation token (optional last parameter).
        // One-way sends have no wait, so they omit it.
        string ctParam = method.OneWay ? "" : CancellationTokenParam(method);

        sb.AppendLine($"{indent}public async {asyncReturn} {method.MethodName}Async{typeParams}({method.ParameterDeclarationList()}{ctParam})");
        sb.AppendLine($"{indent}{{");

        for (int i = 0; i < generic.Instantiations.Count; i++)
        {
            ITypeSymbol[] instantiation = generic.Instantiations[i];
            string condition = string.Join(" && ", generic.TypeParameterNames.Select((name, k) =>
                $"typeof({name}) == typeof({instantiation[k].ToDisplayString(RpcPayload.Qualified)})"));
            string keyword = i == 0 ? "if" : "else if";

            sb.AppendLine($"{indent}    {keyword} ({condition})");
            sb.AppendLine($"{indent}    {{");

            string writeArgs = string.Join(", ", method.Parameters.Select((p, j) =>
                CastArgument(p.Type, generic.ClosedMethods[i].Parameters[j].Type, p.Name)));
            sb.AppendLine($"{indent}        byte[] __payload = {WriteParams(method)}_{i}({writeArgs});");

            if (method.OneWay)
            {
                sb.AppendLine($"{indent}        await SendRPC({method.MethodId}, __payload, {method.ModeExpression}).ConfigureAwait(false);");
            }
            else if (method.IsVoidReturn)
            {
                sb.AppendLine($"{indent}        await RequestRPC({method.MethodId}, __payload, {method.ModeExpression}{TimeoutArg(method)}, cancellationToken).ConfigureAwait(false);");
            }
            else
            {
                sb.AppendLine($"{indent}        byte[] __response = await RequestRPC({method.MethodId}, __payload, {method.ModeExpression}{TimeoutArg(method)}, cancellationToken).ConfigureAwait(false);");
                sb.AppendLine($"{indent}        return ({method.ReturnTypeDisplay})(object){ReadReturn(method)}_{i}(__response);");
            }

            sb.AppendLine($"{indent}    }}");
        }

        string observed = string.Join(" + ", generic.TypeParameterNames.Select(n => $"typeof({n}).FullName"));
        sb.AppendLine($"{indent}    else");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(\"{method.MethodName}: type argument combination [\" + {observed} + \"] is not declared with [GenericProcedure].\");");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    static void EmitGenericIncoming(StringBuilder sb, MethodMetadata method, string indent)
    {
        GenericMethodMetadata generic = method.Generic!;

        sb.AppendLine($"{indent}private async global::System.Threading.Tasks.Task<byte[]> {method.MethodName}_Requested(byte[] __parameterData)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __rd = new global::MessageProtocol.Serialize.MessageBufferReader(__parameterData);");
        sb.AppendLine($"{indent}    int __ci = __rd.ReadInt32();");
        sb.AppendLine($"{indent}    switch (__ci)");
        sb.AppendLine($"{indent}    {{");

        for (int i = 0; i < generic.Instantiations.Count; i++)
        {
            IMethodSymbol closed = generic.ClosedMethods[i];
            sb.AppendLine($"{indent}        case {i}:");
            sb.AppendLine($"{indent}        {{");

            string args = "";
            if (closed.Parameters.Length > 0)
            {
                foreach (IParameterSymbol parameter in closed.Parameters)
                {
                    sb.AppendLine($"{indent}            {parameter.Type.ToDisplayString(RpcPayload.Qualified)} {parameter.Name} = default!;");
                }

                for (int j = 0; j < closed.Parameters.Length; j++)
                {
                    RpcPayload.EmitRead(sb, indent + "            ", closed.Parameters[j].Type,
                        closed.Parameters[j].Name, declare: false, method.References, depth: j + 2);
                }

                args = string.Join(", ", closed.Parameters.Select(static p => p.Name));
            }

            EmitValidationGate(sb, method, args, indent + "            ", $"<{ClosedTypeArgumentList(generic.Instantiations[i])}>");

            if (method.IsVoidReturn)
            {
                sb.AppendLine($"{indent}            await {method.MethodName}_Implementation<{ClosedTypeArgumentList(generic.Instantiations[i])}>({args}).ConfigureAwait(false);");
                sb.AppendLine($"{indent}            return global::System.Array.Empty<byte>();");
            }
            else
            {
                string closedReturn = closed.ReturnType.ToDisplayString(RpcPayload.Qualified);
                sb.AppendLine($"{indent}            {closedReturn} __result = await {method.MethodName}_Implementation<{ClosedTypeArgumentList(generic.Instantiations[i])}>({args}).ConfigureAwait(false);");
                sb.AppendLine($"{indent}            return {WriteReturn(method)}_{i}(__result);");
            }

            sb.AppendLine($"{indent}        }}");
        }

        sb.AppendLine($"{indent}        default:");
        sb.AppendLine($"{indent}            throw new global::System.InvalidOperationException($\"{method.MethodName}: unknown generic construction index {{__ci}}.\");");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}private partial {method.ImplementationSignature};");
        EmitValidateDeclaration(sb, method, indent);
    }

    /// <summary>Closed payload helpers per instantiation. WriteParams/ReadParams embed the instantiation index (send/receive symmetry).</summary>
    static void EmitGenericPayloadHelpers(StringBuilder sb, MethodMetadata method, string indent)
    {
        GenericMethodMetadata generic = method.Generic!;

        for (int i = 0; i < generic.ClosedMethods.Count; i++)
        {
            IMethodSymbol closed = generic.ClosedMethods[i];
            string paramList = string.Join(", ", closed.Parameters.Select(static p =>
                $"{p.Type.ToDisplayString(RpcPayload.Qualified)} {p.Name}"));

            sb.AppendLine($"{indent}static byte[] {WriteParams(method)}_{i}({paramList})");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __buf = {BufferCreate};");
            sb.AppendLine($"{indent}    try");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        __buf.WriteInt32({i});");
            for (int j = 0; j < closed.Parameters.Length; j++)
            {
                RpcPayload.EmitWrite(sb, indent + "        ", closed.Parameters[j].Type, closed.Parameters[j].Name,
                    method.References, depth: j + 1);
            }

            sb.AppendLine($"{indent}        return __buf.ToArray();");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    finally");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        __buf.Dispose();");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();

            if (closed.Parameters.Length > 0)
            {
                string outParams = string.Join(", ", closed.Parameters.Select(static p =>
                    $"out {p.Type.ToDisplayString(RpcPayload.Qualified)} {p.Name}"));

                sb.AppendLine($"{indent}static void {ReadParams(method)}_{i}(byte[] __data, {outParams})");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var __rd = {ReaderCtor};");
                foreach (IParameterSymbol parameter in closed.Parameters)
                {
                    sb.AppendLine($"{indent}    {parameter.Name} = default!;");
                }

                sb.AppendLine($"{indent}    _ = __rd.ReadInt32();");
                for (int j = 0; j < closed.Parameters.Length; j++)
                {
                    RpcPayload.EmitRead(sb, indent + "    ", closed.Parameters[j].Type,
                        closed.Parameters[j].Name, declare: false, method.References, depth: j + 1);
                }

                sb.AppendLine($"{indent}}}");
                sb.AppendLine();
            }

            if (!method.IsVoidReturn)
            {
                string returnType = closed.ReturnType.ToDisplayString(RpcPayload.Qualified);

                sb.AppendLine($"{indent}static byte[] {WriteReturn(method)}_{i}({returnType} __value)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var __buf = {BufferCreate};");
                sb.AppendLine($"{indent}    try");
                sb.AppendLine($"{indent}    {{");
                RpcPayload.EmitWrite(sb, indent + "        ", closed.ReturnType, "__value", method.References, depth: 1);
                sb.AppendLine($"{indent}        return __buf.ToArray();");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    finally");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        __buf.Dispose();");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine();

                sb.AppendLine($"{indent}static {returnType} {ReadReturn(method)}_{i}(byte[] __data)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var __rd = {ReaderCtor};");
                RpcPayload.EmitRead(sb, indent + "    ", closed.ReturnType, "__result", declare: true, method.References, depth: 1);
                sb.AppendLine($"{indent}    return __result;");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine();
            }
        }
    }
}
