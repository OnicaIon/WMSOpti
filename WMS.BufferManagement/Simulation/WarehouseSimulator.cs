using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Domain.Events;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Infrastructure.EventBus;

namespace WMS.BufferManagement.Simulation;

/// <summary>
/// Симулятор склада для тестирования
/// </summary>
public class WarehouseSimulator
{
    private readonly SimulationConfig _config;
    private readonly IEventBus _eventBus;
    private readonly Random _random;

    public BufferZone Buffer { get; }
    public StorageZone Storage { get; }
    public List<Forklift> Forklifts { get; }
    public List<Picker> Pickers { get; }
    public List<TaskStream> ActiveStreams { get; } = new();

    private DateTime _simulationTime = DateTime.UtcNow;
    private bool _running;

    public DateTime SimulationTime => _simulationTime;
    public bool IsRunning => _running;

    public WarehouseSimulator(
        SimulationConfig config,
        WorkersConfig workersConfig,
        BufferConfig bufferConfig,
        IEventBus eventBus)
    {
        _config = config;
        _eventBus = eventBus;
        _random = new Random(config.RandomSeed);

        Buffer = new BufferZone { Capacity = bufferConfig.Capacity };
        Storage = new StorageZone();
        Forklifts = CreateForklifts(workersConfig.ForkliftsCount);
        Pickers = CreatePickers(workersConfig.PickersCount);

        // Инициализируем склад палетами
        InitializeStorage(200);
        // Начальное заполнение буфера
        InitializeBuffer(bufferConfig.Capacity / 2);
    }

