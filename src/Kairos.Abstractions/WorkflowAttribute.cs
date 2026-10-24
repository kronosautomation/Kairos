namespace Kairos;

/// <summary>Marks a class or method as a workflow entry point.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class WorkflowAttribute : Attribute
{
    public WorkflowAttribute(string? name = null) => Name = name;

    /// <summary>Registered workflow type name; defaults to the member name when null.</summary>
    public string? Name { get; }
}
