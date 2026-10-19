namespace SmartX.Api.Services;

public enum GatewayStatus
{
    Accepted,
    Invalid,
    NotFound,
    Overloaded
}

/// <summary>Generic result wrapper returned by the validation gateway services.</summary>
public sealed record GatewayOutcome<T>(
    GatewayStatus Status,
    T? Value,
    IDictionary<string, string[]>? Errors,
    string? Message) where T : class;

public static class GatewayOutcome
{
    public static GatewayOutcome<T> Ok<T>(T value) where T : class =>
        new(GatewayStatus.Accepted, value, null, null);

    public static GatewayOutcome<T> Invalid<T>(string field, string message) where T : class =>
        new(GatewayStatus.Invalid, null, new Dictionary<string, string[]> { [field] = new[] { message } }, message);

    public static GatewayOutcome<T> Invalid<T>(IDictionary<string, string[]> errors) where T : class =>
        new(GatewayStatus.Invalid, null, errors, "The request failed validation.");

    public static GatewayOutcome<T> NotFound<T>(string message) where T : class =>
        new(GatewayStatus.NotFound, null, null, message);

    public static GatewayOutcome<T> Overloaded<T>(string message) where T : class =>
        new(GatewayStatus.Overloaded, null, null, message);

    /// <summary>Maps an outcome to an HTTP result.</summary>
    public static IResult ToHttpResult<T>(this GatewayOutcome<T> outcome, Func<T, IResult> onSuccess, string title) where T : class
    {
        if (outcome.Status == GatewayStatus.Accepted && outcome.Value is not null)
        {
            return onSuccess(outcome.Value);
        }

        if (outcome.Status == GatewayStatus.NotFound)
        {
            return Results.NotFound(new { error = outcome.Message });
        }

        if (outcome.Status == GatewayStatus.Overloaded)
        {
            // Back-pressure: tell the device to retry later instead of silently dropping data.
            return Results.Problem(outcome.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: title);
        }

        return Results.ValidationProblem(
            outcome.Errors ?? new Dictionary<string, string[]>(),
            title: title);
    }
}
