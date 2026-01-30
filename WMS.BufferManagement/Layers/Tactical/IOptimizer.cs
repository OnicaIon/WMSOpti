using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Layers.Tactical.Models;

namespace WMS.BufferManagement.Layers.Tactical;

/// <summary>
/// Интерфейс оптимизатора назначений
/// </summary>
public interface IOptimizer
{
    /// <summary>
    /// Оптимизировать назначение заданий карщикам
    /// </summary>
    OptimizationResult Optimize(
        IEnumerable<DeliveryTask> tasks,
        IEnumerable<Forklift> forklifts,
        TaskStream? currentStream = null);

    /// <summary>
    /// Оптимизировать с учётом потоков
    /// </summary>
    OptimizationResult OptimizeWithStreams(
        IEnumerable<TaskStream> streams,
        IEnumerable<Forklift> forklifts);
}
