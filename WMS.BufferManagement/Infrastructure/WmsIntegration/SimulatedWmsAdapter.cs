using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

/// <summary>
/// Симулированный адаптер WMS для тестирования
/// </summary>
public class SimulatedWmsAdapter : IWmsAdapter, IDisposable
{
    private readonly ILogger<SimulatedWmsAdapter> _logger;
    private readonly Subject<WmsEvent> _events = new();
    private readonly Random _random;

    private readonly ConcurrentDictionary<string, WmsPalletInfo> _pallets = new();
    private readonly ConcurrentDictionary<string, WmsPickerInfo> _pickers = new();
    private readonly ConcurrentDictionary<string, WmsForkliftInfo> _forklifts = new();
    private readonly ConcurrentDictionary<string, WmsOrder> _orders = new();
    private readonly ConcurrentDictionary<string, WmsWave> _waves = new();
    private readonly ConcurrentDictionary<string, WmsDeliveryTaskInfo> _tasks = new();
    private readonly ConcurrentDictionary<string, string> _reservations = new();

    private int _orderCounter = 1;
    private int _palletCounter = 1;
    private int _taskCounter = 1;
    private int _waveCounter = 1;

    public SimulatedWmsAdapter(ILogger<SimulatedWmsAdapter> logger, int? seed = null)
    {
        _logger = logger;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();

        InitializeSimulatedData();
    }

    public IObservable<WmsEvent> Events => _events;

    private void InitializeSimulatedData()
    {
        // Создаём 3 карщика
        for (int i = 1; i <= 3; i++)
        {
            var forklift = new WmsForkliftInfo(
                $"FL-{i:D3}",
                $"Оператор {i}",
                WmsForkliftStatus.Idle,
                new WmsPosition(50 + i * 10, 100),
                null,
                50 + i * 20);
            _forklifts[forklift.Id] = forklift;
        }

        // Создаём 20 сборщиков
        for (int i = 1; i <= 20; i++)
        {
            var picker = new WmsPickerInfo(
                $"PK-{i:D3}",
                $"Сборщик {i}",
                $"Zone-{(i % 4) + 1}",
                i <= 15 ? WmsPickerStatus.Active : WmsPickerStatus.Idle,
                DateTime.Today.AddHours(8),
                DateTime.Today.AddHours(20));
            _pickers[picker.Id] = picker;
        }

        // Создаём 100 палет в хранении
        var productTypes = new[] { "Electronics", "Clothing", "Food", "Beverages", "Household", "Cosmetics", "Toys", "Other" };
        for (int i = 1; i <= 100; i++)
        {
            var productType = productTypes[_random.Next(productTypes.Length)];
            var weight = _random.Next(100, 1200);
            var pallet = new WmsPalletInfo(
                $"PAL-{i:D5}",
                $"PROD-{_random.Next(1000, 9999)}",
                $"{productType} Product {i}",
                _random.Next(10, 100),
                weight,
                weight > 800 ? WeightCategory.Heavy : weight > 400 ? WeightCategory.Medium : WeightCategory.Light,
                "Storage",
                $"A-{(i / 10) + 1:D2}-{(i % 10) + 1:D2}",
                new WmsPosition(10 + (i % 20) * 5, 10 + (i / 20) * 5),
                DateTime.UtcNow.AddDays(-_random.Next(1, 30)),
                WmsPalletStatus.Available);
            _pallets[pallet.Id] = pallet;
        }

        // Создаём несколько заказов
        for (int i = 0; i < 10; i++)
        {
            CreateSimulatedOrder();
        }

        _logger.LogInformation("Симуляция инициализирована: {Forklifts} карщиков, {Pickers} сборщиков, {Pallets} палет",
            _forklifts.Count, _pickers.Count, _pallets.Count);
    }

    private WmsOrder CreateSimulatedOrder()
    {
        var orderId = $"ORD-{_orderCounter++:D6}";
        var lineCount = _random.Next(1, 5);
        var lines = new List<WmsOrderLine>();

        for (int j = 0; j < lineCount; j++)
        {
            lines.Add(new WmsOrderLine(
                $"PROD-{_random.Next(1000, 9999)}",
                $"Product {j + 1}",
                _random.Next(1, 10),
                null));
        }

        var order = new WmsOrder(
            orderId,
            $"CUST-{_random.Next(100, 999)}",
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(_random.Next(2, 8)),
            (OrderPriority)_random.Next(0, 4),
            lines);

        _orders[orderId] = order;
        _events.OnNext(new WmsOrderCreatedEvent(DateTime.UtcNow, order));

        return order;
    }

    // === ЗАКАЗЫ И ВОЛНЫ ===

    public Task<IReadOnlyList<WmsOrder>> GetActiveOrdersAsync(
        DateTime? fromTime = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var orders = _orders.Values
            .Where(o => !fromTime.HasValue || o.CreatedAt >= fromTime.Value)
            .Take(limit ?? 100)
            .ToList();

        return Task.FromResult<IReadOnlyList<WmsOrder>>(orders);
    }

