using ManualLspClient.Protocol;

namespace ManualLspClient.Backend.Hosting;

/// <summary>
/// Centralized error handling for the backend's <c>/api</c> surface. Registered once as an endpoint
/// filter on the route group, it lets every endpoint express only its happy path: any exception a
/// handler throws is mapped to an appropriate HTTP status code and serialized as the shared
/// <see cref="ErrorResponse"/> envelope (<c>{ error }</c>) that <c>BackendClient</c> understands.
/// </summary>
internal static class ApiExceptionFilter
{
    /// <summary>
    /// Endpoint-filter delegate: invokes the endpoint and converts any thrown exception into an
    /// <see cref="ErrorResponse"/> with a status code chosen by <see cref="StatusCodeFor"/>.
    /// Passed by method group to <c>AddEndpointFilter</c> (no reflection ⇒ trim/AOT safe).
    /// </summary>
    public static async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new ErrorResponse { Error = ex.Message },
                BackendJsonContext.Default.ErrorResponse,
                statusCode: StatusCodeFor(ex));
        }
    }

    /// <summary>Maps an exception to the HTTP status code that best describes it.</summary>
    private static int StatusCodeFor(Exception ex) => ex switch
    {
        // Acting on a server that is not running is a state conflict, not a bad request.
        ServerNotRunningException => StatusCodes.Status409Conflict,
        // A request that outlived its timeout waiting on the LSP server is a gateway timeout.
        TimeoutException => StatusCodes.Status504GatewayTimeout,
        // Bad/insufficient input or unresolvable server paths are client errors.
        ArgumentException => StatusCodes.Status400BadRequest,
        FileNotFoundException => StatusCodes.Status400BadRequest,
        InvalidOperationException => StatusCodes.Status400BadRequest,
        // Anything else is an unexpected server-side failure.
        _ => StatusCodes.Status500InternalServerError,
    };
}
