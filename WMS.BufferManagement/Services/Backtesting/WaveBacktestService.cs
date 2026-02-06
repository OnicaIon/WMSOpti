using Microsoft.Extensions.Logging;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.Persistence;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;
using WMS.BufferManagement.Layers.Tactical;

namespace WMS.BufferManagement.Services.Backtesting;

/// <summary>
/// Сервис бэктестирования волн дистрибьюции.
/// Оптимизация по дням с учётом доступности работников и связки repl→dist.
/// READ-ONLY: не модифицирует данные в БД.
/// </summary>
public class WaveBacktestService
{
    private readonly IWms1CClient _wmsClient;
    private readonly IHistoricalRepository _repository;
    private readonly PalletAssignmentOptimizer _optimizer; // сохраняем для DI-совместимости
    private readonly ILogger<WaveBacktestService> _logger;

    // Пауза между палетами у пикера (переход, сканирование)
    private const double PickerTransitionTimeSec = 60.0;
    // Значение по умолчанию, если нет статистики маршрута
    private const double DefaultRouteDurationSec = 120.0;

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

    // ============================================================================
    // Внутренняя модель — действие с метаданными группы
    // ============================================================================

    /// <summary>
    /// Аннотированное действие — action + метаданные задачи (для оптимизации по дням)
    /// </summary>
    private class AnnotatedAction
    {
        public WaveTaskGroup Group { get; init; } = null!;
        public WaveTaskAction Action { get; init; } = null!;
        public string TaskType { get; init; } = string.Empty; // "Replenishment" / "Distribution"
        public DateTime Day { get; init; }
        public double DurationSec { get; init; }
        public string OriginalWorkerCode => Group.AssigneeCode;
        public string OriginalWorkerName => Group.AssigneeName;
        public string TemplateCode => Group.TemplateCode;
        public string TaskGroupRef => Group.TaskRef;
        public string PrevTaskRef => Group.PrevTaskRef;
    }

    // ============================================================================
    // Основной метод бэктеста
    // ============================================================================

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

        // 4. Оптимизировать по дням (двухстадийный LPT/EFF с capacity)
        var (optimizedPlan, simulatedTimeline, dayBreakdowns) =
            OptimizeByDays(waveData, actualTimeline, routeStats, pickerStats);

        _logger.LogInformation("Оптимизация по {Days} дням, makespan: {Duration}",
            dayBreakdowns.Count, simulatedTimeline.TotalDuration);

        // 5. Собрать результат
        var result = BuildResult(waveData, actualTimeline, simulatedTimeline,
            optimizedPlan, dayBreakdowns);

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

