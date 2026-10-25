using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Kairos.Analyzers;

/// <summary>
/// Flags workflow and activity parameters that look like credentials. Inputs and results are recorded in the run's history, so a
/// secret passed as a parameter lives in storage for as long as the history does. Pass a reference (a secret name or an id) and let
/// the activity fetch the secret from the worker's own vault.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SecretInHistoryAnalyzer : DiagnosticAnalyzer
{
    public const string SecretId = "KAI006";

    internal static readonly DiagnosticDescriptor Secret = new(
        SecretId,
        "Credential passed through workflow history",
        "Parameter '{0}' looks like a credential and would be recorded in workflow history; pass a reference and fetch the secret inside the activity",
        "Kairos.Security",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly Regex SecretName = new(
        "(password|passwd|pwd|secret|apikey|api_key|accesskey|privatekey|credential|connectionstring|bearer|authtoken|accesstoken|token$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Secret];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? workflowContext = start.Compilation.GetTypeByMetadataName("Kairos.IWorkflowContext");
            if (workflowContext is null)
            {
                return;
            }

            INamedTypeSymbol? workflow = start.Compilation.GetTypeByMetadataName("Kairos.WorkflowAttribute");
            INamedTypeSymbol? activity = start.Compilation.GetTypeByMetadataName("Kairos.ActivityAttribute");
            start.RegisterSymbolAction(c => OnMethod(c, workflowContext, workflow, activity), SymbolKind.Method);
        });
    }

    private static void OnMethod(SymbolAnalysisContext context, INamedTypeSymbol workflowContext, INamedTypeSymbol? workflow, INamedTypeSymbol? activity)
    {
        var method = (IMethodSymbol)context.Symbol;
        bool carriesHistory =
            method.GetAttributes().Any(a => IsAttribute(a, workflow) || IsAttribute(a, activity))
            || method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, workflowContext));
        if (!carriesHistory)
        {
            return;
        }

        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (IsSecretCarrier(parameter.Type) && SecretName.IsMatch(parameter.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(Secret, parameter.Locations[0], parameter.Name));
            }
        }
    }

    private static bool IsAttribute(AttributeData data, INamedTypeSymbol? attribute) =>
        attribute is not null && SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute);

    private static bool IsSecretCarrier(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_String
        || type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }
        || type.Name is "ReadOnlyMemory" or "Memory" or "SecureString"
        || type.Name.Contains("Credential") || type.Name.Contains("Secret");
}
