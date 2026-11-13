using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace OrderSaga.Downstream;

public static class DownstreamServiceCollectionExtensions
{
    /// <summary>Registers the simulated downstream systems on the <c>ConnectionStrings:Downstream</c> database.</summary>
    public static IServiceCollection AddDownstreamSystems(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Downstream")
            ?? throw new InvalidOperationException("ConnectionStrings:Downstream is not configured.");

        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString)
        {
            Name = "downstream",
        }.Build());
        services.Configure<DownstreamOptions>(configuration.GetSection(DownstreamOptions.Section));
        services.AddSingleton<FaultInjector>();
        services.AddSingleton<DownstreamSystems>();
        services.AddSingleton<MigrationRunner>();
        return services;
    }
}
