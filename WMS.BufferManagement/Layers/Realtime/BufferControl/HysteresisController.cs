using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Layers.Realtime.StateMachine;

namespace WMS.BufferManagement.Layers.Realtime.BufferControl;

/// <summary>
/// Гистерезисный контроллер буфера
/// </summary>
public class HysteresisController : IBufferController
{
    private readonly BufferConfig _config;
    private readonly BufferStateMachine _stateMachine;

    private double _currentLevel;
    private double _targetLevel;
    private double _lastConsumptionRate;

    public double CurrentLevel => _currentLevel;
    public BufferState CurrentState => _stateMachine.CurrentState;
    public bool IsUrgentDeliveryRequired => _stateMachine.CurrentState == BufferState.Critical;

    public event Action<BufferState, BufferState>? StateChanged;

    public HysteresisController(BufferConfig config)
    {
        _config = config;
        _stateMachine = new BufferStateMachine(config);
        _targetLevel = (_config.LowThreshold + _config.HighThreshold) / 2; // Target = middle

        _stateMachine.StateChanged += (prev, curr) => StateChanged?.Invoke(prev, curr);
    }

    public void Update(BufferZone buffer, double consumptionRate)
    {
        _currentLevel = buffer.FillLevel;
        _lastConsumptionRate = consumptionRate;
        _stateMachine.UpdateLevel(_currentLevel);
    }

    public double CalculateRequiredDeliveryRate(double consumptionRate)
    {
        // Базовая скорость = скорость потребления
        var baseRate = consumptionRate;

        // Корректировка на основе отклонения от целевого уровня
        var levelError = _targetLevel - _currentLevel;

        // Коэффициент усиления зависит от состояния
        var gain = _stateMachine.CurrentState switch
        {
            BufferState.Critical => 3.0,  // Очень агрессивное восстановление
            BufferState.Low => 1.5,       // Умеренное восстановление
            BufferState.Normal => 1.0,    // Поддержание
            BufferState.Overflow => 0.5,  // Снижение подачи
            _ => 1.0
        };

        // Требуемая скорость = базовая + коррекция
        var requiredRate = baseRate * gain + levelError * baseRate * 2;

        return Math.Max(0, requiredRate);
    }

    public int GetPalletsToRequest()
    {
        // Количество палет для запроса на основе текущего состояния
        var deficit = (int)((_targetLevel - _currentLevel) * _config.Capacity);

        return _stateMachine.CurrentState switch
        {
            BufferState.Critical => Math.Max(5, deficit + 3), // Минимум 5 + дефицит
            BufferState.Low => Math.Max(3, deficit + 1),      // Минимум 3
            BufferState.Normal => Math.Max(1, deficit),       // По необходимости
            BufferState.Overflow => 0,                        // Не запрашивать
            _ => 1
        };
    }

    /// <summary>
    /// Получить рекомендуемое количество карщиков
    /// </summary>
    public int GetRecommendedForkliftCount(int totalForklifts)
    {
        return _stateMachine.GetRecommendedForkliftCount(totalForklifts);
    }
}
