using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TuitionBilling.Application.Services;
using TuitionBilling.Application.Validation;

namespace TuitionBilling.Application;

public static class ApplicationRegistration
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<LedgerService>();
        services.AddScoped<ContractService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentSynchronizer>();
        services.AddScoped<WebhookService>();
        services.AddScoped<RefundService>();
        services.AddScoped<ReconciliationService>();

        // Валидаторы поднимаются из сборки, но вызываются вручную в контроллерах:
        // автоматическая интеграция FluentValidation в конвейер ASP.NET больше
        // не поддерживается самими авторами библиотеки.
        services.AddValidatorsFromAssemblyContaining<CreatePaymentRequestValidator>();

        return services;
    }
}
