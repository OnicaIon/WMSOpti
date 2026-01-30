using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Layers.Realtime.BufferControl;

/// <summary>
/// Интерфейс контроллера буфера
/// </summary>
public interface IBufferController
{
    /// <summary>
    /// Текущий уровень буфера (0-1)
    /// </summary>
    double CurrentLevel { get; }

    /// <summary>
    /// Рассчитать требуемую скорость подачи (палет/час)
    /// </summary>
    double CalculateRequiredDeliveryRate(double consumptionRate);

    /// <summary>
    /// Требуется ли срочная подача
    /// </summary>
    bool IsUrgentDeliveryRequired { get; }

    /// <summary>
    /// Обновить состояние контроллера
    /// </summary>
    void Update(BufferZone buffer, double consumptionRate);

    /// <summary>
    /// Получить количество палет для запроса
    /// </summary>
    int GetPalletsToRequest();
}
