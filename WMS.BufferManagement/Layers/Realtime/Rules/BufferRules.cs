using NRules.Fluent.Dsl;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Layers.Realtime.StateMachine;

namespace WMS.BufferManagement.Layers.Realtime.Rules;

/// <summary>
/// Факты для правил буфера
/// </summary>
public class BufferFact
{
    public double FillLevel { get; set; }
    public BufferState State { get; set; }
    public int PendingTasks { get; set; }
    public int IdleForklifts { get; set; }
    public double ConsumptionRate { get; set; }
}

public class ForkliftFact
{
    public Forklift Forklift { get; set; } = null!;
    public bool IsIdle => Forklift.State == ForkliftState.Idle;
    public double Utilization => Forklift.Utilization;
}

/// <summary>
/// Действие, рекомендуемое правилом
/// </summary>
public class RecommendedAction
{
    public ActionType Type { get; set; }
    public int Priority { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int? PalletsToRequest { get; set; }
    public int? ForkliftsToActivate { get; set; }
}

public enum ActionType
{
    None,
    RequestPallets,
    ActivateForklifts,
    DeactivateForklifts,
    UrgentDelivery
}

/// <summary>
/// Правило: Критический уровень буфера → Срочная подача
/// </summary>
public class CriticalBufferRule : Rule
{
    public override void Define()
    {
        BufferFact buffer = null!;

        When()
            .Match(() => buffer, b => b.State == BufferState.Critical);

        Then()
            .Do(ctx => ctx.Insert(new RecommendedAction
            {
                Type = ActionType.UrgentDelivery,
                Priority = 100,
                Reason = "Критический уровень буфера",
                PalletsToRequest = 10,
                ForkliftsToActivate = 3
            }));
    }
}

/// <summary>
/// Правило: Низкий уровень буфера + есть свободные карщики → Запросить палеты
/// </summary>
public class LowBufferWithIdleForkliftsRule : Rule
{
    public override void Define()
    {
        BufferFact buffer = null!;

        When()
            .Match(() => buffer, b => b.State == BufferState.Low && b.IdleForklifts > 0);

        Then()
            .Do(ctx => ctx.Insert(new RecommendedAction
            {
                Type = ActionType.RequestPallets,
                Priority = 75,
                Reason = "Низкий уровень буфера, есть свободные карщики",
                PalletsToRequest = Math.Max(3, buffer.IdleForklifts * 2),
                ForkliftsToActivate = buffer.IdleForklifts
            }));
    }
}

/// <summary>
/// Правило: Переполнение буфера → Снизить активность
/// </summary>
public class OverflowBufferRule : Rule
{
    public override void Define()
    {
        BufferFact buffer = null!;

        When()
            .Match(() => buffer, b => b.State == BufferState.Overflow);

        Then()
            .Do(ctx => ctx.Insert(new RecommendedAction
            {
                Type = ActionType.DeactivateForklifts,
                Priority = 50,
                Reason = "Переполнение буфера, снижаем активность",
                ForkliftsToActivate = 1
            }));
    }
}

/// <summary>
/// Правило: Высокое потребление при нормальном уровне → Упреждающая подача
/// </summary>
public class HighConsumptionRule : Rule
{
    public override void Define()
    {
        BufferFact buffer = null!;

        When()
            .Match(() => buffer, b =>
                b.State == BufferState.Normal &&
                b.ConsumptionRate > 150 && // палет/час
                b.FillLevel < 0.5);

        Then()
            .Do(ctx => ctx.Insert(new RecommendedAction
            {
                Type = ActionType.RequestPallets,
                Priority = 60,
                Reason = "Высокое потребление, упреждающая подача",
                PalletsToRequest = 5
            }));
    }
}
