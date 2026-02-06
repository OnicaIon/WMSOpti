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
using WMS.BufferManagement.Services.Backtesting;
using SystemConsole = System.Console;

namespace WMS.BufferManagement;

/// <summary>
/// Точка входа: без параметров → интерактивное меню, с --флагами → CLI-режим
/// </summary>
public static class ProgramWithWms
{
    public static async Task Main(string[] args)
    {
        SystemConsole.OutputEncoding = System.Text.Encoding.UTF8;

        // Справка по командам
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return;
        }

        // CLI-режимы (для скриптов/автоматизации — обратная совместимость)
        if (args.Any(a => a.StartsWith("--")))
        {
            await RunCliMode(args);
            return;
        }

        // Без параметров → интерактивное меню
        var host = CreateHostBuilder(args).Build();
        await Tools.InteractiveMenu.RunAsync(host.Services);
    }

    private static async Task RunCliMode(string[] args)
    {
        // Режим расчёта статистики (--calc)
        if (args.Contains("--calc"))
        {
            SystemConsole.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
            SystemConsole.WriteLine("\u2551  WMS Buffer Management - Statistics Calculator               \u2551");
            SystemConsole.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
            SystemConsole.WriteLine();

            var host = CreateHostBuilder(args).Build();
            await RunCalculations.ExecuteAsync(host.Services);
            return;
        }

        // Режим обучения ML моделей (--train-ml)
        if (args.Contains("--train-ml"))
        {
            await Tools.TrainModelsCommand.RunAsync(args);
            return;
        }

        // Режимы синхронизации (--sync-*)
        if (args.Any(a => a.StartsWith("--sync-") || a == "--truncate-tasks"))
        {
            await Tools.SyncCommand.RunAsync(args);
            return;
        }

        // Режим фонового сервиса (--service)
        if (args.Contains("--service"))
        {
            await RunServiceMode(args);
            return;
        }

        // Неизвестный флаг → справка
        PrintHelp();
    }

    private static async Task RunServiceMode(string[] args)
    {
        SystemConsole.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
        SystemConsole.WriteLine("\u2551  WMS Buffer Management System v1.0                           \u2551");
        SystemConsole.WriteLine("\u2551  Mode: WMS Integration (Background Service)                  \u2551");
        SystemConsole.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
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

    private static void PrintHelp()
    {
        SystemConsole.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
        SystemConsole.WriteLine("\u2551  WMS Buffer Management System                                \u2551");
        SystemConsole.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("\u0418\u0441\u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u043d\u0438\u0435: dotnet run [-- \u043a\u043e\u043c\u0430\u043d\u0434\u0430]");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("\u041a\u043e\u043c\u0430\u043d\u0434\u044b:");
        SystemConsole.WriteLine("  (\u0431\u0435\u0437 \u043f\u0430\u0440\u0430\u043c\u0435\u0442\u0440\u043e\u0432)     \u0418\u043d\u0442\u0435\u0440\u0430\u043a\u0442\u0438\u0432\u043d\u043e\u0435 \u043c\u0435\u043d\u044e (sync, backtest, stats, ML)");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("  \u0421\u0418\u041d\u0425\u0420\u041e\u041d\u0418\u0417\u0410\u0426\u0418\u042f:");
        SystemConsole.WriteLine("  --sync-tasks         \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u0437\u0430\u0434\u0430\u0447 \u0438\u0437 WMS");
        SystemConsole.WriteLine("  --sync-zones         \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u0437\u043e\u043d");
        SystemConsole.WriteLine("  --sync-cells         \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u044f\u0447\u0435\u0435\u043a");
        SystemConsole.WriteLine("  --sync-products      \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u043f\u0440\u043e\u0434\u0443\u043a\u0442\u043e\u0432");
        SystemConsole.WriteLine("  --sync-all           \u0421\u0438\u043d\u0445\u0440\u043e\u043d\u0438\u0437\u0430\u0446\u0438\u044f \u0432\u0441\u0435\u0433\u043e");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("  \u0421\u0422\u0410\u0422\u0418\u0421\u0422\u0418\u041a\u0410:");
        SystemConsole.WriteLine("  --calc               \u041f\u0435\u0440\u0435\u0441\u0447\u0451\u0442 \u0441\u0442\u0430\u0442\u0438\u0441\u0442\u0438\u043a\u0438 (workers, routes, picker-product)");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("  ML:");
        SystemConsole.WriteLine("  --train-ml           \u041e\u0431\u0443\u0447\u0435\u043d\u0438\u0435 ML \u043c\u043e\u0434\u0435\u043b\u0435\u0439");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("  \u0421\u0415\u0420\u0412\u0418\u0421:");
        SystemConsole.WriteLine("  --service            \u0417\u0430\u043f\u0443\u0441\u043a \u0444\u043e\u043d\u043e\u0432\u043e\u0433\u043e \u0441\u0435\u0440\u0432\u0438\u0441\u0430 (sync + management)");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine("  \u0421\u041f\u0420\u0410\u0412\u041a\u0410:");
        SystemConsole.WriteLine("  --help, -h           \u041f\u043e\u043a\u0430\u0437\u0430\u0442\u044c \u044d\u0442\u0443 \u0441\u043f\u0440\u0430\u0432\u043a\u0443");
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

                // Не убивать хост при падении одного фонового сервиса
                services.Configure<HostOptions>(opts =>
                    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

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

                // Backtesting
                services.AddSingleton<WaveBacktestService>();

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