    private List<Forklift> CreateForklifts(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new Forklift
            {
                Name = $"Forklift-{i}",
                SpeedMetersPerSecond = 1.5 + _random.NextDouble() * 1.0, // 1.5-2.5 м/с
                LoadUnloadTimeSeconds = 25 + _random.NextDouble() * 10  // 25-35 сек
            })
            .ToList();
    }

    private List<Picker> CreatePickers(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new Picker
            {
                Name = $"Picker-{i}",
                AveragePickRatePerHour = 80 + _random.NextDouble() * 40, // 80-120 ед/час
                PalletConsumptionRatePerHour = 4 + _random.NextDouble() * 4 // 4-8 пал/час
            })
            .ToList();
    }

    private void InitializeStorage(int palletCount)
    {
        var products = CreateProducts();

        for (int i = 0; i < palletCount; i++)
        {
            var product = products[_random.Next(products.Count)];
            var pallet = new Pallet
            {
                Product = product,
                Quantity = 20 + _random.Next(80),
                StorageDistanceMeters = 10 + _random.NextDouble() * 190 // 10-200 метров
            };
            Storage.Add(pallet);
        }
    }

    private void InitializeBuffer(int count)
    {
        // Перемещаем ближайшие палеты в буфер
        var pallets = Storage.GetNearestPallets(count).ToList();
        foreach (var pallet in pallets)
        {
            Storage.TryTake(pallet);
            Buffer.TryAdd(pallet);
        }
    }

    private List<Product> CreateProducts()
    {
        return new List<Product>
        {
            new("SKU001", "Heavy Equipment", 25.0),
            new("SKU002", "Medium Boxes", 12.0),
            new("SKU003", "Light Items", 3.0),
            new("SKU004", "Beverages", 18.0),
            new("SKU005", "Electronics", 5.0),
            new("SKU006", "Furniture Parts", 22.0),
            new("SKU007", "Clothing", 2.0),
            new("SKU008", "Food Cans", 15.0),
        };
    }

    /// <summary>
    /// Симуляция одного тика
    /// </summary>
    public void Tick(TimeSpan deltaTime)
    {
        var adjustedDelta = TimeSpan.FromTicks((long)(deltaTime.Ticks * _config.SpeedMultiplier));
        _simulationTime += adjustedDelta;

        // Симулируем работу карщиков
        SimulateForklifts(adjustedDelta);

        // Симулируем работу сборщиков
        SimulatePickers(adjustedDelta);
    }

    private void SimulateForklifts(TimeSpan delta)
    {
        foreach (var forklift in Forklifts)
        {
            if (forklift.CurrentTask == null) continue;

            var task = forklift.CurrentTask;
            var pallet = task.Pallet;

            switch (forklift.State)
            {
                case ForkliftState.MovingToPallet:
                    // Расчёт прогресса движения к палете
                    var distanceToPallet = Math.Abs(pallet.StorageDistanceMeters - forklift.CurrentPositionMeters);
                    var moveToPallet = forklift.SpeedMetersPerSecond * delta.TotalSeconds;

                    if (moveToPallet >= distanceToPallet)
                    {
                        forklift.CurrentPositionMeters = pallet.StorageDistanceMeters;
                        forklift.State = ForkliftState.Loading;
                    }
                    else
                    {
                        forklift.CurrentPositionMeters += moveToPallet * Math.Sign(pallet.StorageDistanceMeters - forklift.CurrentPositionMeters);
                    }
                    break;

                case ForkliftState.Loading:
                    // Упрощённо: мгновенная погрузка
                    forklift.State = ForkliftState.MovingToBuffer;
                    pallet.Location = PalletLocation.InTransit;
                    break;

                case ForkliftState.MovingToBuffer:
                    var moveToBuffer = forklift.SpeedMetersPerSecond * delta.TotalSeconds;

                    if (forklift.CurrentPositionMeters <= moveToBuffer)
                    {
                        forklift.CurrentPositionMeters = 0;
                        forklift.State = ForkliftState.Unloading;
                    }
                    else
                    {
                        forklift.CurrentPositionMeters -= moveToBuffer;
                    }
                    break;

                case ForkliftState.Unloading:
                    // Палета доставлена в буфер
                    if (Buffer.TryAdd(pallet))
                    {
                        task.Status = DeliveryTaskStatus.Completed;
                        task.CompletedAt = _simulationTime;
                        forklift.CurrentTask = null;
                        forklift.State = ForkliftState.Idle;
                        forklift.CompletedTasksCount++;

                        _eventBus.Publish(new PalletDeliveredEvent
                        {
                            Pallet = pallet,
                            Forklift = forklift,
                            DeliveryTime = task.ActualDuration ?? TimeSpan.Zero,
                            DistanceMeters = pallet.StorageDistanceMeters
                        });

                        _eventBus.Publish(new BufferLevelChangedEvent
                        {
                            CurrentCount = Buffer.CurrentCount,
                            PreviousCount = Buffer.CurrentCount - 1,
                            Capacity = Buffer.Capacity
                        });
                    }
                    break;
            }

            forklift.TotalWorkTimeSeconds += delta.TotalSeconds;
        }
    }

    private void SimulatePickers(TimeSpan delta)
    {
        foreach (var picker in Pickers.Where(p => p.State == PickerState.Picking))
        {
            // Вероятность завершения обработки палеты
            var processChance = (picker.PalletConsumptionRatePerHour / 3600.0) * delta.TotalSeconds;

            if (_random.NextDouble() < processChance && !Buffer.IsEmpty)
            {
                var pallet = Buffer.TakeHeaviest();
                if (pallet != null)
                {
                    picker.ProcessedPalletsCount++;

                    _eventBus.Publish(new PalletConsumedEvent
                    {
                        Pallet = pallet,
                        Picker = picker,
                        ProcessingTime = TimeSpan.FromMinutes(_random.Next(5, 15))
                    });

                    _eventBus.Publish(new BufferLevelChangedEvent
                    {
                        CurrentCount = Buffer.CurrentCount,
                        PreviousCount = Buffer.CurrentCount + 1,
                        Capacity = Buffer.Capacity
                    });
                }
            }

            picker.TotalWorkTimeSeconds += delta.TotalSeconds;
        }
    }

    /// <summary>
    /// Активировать сборщиков
    /// </summary>
    public void ActivatePickers(int count)
    {
        var idlePickers = Pickers.Where(p => p.State == PickerState.Idle).Take(count);
        foreach (var picker in idlePickers)
        {
            picker.State = PickerState.Picking;
        }
    }

    /// <summary>
    /// Создать поток заданий для тестирования
    /// </summary>
    public TaskStream CreateTestStream(int palletCount)
    {
        var stream = new TaskStream
        {
            Name = $"TestStream-{_simulationTime:HHmmss}",
            SequenceNumber = ActiveStreams.Count
        };

        var pallets = Storage.Pallets.Take(palletCount).ToList();
        foreach (var pallet in pallets)
        {
            Storage.TryTake(pallet);
            stream.AddTask(new DeliveryTask { Pallet = pallet });
        }

        ActiveStreams.Add(stream);
        return stream;
    }

    /// <summary>
    /// Получить статистику
    /// </summary>
    public SimulationStats GetStats()
    {
        return new SimulationStats
        {
            BufferLevel = Buffer.FillPercentage,
            BufferCount = Buffer.CurrentCount,
            StorageCount = Storage.CurrentCount,
            ActiveForklifts = Forklifts.Count(f => f.State != ForkliftState.Idle && f.State != ForkliftState.Offline),
            ActivePickers = Pickers.Count(p => p.State == PickerState.Picking),
            TotalDelivered = Forklifts.Sum(f => f.CompletedTasksCount),
            TotalConsumed = Pickers.Sum(p => p.ProcessedPalletsCount),
            SimulationTime = _simulationTime
        };
    }
}

public record SimulationStats
{
    public double BufferLevel { get; init; }
    public int BufferCount { get; init; }
    public int StorageCount { get; init; }
    public int ActiveForklifts { get; init; }
    public int ActivePickers { get; init; }
    public int TotalDelivered { get; init; }
    public int TotalConsumed { get; init; }
    public DateTime SimulationTime { get; init; }
}
