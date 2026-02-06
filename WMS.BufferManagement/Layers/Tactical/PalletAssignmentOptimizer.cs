using Google.OrTools.Sat;
using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Infrastructure.Configuration;
using WMS.BufferManagement.Layers.Tactical.Models;

namespace WMS.BufferManagement.Layers.Tactical;

/// <summary>
/// Оптимизатор назначений с использованием Google OR-Tools CP-SAT
/// Учитывает: последовательность потоков, сортировку по весу внутри потока
/// </summary>
public class PalletAssignmentOptimizer : IOptimizer
{
    private readonly OptimizationConfig _config;

    public PalletAssignmentOptimizer(OptimizationConfig config)
    {
        _config = config;
    }

    public OptimizationResult Optimize(
        IEnumerable<DeliveryTask> tasks,
        IEnumerable<Forklift> forklifts,
        TaskStream? currentStream = null)
    {
        var taskList = tasks.ToList();
        var forkliftList = forklifts.Where(f => f.State != ForkliftState.Offline).ToList();

        if (!taskList.Any() || !forkliftList.Any())
            return OptimizationResult.Empty;

        var startTime = DateTime.UtcNow;

        // Сортируем задания по весу (тяжёлые первыми)
        taskList = taskList.OrderByDescending(t => t.WeightKg).ToList();

        var model = new CpModel();
        var numTasks = taskList.Count;
        var numForklifts = forkliftList.Count;

        // Переменные: assignment[i,j] = 1 если задание i назначено карщику j
        var assignments = new BoolVar[numTasks, numForklifts];
        for (int i = 0; i < numTasks; i++)
        {
            for (int j = 0; j < numForklifts; j++)
            {
                assignments[i, j] = model.NewBoolVar($"assign_{i}_{j}");
            }
        }

        // Ограничение: каждое задание назначено ровно одному карщику
        for (int i = 0; i < numTasks; i++)
        {
            var taskAssignments = new List<ILiteral>();
            for (int j = 0; j < numForklifts; j++)
            {
                taskAssignments.Add(assignments[i, j]);
            }
            model.AddExactlyOne(taskAssignments);
        }

        // Рассчитываем матрицу стоимости (время доставки)
        var costs = new long[numTasks, numForklifts];
        for (int i = 0; i < numTasks; i++)
        {
            var task = taskList[i];
            for (int j = 0; j < numForklifts; j++)
            {
                var forklift = forkliftList[j];
                var deliveryTime = forklift.EstimateDeliveryTime(task.Pallet);
                costs[i, j] = (long)deliveryTime.TotalSeconds;
            }
        }

        // Целевая функция: минимизировать суммарное время + λ * дисперсию нагрузки
        var totalCost = new List<LinearExpr>();
        var workloads = new IntVar[numForklifts];

        for (int j = 0; j < numForklifts; j++)
        {
            var forkliftWorkload = new List<LinearExpr>();
            for (int i = 0; i < numTasks; i++)
            {
                totalCost.Add(assignments[i, j] * costs[i, j]);
                forkliftWorkload.Add(assignments[i, j] * costs[i, j]);
            }
            workloads[j] = model.NewIntVar(0, 100000, $"workload_{j}");
            model.Add(workloads[j] == LinearExpr.Sum(forkliftWorkload));
        }

        // Минимизируем общую стоимость
        model.Minimize(LinearExpr.Sum(totalCost));

        // Решаем
        var solver = new CpSolver();
        solver.StringParameters = $"max_time_in_seconds:{(_config.MaxSolverTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        var status = solver.Solve(model);
        var solverTime = TimeSpan.FromMilliseconds(_config.MaxSolverTimeMs);

        if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
        {
            return new OptimizationResult
            {
                IsOptimal = false,
                IsFeasible = false,
                SolverTime = solverTime
            };
        }

        // Собираем результаты
        var resultAssignments = new List<Assignment>();
        var workloadValues = new double[numForklifts];

        for (int i = 0; i < numTasks; i++)
        {
            for (int j = 0; j < numForklifts; j++)
            {
                if (solver.Value(assignments[i, j]) == 1)
                {
                    var task = taskList[i];
                    var forklift = forkliftList[j];

                    resultAssignments.Add(new Assignment
                    {
                        Task = task,
                        Forklift = forklift,
                        EstimatedTime = TimeSpan.FromSeconds(costs[i, j]),
                        Cost = costs[i, j],
                        SequenceInStream = i // Порядок по весу
                    });

                    workloadValues[j] += costs[i, j];
                }
            }
        }

        // Рассчитываем дисперсию нагрузки
        var avgWorkload = workloadValues.Average();
        var variance = workloadValues.Select(w => Math.Pow(w - avgWorkload, 2)).Average();

        return new OptimizationResult
        {
            IsOptimal = status == CpSolverStatus.Optimal,
            IsFeasible = true,
            Assignments = resultAssignments,
            ObjectiveValue = solver.ObjectiveValue,
            SolverTime = DateTime.UtcNow - startTime,
            WorkloadVariance = variance,
            TotalTravelTime = resultAssignments.Sum(a => a.EstimatedTime.TotalSeconds)
        };
    }

    public OptimizationResult OptimizeWithStreams(
        IEnumerable<TaskStream> streams,
        IEnumerable<Forklift> forklifts)
    {
        var streamList = streams.OrderBy(s => s.SequenceNumber).ToList();
        var allAssignments = new List<Assignment>();
        double totalObjective = 0;
        var startTime = DateTime.UtcNow;

        // Оптимизируем каждый поток последовательно
        foreach (var stream in streamList)
        {
            // Задания внутри потока уже отсортированы по весу (тяжёлые первыми)
            var tasks = stream.Tasks.Where(t => t.Status == DeliveryTaskStatus.Pending);

            var result = Optimize(tasks, forklifts, stream);
            if (result.IsFeasible)
            {
                allAssignments.AddRange(result.Assignments);
                totalObjective += result.ObjectiveValue;
            }
        }

        return new OptimizationResult
        {
            IsOptimal = true,
            IsFeasible = allAssignments.Any(),
            Assignments = allAssignments,
            ObjectiveValue = totalObjective,
            SolverTime = DateTime.UtcNow - startTime,
            TotalTravelTime = allAssignments.Sum(a => a.EstimatedTime.TotalSeconds)
        };
    }
}
