namespace WMS.BufferManagement.Domain.Entities;

/// <summary>
/// Зона буфера - промежуточное хранение палет перед сборкой
/// </summary>
public class BufferZone
{
    /// <summary>
    /// Максимальная вместимость буфера (количество палет)
    /// </summary>
    public int Capacity { get; init; } = 50;

    /// <summary>
    /// Палеты в буфере
    /// </summary>
    private readonly List<Pallet> _pallets = new();

    /// <summary>
    /// Палеты в буфере (только чтение)
    /// </summary>
    public IReadOnlyList<Pallet> Pallets => _pallets.AsReadOnly();

    /// <summary>
    /// Текущее количество палет
    /// </summary>
    public int CurrentCount => _pallets.Count;

    /// <summary>
    /// Уровень заполнения (0-1)
    /// </summary>
    public double FillLevel => (double)CurrentCount / Capacity;

    /// <summary>
    /// Уровень заполнения в процентах
    /// </summary>
    public double FillPercentage => FillLevel * 100;

    /// <summary>
    /// Свободные места
    /// </summary>
    public int FreeSlots => Capacity - CurrentCount;

    /// <summary>
    /// Буфер пуст
    /// </summary>
    public bool IsEmpty => CurrentCount == 0;

    /// <summary>
    /// Буфер полон
    /// </summary>
    public bool IsFull => CurrentCount >= Capacity;

    /// <summary>
    /// Добавить палету в буфер
    /// </summary>
    public bool TryAdd(Pallet pallet)
    {
        if (IsFull) return false;

        pallet.Location = PalletLocation.Buffer;
        _pallets.Add(pallet);
        return true;
    }

    /// <summary>
    /// Забрать палету из буфера (по весу - тяжёлые первыми для заказа)
    /// </summary>
    public Pallet? TakeHeaviest()
    {
        var pallet = _pallets.OrderByDescending(p => p.Product.WeightKg).FirstOrDefault();
        if (pallet != null)
        {
            _pallets.Remove(pallet);
            pallet.Location = PalletLocation.Picking;
        }
        return pallet;
    }

    /// <summary>
    /// Забрать конкретную палету
    /// </summary>
    public bool TryTake(Pallet pallet)
    {
        if (!_pallets.Contains(pallet)) return false;

        _pallets.Remove(pallet);
        pallet.Location = PalletLocation.Picking;
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
            pallet.Location = PalletLocation.Picking;
        }
        return pallet;
    }

    /// <summary>
    /// Получить палеты, отсортированные по весу (для сборки заказов)
    /// </summary>
    public IEnumerable<Pallet> GetPalletsByWeight(bool heavyFirst = true)
    {
        return heavyFirst
            ? _pallets.OrderByDescending(p => p.Product.WeightKg)
            : _pallets.OrderBy(p => p.Product.WeightKg);
    }

    /// <summary>
    /// Найти палеты с конкретным товаром
    /// </summary>
    public IEnumerable<Pallet> FindByProduct(string productSku)
    {
        return _pallets.Where(p => p.Product.SKU == productSku);
    }
}
