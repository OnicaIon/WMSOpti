namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

// === Заказы ===

public record WmsOrder(
    string Id,
    string CustomerId,
    DateTime CreatedAt,
    DateTime? DueTime,
    OrderPriority Priority,
    IReadOnlyList<WmsOrderLine> Lines);

public record WmsOrderLine(
    string ProductId,
    string ProductName,
    int Quantity,
    string? PreferredPalletId);

public record WmsOrderDetails(
    WmsOrder Order,
    IReadOnlyList<WmsPalletRequirement> RequiredPallets,
    WmsEstimatedTimes Estimates);

public record WmsPalletRequirement(
    string ProductId,
    int Quantity,
    WeightCategory Weight,
    IReadOnlyList<string> AvailablePalletIds);

public record WmsEstimatedTimes(
    TimeSpan EstimatedPickingTime,
    TimeSpan EstimatedDeliveryTime,
    DateTime EstimatedCompletionTime);

// === Волны ===

public record WmsWave(
    string Id,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    WaveStatus Status,
    IReadOnlyList<string> OrderIds,
    int TotalPallets);

public record WmsWaveResult(
    string WaveId,
    bool Success,
    int CompletedOrders,
    int FailedOrders,
    TimeSpan ActualDuration,
    string? FailureReason);

// === Палеты ===

public record WmsPalletInfo(
    string Id,
    string ProductId,
    string ProductName,
    int Quantity,
    double WeightKg,
    WeightCategory WeightCategory,
    string CurrentZone,
    string CurrentSlot,
    WmsPosition Position,
    DateTime LastMovedAt,
    WmsPalletStatus Status);

public record WmsReservationResult(
    bool Success,
    string? ReservationId,
    DateTime? ExpiresAt,
    string? FailureReason);

public record WmsConsumeDetails(
    int QuantityTaken,
    int QuantityRemaining,
    TimeSpan PickingDuration,
    string? DestinationCell);

// === Позиция ===

public record WmsPosition(double X, double Y, double Z = 0);

// === Персонал ===

public record WmsPickerInfo(
    string Id,
    string Name,
    string Zone,
    WmsPickerStatus Status,
    DateTime ShiftStart,
    DateTime? ShiftEnd);

public record WmsPickerStats(
    string PickerId,
    int PalletsProcessed,
    int ItemsPicked,
    double AverageSpeed,
    double Efficiency,
    TimeSpan TotalActiveTime,
    TimeSpan TotalIdleTime);

public record WmsForkliftInfo(
    string Id,
    string OperatorName,
    WmsForkliftStatus Status,
    WmsPosition? CurrentPosition,
    string? CurrentTaskId,
    double DistanceFromBuffer);

// === Задания ===

public record WmsDeliveryTaskRequest(
    string PalletId,
    string SourceZone,
    string SourceSlot,
    string TargetZone,
    string TargetSlot,
    TaskPriority Priority,
    string? AssignedForkliftId);

public record WmsDeliveryTaskInfo(
    string Id,
    string PalletId,
    string? ForkliftId,
    WmsDeliveryTaskStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    WmsPosition? CurrentPosition);

// === События ===

public abstract record WmsEvent(DateTime Timestamp);

public record WmsOrderCreatedEvent(
    DateTime Timestamp,
    WmsOrder Order) : WmsEvent(Timestamp);

public record WmsPalletMovedEvent(
    DateTime Timestamp,
    string PalletId,
    string FromZone,
    string ToZone) : WmsEvent(Timestamp);

public record WmsPickerStatusChangedEvent(
    DateTime Timestamp,
    string PickerId,
    WmsPickerStatus OldStatus,
    WmsPickerStatus NewStatus) : WmsEvent(Timestamp);

public record WmsTaskCompletedEvent(
    DateTime Timestamp,
    string TaskId,
    bool Success,
    string? FailureReason) : WmsEvent(Timestamp);

public record WmsBufferLevelChangedEvent(
    DateTime Timestamp,
    double OldLevel,
    double NewLevel,
    string Reason) : WmsEvent(Timestamp);

// === Enums ===

public enum OrderPriority { Low, Normal, High, Urgent }
public enum WaveStatus { Pending, InProgress, Completed, Failed }
public enum WmsPalletStatus { Available, Reserved, InTransit, InBuffer, Consumed }
public enum WmsPickerStatus { Idle, Active, OnBreak, Offline }
public enum WmsForkliftStatus { Idle, EnRoute, Loading, Unloading, Maintenance }
public enum WmsDeliveryTaskStatus { Pending, Assigned, InProgress, Completed, Failed, Cancelled }
public enum TaskPriority { Low, Normal, High, Critical }
public enum WeightCategory { Light, Medium, Heavy }
