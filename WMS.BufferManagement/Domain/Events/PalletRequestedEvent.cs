using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Events;

/// <summary>
/// Событие запроса палеты (нужна подача в буфер)
/// </summary>
public class PalletRequestedEvent : DomainEvent
{
    public Pallet Pallet { get; init; } = null!;
    public string? OrderId { get; init; }
    public int Priority { get; init; }
    public string? StreamId { get; init; }
}
