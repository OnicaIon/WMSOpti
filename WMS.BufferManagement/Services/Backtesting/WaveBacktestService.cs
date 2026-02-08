using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.Persistence;
using WMS.BufferManagement.Layers.Historical.Persistence.Models;
using WMS.BufferManagement.Layers.Tactical;

namespace WMS.BufferManagement.Services.Backtesting;

/// <summary>
/// Сервис бэктестирования волн дистрибьюции.
/// Кросс-дневная оптимизация: все палеты = пул, работники ограничены днём/сменой, буфер ≤ capacity.
/// READ-ONLY: не модифицирует данные в БД.
/// </summary>
public class WaveBacktestService
{
    private readonly IWms1CClient _wmsClient;
    private readonly IHistoricalRepository _repository;
    private readonly PalletAssignmentOptimizer _optimizer;
    private readonly ILogger<WaveBacktestService> _logger;
    private readonly int _bufferCapacity;

    private const double DefaultRouteDurationSec = 120.0;

    public WaveBacktestService(
        IWms1CClient wmsClient,
        IHistoricalRepository repository,
        PalletAssignmentOptimizer optimizer,
        ILogger<WaveBacktestService> logger,
        IOptions<BufferConfig> bufferConfig)
    {
        _wmsClient = wmsClient;
        _repository = repository;
        _optimizer = optimizer;
        _logger = logger;
        _bufferCapacity = bufferConfig.Value.Capacity;
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

    /// <summary>
    /// Информация о task group для кросс-дневной оптимизации
    /// </summary>
    private class TaskGroupInfo
    {
        public string TaskGroupRef { get; set; } = string.Empty;
        public string PrevTaskRef { get; set; } = string.Empty;
        public string TaskType { get; set; } = string.Empty;
        public double DurationSec { get; set; }
        public double WeightKg { get; set; }
        public double Priority { get; set; }
        public string OriginalWorker { get; set; } = string.Empty;
        public string OriginalWorkerName { get; set; } = string.Empty;
        public List<AnnotatedAction> Actions { get; set; } = new();
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
        var transitionStats = await _repository.GetWorkerTransitionStatsAsync(cancellationToken: ct);
        var productCategoryMap = await _repository.GetProductCategoryMapAsync(ct);

        _logger.LogInformation("Статистика: {Routes} маршрутов, {Pickers} пикер-товар, {Trans} переходов, {Cat} товар→категория",
            routeStats.Count, pickerStats.Count, transitionStats.Count, productCategoryMap.Count);

        // Среднее время перехода по ролям (из исторических данных)
        var pickerTransitions = transitionStats.Where(t => t.WorkerRole == "Picker").ToList();
        var forkliftTransitions = transitionStats.Where(t => t.WorkerRole == "Forklift").ToList();
        var pickerTransitionSec = pickerTransitions.Any()
            ? pickerTransitions.Average(t => t.MedianTransitionSec) : 0;
        var forkliftTransitionSec = forkliftTransitions.Any()
            ? forkliftTransitions.Average(t => t.MedianTransitionSec) : 0;

        _logger.LogInformation(
            "Время перехода: пикеры={PickerSec:F1}с ({PickerCount} работн.), форклифты={ForkSec:F1}с ({ForkCount} работн.)",
            pickerTransitionSec, pickerTransitions.Count, forkliftTransitionSec, forkliftTransitions.Count);

        // 4. Создать контекст для сбора решений
        var decisionCtx = new SimulationDecisionContext();

        // Построить фактические events для Ганта из ActualTimeline
        foreach (var wt in actualTimeline.WorkerTimelines)
        {
            foreach (var action in wt.Actions)
            {
                // Для факта — используем реальные timestamps действий
                // Группируем по TaskGroupRef для палетного уровня
                decisionCtx.ScheduleEvents.Add(new ScheduleEventRecord
                {
                    TimelineType = "fact",
                    WorkerCode = wt.WorkerCode,
                    WorkerName = wt.WorkerName,
                    WorkerRole = action.TaskType == "Distribution" ? "Picker" : "Forklift",
                    TaskRef = action.TaskGroupRef,
                    TaskType = action.TaskType,
                    DayDate = action.DurationSec > 0 && wt.StartTime != default
                        ? wt.StartTime.Date : DateTime.UtcNow.Date,
                    StartTime = wt.StartTime, // Будет уточнено ниже
                    EndTime = wt.EndTime,
                    DurationSec = action.DurationSec,
                    FromBin = action.FromBin,
                    ToBin = action.ToBin,
                    FromZone = action.FromZone,
                    ToZone = action.ToZone,
                    ProductCode = action.ProductCode,
                    ProductName = action.ProductName,
                    WeightKg = action.WeightKg,
                    Qty = action.Qty,
                    DurationSource = "actual"
                });
            }
        }

        // Сгруппировать фактические events по палетам (TaskGroupRef + Worker)
        // чтобы получить 1 строку на палету (а не на action)
        var factEventsByPallet = decisionCtx.ScheduleEvents
            .Where(e => e.TimelineType == "fact" && !string.IsNullOrEmpty(e.TaskRef))
            .GroupBy(e => new { e.TaskRef, e.WorkerCode })
            .Select(g =>
            {
                var first = g.First();
                return new ScheduleEventRecord
                {
                    TimelineType = "fact",
                    WorkerCode = first.WorkerCode,
                    WorkerName = first.WorkerName,
                    WorkerRole = first.WorkerRole,
                    TaskRef = first.TaskRef,
                    TaskType = first.TaskType,
                    DayDate = first.DayDate,
                    StartTime = first.StartTime,
                    EndTime = first.EndTime,
                    DurationSec = g.Sum(e => e.DurationSec),
                    FromBin = first.FromBin,
                    ToBin = first.ToBin,
                    FromZone = first.FromZone,
                    ToZone = first.ToZone,
                    ProductCode = first.ProductCode,
                    ProductName = first.ProductName,
                    WeightKg = g.Sum(e => e.WeightKg),
                    Qty = g.Sum(e => e.Qty),
                    DurationSource = "actual"
                };
            }).ToList();

        // Заменить per-action events на per-pallet events
        decisionCtx.ScheduleEvents.RemoveAll(e => e.TimelineType == "fact");
        decisionCtx.ScheduleEvents.AddRange(factEventsByPallet);

        // Уточнить timestamps из оригинальных данных волны
        var allWaveGroups = waveData.ReplenishmentTasks.Concat(waveData.DistributionTasks)
            .GroupBy(g => g.TaskRef).ToDictionary(g => g.Key, g => g.First());
        foreach (var evt in decisionCtx.ScheduleEvents.Where(e => e.TimelineType == "fact"))
        {
            if (evt.TaskRef != null && allWaveGroups.TryGetValue(evt.TaskRef, out var grp))
            {
                var starts = grp.Actions.Where(a => a.StartedAt.HasValue).Select(a => a.StartedAt!.Value).ToList();
                var ends = grp.Actions.Where(a => a.CompletedAt.HasValue).Select(a => a.CompletedAt!.Value).ToList();
                if (starts.Any()) evt.StartTime = starts.Min();
                if (ends.Any()) evt.EndTime = ends.Max();
                if (starts.Any()) evt.DayDate = starts.Min().Date;
            }
        }

        // 5. Кросс-дневная оптимизация с буфером (с контекстом решений)
        var (optimizedPlan, simulatedTimeline, dayBreakdowns, priorityByRef) =
            OptimizeCrossDay(waveData, actualTimeline, routeStats, pickerStats,
                pickerTransitionSec, forkliftTransitionSec, decisionCtx, productCategoryMap);

        var optDays = dayBreakdowns.Count(d => d.OptimizedReplGroups + d.OptimizedDistGroups > 0);
        var origDays = dayBreakdowns.Count(d => d.OriginalReplGroups + d.OriginalDistGroups > 0);
        _logger.LogInformation("Кросс-дневная оптимизация: {OptDays}/{OrigDays} дней, буфер {Cap}",
            optDays, origDays, _bufferCapacity);
        _logger.LogInformation("Собрано: {Events} events, {Decisions} решений",
            decisionCtx.ScheduleEvents.Count, decisionCtx.Decisions.Count);

        // 6. Собрать результат
        var result = BuildResult(waveData, actualTimeline, simulatedTimeline,
            optimizedPlan, dayBreakdowns, priorityByRef);

        // Заполнить поля переходов
        result.PickerTransitionSec = pickerTransitionSec;
        result.ForkliftTransitionSec = forkliftTransitionSec;
        result.PickerTransitionCount = pickerTransitions.Sum(t => t.TransitionCount);
        result.ForkliftTransitionCount = forkliftTransitions.Sum(t => t.TransitionCount);

        // Привязать контекст решений к результату
        result.DecisionContext = decisionCtx;

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
        // Определяем тип задачи по принадлежности к списку (не по PrevTaskRef — он может не приходить из 1С)
        var distTaskRefs = new HashSet<string>(
            data.DistributionTasks.Select(g => g.TaskRef).Where(r => !string.IsNullOrEmpty(r)));

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
                var actionsWithGroup = group
                    .SelectMany(g => g.Actions.Select(a => new { Group = g, Action = a }))
                    .ToList();

                var actionTimings = actionsWithGroup.Select(x =>
                {
                    var a = x.Action;
                    double dur = a.DurationSec ?? 0;
                    if (dur <= 0 && a.CompletedAt.HasValue && a.StartedAt.HasValue)
                        dur = (a.CompletedAt.Value - a.StartedAt.Value).TotalSeconds;
                    if (dur < 0) dur = 0;

                    var taskType = distTaskRefs.Contains(x.Group.TaskRef)
                        ? "Distribution" : "Replenishment";

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
                        WorkerCode = firstGroup.AssigneeCode,
                        TaskGroupRef = x.Group.TaskRef,
                        TaskType = taskType
                    };
                }).ToList();
                var actions = actionsWithGroup.Select(x => x.Action).ToList();

                var completed = actions.Where(a => a.CompletedAt.HasValue).ToList();
                var started = actions.Where(a => a.StartedAt.HasValue).ToList();
                var workerStart = started.Any() ? started.Min(a => a.StartedAt!.Value)
                    : completed.Any() ? completed.Min(a => a.CompletedAt!.Value)
                    : data.WaveDate;
                var workerEnd = completed.Any() ? completed.Max(a => a.CompletedAt!.Value)
                    : data.WaveDate;

                // Роль по TaskType (большинство задач), а не по TemplateCode
                var replCount = actionTimings.Count(a => a.TaskType == "Replenishment");
                var distCount = actionTimings.Count(a => a.TaskType == "Distribution");
                var role = replCount >= distCount ? "Forklift" : "Picker";

                return new WorkerTimeline
                {
                    WorkerCode = firstGroup.AssigneeCode,
                    WorkerName = firstGroup.AssigneeName,
                    Role = role,
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
    /// Кросс-дневная оптимизация: все палеты = пул, работники ограничены днём,
    /// буфер ≤ capacity. Каждый день заполняется из пула до исчерпания capacity.
    /// </summary>
    private (OptimizedPlan plan, SimulatedTimeline simulated, List<DayBreakdown> dayBreakdowns,
             Dictionary<string, double> priorityByRef)
        OptimizeCrossDay(
            WaveTasksResponse waveData,
            ActualTimeline actualTimeline,
            List<RouteStatistics> routeStats,
            List<PickerProductStats> pickerStats,
            double pickerTransitionSec,
            double forkliftTransitionSec,
            SimulationDecisionContext? decisionCtx = null,
            Dictionary<string, string>? productCategoryMap = null)
    {
        var allAnnotated = BuildAnnotatedActions(waveData);

        var routeLookup = routeStats
            .GroupBy(r => $"{r.FromZone}→{r.ToZone}")
            .ToDictionary(g => g.Key, g => g.First());
        var pickerLookup = pickerStats
            .GroupBy(p => $"{p.PickerId}:{p.ProductSku}")
            .ToDictionary(g => g.Key, g => g.First());

        // === Каскад категорий: worker×category и category avg ===
        var catMap = productCategoryMap ?? new Dictionary<string, string>();
        // Уровень 2: среднее по (worker, category), мин. 5 товаров
        var workerCategoryAvg = pickerStats
            .Where(p => p.AvgDurationSec.HasValue && catMap.ContainsKey(p.ProductSku))
            .GroupBy(p => $"{p.PickerId}:{catMap[p.ProductSku]}")
            .Where(g => g.Count() >= 5)
            .ToDictionary(g => g.Key, g => (double)g.Average(p => p.AvgDurationSec!.Value));
        // Уровень 3: среднее по категории (все работники), мин. 10 записей
        var categoryAvg = pickerStats
            .Where(p => p.AvgDurationSec.HasValue && catMap.ContainsKey(p.ProductSku))
            .GroupBy(p => catMap[p.ProductSku])
            .Where(g => g.Count() >= 10)
            .ToDictionary(g => g.Key, g => (double)g.Average(p => p.AvgDurationSec!.Value));

        var allDurations = allAnnotated.Where(a => a.DurationSec > 0).Select(a => a.DurationSec).ToList();
        var waveMeanDurationSec = allDurations.Any() ? allDurations.Average() : DefaultRouteDurationSec;

        // === Построить task groups с масштабированными длительностями ===
        // Только палеты с назначенным работником (иначе факт vs опт несопоставимы)
        var replGroupsRaw = allAnnotated
            .Where(a => a.TaskType == "Replenishment" && !string.IsNullOrWhiteSpace(a.OriginalWorkerCode))
            .GroupBy(a => a.TaskGroupRef)
            .Select(g => new
            {
                TaskGroupRef = g.Key,
                Actions = g.ToList(),
                RawDurationSec = ComputeGroupSpanSec(g.ToList()),
                OriginalWorker = g.First().OriginalWorkerCode,
                OriginalWorkerName = g.First().OriginalWorkerName,
                Day = g.First().Day,
                WeightKg = (double)g.Sum(a => a.Action.WeightKg)
            }).ToList();

        var distGroupsRaw = allAnnotated
            .Where(a => a.TaskType == "Distribution" && !string.IsNullOrWhiteSpace(a.OriginalWorkerCode))
            .GroupBy(a => a.TaskGroupRef)
            .Select(g => new
            {
                TaskGroupRef = g.Key,
                PrevTaskRef = g.First().PrevTaskRef,
                Actions = g.ToList(),
                RawDurationSec = ComputeGroupSpanSec(g.ToList()),
                OriginalWorker = g.First().OriginalWorkerCode,
                OriginalWorkerName = g.First().OriginalWorkerName,
                Day = g.First().Day,
                WeightKg = (double)g.Sum(a => a.Action.WeightKg)
            }).ToList();

        // Масштабирование: per-worker-per-day sum(scaled) = merged interval
        var scaledDurations = new Dictionary<string, double>();

        foreach (var wg in replGroupsRaw.GroupBy(g => (g.OriginalWorker, g.Day)))
        {
            var workerActions = allAnnotated
                .Where(a => a.OriginalWorkerCode == wg.Key.OriginalWorker
                    && a.Day == wg.Key.Day && a.TaskType == "Replenishment").ToList();
            var capacity = ComputeWorkerDayCapacitySec(workerActions);
            var rawTotal = wg.Sum(g => g.RawDurationSec);
            var scale = rawTotal > 0 ? capacity / rawTotal : 1.0;
            foreach (var g in wg)
                scaledDurations[g.TaskGroupRef] = g.RawDurationSec * scale;
        }

        foreach (var wg in distGroupsRaw.GroupBy(g => (g.OriginalWorker, g.Day)))
        {
            var workerActions = allAnnotated
                .Where(a => a.OriginalWorkerCode == wg.Key.OriginalWorker
                    && a.Day == wg.Key.Day && a.TaskType == "Distribution").ToList();
            var capacity = ComputeWorkerDayCapacitySec(workerActions);
            var rawTotal = wg.Sum(g => g.RawDurationSec);
            var scale = rawTotal > 0 ? capacity / rawTotal : 1.0;
            foreach (var g in wg)
                scaledDurations[g.TaskGroupRef] = g.RawDurationSec * scale;
        }

        // Построить TaskGroupInfo с приоритетами
        var replGroups = replGroupsRaw.Select(g =>
        {
            var tgi = new TaskGroupInfo
            {
                TaskGroupRef = g.TaskGroupRef,
                TaskType = "Replenishment",
                DurationSec = scaledDurations.GetValueOrDefault(g.TaskGroupRef, g.RawDurationSec),
                WeightKg = g.WeightKg,
                OriginalWorker = g.OriginalWorker,
                OriginalWorkerName = g.OriginalWorkerName,
                Actions = g.Actions
            };
            tgi.Priority = CalculatePriority(tgi, routeLookup);
            return tgi;
        }).OrderByDescending(g => g.Priority).ToList();

        var distGroups = distGroupsRaw.Select(g =>
        {
            var tgi = new TaskGroupInfo
            {
                TaskGroupRef = g.TaskGroupRef,
                PrevTaskRef = g.PrevTaskRef,
                TaskType = "Distribution",
                DurationSec = scaledDurations.GetValueOrDefault(g.TaskGroupRef, g.RawDurationSec),
                WeightKg = g.WeightKg,
                OriginalWorker = g.OriginalWorker,
                OriginalWorkerName = g.OriginalWorkerName,
                Actions = g.Actions
            };
            tgi.Priority = CalculatePriority(tgi, routeLookup);
            return tgi;
        }).ToList();

        // Словарь приоритетов для TaskDetail
        var priorityByRef = replGroups.Concat(distGroups)
            .ToDictionary(g => g.TaskGroupRef, g => g.Priority);

        // === Worker-day capacity (по TaskType, не TemplateCode) ===
        var forkliftDayData = allAnnotated
            .Where(a => a.TaskType == "Replenishment" && !string.IsNullOrWhiteSpace(a.OriginalWorkerCode))
            .GroupBy(a => (a.OriginalWorkerCode, a.Day))
            .Select(g => new
            {
                WorkerCode = g.Key.OriginalWorkerCode,
                WorkerName = g.First().OriginalWorkerName,
                Day = g.Key.Day,
                CapacitySec = ComputeWorkerDayCapacitySec(g.ToList())
            }).ToList();

        var pickerDayData = allAnnotated
            .Where(a => a.TaskType == "Distribution" && !string.IsNullOrWhiteSpace(a.OriginalWorkerCode))
            .GroupBy(a => (a.OriginalWorkerCode, a.Day))
            .Select(g => new
            {
                WorkerCode = g.Key.OriginalWorkerCode,
                WorkerName = g.First().OriginalWorkerName,
                Day = g.Key.Day,
                CapacitySec = ComputeWorkerDayCapacitySec(g.ToList())
            }).ToList();

        var allDays = forkliftDayData.Select(w => w.Day)
            .Concat(pickerDayData.Select(w => w.Day))
            .Distinct().OrderBy(d => d).ToList();

        // Фактические подсчёты по дням (для сравнения) — только палеты с назначенным работником
        var originalDayReplGroups = allAnnotated
            .Where(a => a.TaskType == "Replenishment" && !string.IsNullOrWhiteSpace(a.OriginalWorkerCode))
            .GroupBy(a => a.Day)
            .ToDictionary(g => g.Key, g => g.Select(a => a.TaskGroupRef).Distinct().Count());
        var originalDayDistGroups = allAnnotated
            .Where(a => a.TaskType == "Distribution" && !string.IsNullOrWhiteSpace(a.OriginalWorkerCode))
            .GroupBy(a => a.Day)
            .ToDictionary(g => g.Key, g => g.Select(a => a.TaskGroupRef).Distinct().Count());

        var workerNames = allAnnotated
            .GroupBy(a => a.OriginalWorkerCode)
            .ToDictionary(g => g.Key, g =>
            {
                // Берём первое непустое имя (может отличаться между группами)
                var name = g.Select(a => a.OriginalWorkerName)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                return name ?? g.Key; // fallback на код работника
            });

        // === Коэффициент скорости работников (ECT + эффективность) ===
        // speedRatio < 1 = работник быстрее среднего, > 1 = медленнее
        var forkliftAvgDur = replGroups.Any() ? replGroups.Average(g => g.DurationSec) : DefaultRouteDurationSec;
        var forkliftSpeedRatio = replGroups.GroupBy(g => g.OriginalWorker)
            .ToDictionary(
                wg => wg.Key,
                wg => forkliftAvgDur > 0 ? wg.Average(g => g.DurationSec) / forkliftAvgDur : 1.0);

        var pickerAvgDur = distGroups.Any() ? distGroups.Average(g => g.DurationSec) : DefaultRouteDurationSec;
        var pickerSpeedRatio = distGroups.GroupBy(g => g.OriginalWorker)
            .ToDictionary(
                wg => wg.Key,
                wg => pickerAvgDur > 0 ? wg.Average(g => g.DurationSec) / pickerAvgDur : 1.0);

        // === Кросс-дневная симуляция ===
        var replPool = new List<TaskGroupInfo>(replGroups); // отсортированы по Priority ↓
        var distPool = new List<TaskGroupInfo>(distGroups);
        var completedRepl = new HashSet<string>();
        int bufferLevel = 0;

        var mergedPlan = new OptimizedPlan { IsOptimal = true };
        var allSimWorkers = new List<SimulatedWorkerTimeline>();
        var dayBreakdowns = new List<DayBreakdown>();

        foreach (var day in allDays)
        {
            var bufferStart = bufferLevel;

            var dayForklifts = forkliftDayData
                .Where(w => w.Day == day)
                .ToDictionary(w => w.WorkerCode, w => w.CapacitySec);
            var dayPickers = pickerDayData
                .Where(w => w.Day == day)
                .ToDictionary(w => w.WorkerCode, w => w.CapacitySec);

            int replDone = 0, distDone = 0;
            var dayAssignments = new Dictionary<string, List<ActionTiming>>();
            var dayMakespan = TimeSpan.Zero;

            if ((replPool.Any() || distPool.Any()) && (dayForklifts.Any() || dayPickers.Any()))
            {
                (replDone, distDone, dayAssignments, dayMakespan) = SimulateDay(
                    replPool, distPool, completedRepl,
                    dayForklifts, dayPickers,
                    ref bufferLevel, _bufferCapacity,
                    forkliftTransitionSec, pickerTransitionSec,
                    day, decisionCtx, workerNames,
                    forkliftSpeedRatio, pickerSpeedRatio,
                    pickerLookup, productCategoryMap ?? new Dictionary<string, string>(),
                    workerCategoryAvg, categoryAvg, waveMeanDurationSec);

                // Merge assignments
                foreach (var (wc, actions) in dayAssignments)
                {
                    if (!mergedPlan.WorkerAssignments.ContainsKey(wc))
                        mergedPlan.WorkerAssignments[wc] = new List<ActionTiming>();
                    mergedPlan.WorkerAssignments[wc].AddRange(actions);
                }

                // SimulatedWorkerTimeline для этого дня
                foreach (var (wc, actions) in dayAssignments)
                {
                    if (!actions.Any()) continue;
                    var swt = new SimulatedWorkerTimeline
                    {
                        WorkerCode = wc,
                        WorkerName = workerNames.GetValueOrDefault(wc, ""),
                        Duration = TimeSpan.FromSeconds(actions.Sum(a => a.DurationSec)),
                        TaskCount = actions.Count
                    };
                    foreach (var at in actions)
                    {
                        var (estSec, source) = EstimateActionDuration(
                            at, wc, routeLookup, pickerLookup, waveMeanDurationSec);
                        swt.Actions.Add(new SimulatedAction
                        {
                            FromBin = at.FromBin, ToBin = at.ToBin,
                            FromZone = at.FromZone, ToZone = at.ToZone,
                            ProductCode = at.ProductCode, WeightKg = at.WeightKg,
                            EstimatedDurationSec = estSec, DurationSource = source
                        });
                    }
                    allSimWorkers.Add(swt);
                }
            }

            // Фактические метрики дня
            var dayAnnotated = allAnnotated.Where(a => a.Day == day).ToList();
            var dayActualIntervals = dayAnnotated
                .Where(a => a.Action.StartedAt.HasValue && a.Action.CompletedAt.HasValue)
                .Select(a => (start: a.Action.StartedAt!.Value, end: a.Action.CompletedAt!.Value))
                .Where(i => i.end > i.start)
                .OrderBy(i => i.start)
                .ToList();
            var dayActualActive = MergeIntervalsAndSum(dayActualIntervals);

            // Фактический makespan: макс. рабочий по сумме длительностей действий (чистое рабочее время)
            var dayFactMakespanSec = dayAnnotated
                .Where(a => a.Action.StartedAt.HasValue && a.Action.CompletedAt.HasValue)
                .GroupBy(a => a.OriginalWorkerCode)
                .Select(g => g.Sum(a => (a.Action.CompletedAt!.Value - a.Action.StartedAt!.Value).TotalSeconds))
                .DefaultIfEmpty(0)
                .Max();
            var dayFactMakespan = TimeSpan.FromSeconds(dayFactMakespanSec);

            var dayImprovement = dayFactMakespanSec > 0
                ? ((dayFactMakespanSec - dayMakespan.TotalSeconds) / dayFactMakespanSec) * 100
                : 0;

            var origRepl = originalDayReplGroups.GetValueOrDefault(day, 0);
            var origDist = originalDayDistGroups.GetValueOrDefault(day, 0);

            dayBreakdowns.Add(new DayBreakdown
            {
                Date = day,
                Workers = dayAnnotated.Select(a => a.OriginalWorkerCode).Distinct().Count(),
                ForkliftWorkers = dayAnnotated.Where(a => a.TaskType == "Replenishment")
                    .Select(a => a.OriginalWorkerCode).Distinct().Count(),
                PickerWorkers = dayAnnotated.Where(a => a.TaskType == "Distribution")
                    .Select(a => a.OriginalWorkerCode).Distinct().Count(),
                ReplActions = dayAnnotated.Count(a => a.TaskType == "Replenishment"),
                DistActions = dayAnnotated.Count(a => a.TaskType == "Distribution"),
                TotalActions = dayAnnotated.Count,
                ActualActiveDuration = dayActualActive,
                ActualMakespan = dayFactMakespan,
                OptimizedMakespan = dayMakespan,
                ImprovementPercent = dayImprovement,
                OriginalReplGroups = origRepl,
                OriginalDistGroups = origDist,
                OptimizedReplGroups = replDone,
                OptimizedDistGroups = distDone,
                AdditionalPallets = (replDone + distDone) - (origRepl + origDist),
                BufferLevelStart = bufferStart,
                BufferLevelEnd = bufferLevel
            });
        }

        // Если задачи остались — добавляем виртуальные дни с capacity самого продуктивного дня
        if (replPool.Any() || distPool.Any())
        {
            _logger.LogInformation("Остались неназначенные задачи ({Repl} repl, {Dist} dist) — добавляем виртуальные дни",
                replPool.Count, distPool.Count);

            // Capacity самого продуктивного дня (max задач)
            var bestForklifts = forkliftDayData
                .GroupBy(w => w.Day)
                .OrderByDescending(g => g.Sum(w => w.CapacitySec))
                .FirstOrDefault()?.ToDictionary(w => w.WorkerCode, w => w.CapacitySec)
                ?? new Dictionary<string, double>();
            var bestPickers = pickerDayData
                .GroupBy(w => w.Day)
                .OrderByDescending(g => g.Sum(w => w.CapacitySec))
                .FirstOrDefault()?.ToDictionary(w => w.WorkerCode, w => w.CapacitySec)
                ?? new Dictionary<string, double>();

            var lastDay = allDays.Last();
            var maxExtraDays = 30; // защита от бесконечного цикла

            for (int extraDay = 1; extraDay <= maxExtraDays && (replPool.Any() || distPool.Any()); extraDay++)
            {
                var virtualDay = lastDay.AddDays(extraDay);
                var bufferStart = bufferLevel;

                var (replDoneV, distDoneV, dayAssignmentsV, dayMakespanV) = SimulateDay(
                    replPool, distPool, completedRepl,
                    new Dictionary<string, double>(bestForklifts),
                    new Dictionary<string, double>(bestPickers),
                    ref bufferLevel, _bufferCapacity,
                    forkliftTransitionSec, pickerTransitionSec,
                    virtualDay, decisionCtx, workerNames,
                    forkliftSpeedRatio, pickerSpeedRatio,
                    pickerLookup, productCategoryMap ?? new Dictionary<string, string>(),
                    workerCategoryAvg, categoryAvg, waveMeanDurationSec);

                if (replDoneV + distDoneV == 0)
                    break; // нет прогресса — выходим

                foreach (var (wc, actions) in dayAssignmentsV)
                {
                    if (!mergedPlan.WorkerAssignments.ContainsKey(wc))
                        mergedPlan.WorkerAssignments[wc] = new List<ActionTiming>();
                    mergedPlan.WorkerAssignments[wc].AddRange(actions);
                }

                foreach (var (wc, actions) in dayAssignmentsV)
                {
                    if (!actions.Any()) continue;
                    var swt = new SimulatedWorkerTimeline
                    {
                        WorkerCode = wc,
                        WorkerName = workerNames.GetValueOrDefault(wc, ""),
                        Duration = TimeSpan.FromSeconds(actions.Sum(a => a.DurationSec)),
                        TaskCount = actions.Count
                    };
                    foreach (var at in actions)
                    {
                        var (estSec, source) = EstimateActionDuration(
                            at, wc, routeLookup, pickerLookup, waveMeanDurationSec);
                        swt.Actions.Add(new SimulatedAction
                        {
                            FromBin = at.FromBin, ToBin = at.ToBin,
                            FromZone = at.FromZone, ToZone = at.ToZone,
                            ProductCode = at.ProductCode, WeightKg = at.WeightKg,
                            EstimatedDurationSec = estSec, DurationSource = source
                        });
                    }
                    allSimWorkers.Add(swt);
                }

                dayBreakdowns.Add(new DayBreakdown
                {
                    Date = virtualDay,
                    Workers = bestForklifts.Count + bestPickers.Count,
                    ForkliftWorkers = bestForklifts.Count,
                    PickerWorkers = bestPickers.Count,
                    OptimizedMakespan = dayMakespanV,
                    OriginalReplGroups = 0,
                    OriginalDistGroups = 0,
                    OptimizedReplGroups = replDoneV,
                    OptimizedDistGroups = distDoneV,
                    AdditionalPallets = replDoneV + distDoneV,
                    BufferLevelStart = bufferStart,
                    BufferLevelEnd = bufferLevel
                });

                _logger.LogInformation("  Виртуальный день {Day}: +{Repl} repl, +{Dist} dist (осталось {ReplLeft} repl, {DistLeft} dist)",
                    virtualDay.ToString("dd.MM"), replDoneV, distDoneV, replPool.Count, distPool.Count);
            }

            if (replPool.Any() || distPool.Any())
            {
                _logger.LogWarning("После {MaxDays} виртуальных дней остались: {Repl} repl, {Dist} dist",
                    maxExtraDays, replPool.Count, distPool.Count);
            }
        }

        // Агрегация SimulatedWorkerTimeline по работникам
        var simTimeline = new SimulatedTimeline { WaveMeanDurationSec = waveMeanDurationSec };
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

        simTimeline.TotalDuration = TimeSpan.FromSeconds(
            dayBreakdowns.Where(d => d.OptimizedReplGroups + d.OptimizedDistGroups > 0)
                .Sum(d => d.OptimizedMakespan.TotalSeconds));

        return (mergedPlan, simTimeline, dayBreakdowns, priorityByRef);
    }

    /// <summary>
    /// Симуляция одного дня: чередование repl (буфер +1) и dist (буфер -1)
    /// до исчерпания capacity работников или пула.
    /// </summary>
    private (int replDone, int distDone, Dictionary<string, List<ActionTiming>> assignments, TimeSpan makespan)
        SimulateDay(
            List<TaskGroupInfo> replPool,
            List<TaskGroupInfo> distPool,
            HashSet<string> completedRepl,
            Dictionary<string, double> forkliftCapacity,
            Dictionary<string, double> pickerCapacity,
            ref int bufferLevel,
            int bufferCapacity,
            double forkliftTransitionSec,
            double pickerTransitionSec,
            DateTime day = default,
            SimulationDecisionContext? decisionCtx = null,
            Dictionary<string, string>? workerNames = null,
            Dictionary<string, double>? forkliftSpeedRatio = null,
            Dictionary<string, double>? pickerSpeedRatio = null,
            Dictionary<string, PickerProductStats>? pickerLookup = null,
            Dictionary<string, string>? productCategoryMap = null,
            Dictionary<string, double>? workerCategoryAvg = null,
            Dictionary<string, double>? categoryAvg = null,
            double waveMeanDurationSec = 120.0)
    {
        var assignments = new Dictionary<string, List<ActionTiming>>();
        var forkliftLoad = forkliftCapacity.Keys.ToDictionary(k => k, _ => 0.0);
        var forkliftTaskCount = forkliftCapacity.Keys.ToDictionary(k => k, _ => 0);
        var pickerFinishTime = pickerCapacity.Keys.ToDictionary(k => k, _ => 0.0);
        var pickerTaskCount = pickerCapacity.Keys.ToDictionary(k => k, _ => 0);
        var forkliftRemaining = new Dictionary<string, double>(forkliftCapacity);
        var pickerRemaining = new Dictionary<string, double>(pickerCapacity);

        // Per-worker time offset для Ганта (секунды от начала дня)
        var workerTimeOffset = forkliftCapacity.Keys.Concat(pickerCapacity.Keys)
            .ToDictionary(k => k, _ => 0.0);

        // Время готовности палет в буфере (когда форклифт завершит repl)
        // Палеты от предыдущих дней уже в буфере → доступны в t=0
        var palletReadyTimes = new List<double>();
        for (int i = 0; i < bufferLevel; i++)
            palletReadyTimes.Add(0.0);

        int replDone = 0, distDone = 0;
        const double tolerance = 1.0;

        bool progress = true;
        while (progress)
        {
            progress = false;

            // === Фаза 1: загрузить буфер — все свободные форклифты получают задачи ===
            bool replProgress = true;
            while (replProgress && bufferLevel < bufferCapacity && replPool.Any())
            {
                replProgress = false;
                TaskGroupInfo? bestRepl = null;
                string? bestForklift = null;

                foreach (var rg in replPool)
                {
                    // ECT + эффективность: score = текущий load + задача × speedRatio
                    // Быстрый работник (ratio<1) получает меньший score → берёт больше задач
                    var fk = forkliftRemaining
                        .Where(kv => kv.Value >= rg.DurationSec - tolerance)
                        .OrderBy(kv => forkliftLoad[kv.Key]
                            + rg.DurationSec * (forkliftSpeedRatio?.GetValueOrDefault(kv.Key, 1.0) ?? 1.0))
                        .Select(kv => kv.Key)
                        .FirstOrDefault();

                    if (fk != null)
                    {
                        bestRepl = rg;
                        bestForklift = fk;
                        break; // пул отсортирован по приоритету — берём первый подходящий
                    }
                }

                if (bestRepl != null && bestForklift != null)
                {
                    replPool.Remove(bestRepl);
                    // Transition → только finish time (capacity уже включает паузы)
                    var fkTransition = forkliftTaskCount[bestForklift] > 0 ? forkliftTransitionSec : 0;
                    forkliftLoad[bestForklift] += fkTransition + bestRepl.DurationSec;
                    forkliftRemaining[bestForklift] -= bestRepl.DurationSec;
                    forkliftTaskCount[bestForklift]++;
                    completedRepl.Add(bestRepl.TaskGroupRef);
                    bufferLevel++;
                    replDone++;
                    // Палета будет в буфере когда форклифт закончит
                    palletReadyTimes.Add(forkliftLoad[bestForklift]);
                    palletReadyTimes.Sort();

                    if (!assignments.ContainsKey(bestForklift))
                        assignments[bestForklift] = new List<ActionTiming>();
                    foreach (var aa in bestRepl.Actions)
                        assignments[bestForklift].Add(CreateActionTiming(aa, bestForklift));

                    // --- Сбор данных для БД ---
                    if (decisionCtx != null)
                    {
                        var altForklifts = forkliftRemaining
                            .Where(kv => kv.Key != bestForklift)
                            .OrderByDescending(kv => kv.Value).Take(3)
                            .Select(kv => new { code = kv.Key, remaining = Math.Round(kv.Value, 1),
                                load = Math.Round(forkliftLoad[kv.Key], 1), tasks = forkliftTaskCount[kv.Key] });
                        var altRepls = replPool.Take(3)
                            .Select(r => new { ref_ = r.TaskGroupRef, priority = Math.Round(r.Priority, 0),
                                duration = Math.Round(r.DurationSec, 1), weight = Math.Round(r.WeightKg, 1) });

                        decisionCtx.Decisions.Add(new DecisionLogRecord
                        {
                            DecisionSeq = ++decisionCtx.DecisionCounter,
                            DayDate = day,
                            DecisionType = "assign_repl",
                            TaskRef = bestRepl.TaskGroupRef,
                            TaskType = "Replenishment",
                            ChosenWorkerCode = bestForklift,
                            ChosenWorkerRemainingSec = forkliftRemaining[bestForklift],
                            ChosenTaskPriority = bestRepl.Priority,
                            ChosenTaskDurationSec = bestRepl.DurationSec,
                            ChosenTaskWeightKg = (decimal)bestRepl.WeightKg,
                            BufferLevelBefore = bufferLevel - 1,
                            BufferLevelAfter = bufferLevel,
                            BufferCapacity = bufferCapacity,
                            AltWorkersJson = JsonSerializer.Serialize(altForklifts),
                            AltTasksJson = JsonSerializer.Serialize(altRepls),
                            ActiveConstraint = "none",
                            ReasonText = $"Палета приоритет={bestRepl.Priority:F0}, вес={bestRepl.WeightKg:F1}кг → форклифт {bestForklift} (остаток {forkliftRemaining[bestForklift]:F0}с)"
                        });

                        // Событие Ганта (optimized)
                        var startOffset = workerTimeOffset[bestForklift] + fkTransition;
                        var endOffset = startOffset + bestRepl.DurationSec;
                        workerTimeOffset[bestForklift] = endOffset;
                        var firstAction = bestRepl.Actions.FirstOrDefault();

                        decisionCtx.ScheduleEvents.Add(new ScheduleEventRecord
                        {
                            TimelineType = "optimized",
                            WorkerCode = bestForklift,
                            WorkerName = workerNames?.GetValueOrDefault(bestForklift),
                            WorkerRole = "Forklift",
                            TaskRef = bestRepl.TaskGroupRef,
                            TaskType = "Replenishment",
                            DayDate = day,
                            StartTime = day.Date.AddSeconds(startOffset),
                            EndTime = day.Date.AddSeconds(endOffset),
                            DurationSec = bestRepl.DurationSec,
                            FromBin = firstAction?.Action.StorageBin,
                            ToBin = firstAction?.Action.AllocationBin,
                            FromZone = firstAction != null ? ExtractZone(firstAction.Action.StorageBin) : null,
                            ToZone = firstAction != null ? ExtractZone(firstAction.Action.AllocationBin) : null,
                            ProductCode = firstAction?.Action.ProductCode,
                            ProductName = firstAction?.Action.ProductName,
                            WeightKg = bestRepl.Actions.Sum(a => a.Action.WeightKg),
                            Qty = bestRepl.Actions.Sum(a => a.Action.QtyPlan > 0 ? a.Action.QtyPlan : 1),
                            SequenceNumber = ++decisionCtx.SequenceCounter,
                            BufferLevel = bufferLevel,
                            TransitionSec = fkTransition
                        });
                    }

                    replProgress = true;
                    progress = true;
                }
            }

            // === Фаза 2: разгрузить буфер — все свободные пикеры получают задачи ===
            bool distProgress = true;
            while (distProgress && bufferLevel > 0)
            {
                distProgress = false;
                var readyDist = distPool
                    .Where(d => string.IsNullOrEmpty(d.PrevTaskRef) || completedRepl.Contains(d.PrevTaskRef))
                    .OrderByDescending(d => d.Priority)
                    .FirstOrDefault();

                if (readyDist != null)
                {
                    // Персональная оценка длительности для каждого кандидата-пикера
                    string? bestPicker = null;
                    double bestPickerEstDur = 0;
                    double bestPickerScore = double.MaxValue;
                    var hasPersonalEstimate = pickerLookup != null && productCategoryMap != null
                        && workerCategoryAvg != null && categoryAvg != null;

                    // Самая ранняя палета в буфере — пикер не начнёт раньше
                    var earliestPalletTime = palletReadyTimes.Count > 0 ? palletReadyTimes[0] : 0.0;

                    foreach (var kv in pickerRemaining)
                    {
                        var estDur = hasPersonalEstimate
                            ? EstimateGroupDurationForWorker(readyDist, kv.Key,
                                pickerLookup!, productCategoryMap!, workerCategoryAvg!, categoryAvg!,
                                pickerSpeedRatio ?? new Dictionary<string, double>(), waveMeanDurationSec)
                            : readyDist.DurationSec * (pickerSpeedRatio?.GetValueOrDefault(kv.Key, 1.0) ?? 1.0);

                        if (kv.Value < estDur - tolerance) continue; // не хватает capacity

                        // ECT: реальный старт = max(когда пикер свободен, когда палета готова)
                        var realStart = Math.Max(pickerFinishTime[kv.Key], earliestPalletTime);
                        var score = realStart + estDur;
                        if (score < bestPickerScore)
                        {
                            bestPickerScore = score;
                            bestPicker = kv.Key;
                            bestPickerEstDur = estDur;
                        }
                    }

                    if (bestPicker != null)
                    {
                        distPool.Remove(readyDist);
                        // Transition → только finish time (capacity уже включает паузы)
                        var pkTransition = pickerTaskCount[bestPicker] > 0 ? pickerTransitionSec : 0;
                        pickerFinishTime[bestPicker] += pkTransition + bestPickerEstDur;
                        pickerRemaining[bestPicker] -= bestPickerEstDur;
                        pickerTaskCount[bestPicker]++;
                        bufferLevel--;
                        distDone++;
                        // Забрать самую раннюю готовую палету из буфера
                        var palletReadyTime = 0.0;
                        if (palletReadyTimes.Count > 0)
                        {
                            palletReadyTime = palletReadyTimes[0];
                            palletReadyTimes.RemoveAt(0);
                        }

                        if (!assignments.ContainsKey(bestPicker))
                            assignments[bestPicker] = new List<ActionTiming>();
                        // Масштабировать длительности действий пропорционально персональной оценке
                        var origGroupDur = readyDist.DurationSec > 0 ? readyDist.DurationSec : 1.0;
                        var durScale = bestPickerEstDur / origGroupDur;
                        foreach (var aa in readyDist.Actions)
                        {
                            var at = CreateActionTiming(aa, bestPicker);
                            at.DurationSec *= durScale;
                            assignments[bestPicker].Add(at);
                        }

                        // --- Сбор данных для БД ---
                        if (decisionCtx != null)
                        {
                            var altPickers = pickerRemaining
                                .Where(kv => kv.Key != bestPicker)
                                .OrderBy(kv => pickerFinishTime[kv.Key]).Take(3)
                                .Select(kv => new { code = kv.Key, remaining = Math.Round(kv.Value, 1),
                                    load = Math.Round(pickerFinishTime[kv.Key], 1), tasks = pickerTaskCount[kv.Key] });
                            var altDists = distPool
                                .Where(d => string.IsNullOrEmpty(d.PrevTaskRef) || completedRepl.Contains(d.PrevTaskRef))
                                .Take(3)
                                .Select(d => new { ref_ = d.TaskGroupRef, priority = Math.Round(d.Priority, 0),
                                    duration = Math.Round(d.DurationSec, 1), weight = Math.Round(d.WeightKg, 1) });

                            decisionCtx.Decisions.Add(new DecisionLogRecord
                            {
                                DecisionSeq = ++decisionCtx.DecisionCounter,
                                DayDate = day,
                                DecisionType = "assign_dist",
                                TaskRef = readyDist.TaskGroupRef,
                                TaskType = "Distribution",
                                ChosenWorkerCode = bestPicker,
                                ChosenWorkerRemainingSec = pickerRemaining[bestPicker],
                                ChosenTaskPriority = readyDist.Priority,
                                ChosenTaskDurationSec = bestPickerEstDur,
                                ChosenTaskWeightKg = (decimal)readyDist.WeightKg,
                                BufferLevelBefore = bufferLevel + 1,
                                BufferLevelAfter = bufferLevel,
                                BufferCapacity = bufferCapacity,
                                AltWorkersJson = JsonSerializer.Serialize(altPickers),
                                AltTasksJson = JsonSerializer.Serialize(altDists),
                                ActiveConstraint = "none",
                                ReasonText = $"Палета приоритет={readyDist.Priority:F0}, вес={readyDist.WeightKg:F1}кг → пикер {bestPicker} (ест.длит={bestPickerEstDur:F0}с, финиш {pickerFinishTime[bestPicker]:F0}с)"
                            });

                            // Событие Ганта (optimized)
                            // Пикер не может начать раньше чем палета готова в буфере
                            var pickerFreeAt = workerTimeOffset[bestPicker] + pkTransition;
                            var startOffset = Math.Max(pickerFreeAt, palletReadyTime);
                            var endOffset = startOffset + bestPickerEstDur;
                            workerTimeOffset[bestPicker] = endOffset;
                            var firstAction = readyDist.Actions.FirstOrDefault();

                            decisionCtx.ScheduleEvents.Add(new ScheduleEventRecord
                            {
                                TimelineType = "optimized",
                                WorkerCode = bestPicker,
                                WorkerName = workerNames?.GetValueOrDefault(bestPicker),
                                WorkerRole = "Picker",
                                TaskRef = readyDist.TaskGroupRef,
                                TaskType = "Distribution",
                                DayDate = day,
                                StartTime = day.Date.AddSeconds(startOffset),
                                EndTime = day.Date.AddSeconds(endOffset),
                                DurationSec = bestPickerEstDur,
                                FromBin = firstAction?.Action.StorageBin,
                                ToBin = firstAction?.Action.AllocationBin,
                                FromZone = firstAction != null ? ExtractZone(firstAction.Action.StorageBin) : null,
                                ToZone = firstAction != null ? ExtractZone(firstAction.Action.AllocationBin) : null,
                                ProductCode = firstAction?.Action.ProductCode,
                                ProductName = firstAction?.Action.ProductName,
                                WeightKg = readyDist.Actions.Sum(a => a.Action.WeightKg),
                                Qty = readyDist.Actions.Sum(a => a.Action.QtyPlan > 0 ? a.Action.QtyPlan : 1),
                                SequenceNumber = ++decisionCtx.SequenceCounter,
                                BufferLevel = bufferLevel,
                                DurationSource = hasPersonalEstimate ? "personal_cascade" : "speed_ratio",
                                TransitionSec = pkTransition
                            });
                        }

                        distProgress = true;
                        progress = true;
                    }
                }
            }
        }

        // --- Записать skip-решения при остатке задач ---
        if (decisionCtx != null)
        {
            if (replPool.Any())
            {
                var constraint = bufferLevel >= bufferCapacity ? "buffer_full" : "no_capacity";
                decisionCtx.Decisions.Add(new DecisionLogRecord
                {
                    DecisionSeq = ++decisionCtx.DecisionCounter,
                    DayDate = day,
                    DecisionType = "skip_repl",
                    ActiveConstraint = constraint,
                    BufferLevelBefore = bufferLevel,
                    BufferLevelAfter = bufferLevel,
                    BufferCapacity = bufferCapacity,
                    ReasonText = $"День {day:dd.MM}: {replPool.Count} repl не назначены ({constraint})"
                });
            }
            if (distPool.Any())
            {
                var readyCount = distPool.Count(d => string.IsNullOrEmpty(d.PrevTaskRef) || completedRepl.Contains(d.PrevTaskRef));
                var constraint = bufferLevel <= 0 ? "buffer_empty"
                    : readyCount == 0 ? "no_ready_dist" : "no_capacity";
                decisionCtx.Decisions.Add(new DecisionLogRecord
                {
                    DecisionSeq = ++decisionCtx.DecisionCounter,
                    DayDate = day,
                    DecisionType = "skip_dist",
                    ActiveConstraint = constraint,
                    BufferLevelBefore = bufferLevel,
                    BufferLevelAfter = bufferLevel,
                    BufferCapacity = bufferCapacity,
                    ReasonText = $"День {day:dd.MM}: {distPool.Count} dist не назначены ({constraint}, готово={readyCount})"
                });
            }
        }

        // Makespan = макс. offset любого работника (с учётом ожидания палет)
        var makespan = TimeSpan.FromSeconds(
            workerTimeOffset.Any() ? workerTimeOffset.Values.Max() : 0);

        return (replDone, distDone, assignments, makespan);
    }

    /// <summary>
    /// Приоритет палеты: тяжёлые+быстрые первыми, дальние потом
    /// </summary>
    private double CalculatePriority(TaskGroupInfo group, Dictionary<string, RouteStatistics> routeLookup)
    {
        var weightScore = group.WeightKg * 1000;
        var speedScore = -group.DurationSec * 10;

        var zoneDistances = group.Actions
            .Select(a =>
            {
                var key = $"{ExtractZone(a.Action.StorageBin)}→{ExtractZone(a.Action.AllocationBin)}";
                return routeLookup.TryGetValue(key, out var rs)
                    ? (double)rs.AvgDurationSec
                    : DefaultRouteDurationSec;
            })
            .ToList();
        var distanceScore = -(zoneDistances.Any() ? zoneDistances.Average() : 0);

        return weightScore + speedScore + distanceScore;
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
        List<DayBreakdown> dayBreakdowns,
        Dictionary<string, double>? priorityByRef = null)
    {
        // Итоговое сравнение: makespan на обеих сторонах (сопоставимые метрики)
        // Факт: сумма ежедневных makespans (макс. рабочий от первого до последнего действия)
        var actualDuration = TimeSpan.FromSeconds(
            dayBreakdowns.Sum(d => d.ActualMakespan.TotalSeconds));

        // Оптимизированное: сумма ежедневных makespans (wall-clock за день)
        var optimizedDuration = TimeSpan.FromSeconds(
            dayBreakdowns.Sum(d => d.OptimizedMakespan.TotalSeconds));

        var improvementTime = actualDuration - optimizedDuration;

        // Основная метрика: сокращение дней волны
        var origWaveDays = dayBreakdowns.Count(d => d.OriginalReplGroups + d.OriginalDistGroups > 0);
        var optWaveDays = dayBreakdowns.Count(d => d.OptimizedReplGroups + d.OptimizedDistGroups > 0);
        var improvementPercent = origWaveDays > 0
            ? ((double)(origWaveDays - optWaveDays) / origWaveDays) * 100
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

        // Детали заданий — per pallet (task group), НЕ per action
        // Только палеты с назначенным работником (сопоставимо с оптимизацией)
        var taskDetails = new List<TaskDetail>();
        var allGroups = data.ReplenishmentTasks.Concat(data.DistributionTasks)
            .Where(g => !string.IsNullOrWhiteSpace(g.AssigneeCode))
            .ToList();

        // Маппинг TaskRef → оптимизированный работник (из plan assignments)
        var optWorkerByRef = new Dictionary<string, string>();
        foreach (var (wc, actions) in plan.WorkerAssignments)
        {
            foreach (var tgRef in actions.Select(a => a.TaskGroupRef).Distinct())
            {
                if (!string.IsNullOrEmpty(tgRef))
                    optWorkerByRef[tgRef] = wc;
            }
        }

        // Маппинг TaskRef → оптимизированная длительность (сумма DurationSec действий из плана)
        var optDurationByRef = plan.WorkerAssignments
            .SelectMany(kv => kv.Value)
            .Where(a => !string.IsNullOrEmpty(a.TaskGroupRef))
            .GroupBy(a => a.TaskGroupRef)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.DurationSec));

        // Объединяем task groups с одинаковым TaskRef (одна палета = все действия)
        var replRefs = new HashSet<string>(data.ReplenishmentTasks.Select(g => g.TaskRef));
        var mergedGroups = allGroups
            .GroupBy(g => g.TaskRef)
            .Select(grp =>
            {
                var first = grp.First();
                var allActions = grp.SelectMany(g => g.Actions).ToList();
                var taskType = replRefs.Contains(grp.Key) ? "Replenishment" : "Distribution";

                // Реальные timestamps палеты (от первого действия до последнего)
                var starts = allActions.Where(a => a.StartedAt.HasValue).Select(a => a.StartedAt!.Value).ToList();
                var ends = allActions.Where(a => a.CompletedAt.HasValue).Select(a => a.CompletedAt!.Value).ToList();
                var groupStart = starts.Any() ? starts.Min() : (DateTime?)null;
                var groupEnd = ends.Any() ? ends.Max() : (DateTime?)null;
                var groupDuration = (groupStart.HasValue && groupEnd.HasValue && groupEnd > groupStart)
                    ? (groupEnd.Value - groupStart.Value).TotalSeconds : (double?)null;

                // Маршрут и ячейки
                var firstAction = allActions.FirstOrDefault();
                var lastAction = allActions.LastOrDefault();
                var fromBin = firstAction?.StorageBin ?? "";
                var toBin = firstAction?.AllocationBin ?? "";
                var route = $"{ExtractZone(fromBin)}→{ExtractZone(toBin)}";

                return new TaskDetail
                {
                    TaskNumber = first.TaskNumber,
                    TaskRef = grp.Key,
                    PrevTaskRef = first.PrevTaskRef,
                    WorkerCode = first.AssigneeCode,
                    WorkerName = first.AssigneeName,
                    TaskType = taskType,
                    Route = route,
                    FromBin = fromBin,
                    ToBin = toBin,
                    ActionCount = allActions.Count,
                    TotalWeightKg = allActions.Sum(a => a.WeightKg),
                    TotalQty = allActions.Sum(a => a.QtyFact > 0 ? a.QtyFact : a.QtyPlan),
                    StartedAt = groupStart,
                    CompletedAt = groupEnd,
                    ActualDurationSec = groupDuration,
                    OptimizedDurationSec = optDurationByRef.GetValueOrDefault(grp.Key,
                        groupDuration ?? DefaultRouteDurationSec),
                    OptimizedWorkerCode = optWorkerByRef.GetValueOrDefault(grp.Key),
                    PriorityScore = priorityByRef?.GetValueOrDefault(grp.Key) ?? 0
                };
            }).ToList();

        taskDetails.AddRange(mergedGroups);

        // Связать repl↔dist через PrevTaskRef
        var detailByRef = taskDetails
            .Where(d => !string.IsNullOrEmpty(d.TaskRef))
            .GroupBy(d => d.TaskRef)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var td in taskDetails)
        {
            if (td.TaskType == "Distribution" && !string.IsNullOrEmpty(td.PrevTaskRef))
            {
                // dist → repl: кто принёс эту палету
                if (detailByRef.TryGetValue(td.PrevTaskRef, out var repl))
                {
                    td.LinkedWorkerCode = repl.WorkerCode;
                    td.LinkedWorkerName = repl.WorkerName;
                    td.LinkedActualDurationSec = repl.ActualDurationSec;
                    td.LinkedOptWorkerCode = repl.OptimizedWorkerCode;
                    td.LinkedOptDurationSec = repl.OptimizedDurationSec;

                    // repl → dist: кто забрал палету
                    repl.LinkedWorkerCode = td.WorkerCode;
                    repl.LinkedWorkerName = td.WorkerName;
                    repl.LinkedActualDurationSec = td.ActualDurationSec;
                    repl.LinkedOptWorkerCode = td.OptimizedWorkerCode;
                    repl.LinkedOptDurationSec = td.OptimizedDurationSec;
                }
            }
        }

