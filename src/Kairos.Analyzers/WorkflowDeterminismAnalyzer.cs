using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kairos.Analyzers;

/// <summary>
/// Flags code that makes a workflow non-deterministic. A workflow method is replayed from history, so it may only observe the
/// world through <c>IWorkflowContext</c>; anything else (the clock, randomness, I/O, threads, mutable statics) can give a different
/// answer on replay. Workflow code is a method marked <c>[Workflow]</c> or taking an <c>IWorkflowContext</c>, and any lambda or local
/// function inside one. A method marked <c>[Activity]</c> is never workflow code. Helpers called from a workflow are not followed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WorkflowDeterminismAnalyzer : DiagnosticAnalyzer
{
    public const string ClockId = "KAI001";
    public const string RandomId = "KAI002";
    public const string IoId = "KAI003";
    public const string ThreadingId = "KAI004";
    public const string StaticStateId = "KAI005";

    private const string Category = "Kairos.Determinism";

    private static DiagnosticDescriptor Rule(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor Clock = Rule(ClockId,
        "Workflow code reads the clock",
        "'{0}' reads the wall clock, which differs on replay; use ctx.Now for time and ctx.DelayAsync to wait");

    internal static readonly DiagnosticDescriptor Random = Rule(RandomId,
        "Workflow code generates random values",
        "'{0}' gives a different value on replay; use ctx.NewGuid() or generate the value in an activity");

    internal static readonly DiagnosticDescriptor Io = Rule(IoId,
        "Workflow code performs I/O or reads the environment",
        "'{0}' touches the outside world, so replay can diverge; do it in an activity");

    internal static readonly DiagnosticDescriptor Threading = Rule(ThreadingId,
        "Workflow code uses threads or its own scheduling",
        "'{0}' escapes the workflow scheduler; use ctx.DelayAsync and activities, and await tasks directly (no ConfigureAwait(false))");

    internal static readonly DiagnosticDescriptor StaticState = Rule(StaticStateId,
        "Workflow code uses mutable static state",
        "'{0}' is mutable static state shared across runs and not restored on replay; keep state in locals or workflow fields");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Clock, Random, Io, Threading, StaticState];

    private enum Kind
    {
        Clock,
        Random,
        Io,
        Threading,
    }

    /// <summary>Type metadata name to members. A null member list means every member of the type, constructors included.</summary>
    private static readonly (string Type, string[]? Members, Kind Kind)[] Banned =
    [
        ("System.DateTime", ["Now", "UtcNow", "Today"], Kind.Clock),
        ("System.DateTimeOffset", ["Now", "UtcNow"], Kind.Clock),
        ("System.TimeProvider", ["GetUtcNow", "GetLocalNow", "GetTimestamp", "System"], Kind.Clock),
        ("System.Environment", ["TickCount", "TickCount64"], Kind.Clock),
        ("System.Diagnostics.Stopwatch", ["GetTimestamp", "StartNew", ".ctor"], Kind.Clock),

        ("System.Guid", ["NewGuid", "CreateVersion7"], Kind.Random),
        ("System.Random", null, Kind.Random),
        ("System.Security.Cryptography.RandomNumberGenerator", null, Kind.Random),

        ("System.IO.File", null, Kind.Io),
        ("System.IO.Directory", null, Kind.Io),
        ("System.IO.FileInfo", null, Kind.Io),
        ("System.IO.DirectoryInfo", null, Kind.Io),
        ("System.IO.FileStream", null, Kind.Io),
        ("System.Net.Http.HttpClient", null, Kind.Io),
        ("System.Net.Sockets.Socket", null, Kind.Io),
        ("System.Net.Sockets.TcpClient", null, Kind.Io),
        ("System.Net.Sockets.UdpClient", null, Kind.Io),
        ("System.Net.Dns", null, Kind.Io),
        ("System.Diagnostics.Process", null, Kind.Io),
        ("System.Console", null, Kind.Io),
        ("System.Data.Common.DbConnection", null, Kind.Io),
        ("System.Data.Common.DbCommand", null, Kind.Io),
        ("System.Environment", ["GetEnvironmentVariable", "GetEnvironmentVariables", "SetEnvironmentVariable", "MachineName", "UserName", "CurrentDirectory", "ProcessId", "GetFolderPath", "ExpandEnvironmentVariables"], Kind.Io),

        ("System.Threading.Tasks.Task", ["Run", "Delay", "Yield", "Factory", "Wait", "WaitAll", "WaitAny"], Kind.Threading),
        ("System.Threading.Tasks.TaskFactory", null, Kind.Threading),
        ("System.Threading.Tasks.Parallel", null, Kind.Threading),
        ("System.Threading.Thread", null, Kind.Threading),
        ("System.Threading.ThreadPool", null, Kind.Threading),
        ("System.Threading.Timer", null, Kind.Threading),
        ("System.Threading.PeriodicTimer", null, Kind.Threading),
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext start)
    {
        INamedTypeSymbol? workflowContext = start.Compilation.GetTypeByMetadataName("Kairos.IWorkflowContext");
        if (workflowContext is null)
        {
            return; // the compilation does not use Kairos
        }

        var scope = new Scope(
            workflowContext,
            start.Compilation.GetTypeByMetadataName("Kairos.WorkflowAttribute"),
            start.Compilation.GetTypeByMetadataName("Kairos.ActivityAttribute"),
            Resolve(start.Compilation));

        start.RegisterOperationAction(c => OnInvocation(c, scope), OperationKind.Invocation);
        start.RegisterOperationAction(c => OnCreation(c, scope), OperationKind.ObjectCreation);
        start.RegisterOperationAction(c => OnProperty(c, scope), OperationKind.PropertyReference);
        start.RegisterOperationAction(c => OnField(c, scope), OperationKind.FieldReference);
    }

    private static List<(INamedTypeSymbol Type, string[]? Members, Kind Kind)> Resolve(Compilation compilation)
    {
        var resolved = new List<(INamedTypeSymbol, string[]?, Kind)>();
        foreach ((string type, string[]? members, Kind kind) in Banned)
        {
            if (compilation.GetTypeByMetadataName(type) is { } symbol)
            {
                resolved.Add((symbol, members, kind));
            }
        }

        return resolved;
    }

    private sealed class Scope(
        INamedTypeSymbol workflowContext, INamedTypeSymbol? workflowAttribute, INamedTypeSymbol? activityAttribute,
        List<(INamedTypeSymbol Type, string[]? Members, Kind Kind)> banned)
    {
        public INamedTypeSymbol WorkflowContext { get; } = workflowContext;

        public INamedTypeSymbol? WorkflowAttribute { get; } = workflowAttribute;

        public INamedTypeSymbol? ActivityAttribute { get; } = activityAttribute;

        public List<(INamedTypeSymbol Type, string[]? Members, Kind Kind)> Banned { get; } = banned;

        public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attribute) =>
            attribute is not null && symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

        public bool IsWorkflowMethod(IMethodSymbol method) =>
            !HasAttribute(method, ActivityAttribute)
            && (HasAttribute(method, WorkflowAttribute)
                || method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, WorkflowContext)));

        /// <summary>True when the operation is workflow code or nested (lambda, local function) inside it.</summary>
        public bool InWorkflowCode(IOperation operation, ISymbol? containing)
        {
            // Operation actions report the enclosing member, so lambdas and local functions are found on the operation tree.
            for (IOperation? o = operation; o is not null; o = o.Parent)
            {
                IMethodSymbol? inner = o switch
                {
                    IAnonymousFunctionOperation lambda => lambda.Symbol,
                    ILocalFunctionOperation local => local.Symbol,
                    _ => null,
                };
                if (inner is not null && IsWorkflowMethod(inner))
                {
                    return true;
                }
            }

            return InWorkflowCode(containing);
        }

        private bool InWorkflowCode(ISymbol? symbol)
        {
            for (ISymbol? s = symbol; s is not null; s = s.ContainingSymbol)
            {
                if (s is IMethodSymbol m)
                {
                    if (HasAttribute(m, ActivityAttribute))
                    {
                        return false;
                    }

                    if (IsWorkflowMethod(m))
                    {
                        return true;
                    }

                    if (m.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction))
                    {
                        return false; // an ordinary method that is not itself workflow code ends the search
                    }
                }
            }

            return false;
        }
    }

    private static void OnInvocation(OperationAnalysisContext context, Scope scope)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!scope.InWorkflowCode(context.Operation, context.ContainingSymbol))
        {
            return;
        }

        IMethodSymbol target = invocation.TargetMethod;
        if (target.Name == "ConfigureAwait" && invocation.Arguments.Length == 1
            && invocation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: false })
        {
            Report(context, Threading, invocation.Syntax, "ConfigureAwait(false)");
            return;
        }

        Check(context, scope, target.ContainingType, target.Name, invocation.Syntax);
    }

    private static void OnCreation(OperationAnalysisContext context, Scope scope)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Constructor is { } ctor && scope.InWorkflowCode(context.Operation, context.ContainingSymbol))
        {
            Check(context, scope, ctor.ContainingType, ".ctor", creation.Syntax);
        }
    }

    private static void OnProperty(OperationAnalysisContext context, Scope scope)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        IPropertySymbol property = reference.Property;
        if (!scope.InWorkflowCode(context.Operation, context.ContainingSymbol))
        {
            return;
        }

        if (property.IsStatic && property.SetMethod is { DeclaredAccessibility: not Accessibility.Private } && !IsFrameworkType(property.ContainingType))
        {
            Report(context, StaticState, reference.Syntax, property.ContainingType.Name + "." + property.Name);
            return;
        }

        Check(context, scope, property.ContainingType, property.Name, reference.Syntax);
    }

    private static void OnField(OperationAnalysisContext context, Scope scope)
    {
        var reference = (IFieldReferenceOperation)context.Operation;
        IFieldSymbol field = reference.Field;
        if (!scope.InWorkflowCode(context.Operation, context.ContainingSymbol))
        {
            return;
        }

        if (field.IsStatic && !field.IsConst && !field.IsReadOnly && !IsFrameworkType(field.ContainingType)
            && field.ContainingType.TypeKind != TypeKind.Enum)
        {
            Report(context, StaticState, reference.Syntax, field.ContainingType.Name + "." + field.Name);
            return;
        }

        Check(context, scope, field.ContainingType, field.Name, reference.Syntax);
    }

    private static bool IsFrameworkType(INamedTypeSymbol type) =>
        type.ContainingAssembly?.Name is "System.Private.CoreLib" or "mscorlib" or "netstandard" or "System.Runtime" or "System";

    private static void Check(OperationAnalysisContext context, Scope scope, INamedTypeSymbol? type, string member, SyntaxNode syntax)
    {
        if (type is null)
        {
            return;
        }

        foreach ((INamedTypeSymbol banned, string[]? members, Kind kind) in scope.Banned)
        {
            bool sameType = SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, banned) || DerivesFrom(type, banned);
            if (!sameType || (members is not null && Array.IndexOf(members, member) < 0))
            {
                continue;
            }

            DiagnosticDescriptor rule = kind switch
            {
                Kind.Clock => Clock,
                Kind.Random => Random,
                Kind.Io => Io,
                _ => Threading,
            };
            Report(context, rule, syntax, member == ".ctor" ? "new " + type.Name : type.Name + "." + member);
            return;
        }
    }

    /// <summary>True when <paramref name="type"/> derives from <paramref name="banned"/> (database connections and commands come in many flavours).</summary>
    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol banned)
    {
        if (banned.Name is not ("DbConnection" or "DbCommand"))
        {
            return false;
        }

        for (INamedTypeSymbol? t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, banned))
            {
                return true;
            }
        }

        return false;
    }

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor rule, SyntaxNode syntax, string what) =>
        context.ReportDiagnostic(Diagnostic.Create(rule, syntax.GetLocation(), what));
}
