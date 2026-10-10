namespace DataPuddle.Api;

/// <summary>An error the API reports to the caller as a JSON error with the given HTTP status.</summary>
public sealed class ApiException : Exception {
    public int Status { get; }
    public string Code { get; }

    public ApiException(int status, string code, string message) : base(message) {
        Status = status;
        Code = code;
    }

    public static ApiException BadRequest(string message) {
        return new ApiException(400, "bad_request", message);
    }

    public static ApiException NotFound(string message) {
        return new ApiException(404, "not_found", message);
    }

    public static ApiException Forbidden(string message) {
        return new ApiException(403, "forbidden", message);
    }

    public static ApiException Unauthorized() {
        return new ApiException(401, "unauthorized", "A valid API key is required. Send it as the X-Api-Key header or as 'Authorization: Bearer <key>'.");
    }

    public static ApiException Busy() {
        return new ApiException(503, "busy", "The database is busy with another request. Try again shortly.");
    }
}
