namespace COA.CodeNav.McpServer.Exceptions;

/// <summary>
/// Exception thrown when a JSON-RPC error occurs
/// </summary>
public class JsonRpcException : Exception
{
    /// <summary>
    /// Gets the JSON-RPC error code
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// Gets optional error data
    /// </summary>
    public new object? Data { get; }

    /// <summary>
    /// Initializes a new instance of the JsonRpcException class
    /// </summary>
    public JsonRpcException(int code, string message, object? data = null) 
        : base(message)
    {
        Code = code;
        Data = data;
    }
}