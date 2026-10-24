namespace Kairos;

/// <summary>Thrown into workflow code when an activity fails terminally (retries exhausted or non-retryable).</summary>
public sealed class ActivityFailureException : Exception
{
    public ActivityFailureException(string activityType, string errorType, string message)
        : base($"Activity '{activityType}' failed: {errorType}: {message}")
    {
        ActivityType = activityType;
        ErrorType = errorType;
        ErrorMessage = message;
    }

    public ActivityFailureException()
        : this("", "", "")
    {
    }

    public ActivityFailureException(string message)
        : this("", "", message)
    {
    }

    public ActivityFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
        ActivityType = "";
        ErrorType = "";
        ErrorMessage = message;
    }

    public string ActivityType { get; }

    public string ErrorType { get; }

    public string ErrorMessage { get; }
}
