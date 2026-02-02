using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Npgsql;

namespace WMS.BufferManagement.Layers.Historical.Prediction;

/// <summary>
/// Обучение ML моделей для прогнозирования времени выполнения
/// </summary>
public class MlTrainer
{
    private readonly string _connectionString;
    private readonly MLContext _mlContext;
    private readonly string _modelsPath;

    public MlTrainer(string connectionString, string modelsPath = "data/models")
    {
        _connectionString = connectionString;
        _mlContext = new MLContext(seed: 42);
        _modelsPath = modelsPath;
        Directory.CreateDirectory(_modelsPath);
    }

    #region Data Classes

    /// <summary>
    /// Данные для обучения модели пикера (агрегировано по заданию)
    /// </summary>
    public class PickerTaskData
    {
        public string WorkerId { get; set; } = "";
        public float LinesCount { get; set; }
        public float TotalQty { get; set; }
        public float HourOfDay { get; set; }
        public float DayOfWeek { get; set; }
        public float TotalDurationSec { get; set; } // Target
    }

    /// <summary>
    /// Данные для обучения модели карщика (маршрут)
    /// </summary>
    public class ForkliftRouteData
    {
        public string FromZone { get; set; } = "";
        public string ToZone { get; set; } = "";
        public float HourOfDay { get; set; }
        public float DayOfWeek { get; set; }
        public float DurationSec { get; set; } // Target
    }

    public class DurationPrediction
    {
        [ColumnName("Score")]
        public float PredictedDuration { get; set; }
    }

    #endregion

    #region Training

    /// <summary>
    /// Обучить обе модели и сравнить с baseline
    /// </summary>
    public async Task TrainAllModelsAsync()
    {
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║  ML Training - Picker & Forklift Models                      ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

        var allMetrics = new AllModelsMetrics();

        var pickerResult = await TrainPickerModelAsync();
        if (pickerResult.HasValue)
            allMetrics.PickerModel = pickerResult.Value.Metrics;

        System.Console.WriteLine();

        var forkliftResult = await TrainForkliftModelAsync();
        if (forkliftResult.HasValue)
            allMetrics.ForkliftModel = forkliftResult.Value.Metrics;

        // Сохраняем метрики в JSON
        await SaveMetricsAsync(allMetrics);
    }

