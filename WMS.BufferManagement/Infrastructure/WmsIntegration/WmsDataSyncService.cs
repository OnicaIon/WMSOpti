using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WMS.BufferManagement.Layers.Historical.Persistence;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;

namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

/// <summary>
/// Настройки синхронизации данных
/// </summary>
public class WmsSyncSettings
{
    /// <summary>
    /// Интервал синхронизации заданий (мс)
    /// </summary>
    public int TasksSyncIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Интервал синхронизации сборщиков (мс)
    /// </summary>
    public int PickersSyncIntervalMs { get; set; } = 30000;

    /// <summary>
    /// Интервал синхронизации карщиков (мс)
    /// </summary>
    public int ForkliftsSyncIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Интервал синхронизации буфера (мс)
    /// </summary>
    public int BufferSyncIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Интервал расчёта агрегатов (мс)
    /// </summary>
    public int AggregationIntervalMs { get; set; } = 60000;

    /// <summary>
    /// Интервал синхронизации продуктов (мс) - реже, т.к. справочник меняется редко
    /// </summary>
    public int ProductsSyncIntervalMs { get; set; } = 3600000; // 1 час

    /// <summary>
    /// Интервал синхронизации зон и ячеек (мс) - редко, справочники меняются редко
    /// </summary>
    public int ZonesCellsSyncIntervalMs { get; set; } = 3600000; // 1 час

    /// <summary>
    /// Включена ли синхронизация
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Включена ли синхронизация зон и ячеек
    /// </summary>
    public bool ZonesCellsSyncEnabled { get; set; } = true;

    /// <summary>
    /// Максимальное количество заданий для загрузки (0 = без ограничения)
    /// </summary>
    public int MaxTasksToSync { get; set; } = 0;

    /// <summary>
    /// Очистить таблицу заданий перед синхронизацией
    /// </summary>
    public bool TruncateBeforeSync { get; set; } = false;
}

/// <summary>
/// Состояние синхронизации (последние загруженные ID)
/// </summary>
public class SyncState
{
    public string? LastTaskId { get; set; }
    public string? LastPickerActivityId { get; set; }
    public string? LastBufferSnapshotId { get; set; }
    public string? LastProductId { get; set; }
    public DateTime LastTasksSync { get; set; }
    public DateTime LastPickersSync { get; set; }
    public DateTime LastForkliftsSync { get; set; }
    public DateTime LastBufferSync { get; set; }
    public DateTime LastProductsSync { get; set; }
    public DateTime LastAggregation { get; set; }
}

/// <summary>
/// Сервис синхронизации данных из WMS 1C в TimescaleDB
/// </summary>
public class WmsDataSyncService : BackgroundService
{
    private readonly IWms1CClient _wmsClient;
    private readonly IHistoricalRepository _repository;
    private readonly ILogger<WmsDataSyncService> _logger;
    private readonly WmsSyncSettings _settings;
    private readonly SyncState _state = new();

    // События для уведомления других компонентов
    public event Action<List<WmsTaskRecord>>? OnTasksSynced;
    public event Action<List<WmsPickerRecord>>? OnPickersSynced;
    public event Action<List<WmsForkliftRecord>>? OnForkliftsSynced;
    public event Action<WmsBufferState>? OnBufferStateSynced;
    public event Action<List<WmsProductRecord>>? OnProductsSynced;

    // Кэш текущего состояния для быстрого доступа
    public IReadOnlyList<WmsPickerRecord> CurrentPickers => _currentPickers;
    public IReadOnlyList<WmsForkliftRecord> CurrentForklifts => _currentForklifts;
    public WmsBufferState? CurrentBufferState => _currentBufferState;
    public IReadOnlyDictionary<string, WmsProductRecord> Products => _products;

    private List<WmsPickerRecord> _currentPickers = new();
    private List<WmsForkliftRecord> _currentForklifts = new();
    private WmsBufferState? _currentBufferState;
    private Dictionary<string, WmsProductRecord> _products = new();
    private bool _truncateDone = false;

