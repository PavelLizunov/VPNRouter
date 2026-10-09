namespace VPNRouter.Headless;

/// <summary>
/// Domain exception representing structured protocol and backend errors.
/// Error messages are sanitized and safe for client consumption without secret leakage.
/// Public SafeMessage is guaranteed to use fixed safe mappings only, preventing dynamic field/input echoing.
/// </summary>
public class RouterException : Exception
{
    public string Code { get; }
    public string SafeMessage => GetSafeMessage(Code);

    public RouterException(string code, string? customSafeMessage = null, Exception? innerException = null)
        : base(customSafeMessage ?? GetSafeMessage(code), innerException)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "internal_error" : code;
    }

    public static string GetSafeMessage(string code) => code switch
    {
        "invalid_request" => "The request is malformed or invalid.",
        "invalid_argument" => "One or more arguments are invalid.",
        "payload_too_large" => "Input frame exceeds maximum allowed size.",
        "depth_limit_exceeded" => "JSON nesting exceeds maximum allowed depth.",
        "duplicate_key" => "Duplicate JSON object key detected.",
        "invalid_id" => "Request id must be 1-64 ASCII alphanumeric or hyphen characters.",
        "invalid_version" => "Unsupported protocol version.",
        "unknown_method" => "Unknown method requested.",
        "busy" => "Another operation is currently in progress.",
        "conflict" => "Configuration revision mismatch.",
        "not_found" => "The requested item was not found.",
        "cancelled" => "The operation was cancelled.",
        "unavailable" => "The requested service or feature is unavailable.",
        "unsupported" => "The requested operation or parameter is unsupported.",
        "storage_error" => "A configuration storage error occurred.",
        "connect_failed" => "Failed to initiate VPN connection.",
        "stop_failed" => "Failed to stop VPN connection.",
        "internal_error" => "An internal error occurred.",
        _ => "An error occurred."
    };
}
