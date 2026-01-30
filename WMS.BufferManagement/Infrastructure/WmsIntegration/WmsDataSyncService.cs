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
    /// Включена ли синхронизация
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Состояние синхронизации (последние загруженные ID)
/// </summary>
public class SyncState
{
    public string? LastTaskId { get; set; }
    public string? LastPickerActivityId { get; set; }
    public string? LastBufferSnapshotId { get; set; }
    public DateTime LastTasksSync { get; set; }
    public DateTime LastPickersSync { get; set; }
    public DateTime LastForkliftsSync { get; set; }
    public DateTime LastBufferSync { get; set; }
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

    // Кэш текущего состояния для быстрого доступа
    public IReadOnlyList<WmsPickerRecord> CurrentPickers => _currentPickers;
    public IReadOnlyList<WmsForkliftRecord> CurrentForklifts => _currentForklifts;
    public WmsBufferState? CurrentBufferState => _currentBufferState;

    private List<WmsPickerRecord> _currentPickers = new();
    private List<WmsForkliftRecord> _currentForklifts = new();
    private WmsBufferState? _currentBufferState;

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

        // Запускаем параллельные циклы синхронизации
        var tasks = new List<Task>
        {
            RunSyncLoopAsync(SyncTasksAsync, _settings.TasksSyncIntervalMs, "Tasks", stoppingToken),
            RunSyncLoopAsync(SyncPickersAsync, _settings.PickersSyncIntervalMs, "Pickers", stoppingToken),
            RunSyncLoopAsync(SyncForkliftsAsync, _settings.ForkliftsSyncIntervalMs, "Forklifts", stoppingToken),
            RunSyncLoopAsync(SyncBufferAsync, _settings.BufferSyncIntervalMs, "Buffer", stoppingToken),
            RunSyncLoopAsync(RunAggregationsAsync, _settings.AggregationIntervalMs, "Aggregations", stoppingToken)
        };

        await Task.WhenAll(tasks);
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
    /// </summary>
    private async Task SyncTasksAsync(CancellationToken ct)
    {
        var allTasks = new List<WmsTaskRecord>();
        var afterId = _state.LastTaskId;

        // Загружаем все новые записи
        while (true)
        {
            var response = await _wmsClient.GetTasksAsync(afterId, 1000, ct);

            if (response.Items.Count == 0)
                break;

            allTasks.AddRange(response.Items);
            afterId = response.LastId;

            if (!response.HasMore)
                break;
        }

        if (allTasks.Count > 0)
        {
            _logger.LogDebug("Syncing {Count} tasks from WMS", allTasks.Count);

            // Конвертируем Task action в TaskRecord для TimescaleDB
            // Маппинг полей WMS Task action → TaskRecord:
            //   id → Id (номер документа)
            //   actionId → используем как Id если это UUID
            //   storageBinCode → FromSlot (источник)
            //   storagePalletCode → PalletId
            //   allocationBinCode → ToSlot (назначение)
            //   assigneeCode → ForkliftId (исполнитель, может быть карщик или сборщик)
            //   actionType → определяет тип операции
            var records = allTasks.Select(t => new TaskRecord
            {
                // Используем ActionId как UUID если есть, иначе генерируем
                Id = Guid.TryParse(t.ActionId, out var actionGuid) ? actionGuid
                   : Guid.TryParse(t.Id, out var idGuid) ? idGuid
                   : Guid.NewGuid(),

                CreatedAt = t.CreatedAt,
                StartedAt = t.StartedAt,
                CompletedAt = t.CompletedAt,

                // Палета из источника
                PalletId = t.StoragePalletCode ?? t.AllocationPalletCode ?? t.Id,
                ProductType = t.ProductSku,

                // Вес пока не приходит, рассчитаем позже из справочника
                WeightKg = 0,
                WeightCategory = "Unknown",

                // Исполнитель (карщик/сборщик)
                ForkliftId = t.AssigneeCode,

                // Источник (Storage) - извлекаем зону из кода ячейки
                FromZone = ExtractZoneFromBinCode(t.StorageBinCode),
                FromSlot = t.StorageBinCode,

                // Назначение (Allocation)
                ToZone = ExtractZoneFromBinCode(t.AllocationBinCode),
                ToSlot = t.AllocationBinCode,

                // Расстояние рассчитаем позже если нужно
                DistanceMeters = null,

                Status = GetTaskStatus(t.Status),

                // Длительность из API или расчёт
                DurationSec = t.DurationSec.HasValue
                    ? (decimal)t.DurationSec.Value
                    : (t.CompletedAt.HasValue && t.StartedAt.HasValue
                        ? (decimal)(t.CompletedAt.Value - t.StartedAt.Value).TotalSeconds
                        : null),

                // Дополнительная информация в поле FailureReason
                FailureReason = t.ActionType != null
                    ? $"ActionType: {t.ActionType}, Template: {t.TemplateName}"
                    : null
            }).ToList();

            await _repository.SaveTasksBatchAsync(records, ct);

            _state.LastTaskId = afterId;
            _state.LastTasksSync = DateTime.UtcNow;

            OnTasksSynced?.Invoke(allTasks);
        }
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
}
