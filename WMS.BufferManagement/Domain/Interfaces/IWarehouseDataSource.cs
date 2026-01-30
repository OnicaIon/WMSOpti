using WMS.BufferManagement.Domain.Entities;

namespace WMS.BufferManagement.Domain.Interfaces;

/// <summary>
/// Интерфейс источника данных склада (для интеграции с внешней WMS)
/// </summary>
public interface IWarehouseDataSource
{
    /// <summary>
    /// Получить доступные палеты в зоне хранения
    /// </summary>
    Task<IEnumerable<Pallet>> GetAvailablePalletsAsync();

    /// <summary>
    /// Получить заказы для сборки
    /// </summary>
    Task<IEnumerable<Order>> GetPendingOrdersAsync();

    /// <summary>
    /// Получить информацию о карщиках
    /// </summary>
    Task<IEnumerable<Forklift>> GetForkliftsAsync();

    /// <summary>
    /// Получить информацию о сборщиках
    /// </summary>
    Task<IEnumerable<Picker>> GetPickersAsync();

    /// <summary>
    /// Получить текущее состояние буфера
    /// </summary>
    Task<BufferZone> GetBufferStateAsync();

    /// <summary>
    /// Получить потоки заданий
    /// </summary>
    Task<IEnumerable<TaskStream>> GetTaskStreamsAsync();
}
