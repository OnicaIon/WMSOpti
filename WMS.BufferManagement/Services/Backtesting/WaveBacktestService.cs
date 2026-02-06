using Microsoft.Extensions.Logging;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.Persistence;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;
using WMS.BufferManagement.Layers.Tactical;

namespace WMS.BufferManagement.Services.Backtesting;

/// <summary>
/// Сервис бэктестирования волн дистрибьюции.
/// Сравнивает фактическое выполнение волны с оптимизированным вариантом.
/// READ-ONLY: не модифицирует данные в БД.
/// </summary>
public class WaveBacktestService
{
    private readonly IWms1CClient _wmsClient;
    private readonly IHistoricalRepository _repository;
    private readonly PalletAssignmentOptimizer _optimizer;
    private readonly ILogger<WaveBacktestService> _logger;

    // Значение по умолчанию, если нет статистики маршрута (секунды)
    private const double DefaultRouteDurationSec = 120.0;
    // Время погрузки/разгрузки (секунды)
    private const double LoadUnloadTimeSec = 30.0;

    public WaveBacktestService(
        IWms1CClient wmsClient,
        IHistoricalRepository repository,
        PalletAssignmentOptimizer optimizer,
        ILogger<WaveBacktestService> logger)
    {
        _wmsClient = wmsClient;
        _repository = repository;
        _optimizer = optimizer;
        _logger = logger;
    }

    /// <summary>
    /// Запустить полный бэктест волны
    /// </summary>
    public async Task<BacktestResult> RunBacktestAsync(string waveNumber, CancellationToken ct = default)
    {
        _logger.LogInformation("Запуск бэктеста волны {WaveNumber}", waveNumber);

        // 1. Получить данные волны из 1С
        var waveData = await FetchWaveDataAsync(waveNumber, ct);
        if (waveData == null)
            throw new InvalidOperationException($"Волна {waveNumber} не найдена в 1С");

        _logger.LogInformation("Получено: {Repl} replenishment, {Dist} distribution задач",
            waveData.ReplenishmentTasks.Count, waveData.DistributionTasks.Count);

        // 2. Рассчитать фактическое время
        var actualTimeline = CalculateActualTimeline(waveData);
        _logger.LogInformation("Фактическое время: {WallClock} (активное: {Active})",
            actualTimeline.WallClockDuration, actualTimeline.ActiveDuration);

        // 3. Загрузить статистику из БД
        var routeStats = await _repository.GetRouteStatisticsAsync(cancellationToken: ct);
        var pickerStats = await _repository.GetPickerProductStatsAsync(cancellationToken: ct);

        _logger.LogInformation("Статистика: {Routes} маршрутов, {Pickers} пикер-товар",
            routeStats.Count, pickerStats.Count);

        // 4. Оптимизировать назначение заданий
        var optimizedPlan = OptimizeTasks(waveData, routeStats);
        _logger.LogInformation("Оптимизация: optimal={IsOptimal}", optimizedPlan.IsOptimal);

        // 5. Симулировать оптимизированное выполнение
        var simulatedTimeline = SimulateOptimized(optimizedPlan, routeStats, pickerStats);
        _logger.LogInformation("Симулированное время: {Duration}", simulatedTimeline.TotalDuration);

        // 6. Собрать результат
        var result = BuildResult(waveData, actualTimeline, simulatedTimeline, optimizedPlan, routeStats.Count, pickerStats.Count);

        return result;
    }

