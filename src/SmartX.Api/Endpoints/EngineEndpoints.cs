using SmartX.Api.Services;
using SmartX.Core.Contracts;

namespace SmartX.Api.Endpoints;

public static class EngineEndpoints
{
    public static IEndpointRouteBuilder MapEngineEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/engine").WithTags("Action Engine");

        group.MapGet("/", (ActionEngine engine) => Results.Ok(engine.Snapshot()));

        group.MapPost("/refresh", (ActionEngine engine) =>
        {
            engine.Refresh();
            return Results.Ok(engine.Snapshot());
        });

        group.MapGet("/rules", (int? top, ActionEngine engine) => Results.Ok(engine.Rules(Math.Clamp(top ?? 15, 1, 100))));

        // For Inspect / Search suggestions. Command suggestions are executed with POST /api/commands + suggestionId.
        group.MapPost("/suggestions/{id}/accept", (string id, ActionEngine engine, TimeProvider time) =>
        {
            SuggestionDto? suggestion = engine.Accept(id, time.GetUtcNow());
            return suggestion is null
                ? Results.NotFound(new { error = "That suggestion is no longer active." })
                : Results.Ok(suggestion);
        });

        group.MapPost("/suggestions/{id}/dismiss", (string id, ActionEngine engine, TimeProvider time) =>
            engine.Dismiss(id, time.GetUtcNow())
                ? Results.NoContent()
                : Results.NotFound(new { error = "That suggestion is no longer active." }));

        return app;
    }
}
