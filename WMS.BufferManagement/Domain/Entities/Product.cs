namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Товар с характеристиками веса и приоритета
/// </summary>
public class Product
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string SKU { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Вес товара в кг (используется для сортировки heavy-on-bottom)
    /// </summary>
    public double WeightKg { get; init; }

    /// <summary>
    /// Категория веса для быстрой сортировки
    /// </summary>
    public WeightCategory Category => WeightKg switch
    {
        >= 20 => WeightCategory.Heavy,
        >= 5 => WeightCategory.Medium,
        _ => WeightCategory.Light
    };

    /// <summary>
    /// Приоритет обработки (выше = раньше). По умолчанию = вес
    /// </summary>
    public int Priority { get; init; }

    public Product()
    {
        Priority = (int)(WeightKg * 10); // Default priority based on weight
    }

    public Product(string sku, string name, double weightKg, int? priority = null)
    {
        SKU = sku;
        Name = name;
        WeightKg = weightKg;
        Priority = priority ?? (int)(weightKg * 10);
    }
}

public enum WeightCategory
{
    Light = 0,   // < 5 kg
    Medium = 1,  // 5-20 kg
    Heavy = 2    // > 20 kg
}