    // ============================================================================
    // Фактическая хронология
    // ============================================================================

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
                    Role = firstGroup.TemplateCode == "029" ? "Forklift"
                        : firstGroup.TemplateCode == "031" ? "Picker" : "Unknown",
                    StartTime = workerStart,
                    EndTime = workerEnd,
                    TaskCount = actions.Count,
                    Actions = actionTimings
                };
            })
            .ToList();

        var allCompleted = allActions.Where(a => a.Action.CompletedAt.HasValue).ToList();
        var allStarted = allActions.Where(a => a.Action.StartedAt.HasValue).ToList();

        var startTime = allStarted.Any() ? allStarted.Min(a => a.Action.StartedAt!.Value)
            : allCompleted.Any() ? allCompleted.Min(a => a.Action.CompletedAt!.Value)
            : data.WaveDate;
        var endTime = allCompleted.Any() ? allCompleted.Max(a => a.Action.CompletedAt!.Value)
            : data.WaveDate;

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

    // ============================================================================
    // Оптимизация по дням
    // ============================================================================

    /// <summary>
    /// Построить аннотированные действия из данных волны
    /// </summary>
    private List<AnnotatedAction> BuildAnnotatedActions(WaveTasksResponse data)
    {
        var result = new List<AnnotatedAction>();

        void ProcessGroups(List<WaveTaskGroup> groups, string taskType)
        {
            foreach (var group in groups)
            {
                foreach (var action in group.Actions)
                {
                    double dur = action.DurationSec ?? 0;
                    if (dur <= 0 && action.CompletedAt.HasValue && action.StartedAt.HasValue)
                        dur = (action.CompletedAt.Value - action.StartedAt.Value).TotalSeconds;
                    if (dur < 0) dur = 0;

                    var day = action.StartedAt?.Date
                        ?? action.CompletedAt?.Date
                        ?? data.WaveDate.Date;

                    result.Add(new AnnotatedAction
                    {
                        Group = group,
                        Action = action,
                        TaskType = taskType,
                        Day = day,
                        DurationSec = dur
                    });
                }
            }
        }

        ProcessGroups(data.ReplenishmentTasks, "Replenishment");
        ProcessGroups(data.DistributionTasks, "Distribution");

        return result;
    }

    /// <summary>
    /// Оптимизация по дням: двухстадийный scheduling
    /// 1) Replenishment → форклифты (LPT + capacity)
    /// 2) Distribution → пикеры (EFF + precedence + 1мин пауза)
    /// </summary>
    private (OptimizedPlan plan, SimulatedTimeline simulated, List<DayBreakdown> dayBreakdowns)
        OptimizeByDays(
            WaveTasksResponse waveData,
            ActualTimeline actualTimeline,
            List<RouteStatistics> routeStats,
            List<PickerProductStats> pickerStats)
    {
        var allAnnotated = BuildAnnotatedActions(waveData);

        // Группировка по дням
        var dayGroups = allAnnotated
            .GroupBy(a => a.Day)
            .OrderBy(g => g.Key)
            .ToList();

        // Lookup-таблицы для оценки времени
        var routeLookup = routeStats
            .GroupBy(r => $"{r.FromZone}→{r.ToZone}")
            .ToDictionary(g => g.Key, g => g.First());
        var pickerLookup = pickerStats
            .GroupBy(p => $"{p.PickerId}:{p.ProductSku}")
            .ToDictionary(g => g.Key, g => g.First());

        // Среднее фактическое время действия — для default fallback
        var allDurations = allAnnotated.Where(a => a.DurationSec > 0).Select(a => a.DurationSec).ToList();
        var waveMeanDurationSec = allDurations.Any() ? allDurations.Average() : DefaultRouteDurationSec;

        var mergedPlan = new OptimizedPlan { IsOptimal = true };
        var allSimWorkers = new List<SimulatedWorkerTimeline>();
        var dayBreakdowns = new List<DayBreakdown>();

        foreach (var dayGroup in dayGroups)
        {
            var day = dayGroup.Key;
            var dayActions = dayGroup.ToList();

            // Двухстадийная оптимизация дня
            var (dayAssignments, daySimWorkers, dayMakespan) = OptimizeDay(
                dayActions, waveMeanDurationSec, routeLookup, pickerLookup);

            // Merge назначений в общий план
            foreach (var (workerCode, actions) in dayAssignments)
            {
                if (!mergedPlan.WorkerAssignments.ContainsKey(workerCode))
                    mergedPlan.WorkerAssignments[workerCode] = new List<ActionTiming>();
                mergedPlan.WorkerAssignments[workerCode].AddRange(actions);
            }

            allSimWorkers.AddRange(daySimWorkers);

            // Фактическое активное время за день (merged intervals)
            var dayActualIntervals = dayActions
                .Where(a => a.Action.StartedAt.HasValue && a.Action.CompletedAt.HasValue)
                .Select(a => (start: a.Action.StartedAt!.Value, end: a.Action.CompletedAt!.Value))
                .Where(i => i.end > i.start)
                .OrderBy(i => i.start)
                .ToList();
            var dayActualActive = MergeIntervalsAndSum(dayActualIntervals);

            var dayImprovement = dayActualActive.TotalSeconds > 0
                ? ((dayActualActive - dayMakespan).TotalSeconds / dayActualActive.TotalSeconds) * 100
                : 0;

            var forkliftCount = dayActions.Where(a => a.TaskType == "Replenishment")
                .Select(a => a.OriginalWorkerCode).Distinct().Count();
            var pickerCount = dayActions.Where(a => a.TaskType == "Distribution")
                .Select(a => a.OriginalWorkerCode).Distinct().Count();

            dayBreakdowns.Add(new DayBreakdown
            {
                Date = day,
                Workers = dayActions.Select(a => a.OriginalWorkerCode).Distinct().Count(),
                ForkliftWorkers = forkliftCount,
                PickerWorkers = pickerCount,
                ReplActions = dayActions.Count(a => a.TaskType == "Replenishment"),
                DistActions = dayActions.Count(a => a.TaskType == "Distribution"),
                TotalActions = dayActions.Count,
                ActualActiveDuration = dayActualActive,
                OptimizedMakespan = dayMakespan,
                ImprovementPercent = dayImprovement
            });
        }

        // Агрегация SimulatedWorkerTimeline по работникам (работник может быть в нескольких днях)
        var simTimeline = new SimulatedTimeline
        {
            WaveMeanDurationSec = waveMeanDurationSec
        };

        foreach (var wg in allSimWorkers.GroupBy(w => w.WorkerCode))
        {
            simTimeline.WorkerTimelines.Add(new SimulatedWorkerTimeline
            {
                WorkerCode = wg.Key,
                WorkerName = wg.First().WorkerName,
                Duration = TimeSpan.FromSeconds(wg.Sum(w => w.Duration.TotalSeconds)),
                TaskCount = wg.Sum(w => w.TaskCount),
                Actions = wg.SelectMany(w => w.Actions).ToList()
            });
        }

        // Общее время = сумма per-day makespans (дни последовательны)
        simTimeline.TotalDuration = TimeSpan.FromSeconds(
            dayBreakdowns.Sum(d => d.OptimizedMakespan.TotalSeconds));

        return (mergedPlan, simTimeline, dayBreakdowns);
    }

    /// <summary>
    /// Оптимизировать один день — двухстадийный scheduling:
    /// Стадия 1: Replenishment → форклифты (LPT greedy + capacity)
    /// Стадия 2: Distribution → пикеры (EFF + precedence + 1мин пауза + capacity)
    /// </summary>
    private (
        Dictionary<string, List<ActionTiming>> assignments,
        List<SimulatedWorkerTimeline> simWorkers,
        TimeSpan makespan
    ) OptimizeDay(
        List<AnnotatedAction> dayActions,
        double waveMeanDurationSec,
        Dictionary<string, RouteStatistics> routeLookup,
        Dictionary<string, PickerProductStats> pickerLookup)
    {
        var assignments = new Dictionary<string, List<ActionTiming>>();
        var simWorkers = new List<SimulatedWorkerTimeline>();

        // === Группируем действия по задачам (task groups) ===
        var replTaskGroups = dayActions
            .Where(a => a.TaskType == "Replenishment")
            .GroupBy(a => a.TaskGroupRef)
            .Select(g => new
            {
                TaskGroupRef = g.Key,
                Actions = g.ToList(),
                TotalDurationSec = g.Sum(a => a.DurationSec),
                OriginalWorker = g.First().OriginalWorkerCode,
                OriginalWorkerName = g.First().OriginalWorkerName
            })
            .OrderByDescending(g => g.TotalDurationSec) // LPT: длинные задачи первыми
            .ToList();

        var distTaskGroups = dayActions
            .Where(a => a.TaskType == "Distribution")
            .GroupBy(a => a.TaskGroupRef)
            .Select(g => new
            {
                TaskGroupRef = g.Key,
                PrevTaskRef = g.First().PrevTaskRef,
                Actions = g.ToList(),
                TotalDurationSec = g.Sum(a => a.DurationSec),
                OriginalWorker = g.First().OriginalWorkerCode,
                OriginalWorkerName = g.First().OriginalWorkerName
            })
            .ToList();

        // === Capacity работников: сумма фактических длительностей за день ===
        var workerCapacities = dayActions
            .GroupBy(a => a.OriginalWorkerCode)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.DurationSec));

        // Форклифт-работники (только те, у кого есть repl-действия в этот день)
        var forkliftWorkerCodes = replTaskGroups
            .Select(g => g.OriginalWorker)
            .Distinct()
            .ToList();

        // Пикер-работники (только те, у кого есть dist-действия в этот день)
        var pickerWorkerCodes = distTaskGroups
            .Select(g => g.OriginalWorker)
            .Distinct()
            .ToList();

        // ================================================================
        // СТАДИЯ 1: Replenishment → форклифты (LPT + capacity)
        // ================================================================
        var forkliftLoad = forkliftWorkerCodes.ToDictionary(c => c, _ => 0.0);
        var forkliftRemaining = forkliftWorkerCodes.ToDictionary(
            c => c, c => workerCapacities.GetValueOrDefault(c, 0.0));
        var replFinishTimes = new Dictionary<string, double>(); // taskGroupRef → finish time

        foreach (var rg in replTaskGroups)
        {
            // Назначаем форклифту с наибольшим оставшимся capacity
            var bestWorker = forkliftRemaining
                .OrderByDescending(kv => kv.Value)
                .First().Key;

            forkliftLoad[bestWorker] += rg.TotalDurationSec;
            forkliftRemaining[bestWorker] -= rg.TotalDurationSec;

            // Время окончания этого repl-задания = cumulative load форклифта
            replFinishTimes[rg.TaskGroupRef] = forkliftLoad[bestWorker];

            // Формируем ActionTiming для назначения
            if (!assignments.ContainsKey(bestWorker))
                assignments[bestWorker] = new List<ActionTiming>();

            foreach (var aa in rg.Actions)
            {
                assignments[bestWorker].Add(CreateActionTiming(aa, bestWorker));
            }
        }

        // SimulatedWorkerTimeline для форклифтов
        foreach (var workerCode in forkliftWorkerCodes)
        {
            var workerActions = assignments.GetValueOrDefault(workerCode);
            if (workerActions == null || !workerActions.Any()) continue;

            var swt = new SimulatedWorkerTimeline
            {
                WorkerCode = workerCode,
                WorkerName = replTaskGroups.FirstOrDefault(g => g.OriginalWorker == workerCode)?.OriginalWorkerName ?? "",
                Duration = TimeSpan.FromSeconds(forkliftLoad[workerCode]),
                TaskCount = workerActions.Count
            };

            foreach (var at in workerActions)
            {
                var (estSec, source) = EstimateActionDuration(
                    at, workerCode, routeLookup, pickerLookup, waveMeanDurationSec);
                swt.Actions.Add(new SimulatedAction
                {
                    FromBin = at.FromBin,
                    ToBin = at.ToBin,
                    FromZone = at.FromZone,
                    ToZone = at.ToZone,
                    ProductCode = at.ProductCode,
                    WeightKg = at.WeightKg,
                    EstimatedDurationSec = estSec,
                    DurationSource = source
                });
            }
            simWorkers.Add(swt);
        }

        // ================================================================
        // СТАДИЯ 2: Distribution → пикеры (EFF + precedence + 1мин пауза)
        // ================================================================
        var pickerFinishTime = pickerWorkerCodes.ToDictionary(c => c, _ => 0.0);
        var pickerRemaining = pickerWorkerCodes.ToDictionary(
            c => c, c => workerCapacities.GetValueOrDefault(c, 0.0));

        // Сортируем dist-задачи по времени доступности (когда repl завершился)
        var sortedDistGroups = distTaskGroups
            .OrderBy(dg =>
            {
                if (!string.IsNullOrEmpty(dg.PrevTaskRef) && replFinishTimes.TryGetValue(dg.PrevTaskRef, out var ft))
                    return ft;
                return 0.0; // нет связанного repl → доступна сразу
            })
            .ToList();

        foreach (var dg in sortedDistGroups)
        {
            if (!pickerWorkerCodes.Any()) break;

            // Когда distribution становится доступной?
            var replFinish = 0.0;
            if (!string.IsNullOrEmpty(dg.PrevTaskRef) && replFinishTimes.TryGetValue(dg.PrevTaskRef, out var ft))
                replFinish = ft;

            // Находим пикера с наименьшим временем старта (EFF):
            // start = max(pickerFinish + 1мин пауза, replFinish)
            var bestPicker = pickerWorkerCodes
                .OrderBy(code =>
                {
                    var pFinish = pickerFinishTime[code];
                    var pause = pFinish > 0 ? PickerTransitionTimeSec : 0;
                    return Math.Max(pFinish + pause, replFinish);
                })
                .First();

            var pickerPause = pickerFinishTime[bestPicker] > 0 ? PickerTransitionTimeSec : 0;
            var startTime = Math.Max(pickerFinishTime[bestPicker] + pickerPause, replFinish);
            pickerFinishTime[bestPicker] = startTime + dg.TotalDurationSec;
            pickerRemaining[bestPicker] -= dg.TotalDurationSec;

            if (!assignments.ContainsKey(bestPicker))
                assignments[bestPicker] = new List<ActionTiming>();

            foreach (var aa in dg.Actions)
            {
                assignments[bestPicker].Add(CreateActionTiming(aa, bestPicker));
            }
        }

        // SimulatedWorkerTimeline для пикеров
        foreach (var workerCode in pickerWorkerCodes)
        {
            var workerActions = assignments.GetValueOrDefault(workerCode);
            if (workerActions == null || !workerActions.Any()) continue;

            var swt = new SimulatedWorkerTimeline
            {
                WorkerCode = workerCode,
                WorkerName = distTaskGroups.FirstOrDefault(g => g.OriginalWorker == workerCode)?.OriginalWorkerName ?? "",
                Duration = TimeSpan.FromSeconds(pickerFinishTime[workerCode]),
                TaskCount = workerActions.Count
            };

            foreach (var at in workerActions)
            {
                var (estSec, source) = EstimateActionDuration(
                    at, workerCode, routeLookup, pickerLookup, waveMeanDurationSec);
                swt.Actions.Add(new SimulatedAction
                {
                    FromBin = at.FromBin,
                    ToBin = at.ToBin,
                    FromZone = at.FromZone,
                    ToZone = at.ToZone,
                    ProductCode = at.ProductCode,
                    WeightKg = at.WeightKg,
                    EstimatedDurationSec = estSec,
                    DurationSource = source
                });
            }
            simWorkers.Add(swt);
        }

        // Makespan = max из всех worker finish times
        var maxForklift = forkliftLoad.Any() ? forkliftLoad.Values.Max() : 0;
        var maxPicker = pickerFinishTime.Any() ? pickerFinishTime.Values.Max() : 0;
        var makespan = TimeSpan.FromSeconds(Math.Max(maxForklift, maxPicker));

        return (assignments, simWorkers, makespan);
    }

    /// <summary>
    /// Создать ActionTiming из AnnotatedAction
    /// </summary>
    private static ActionTiming CreateActionTiming(AnnotatedAction aa, string assignedWorkerCode)
    {
        return new ActionTiming
        {
            FromBin = aa.Action.StorageBin,
            ToBin = aa.Action.AllocationBin,
            FromZone = ExtractZone(aa.Action.StorageBin),
            ToZone = ExtractZone(aa.Action.AllocationBin),
            ProductCode = aa.Action.ProductCode,
            ProductName = aa.Action.ProductName,
            WeightKg = aa.Action.WeightKg,
            Qty = aa.Action.QtyPlan > 0 ? aa.Action.QtyPlan : 1,
            DurationSec = aa.DurationSec,
            WorkerCode = assignedWorkerCode,
            TaskType = aa.TaskType,
            TaskGroupRef = aa.TaskGroupRef,
            PrevTaskRef = aa.PrevTaskRef
        };
    }

    // ============================================================================
    // Сборка результата
    // ============================================================================

    /// <summary>
    /// Собрать итоговый результат бэктеста
    /// </summary>
    public BacktestResult BuildResult(
        WaveTasksResponse data,
        ActualTimeline actual,
        SimulatedTimeline simulated,
        OptimizedPlan plan,
        List<DayBreakdown> dayBreakdowns)
    {
        // Итоговое сравнение: сумма per-day active vs сумма per-day makespans
        var actualDuration = TimeSpan.FromSeconds(
            dayBreakdowns.Sum(d => d.ActualActiveDuration.TotalSeconds));
        var optimizedDuration = simulated.TotalDuration;
        var improvementTime = actualDuration - optimizedDuration;
        var improvementPercent = actualDuration.TotalSeconds > 0
            ? (improvementTime.TotalSeconds / actualDuration.TotalSeconds) * 100
            : 0;

        // Разбивка по работникам
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

        // Детали заданий
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
            DayBreakdowns = dayBreakdowns,
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

        // 2. Пробуем route_stats (from_zone → to_zone)
        if (action.FromZone != "?" && action.ToZone != "?")
        {
            var routeKey = $"{action.FromZone}→{action.ToZone}";
            if (routeLookup.TryGetValue(routeKey, out var route) && route.NormalizedTrips >= 3)
            {
                return ((double)route.AvgDurationSec, "route_stats");
            }
        }

        // 3. Среднее по волне
        return (waveMeanDurationSec, "default");
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
                if (interval.end > current.end)
                    current.end = interval.end;
            }
            else
            {
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
