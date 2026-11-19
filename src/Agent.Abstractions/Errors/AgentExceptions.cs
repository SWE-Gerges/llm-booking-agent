namespace Agent.Abstractions.Errors;

public sealed class SessionNotFoundException : Exception
{
    public SessionNotFoundException(string sessionId)
        : base("The session does not exist.") => SessionId = sessionId;

    public string SessionId { get; }
}

public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException(string sessionId)
        : base("The session has expired.") => SessionId = sessionId;

    public string SessionId { get; }
}

public sealed class TurnInProgressException : Exception
{
    public TurnInProgressException(string sessionId)
        : base("Another message in this session is still being processed.") => SessionId = sessionId;

    public string SessionId { get; }
}

/// <summary>A failure whose message is safe to show to the model and the end user.</summary>
public sealed class ToolExecutionException : Exception
{
    public ToolExecutionException(string message)
        : base(message)
    {
    }
}

/// <summary>Stored protected data could not be decrypted or authenticated. Callers must not fall back to partial data.</summary>
public sealed class FormDataProtectionException : Exception
{
    public FormDataProtectionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
