using NRules;
using NRules.Fluent;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Layers.Realtime.StateMachine;

namespace WMS.BufferManagement.Layers.Realtime.Rules;

/// <summary>
/// Движок правил NRules
/// </summary>
public class RuleEngine
{
    private readonly ISession _session;
    private readonly List<RecommendedAction> _actions = new();

    public IReadOnlyList<RecommendedAction> LastActions => _actions;

    public RuleEngine()
    {
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(CriticalBufferRule).Assembly));

        var factory = repository.Compile();
        _session = factory.CreateSession();
    }

    /// <summary>
    /// Вычислить рекомендуемые действия
    /// </summary>
    public IReadOnlyList<RecommendedAction> Evaluate(
        BufferZone buffer,
        BufferState state,
        int pendingTasks,
        IEnumerable<Forklift> forklifts,
        double consumptionRate)
    {
        _actions.Clear();

        // Создаём факт буфера
        var bufferFact = new BufferFact
        {
            FillLevel = buffer.FillLevel,
            State = state,
            PendingTasks = pendingTasks,
            IdleForklifts = forklifts.Count(f => f.State == ForkliftState.Idle),
            ConsumptionRate = consumptionRate
        };

        // Очищаем предыдущие факты
        _session.Retract(_session.Query<BufferFact>().ToList());
        _session.Retract(_session.Query<RecommendedAction>().ToList());

        // Вставляем новые факты
        _session.Insert(bufferFact);

        foreach (var forklift in forklifts)
        {
            _session.Insert(new ForkliftFact { Forklift = forklift });
        }

        // Запускаем правила
        _session.Fire();

        // Собираем рекомендации
        _actions.AddRange(_session.Query<RecommendedAction>()
            .OrderByDescending(a => a.Priority));

        return _actions;
    }

    /// <summary>
    /// Получить наиболее приоритетное действие
    /// </summary>
    public RecommendedAction? GetTopAction()
    {
        return _actions.OrderByDescending(a => a.Priority).FirstOrDefault();
    }
}