    public WmsDataSyncService(
        IWms1CClient wmsClient,
        IHistoricalRepository repository,
        IOptions<WmsSyncSettings> settings,
        ILogger<WmsDataSyncService> logger)
    {
        _wmsClient = wmsClient;
        _repository = repository;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("WMS sync is disabled");
            return;
        }

        _logger.LogInformation("WMS data sync service starting...");

        // Ждём доступности WMS
        await WaitForWmsAsync(stoppingToken);

        // Инициализируем схему БД
        await _repository.InitializeSchemaAsync(stoppingToken);

        // ===== ОДНОКРАТНАЯ СИНХРОНИЗАЦИЯ =====
        _logger.LogInformation("Starting one-time sync...");

        try
        {
            // 1. Загружаем справочник продуктов
            _logger.LogInformation("Syncing products...");
            await SyncProductsAsync(stoppingToken);

            // 1.5. Загружаем зоны и ячейки
            _logger.LogInformation("Syncing zones and cells...");
            await SyncZonesAndCellsAsync(stoppingToken);

            // 2. Загружаем задания (с truncate если настроено)
            _logger.LogInformation("Syncing tasks...");
            await SyncTasksAsync(stoppingToken);

            // 3. Загружаем работников
            _logger.LogInformation("Syncing workers...");
            await SyncForkliftsAsync(stoppingToken);
            await SyncPickersAsync(stoppingToken);

            // 4. Загружаем состояние буфера
            _logger.LogInformation("Syncing buffer...");
            await SyncBufferAsync(stoppingToken);

            // 5. Рассчитываем статистику работников
            _logger.LogInformation("Calculating workers statistics...");
            await _repository.UpdateWorkersFromTasksAsync(stoppingToken);

            // 6. Рассчитываем статистику маршрутов (с нормализацией IQR)
            _logger.LogInformation("Calculating route statistics (IQR normalization)...");
            await _repository.UpdateRouteStatisticsAsync(stoppingToken);

            // 7. Рассчитываем статистику пикер + товар
            _logger.LogInformation("Calculating picker-product statistics...");
            await _repository.UpdatePickerProductStatsAsync(stoppingToken);

            _logger.LogInformation("=== SYNC COMPLETE ===");
            _logger.LogInformation("Tasks synced. LastTaskId: {LastId}", _state.LastTaskId);
            _logger.LogInformation("Products in cache: {Count}", _products.Count);
            _logger.LogInformation("Pickers: {Count}, Forklifts: {Count}",
                _currentPickers.Count, _currentForklifts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sync");
        }

        // Остаёмся активными для обслуживания запросов к кэшу
        _logger.LogInformation("Sync service ready. Waiting for shutdown...");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task WaitForWmsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for WMS to become available...");

        while (!ct.IsCancellationRequested)
        {
            if (await _wmsClient.HealthCheckAsync(ct))
            {
                _logger.LogInformation("WMS is available");
                return;
            }

            await Task.Delay(5000, ct);
        }
    }

