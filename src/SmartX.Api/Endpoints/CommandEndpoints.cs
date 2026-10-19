using SmartX.Api.Services;
using SmartX.Core.Contracts;

namespace SmartX.Api.Endpoints;

public static class CommandEndpoints
{
    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/commands").WithTags("Commands");

        group.MapGet("/history", (CommandService commands) => Results.Ok(commands.History()));

        // Polled by devices (and the simulator) to learn their desired state.
        group.MapGet("/shadows", (CommandService commands) => Results.Ok(commands.Shadows()));

        group.MapPost("/", (IssueCommandRequest request, CommandService commands) =>
        {
            CommandResultDto result = commands.Execute(request);
            return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
        });

        group.MapPost("/undo", (string? issuedBy, CommandService commands) =>
        {
            CommandResultDto result = commands.Undo(issuedBy);
            return result.Success ? Results.Ok(result) : Results.Conflict(result);
        });

        group.MapPost("/redo", (string? issuedBy, CommandService commands) =>
        {
            CommandResultDto result = commands.Redo(issuedBy);
            return result.Success ? Results.Ok(result) : Results.Conflict(result);
        });

        return app;
    }
}
