using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Layers.Tactical.Models;

namespace WMS.BufferManagement.Layers.Tactical;

/// <summary>
/// Менеджер волн заказов (Bartholdi & Hackman Wave Planning)
/// </summary>
public class WaveManager
{
    private readonly WaveConfig _config;
    private readonly List<Wave> _waves = new();
    private int _waveSequence = 0;

    public IReadOnlyList<Wave> ActiveWaves => _waves.Where(w => w.Status == WaveStatus.InProgress).ToList();
    public IReadOnlyList<Wave> PendingWaves => _waves.Where(w => w.Status == WaveStatus.Pending).ToList();

    public WaveManager(WaveConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Создать волну из заказов
    /// </summary>
    public Wave CreateWave(IEnumerable<Order> orders, IEnumerable<Pallet> availablePallets)
    {
        var wave = new Wave
        {
            SequenceNumber = _waveSequence++,
            StartTime = DateTime.UtcNow,
            Deadline = DateTime.UtcNow.AddMinutes(_config.DurationMinutes)
        };

        wave.Orders.AddRange(orders);

        // Создаём потоки заданий для волны
        // Каждый заказ → отдельный поток (последовательное выполнение)
        var streamSequence = 0;
        foreach (var order in orders)
        {
            var stream = new TaskStream
            {
                Name = $"Wave{wave.SequenceNumber}-Order{order.Id}",
                SequenceNumber = streamSequence++
            };

            // Находим палеты для заказа и сортируем по весу
            var pallets = GetPalletsForOrder(order, availablePallets);
            var tasks = pallets
                .OrderByDescending(p => p.Product.WeightKg) // Heavy first!
                .Select(p => new DeliveryTask { Pallet = p });

            stream.AddTasks(tasks);
            wave.Streams.Add(stream);
        }

        _waves.Add(wave);
        return wave;
    }

    /// <summary>
    /// Получить следующую волну для выполнения
    /// </summary>
    public Wave? GetNextWave()
    {
        return _waves
            .Where(w => w.Status == WaveStatus.Pending)
            .OrderBy(w => w.SequenceNumber)
            .FirstOrDefault();
    }

    /// <summary>
    /// Начать выполнение волны
    /// </summary>
    public void StartWave(Wave wave)
    {
        wave.Status = WaveStatus.InProgress;
        wave.StartTime = DateTime.UtcNow;

        foreach (var stream in wave.Streams)
        {
            stream.Status = StreamStatus.Pending;
        }
    }

    /// <summary>
    /// Проверить и обновить статус волн
    /// </summary>
    public void UpdateWaveStatuses()
    {
        var now = DateTime.UtcNow;

        foreach (var wave in _waves.Where(w => w.Status == WaveStatus.InProgress))
        {
            // Проверяем, все ли потоки завершены
            if (wave.Streams.All(s => s.IsCompleted))
            {
                wave.Status = WaveStatus.Completed;
            }
            // Проверяем, не истёк ли дедлайн
            else if (now > wave.Deadline)
            {
                wave.Status = WaveStatus.Overdue;
            }
        }
    }

    /// <summary>
    /// Рассчитать lead time для волны (когда начать подачу)
    /// </summary>
    public TimeSpan CalculateLeadTime(Wave wave, IEnumerable<Forklift> forklifts)
    {
        var avgForkliftSpeed = forklifts.Average(f => f.SpeedMetersPerSecond);
        var maxDistance = wave.Streams
            .SelectMany(s => s.AllTasks)
            .Max(t => t.Pallet.StorageDistanceMeters);

        // Время на доставку самой дальней палеты + запас
        var deliveryTime = maxDistance / avgForkliftSpeed * 2; // туда и обратно
        var safetyMargin = _config.SafetyMarginSeconds;

        return TimeSpan.FromSeconds(deliveryTime + safetyMargin);
    }

    private IEnumerable<Pallet> GetPalletsForOrder(Order order, IEnumerable<Pallet> availablePallets)
    {
        var result = new List<Pallet>();
        var palletDict = availablePallets
            .GroupBy(p => p.Product.SKU)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var line in order.Lines)
        {
            if (palletDict.TryGetValue(line.Product.SKU, out var pallets))
            {
                // Берём нужное количество палет
                var needed = Math.Min(line.Quantity, pallets.Count);
                result.AddRange(pallets.Take(needed));
                pallets.RemoveRange(0, needed);
            }
        }

        return result;
    }
}
