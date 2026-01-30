using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Domain.Events;
using WMS.BufferManagement.Infrastructure.EventBus;

namespace WMS.BufferManagement.Layers.Realtime.Dispatcher;

/// <summary>
/// Диспетчер заданий для карщиков.
/// Потоки выполняются последовательно, задания внутри - по весу.
/// </summary>
public class ForkliftDispatcher : IDispatcher
{
    private readonly IEventBus _eventBus;
    private readonly List<TaskStream> _streams = new();
    private readonly object _lock = new();

    private TaskStream? _currentStream;
    private int _completedStreams;
    private int _totalTasksProcessed;

    public TaskStream? CurrentStream => _currentStream;
    public IReadOnlyList<TaskStream> PendingStreams
    {
        get
        {
            lock (_lock)
            {
                return _streams.Where(s => s.Status == StreamStatus.Pending).ToList();
            }
        }
    }

    public ForkliftDispatcher(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public void EnqueueTask(DeliveryTask task)
    {
        // Одиночное задание - создаём временный поток
        var stream = new TaskStream
        {
            Name = $"Single-{task.Id}",
            SequenceNumber = GetNextStreamSequence()
        };
        stream.AddTask(task);

        EnqueueStream(stream);
    }

    public void EnqueueStream(TaskStream stream)
    {
        lock (_lock)
        {
            stream.Status = StreamStatus.Pending;
            _streams.Add(stream);

            // Сортируем потоки по sequence number
            _streams.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
        }
    }

    public void DispatchTasks(IEnumerable<Forklift> forklifts)
    {
        lock (_lock)
        {
            // 1. Если нет текущего потока, берём следующий pending
            if (_currentStream == null || _currentStream.IsCompleted)
            {
                CompleteCurrentStream();
                _currentStream = GetNextPendingStream();

                if (_currentStream != null)
                {
                    _currentStream.Status = StreamStatus.InProgress;
                    _currentStream.StartedAt = DateTime.UtcNow;
                }
            }

            if (_currentStream == null) return;

            // 2. Назначаем задания из текущего потока свободным карщикам
            var idleForklifts = forklifts
                .Where(f => f.State == ForkliftState.Idle && f.CurrentTask == null)
                .ToList();

            foreach (var forklift in idleForklifts)
            {
                var task = _currentStream.GetNextPendingTask();
                if (task == null) break;

                AssignTaskToForklift(task, forklift);
            }
        }
    }

    private void AssignTaskToForklift(DeliveryTask task, Forklift forklift)
    {
        task.Status = DeliveryTaskStatus.Assigned;
        task.AssignedForklift = forklift;
        task.StartedAt = DateTime.UtcNow;

        forklift.CurrentTask = task;
        forklift.State = ForkliftState.MovingToPallet;

        // Рассчитываем ETA
        task.EstimatedCompletionTime = DateTime.UtcNow + forklift.EstimateDeliveryTime(task.Pallet);

        _eventBus.Publish(new ForkliftStateChangedEvent
        {
            Forklift = forklift,
            PreviousState = ForkliftState.Idle,
            CurrentState = ForkliftState.MovingToPallet
        });
    }

    private TaskStream? GetNextPendingStream()
    {
        return _streams
            .Where(s => s.Status == StreamStatus.Pending)
            .OrderBy(s => s.SequenceNumber)
            .FirstOrDefault();
    }

    private void CompleteCurrentStream()
    {
        if (_currentStream != null && _currentStream.IsCompleted)
        {
            _currentStream.Status = StreamStatus.Completed;
            _currentStream.CompletedAt = DateTime.UtcNow;
            _completedStreams++;

            var duration = _currentStream.CompletedAt.Value - _currentStream.StartedAt!.Value;
            _eventBus.Publish(new TaskStreamCompletedEvent
            {
                Stream = _currentStream,
                TotalTasks = _currentStream.AllTasks.Count,
                TotalDuration = duration
            });
        }
    }

    private int GetNextStreamSequence()
    {
        lock (_lock)
        {
            return _streams.Count > 0 ? _streams.Max(s => s.SequenceNumber) + 1 : 0;
        }
    }

    public QueueStats GetQueueStats()
    {
        lock (_lock)
        {
            var allTasks = _streams.SelectMany(s => s.AllTasks).ToList();
            var totalForklifts = allTasks.Count(t => t.AssignedForklift != null);

            return new QueueStats(
                TotalTasks: allTasks.Count,
                PendingTasks: allTasks.Count(t => t.Status == DeliveryTaskStatus.Pending),
                InProgressTasks: allTasks.Count(t => t.Status == DeliveryTaskStatus.InProgress || t.Status == DeliveryTaskStatus.Assigned),
                CompletedTasks: allTasks.Count(t => t.Status == DeliveryTaskStatus.Completed),
                TotalStreams: _streams.Count,
                CompletedStreams: _completedStreams,
                Utilization: totalForklifts > 0 ? (double)allTasks.Count(t => t.Status == DeliveryTaskStatus.InProgress) / totalForklifts : 0
            );
        }
    }

    /// <summary>
    /// Отметить задание как завершённое
    /// </summary>
    public void CompleteTask(DeliveryTask task, Forklift forklift)
    {
        lock (_lock)
        {
            task.Status = DeliveryTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            forklift.CurrentTask = null;
            forklift.State = ForkliftState.Idle;
            forklift.CompletedTasksCount++;
            _totalTasksProcessed++;

            _eventBus.Publish(new PalletDeliveredEvent
            {
                Pallet = task.Pallet,
                Forklift = forklift,
                DeliveryTime = task.ActualDuration ?? TimeSpan.Zero,
                DistanceMeters = task.Pallet.StorageDistanceMeters,
                TaskId = task.Id,
                StreamId = task.Stream?.Id
            });
        }
    }
}
