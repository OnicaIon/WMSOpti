using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WMS.BufferManagement.Layers.Historical.Persistence;

namespace WMS.BufferManagement.Services;

/// <summary>
/// Настройки службы агрегации
/// </summary>
public class AggregationSettings
{
    /// <summary>
    /// Интервал расчёта агрегатов (мс)
    /// </summary>
    public int IntervalMs { get; set; } = 300000; // 5 минут

    /// <summary>
    /// Включена ли служба
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Глубина анализа для паттернов (дней)
    /// </summary>
    public int PatternAnalysisDays { get; set; } = 30;

    /// <summary>
    /// Глубина анализа для маршрутов (дней)
    /// </summary>
    public int RouteAnalysisDays { get; set; } = 7;
}

/// <summary>
/// Служба периодического расчёта агрегатов для ML и аналитики
/// </summary>
public class AggregationService : BackgroundService
{
    private readonly IHistoricalRepository _repository;
    private readonly AggregationSettings _settings;
    private readonly ILogger<AggregationService> _logger;

    // Кэш рассчитанных агрегатов
    private readonly Dictionary<string, PickerPerformanceAggregate> _pickerAggregates = new();
    private readonly Dictionary<string, RoutePerformanceAggregate> _routeAggregates = new();
    private readonly List<HourlyDemandPattern> _demandPatterns = new();

    private DateTime _lastCalculation = DateTime.MinValue;

    public IReadOnlyDictionary<string, PickerPerformanceAggregate> PickerAggregates => _pickerAggregates;
    public IReadOnlyDictionary<string, RoutePerformanceAggregate> RouteAggregates => _routeAggregates;
    public IReadOnlyList<HourlyDemandPattern> DemandPatterns => _demandPatterns;

    public AggregationService(
        IHistoricalRepository repository,
        IOptions<AggregationSettings> settings,
        ILogger<AggregationService> logger)
    {
        _repository = repository;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Aggregation service is disabled");
            return;
        }

        _logger.LogInformation("Aggregation service starting...");

