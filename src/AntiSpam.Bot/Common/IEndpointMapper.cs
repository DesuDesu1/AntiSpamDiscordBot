namespace AntiSpam.Bot.Common;

public interface IEndpointMapper
{
    IEndpointRouteBuilder Map(IEndpointRouteBuilder app);
}
