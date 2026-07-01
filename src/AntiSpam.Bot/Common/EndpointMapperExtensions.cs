using System.Reflection;

namespace AntiSpam.Bot.Common;

public static class EndpointMapperExtensions
{
    /// <summary>
    /// Maps every <see cref="IEndpointMapper"/> in the assembly onto <paramref name="builder"/>.
    /// Pass a route group (e.g. <c>app.MapGroup("/internal").AddEndpointFilter&lt;InternalApiKeyFilter&gt;()</c>)
    /// so a cross-cutting transport concern like the internal api-key check is declared once on the
    /// group instead of repeated in every slice's endpoint.
    /// </summary>
    public static IEndpointRouteBuilder MapAllEndpoints(this IEndpointRouteBuilder builder)
    {
        var mapperType = typeof(IEndpointMapper);
        var mapperTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && mapperType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName);

        foreach (var type in mapperTypes)
        {
            var mapper = (IEndpointMapper)Activator.CreateInstance(type)!;
            mapper.Map(builder);
        }

        return builder;
    }
}
