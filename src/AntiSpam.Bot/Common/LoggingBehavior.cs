using Mediator;

namespace AntiSpam.Bot.Common;

public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMessage
{
    public async ValueTask<TResponse> Handle(
        TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken ct)
    {
        logger.LogInformation("Handling {Request}", typeof(TRequest).Name);
        var response = await next(request, ct);
        logger.LogDebug("Handled {Request}", typeof(TRequest).Name);
        return response;
    }
}