    private async Task SaveMetricsAsync(AllModelsMetrics metrics)
    {
        var metricsPath = Path.Combine(_modelsPath, "metrics.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(metrics, options);
        await File.WriteAllTextAsync(metricsPath, json);
        System.Console.WriteLine($"\n6. Метрики сохранены: {metricsPath}");
    }

    /// <summary>
    /// Обучить модель пикера
    /// </summary>
    public async Task<(ITransformer Model, ModelMetrics Metrics)?> TrainPickerModelAsync()
    {
        System.Console.WriteLine("=== МОДЕЛЬ ПИКЕРА (время выполнения задания) ===\n");

        // 1. Загрузка данных
        System.Console.WriteLine("1. Загрузка данных...");
        var data = await LoadPickerDataAsync();
        System.Console.WriteLine($"   Загружено заданий: {data.Count}");

        if (data.Count < 100)
        {
            System.Console.WriteLine("   Недостаточно данных для обучения");
            return null;
        }

        // 2. Разбивка train/test
        var (trainData, testData) = SplitData(data, 0.8);
        System.Console.WriteLine($"   Train: {trainData.Count}, Test: {testData.Count}\n");

        // 3. Baseline - средние
        System.Console.WriteLine("2. Расчёт baseline (простые средние)...");
        var globalAvg = trainData.Average(d => d.TotalDurationSec);
        var workerAvgs = trainData
            .Where(d => !string.IsNullOrEmpty(d.WorkerId))
            .GroupBy(d => d.WorkerId)
            .ToDictionary(g => g.Key, g => (double)g.Average(x => x.TotalDurationSec));
        System.Console.WriteLine($"   Глобальное среднее: {globalAvg:F1} сек");
        System.Console.WriteLine($"   Работников с данными: {workerAvgs.Count}\n");

        // 4. Обучение ML модели
        System.Console.WriteLine("3. Обучение ML модели (FastTree)...");
        var model = TrainPickerModel(trainData);
        System.Console.WriteLine("   Модель обучена\n");

        // 5. Оценка
        System.Console.WriteLine("4. Оценка на тестовых данных:\n");
        var metrics = EvaluatePickerModel(model, testData, globalAvg, workerAvgs);
        metrics.ModelName = "PickerTaskDuration";
        metrics.TrainSamples = trainData.Count;
        PrintMetrics(metrics);

        // 6. Сохранение
        var modelPath = Path.Combine(_modelsPath, $"picker_model_{DateTime.Now:yyyy-MM-dd}.zip");
        _mlContext.Model.Save(model, null, modelPath);
        metrics.ModelPath = modelPath;
        System.Console.WriteLine($"\n5. Модель сохранена: {modelPath}");

        return (model, metrics);
    }

    /// <summary>
    /// Обучить модель карщика
    /// </summary>
    public async Task<(ITransformer Model, ModelMetrics Metrics)?> TrainForkliftModelAsync()
    {
        System.Console.WriteLine("=== МОДЕЛЬ КАРЩИКА (время маршрута) ===\n");

        // 1. Загрузка данных
        System.Console.WriteLine("1. Загрузка данных...");
        var data = await LoadForkliftDataAsync();
        System.Console.WriteLine($"   Загружено маршрутов: {data.Count}");

        if (data.Count < 100)
        {
            System.Console.WriteLine("   Недостаточно данных для обучения");
            return null;
        }

        // 2. Разбивка train/test
        var (trainData, testData) = SplitData(data, 0.8);
        System.Console.WriteLine($"   Train: {trainData.Count}, Test: {testData.Count}\n");

        // 3. Baseline
        System.Console.WriteLine("2. Расчёт baseline...");
        var globalAvg = trainData.Average(d => d.DurationSec);
        var routeAvgs = trainData
            .GroupBy(d => (d.FromZone, d.ToZone))
            .ToDictionary(g => g.Key, g => (double)g.Average(x => x.DurationSec));
        System.Console.WriteLine($"   Глобальное среднее: {globalAvg:F1} сек");
        System.Console.WriteLine($"   Уникальных маршрутов: {routeAvgs.Count}\n");

        // 4. Обучение
        System.Console.WriteLine("3. Обучение ML модели (FastTree)...");
        var model = TrainForkliftModel(trainData);
        System.Console.WriteLine("   Модель обучена\n");

        // 5. Оценка
        System.Console.WriteLine("4. Оценка на тестовых данных:\n");
        var metrics = EvaluateForkliftModel(model, testData, globalAvg, routeAvgs);
        metrics.ModelName = "ForkliftRouteDuration";
        metrics.TrainSamples = trainData.Count;
        PrintMetrics(metrics);

        // 6. Сохранение
        var modelPath = Path.Combine(_modelsPath, $"forklift_model_{DateTime.Now:yyyy-MM-dd}.zip");
        _mlContext.Model.Save(model, null, modelPath);
        metrics.ModelPath = modelPath;
        System.Console.WriteLine($"\n5. Модель сохранена: {modelPath}");

        return (model, metrics);
    }

    #endregion

    #region Data Loading

    private async Task<List<PickerTaskData>> LoadPickerDataAsync()
    {
        var data = new List<PickerTaskData>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Агрегация по заданию (task_basis_number)
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                COALESCE(worker_id, 'UNKNOWN') as worker_id,
                COUNT(*) as lines_count,
                SUM(qty) as total_qty,
                EXTRACT(HOUR FROM MIN(created_at))::int as hour_of_day,
                EXTRACT(DOW FROM MIN(created_at))::int as day_of_week,
                SUM(duration_sec) as total_duration_sec
            FROM tasks
            WHERE worker_role = 'Picker'
              AND duration_sec > 0
              AND duration_sec < 600  -- отсекаем выбросы > 10 мин на строку
              AND task_basis_number IS NOT NULL
            GROUP BY task_basis_number, worker_id
            HAVING SUM(duration_sec) > 0
               AND SUM(duration_sec) < 3600  -- задание < 1 часа
            ORDER BY MIN(created_at)", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            data.Add(new PickerTaskData
            {
                WorkerId = reader.GetString(0),
                LinesCount = reader.GetInt64(1),
                TotalQty = (float)reader.GetDecimal(2),
                HourOfDay = reader.GetInt32(3),
                DayOfWeek = reader.GetInt32(4),
                TotalDurationSec = (float)reader.GetDecimal(5)
            });
        }

