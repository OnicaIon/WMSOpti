using System.Text;

namespace WMS.BufferManagement.Services.Backtesting;

/// <summary>
/// Формирование отчётов бэктестирования.
/// Краткий отчёт → System.Console, подробный → файл.
/// </summary>
public static class BacktestReportWriter
{
    /// <summary>
    /// Вывести краткий отчёт в консоль
    /// </summary>
    public static void PrintSummary(BacktestResult result)
    {
        var w = 62; // ширина рамки

        System.Console.WriteLine();
        PrintBoxTop(w);
        PrintBoxLine(w, $"  Бэктест волны {result.WaveNumber}");
        PrintBoxSep(w);
        PrintBoxLine(w, $"  Дата волны:    {result.WaveDate:dd.MM.yyyy HH:mm}");
        PrintBoxLine(w, $"  Статус:        {result.WaveStatus}");
        PrintBoxLine(w, $"  Replenishment: {result.TotalReplenishmentTasks} действий");
        PrintBoxLine(w, $"  Distribution:  {result.TotalDistributionTasks} действий");
        PrintBoxLine(w, $"  Работников:    {result.UniqueWorkers}");
        PrintBoxSep(w);

        var wallClockStr = FormatDuration(result.ActualWallClockDuration);
        var activeStr = FormatDuration(result.ActualActiveDuration);
        var optStr = FormatDuration(result.OptimizedDuration);
        var diffStr = FormatDuration(result.ImprovementTime);

        PrintBoxLine(w, $"  ФАКТ (от-до):  {wallClockStr}");
        PrintBoxLine(w, $"  ФАКТ (работа): {activeStr}");
        PrintBoxLine(w, $"  ОПТИМИЗАЦИЯ:   {optStr}");

        if (result.ImprovementPercent > 0)
        {
            PrintBoxLine(w, $"  УЛУЧШЕНИЕ:     {result.ImprovementPercent:F1}% (-{diffStr})");
        }
        else
        {
            PrintBoxLine(w, $"  РАЗНИЦА:       {result.ImprovementPercent:F1}% ({diffStr})");
        }

        PrintBoxLine(w, $"  Optimizer:     {(result.OptimizerIsOptimal ? "Optimal" : "Feasible")}");
        PrintBoxSep(w);

        // Таблица работников
        PrintBoxLine(w, "  Работник          Факт      Опт       Разница");
        foreach (var wb in result.WorkerBreakdowns.OrderByDescending(b => b.ActualDuration))
        {
            var name = $"{wb.WorkerCode} {wb.WorkerName}";
            if (name.Length > 18) name = name[..18];
            var factStr = FormatDurationShort(wb.ActualDuration);
            var optWStr = FormatDurationShort(wb.OptimizedDuration);
            var sign = wb.ImprovementPercent > 0 ? "-" : "+";
            PrintBoxLine(w, $"  {name,-18} {factStr,-9} {optWStr,-9} {sign}{Math.Abs(wb.ImprovementPercent):F1}%");
        }

        PrintBoxSep(w);
        PrintBoxLine(w, $"  Источники оценки времени:");
        PrintBoxLine(w, $"    actual (из 1С):  {result.ActualDurationsUsed}");
        PrintBoxLine(w, $"    route_stats:     {result.RouteStatsUsed}");
        PrintBoxLine(w, $"    picker_product:  {result.PickerStatsUsed}");
        PrintBoxLine(w, $"    default (~{result.WaveMeanDurationSec:F0}с): {result.DefaultEstimatesUsed}");
        PrintBoxBottom(w);
        System.Console.WriteLine();
    }

