namespace AntiSpam.Bot.Common;

/// <summary>
/// Internal endpoints (Gateway -> Bot slash command/interaction forwarding) sit on the same
/// cluster network as everything else; this shared-secret check is what stops any other pod
/// on that network from posing as Gateway and forging moderation actions.
/// </summary>
public sealed class InternalApiKeyFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = configuration["Internal:ApiKey"];
        var provided = context.HttpContext.Request.Headers["X-Internal-Key"].ToString();

        if (string.IsNullOrEmpty(expected) || !string.Equals(provided, expected, StringComparison.Ordinal))
            return Results.Unauthorized();

        return await next(context);
    }
}
