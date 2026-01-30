using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Событие доставки палеты в буфер
/// </summary>
public class PalletDeliveredEvent : DomainEvent
{
    public Pallet Pallet { get; init; } = null!;
    public Forklift Forklift { get; init; } = null!;
    public TimeSpan DeliveryTime { get; init; }
    public double DistanceMeters { get; init; }
    public string? TaskId { get; init; }
    public string? StreamId { get; init; }
}
