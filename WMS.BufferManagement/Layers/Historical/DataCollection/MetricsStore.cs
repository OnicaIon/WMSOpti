using System.Collections.Concurrent;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Domain.Interfaces;

namespace WMS.BufferManagement.Layers.Historical.DataCollection;

/// <summary>
/// In-memory хранилище метрик
/// </summary>
public class MetricsStore : IMetricsCollector
{
    private readonly ConcurrentQueue<BufferMetric> _bufferMetrics = new();
    private readonly ConcurrentQueue<DeliveryMetric> _deliveryMetrics = new();
    private readonly ConcurrentQueue<PickerMetric> _pickerMetrics = new();
    private readonly ConcurrentQueue<StreamMetric> _streamMetrics = new();

    private const int MaxMetricsCount = 10000;

    public void RecordBufferLevel(double level, DateTime timestamp)
    {
        _bufferMetrics.Enqueue(new BufferMetric(level, timestamp));
        TrimQueue(_bufferMetrics);
    }

    public void RecordDeliveryTime(Forklift forklift, Pallet pallet, TimeSpan duration)
    {
        _deliveryMetrics.Enqueue(new DeliveryMetric(
            forklift.Id,
            pallet.Id,
            pallet.Product.SKU,
            pallet.StorageDistanceMeters,
            duration,
            DateTime.UtcNow));
        TrimQueue(_deliveryMetrics);
    }

    public void RecordPickerSpeed(Picker picker, double palletsPerHour)
    {
        _pickerMetrics.Enqueue(new PickerMetric(
            picker.Id,
            palletsPerHour,
            DateTime.UtcNow));
        TrimQueue(_pickerMetrics);
    }

    public void RecordStreamCompletion(TaskStream stream, TimeSpan duration)
    {
        _streamMetrics.Enqueue(new StreamMetric(
            stream.Id,
            stream.AllTasks.Count,
            duration,
            DateTime.UtcNow));
        TrimQueue(_streamMetrics);
    }

    public void RecordForkliftUtilization(Forklift forklift, double utilization)
    {
        // Could add a separate queue for utilization metrics
    }

    public Task<double> GetAverageBufferLevelAsync(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var metrics = _bufferMetrics.Where(m => m.Timestamp >= cutoff).ToList();

        return Task.FromResult(metrics.Any() ? metrics.Average(m => m.Level) : 0.5);
    }

    public Task<TimeSpan> GetAverageDeliveryTimeAsync(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var metrics = _deliveryMetrics.Where(m => m.Timestamp >= cutoff).ToList();

        var avgSeconds = metrics.Any() ? metrics.Average(m => m.Duration.TotalSeconds) : 60;
        return Task.FromResult(TimeSpan.FromSeconds(avgSeconds));
    }

    public Task<double> GetConsumptionRateAsync(TimeSpan period)
    {
        var cutoff = DateTime.UtcNow - period;
        var metrics = _pickerMetrics.Where(m => m.Timestamp >= cutoff).ToList();

        return Task.FromResult(metrics.Any() ? metrics.Average(m => m.PalletsPerHour) : 100);
    }

    /// <summary>
    /// Получить данные для обучения ML модели
    /// </summary>
    public IEnumerable<PickerTrainingData> GetPickerTrainingData()
    {
        return _pickerMetrics.Select(m => new PickerTrainingData
        {
            PickerId = m.PickerId,
            Hour = m.Timestamp.Hour,
            DayOfWeek = (int)m.Timestamp.DayOfWeek,
            PalletsPerHour = (float)m.PalletsPerHour
        });
    }

    /// <summary>
    /// Получить данные для обучения модели прогноза доставки
    /// </summary>
    public IEnumerable<DeliveryTrainingData> GetDeliveryTrainingData()
    {
        return _deliveryMetrics.Select(m => new DeliveryTrainingData
        {
            Distance = (float)m.Distance,
            Hour = m.Timestamp.Hour,
            DeliverySeconds = (float)m.Duration.TotalSeconds
        });
    }

    private void TrimQueue<T>(ConcurrentQueue<T> queue)
    {
        while (queue.Count > MaxMetricsCount)
        {
            queue.TryDequeue(out _);
        }
    }
}

// Метрики
public record BufferMetric(double Level, DateTime Timestamp);
public record DeliveryMetric(string ForkliftId, string PalletId, string ProductSku, double Distance, TimeSpan Duration, DateTime Timestamp);
public record PickerMetric(string PickerId, double PalletsPerHour, DateTime Timestamp);
public record StreamMetric(string StreamId, int TaskCount, TimeSpan Duration, DateTime Timestamp);

// Данные для ML
public class PickerTrainingData
{
    public string PickerId { get; set; } = string.Empty;
    public int Hour { get; set; }
    public int DayOfWeek { get; set; }
    public float PalletsPerHour { get; set; }
}

public class DeliveryTrainingData
{
    public float Distance { get; set; }
    public int Hour { get; set; }
    public float DeliverySeconds { get; set; }
}
