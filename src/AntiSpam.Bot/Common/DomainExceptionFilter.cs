using AntiSpam.Bot.Domain.Common;

namespace AntiSpam.Bot.Common;

/// <summary>
/// Turns any <see cref="DomainException"/> thrown while handling an internal request into the
/// "&#10060; {message}" reply, in one place, so no slice endpoint needs its own try/catch. Declared
/// once on the <c>/internal</c> group. For a slash command that text is what the user sees; for a
/// moderation interaction (button/reaction) Gateway ignores the body, so an already-handled incident
/// simply returns 200 here instead of erroring. Non-domain exceptions fall through to the 500 handler.
/// </summary>
public sealed class DomainExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DomainException ex)
        {
            return Results.Text($"❌ {ex.Message}");
        }
    }
}
