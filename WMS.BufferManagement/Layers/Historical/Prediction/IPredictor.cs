using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Layers.Historical.Prediction;

/// <summary>
/// Интерфейс предиктора
/// </summary>
public interface IPredictor
{
    /// <summary>
    /// Прогноз скорости сборщика
    /// </summary>
    double PredictPickerSpeed(Picker picker, DateTime when);

    /// <summary>
    /// Прогноз времени доставки
    /// </summary>
    TimeSpan PredictDeliveryTime(double distance, DateTime when);

    /// <summary>
    /// Прогноз потребления буфера
    /// </summary>
    double PredictConsumptionRate(DateTime when);

    /// <summary>
    /// Обучить модель на новых данных
    /// </summary>
    Task TrainAsync();
}