    /// <summary>
    /// Записать подробный отчёт в файл
    /// </summary>
    public static async Task<string> WriteDetailedReportAsync(BacktestResult result, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var timestamp = result.AnalyzedAt.ToString("yyyyMMdd_HHmmss");
        var fileName = $"backtest_{result.WaveNumber}_{timestamp}.txt";
        var filePath = Path.Combine(outputDir, fileName);

        var sb = new StringBuilder();

        // Заголовок
        sb.AppendLine("================================================================================");
        sb.AppendLine($"  БЭКТЕСТ ВОЛНЫ ДИСТРИБЬЮЦИИ: {result.WaveNumber}");
        sb.AppendLine($"  Сформирован: {result.AnalyzedAt:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // Общая информация
        sb.AppendLine("--- ОБЩАЯ ИНФОРМАЦИЯ ---");
        sb.AppendLine($"  Номер волны:           {result.WaveNumber}");
        sb.AppendLine($"  Дата волны:            {result.WaveDate:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"  Статус:                {result.WaveStatus}");
        sb.AppendLine($"  Replenishment действий:{result.TotalReplenishmentTasks}");
        sb.AppendLine($"  Distribution действий: {result.TotalDistributionTasks}");
        sb.AppendLine($"  Всего действий:        {result.TotalActions}");
        sb.AppendLine($"  Уникальных работников: {result.UniqueWorkers}");
        sb.AppendLine();

        // Результаты сравнения
        sb.AppendLine("--- РЕЗУЛЬТАТЫ СРАВНЕНИЯ ---");
        sb.AppendLine($"  Фактическое начало:    {result.ActualStartTime:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"  Фактическое окончание: {result.ActualEndTime:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"  Факт (от-до):          {FormatDuration(result.ActualWallClockDuration)} (включая ночи/перерывы)");
        sb.AppendLine($"  Факт (работа):         {FormatDuration(result.ActualActiveDuration)} (только активное время)");
        sb.AppendLine($"  Оптимизированное:      {FormatDuration(result.OptimizedDuration)}");
        sb.AppendLine($"  Улучшение:             {result.ImprovementPercent:F1}% ({FormatDuration(result.ImprovementTime)})");
        sb.AppendLine($"  Optimizer:             {(result.OptimizerIsOptimal ? "Optimal solution" : "Feasible solution")}");
        sb.AppendLine();

        // Разбивка по работникам
        sb.AppendLine("--- РАЗБИВКА ПО РАБОТНИКАМ ---");
        sb.AppendLine($"  {"Код",-12} {"Имя",-20} {"Роль",-10} {"Факт задач",10} {"Опт задач",10} {"Факт время",12} {"Опт время",12} {"Разница",8}");
        sb.AppendLine($"  {new string('-', 96)}");

        foreach (var wb in result.WorkerBreakdowns.OrderByDescending(b => b.ActualDuration))
        {
            var name = wb.WorkerName.Length > 20 ? wb.WorkerName[..20] : wb.WorkerName;
            sb.AppendLine($"  {wb.WorkerCode,-12} {name,-20} {wb.Role,-10} {wb.ActualTasks,10} {wb.OptimizedTasks,10} {FormatDurationShort(wb.ActualDuration),12} {FormatDurationShort(wb.OptimizedDuration),12} {wb.ImprovementPercent:+0.0;-0.0}%");
        }
        sb.AppendLine();

        // Источники оценки
        sb.AppendLine("--- ИСТОЧНИКИ ОЦЕНКИ ВРЕМЕНИ ---");
        sb.AppendLine($"  actual (фактическое из 1С):      {result.ActualDurationsUsed} действий");
        sb.AppendLine($"  route_stats (маршруты из БД):    {result.RouteStatsUsed} действий");
        sb.AppendLine($"  picker_product (пикер+товар):    {result.PickerStatsUsed} действий");
        sb.AppendLine($"  default (среднее ~{result.WaveMeanDurationSec:F1}с):   {result.DefaultEstimatesUsed} действий");
        sb.AppendLine();

        // Детали заданий
        sb.AppendLine("--- ДЕТАЛИ ЗАДАНИЙ ---");
        sb.AppendLine($"  {"#",-4} {"Тип",-14} {"Работник",-12} {"Опт.работник",-12} {"Из ячейки",-18} {"В ячейку",-18} {"Зона",-6} {"Товар",-12} {"Вес,кг",8} {"Факт,с",8} {"Опт,с",8} {"Источник",-14}");
        sb.AppendLine($"  {new string('-', 150)}");

        int num = 1;
        foreach (var td in result.TaskDetails)
        {
            var fromBin = td.FromBin.Length > 16 ? td.FromBin[..16] : td.FromBin;
            var toBin = td.ToBin.Length > 16 ? td.ToBin[..16] : td.ToBin;
            var product = td.ProductCode.Length > 10 ? td.ProductCode[..10] : td.ProductCode;
            var actualSec = td.ActualDurationSec.HasValue ? $"{td.ActualDurationSec:F0}" : "n/a";
            var route = $"{td.FromZone}→{td.ToZone}";
            var optWorker = td.OptimizedWorkerCode ?? td.WorkerCode;

            sb.AppendLine($"  {num,-4} {td.TaskType,-14} {td.WorkerCode,-12} {optWorker,-12} {fromBin,-18} {toBin,-18} {route,-6} {product,-12} {td.WeightKg,8:F1} {actualSec,8} {td.OptimizedDurationSec,8:F0} {td.DurationSource,-14}");
            num++;
        }

        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("  Конец отчёта");
        sb.AppendLine("================================================================================");

        await File.WriteAllTextAsync(filePath, sb.ToString());
        return filePath;
    }

    // ============================================================================
    // Вспомогательные методы форматирования
    // ============================================================================

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}ч {ts.Minutes:D2}м {ts.Seconds:D2}с";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}м {ts.Seconds:D2}с";
        return $"{ts.TotalSeconds:F0}с";
    }

    private static string FormatDurationShort(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}ч{ts.Minutes:D2}м";
        return $"{(int)ts.TotalMinutes}м{ts.Seconds:D2}с";
    }

    private static void PrintBoxTop(int w)
    {
        System.Console.WriteLine($"\u2554{new string('\u2550', w)}\u2557");
    }

    private static void PrintBoxBottom(int w)
    {
        System.Console.WriteLine($"\u255a{new string('\u2550', w)}\u255d");
    }

    private static void PrintBoxSep(int w)
    {
        System.Console.WriteLine($"\u2560{new string('\u2550', w)}\u2563");
    }

    private static void PrintBoxLine(int w, string text)
    {
        if (text.Length > w - 2)
            text = text[..(w - 2)];
        System.Console.WriteLine($"\u2551{text.PadRight(w)}\u2551");
    }
}