        // Первичный расчёт при старте
        await CalculateAggregatesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_settings.IntervalMs, stoppingToken);

            try
            {
                await CalculateAggregatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating aggregates");
            }
        }
    }

    private async Task CalculateAggregatesAsync(CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("Starting aggregate calculation...");

        try
        {
            // 1. Агрегаты по сборщикам
            await CalculatePickerAggregatesAsync(ct);

            // 2. Агрегаты по маршрутам
            await CalculateRouteAggregatesAsync(ct);

            // 3. Паттерны спроса по часам
            await CalculateDemandPatternsAsync(ct);

            _lastCalculation = DateTime.UtcNow;
            var duration = DateTime.UtcNow - startTime;

            _logger.LogInformation(
                "Aggregates calculated in {Duration}ms: {PickerCount} pickers, {RouteCount} routes, {PatternCount} demand patterns",
                duration.TotalMilliseconds,
                _pickerAggregates.Count,
                _routeAggregates.Count,
                _demandPatterns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate aggregates");
        }
    }

    private async Task CalculatePickerAggregatesAsync(CancellationToken ct)
    {
        var patterns = await _repository.GetPickerHourlyPatternsAsync(_settings.PatternAnalysisDays, ct);

        _pickerAggregates.Clear();

        foreach (var group in patterns.GroupBy(p => p.PickerId))
        {
            var pickerId = group.Key;
            var hourlyData = group.ToList();

            var aggregate = new PickerPerformanceAggregate
            {
                PickerId = pickerId,
                AvgConsumptionRate = (double)hourlyData.Average(h => h.AvgConsumptionRate),
                AvgEfficiency = (double)hourlyData.Average(h => h.AvgEfficiency),
                TotalSamples = hourlyData.Sum(h => h.SampleCount),
                PeakHour = hourlyData.OrderByDescending(h => h.AvgConsumptionRate).First().HourOfDay,
                SlowHour = hourlyData.OrderBy(h => h.AvgConsumptionRate).First().HourOfDay,
                HourlyRates = hourlyData.ToDictionary(h => h.HourOfDay, h => (double)h.AvgConsumptionRate)
            };

            _pickerAggregates[pickerId] = aggregate;
        }
    }

    private async Task CalculateRouteAggregatesAsync(CancellationToken ct)
    {
        var routes = await _repository.GetSlowestRoutesAsync(100, _settings.RouteAnalysisDays, ct);

        _routeAggregates.Clear();

        foreach (var route in routes)
        {
            var key = $"{route.FromZone}→{route.ToZone}";

            _routeAggregates[key] = new RoutePerformanceAggregate
            {
                FromZone = route.FromZone,
                ToZone = route.ToZone,
                AvgDurationSec = (double)route.AvgDurationSec,
                TaskCount = route.TaskCount
            };
        }
    }

    private async Task CalculateDemandPatternsAsync(CancellationToken ct)
    {
        // Получаем статистику буфера за последние N дней
        var fromTime = DateTime.UtcNow.AddDays(-_settings.PatternAnalysisDays);
        var toTime = DateTime.UtcNow;

        var bufferData = await _repository.GetBufferTimeSeriesAsync(fromTime, toTime, TimeSpan.FromHours(1), ct);

        _demandPatterns.Clear();

        // Группируем по часу дня и дню недели
        var grouped = bufferData
            .GroupBy(b => new { Hour = b.Time.Hour, DayOfWeek = (int)b.Time.DayOfWeek })
            .Select(g => new HourlyDemandPattern
            {
                HourOfDay = g.Key.Hour,
                DayOfWeek = g.Key.DayOfWeek,
                AvgConsumptionRate = (double)(g.Average(b => b.ConsumptionRate) ?? 0),
                AvgBufferLevel = (double)g.Average(b => b.BufferLevel),
                AvgActivePickers = (int)g.Average(b => b.ActivePickers),
                SampleCount = g.Count()
            })
            .OrderBy(p => p.DayOfWeek)
            .ThenBy(p => p.HourOfDay)
            .ToList();

        _demandPatterns.AddRange(grouped);
    }

    /// <summary>
    /// Получить прогноз скорости сборщика на указанный час
    /// </summary>
    public double GetPickerSpeedForecast(string pickerId, int hourOfDay)
    {
        if (_pickerAggregates.TryGetValue(pickerId, out var aggregate))
        {
            if (aggregate.HourlyRates.TryGetValue(hourOfDay, out var rate))
            {
                return rate;
            }
            return aggregate.AvgConsumptionRate;
        }

        return 5.0; // Значение по умолчанию
    }

    /// <summary>
    /// Получить прогноз времени маршрута
    /// </summary>
    public double? GetRouteDurationForecast(string fromZone, string toZone)
    {
        var key = $"{fromZone}→{toZone}";

        if (_routeAggregates.TryGetValue(key, out var aggregate))
        {
            return aggregate.AvgDurationSec;
        }

        return null;
    }

    /// <summary>
    /// Получить прогноз потребления на указанное время
    /// </summary>
    public double GetDemandForecast(DateTime targetTime)
    {
        var hour = targetTime.Hour;
        var dayOfWeek = (int)targetTime.DayOfWeek;

        var pattern = _demandPatterns
            .FirstOrDefault(p => p.HourOfDay == hour && p.DayOfWeek == dayOfWeek);

        return pattern?.AvgConsumptionRate ?? 180.0; // Значение по умолчанию
    }
}

/// <summary>
/// Агрегированные показатели сборщика
/// </summary>
public class PickerPerformanceAggregate
{
    public string PickerId { get; set; } = string.Empty;
    public double AvgConsumptionRate { get; set; }
    public double AvgEfficiency { get; set; }
    public int TotalSamples { get; set; }
    public int PeakHour { get; set; }
    public int SlowHour { get; set; }
    public Dictionary<int, double> HourlyRates { get; set; } = new();
}

/// <summary>
/// Агрегированные показатели маршрута
/// </summary>
public class RoutePerformanceAggregate
{
    public string FromZone { get; set; } = string.Empty;
    public string ToZone { get; set; } = string.Empty;
    public double AvgDurationSec { get; set; }
    public int TaskCount { get; set; }
}

/// <summary>
/// Паттерн спроса по часам
/// </summary>
public class HourlyDemandPattern
{
    public int HourOfDay { get; set; }
    public int DayOfWeek { get; set; }
    public double AvgConsumptionRate { get; set; }
    public double AvgBufferLevel { get; set; }
    public int AvgActivePickers { get; set; }
    public int SampleCount { get; set; }
}