    public Task<WmsOrderDetails?> GetOrderDetailsAsync(
        string orderId,
        CancellationToken cancellationToken = default)
    {
        if (!_orders.TryGetValue(orderId, out var order))
            return Task.FromResult<WmsOrderDetails?>(null);

        var requirements = order.Lines.Select(l => new WmsPalletRequirement(
            l.ProductId,
            l.Quantity,
            (WeightCategory)_random.Next(0, 3),
            _pallets.Values
                .Where(p => p.Status == WmsPalletStatus.Available)
                .Take(3)
                .Select(p => p.Id)
                .ToList()
        )).ToList();

        var details = new WmsOrderDetails(
            order,
            requirements,
            new WmsEstimatedTimes(
                TimeSpan.FromMinutes(_random.Next(5, 30)),
                TimeSpan.FromMinutes(_random.Next(2, 10)),
                DateTime.UtcNow.AddMinutes(_random.Next(10, 40))));

        return Task.FromResult<WmsOrderDetails?>(details);
    }

    public Task<WmsWave?> GetNextWaveAsync(CancellationToken cancellationToken = default)
    {
        var pendingWave = _waves.Values.FirstOrDefault(w => w.Status == WaveStatus.Pending);
        if (pendingWave != null)
            return Task.FromResult<WmsWave?>(pendingWave);

        // Создаём новую волну из ожидающих заказов
        var pendingOrders = _orders.Values.Take(5).ToList();
        if (pendingOrders.Count == 0)
            return Task.FromResult<WmsWave?>(null);

        var wave = new WmsWave(
            $"WAVE-{_waveCounter++:D4}",
            DateTime.UtcNow,
            null,
            null,
            WaveStatus.Pending,
            pendingOrders.Select(o => o.Id).ToList(),
            pendingOrders.Sum(o => o.Lines.Count));

        _waves[wave.Id] = wave;
        return Task.FromResult<WmsWave?>(wave);
    }

    public Task ConfirmWaveStartAsync(string waveId, CancellationToken cancellationToken = default)
    {
        if (_waves.TryGetValue(waveId, out var wave))
        {
            _waves[waveId] = wave with { Status = WaveStatus.InProgress, StartedAt = DateTime.UtcNow };
            _logger.LogInformation("Волна {WaveId} запущена", waveId);
        }
        return Task.CompletedTask;
    }

    public Task CompleteWaveAsync(string waveId, WmsWaveResult result, CancellationToken cancellationToken = default)
    {
        if (_waves.TryGetValue(waveId, out var wave))
        {
            _waves[waveId] = wave with
            {
                Status = result.Success ? WaveStatus.Completed : WaveStatus.Failed,
                CompletedAt = DateTime.UtcNow
            };
            _logger.LogInformation("Волна {WaveId} завершена: {Success}", waveId, result.Success);
        }
        return Task.CompletedTask;
    }

    // === ПАЛЕТЫ ===

    public Task<IReadOnlyList<WmsPalletInfo>> GetStoragePalletsAsync(
        string? productType = null,
        string? zone = null,
        CancellationToken cancellationToken = default)
    {
        var pallets = _pallets.Values
            .Where(p => p.Status == WmsPalletStatus.Available)
            .Where(p => productType == null || p.ProductName.Contains(productType, StringComparison.OrdinalIgnoreCase))
            .Where(p => zone == null || p.CurrentZone == zone)
            .ToList();

        return Task.FromResult<IReadOnlyList<WmsPalletInfo>>(pallets);
    }

    public Task<WmsPalletInfo?> GetPalletInfoAsync(string palletId, CancellationToken cancellationToken = default)
    {
        _pallets.TryGetValue(palletId, out var pallet);
        return Task.FromResult(pallet);
    }

    public Task<WmsReservationResult> ReservePalletAsync(
        string palletId,
        string forkliftId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!_pallets.TryGetValue(palletId, out var pallet))
        {
            return Task.FromResult(new WmsReservationResult(false, null, null, "Палета не найдена"));
        }

        if (pallet.Status != WmsPalletStatus.Available)
        {
            return Task.FromResult(new WmsReservationResult(false, null, null, "Палета недоступна"));
        }

        var reservationId = Guid.NewGuid().ToString();
        _reservations[palletId] = reservationId;
        _pallets[palletId] = pallet with { Status = WmsPalletStatus.Reserved };

