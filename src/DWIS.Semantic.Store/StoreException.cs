using System.Net;

namespace DWIS.Semantic.Store;

/// <summary>The store refused or failed a request. The status and the body are those of the store's answer, when there was one.</summary>
public class StoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StoreException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public StoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public StoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the store's answer.</summary>
    public StoreException(string message, HttpStatusCode? statusCode, string? body)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>The HTTP status of the store's answer, when there was one.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The body of the store's answer, when there was one.</summary>
    public string? Body { get; }
}

/// <summary>The query hit its time limit (R-SEM-8) before any row was returned.</summary>
public sealed class StoreTimeoutException : StoreException
{
    /// <summary>Creates the exception.</summary>
    public StoreTimeoutException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public StoreTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public StoreTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the limit that was hit.</summary>
    public StoreTimeoutException(string message, TimeSpan limit)
        : base(message)
    {
        Limit = limit;
    }

    /// <summary>The limit that was hit.</summary>
    public TimeSpan Limit { get; }
}
