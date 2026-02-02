using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Infrastructure.EventBus;
using WMS.BufferManagement.Infrastructure.WmsIntegration;
using WMS.BufferManagement.Layers.Historical.DataCollection;
using WMS.BufferManagement.Layers.Historical.Prediction;
using WMS.BufferManagement.Layers.Realtime.BufferControl;
using WMS.BufferManagement.Layers.Realtime.Dispatcher;
using WMS.BufferManagement.Layers.Tactical;
using WMS.BufferManagement.Services;
using SystemConsole = System.Console;

namespace WMS.BufferManagement;

/// <summary>
/// Точка входа с поддержкой WMS интеграции
/// Использование: dotnet run -- --wms для режима WMS, иначе симуляция
/// </summary>
public static class ProgramWithWms
{
    public static async Task Main(string[] args)
    {
        SystemConsole.OutputEncoding = System.Text.Encoding.UTF8;

        // Режим расчёта статистики (--calc)
        if (args.Contains("--calc"))
        {
            SystemConsole.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            SystemConsole.WriteLine("║  WMS Buffer Management - Statistics Calculator               ║");
            SystemConsole.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            SystemConsole.WriteLine();

            var host = CreateHostBuilder(args).Build();
            await RunCalculations.ExecuteAsync(host.Services);
            return;
        }

        SystemConsole.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        SystemConsole.WriteLine("║  WMS Buffer Management System v1.0                           ║");
        SystemConsole.WriteLine("║  Mode: WMS Integration                                       ║");
        SystemConsole.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        SystemConsole.WriteLine();

        var hostFull = CreateHostBuilder(args).Build();

        var cts = new CancellationTokenSource();

        SystemConsole.CancelKeyPress += (s, e) =>
        {
            SystemConsole.WriteLine("\nShutting down...");
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await hostFull.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false)
                    .AddEnvironmentVariables("WMS_");
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;

                // Конфигурация
                services.Configure<BufferConfig>(configuration.GetSection("Buffer"));
                services.Configure<TimingConfig>(configuration.GetSection("Timing"));
                services.Configure<WaveConfig>(configuration.GetSection("Wave"));
                services.Configure<WorkersConfig>(configuration.GetSection("Workers"));
                services.Configure<OptimizationConfig>(configuration.GetSection("Optimization"));
                services.Configure<SimulationConfig>(configuration.GetSection("Simulation"));
                services.Configure<BufferManagementSettings>(configuration.GetSection("BufferManagement"));
                services.Configure<AggregationSettings>(configuration.GetSection("Aggregation"));

                // Инфраструктура
                services.AddSingleton<IEventBus, InMemoryEventBus>();
                services.AddSingleton<MetricsStore>();

                // Realtime слой
                services.AddSingleton<HysteresisController>(sp =>
                {
                    var config = configuration.GetSection("Buffer").Get<BufferConfig>() ?? new BufferConfig();
                    return new HysteresisController(config);
                });
                services.AddSingleton<ForkliftDispatcher>();

                // Tactical слой
                services.AddSingleton<PalletAssignmentOptimizer>(sp =>
                {
                    var config = configuration.GetSection("Optimization").Get<OptimizationConfig>() ?? new OptimizationConfig();
                    return new PalletAssignmentOptimizer(config);
                });
                services.AddSingleton<WaveManager>(sp =>
                {
                    var config = configuration.GetSection("Wave").Get<WaveConfig>() ?? new WaveConfig();
                    return new WaveManager(config);
                });

                // Historical слой
                services.AddSingleton<PickerSpeedPredictor>();

                // WMS интеграция (всегда включена)
                services.AddWms1CIntegration(configuration);
                services.AddHistoricalPersistence(configuration);

                // Фоновые службы (регистрируем как Singleton + HostedService)
                services.AddSingleton<BufferManagementService>();
                services.AddHostedService(sp => sp.GetRequiredService<BufferManagementService>());

                services.AddSingleton<AggregationService>();
                services.AddHostedService(sp => sp.GetRequiredService<AggregationService>());

                // Служба вывода статистики (отключена)
                // services.AddHostedService<StatsReportingService>();
            });
    }
}

/// <summary>
/// Служба периодического вывода статистики
/// </summary>
public class StatsReportingService : BackgroundService
{
    private readonly BufferManagementService _bufferService;
    private readonly AggregationService _aggregationService;
    private readonly ILogger<StatsReportingService> _logger;

    public StatsReportingService(
        BufferManagementService bufferService,
        AggregationService aggregationService,
        ILogger<StatsReportingService> logger)
    {
        _bufferService = bufferService;
        _aggregationService = aggregationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken); // Каждые 10 секунд

            try
            {
                var stats = _bufferService.GetStats();

                SystemConsole.Clear();
                SystemConsole.WriteLine("╔══════════════════════════════════════════════════════════════╗");
                SystemConsole.WriteLine("║  WMS Buffer Management - Live Status                         ║");
                SystemConsole.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                SystemConsole.WriteLine($"║  BUFFER: [{GetProgressBar(stats.CurrentLevel, 20)}] {stats.CurrentLevel:P0} ({stats.CurrentState})  ║");
                SystemConsole.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                SystemConsole.WriteLine($"║  Consumption Rate: {stats.ConsumptionRate,6:F1} pal/h                           ║");
                SystemConsole.WriteLine($"║  Delivery Rate:    {stats.DeliveryRate,6:F1} pal/h                           ║");
                SystemConsole.WriteLine($"║  Active Pickers:   {stats.ActivePickers,3}                                     ║");
                SystemConsole.WriteLine($"║  Active Forklifts: {stats.ActiveForklifts,3}                                     ║");
                SystemConsole.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                SystemConsole.WriteLine($"║  Total Requests:   {stats.TotalDeliveryRequests,5}                                   ║");
                SystemConsole.WriteLine($"║  Critical Events:  {stats.CriticalInterventions,5}                                   ║");
                SystemConsole.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                SystemConsole.WriteLine($"║  Picker Aggregates: {_aggregationService.PickerAggregates.Count,3} | Routes: {_aggregationService.RouteAggregates.Count,3}     ║");
                SystemConsole.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                SystemConsole.WriteLine("║  Press Ctrl+C to exit                                        ║");
                SystemConsole.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error rendering stats");
            }
        }
    }

    private static string GetProgressBar(double value, int width)
    {
        var filled = (int)(value * width);
        var empty = width - filled;
        return new string('█', filled) + new string('░', empty);
    }
}

