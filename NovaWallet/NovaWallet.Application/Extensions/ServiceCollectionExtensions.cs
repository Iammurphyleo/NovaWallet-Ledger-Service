using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Application.Interfaces;
using NovaWallet.Application.Services;
using NovaWallet.Application.Settings;

namespace NovaWallet.Application.Extensions;

public static class ServiceCollectionExtensions
{
    // IConfiguration not referenced here to keep Application layer configuration-agnostic.
    // Callers (Presentation) bind WalletSettings via services.Configure<WalletSettings>(config.GetSection(...)).
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IWalletService, WalletService>();
        services.AddValidatorsFromAssemblyContaining<WalletService>();
        return services;
    }
}
