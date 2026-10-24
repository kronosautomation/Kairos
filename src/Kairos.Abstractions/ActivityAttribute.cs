namespace Kairos;

/// <summary>Marks a method as an activity: the only place workflow code may perform I/O or non-deterministic work.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ActivityAttribute : Attribute
{
    public ActivityAttribute(string? name = null) => Name = name;

    /// <summary>Registered activity type name; defaults to the method name when null.</summary>
    public string? Name { get; }
}
