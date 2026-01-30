using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Infrastructure.EventBus;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.Persistence;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;
using WMS.BufferManagement.Layers.Realtime.BufferControl;
using WMS.BufferManagement.Layers.Realtime.Dispatcher;
using WMS.BufferManagement.Layers.Realtime.StateMachine;

namespace WMS.BufferManagement.Services;

/// <summary>
/// Настройки службы управления буфером
/// </summary>
public class BufferManagementSettings
{
    /// <summary>
    /// Интервал цикла управления (мс)
    /// </summary>
    public int ControlCycleMs { get; set; } = 1000;

    /// <summary>
    /// Включено ли управление
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Использовать данные из WMS (true) или симуляцию (false)
    /// </summary>
    public bool UseRealData { get; set; } = false;
}

/// <summary>
/// Основная служба управления буфером.
/// Объединяет данные из WMS, алгоритмы управления и исторические данные.
/// </summary>
public class BufferManagementService : BackgroundService
{
    private readonly IRealTimeDataProvider _dataProvider;
    private readonly IWms1CClient _wmsClient;
    private readonly IHistoricalRepository _repository;
    private readonly HysteresisController _controller;
    private readonly IEventBus _eventBus;
    private readonly BufferManagementSettings _settings;
    private readonly BufferConfig _bufferConfig;
    private readonly ILogger<BufferManagementService> _logger;

    // Внутреннее состояние
    private readonly BufferZone _buffer;
    private BufferState _lastState = BufferState.Normal;
    private DateTime _lastSnapshotTime = DateTime.MinValue;
    private readonly TimeSpan _snapshotInterval = TimeSpan.FromSeconds(30);

    // Метрики
    private int _totalDeliveryRequests;
    private int _criticalInterventions;

    public BufferManagementService(
        IRealTimeDataProvider dataProvider,
        IWms1CClient wmsClient,
        IHistoricalRepository repository,
        IEventBus eventBus,
        IOptions<BufferManagementSettings> settings,
        IOptions<BufferConfig> bufferConfig,
        ILogger<BufferManagementService> logger)
    {
        _dataProvider = dataProvider;
        _wmsClient = wmsClient;
        _repository = repository;
        _eventBus = eventBus;
        _settings = settings.Value;
        _bufferConfig = bufferConfig.Value;
        _logger = logger;

        _buffer = new BufferZone { Capacity = _bufferConfig.Capacity };
        _controller = new HysteresisController(_bufferConfig);

        _controller.StateChanged += OnBufferStateChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Buffer management service is disabled");
            return;
        }

