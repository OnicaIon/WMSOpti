using Stateless;
using WMS.BufferManagement.Infrastructure.Configuration;

namespace WMS.BufferManagement.Layers.Realtime.StateMachine;

/// <summary>
/// Конечный автомат для управления состоянием буфера
/// </summary>
public class BufferStateMachine
{
    private readonly StateMachine<BufferState, BufferTrigger> _machine;
    private readonly BufferConfig _config;

    public BufferState CurrentState => _machine.State;

    public event Action<BufferState, BufferState>? StateChanged;

    public BufferStateMachine(BufferConfig config)
    {
        _config = config;
        _machine = new StateMachine<BufferState, BufferTrigger>(BufferState.Normal);

        ConfigureTransitions();
    }

    private void ConfigureTransitions()
    {
        // Normal state transitions
        _machine.Configure(BufferState.Normal)
            .Permit(BufferTrigger.LevelDropped, BufferState.Low)
            .Permit(BufferTrigger.LevelOverflow, BufferState.Overflow)
            .Permit(BufferTrigger.LevelCritical, BufferState.Critical)
            .OnEntry(() => OnStateChanged(BufferState.Normal));

        // Low state transitions
        _machine.Configure(BufferState.Low)
            .Permit(BufferTrigger.LevelNormalized, BufferState.Normal)
            .Permit(BufferTrigger.LevelCritical, BufferState.Critical)
            .Permit(BufferTrigger.LevelOverflow, BufferState.Overflow)
            .OnEntry(() => OnStateChanged(BufferState.Low));

        // Critical state transitions
        _machine.Configure(BufferState.Critical)
            .Permit(BufferTrigger.LevelRaised, BufferState.Low)
            .Permit(BufferTrigger.LevelNormalized, BufferState.Normal)
            .OnEntry(() => OnStateChanged(BufferState.Critical));

        // Overflow state transitions
        _machine.Configure(BufferState.Overflow)
            .Permit(BufferTrigger.LevelNormalized, BufferState.Normal)
            .Permit(BufferTrigger.LevelDropped, BufferState.Low)
            .OnEntry(() => OnStateChanged(BufferState.Overflow));
    }

    private void OnStateChanged(BufferState newState)
    {
        // Will be called after transition
    }

    /// <summary>
    /// Обновить состояние на основе уровня буфера
    /// </summary>
    public void UpdateLevel(double fillLevel)
    {
        var previousState = _machine.State;
        var deadBand = _config.DeadBand;

        // Determine trigger based on level and hysteresis
        BufferTrigger? trigger = _machine.State switch
        {
            BufferState.Normal when fillLevel < _config.CriticalThreshold =>
                BufferTrigger.LevelCritical,

            BufferState.Normal when fillLevel < _config.LowThreshold - deadBand =>
                BufferTrigger.LevelDropped,

            BufferState.Normal when fillLevel > _config.HighThreshold + deadBand =>
                BufferTrigger.LevelOverflow,

            BufferState.Low when fillLevel < _config.CriticalThreshold =>
                BufferTrigger.LevelCritical,

            BufferState.Low when fillLevel > _config.LowThreshold + deadBand =>
                BufferTrigger.LevelNormalized,

            BufferState.Critical when fillLevel > _config.CriticalThreshold + deadBand =>
                BufferTrigger.LevelRaised,

            BufferState.Overflow when fillLevel < _config.HighThreshold - deadBand =>
                BufferTrigger.LevelNormalized,

            _ => null
        };

        if (trigger.HasValue && _machine.CanFire(trigger.Value))
        {
            _machine.Fire(trigger.Value);

            if (_machine.State != previousState)
            {
                StateChanged?.Invoke(previousState, _machine.State);
            }
        }
    }

    /// <summary>
    /// Получить рекомендуемое количество активных карщиков
    /// </summary>
    public int GetRecommendedForkliftCount(int totalForklifts)
    {
        return _machine.State switch
        {
            BufferState.Critical => totalForklifts, // Все карщики
            BufferState.Low => Math.Max(2, totalForklifts - 1), // Большинство
            BufferState.Normal => Math.Max(1, totalForklifts / 2), // Половина
            BufferState.Overflow => 1, // Минимум
            _ => 1
        };
    }

    /// <summary>
    /// Получить приоритет подачи
    /// </summary>
    public int GetDeliveryPriority()
    {
        return _machine.State switch
        {
            BufferState.Critical => 100,
            BufferState.Low => 75,
            BufferState.Normal => 50,
            BufferState.Overflow => 10,
            _ => 50
        };
    }
}