    private async Task RunSyncLoopAsync(
        Func<CancellationToken, Task> syncAction,
        int intervalMs,
        string name,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await syncAction(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in {SyncName} sync loop", name);
            }

            await Task.Delay(intervalMs, ct);
        }
    }

    /// <summary>
    /// Синхронизация заданий (Task action из WMS)
    /// Сохраняем каждые 10,000 записей для экономии памяти
    /// </summary>
    private async Task SyncTasksAsync(CancellationToken ct)
    {
        // Очистка перед синхронизацией если настроено (только один раз)
        if (_settings.TruncateBeforeSync && !_truncateDone)
        {
            _logger.LogInformation("Truncating tasks before sync (TruncateBeforeSync=true)");
            await _repository.TruncateTasksAsync(ct);
            _truncateDone = true;
        }

        var pendingTasks = new List<WmsTaskRecord>();
        var afterId = _state.LastTaskId;
        var maxTasks = _settings.MaxTasksToSync;
        var totalLoaded = 0;
        var totalSaved = 0;
        const int saveEvery = 10000;

        // Загружаем и сохраняем инкрементально
        while (true)
        {
            var response = await _wmsClient.GetTasksAsync(afterId, 500, ct);

            if (response.Items.Count == 0)
                break;

            pendingTasks.AddRange(response.Items);
            afterId = response.LastId;
            totalLoaded += response.Items.Count;

            _logger.LogInformation("Loaded {Count} tasks, total: {Total}, lastId: {LastId}",
                response.Items.Count, totalLoaded, afterId);

            // Сохраняем каждые 10,000 записей
            if (pendingTasks.Count >= saveEvery)
            {
                await SaveTaskBatchAsync(pendingTasks, ct);
                totalSaved += pendingTasks.Count;
                _logger.LogInformation("Saved {Saved} tasks to DB (total saved: {TotalSaved})",
                    pendingTasks.Count, totalSaved);
                pendingTasks.Clear();
            }

            // Проверка лимита
            if (maxTasks > 0 && totalLoaded >= maxTasks)
            {
                _logger.LogInformation("Reached MaxTasksToSync limit: {Max}", maxTasks);
                break;
            }

            // Продолжаем пока есть записи
            if (response.Items.Count < 500)
                break;
        }

        // Сохраняем оставшиеся записи
        if (pendingTasks.Count > 0)
        {
            await SaveTaskBatchAsync(pendingTasks, ct);
            totalSaved += pendingTasks.Count;
            _logger.LogInformation("Saved final batch: {Count} tasks (total saved: {TotalSaved})",
                pendingTasks.Count, totalSaved);
        }

        _state.LastTaskId = afterId;
        _state.LastTasksSync = DateTime.UtcNow;

        _logger.LogInformation("Tasks sync complete: {Total} loaded, {Saved} saved", totalLoaded, totalSaved);
    }

    /// <summary>
    /// Конвертирует и сохраняет пакет задач
    /// </summary>
    private async Task SaveTaskBatchAsync(List<WmsTaskRecord> tasks, CancellationToken ct)
    {
        var records = tasks.Select(t => new TaskRecord
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
            WeightKg = (decimal)(t.ProductWeight * t.Qty / 1000.0),  // Общий вес в кг = (граммы * количество) / 1000
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

        UpdateWorkersFromTasks(tasks);
        await _repository.SaveTasksBatchAsync(records, ct);
    }

    /// <summary>
    /// Извлекает код зоны из кода ячейки
    /// Формат WMS: 01[Zone_Code]-[Aisle]-[Position]-[Shelf]
    /// Например: "01I-07-052-1" → Zone: "I", Aisle: "07", Position: "052", Shelf: "1"
    /// </summary>
    private static string? ExtractZoneFromBinCode(string? binCode)
    {
        if (string.IsNullOrEmpty(binCode))
            return null;

        // Формат: 01X-AA-PPP-S где X=Zone, AA=Aisle, PPP=Position, S=Shelf
        // Примеры: 01I-07-052-1 → Zone=I, 01D-01-029-1 → Zone=D, 01A-04-069-5 → Zone=A

        // Первая часть до дефиса содержит "01" + Zone_Code
        var parts = binCode.Split('-');
        if (parts.Length > 0 && parts[0].Length >= 3)
        {
            // Извлекаем всё после "01" как код зоны
            var firstPart = parts[0];
            if (firstPart.StartsWith("01"))
            {
                return firstPart.Substring(2); // "01I" → "I", "01D" → "D"
            }
        }

        return parts.Length > 0 ? parts[0] : binCode;
    }

    /// <summary>
    /// Парсит код ячейки в структурированный вид
    /// </summary>
    private static (string? Zone, string? Aisle, string? Position, string? Shelf) ParseBinCode(string? binCode)
    {
        if (string.IsNullOrEmpty(binCode))
            return (null, null, null, null);

        var parts = binCode.Split('-');
        if (parts.Length < 4)
            return (ExtractZoneFromBinCode(binCode), null, null, null);

        var zone = parts[0].Length >= 3 && parts[0].StartsWith("01")
            ? parts[0].Substring(2)
            : parts[0];

        return (zone, parts[1], parts[2], parts[3]);
    }

    /// <summary>
    /// Обновляет списки пикеров и карщиков на основе заданий
    /// TemplateCode 029 = Forklift (Distribution replenish)
    /// TemplateCode 031 = Picker (Put product into the bin)
    /// </summary>
    private void UpdateWorkersFromTasks(List<WmsTaskRecord> tasks)
    {
        // Группируем работников по ролям
        var forklifts = tasks
            .Where(t => t.WorkerRole == "Forklift" && !string.IsNullOrEmpty(t.AssigneeCode))
            .GroupBy(t => t.AssigneeCode)
            .Select(g => new WmsForkliftRecord
            {
                Id = g.Key!,
                OperatorId = g.Key!,
                OperatorName = g.First().AssigneeName ?? g.Key!,
                Status = 1, // Active
                CurrentTaskId = g.OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.Id
            })
            .ToList();

        var pickers = tasks
            .Where(t => t.WorkerRole == "Picker" && !string.IsNullOrEmpty(t.AssigneeCode))
            .GroupBy(t => t.AssigneeCode)
            .Select(g => new WmsPickerRecord
            {
                Id = g.Key!,
                Name = g.First().AssigneeName ?? g.Key!,
                Status = 1, // Active
                PalletsToday = g.Count(t => t.CompletedAt?.Date == DateTime.Today)
            })
            .ToList();

        // Обновляем кэш, сохраняя уникальных работников
        if (forklifts.Count > 0)
        {
            var existingIds = _currentForklifts.Select(f => f.Id).ToHashSet();
            foreach (var f in forklifts.Where(f => !existingIds.Contains(f.Id)))
            {
                _currentForklifts.Add(f);
            }
            _logger.LogDebug("Updated forklifts from tasks: {NewCount} new, {TotalCount} total",
                forklifts.Count(f => !existingIds.Contains(f.Id)), _currentForklifts.Count);
        }

        if (pickers.Count > 0)
        {
            var existingIds = _currentPickers.Select(p => p.Id).ToHashSet();
            foreach (var p in pickers.Where(p => !existingIds.Contains(p.Id)))
            {
                _currentPickers.Add(p);
            }
            _logger.LogDebug("Updated pickers from tasks: {NewCount} new, {TotalCount} total",
                pickers.Count(p => !existingIds.Contains(p.Id)), _currentPickers.Count);
        }
    }

    /// <summary>
    /// Синхронизация сборщиков и их активности
    /// </summary>
    private async Task SyncPickersAsync(CancellationToken ct)
    {
        // Получаем текущее состояние сборщиков
        var pickersResponse = await _wmsClient.GetPickersAsync(ct: ct);
        _currentPickers = pickersResponse.Items;

        // Получаем лог активности
        var activityList = new List<WmsPickerActivityRecord>();
        var afterId = _state.LastPickerActivityId;

        while (true)
        {
            var response = await _wmsClient.GetPickerActivityAsync(afterId, ct: ct);

            if (response.Items.Count == 0)
                break;

            activityList.AddRange(response.Items);
            afterId = response.LastId;

            if (!response.HasMore)
                break;
        }

        if (activityList.Count > 0)
        {
            _logger.LogDebug("Syncing {Count} picker activity records", activityList.Count);

            // Конвертируем события pallet_end в метрики
            var metrics = activityList
                .Where(a => a.EventType == "pallet_end")
                .Select(a => new PickerMetric
                {
                    Time = a.Timestamp,
                    PickerId = a.PickerId,
                    ItemsPicked = a.ItemsPicked,
                    Active = true,
                    // Скорость рассчитывается из durationSec и itemsPicked
                    ConsumptionRate = a.DurationSec > 0 && a.ItemsPicked > 0
                        ? (decimal)(a.ItemsPicked.Value / (a.DurationSec.Value / 3600.0))
                        : null
                })
                .ToList();

            if (metrics.Count > 0)
            {
                await _repository.SavePickerMetricsBatchAsync(metrics, ct);
            }

            _state.LastPickerActivityId = afterId;
        }

        _state.LastPickersSync = DateTime.UtcNow;
        OnPickersSynced?.Invoke(_currentPickers);
    }

    /// <summary>
    /// Синхронизация карщиков
    /// </summary>
    private async Task SyncForkliftsAsync(CancellationToken ct)
    {
        var response = await _wmsClient.GetForkliftsAsync(ct);
        _currentForklifts = response.Items;
        _state.LastForkliftsSync = DateTime.UtcNow;

        OnForkliftsSynced?.Invoke(_currentForklifts);
    }

    /// <summary>
    /// Синхронизация состояния буфера
    /// </summary>
    private async Task SyncBufferAsync(CancellationToken ct)
    {
        // Текущее состояние
        _currentBufferState = await _wmsClient.GetBufferStateAsync(ct);

        // Снимки истории
        var snapshots = new List<WmsBufferSnapshotRecord>();
        var afterId = _state.LastBufferSnapshotId;

        while (true)
        {
            var response = await _wmsClient.GetBufferSnapshotsAsync(afterId, ct: ct);

            if (response.Items.Count == 0)
                break;

            snapshots.AddRange(response.Items);
            afterId = response.LastId;

            if (!response.HasMore)
                break;
        }

        if (snapshots.Count > 0)
        {
            _logger.LogDebug("Syncing {Count} buffer snapshots", snapshots.Count);

            foreach (var s in snapshots)
            {
                var snapshot = new BufferSnapshot
                {
                    Time = s.Timestamp,
                    BufferLevel = s.Capacity > 0 ? (decimal)s.CurrentCount / s.Capacity : 0,
                    BufferState = GetBufferState(s.CurrentCount, s.Capacity),
                    PalletsCount = s.CurrentCount,
                    ActiveForklifts = s.ActiveForklifts,
                    ActivePickers = s.ActivePickers
                };

                await _repository.SaveBufferSnapshotAsync(snapshot, ct);
            }

            _state.LastBufferSnapshotId = afterId;
        }

        _state.LastBufferSync = DateTime.UtcNow;

        if (_currentBufferState != null)
        {
            OnBufferStateSynced?.Invoke(_currentBufferState);
        }
    }

    /// <summary>
    /// Синхронизация справочника продуктов (для Heavy-on-Bottom rule)
    /// </summary>
    private async Task SyncProductsAsync(CancellationToken ct)
    {
        var allProducts = new List<WmsProductRecord>();
        var afterId = _state.LastProductId;

        // Загружаем все продукты (инкрементально)
        while (true)
        {
            var response = await _wmsClient.GetProductsAsync(afterId, limit: 1000, ct: ct);

            if (response.Items.Count == 0)
                break;

            allProducts.AddRange(response.Items);
            afterId = response.LastId;

            if (!response.HasMore)
                break;
        }

        if (allProducts.Count > 0)
        {
            _logger.LogInformation("Synced {Count} products from WMS", allProducts.Count);

            // Обновляем кэш продуктов (словарь по коду для быстрого доступа)
            foreach (var product in allProducts)
            {
                _products[product.Code] = product;
            }

            // Сохраняем в БД
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

            _state.LastProductId = afterId;
            OnProductsSynced?.Invoke(allProducts);
        }

        _state.LastProductsSync = DateTime.UtcNow;
    }

    /// <summary>
    /// Получить вес продукта по SKU (для Heavy-on-Bottom rule)
    /// </summary>
    public double GetProductWeight(string? sku)
    {
        if (string.IsNullOrEmpty(sku))
            return 0;

        // Сначала ищем по SKU
        var product = _products.Values.FirstOrDefault(p =>
            p.Sku == sku || p.Code == sku || p.ExternalCode == sku);

        return product?.WeightKg ?? 0;
    }

    /// <summary>
    /// Получить категорию веса продукта (Light/Medium/Heavy)
    /// </summary>
    public string GetProductWeightCategory(string? sku)
    {
        var weight = GetProductWeight(sku);
        return GetWeightCategory(weight);
    }

    /// <summary>
    /// Периодический расчёт агрегатов для ML
    /// </summary>
    private async Task RunAggregationsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Running periodic aggregations...");

        // Здесь можно добавить расчёт агрегированных метрик
        // Например, почасовые паттерны сборщиков, статистика маршрутов и т.д.

        _state.LastAggregation = DateTime.UtcNow;

        await Task.CompletedTask;
    }

    private static string GetWeightCategory(double weightKg) => weightKg switch
    {
        >= 20 => "Heavy",
        >= 5 => "Medium",
        _ => "Light"
    };

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

    private static string GetBufferState(int count, int capacity)
    {
        if (capacity == 0) return "Normal";
        var level = (double)count / capacity;
        return level switch
        {
            < 0.15 => "Critical",
            < 0.30 => "Low",
            > 0.85 => "Overflow",
            _ => "Normal"
        };
    }

    /// <summary>
    /// Синхронизация зон склада
    /// </summary>
    public async Task SyncZonesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Syncing zones...");

        var response = await _wmsClient.GetZonesAsync(ct);

        if (response.Items.Count == 0)
        {
            _logger.LogWarning("No zones received from WMS");
            return;
        }

        // Определяем буферные зоны (зона "I" или тип "Picking")
        var bufferZoneCodes = new HashSet<string> { "I" };

        await _repository.UpsertZonesAsync(response.Items, bufferZoneCodes, ct);

        var bufferZones = response.Items.Where(z =>
            bufferZoneCodes.Contains(z.Code) || z.ZoneType == "Picking").ToList();

        _logger.LogInformation("Synced {Count} zones ({BufferCount} buffer zones: {Codes})",
            response.Items.Count,
            bufferZones.Count,
            string.Join(", ", bufferZones.Select(z => z.Code)));
    }

    /// <summary>
    /// Синхронизация ячеек склада
    /// </summary>
    public async Task SyncCellsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Syncing cells...");

        int totalSynced = 0;
        string? lastCode = null;

        while (true)
        {
            var response = await _wmsClient.GetCellsAsync(
                afterId: lastCode,
                limit: 10000,
                ct: ct);

            if (response.Items.Count == 0)
                break;

            await _repository.UpsertCellsAsync(response.Items, ct);
            totalSynced += response.Items.Count;
            lastCode = response.LastId;

            _logger.LogInformation("Synced {Count} cells (total: {Total})",
                response.Items.Count, totalSynced);

            if (!response.HasMore)
                break;
        }

        // Подсчёт буферных ячеек
        var bufferCellsCount = await _repository.GetBufferCellsCountAsync(ct);
        _logger.LogInformation("Cells sync complete. Total: {Total}, Buffer cells: {Buffer}",
            totalSynced, bufferCellsCount);
    }

    /// <summary>
    /// Синхронизация зон и ячеек (вызывается из ExecuteAsync)
    /// </summary>
    public async Task SyncZonesAndCellsAsync(CancellationToken ct)
    {
        if (!_settings.ZonesCellsSyncEnabled)
        {
            _logger.LogInformation("Zones/cells sync is disabled");
            return;
        }

        await SyncZonesAsync(ct);
        await SyncCellsAsync(ct);
    }
}
