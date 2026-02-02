using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WMS.BufferManagement.Layers.Historical.Persistence;

namespace WMS.BufferManagement;

/// <summary>
/// Запуск только расчётов статистики (без синхронизации)
/// </summary>
public static class RunCalculations
{
    public static async Task ExecuteAsync(IServiceProvider services)
    {
        var repository = services.GetRequiredService<IHistoricalRepository>();
        var logger = services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("=== Running Statistics Calculations ===");

        try
        {
            // 1. Расчёт статистики работников
            logger.LogInformation("1. Updating workers from tasks...");
            await repository.UpdateWorkersFromTasksAsync();
            logger.LogInformation("   Workers updated successfully");

            // 2. Расчёт статистики маршрутов
            logger.LogInformation("2. Updating route statistics...");
            await repository.UpdateRouteStatisticsAsync();
            logger.LogInformation("   Route statistics updated successfully");

            // 3. Расчёт статистики пикер + товар
            logger.LogInformation("3. Updating picker-product statistics...");
            await repository.UpdatePickerProductStatsAsync();
            logger.LogInformation("   Picker-product statistics updated successfully");

            logger.LogInformation("=== All calculations completed ===");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during calculations");
            throw;
        }
    }
}
