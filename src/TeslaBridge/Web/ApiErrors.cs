using TeslaBridge.Auth;

namespace TeslaBridge.Web;

/// <summary>
/// Every error from the API has the same shape: RFC 9457 problem details (application/problem+json), plus an
/// <c>error</c> member with the same text as <c>detail</c>, which the dashboard and the HA integration read.
/// </summary>
public static class ApiErrors
{
    public static IResult Problem(int status, string message, IDictionary<string, object?>? extra = null)
    {
        var ext = new Dictionary<string, object?>(extra ?? new Dictionary<string, object?>()) { ["error"] = message };
        return Results.Problem(detail: message, statusCode: status, extensions: ext);
    }

    public static IResult BadRequest(string message) => Problem(StatusCodes.Status400BadRequest, message);
    public static IResult NotFound(string message) => Problem(StatusCodes.Status404NotFound, message);

    /// <summary>
    /// A call to Tesla (or another service) failed: 409 when nobody has signed in with Tesla yet, 504 on a timeout,
    /// otherwise 502.
    /// </summary>
    public static IResult Upstream(Exception ex) => ex switch
    {
        TeslaNotSignedInException => Problem(StatusCodes.Status409Conflict, ex.Message),
        TimeoutException or TaskCanceledException => Problem(StatusCodes.Status504GatewayTimeout, ex.Message),
        _ => Problem(StatusCodes.Status502BadGateway, ex.Message),
    };
}
