using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Layers.Tactical.Models;

/// <summary>
/// Назначение задания карщику
/// </summary>
public class Assignment
{
    public DeliveryTask Task { get; init; } = null!;
    public Forklift Forklift { get; init; } = null!;
    public TimeSpan EstimatedTime { get; init; }
    public double Cost { get; init; }
    public int SequenceInStream { get; init; }
}
