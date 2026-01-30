using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WMS.BufferManagement.Layers.Historical.Persistence;

namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

/// <summary>
/// Расширения для регистрации WMS интеграции в DI
/// </summary>
public static class WmsIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет сервисы интеграции с WMS 1C
    /// </summary>
    public static IServiceCollection AddWms1CIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Настройки
        services.Configure<Wms1CSettings>(configuration.GetSection("Wms1C"));
        services.Configure<WmsSyncSettings>(configuration.GetSection("WmsSync"));

        // HTTP клиент (регистрируем вручную, т.к. AddHttpClient требует доп. пакет)
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IWms1CClient, Wms1CClient>();

        // Сервис синхронизации (singleton, т.к. хранит состояние)
        services.AddSingleton<WmsDataSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<WmsDataSyncService>());

        // Провайдер данных реального времени
        services.AddSingleton<IRealTimeDataProvider, RealTimeDataProvider>();

        return services;
    }

    /// <summary>
    /// Добавляет исторический репозиторий (TimescaleDB)
    /// </summary>
    public static IServiceCollection AddHistoricalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HistoricalOptions>(configuration.GetSection("Historical"));

        services.AddSingleton<IHistoricalRepository, TimescaleDbRepository>();

        return services;
    }
}
