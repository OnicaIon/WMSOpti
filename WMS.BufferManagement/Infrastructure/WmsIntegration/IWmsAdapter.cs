namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

/// <summary>
/// Интерфейс адаптера для интеграции с WMS системой
/// </summary>
public interface IWmsAdapter
{
    // === ЗАКАЗЫ И ВОЛНЫ ===

    /// <summary>
    /// Получить активные заказы для сборки
    /// </summary>
    Task<IReadOnlyList<WmsOrder>> GetActiveOrdersAsync(
        DateTime? fromTime = null,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить детали заказа с позициями
    /// </summary>
    Task<WmsOrderDetails?> GetOrderDetailsAsync(
        string orderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить следующую волну заказов
    /// </summary>
    Task<WmsWave?> GetNextWaveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтвердить запуск волны
    /// </summary>
    Task ConfirmWaveStartAsync(
        string waveId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Завершить волну
    /// </summary>
    Task CompleteWaveAsync(
        string waveId,
        WmsWaveResult result,
        CancellationToken cancellationToken = default);

    // === ПАЛЕТЫ И ТОВАРЫ ===

    /// <summary>
    /// Получить список палет в зоне хранения
    /// </summary>
    Task<IReadOnlyList<WmsPalletInfo>> GetStoragePalletsAsync(
        string? productType = null,
        string? zone = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить информацию о палете
    /// </summary>
    Task<WmsPalletInfo?> GetPalletInfoAsync(
        string palletId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Зарезервировать палету для доставки
    /// </summary>
    Task<WmsReservationResult> ReservePalletAsync(
        string palletId,
        string forkliftId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Освободить резервацию палеты
    /// </summary>
    Task ReleasePalletReservationAsync(
        string palletId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтвердить доставку палеты в буфер
    /// </summary>
    Task ConfirmPalletDeliveryAsync(
        string palletId,
        string bufferSlotId,
        DateTime deliveryTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтвердить потребление палеты сборщиком
    /// </summary>
    Task ConfirmPalletConsumedAsync(
        string palletId,
        string pickerId,
        WmsConsumeDetails details,
        CancellationToken cancellationToken = default);

    // === ПЕРСОНАЛ ===

    /// <summary>
    /// Получить список активных сборщиков
    /// </summary>
    Task<IReadOnlyList<WmsPickerInfo>> GetActivePickersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить статистику сборщика
    /// </summary>
    Task<WmsPickerStats?> GetPickerStatsAsync(
        string pickerId,
        DateTime fromTime,
        DateTime toTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить список карщиков
    /// </summary>
    Task<IReadOnlyList<WmsForkliftInfo>> GetForkliftsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Обновить статус карщика
    /// </summary>
    Task UpdateForkliftStatusAsync(
        string forkliftId,
        WmsForkliftStatus status,
        WmsPosition? position = null,
        CancellationToken cancellationToken = default);

    // === ЗАДАНИЯ ===

    /// <summary>
    /// Создать задание на перемещение
    /// </summary>
    Task<string> CreateDeliveryTaskAsync(
        WmsDeliveryTaskRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Обновить статус задания
    /// </summary>
    Task UpdateTaskStatusAsync(
        string taskId,
        WmsDeliveryTaskStatus status,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить активные задания
    /// </summary>
    Task<IReadOnlyList<WmsDeliveryTaskInfo>> GetActiveTasksAsync(
        CancellationToken cancellationToken = default);

    // === СОБЫТИЯ ===

    /// <summary>
    /// Подписка на события WMS
    /// </summary>
    IObservable<WmsEvent> Events { get; }

    // === ЗДОРОВЬЕ ===

    /// <summary>
    /// Проверка доступности WMS
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
