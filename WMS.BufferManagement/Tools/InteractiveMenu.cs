using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.Persistence;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;
using WMS.BufferManagement.Layers.Historical.Prediction;
using WMS.BufferManagement.Services.Backtesting;
using SystemConsole = System.Console;

namespace WMS.BufferManagement.Tools;

/// <summary>
/// Интерактивное консольное меню.
/// Запуск: dotnet run (без параметров).
/// Объединяет все функции: sync, stats, backtest, ML.
/// </summary>
public static class InteractiveMenu
{
    private static IWms1CClient _wmsClient = null!;
    private static IHistoricalRepository _repository = null!;
    private static WaveBacktestService _backtestService = null!;
    private static IConfiguration _configuration = null!;
    private static string _reportsDir = "reports";

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        _wmsClient = services.GetRequiredService<IWms1CClient>();
        _repository = services.GetRequiredService<IHistoricalRepository>();
        _backtestService = services.GetRequiredService<WaveBacktestService>();
        _configuration = services.GetRequiredService<IConfiguration>();
        _reportsDir = Path.Combine(Directory.GetCurrentDirectory(), "reports");

        SystemConsole.OutputEncoding = System.Text.Encoding.UTF8;

        // Инициализация схемы БД
        try
        {
            await _repository.InitializeSchemaAsync();
        }
        catch (Exception ex)
        {
            SystemConsole.WriteLine($"Предупреждение: не удалось инициализировать схему БД: {ex.Message}");
        }

