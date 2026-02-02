using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.Persistence;

namespace WMS.BufferManagement.Tools;

/// <summary>
/// Команды синхронизации данных
/// </summary>
public static class SyncCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Загружаем конфигурацию
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Создаём сервисы
        var services = new ServiceCollection();

        // Логирование
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Конфигурация
        services.Configure<Wms1CSettings>(config.GetSection("Wms1C"));
        services.Configure<WmsSyncSettings>(config.GetSection("WmsSync"));
        services.Configure<HistoricalOptions>(config.GetSection("Historical"));
        services.Configure<RouteStatisticsOptions>(config.GetSection("RouteStatistics"));

        // Сервисы
        services.AddHttpClient<IWms1CClient, Wms1CClient>();
        services.AddSingleton<IHistoricalRepository, TimescaleDbRepository>();

        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Wms1CClient>>();
        var wmsClient = provider.GetRequiredService<IWms1CClient>();
        var repository = provider.GetRequiredService<IHistoricalRepository>();

        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║  WMS Sync Command                                            ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var ct = CancellationToken.None;

        try
        {
            // Инициализация схемы
            await repository.InitializeSchemaAsync(ct);

            // Проверка доступности WMS
            System.Console.WriteLine("Проверка WMS...");
            if (!await wmsClient.HealthCheckAsync(ct))
            {
                System.Console.WriteLine("ОШИБКА: WMS недоступен");
                return 1;
            }
            System.Console.WriteLine("WMS доступен\n");

            // Очистка задач если запрошено
            if (args.Contains("--truncate-tasks"))
            {
                System.Console.WriteLine("=== ОЧИСТКА ТАБЛИЦЫ ЗАДАЧ ===");
                await repository.TruncateTasksAsync(ct);
                System.Console.WriteLine("Таблица tasks очищена\n");
            }

            // Синхронизация зон
            if (args.Contains("--sync-zones") || args.Contains("--sync-all"))
            {
                System.Console.WriteLine("=== СИНХРОНИЗАЦИЯ ЗОН ===");
                await SyncZonesAsync(wmsClient, repository, ct);
                System.Console.WriteLine();
            }

            // Синхронизация ячеек
            if (args.Contains("--sync-cells") || args.Contains("--sync-all"))
            {
                System.Console.WriteLine("=== СИНХРОНИЗАЦИЯ ЯЧЕЕК ===");
                await SyncCellsAsync(wmsClient, repository, ct);
                System.Console.WriteLine();
            }

            // Синхронизация продуктов
            if (args.Contains("--sync-products") || args.Contains("--sync-all"))
            {
                System.Console.WriteLine("=== СИНХРОНИЗАЦИЯ ПРОДУКТОВ ===");
                await SyncProductsAsync(wmsClient, repository, ct);
                System.Console.WriteLine();
            }

            // Синхронизация задач
            if (args.Contains("--sync-tasks") || args.Contains("--sync-all"))
            {
                System.Console.WriteLine("=== СИНХРОНИЗАЦИЯ ЗАДАЧ ===");
                await SyncTasksAsync(wmsClient, repository, ct);
                System.Console.WriteLine();
            }

            // Пересчёт статистики
            if (args.Contains("--calc-workers") || args.Contains("--calc"))
            {
                System.Console.WriteLine("=== ПЕРЕСЧЁТ СТАТИСТИКИ РАБОТНИКОВ ===");
                await repository.UpdateWorkersFromTasksAsync(ct);
                System.Console.WriteLine("Готово\n");
            }

            if (args.Contains("--calc-routes") || args.Contains("--calc"))
            {
                System.Console.WriteLine("=== ПЕРЕСЧЁТ СТАТИСТИКИ МАРШРУТОВ ===");
                await repository.UpdateRouteStatisticsAsync(ct);
                System.Console.WriteLine("Готово\n");
            }

            if (args.Contains("--calc-picker-product") || args.Contains("--calc"))
            {
                System.Console.WriteLine("=== ПЕРЕСЧЁТ СТАТИСТИКИ ПИКЕР-ТОВАР ===");
                await repository.UpdatePickerProductStatsAsync(ct);
                System.Console.WriteLine("Готово\n");
            }

            System.Console.WriteLine("=== ГОТОВО ===");
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"ОШИБКА: {ex.Message}");
            return 1;
        }
    }

    private static async Task SyncZonesAsync(IWms1CClient wmsClient, IHistoricalRepository repository, CancellationToken ct)
    {
        var response = await wmsClient.GetZonesAsync(ct);
        System.Console.WriteLine($"Получено зон: {response.Items.Count}");

        if (response.Items.Count > 0)
        {
            var bufferZoneCodes = new HashSet<string> { "I" };
            await repository.UpsertZonesAsync(response.Items, bufferZoneCodes, ct);

            var bufferZones = response.Items.Where(z =>
                bufferZoneCodes.Contains(z.Code) || z.ZoneType == "Picking").ToList();
            System.Console.WriteLine($"Буферных зон: {bufferZones.Count} ({string.Join(", ", bufferZones.Select(z => z.Code))})");
        }
    }

    private static async Task SyncCellsAsync(IWms1CClient wmsClient, IHistoricalRepository repository, CancellationToken ct)
    {
        int totalSynced = 0;
        string? lastCode = null;

        while (true)
        {
            var response = await wmsClient.GetCellsAsync(afterId: lastCode, limit: 10000, ct: ct);

            if (response.Items.Count == 0)
                break;

            await repository.UpsertCellsAsync(response.Items, ct);
            totalSynced += response.Items.Count;
            lastCode = response.LastId;

            System.Console.WriteLine($"Загружено ячеек: {totalSynced}");

            if (!response.HasMore)
                break;
        }

        var bufferCount = await repository.GetBufferCellsCountAsync(ct);
        System.Console.WriteLine($"Всего ячеек: {totalSynced}, буферных: {bufferCount}");
    }

    private static async Task SyncProductsAsync(IWms1CClient wmsClient, IHistoricalRepository repository, CancellationToken ct)
    {
        var allProducts = new List<WmsProductRecord>();
        string? afterId = null;

        while (true)
        {
            var response = await wmsClient.GetProductsAsync(afterId, limit: 1000, ct: ct);

            if (response.Items.Count == 0)
                break;

            allProducts.AddRange(response.Items);
            afterId = response.LastId;

            System.Console.WriteLine($"Загружено продуктов: {allProducts.Count}");

            if (!response.HasMore)
                break;
        }

        if (allProducts.Count > 0)
        {
            var productRecords = allProducts.Select(p => new Layers.Historical.Persistence.Models.ProductRecord
            {
                Code = p.Code,
                Sku = p.Sku,
                Name = p.Name,
                ExternalCode = p.ExternalCode,
                VendorCode = p.VendorCode,
                Barcode = p.Barcode,
                WeightKg = (decimal)p.WeightKg,
                VolumeM3 = (decimal)p.VolumeM3,
                WeightCategory = p.WeightCategory,
                CategoryCode = p.CategoryCode,
                CategoryName = p.CategoryName,
                MaxQtyPerPallet = p.MaxQtyPerPallet,
                SyncedAt = DateTime.UtcNow
            });

            await repository.SaveProductsBatchAsync(productRecords, ct);
        }

        System.Console.WriteLine($"Сохранено продуктов: {allProducts.Count}");
    }

    private static async Task SyncTasksAsync(IWms1CClient wmsClient, IHistoricalRepository repository, CancellationToken ct)
    {
        // Получаем последний ID из базы
        var tasksCount = await repository.GetTasksCountAsync(ct);
        System.Console.WriteLine($"Задач в базе: {tasksCount}");

        string? afterId = null; // TODO: получить из sync_state
        int totalLoaded = 0;
        int totalSaved = 0;
        const int batchSize = 10000;
        var pendingTasks = new List<WmsTaskRecord>();

        while (true)
        {
            var response = await wmsClient.GetTasksAsync(afterId, 500, ct);

            if (response.Items.Count == 0)
                break;

            pendingTasks.AddRange(response.Items);
            afterId = response.LastId;
            totalLoaded += response.Items.Count;

            // Сохраняем пакетами
            if (pendingTasks.Count >= batchSize)
            {
                await SaveTaskBatchAsync(pendingTasks, repository, ct);
                totalSaved += pendingTasks.Count;
                System.Console.WriteLine($"Загружено: {totalLoaded}, сохранено: {totalSaved}");
                pendingTasks.Clear();
            }

            if (response.Items.Count < 500)
                break;
        }

        // Сохраняем остаток
        if (pendingTasks.Count > 0)
        {
            await SaveTaskBatchAsync(pendingTasks, repository, ct);
            totalSaved += pendingTasks.Count;
        }

        System.Console.WriteLine($"Итого загружено: {totalLoaded}, сохранено: {totalSaved}");
    }

    private static async Task SaveTaskBatchAsync(List<WmsTaskRecord> tasks, IHistoricalRepository repository, CancellationToken ct)
    {
        var records = tasks.Select(t => new Layers.Historical.Persistence.Models.TaskRecord
        {
            Id = Guid.TryParse(t.ActionId, out var actionGuid) ? actionGuid
               : Guid.TryParse(t.Id, out var idGuid) ? idGuid
               : Guid.NewGuid(),
            CreatedAt = t.CreatedAt,
            StartedAt = t.StartedAt,
            CompletedAt = t.CompletedAt,
            PalletId = t.StoragePalletCode ?? t.AllocationPalletCode ?? t.Id,
            ProductType = !string.IsNullOrEmpty(t.ProductSku) ? t.ProductSku
                        : !string.IsNullOrEmpty(t.ProductCode) ? t.ProductCode
                        : null,
            WeightKg = (decimal)(t.ProductWeight * t.Qty / 1000.0),
            WeightCategory = "Unknown",
            Qty = (decimal)t.Qty,
            WorkerId = t.AssigneeCode,
            WorkerName = t.AssigneeName,
            WorkerRole = t.WorkerRole,
            TemplateCode = t.TemplateCode,
            TemplateName = t.TemplateName,
            TaskBasisNumber = t.TaskBasisNumber,
            ForkliftId = t.WorkerRole == "Forklift" ? t.AssigneeCode : null,
            FromZone = ExtractZoneFromBinCode(t.StorageBinCode),
            FromSlot = t.StorageBinCode,
            ToZone = ExtractZoneFromBinCode(t.AllocationBinCode),
            ToSlot = t.AllocationBinCode,
            DistanceMeters = null,
            Status = GetTaskStatus(t.Status),
            DurationSec = t.DurationSec.HasValue
                ? (decimal)t.DurationSec.Value
                : (t.CompletedAt.HasValue && t.StartedAt.HasValue
                    ? (decimal)(t.CompletedAt.Value - t.StartedAt.Value).TotalSeconds
                    : null),
            FailureReason = null
        }).ToList();

        await repository.SaveTasksBatchAsync(records, ct);
    }

    private static string? ExtractZoneFromBinCode(string? binCode)
    {
        if (string.IsNullOrEmpty(binCode))
            return null;

        var parts = binCode.Split('-');
        if (parts.Length > 0 && parts[0].Length >= 3 && parts[0].StartsWith("01"))
        {
            return parts[0].Substring(2);
        }

        return parts.Length > 0 ? parts[0] : binCode;
    }

    private static string GetTaskStatus(int status) => status switch
    {
        0 => "Pending",
        1 => "Assigned",
        2 => "InProgress",
        3 => "Completed",
        4 => "Failed",
        5 => "Cancelled",
        _ => "Unknown"
    };
}
