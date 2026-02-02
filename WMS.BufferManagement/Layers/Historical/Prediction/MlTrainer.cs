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

        System.Console.WriteLine();

        // Статистика по товарам
        allMetrics.ProductStats = await CalculateProductStatisticsAsync();

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

    #region Product Statistics

    /// <summary>
    /// Расчёт статистики по товарам
    /// </summary>
    public async Task<ProductStatsSummary> CalculateProductStatisticsAsync()
    {
        System.Console.WriteLine("=== СТАТИСТИКА ПО ТОВАРАМ ===\n");
        System.Console.WriteLine("1. Расчёт статистики...");

        var stats = new List<ProductStatRecord>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Получаем статистику по каждому товару
        await using var cmd = new NpgsqlCommand(@"
            WITH product_tasks AS (
                SELECT
                    product_type,
                    duration_sec,
                    qty,
                    created_at
                FROM tasks
                WHERE product_type IS NOT NULL
                  AND duration_sec > 0
                  AND duration_sec < 600
                  AND qty > 0
            ),
            date_range AS (
                SELECT
                    MIN(created_at::date) as min_date,
                    MAX(created_at::date) as max_date,
                    GREATEST(1, MAX(created_at::date) - MIN(created_at::date) + 1) as days_count
                FROM product_tasks
            )
            SELECT
                pt.product_type,
                AVG(pt.duration_sec) as avg_time,
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY pt.duration_sec) as median_time,
                STDDEV(pt.duration_sec) as std_dev,
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY pt.qty) as typical_qty,
                MIN(pt.qty) as min_qty,
                MAX(pt.qty) as max_qty,
                COUNT(*) as tasks_count,
                COUNT(*)::decimal / dr.days_count as tasks_per_day
            FROM product_tasks pt
            CROSS JOIN date_range dr
            GROUP BY pt.product_type, dr.days_count
            HAVING COUNT(*) >= 10
            ORDER BY COUNT(*) DESC", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            stats.Add(new ProductStatRecord
            {
                ProductCode = reader.GetString(0),
                ProductName = reader.GetString(0), // Используем код как имя, если имя не доступно
                AvgTimeSec = reader.IsDBNull(1) ? 0 : (double)reader.GetDecimal(1),
                MedianTimeSec = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                StdDevSec = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                TypicalQty = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                MinQty = reader.IsDBNull(5) ? 0 : (double)reader.GetDecimal(5),
                MaxQty = reader.IsDBNull(6) ? 0 : (double)reader.GetDecimal(6),
                TasksCount = reader.GetInt32(7),
                TasksPerDay = reader.IsDBNull(8) ? 0 : (double)reader.GetDecimal(8)
            });
        }

        System.Console.WriteLine($"   Товаров со статистикой: {stats.Count}");

        if (stats.Count > 0)
        {
            var avgTime = stats.Average(s => s.AvgTimeSec);
            var medianTime = stats.OrderBy(s => s.MedianTimeSec).Skip(stats.Count / 2).First().MedianTimeSec;

            System.Console.WriteLine($"   Среднее время распределения: {avgTime:F1} сек");
            System.Console.WriteLine($"   Медианное время: {medianTime:F1} сек\n");

            // Топ-10 самых частых товаров
            System.Console.WriteLine("2. Топ-10 товаров по частоте:\n");
            System.Console.WriteLine("   ┌──────────────────┬──────────┬──────────┬──────────┬──────────┐");
            System.Console.WriteLine("   │ Товар            │ Avg(сек) │ Med(сек) │ Кол-во   │ В день   │");
            System.Console.WriteLine("   ├──────────────────┼──────────┼──────────┼──────────┼──────────┤");

            foreach (var s in stats.Take(10))
            {
                var name = s.ProductCode.Length > 16 ? s.ProductCode[..16] : s.ProductCode.PadRight(16);
                System.Console.WriteLine($"   │ {name} │ {s.AvgTimeSec,8:F1} │ {s.MedianTimeSec,8:F1} │ {s.TasksCount,8} │ {s.TasksPerDay,8:F1} │");
            }
            System.Console.WriteLine("   └──────────────────┴──────────┴──────────┴──────────┴──────────┘");

            // Сохраняем в БД (новое соединение, т.к. reader ещё открыт)
            await SaveProductStatisticsToDbAsync(stats);

            return new ProductStatsSummary
            {
                TotalProducts = stats.Count,
                ProductsWithStats = stats.Count,
                AvgDistributionTimeSec = avgTime,
                MedianDistributionTimeSec = medianTime,
                TopProducts = stats.Take(20).ToList()
            };
        }

        return new ProductStatsSummary();
    }

    private async Task SaveProductStatisticsToDbAsync(List<ProductStatRecord> stats)
    {
        System.Console.WriteLine("\n3. Сохранение в БД...");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Создаём таблицу если не существует
        await using (var createCmd = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS product_statistics (
                product_code TEXT PRIMARY KEY,
                product_name TEXT,
                avg_distribution_time_sec DECIMAL(10,2),
                median_distribution_time_sec DECIMAL(10,2),
                std_dev_time_sec DECIMAL(10,2),
                typical_qty DECIMAL(10,2),
                min_qty DECIMAL(10,2),
                max_qty DECIMAL(10,2),
                tasks_count INTEGER,
                tasks_per_day DECIMAL(10,2),
                updated_at TIMESTAMPTZ DEFAULT NOW()
            )", conn))
        {
            await createCmd.ExecuteNonQueryAsync();
        }

        // Очищаем и вставляем заново
        await using (var truncateCmd = new NpgsqlCommand("TRUNCATE product_statistics", conn))
        {
            await truncateCmd.ExecuteNonQueryAsync();
        }

        // Вставляем данные
        foreach (var s in stats)
        {
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO product_statistics
                (product_code, product_name, avg_distribution_time_sec, median_distribution_time_sec,
                 std_dev_time_sec, typical_qty, min_qty, max_qty, tasks_count, tasks_per_day)
                VALUES (@code, @name, @avg, @median, @std, @typ, @min, @max, @cnt, @perday)", conn);

            insertCmd.Parameters.AddWithValue("code", s.ProductCode);
            insertCmd.Parameters.AddWithValue("name", s.ProductName);
            insertCmd.Parameters.AddWithValue("avg", (decimal)s.AvgTimeSec);
            insertCmd.Parameters.AddWithValue("median", (decimal)s.MedianTimeSec);
            insertCmd.Parameters.AddWithValue("std", (decimal)s.StdDevSec);
            insertCmd.Parameters.AddWithValue("typ", (decimal)s.TypicalQty);
            insertCmd.Parameters.AddWithValue("min", (decimal)s.MinQty);
            insertCmd.Parameters.AddWithValue("max", (decimal)s.MaxQty);
            insertCmd.Parameters.AddWithValue("cnt", s.TasksCount);
            insertCmd.Parameters.AddWithValue("perday", (decimal)s.TasksPerDay);

            await insertCmd.ExecuteNonQueryAsync();
        }

        System.Console.WriteLine($"   Сохранено записей: {stats.Count}");
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
        public ProductStatsSummary? ProductStats { get; set; }
    }

    public class ProductStatsSummary
    {
        public int TotalProducts { get; set; }
        public int ProductsWithStats { get; set; }
        public double AvgDistributionTimeSec { get; set; }
        public double MedianDistributionTimeSec { get; set; }
        public List<ProductStatRecord> TopProducts { get; set; } = new();
    }

    public class ProductStatRecord
    {
        public string ProductCode { get; set; } = "";
        public string ProductName { get; set; } = "";
        public double AvgTimeSec { get; set; }
        public double MedianTimeSec { get; set; }
        public double StdDevSec { get; set; }
        public double TypicalQty { get; set; }
        public double MinQty { get; set; }
        public double MaxQty { get; set; }
        public int TasksCount { get; set; }
        public double TasksPerDay { get; set; }
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