        while (true)
        {
            PrintMainMenu();
            var choice = SystemConsole.ReadLine()?.Trim();

            try
            {
                switch (choice)
                {
                    case "1":
                        await SyncSubMenu();
                        break;
                    case "2":
                        await UpdateStatistics();
                        break;
                    case "3":
                        await RunWaveBacktest();
                        break;
                    case "4":
                        await ShowRouteStatistics();
                        break;
                    case "5":
                        await ShowWorkerStatistics();
                        break;
                    case "6":
                        await ShowPickerProductStats();
                        break;
                    case "7":
                        await TrainMlModels();
                        break;
                    case "0":
                    case null:
                        SystemConsole.WriteLine("Выход.");
                        return 0;
                    default:
                        SystemConsole.WriteLine("Неизвестная команда.");
                        break;
                }
            }
            catch (Exception ex)
            {
                SystemConsole.WriteLine($"\nОШИБКА: {ex.Message}");
                SystemConsole.WriteLine($"  {ex.GetType().Name}");
                if (ex.InnerException != null)
                    SystemConsole.WriteLine($"  Inner: {ex.InnerException.Message}");
            }

            SystemConsole.WriteLine("\nНажмите Enter для продолжения...");
            SystemConsole.ReadLine();
        }
    }

    // ============================================================================
    // Главное меню
    // ============================================================================

    private static void PrintMainMenu()
    {
        SystemConsole.Clear();
        SystemConsole.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
        SystemConsole.WriteLine("\u2551  WMS Buffer Management System                                \u2551");
        SystemConsole.WriteLine("\u2560\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2563");
        SystemConsole.WriteLine("\u2551                                                              \u2551");
        SystemConsole.WriteLine("\u2551  1. \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u0434\u0430\u043d\u043d\u044b\u0445 \u0438\u0437 WMS (\u043f\u043e\u0434\u043c\u0435\u043d\u044e)                    \u2551");
        SystemConsole.WriteLine("\u2551  2. \u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0443 (routes, workers, picker-product)    \u2551");
        SystemConsole.WriteLine("\u2551  3. \u0411\u044d\u043a\u0442\u0435\u0441\u0442 \u0432\u043e\u043b\u043d\u044b (\u0432\u0432\u043e\u0434 \u043d\u043e\u043c\u0435\u0440\u0430)                             \u2551");
        SystemConsole.WriteLine("\u2551  4. \u0421\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0430 \u043c\u0430\u0440\u0448\u0440\u0443\u0442\u043e\u0432                                     \u2551");
        SystemConsole.WriteLine("\u2551  5. \u0421\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0430 \u0440\u0430\u0431\u043e\u0442\u043d\u0438\u043a\u043e\u0432                                    \u2551");
        SystemConsole.WriteLine("\u2551  6. \u0421\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0430 \u043f\u0438\u043a\u0435\u0440-\u0442\u043e\u0432\u0430\u0440                                   \u2551");
        SystemConsole.WriteLine("\u2551  7. \u041e\u0431\u0443\u0447\u0435\u043d\u0438\u0435 ML \u043c\u043e\u0434\u0435\u043b\u0435\u0439                                      \u2551");
        SystemConsole.WriteLine("\u2551  0. \u0412\u044b\u0445\u043e\u0434                                                    \u2551");
        SystemConsole.WriteLine("\u2551                                                              \u2551");
        SystemConsole.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
        SystemConsole.Write("\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u0434\u0435\u0439\u0441\u0442\u0432\u0438\u0435: ");
    }

    // ============================================================================
    // 1. Синхронизация данных
    // ============================================================================

    private static async Task SyncSubMenu()
    {
        while (true)
        {
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("  --- \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u0434\u0430\u043d\u043d\u044b\u0445 ---");
            SystemConsole.WriteLine("  1. \u0417\u0430\u0434\u0430\u0447\u0438 (\u0438\u043d\u043a\u0440\u0435\u043c\u0435\u043d\u0442\u0430\u043b\u044c\u043d\u043e)");
            SystemConsole.WriteLine("  2. \u0417\u043e\u043d\u044b");
            SystemConsole.WriteLine("  3. \u042f\u0447\u0435\u0439\u043a\u0438");
            SystemConsole.WriteLine("  4. \u041f\u0440\u043e\u0434\u0443\u043a\u0442\u044b");
            SystemConsole.WriteLine("  5. \u0412\u0441\u0451 (\u0437\u0430\u0434\u0430\u0447\u0438 + \u0437\u043e\u043d\u044b + \u044f\u0447\u0435\u0439\u043a\u0438 + \u043f\u0440\u043e\u0434\u0443\u043a\u0442\u044b)");
            SystemConsole.WriteLine("  0. \u041d\u0430\u0437\u0430\u0434");
            SystemConsole.Write("  \u0412\u044b\u0431\u043e\u0440: ");

            var choice = SystemConsole.ReadLine()?.Trim();
            var ct = CancellationToken.None;

            // Проверяем доступность WMS
            if (choice != "0" && choice != null)
            {
                SystemConsole.WriteLine("  \u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 WMS...");
                if (!await _wmsClient.HealthCheckAsync(ct))
                {
                    SystemConsole.WriteLine("  \u041e\u0428\u0418\u0411\u041a\u0410: WMS \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d");
                    return;
                }
                SystemConsole.WriteLine("  WMS \u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d");
            }

            switch (choice)
            {
                case "1":
                    await SyncTasks(ct);
                    return;
                case "2":
                    await SyncZones(ct);
                    return;
                case "3":
                    await SyncCells(ct);
                    return;
                case "4":
                    await SyncProducts(ct);
                    return;
                case "5":
                    await SyncZones(ct);
                    await SyncCells(ct);
                    await SyncProducts(ct);
                    await SyncTasks(ct);
                    return;
                case "0":
                    return;
                default:
                    SystemConsole.WriteLine("  \u041d\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043d\u0430\u044f \u043a\u043e\u043c\u0430\u043d\u0434\u0430");
                    break;
            }
        }
    }

    private static async Task SyncZones(CancellationToken ct)
    {
        SystemConsole.WriteLine("\n  === \u0421\u0418\u041d\u0425\u0420\u041e\u041d\u0418\u0417\u0410\u0426\u0418\u042f \u0417\u041e\u041d ===");
        var response = await _wmsClient.GetZonesAsync(ct);
        SystemConsole.WriteLine($"  \u041f\u043e\u043b\u0443\u0447\u0435\u043d\u043e \u0437\u043e\u043d: {response.Items.Count}");

        if (response.Items.Count > 0)
        {
            var bufferZoneCodes = new HashSet<string> { "I" };
            await _repository.UpsertZonesAsync(response.Items, bufferZoneCodes, ct);
            SystemConsole.WriteLine("  \u0417\u043e\u043d\u044b \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u044b");
        }
    }

    private static async Task SyncCells(CancellationToken ct)
    {
        SystemConsole.WriteLine("\n  === \u0421\u0418\u041d\u0425\u0420\u041e\u041d\u0418\u0417\u0410\u0426\u0418\u042f \u042f\u0427\u0415\u0415\u041a ===");
        int totalSynced = 0;
        string? lastCode = null;

        while (true)
        {
            var response = await _wmsClient.GetCellsAsync(afterId: lastCode, limit: 10000, ct: ct);
            if (response.Items.Count == 0) break;

            await _repository.UpsertCellsAsync(response.Items, ct);
            totalSynced += response.Items.Count;
            lastCode = response.LastId;

            SystemConsole.WriteLine($"  \u0417\u0430\u0433\u0440\u0443\u0436\u0435\u043d\u043e \u044f\u0447\u0435\u0435\u043a: {totalSynced}");
            if (!response.HasMore) break;
        }

        var bufferCount = await _repository.GetBufferCellsCountAsync(ct);
        SystemConsole.WriteLine($"  \u0412\u0441\u0435\u0433\u043e: {totalSynced}, \u0431\u0443\u0444\u0435\u0440\u043d\u044b\u0445: {bufferCount}");
    }

    private static async Task SyncProducts(CancellationToken ct)
    {
        SystemConsole.WriteLine("\n  === \u0421\u0418\u041d\u0425\u0420\u041e\u041d\u0418\u0417\u0410\u0426\u0418\u042f \u041f\u0420\u041e\u0414\u0423\u041a\u0422\u041e\u0412 ===");
        var allProducts = new List<WmsProductRecord>();
        string? afterId = null;

        while (true)
        {
            var response = await _wmsClient.GetProductsAsync(afterId, limit: 1000, ct: ct);
            if (response.Items.Count == 0) break;

            allProducts.AddRange(response.Items);
            afterId = response.LastId;
            SystemConsole.WriteLine($"  \u0417\u0430\u0433\u0440\u0443\u0436\u0435\u043d\u043e \u043f\u0440\u043e\u0434\u0443\u043a\u0442\u043e\u0432: {allProducts.Count}");
            if (!response.HasMore) break;
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

            await _repository.SaveProductsBatchAsync(productRecords, ct);
        }
        SystemConsole.WriteLine($"  \u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u043e: {allProducts.Count}");
    }

    private static async Task SyncTasks(CancellationToken ct)
    {
        SystemConsole.WriteLine("\n  === \u0421\u0418\u041d\u0425\u0420\u041e\u041d\u0418\u0417\u0410\u0426\u0418\u042f \u0417\u0410\u0414\u0410\u0427 ===");
        var tasksCount = await _repository.GetTasksCountAsync(ct);
        SystemConsole.WriteLine($"  \u0417\u0430\u0434\u0430\u0447 \u0432 \u0431\u0430\u0437\u0435: {tasksCount}");

        string? afterId = null;
        int totalLoaded = 0;
        int totalSaved = 0;
        const int batchSize = 10000;
        var pendingTasks = new List<WmsTaskRecord>();

        while (true)
        {
            var response = await _wmsClient.GetTasksAsync(afterId, 500, ct);
            if (response.Items.Count == 0) break;

            pendingTasks.AddRange(response.Items);
            afterId = response.LastId;
            totalLoaded += response.Items.Count;

            if (pendingTasks.Count >= batchSize)
            {
                await SaveTaskBatch(pendingTasks, ct);
                totalSaved += pendingTasks.Count;
                SystemConsole.WriteLine($"  \u0417\u0430\u0433\u0440\u0443\u0436\u0435\u043d\u043e: {totalLoaded}, \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u043e: {totalSaved}");
                pendingTasks.Clear();
            }

            if (response.Items.Count < 500) break;
        }

        if (pendingTasks.Count > 0)
        {
            await SaveTaskBatch(pendingTasks, ct);
            totalSaved += pendingTasks.Count;
        }

        SystemConsole.WriteLine($"  \u0418\u0442\u043e\u0433\u043e: \u0437\u0430\u0433\u0440\u0443\u0436\u0435\u043d\u043e {totalLoaded}, \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u043e {totalSaved}");
    }

    private static async Task SaveTaskBatch(List<WmsTaskRecord> tasks, CancellationToken ct)
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

        await _repository.SaveTasksBatchAsync(records, ct);
    }

    // ============================================================================
    // 2. Обновить статистику
    // ============================================================================

    private static async Task UpdateStatistics()
    {
        var ct = CancellationToken.None;
        SystemConsole.WriteLine("\n=== \u041f\u0415\u0420\u0415\u0421\u0427\u0401\u0422 \u0421\u0422\u0410\u0422\u0418\u0421\u0422\u0418\u041a\u0418 ===");

        SystemConsole.WriteLine("  \u041f\u0435\u0440\u0435\u0441\u0447\u0451\u0442 \u0440\u0430\u0431\u043e\u0442\u043d\u0438\u043a\u043e\u0432...");
        await _repository.UpdateWorkersFromTasksAsync(ct);
        SystemConsole.WriteLine("  \u0413\u043e\u0442\u043e\u0432\u043e");

        SystemConsole.WriteLine("  \u041f\u0435\u0440\u0435\u0441\u0447\u0451\u0442 \u043c\u0430\u0440\u0448\u0440\u0443\u0442\u043e\u0432...");
        await _repository.UpdateRouteStatisticsAsync(ct);
        SystemConsole.WriteLine("  \u0413\u043e\u0442\u043e\u0432\u043e");

        SystemConsole.WriteLine("  \u041f\u0435\u0440\u0435\u0441\u0447\u0451\u0442 \u043f\u0438\u043a\u0435\u0440-\u0442\u043e\u0432\u0430\u0440...");
        await _repository.UpdatePickerProductStatsAsync(ct);
        SystemConsole.WriteLine("  \u0413\u043e\u0442\u043e\u0432\u043e");

        SystemConsole.WriteLine("\n=== \u0421\u0422\u0410\u0422\u0418\u0421\u0422\u0418\u041a\u0410 \u041e\u0411\u041d\u041e\u0412\u041b\u0415\u041d\u0410 ===");
    }

    // ============================================================================
    // 3. Бэктест волны
    // ============================================================================

    private static async Task RunWaveBacktest()
    {
        SystemConsole.Write("\n\u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u043d\u043e\u043c\u0435\u0440 \u0432\u043e\u043b\u043d\u044b: ");
        var waveNumber = SystemConsole.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(waveNumber))
        {
            SystemConsole.WriteLine("\u041d\u043e\u043c\u0435\u0440 \u0432\u043e\u043b\u043d\u044b \u043d\u0435 \u0443\u043a\u0430\u0437\u0430\u043d");
            return;
        }

        SystemConsole.WriteLine($"\n\u0417\u0430\u043f\u0443\u0441\u043a \u0431\u044d\u043a\u0442\u0435\u0441\u0442\u0430 \u0432\u043e\u043b\u043d\u044b {waveNumber}...");
        SystemConsole.WriteLine("  \u041f\u043e\u043b\u0443\u0447\u0435\u043d\u0438\u0435 \u0434\u0430\u043d\u043d\u044b\u0445 \u0438\u0437 1\u0421...");

        var result = await _backtestService.RunBacktestAsync(waveNumber);

        // Краткий отчёт в консоль
        BacktestReportWriter.PrintSummary(result);

        // Подробный отчёт в файл
        var reportPath = await BacktestReportWriter.WriteDetailedReportAsync(result, _reportsDir);
        SystemConsole.WriteLine($"\u041f\u043e\u0434\u0440\u043e\u0431\u043d\u044b\u0439 \u043e\u0442\u0447\u0451\u0442: {reportPath}");
    }

    // ============================================================================
    // 4. Статистика маршрутов
    // ============================================================================

    private static async Task ShowRouteStatistics()
    {
        SystemConsole.WriteLine("\n=== \u0421\u0422\u0410\u0422\u0418\u0421\u0422\u0418\u041a\u0410 \u041c\u0410\u0420\u0428\u0420\u0423\u0422\u041e\u0412 ===\n");

        var routes = await _repository.GetRouteStatisticsAsync(minTrips: 3);

        if (routes.Count == 0)
        {
            SystemConsole.WriteLine("  \u041d\u0435\u0442 \u0434\u0430\u043d\u043d\u044b\u0445. \u0412\u044b\u043f\u043e\u043b\u043d\u0438\u0442\u0435 \u043f\u0443\u043d\u043a\u0442 2 (\u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0443).");
            return;
        }

        SystemConsole.WriteLine($"  {"Из зоны",-10} {"В зону",-10} {"Поездок",8} {"Норм.",6} {"Ср.время,с",12} {"Медиана,с",10} {"P95,с",8} {"Прогноз,с",10}");
        SystemConsole.WriteLine($"  {new string('-', 76)}");

        foreach (var r in routes.OrderByDescending(r => r.TotalTrips).Take(30))
        {
            SystemConsole.WriteLine($"  {r.FromZone,-10} {r.ToZone,-10} {r.TotalTrips,8} {r.NormalizedTrips,6} {r.AvgDurationSec,12:F1} {r.MedianDurationSec,10:F1} {r.Percentile95DurationSec,8:F0} {r.PredictedDurationSec,10:F1}");
        }

        SystemConsole.WriteLine($"\n  \u0412\u0441\u0435\u0433\u043e \u043c\u0430\u0440\u0448\u0440\u0443\u0442\u043e\u0432: {routes.Count}");
    }

    // ============================================================================
    // 5. Статистика работников
    // ============================================================================

    private static async Task ShowWorkerStatistics()
    {
        SystemConsole.WriteLine("\n=== \u0421\u0422\u0410\u0422\u0418\u0421\u0422\u0418\u041a\u0410 \u0420\u0410\u0411\u041e\u0422\u041d\u0418\u041a\u041e\u0412 ===\n");

        var workers = await _repository.GetWorkersAsync();

        if (workers.Count == 0)
        {
            SystemConsole.WriteLine("  \u041d\u0435\u0442 \u0434\u0430\u043d\u043d\u044b\u0445. \u0412\u044b\u043f\u043e\u043b\u043d\u0438\u0442\u0435 \u043f\u0443\u043d\u043a\u0442 2 (\u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0443).");
            return;
        }

        SystemConsole.WriteLine($"  {"Код",-12} {"Имя",-20} {"Роль",-10} {"Задач",8} {"Ср.время,с",12} {"Медиана,с",10} {"Задач/ч",8}");
        SystemConsole.WriteLine($"  {new string('-', 82)}");

        foreach (var w in workers.OrderByDescending(w => w.TotalTasks).Take(30))
        {
            var name = w.Name.Length > 18 ? w.Name[..18] : w.Name;
            SystemConsole.WriteLine($"  {w.Id,-12} {name,-20} {w.Role,-10} {w.TotalTasks,8} {w.AvgDurationSec,12:F1} {w.MedianDurationSec,10:F1} {w.TasksPerHour,8:F1}");
        }

        SystemConsole.WriteLine($"\n  \u0412\u0441\u0435\u0433\u043e \u0440\u0430\u0431\u043e\u0442\u043d\u0438\u043a\u043e\u0432: {workers.Count}");
    }

    // ============================================================================
    // 6. Статистика пикер-товар
    // ============================================================================

    private static async Task ShowPickerProductStats()
    {
        SystemConsole.WriteLine("\n=== \u0421\u0422\u0410\u0422\u0418\u0421\u0422\u0418\u041a\u0410 \u041f\u0418\u041a\u0415\u0420-\u0422\u041e\u0412\u0410\u0420 ===\n");

        var stats = await _repository.GetPickerProductStatsAsync(minLines: 3);

        if (stats.Count == 0)
        {
            SystemConsole.WriteLine("  \u041d\u0435\u0442 \u0434\u0430\u043d\u043d\u044b\u0445. \u0412\u044b\u043f\u043e\u043b\u043d\u0438\u0442\u0435 \u043f\u0443\u043d\u043a\u0442 2 (\u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0443).");
            return;
        }

        SystemConsole.WriteLine($"  {"Пикер",-12} {"Товар",-15} {"Строк",6} {"Ср.время,с",12} {"Стр/мин",8} {"Ед/мин",8} {"Кг/мин",8}");
        SystemConsole.WriteLine($"  {new string('-', 72)}");

        foreach (var s in stats.OrderByDescending(s => s.TotalLines).Take(30))
        {
            var product = s.ProductSku.Length > 13 ? s.ProductSku[..13] : s.ProductSku;
            SystemConsole.WriteLine($"  {s.PickerId,-12} {product,-15} {s.TotalLines,6} {s.AvgDurationSec,12:F1} {s.LinesPerMin,8:F2} {s.QtyPerMin,8:F2} {s.KgPerMin,8:F2}");
        }

        SystemConsole.WriteLine($"\n  \u0412\u0441\u0435\u0433\u043e \u0437\u0430\u043f\u0438\u0441\u0435\u0439: {stats.Count}");
    }

    // ============================================================================
    // 7. Обучение ML моделей
    // ============================================================================

    private static async Task TrainMlModels()
    {
        SystemConsole.WriteLine("\n=== \u041e\u0411\u0423\u0427\u0415\u041d\u0418\u0415 ML \u041c\u041e\u0414\u0415\u041b\u0415\u0419 ===");

        var connectionString = _configuration["Historical:ConnectionString"]
            ?? "Host=localhost;Port=5433;Database=wms_history;Username=wms;Password=wms_password";
        var modelsPath = _configuration["MlModels:Path"] ?? "data/models";

        SystemConsole.WriteLine($"  Models path: {modelsPath}");

        var trainer = new MlTrainer(connectionString, modelsPath);
        await trainer.TrainAllModelsAsync();

        SystemConsole.WriteLine("\n=== \u041e\u0411\u0423\u0427\u0415\u041d\u0418\u0415 \u0417\u0410\u0412\u0415\u0420\u0428\u0415\u041d\u041e ===");
    }

    // ============================================================================
    // Утилиты
    // ============================================================================

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
