using Microsoft.Extensions.Logging;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Layers.Historical.Persistence;

namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

/// <summary>
/// Провайдер данных реального времени для алгоритмов управления буфером.
/// Объединяет данные из WMS sync и исторические агрегаты из TimescaleDB.
/// </summary>
public interface IRealTimeDataProvider
{
    /// <summary>
    /// Получить текущий уровень буфера (0-1)
    /// </summary>
    double GetCurrentBufferLevel();

    /// <summary>
    /// Получить текущую скорость потребления (палет/час)
    /// </summary>
    double GetCurrentConsumptionRate();

    /// <summary>
    /// Получить текущую скорость доставки (палет/час)
    /// </summary>
    double GetCurrentDeliveryRate();

    /// <summary>
    /// Получить количество активных сборщиков
    /// </summary>
    int GetActivePickersCount();

    /// <summary>
    /// Получить количество активных карщиков
    /// </summary>
    int GetActiveForkliftsCount();

    /// <summary>
    /// Получить состояние конкретного карщика
    /// </summary>
    ForkliftState? GetForkliftState(string forkliftId);

    /// <summary>
    /// Получить среднюю скорость сборщика (для прогноза)
    /// </summary>
    Task<double> GetPickerAverageSpeedAsync(string pickerId, CancellationToken ct = default);

    /// <summary>
    /// Получить прогноз потребления на следующие N минут
    /// </summary>
    Task<double> GetDemandForecastAsync(int minutes = 15, CancellationToken ct = default);

    /// <summary>
    /// Получить статистику по маршруту (среднее время доставки)
    /// </summary>
    Task<double?> GetRouteAverageDurationAsync(string fromZone, string toZone, CancellationToken ct = default);
}

public class ForkliftState
{
    public string Id { get; set; } = string.Empty;
    public string? OperatorName { get; set; }
    public int Status { get; set; }
    public string? CurrentTaskId { get; set; }
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }
    public DateTime? LastUpdate { get; set; }

    public bool IsAvailable => Status == 0; // Idle
    public bool IsBusy => Status is 1 or 2 or 3; // EnRoute, Loading, Unloading
}

/// <summary>
/// Реализация провайдера данных реального времени
/// </summary>
public class RealTimeDataProvider : IRealTimeDataProvider
{
    private readonly WmsDataSyncService _syncService;
    private readonly IHistoricalRepository _repository;
    private readonly ILogger<RealTimeDataProvider> _logger;

    // Кэш для расчётов скорости
    private readonly Queue<(DateTime Time, int Count)> _bufferHistory = new();
    private readonly object _historyLock = new();
    private const int MaxHistorySize = 60; // 5 минут при интервале 5 сек

    // Кэш агрегатов
    private DateTime _lastAggregateUpdate;
    private Dictionary<string, double> _pickerSpeedCache = new();
    private Dictionary<string, double> _routeDurationCache = new();

    public RealTimeDataProvider(
        WmsDataSyncService syncService,
        IHistoricalRepository repository,
        ILogger<RealTimeDataProvider> logger)
    {
        _syncService = syncService;
        _repository = repository;
        _logger = logger;

        // Подписываемся на обновления буфера
        _syncService.OnBufferStateSynced += OnBufferStateUpdated;
    }

    private void OnBufferStateUpdated(WmsBufferState state)
    {
        lock (_historyLock)
        {
            _bufferHistory.Enqueue((DateTime.UtcNow, state.CurrentCount));

            while (_bufferHistory.Count > MaxHistorySize)
            {
                _bufferHistory.Dequeue();
            }
        }
    }

    public double GetCurrentBufferLevel()
    {
        var state = _syncService.CurrentBufferState;
        if (state == null || state.Capacity == 0)
            return 0.5; // Значение по умолчанию

        return (double)state.CurrentCount / state.Capacity;
    }

    public double GetCurrentConsumptionRate()
    {
        // Рассчитываем скорость потребления по истории буфера
        lock (_historyLock)
        {
            if (_bufferHistory.Count < 2)
                return 0;

            var points = _bufferHistory.ToArray();
            var oldest = points[0];
            var newest = points[^1];

            var timeDiffHours = (newest.Time - oldest.Time).TotalHours;
            if (timeDiffHours <= 0)
                return 0;

            // Если уровень падает - это потребление
            var countDiff = oldest.Count - newest.Count;
            if (countDiff <= 0)
                return 0;

            return countDiff / timeDiffHours;
        }
    }

    public double GetCurrentDeliveryRate()
    {
        // Рассчитываем скорость доставки по завершённым заданиям
        // Пока упрощённо - через изменение буфера
        lock (_historyLock)
        {
            if (_bufferHistory.Count < 2)
                return 0;

            var points = _bufferHistory.ToArray();
            var oldest = points[0];
            var newest = points[^1];

            var timeDiffHours = (newest.Time - oldest.Time).TotalHours;
            if (timeDiffHours <= 0)
                return 0;

            // Если уровень растёт - это доставка
            var countDiff = newest.Count - oldest.Count;
            if (countDiff <= 0)
                return 0;

            return countDiff / timeDiffHours;
        }
    }

