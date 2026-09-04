using Concourse.Application.Abstractions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Concourse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddServicesByLifetimeMarkers(AssemblyReference.Assembly);

        return services;
    }
}