    /// <summary>
    /// Получить данные волны из 1С
    /// </summary>
    public async Task<WaveTasksResponse?> FetchWaveDataAsync(string waveNumber, CancellationToken ct)
    {
        try
        {
            return await _wmsClient.GetWaveTasksAsync(waveNumber, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка получения данных волны {WaveNumber} из 1С", waveNumber);
            return null;
        }
    }

    /// <summary>
    /// Рассчитать фактическое время выполнения волны
    /// </summary>
    public ActualTimeline CalculateActualTimeline(WaveTasksResponse data)
    {
        var allGroups = data.ReplenishmentTasks.Concat(data.DistributionTasks).ToList();
        var allActions = allGroups.SelectMany(g => g.Actions.Select(a => new { Group = g, Action = a })).ToList();

        if (!allActions.Any())
            return new ActualTimeline { StartTime = data.WaveDate, EndTime = data.WaveDate };

        // Группируем по работникам
        var workerTimelines = allGroups
            .Where(g => !string.IsNullOrEmpty(g.AssigneeCode))
            .GroupBy(g => g.AssigneeCode)
            .Select(group =>
            {
                var firstGroup = group.First();
                var actions = group.SelectMany(g => g.Actions).ToList();

                // Рассчитываем длительность каждого действия
                var actionTimings = actions.Select(a =>
                {
                    double dur = a.DurationSec ?? 0;
                    if (dur <= 0 && a.CompletedAt.HasValue && a.StartedAt.HasValue)
                        dur = (a.CompletedAt.Value - a.StartedAt.Value).TotalSeconds;
                    if (dur < 0) dur = 0;

                    return new ActionTiming
                    {
                        FromBin = a.StorageBin,
                        ToBin = a.AllocationBin,
                        FromZone = ExtractZone(a.StorageBin),
                        ToZone = ExtractZone(a.AllocationBin),
                        ProductCode = a.ProductCode,
                        ProductName = a.ProductName,
                        WeightKg = a.WeightKg,
                        Qty = a.QtyFact > 0 ? a.QtyFact : a.QtyPlan,
                        DurationSec = dur,
                        WorkerCode = firstGroup.AssigneeCode
                    };
                }).ToList();

                // Фактическое время работника = сумма длительностей его действий
                var totalWorkerSec = actionTimings.Sum(a => a.DurationSec);

                // Определяем start/end для хронологии
                var completed = actions.Where(a => a.CompletedAt.HasValue).ToList();
                var started = actions.Where(a => a.StartedAt.HasValue).ToList();
                var workerStart = started.Any() ? started.Min(a => a.StartedAt!.Value)
                    : completed.Any() ? completed.Min(a => a.CompletedAt!.Value)
                    : data.WaveDate;
                var workerEnd = completed.Any() ? completed.Max(a => a.CompletedAt!.Value)
                    : data.WaveDate;

                return new WorkerTimeline
                {
                    WorkerCode = firstGroup.AssigneeCode,
                    WorkerName = firstGroup.AssigneeName,
                    Role = firstGroup.TemplateCode == "029" ? "Forklift" : firstGroup.TemplateCode == "031" ? "Picker" : "Unknown",
                    StartTime = workerStart,
                    EndTime = workerEnd,
                    TaskCount = actions.Count,
                    Actions = actionTimings
                };
            })
            .ToList();

        // Общее время волны: от самого раннего startedAt до самого позднего completedAt
        var allCompleted = allActions.Where(a => a.Action.CompletedAt.HasValue).ToList();
        var allStarted = allActions.Where(a => a.Action.StartedAt.HasValue).ToList();

        var startTime = allStarted.Any() ? allStarted.Min(a => a.Action.StartedAt!.Value)
            : allCompleted.Any() ? allCompleted.Min(a => a.Action.CompletedAt!.Value)
            : data.WaveDate;
        var endTime = allCompleted.Any() ? allCompleted.Max(a => a.Action.CompletedAt!.Value)
            : data.WaveDate;

        // Активное время: merge интервалов [startedAt, completedAt] всех действий
        // Исключает перерывы, ночь и другие паузы когда никто не работал
        var activeIntervals = allActions
            .Where(a => a.Action.StartedAt.HasValue && a.Action.CompletedAt.HasValue)
            .Select(a => (start: a.Action.StartedAt!.Value, end: a.Action.CompletedAt!.Value))
            .Where(i => i.end > i.start)
            .OrderBy(i => i.start)
            .ToList();
        var activeDuration = MergeIntervalsAndSum(activeIntervals);

        return new ActualTimeline
        {
            StartTime = startTime,
            EndTime = endTime,
            ActiveDuration = activeDuration,
            TotalActions = allActions.Count,
            WorkerTimelines = workerTimelines
        };
    }

    /// <summary>
    /// Оптимизировать назначение заданий через CP-SAT solver
    /// </summary>
    public OptimizedPlan OptimizeTasks(WaveTasksResponse data, List<RouteStatistics> routeStats)
    {
        var allGroups = data.ReplenishmentTasks.Concat(data.DistributionTasks).ToList();
        var allActions = allGroups.SelectMany(g => g.Actions.Select(a => new { Group = g, Action = a })).ToList();

        if (!allActions.Any())
            return new OptimizedPlan();

        // Уникальные работники (виртуальные карщики)
        var workerCodes = allGroups.Select(g => g.AssigneeCode).Distinct().ToList();

        // Создаём domain entities для оптимизатора
        var deliveryTasks = new List<DeliveryTask>();
        var actionMappings = new Dictionary<string, (WaveTaskGroup Group, WaveTaskAction Action)>();

        foreach (var item in allActions)
        {
            var taskId = $"{item.Group.TaskNumber}_{item.Action.SortOrder}";
            var product = new Product(
                item.Action.ProductCode,
                item.Action.ProductName,
                (double)item.Action.WeightKg);

            var pallet = new Pallet
            {
                Id = taskId,
                Product = product,
                Quantity = item.Action.QtyPlan > 0 ? item.Action.QtyPlan : 1,
                StorageDistanceMeters = EstimateDistance(item.Action.StorageBin, item.Action.AllocationBin, routeStats)
            };

            var task = new DeliveryTask
            {
                Id = taskId,
                Pallet = pallet,
                Status = DeliveryTaskStatus.Pending
            };

            deliveryTasks.Add(task);
            actionMappings[taskId] = (item.Group, item.Action);
        }

        // Создаём виртуальных карщиков
        var forklifts = workerCodes.Select(code => new Forklift
        {
            Id = code,
            Name = allGroups.First(g => g.AssigneeCode == code).AssigneeName,
            State = Domain.Entities.ForkliftState.Idle,
            SpeedMetersPerSecond = 2.0,
            LoadUnloadTimeSeconds = LoadUnloadTimeSec
        }).ToList();

        // Запускаем оптимизатор
        var result = _optimizer.Optimize(deliveryTasks, forklifts);

        // Конвертируем назначения в план
        var plan = new OptimizedPlan
        {
            IsOptimal = result.IsOptimal,
            SolverObjective = result.ObjectiveValue,
            SolverTime = result.SolverTime
        };

        if (result.IsFeasible)
        {
            foreach (var assignment in result.Assignments)
            {
                var workerCode = assignment.Forklift.Id;
                if (!plan.WorkerAssignments.ContainsKey(workerCode))
                    plan.WorkerAssignments[workerCode] = new List<ActionTiming>();

                if (actionMappings.TryGetValue(assignment.Task.Id, out var mapping))
                {
                    // Сохраняем фактическую длительность из 1С для честного сравнения
                    double dur = mapping.Action.DurationSec ?? 0;
                    if (dur <= 0 && mapping.Action.CompletedAt.HasValue && mapping.Action.StartedAt.HasValue)
                        dur = (mapping.Action.CompletedAt.Value - mapping.Action.StartedAt.Value).TotalSeconds;
                    if (dur < 0) dur = 0;

                    plan.WorkerAssignments[workerCode].Add(new ActionTiming
                    {
                        FromBin = mapping.Action.StorageBin,
                        ToBin = mapping.Action.AllocationBin,
                        FromZone = ExtractZone(mapping.Action.StorageBin),
                        ToZone = ExtractZone(mapping.Action.AllocationBin),
                        ProductCode = mapping.Action.ProductCode,
                        ProductName = mapping.Action.ProductName,
                        WeightKg = mapping.Action.WeightKg,
                        Qty = mapping.Action.QtyPlan,
                        DurationSec = dur,
                        WorkerCode = workerCode
                    });
                }
            }

            // Сортируем задания внутри каждого работника по весу (heavy-on-bottom)
            foreach (var kvp in plan.WorkerAssignments)
            {
                plan.WorkerAssignments[kvp.Key] = kvp.Value
                    .OrderByDescending(a => a.WeightKg)
                    .ToList();
            }
        }
        else
        {
            // Fallback: распределяем round-robin по работникам, сортируя по весу
            var sorted = allActions.OrderByDescending(a => a.Action.WeightKg).ToList();
            int idx = 0;
            foreach (var item in sorted)
            {
                var workerCode = workerCodes[idx % workerCodes.Count];
                if (!plan.WorkerAssignments.ContainsKey(workerCode))
                    plan.WorkerAssignments[workerCode] = new List<ActionTiming>();

                // Сохраняем фактическую длительность из 1С для честного сравнения
                double dur = item.Action.DurationSec ?? 0;
                if (dur <= 0 && item.Action.CompletedAt.HasValue && item.Action.StartedAt.HasValue)
                    dur = (item.Action.CompletedAt.Value - item.Action.StartedAt.Value).TotalSeconds;
                if (dur < 0) dur = 0;

                plan.WorkerAssignments[workerCode].Add(new ActionTiming
                {
                    FromBin = item.Action.StorageBin,
                    ToBin = item.Action.AllocationBin,
                    FromZone = ExtractZone(item.Action.StorageBin),
                    ToZone = ExtractZone(item.Action.AllocationBin),
                    ProductCode = item.Action.ProductCode,
                    ProductName = item.Action.ProductName,
                    WeightKg = item.Action.WeightKg,
                    Qty = item.Action.QtyPlan,
                    DurationSec = dur,
                    WorkerCode = workerCode
                });
                idx++;
            }
        }

        return plan;
    }

    /// <summary>
    /// Симулировать оптимизированное выполнение
    /// </summary>
    public SimulatedTimeline SimulateOptimized(
        OptimizedPlan plan,
        List<RouteStatistics> routeStats,
        List<PickerProductStats> pickerStats)
    {
        var timeline = new SimulatedTimeline();

        // Строим lookup для быстрого поиска
        var routeLookup = routeStats
            .GroupBy(r => $"{r.FromZone}→{r.ToZone}")
            .ToDictionary(g => g.Key, g => g.First());

        var pickerLookup = pickerStats
            .GroupBy(p => $"{p.PickerId}:{p.ProductSku}")
            .ToDictionary(g => g.Key, g => g.First());

        // Среднее фактическое время действия из данных волны — для default fallback
        // вместо фиксированных 120с
        var allActualDurations = plan.WorkerAssignments
            .SelectMany(kvp => kvp.Value)
            .Where(a => a.DurationSec > 0)
            .Select(a => a.DurationSec)
            .ToList();
        var waveMeanDurationSec = allActualDurations.Any()
            ? allActualDurations.Average()
            : DefaultRouteDurationSec;
        timeline.WaveMeanDurationSec = waveMeanDurationSec;

        foreach (var (workerCode, actions) in plan.WorkerAssignments)
        {
            var workerTimeline = new SimulatedWorkerTimeline
            {
                WorkerCode = workerCode,
                TaskCount = actions.Count
            };

            double totalWorkerSec = 0;

            foreach (var action in actions)
            {
                var (estimatedSec, source) = EstimateActionDuration(
                    action, workerCode, routeLookup, pickerLookup, waveMeanDurationSec);

                workerTimeline.Actions.Add(new SimulatedAction
                {
                    FromBin = action.FromBin,
                    ToBin = action.ToBin,
                    FromZone = action.FromZone,
                    ToZone = action.ToZone,
                    ProductCode = action.ProductCode,
                    WeightKg = action.WeightKg,
                    EstimatedDurationSec = estimatedSec,
                    DurationSource = source
                });

                totalWorkerSec += estimatedSec;
            }

            workerTimeline.Duration = TimeSpan.FromSeconds(totalWorkerSec);
            timeline.WorkerTimelines.Add(workerTimeline);
        }

        // Общее время = max длительности по работникам (параллельная работа)
        timeline.TotalDuration = timeline.WorkerTimelines.Any()
            ? timeline.WorkerTimelines.Max(w => w.Duration)
            : TimeSpan.Zero;

        return timeline;
    }

    /// <summary>
    /// Собрать итоговый результат бэктеста
    /// </summary>
    public BacktestResult BuildResult(
        WaveTasksResponse data,
        ActualTimeline actual,
        SimulatedTimeline simulated,
        OptimizedPlan plan,
        int routeStatsCount,
        int pickerStatsCount)
    {
        // Сравниваем активное время (без перерывов/ночей) с оптимизированным
        var actualDuration = actual.ActiveDuration;
        var optimizedDuration = simulated.TotalDuration;
        var improvementTime = actualDuration - optimizedDuration;
        var improvementPercent = actualDuration.TotalSeconds > 0
            ? (improvementTime.TotalSeconds / actualDuration.TotalSeconds) * 100
            : 0;

        // Собираем разбивку по работникам
        var workerBreakdowns = new List<WorkerBreakdown>();
        foreach (var actualWorker in actual.WorkerTimelines)
        {
            var simWorker = simulated.WorkerTimelines
                .FirstOrDefault(w => w.WorkerCode == actualWorker.WorkerCode);

            var optDuration = simWorker?.Duration ?? actualWorker.Duration;
            var optTasks = simWorker?.TaskCount ?? actualWorker.TaskCount;
            var workerImprovement = actualWorker.Duration.TotalSeconds > 0
                ? ((actualWorker.Duration - optDuration).TotalSeconds / actualWorker.Duration.TotalSeconds) * 100
                : 0;

            workerBreakdowns.Add(new WorkerBreakdown
            {
                WorkerCode = actualWorker.WorkerCode,
                WorkerName = actualWorker.WorkerName,
                Role = actualWorker.Role,
                ActualTasks = actualWorker.TaskCount,
                OptimizedTasks = optTasks,
                ActualDuration = actualWorker.Duration,
                OptimizedDuration = optDuration,
                ImprovementPercent = workerImprovement
            });
        }

        // Собираем детали заданий
        var taskDetails = new List<TaskDetail>();
        var allGroups = data.ReplenishmentTasks.Concat(data.DistributionTasks).ToList();

        foreach (var group in allGroups)
        {
            var taskType = data.ReplenishmentTasks.Contains(group) ? "Replenishment" : "Distribution";

            foreach (var action in group.Actions)
            {
                // Ищем оптимизированное время для этого действия
                var simAction = simulated.WorkerTimelines
                    .SelectMany(w => w.Actions)
                    .FirstOrDefault(a => a.FromBin == action.StorageBin &&
                                         a.ToBin == action.AllocationBin &&
                                         a.ProductCode == action.ProductCode);

                // Ищем, кому было переназначено
                var optimizedWorker = plan.WorkerAssignments
                    .FirstOrDefault(kvp => kvp.Value.Any(a =>
                        a.FromBin == action.StorageBin &&
                        a.ToBin == action.AllocationBin &&
                        a.ProductCode == action.ProductCode));

                taskDetails.Add(new TaskDetail
                {
                    TaskNumber = group.TaskNumber,
                    WorkerCode = group.AssigneeCode,
                    TaskType = taskType,
                    FromBin = action.StorageBin,
                    ToBin = action.AllocationBin,
                    FromZone = ExtractZone(action.StorageBin),
                    ToZone = ExtractZone(action.AllocationBin),
                    ProductCode = action.ProductCode,
                    WeightKg = action.WeightKg,
                    Qty = action.QtyFact > 0 ? action.QtyFact : action.QtyPlan,
                    ActualDurationSec = action.DurationSec,
                    OptimizedDurationSec = simAction?.EstimatedDurationSec ?? DefaultRouteDurationSec,
                    DurationSource = simAction?.DurationSource ?? "default",
                    OptimizedWorkerCode = optimizedWorker.Key
                });
            }
        }

        // Подсчёт источников оценки
        var allSimActions = simulated.WorkerTimelines.SelectMany(w => w.Actions).ToList();
        var actualUsed = allSimActions.Count(a => a.DurationSource == "actual");
        var routeStatsUsed = allSimActions.Count(a => a.DurationSource == "route_stats");
        var pickerStatsUsed = allSimActions.Count(a => a.DurationSource == "picker_product");
        var defaultUsed = allSimActions.Count(a => a.DurationSource == "default");

        return new BacktestResult
        {
            WaveNumber = data.WaveNumber,
            WaveDate = data.WaveDate,
            WaveStatus = data.Status,
            TotalReplenishmentTasks = data.ReplenishmentTasks.Sum(g => g.Actions.Count),
            TotalDistributionTasks = data.DistributionTasks.Sum(g => g.Actions.Count),
            TotalActions = taskDetails.Count,
            UniqueWorkers = actual.WorkerTimelines.Count,
            ActualStartTime = actual.StartTime,
            ActualEndTime = actual.EndTime,
            ActualWallClockDuration = actual.WallClockDuration,
            ActualActiveDuration = actualDuration,
            OptimizedDuration = optimizedDuration,
            ImprovementPercent = improvementPercent,
            ImprovementTime = improvementTime,
            OptimizerIsOptimal = plan.IsOptimal,
            WorkerBreakdowns = workerBreakdowns,
            TaskDetails = taskDetails,
            ActualDurationsUsed = actualUsed,
            RouteStatsUsed = routeStatsUsed,
            PickerStatsUsed = pickerStatsUsed,
            DefaultEstimatesUsed = defaultUsed,
            WaveMeanDurationSec = simulated.WaveMeanDurationSec
        };
    }

    // ============================================================================
    // Вспомогательные методы
    // ============================================================================

    /// <summary>
    /// Оценить длительность одного действия на основе статистики
    /// </summary>
    private (double durationSec, string source) EstimateActionDuration(
        ActionTiming action,
        string workerCode,
        Dictionary<string, RouteStatistics> routeLookup,
        Dictionary<string, PickerProductStats> pickerLookup,
        double waveMeanDurationSec)
    {
        // 0. Фактическая длительность из данных волны — самый точный источник
        if (action.DurationSec > 0)
        {
            return (action.DurationSec, "actual");
        }

        // 1. Пробуем picker_product_stats (worker + product)
        if (!string.IsNullOrEmpty(workerCode) && !string.IsNullOrEmpty(action.ProductCode))
        {
            var pickerKey = $"{workerCode}:{action.ProductCode}";
            if (pickerLookup.TryGetValue(pickerKey, out var picker) && picker.AvgDurationSec.HasValue)
            {
                return ((double)picker.AvgDurationSec.Value, "picker_product");
            }
        }

        // 2. Пробуем route_stats (from_zone → to_zone), только если обе зоны известны
        if (action.FromZone != "?" && action.ToZone != "?")
        {
            var routeKey = $"{action.FromZone}→{action.ToZone}";
            if (routeLookup.TryGetValue(routeKey, out var route) && route.NormalizedTrips >= 3)
            {
                return ((double)route.AvgDurationSec, "route_stats");
            }
        }

        // 3. Среднее по волне (вместо фиксированных 120с)
        return (waveMeanDurationSec, "default");
    }

    /// <summary>
    /// Оценить расстояние маршрута на основе статистики (для PalletAssignmentOptimizer)
    /// </summary>
    private double EstimateDistance(string fromBin, string toBin, List<RouteStatistics> routeStats)
    {
        var fromZone = ExtractZone(fromBin);
        var toZone = ExtractZone(toBin);

        var route = routeStats.FirstOrDefault(r =>
            r.FromZone == fromZone && r.ToZone == toZone && r.NormalizedTrips >= 3);

        if (route != null)
        {
            // Конвертируем avg_duration_sec в приблизительное расстояние (скорость 2 м/с)
            return (double)route.AvgDurationSec * 2.0 / 4.0; // round-trip, 2 load/unload cycles
        }

        return 50.0; // Default 50 meters
    }

    /// <summary>
    /// Объединить пересекающиеся интервалы и посчитать суммарную длительность.
    /// Промежутки где никто не работал (перерывы, ночь) автоматически исключаются.
    /// </summary>
    private static TimeSpan MergeIntervalsAndSum(List<(DateTime start, DateTime end)> intervals)
    {
        if (!intervals.Any()) return TimeSpan.Zero;

        var merged = new List<(DateTime start, DateTime end)>();
        var current = intervals[0];

        foreach (var interval in intervals.Skip(1))
        {
            if (interval.start <= current.end)
            {
                // Пересекаются — расширяем
                if (interval.end > current.end)
                    current.end = interval.end;
            }
            else
            {
                // Разрыв — это перерыв/ночь, сохраняем текущий и начинаем новый
                merged.Add(current);
                current = interval;
            }
        }
        merged.Add(current);

        return TimeSpan.FromSeconds(merged.Sum(m => (m.end - m.start).TotalSeconds));
    }

    /// <summary>
    /// Извлечь код зоны из кода ячейки (например "01A-01-02-03" → "A")
    /// </summary>
    private static string ExtractZone(string binCode)
    {
        if (string.IsNullOrEmpty(binCode))
            return "?";

        var parts = binCode.Split('-');
        if (parts.Length > 0 && parts[0].Length >= 3 && parts[0].StartsWith("01"))
        {
            return parts[0].Substring(2);
        }

        return parts.Length > 0 ? parts[0] : binCode;
    }
}