    public int GetActivePickersCount()
    {
        return _syncService.CurrentPickers
            .Count(p => p.Status == 1); // Status 1 = Active/Working
    }

    public int GetActiveForkliftsCount()
    {
        return _syncService.CurrentForklifts
            .Count(f => f.Status is 0 or 1 or 2 or 3); // Не на обслуживании
    }

    public ForkliftState? GetForkliftState(string forkliftId)
    {
        var forklift = _syncService.CurrentForklifts
            .FirstOrDefault(f => f.Id == forkliftId);

        if (forklift == null)
            return null;

        return new ForkliftState
        {
            Id = forklift.Id,
            OperatorName = forklift.OperatorName,
            Status = forklift.Status,
            CurrentTaskId = forklift.CurrentTaskId,
            PositionX = forklift.PositionX,
            PositionY = forklift.PositionY,
            LastUpdate = forklift.LastUpdateAt
        };
    }

    public async Task<double> GetPickerAverageSpeedAsync(string pickerId, CancellationToken ct = default)
    {
        // Проверяем кэш
        await RefreshAggregatesIfNeededAsync(ct);

        if (_pickerSpeedCache.TryGetValue(pickerId, out var speed))
            return speed;

        // Запрашиваем из БД
        var stats = await _repository.GetPickerHourlyStatsAsync(
            pickerId,
            DateTime.UtcNow.AddDays(-7),
            DateTime.UtcNow,
            ct);

        if (stats.Count == 0)
            return 5.0; // Значение по умолчанию (палет/час)

        var avgSpeed = (double)stats.Average(s => s.AvgRate);
        _pickerSpeedCache[pickerId] = avgSpeed;

        return avgSpeed;
    }

    public async Task<double> GetDemandForecastAsync(int minutes = 15, CancellationToken ct = default)
    {
        // Простой прогноз: текущая скорость * активные сборщики * время
        var currentRate = GetCurrentConsumptionRate();
        var activePickers = GetActivePickersCount();

        if (currentRate <= 0 && activePickers > 0)
        {
            // Если нет данных о скорости, используем среднюю из истории
            var patterns = await _repository.GetPickerHourlyPatternsAsync(30, ct);
            var currentHour = DateTime.UtcNow.Hour;

            var hourPatterns = patterns
                .Where(p => p.HourOfDay == currentHour)
                .ToList();

            if (hourPatterns.Count > 0)
            {
                currentRate = (double)hourPatterns.Average(p => p.AvgConsumptionRate) * activePickers;
            }
            else
            {
                currentRate = 5.0 * activePickers; // Fallback
            }
        }

        // Прогноз = скорость * время в часах
        return currentRate * (minutes / 60.0);
    }

    public async Task<double?> GetRouteAverageDurationAsync(string fromZone, string toZone, CancellationToken ct = default)
    {
        var cacheKey = $"{fromZone}→{toZone}";

        await RefreshAggregatesIfNeededAsync(ct);

        if (_routeDurationCache.TryGetValue(cacheKey, out var duration))
            return duration;

        // Запрашиваем статистику маршрутов
        var routes = await _repository.GetSlowestRoutesAsync(100, 30, ct);

        var route = routes.FirstOrDefault(r =>
            r.FromZone == fromZone && r.ToZone == toZone);

        if (route != null)
        {
            var avgDuration = (double)route.AvgDurationSec;
            _routeDurationCache[cacheKey] = avgDuration;
            return avgDuration;
        }

        return null;
    }

    private async Task RefreshAggregatesIfNeededAsync(CancellationToken ct)
    {
        // Обновляем кэш раз в 5 минут
        if ((DateTime.UtcNow - _lastAggregateUpdate).TotalMinutes < 5)
            return;

        _logger.LogDebug("Refreshing aggregate caches...");

        // Очищаем кэш
        _pickerSpeedCache.Clear();
        _routeDurationCache.Clear();

        // Предзагружаем паттерны сборщиков
        var patterns = await _repository.GetPickerHourlyPatternsAsync(30, ct);
        foreach (var group in patterns.GroupBy(p => p.PickerId))
        {
            var avgSpeed = (double)group.Average(p => p.AvgConsumptionRate);
            _pickerSpeedCache[group.Key] = avgSpeed;
        }

        // Предзагружаем маршруты
        var routes = await _repository.GetSlowestRoutesAsync(100, 30, ct);
        foreach (var route in routes)
        {
            var key = $"{route.FromZone}→{route.ToZone}";
            _routeDurationCache[key] = (double)route.AvgDurationSec;
        }

        _lastAggregateUpdate = DateTime.UtcNow;
    }
}
