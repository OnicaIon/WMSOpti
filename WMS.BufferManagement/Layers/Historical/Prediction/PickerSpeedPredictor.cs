using Microsoft.ML;
using Microsoft.ML.Data;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Layers.Historical.DataCollection;

namespace WMS.BufferManagement.Layers.Historical.Prediction;

/// <summary>
/// ML.NET предиктор скорости сборщиков
/// </summary>
public class PickerSpeedPredictor : IPredictor
{
    private readonly MLContext _mlContext;
    private readonly MetricsStore _metricsStore;
    private ITransformer? _model;
    private PredictionEngine<PickerInput, PickerPrediction>? _predictionEngine;

    private double _defaultSpeed = 100; // палет/час по умолчанию

    public PickerSpeedPredictor(MetricsStore metricsStore)
    {
        _mlContext = new MLContext(seed: 42);
        _metricsStore = metricsStore;
    }

    public double PredictPickerSpeed(Picker picker, DateTime when)
    {
        if (_predictionEngine == null)
            return picker.AveragePickRatePerHour;

        var input = new PickerInput
        {
            Hour = when.Hour,
            DayOfWeek = (int)when.DayOfWeek
        };

        var prediction = _predictionEngine.Predict(input);
        return Math.Max(10, prediction.PalletsPerHour); // Минимум 10 палет/час
    }

    public TimeSpan PredictDeliveryTime(double distance, DateTime when)
    {
        // Простая модель: 2 м/с + 30 секунд на погрузку/разгрузку
        var travelTime = distance / 2.0 * 2; // туда и обратно
        var loadTime = 60; // погрузка + разгрузка
        return TimeSpan.FromSeconds(travelTime + loadTime);
    }

    public double PredictConsumptionRate(DateTime when)
    {
        // Используем среднюю скорость из метрик или модели
        if (_predictionEngine != null)
        {
            var input = new PickerInput
            {
                Hour = when.Hour,
                DayOfWeek = (int)when.DayOfWeek
            };
            var prediction = _predictionEngine.Predict(input);
            return prediction.PalletsPerHour * 20; // 20 сборщиков
        }

        return _defaultSpeed * 20;
    }

    public async Task TrainAsync()
    {
        var trainingData = _metricsStore.GetPickerTrainingData().ToList();

        if (trainingData.Count < 10)
        {
            // Недостаточно данных для обучения
            return;
        }

        await Task.Run(() =>
        {
            var dataView = _mlContext.Data.LoadFromEnumerable(
                trainingData.Select(d => new PickerInput
                {
                    Hour = d.Hour,
                    DayOfWeek = d.DayOfWeek,
                    PalletsPerHour = d.PalletsPerHour
                }));

            // Пайплайн: конкатенация фич → FastTree регрессия
            var pipeline = _mlContext.Transforms.Concatenate("Features", "Hour", "DayOfWeek")
                .Append(_mlContext.Regression.Trainers.FastTree(
                    labelColumnName: "PalletsPerHour",
                    featureColumnName: "Features",
                    numberOfTrees: 50,
                    numberOfLeaves: 20));

            _model = pipeline.Fit(dataView);
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<PickerInput, PickerPrediction>(_model);

            // Обновляем дефолтную скорость
            _defaultSpeed = trainingData.Average(d => d.PalletsPerHour);
        });
    }
}

public class PickerInput
{
    public float Hour { get; set; }
    public float DayOfWeek { get; set; }
    public float PalletsPerHour { get; set; }
}

public class PickerPrediction
{
    [ColumnName("Score")]
    public float PalletsPerHour { get; set; }
}
