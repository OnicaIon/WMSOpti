using Microsoft.Extensions.Configuration;
using WMS.BufferManagement.Console;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Domain.Events;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Infrastructure.EventBus;
using WMS.BufferManagement.Layers.Historical.DataCollection;
using WMS.BufferManagement.Layers.Historical.Prediction;
using WMS.BufferManagement.Layers.Realtime.BufferControl;
using WMS.BufferManagement.Layers.Realtime.Dispatcher;
using WMS.BufferManagement.Layers.Tactical;
using WMS.BufferManagement.Simulation;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Console.WriteLine("WMS Buffer Management System - Starting...");

// Загружаем конфигурацию
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var config = new SystemConfig();
configuration.Bind(config);

// Создаём сервисы
var eventBus = new InMemoryEventBus();
var metricsStore = new MetricsStore();
var simulator = new WarehouseSimulator(config.Simulation, config.Workers, config.Buffer, eventBus);
var bufferController = new HysteresisController(config.Buffer);
var dispatcher = new ForkliftDispatcher(eventBus);
var optimizer = new PalletAssignmentOptimizer(config.Optimization);
var waveManager = new WaveManager(config.Wave);
var predictor = new PickerSpeedPredictor(metricsStore);
var renderer = new ConsoleRenderer();

// Подписываемся на события
eventBus.Subscribe<BufferLevelChangedEvent>(e =>
{
    metricsStore.RecordBufferLevel(e.CurrentLevel, DateTime.UtcNow);
});

eventBus.Subscribe<PalletDeliveredEvent>(e =>
{
    metricsStore.RecordDeliveryTime(e.Forklift, e.Pallet, e.DeliveryTime);
});

eventBus.Subscribe<PalletConsumedEvent>(e =>
{
    metricsStore.RecordPickerSpeed(e.Picker, e.Picker.PalletConsumptionRatePerHour);
});

// Активируем сборщиков
simulator.ActivatePickers(config.Workers.PickersCount);

// Создаём начальный поток заданий
var initialStream = simulator.CreateTestStream(10);
dispatcher.EnqueueStream(initialStream);

System.Console.WriteLine("Press any key to start simulation...");
System.Console.ReadKey(true);

var running = true;
var paused = false;
var lastUpdate = DateTime.UtcNow;

// Основной цикл
while (running)
{
    // Обработка ввода
    if (System.Console.KeyAvailable)
    {
        var key = System.Console.ReadKey(true).Key;
        switch (key)
        {
            case ConsoleKey.Q:
                running = false;
                break;
            case ConsoleKey.P:
                paused = true;
                break;
            case ConsoleKey.R:
                paused = false;
                break;
            case ConsoleKey.S:
                // Создать новый поток заданий
                var newStream = simulator.CreateTestStream(5);
                dispatcher.EnqueueStream(newStream);
                break;
            case ConsoleKey.OemPlus:
            case ConsoleKey.Add:
                config.Simulation.SpeedMultiplier = Math.Min(10, config.Simulation.SpeedMultiplier + 0.5);
                break;
            case ConsoleKey.OemMinus:
            case ConsoleKey.Subtract:
                config.Simulation.SpeedMultiplier = Math.Max(0.1, config.Simulation.SpeedMultiplier - 0.5);
                break;
        }
    }

    if (paused)
    {
        Thread.Sleep(100);
        continue;
    }

    var now = DateTime.UtcNow;
    var deltaTime = now - lastUpdate;
    lastUpdate = now;

    // 1. Realtime слой (каждый тик)
    simulator.Tick(deltaTime);

    // Обновляем контроллер буфера
    var consumptionRate = simulator.Pickers
        .Where(p => p.State == PickerState.Picking)
        .Sum(p => p.PalletConsumptionRatePerHour);

    bufferController.Update(simulator.Buffer, consumptionRate);

    // Диспетчер назначает задания
    dispatcher.DispatchTasks(simulator.Forklifts);

    // 2. Проверяем, нужно ли запросить больше палет
    if (bufferController.IsUrgentDeliveryRequired || simulator.Buffer.FillLevel < config.Buffer.LowThreshold)
    {
        var palletsNeeded = bufferController.GetPalletsToRequest();
        if (dispatcher.CurrentStream == null || dispatcher.CurrentStream.IsCompleted)
        {
            var urgentStream = simulator.CreateTestStream(palletsNeeded);
            dispatcher.EnqueueStream(urgentStream);
        }
    }

    // 3. Рендерим состояние
    var stats = simulator.GetStats();
    renderer.Render(stats, bufferController.CurrentState, simulator.Forklifts, simulator.Pickers);

    // Дополнительная информация
    System.Console.WriteLine($"Speed: {config.Simulation.SpeedMultiplier:F1}x | Streams: {dispatcher.PendingStreams.Count} pending | Controller: {bufferController.CurrentState}");

    Thread.Sleep(config.Timing.RealtimeCycleMs);
}

System.Console.CursorVisible = true;
System.Console.WriteLine("\nSimulation ended.");
