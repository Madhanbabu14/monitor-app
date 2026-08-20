namespace Monitor.Core.Errors;

/// <summary>
/// Direct translation of the Node `AppError` class (middleware/error.middleware.ts).
/// Thrown by feature services for expected, operator-facing failures
/// ("Invalid email or password", "Account is inactive", ...). Caught by the
/// terminal exception-handling middleware in Monitor.Api and rendered as the
/// legacy `{"status":"error","message":"..."}` envelope with StatusCode.
/// </summary>
public class AppException : Exception
{
    public int StatusCode { get; }

    /// <summary>
    /// True for expected/operational failures (bad input, auth failure, etc.)
    /// as opposed to programmer errors / unhandled crashes. Mirrors the
    /// source's `isOperational` flag; currently informational only (used for
    /// log-level decisions), never changes the emitted envelope shape.
    /// </summary>
    public bool IsOperational { get; }

    public AppException(int statusCode, string message, bool isOperational = true)
        : base(message)
    {
        StatusCode = statusCode;
        IsOperational = isOperational;
    }
}
