namespace WMS.BufferManagement.Layers.Tactical.Models;

/// <summary>
/// Результат оптимизации
/// </summary>
public class OptimizationResult
{
    public bool IsOptimal { get; init; }
    public bool IsFeasible { get; init; }
    public List<Assignment> Assignments { get; init; } = new();
    public double ObjectiveValue { get; init; }
    public TimeSpan SolverTime { get; init; }
    public double WorkloadVariance { get; init; }
    public double TotalTravelTime { get; init; }

    public static OptimizationResult Empty => new()
    {
        IsOptimal = false,
        IsFeasible = false
    };
}
