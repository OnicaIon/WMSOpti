namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Моно-палета с одним типом товара
/// </summary>
public class Pallet
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Товар на палете
    /// </summary>
    public Product Product { get; init; } = null!;

    /// <summary>
    /// Количество единиц товара на палете
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Текущее местоположение палеты
    /// </summary>
    public PalletLocation Location { get; set; } = PalletLocation.Storage;

    /// <summary>
    /// Координата в зоне хранения (расстояние от буфера в метрах)
    /// </summary>
    public double StorageDistanceMeters { get; init; }

    /// <summary>
    /// Время создания/поступления палеты
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Общий вес палеты (товар * количество)
    /// </summary>
    public double TotalWeightKg => Product.WeightKg * Quantity;

    /// <summary>
    /// Приоритет палеты (наследуется от товара)
    /// </summary>
    public int Priority => Product.Priority;
}

public enum PalletLocation
{
    Storage,      // В зоне хранения
    InTransit,    // Перемещается карщиком
    Buffer,       // В зоне буфера
    Picking,      // Забрана сборщиком
    Completed     // Обработана
}
