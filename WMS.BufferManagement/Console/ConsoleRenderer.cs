using WMS.BufferManagement.Domain.Entities;
using WMS.BufferManagement.Layers.Realtime.StateMachine;
using WMS.BufferManagement.Simulation;

namespace WMS.BufferManagement.Console;

/// <summary>
/// ASCII визуализация состояния системы
/// </summary>
public class ConsoleRenderer
{
    private const int BarWidth = 40;

    public void Render(SimulationStats stats, BufferState state, IEnumerable<Forklift> forklifts, IEnumerable<Picker> pickers)
    {
        System.Console.Clear();
        System.Console.CursorVisible = false;

        RenderHeader();
        RenderBuffer(stats.BufferLevel, stats.BufferCount, (int)(stats.BufferLevel * 50 / 100), state);
        RenderForklifts(forklifts);
        RenderPickers(pickers, stats);
        RenderStats(stats);
        RenderCommands();
    }

    private void RenderHeader()
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
        System.Console.WriteLine("║     WMS Buffer Management System v1.0                            ║");
        System.Console.WriteLine("║     Streams: Sequential | Tasks: Sorted by Weight (Heavy First)  ║");
        System.Console.WriteLine("╠══════════════════════════════════════════════════════════════════╣");
        System.Console.ResetColor();
    }

    private void RenderBuffer(double level, int count, int capacity, BufferState state)
    {
        var stateColor = state switch
        {
            BufferState.Critical => ConsoleColor.Red,
            BufferState.Low => ConsoleColor.Yellow,
            BufferState.Overflow => ConsoleColor.Magenta,
            _ => ConsoleColor.Green
        };

        System.Console.Write("║  BUFFER: [");

        // Прогресс-бар
        var filled = (int)(level / 100 * BarWidth);
        System.Console.ForegroundColor = stateColor;
        System.Console.Write(new string('█', filled));
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.Write(new string('░', BarWidth - filled));
        System.Console.ResetColor();

        System.Console.Write($"] {level:F0}% ({count}/{capacity})  ");
        System.Console.ForegroundColor = stateColor;
        System.Console.Write($"State: {state,-8}");
        System.Console.ResetColor();
        System.Console.WriteLine("   ║");
    }

    private void RenderForklifts(IEnumerable<Forklift> forklifts)
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("╠══════════════════════════════════════════════════════════════════╣");
        System.Console.ResetColor();
        System.Console.WriteLine("║  FORKLIFTS:                                                      ║");

        foreach (var forklift in forklifts)
        {
            var stateIcon = forklift.State switch
            {
                ForkliftState.Idle => "⏸",
                ForkliftState.MovingToPallet => "→",
                ForkliftState.Loading => "⬆",
                ForkliftState.MovingToBuffer => "←",
                ForkliftState.Unloading => "⬇",
                _ => "✕"
            };

            var stateColor = forklift.State switch
            {
                ForkliftState.Idle => ConsoleColor.Gray,
                ForkliftState.Offline => ConsoleColor.DarkRed,
                _ => ConsoleColor.Green
            };

            System.Console.Write("║    ");
            System.Console.ForegroundColor = stateColor;
            System.Console.Write($"[{forklift.Name}] {stateIcon} {forklift.State,-15}");
            System.Console.ResetColor();

            if (forklift.CurrentTask != null)
            {
                var task = forklift.CurrentTask;
                System.Console.Write($" Pallet: {task.Pallet.Id} Weight: {task.WeightKg:F1}kg");
            }
            else
            {
                System.Console.Write($" Completed: {forklift.CompletedTasksCount}");
            }

            System.Console.WriteLine("".PadRight(10) + "║");
        }
    }

    private void RenderPickers(IEnumerable<Picker> pickers, SimulationStats stats)
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("╠══════════════════════════════════════════════════════════════════╣");
        System.Console.ResetColor();
        System.Console.WriteLine("║  PICKERS (top 5 active):                                         ║");

        var activePickers = pickers
            .Where(p => p.State == PickerState.Picking)
            .OrderByDescending(p => p.PalletConsumptionRatePerHour)
            .Take(5);

        System.Console.Write("║    ");
        foreach (var picker in activePickers)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.Write($"{picker.Name}: {picker.PalletConsumptionRatePerHour:F1}/h  ");
            System.Console.ResetColor();
        }
        System.Console.WriteLine("".PadRight(20) + "║");

        var totalRate = pickers.Where(p => p.State == PickerState.Picking).Sum(p => p.PalletConsumptionRatePerHour);
        System.Console.WriteLine($"║  Total consumption: {totalRate:F0} pal/h   Active: {stats.ActivePickers}               ║");
    }

    private void RenderStats(SimulationStats stats)
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("╠══════════════════════════════════════════════════════════════════╣");
        System.Console.ResetColor();
        System.Console.WriteLine("║  STATISTICS:                                                     ║");
        System.Console.WriteLine($"║    Storage: {stats.StorageCount} pallets   Delivered: {stats.TotalDelivered}   Consumed: {stats.TotalConsumed}".PadRight(67) + "║");
        System.Console.WriteLine($"║    Time: {stats.SimulationTime:HH:mm:ss}".PadRight(67) + "║");
    }

    private void RenderCommands()
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("╠══════════════════════════════════════════════════════════════════╣");
        System.Console.WriteLine("║  [S]tream [P]ause [R]esume [+]Speed [-]Speed [Q]uit              ║");
        System.Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        System.Console.ResetColor();
    }
}
