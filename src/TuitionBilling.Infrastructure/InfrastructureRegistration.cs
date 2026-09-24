using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TuitionBilling.Application.Abstractions;
using TuitionBilling.Application.Common;
using TuitionBilling.Infrastructure.BackgroundJobs;
using TuitionBilling.Infrastructure.Gateway;
using TuitionBilling.Infrastructure.Identity;
using TuitionBilling.Infrastructure.Persistence;

namespace TuitionBilling.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Billing")
                               ?? throw new InvalidOperationException(
                                   "Не задана строка подключения ConnectionStrings:Billing. "
                                   + "Локально её держат в dotnet user-secrets, в контейнере — в переменной окружения.");

        services.AddDbContext<BillingDbContext>(options => options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
            npgsql.MigrationsHistoryTable("__migrations");
        }));

        services.AddScoped<IBillingDbContext>(sp => sp.GetRequiredService<BillingDbContext>());
        services.AddSingleton<IUniqueConstraintDetector, NpgsqlUniqueConstraintDetector>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<BillingDbContext>();

        services.AddOptions<BillingOptions>()
            .Bind(configuration.GetSection(BillingOptions.SectionName));

        services.AddOptions<ReconciliationOptions>()
            .Bind(configuration.GetSection(ReconciliationOptions.SectionName));

        services.AddOptions<GatewayClientOptions>()
            .Bind(configuration.GetSection(GatewayClientOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ShopId) && !string.IsNullOrWhiteSpace(o.SecretKey),
                "Не заданы Gateway:ShopId и Gateway:SecretKey. Секреты берутся из переменных окружения "
                + "или dotnet user-secrets, в репозиторий они не попадают.")
            .ValidateOnStart();

        services.AddHttpClient<IPaymentGatewayClient, PaymentGatewayHttpClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<GatewayClientOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ShopId}:{options.SecretKey}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        });

        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<ReconciliationWorker>();
        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}