        return data;
    }

    private async Task<List<ForkliftRouteData>> LoadForkliftDataAsync()
    {
        var data = new List<ForkliftRouteData>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                COALESCE(from_zone, 'UNK') as from_zone,
                COALESCE(to_zone, 'UNK') as to_zone,
                EXTRACT(HOUR FROM created_at)::int as hour_of_day,
                EXTRACT(DOW FROM created_at)::int as day_of_week,
                duration_sec
            FROM tasks
            WHERE worker_role = 'Forklift'
              AND duration_sec > 0
              AND duration_sec < 600  -- отсекаем выбросы > 10 мин
              AND from_zone IS NOT NULL
              AND to_zone IS NOT NULL
            ORDER BY created_at", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            data.Add(new ForkliftRouteData
            {
                FromZone = reader.GetString(0),
                ToZone = reader.GetString(1),
                HourOfDay = reader.GetInt32(2),
                DayOfWeek = reader.GetInt32(3),
                DurationSec = (float)reader.GetDecimal(4)
            });
        }

        return data;
    }

    #endregion

    #region Model Training

    private ITransformer TrainPickerModel(List<PickerTaskData> trainData)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(trainData);

        var pipeline = _mlContext.Transforms.Categorical.OneHotEncoding("WorkerIdEncoded", "WorkerId")
            .Append(_mlContext.Transforms.Concatenate("Features",
                "WorkerIdEncoded", "LinesCount", "TotalQty", "HourOfDay", "DayOfWeek"))
            .Append(_mlContext.Regression.Trainers.FastTree(
                labelColumnName: "TotalDurationSec",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 10));

        return pipeline.Fit(dataView);
    }

    private ITransformer TrainForkliftModel(List<ForkliftRouteData> trainData)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(trainData);

        var pipeline = _mlContext.Transforms.Categorical.OneHotEncoding("FromZoneEncoded", "FromZone")
            .Append(_mlContext.Transforms.Categorical.OneHotEncoding("ToZoneEncoded", "ToZone"))
            .Append(_mlContext.Transforms.Concatenate("Features",
                "FromZoneEncoded", "ToZoneEncoded", "HourOfDay", "DayOfWeek"))
            .Append(_mlContext.Regression.Trainers.FastTree(
                labelColumnName: "DurationSec",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 5));

        return pipeline.Fit(dataView);
    }

    #endregion

    #region Evaluation

    public class ModelMetrics
    {
        public string ModelName { get; set; } = "";
        public DateTime TrainedAt { get; set; } = DateTime.UtcNow;
        public int TrainSamples { get; set; }
        public int TestSamples { get; set; }
        public double GlobalAvgMAE { get; set; }
        public double SpecificAvgMAE { get; set; }  // Worker avg или Route avg
        public double MlModelMAE { get; set; }
        public double ImprovementVsGlobal => Math.Round((1 - MlModelMAE / GlobalAvgMAE) * 100, 1);
        public double ImprovementVsSpecific => Math.Round((1 - MlModelMAE / SpecificAvgMAE) * 100, 1);
        public string ModelPath { get; set; } = "";
    }

    public class AllModelsMetrics
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public ModelMetrics? PickerModel { get; set; }
        public ModelMetrics? ForkliftModel { get; set; }
    }

    private ModelMetrics EvaluatePickerModel(
        ITransformer model,
        List<PickerTaskData> testData,
        double globalAvg,
        Dictionary<string, double> workerAvgs)
    {
        var predictor = _mlContext.Model.CreatePredictionEngine<PickerTaskData, DurationPrediction>(model);

        var globalErrors = new List<double>();
        var workerErrors = new List<double>();
        var mlErrors = new List<double>();

        foreach (var item in testData)
        {
            var actual = item.TotalDurationSec;

            // Baseline: глобальное среднее
            globalErrors.Add(Math.Abs(actual - globalAvg));

            // Среднее по работнику
            var workerPred = workerAvgs.TryGetValue(item.WorkerId, out var avg) ? avg : globalAvg;
            workerErrors.Add(Math.Abs(actual - workerPred));

            // ML
            var mlPred = predictor.Predict(item).PredictedDuration;
            mlErrors.Add(Math.Abs(actual - mlPred));
        }

        return new ModelMetrics
        {
            GlobalAvgMAE = globalErrors.Average(),
            SpecificAvgMAE = workerErrors.Average(),
            MlModelMAE = mlErrors.Average(),
            TestSamples = testData.Count
        };
    }

    private ModelMetrics EvaluateForkliftModel(
        ITransformer model,
        List<ForkliftRouteData> testData,
        double globalAvg,
        Dictionary<(string, string), double> routeAvgs)
    {
        var predictor = _mlContext.Model.CreatePredictionEngine<ForkliftRouteData, DurationPrediction>(model);

        var globalErrors = new List<double>();
        var routeErrors = new List<double>();
        var mlErrors = new List<double>();

        foreach (var item in testData)
        {
            var actual = item.DurationSec;

            // Baseline: глобальное среднее
            globalErrors.Add(Math.Abs(actual - globalAvg));

            // Среднее по маршруту
            var routePred = routeAvgs.TryGetValue((item.FromZone, item.ToZone), out var avg) ? avg : globalAvg;
            routeErrors.Add(Math.Abs(actual - routePred));

            // ML
            var mlPred = predictor.Predict(item).PredictedDuration;
            mlErrors.Add(Math.Abs(actual - mlPred));
        }

        return new ModelMetrics
        {
            GlobalAvgMAE = globalErrors.Average(),
            SpecificAvgMAE = routeErrors.Average(),
            MlModelMAE = mlErrors.Average(),
            TestSamples = testData.Count
        };
    }

    private void PrintMetrics(ModelMetrics m)
    {
        System.Console.WriteLine("   ┌─────────────────────────────┬────────────┬─────────────┐");
        System.Console.WriteLine("   │ Метод                       │ MAE (сек)  │ Улучшение   │");
        System.Console.WriteLine("   ├─────────────────────────────┼────────────┼─────────────┤");
        System.Console.WriteLine($"   │ Глобальное среднее          │ {m.GlobalAvgMAE,10:F2} │ baseline    │");
        System.Console.WriteLine($"   │ Среднее по группе           │ {m.SpecificAvgMAE,10:F2} │ {m.ImprovementVsGlobal - m.ImprovementVsSpecific + m.ImprovementVsGlobal,9:F1}%  │");
        System.Console.WriteLine($"   │ ML FastTree                 │ {m.MlModelMAE,10:F2} │ {m.ImprovementVsGlobal,9:F1}%  │");
        System.Console.WriteLine("   └─────────────────────────────┴────────────┴─────────────┘");
        System.Console.WriteLine($"   Тестовых образцов: {m.TestSamples}");
    }

    #endregion

    #region Helpers

    private (List<T> train, List<T> test) SplitData<T>(List<T> data, double trainRatio)
    {
        var shuffled = data.OrderBy(_ => Random.Shared.Next()).ToList();
        var splitIndex = (int)(shuffled.Count * trainRatio);
        return (shuffled.Take(splitIndex).ToList(), shuffled.Skip(splitIndex).ToList());
    }

    #endregion
}
