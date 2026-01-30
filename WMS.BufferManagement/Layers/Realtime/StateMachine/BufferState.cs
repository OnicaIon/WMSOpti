namespace WMS.BufferManagement.Layers.Realtime.StateMachine;

/// <summary>
/// Состояния буфера
/// </summary>
public enum BufferState
{
    /// <summary>
    /// Нормальный уровень (30-70%)
    /// </summary>
    Normal,

    /// <summary>
    /// Низкий уровень (15-30%) - нужна дополнительная подача
    /// </summary>
    Low,

    /// <summary>
    /// Критический уровень (< 15%) - срочная подача всеми карщиками
    /// </summary>
    Critical,

    /// <summary>
    /// Переполнение (> 70%) - снизить интенсивность подачи
    /// </summary>
    Overflow
}

/// <summary>
/// Триггеры для перехода между состояниями
/// </summary>
public enum BufferTrigger
{
    LevelDropped,
    LevelRaised,
    LevelCritical,
    LevelNormalized,
    LevelOverflow
}
