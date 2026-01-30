namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Заказ для сборки
/// </summary>
public class Order
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Список позиций заказа (товар + количество)
    /// </summary>
    public List<OrderLine> Lines { get; init; } = new();

    /// <summary>
    /// Приоритет заказа
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>
    /// Время создания заказа
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Крайний срок выполнения
    /// </summary>
    public DateTime? Deadline { get; init; }

    /// <summary>
    /// Статус заказа
    /// </summary>
    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    /// <summary>
    /// Сборщик, назначенный на заказ
    /// </summary>
    public Picker? AssignedPicker { get; set; }

    /// <summary>
    /// Требуемые палеты (отсортированные по весу - тяжёлые первыми)
    /// </summary>
    public List<Pallet> GetRequiredPalletsSortedByWeight()
    {
        return Lines
            .SelectMany(l => l.RequiredPallets)
            .OrderByDescending(p => p.Product.WeightKg)
            .ToList();
    }
}

public class OrderLine
{
    public Product Product { get; init; } = null!;
    public int Quantity { get; init; }

    /// <summary>
    /// Палеты, необходимые для этой позиции
    /// </summary>
    public List<Pallet> RequiredPallets { get; init; } = new();
}

public enum OrderStatus
{
    Pending,     // Ожидает обработки
    InProgress,  // В процессе сборки
    Completed,   // Собран
    Cancelled    // Отменён
}
