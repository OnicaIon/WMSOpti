using Microsoft.Extensions.Configuration;
using WMS.BufferManagement.Layers.Historical.Prediction;

namespace WMS.BufferManagement.Tools;

/// <summary>
/// Команда для обучения ML моделей
/// Запуск: dotnet run -- --train-ml
/// </summary>
public static class TrainModelsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Загружаем конфигурацию
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var connectionString = config["Historical:ConnectionString"]
            ?? "Host=localhost;Port=5433;Database=wms_history;Username=wms;Password=wms_password";

        var modelsPath = config["MlModels:Path"] ?? "data/models";

        System.Console.WriteLine($"Connection: {connectionString}");
        System.Console.WriteLine($"Models path: {modelsPath}\n");

        var trainer = new MlTrainer(connectionString, modelsPath);
        await trainer.TrainAllModelsAsync();

        return 0;
    }
}