        _logger.LogInformation("Buffer management service starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunControlCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in buffer management control cycle");
            }

            await Task.Delay(_settings.ControlCycleMs, stoppingToken);
        }

        _logger.LogInformation("Buffer management service stopped");
    }

    /// <summary>
    /// Основной цикл управления
    /// </summary>
    private async Task RunControlCycleAsync(CancellationToken ct)
    {
        // 1. Получаем текущее состояние из WMS
        var bufferLevel = _dataProvider.GetCurrentBufferLevel();
        var consumptionRate = _dataProvider.GetCurrentConsumptionRate();
        var activePickers = _dataProvider.GetActivePickersCount();
        var activeForklifts = _dataProvider.GetActiveForkliftsCount();

        // 2. Обновляем внутреннее состояние буфера для контроллера
        // (симулируем уровень на основе данных WMS)
        UpdateInternalBufferState(bufferLevel);

        // 3. Обновляем контроллер
        _controller.Update(_buffer, consumptionRate);

        // 4. Получаем рекомендации
        var requiredRate = _controller.CalculateRequiredDeliveryRate(consumptionRate);
        var palletsToRequest = _controller.GetPalletsToRequest();
        var recommendedForklifts = _controller.GetRecommendedForkliftCount(activeForklifts);

        // 5. Если нужно - создаём задания в WMS
        if (_settings.UseRealData && palletsToRequest > 0 && _controller.IsUrgentDeliveryRequired)
        {
            await CreateDeliveryTasksAsync(palletsToRequest, ct);
        }

        // 6. Записываем снимок состояния
        if (DateTime.UtcNow - _lastSnapshotTime >= _snapshotInterval)
        {
            await SaveBufferSnapshotAsync(bufferLevel, consumptionRate, activeForklifts, activePickers, ct);
            _lastSnapshotTime = DateTime.UtcNow;
        }

        // 7. Логируем состояние
        if (_controller.CurrentState != _lastState)
        {
            _logger.LogInformation(
                "Buffer state: {State}, Level: {Level:P1}, Consumption: {Rate:F1} pal/h, Pickers: {Pickers}, Forklifts: {Forklifts}",
                _controller.CurrentState,
                bufferLevel,
                consumptionRate,
                activePickers,
                activeForklifts);

            _lastState = _controller.CurrentState;
        }
    }

    private void UpdateInternalBufferState(double level)
    {
        // Обновляем внутренний буфер для соответствия уровню из WMS
        var targetCount = (int)(level * _buffer.Capacity);
        var currentCount = _buffer.CurrentCount;

        // Добавляем или удаляем фиктивные палеты для синхронизации уровня
        while (_buffer.CurrentCount < targetCount)
        {
            var dummy = new Pallet
            {
                Product = new Product("SYNC", "Sync Pallet", 10),
                Quantity = 1
            };
            _buffer.TryAdd(dummy);
        }

        while (_buffer.CurrentCount > targetCount && _buffer.CurrentCount > 0)
        {
            _buffer.TakeHeaviest();
        }
    }

    private async Task CreateDeliveryTasksAsync(int count, CancellationToken ct)
    {
        _logger.LogInformation("Requesting {Count} pallets from storage", count);

        for (int i = 0; i < count; i++)
        {
            try
            {
                var request = new CreateTaskRequest
                {
                    PalletId = $"AUTO-{DateTime.UtcNow.Ticks}-{i}",
                    FromZone = "STORAGE",
                    FromSlot = "AUTO",
                    ToZone = "BUFFER",
                    ToSlot = "AUTO",
                    Priority = _controller.CurrentState == BufferState.Critical ? 3 : 2
                };

                var taskId = await _wmsClient.CreateTaskAsync(request, ct);
                _totalDeliveryRequests++;

                _logger.LogDebug("Created delivery task: {TaskId}", taskId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create delivery task");
            }
        }
    }

    private async Task SaveBufferSnapshotAsync(
        double level,
        double consumptionRate,
        int activeForklifts,
        int activePickers,
        CancellationToken ct)
    {
        var deliveryRate = _dataProvider.GetCurrentDeliveryRate();

        var snapshot = new BufferSnapshot
        {
            Time = DateTime.UtcNow,
            BufferLevel = (decimal)level,
            BufferState = _controller.CurrentState.ToString(),
            PalletsCount = (int)(level * _bufferConfig.Capacity),
            ActiveForklifts = activeForklifts,
            ActivePickers = activePickers,
            ConsumptionRate = (decimal)consumptionRate,
            DeliveryRate = (decimal)deliveryRate,
            QueueLength = 0, // TODO: получить из диспетчера
            PendingTasks = 0
        };

        await _repository.SaveBufferSnapshotAsync(snapshot, ct);
    }

    private void OnBufferStateChanged(BufferState previousState, BufferState newState)
    {
        _logger.LogWarning(
            "Buffer state changed: {PreviousState} → {NewState}",
            previousState,
            newState);

        if (newState == BufferState.Critical)
        {
            _criticalInterventions++;
            _logger.LogError(
                "CRITICAL: Buffer level is critical! Intervention #{Count}",
                _criticalInterventions);
        }

        _eventBus.Publish(new Domain.Events.BufferLevelChangedEvent
        {
            PreviousCount = _buffer.CurrentCount,
            CurrentCount = _buffer.CurrentCount,
            Capacity = _buffer.Capacity
        });
    }

    /// <summary>
    /// Получить статистику службы
    /// </summary>
    public BufferManagementStats GetStats()
    {
        return new BufferManagementStats
        {
            CurrentLevel = _dataProvider.GetCurrentBufferLevel(),
            CurrentState = _controller.CurrentState,
            ConsumptionRate = _dataProvider.GetCurrentConsumptionRate(),
            DeliveryRate = _dataProvider.GetCurrentDeliveryRate(),
            ActivePickers = _dataProvider.GetActivePickersCount(),
            ActiveForklifts = _dataProvider.GetActiveForkliftsCount(),
            TotalDeliveryRequests = _totalDeliveryRequests,
            CriticalInterventions = _criticalInterventions
        };
    }
}

public class BufferManagementStats
{
    public double CurrentLevel { get; set; }
    public BufferState CurrentState { get; set; }
    public double ConsumptionRate { get; set; }
    public double DeliveryRate { get; set; }
    public int ActivePickers { get; set; }
    public int ActiveForklifts { get; set; }
    public int TotalDeliveryRequests { get; set; }
    public int CriticalInterventions { get; set; }
}
