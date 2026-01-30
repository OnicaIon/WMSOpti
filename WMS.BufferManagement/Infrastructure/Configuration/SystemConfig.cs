namespace WMS.BufferManagement.Infrastructure.Configuration;

/// <summary>
/// Конфигурация системы
/// </summary>
public class SystemConfig
{
    public BufferConfig Buffer { get; set; } = new();
    public TimingConfig Timing { get; set; } = new();
    public WaveConfig Wave { get; set; } = new();
    public WorkersConfig Workers { get; set; } = new();
    public OptimizationConfig Optimization { get; set; } = new();
    public QueueingConfig Queueing { get; set; } = new();
    public SimulationConfig Simulation { get; set; } = new();
}

public class BufferConfig
{
    public int Capacity { get; set; } = 50;
    public double LowThreshold { get; set; } = 0.3;
    public double HighThreshold { get; set; } = 0.7;
    public double CriticalThreshold { get; set; } = 0.15;
    public double DeadBand { get; set; } = 0.05;
}

public class TimingConfig
{
    public int RealtimeCycleMs { get; set; } = 200;
    public int TacticalCycleMs { get; set; } = 2000;
    public int HistoricalCycleMs { get; set; } = 60000;
}

public class WaveConfig
{
    public int DurationMinutes { get; set; } = 15;
    public int SafetyMarginSeconds { get; set; } = 60;
    public int MaxPalletsPerWave { get; set; } = 30;
}

public class WorkersConfig
{
    public int ForkliftsCount { get; set; } = 3;
    public int PickersCount { get; set; } = 20;
}

public class OptimizationConfig
{
    public double WorkloadBalanceLambda { get; set; } = 0.3;
    public int MaxSolverTimeMs { get; set; } = 500;
    public bool WarmStartEnabled { get; set; } = true;
}

public class QueueingConfig
{
    public double OverloadThreshold { get; set; } = 0.85;
    public double CriticalThreshold { get; set; } = 0.95;
}

public class SimulationConfig
{
    public bool Enabled { get; set; } = true;
    public double SpeedMultiplier { get; set; } = 1.0;
    public int RandomSeed { get; set; } = 42;
}
