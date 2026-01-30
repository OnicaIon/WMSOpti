using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Событие потребления палеты сборщиком
/// </summary>
public class PalletConsumedEvent : DomainEvent
{
    public Pallet Pallet { get; init; } = null!;
    public Picker Picker { get; init; } = null!;
    public string? OrderId { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}
