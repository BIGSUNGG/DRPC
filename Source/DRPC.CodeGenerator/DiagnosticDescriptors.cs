using Microsoft.CodeAnalysis;

namespace DRPC.CodeGenerator;

internal static class DiagnosticDescriptors
{
    const string Category = "DRPC";

    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "DRPCGEN001",
        title: "DRPC hub type must be partial",
        messageFormat: "The DRPC hub type '{0}' must be declared partial.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidHubBase = new(
        id: "DRPCGEN002",
        title: "DRPC hub must inherit ClientHub or ServerHub",
        messageFormat: "The type '{0}' must inherit ClientHub<TSPD, TCPD> (DRPC.Client.Network) or ServerHub<TSPD, TCPD> (DRPC.Server.Network).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        id: "DRPCGEN003",
        title: "DRPC contract type not supported",
        messageFormat: "Method '{0}' uses unsupported type '{1}'. Use primitives, string, enums, nullable primitives, byte[], arrays/List<T> of those, or a [Message]-attributed MessageProtocol type. Reason: {2}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateMethodId = new(
        id: "DRPCGEN005",
        title: "DRPC MethodId is duplicated",
        messageFormat: "MethodId {0} is used more than once in declaration '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OneWayRequiresVoid = new(
        id: "DRPCGEN006",
        title: "DRPC OneWay requires void return",
        messageFormat: "Method '{0}' sets OneWay=true but does not return void.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericDeclarationMissing = new(
        id: "DRPCGEN007",
        title: "DRPC generic procedure type parameter is not declared",
        messageFormat: "Method '{0}' has a generic type parameter without an allowed-type declaration: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericTypeArgumentNotDeclared = new(
        id: "DRPCGEN008",
        title: "DRPC generic procedure type argument is not declared",
        messageFormat: "The call '{0}' uses type argument '{1}', which is not declared for '{2}'. Declare it with [GenericProcedure] (slot {3}) or use a declared type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericDeclarationInvalid = new(
        id: "DRPCGEN009",
        title: "DRPC generic procedure declaration is invalid",
        messageFormat: "Method '{0}' has an invalid generic procedure declaration: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidTimeoutMs = new(
        id: "DRPCGEN010",
        title: "DRPC TimeoutMs is invalid",
        messageFormat: "Method '{0}' sets TimeoutMs={1}; use a positive millisecond budget or omit it (-1) to inherit the hub default (HubBase.RpcTimeout).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TimeoutOnOneWay = new(
        id: "DRPCGEN011",
        title: "DRPC TimeoutMs has no effect on OneWay",
        messageFormat: "Method '{0}' sets TimeoutMs but OneWay=true never waits for a response; the timeout is ignored.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
