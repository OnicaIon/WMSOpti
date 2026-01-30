namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Зона хранения - основной склад палет
/// </summary>
public class StorageZone
{
    /// <summary>
    /// Палеты в зоне хранения
    /// </summary>
    private readonly List<Pallet> _pallets = new();

    /// <summary>
    /// Палеты в зоне хранения (только чтение)
    /// </summary>
    public IReadOnlyList<Pallet> Pallets => _pallets.AsReadOnly();

    /// <summary>
    /// Текущее количество палет
    /// </summary>
    public int CurrentCount => _pallets.Count;

    /// <summary>
    /// Максимальное расстояние до буфера (метры)
    /// </summary>
    public double MaxDistanceMeters { get; init; } = 200;

    /// <summary>
    /// Минимальное расстояние до буфера (метры)
    /// </summary>
    public double MinDistanceMeters { get; init; } = 10;

    /// <summary>
    /// Добавить палету в зону хранения
    /// </summary>
    public void Add(Pallet pallet)
    {
        pallet.Location = PalletLocation.Storage;
        _pallets.Add(pallet);
    }

    /// <summary>
    /// Добавить несколько палет
    /// </summary>
    public void AddRange(IEnumerable<Pallet> pallets)
    {
        foreach (var pallet in pallets)
        {
            Add(pallet);
        }
    }

    /// <summary>
    /// Забрать палету для перемещения
    /// </summary>
    public bool TryTake(Pallet pallet)
    {
        if (!_pallets.Contains(pallet)) return false;

        _pallets.Remove(pallet);
        pallet.Location = PalletLocation.InTransit;
        return true;
    }

    /// <summary>
    /// Забрать палету по ID
    /// </summary>
    public Pallet? TakeById(string palletId)
    {
        var pallet = _pallets.FirstOrDefault(p => p.Id == palletId);
        if (pallet != null)
        {
            _pallets.Remove(pallet);
            pallet.Location = PalletLocation.InTransit;
        }
        return pallet;
    }

    /// <summary>
    /// Найти палеты с конкретным товаром
    /// </summary>
    public IEnumerable<Pallet> FindByProduct(string productSku)
    {
        return _pallets.Where(p => p.Product.SKU == productSku);
    }

    /// <summary>
    /// Найти ближайшие палеты (для оптимизации)
    /// </summary>
    public IEnumerable<Pallet> GetNearestPallets(int count)
    {
        return _pallets
            .OrderBy(p => p.StorageDistanceMeters)
            .Take(count);
    }

    /// <summary>
    /// Найти палеты для конкретного заказа, отсортированные по весу
    /// </summary>
    public IEnumerable<Pallet> FindForOrder(Order order)
    {
        var requiredSkus = order.Lines.Select(l => l.Product.SKU).ToHashSet();
        return _pallets
            .Where(p => requiredSkus.Contains(p.Product.SKU))
            .OrderByDescending(p => p.Product.WeightKg);
    }

    /// <summary>
    /// Получить статистику по расстояниям
    /// </summary>
    public (double avg, double min, double max) GetDistanceStats()
    {
        if (!_pallets.Any()) return (0, 0, 0);

        return (
            _pallets.Average(p => p.StorageDistanceMeters),
            _pallets.Min(p => p.StorageDistanceMeters),
            _pallets.Max(p => p.StorageDistanceMeters)
        );
    }
}