        // Сортировка по времени начала
        taskDetails = taskDetails
            .OrderBy(d => d.StartedAt ?? DateTime.MaxValue)
            .ThenBy(d => d.TaskType)
            .ToList();

        // Заполнить DurationSource для TaskDetails из SimulatedAction
        // (берём преобладающий source для actions в каждой группе)
        var durationSourceByRef = simulated.WorkerTimelines
            .SelectMany(w => w.Actions.Select(a => new { w.WorkerCode, a.FromBin, a.DurationSource }))
            .Where(a => !string.IsNullOrEmpty(a.DurationSource))
            .GroupBy(a => a.FromBin) // группируем по FromBin как proxy для TaskRef
            .ToDictionary(g => g.Key, g => g.First().DurationSource);

        // Более точный маппинг: по WorkerCode + TaskGroupRef из plan assignments
        var sourceByTaskRef = new Dictionary<string, string>();
        foreach (var (wc, actions) in plan.WorkerAssignments)
        {
            var simActions = simulated.WorkerTimelines
                .FirstOrDefault(w => w.WorkerCode == wc)?.Actions ?? new List<SimulatedAction>();
            // Индексируем по FromBin для соответствия
            var actionIndex = 0;
            foreach (var pa in actions)
            {
                if (actionIndex < simActions.Count && !string.IsNullOrEmpty(pa.TaskGroupRef))
                {
                    sourceByTaskRef.TryAdd(pa.TaskGroupRef, simActions[actionIndex].DurationSource);
                }
                actionIndex++;
            }
        }
        foreach (var td in taskDetails)
        {
            if (sourceByTaskRef.TryGetValue(td.TaskRef, out var src))
                td.DurationSource = src;
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
            TotalReplenishmentTasks = data.ReplenishmentTasks
                .Where(g => !string.IsNullOrWhiteSpace(g.AssigneeCode))
                .GroupBy(g => g.TaskRef).Select(g => g.First())
                .Sum(g => g.Actions.Count),
            TotalDistributionTasks = data.DistributionTasks
                .Where(g => !string.IsNullOrWhiteSpace(g.AssigneeCode))
                .GroupBy(g => g.TaskRef).Select(g => g.First())
                .Sum(g => g.Actions.Count),
            TotalActions = data.ReplenishmentTasks
                .Where(g => !string.IsNullOrWhiteSpace(g.AssigneeCode))
                .GroupBy(g => g.TaskRef).Select(g => g.First())
                .Sum(g => g.Actions.Count) +
                data.DistributionTasks
                .Where(g => !string.IsNullOrWhiteSpace(g.AssigneeCode))
                .GroupBy(g => g.TaskRef).Select(g => g.First())
                .Sum(g => g.Actions.Count),
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
            WaveMeanDurationSec = simulated.WaveMeanDurationSec,
            // Кросс-дневные метрики
            TotalReplGroups = data.ReplenishmentTasks
                .Where(t => !string.IsNullOrWhiteSpace(t.AssigneeCode))
                .Select(t => t.TaskRef).Distinct().Count(),
            TotalDistGroups = data.DistributionTasks
                .Where(t => !string.IsNullOrWhiteSpace(t.AssigneeCode))
                .Select(t => t.TaskRef).Distinct().Count(),
            OriginalWaveDays = origWaveDays,
            OptimizedWaveDays = optWaveDays,
            DaysSaved = origWaveDays - optWaveDays,
            BufferCapacity = _bufferCapacity
        };
    }

    // ============================================================================
    // Вспомогательные методы
    // ============================================================================

    /// <summary>
    /// Оценить длительность ВСЕЙ палеты для КОНКРЕТНОГО работника.
    /// Каскад: 1) worker×product → 2) worker×category → 3) category avg → 4) speedRatio → 5) wave mean
    /// </summary>
    private static double EstimateGroupDurationForWorker(
        TaskGroupInfo task,
        string workerCode,
        Dictionary<string, PickerProductStats> pickerLookup,
        Dictionary<string, string> productCategoryMap,
        Dictionary<string, double> workerCategoryAvg,
        Dictionary<string, double> categoryAvg,
        Dictionary<string, double> speedRatio,
        double waveMeanDurationSec)
    {
        // Каскад вычисляет персональный коэффициент = personalSum / baselineSum,
        // затем умножает на масштабированную длительность группы (task.DurationSec).
        // Это нужно потому что task.DurationSec уже масштабирована под capacity,
        // а aa.DurationSec — сырые длительности (их сумма >> task.DurationSec).
        double personalSum = 0;
        double baselineSum = 0;

        foreach (var aa in task.Actions)
        {
            var productCode = aa.Action.ProductCode ?? "";
            var originalDur = aa.DurationSec > 0 ? aa.DurationSec : waveMeanDurationSec;
            baselineSum += originalDur;

            // 1. Worker × Product (самое точное)
            if (!string.IsNullOrEmpty(productCode))
            {
                var key = $"{workerCode}:{productCode}";
                if (pickerLookup.TryGetValue(key, out var ps) && ps.AvgDurationSec.HasValue)
                {
                    personalSum += (double)ps.AvgDurationSec.Value;
                    continue;
                }

                // 2. Worker × Category
                if (productCategoryMap.TryGetValue(productCode, out var cat) && !string.IsNullOrEmpty(cat))
                {
                    var catKey = $"{workerCode}:{cat}";
                    if (workerCategoryAvg.TryGetValue(catKey, out var wcAvg))
                    {
                        personalSum += wcAvg;
                        continue;
                    }

                    // 3. Category avg (все работники)
                    if (categoryAvg.TryGetValue(cat, out var cAvg))
                    {
                        personalSum += cAvg;
                        continue;
                    }
                }
            }

            // 4. Средняя скорость работника (speedRatio × оригинальная длительность)
            var ratio = speedRatio.GetValueOrDefault(workerCode, 1.0);
            personalSum += originalDur * ratio;
            // (если ratio нет — уровень 5: originalDur × 1.0 = среднее по складу/волне)
        }

        // Коэффициент: насколько этот работник быстрее/медленнее среднего для данных действий
        // Результат = масштабированная длительность группы × персональный коэффициент
        if (baselineSum > 0)
            return Math.Max(task.DurationSec * (personalSum / baselineSum), 1.0);
        return Math.Max(task.DurationSec, 1.0);
    }

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
    /// Wall-clock длительность группы действий (span от первого StartedAt до последнего CompletedAt).
    /// Корректно при перекрывающихся timestamps в 1С.
    /// </summary>
    private static double ComputeGroupSpanSec(List<AnnotatedAction> actions)
    {
        var withTimestamps = actions
            .Where(a => a.Action.StartedAt.HasValue && a.Action.CompletedAt.HasValue)
            .ToList();

        if (!withTimestamps.Any())
            return actions.Sum(a => a.DurationSec); // fallback

        var start = withTimestamps.Min(a => a.Action.StartedAt!.Value);
        var end = withTimestamps.Max(a => a.Action.CompletedAt!.Value);
        var span = (end - start).TotalSeconds;

        return span > 0 ? span : actions.Sum(a => a.DurationSec);
    }

    /// <summary>
    /// Merged interval duration работника за день.
    /// Корректно при перекрывающихся timestamps — даёт реальное wall-clock время.
    /// </summary>
    private static double ComputeWorkerDayCapacitySec(List<AnnotatedAction> workerActions)
    {
        var intervals = workerActions
            .Where(a => a.Action.StartedAt.HasValue && a.Action.CompletedAt.HasValue)
            .Select(a => (start: a.Action.StartedAt!.Value, end: a.Action.CompletedAt!.Value))
            .Where(i => i.end > i.start)
            .OrderBy(i => i.start)
            .ToList();

        if (!intervals.Any())
            return workerActions.Sum(a => a.DurationSec); // fallback

        return MergeIntervalsAndSum(intervals).TotalSeconds;
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