        return Task.FromResult(new WmsReservationResult(
            true,
            reservationId,
            DateTime.UtcNow.Add(timeout ?? TimeSpan.FromMinutes(5)),
            null));
    }

    public Task ReleasePalletReservationAsync(string palletId, CancellationToken cancellationToken = default)
    {
        if (_pallets.TryGetValue(palletId, out var pallet) && pallet.Status == WmsPalletStatus.Reserved)
        {
            _pallets[palletId] = pallet with { Status = WmsPalletStatus.Available };
            _reservations.TryRemove(palletId, out _);
        }
        return Task.CompletedTask;
    }

    public Task ConfirmPalletDeliveryAsync(
        string palletId,
        string bufferSlotId,
        DateTime deliveryTime,
        CancellationToken cancellationToken = default)
    {
        if (_pallets.TryGetValue(palletId, out var pallet))
        {
            _pallets[palletId] = pallet with
            {
                Status = WmsPalletStatus.InBuffer,
                CurrentZone = "Buffer",
                CurrentSlot = bufferSlotId,
                LastMovedAt = deliveryTime
            };
            _events.OnNext(new WmsPalletMovedEvent(DateTime.UtcNow, palletId, pallet.CurrentZone, "Buffer"));
            _logger.LogDebug("Палета {PalletId} доставлена в буфер слот {Slot}", palletId, bufferSlotId);
        }
        return Task.CompletedTask;
    }

    public Task ConfirmPalletConsumedAsync(
        string palletId,
        string pickerId,
        WmsConsumeDetails details,
        CancellationToken cancellationToken = default)
    {
        if (_pallets.TryGetValue(palletId, out var pallet))
        {
            if (details.QuantityRemaining <= 0)
            {
                _pallets[palletId] = pallet with { Status = WmsPalletStatus.Consumed };
            }
            _logger.LogDebug("Палета {PalletId} обработана сборщиком {PickerId}", palletId, pickerId);
        }
        return Task.CompletedTask;
    }

    // === ПЕРСОНАЛ ===

    public Task<IReadOnlyList<WmsPickerInfo>> GetActivePickersAsync(CancellationToken cancellationToken = default)
    {
        var pickers = _pickers.Values
            .Where(p => p.Status == WmsPickerStatus.Active || p.Status == WmsPickerStatus.Idle)
            .ToList();

        return Task.FromResult<IReadOnlyList<WmsPickerInfo>>(pickers);
    }

    public Task<WmsPickerStats?> GetPickerStatsAsync(
        string pickerId,
        DateTime fromTime,
        DateTime toTime,
        CancellationToken cancellationToken = default)
    {
        if (!_pickers.ContainsKey(pickerId))
            return Task.FromResult<WmsPickerStats?>(null);

        var stats = new WmsPickerStats(
            pickerId,
            _random.Next(10, 50),
            _random.Next(100, 500),
            _random.NextDouble() * 15 + 5,
            _random.NextDouble() * 40 + 60,
            TimeSpan.FromHours(_random.NextDouble() * 6 + 2),
            TimeSpan.FromMinutes(_random.Next(10, 60)));

        return Task.FromResult<WmsPickerStats?>(stats);
    }

    public Task<IReadOnlyList<WmsForkliftInfo>> GetForkliftsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<WmsForkliftInfo>>(_forklifts.Values.ToList());
    }

    public Task UpdateForkliftStatusAsync(
        string forkliftId,
        WmsForkliftStatus status,
        WmsPosition? position = null,
        CancellationToken cancellationToken = default)
    {
        if (_forklifts.TryGetValue(forkliftId, out var forklift))
        {
            _forklifts[forkliftId] = forklift with
            {
                Status = status,
                CurrentPosition = position ?? forklift.CurrentPosition
            };
        }
        return Task.CompletedTask;
    }

    // === ЗАДАНИЯ ===

    public Task<string> CreateDeliveryTaskAsync(
        WmsDeliveryTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var taskId = $"TASK-{_taskCounter++:D6}";
        var task = new WmsDeliveryTaskInfo(
            taskId,
            request.PalletId,
            request.AssignedForkliftId,
            WmsDeliveryTaskStatus.Pending,
            DateTime.UtcNow,
            null,
            null,
            null);

        _tasks[taskId] = task;
        _logger.LogDebug("Создано задание {TaskId} для палеты {PalletId}", taskId, request.PalletId);

        return Task.FromResult(taskId);
    }

    public Task UpdateTaskStatusAsync(
        string taskId,
        WmsDeliveryTaskStatus status,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            _tasks[taskId] = task with
            {
                Status = status,
                StartedAt = status == WmsDeliveryTaskStatus.InProgress ? DateTime.UtcNow : task.StartedAt,
                CompletedAt = status == WmsDeliveryTaskStatus.Completed ? DateTime.UtcNow : task.CompletedAt
            };

            if (status == WmsDeliveryTaskStatus.Completed || status == WmsDeliveryTaskStatus.Failed)
            {
                _events.OnNext(new WmsTaskCompletedEvent(
                    DateTime.UtcNow,
                    taskId,
                    status == WmsDeliveryTaskStatus.Completed,
                    reason));
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WmsDeliveryTaskInfo>> GetActiveTasksAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _tasks.Values
            .Where(t => t.Status == WmsDeliveryTaskStatus.Pending ||
                       t.Status == WmsDeliveryTaskStatus.Assigned ||
                       t.Status == WmsDeliveryTaskStatus.InProgress)
            .ToList();

        return Task.FromResult<IReadOnlyList<WmsDeliveryTaskInfo>>(tasks);
    }

    public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _events.OnCompleted();
        _events.Dispose();
    }
}
