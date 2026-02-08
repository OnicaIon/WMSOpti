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

        PrintBoxLine(w, $"  ФАКТ (от-до):  {wallClockStr}");
        PrintBoxLine(w, $"  ФАКТ (работа): {activeStr} (сумма ежедневн.)");
        PrintBoxLine(w, $"  ОПТИМИЗАЦИЯ:   {optStr} (сумма makespan)");
        PrintBoxLine(w, $"  Метод:         Кросс-дневной пул + буфер");

        // Основная метрика: сокращение дней
        if (result.DaysSaved > 0)
        {
            PrintBoxLine(w, $"  ВОЛНА:         {result.OriginalWaveDays} дн. -> {result.OptimizedWaveDays} дн. (-{result.DaysSaved} дн.)");
            PrintBoxLine(w, $"  УЛУЧШЕНИЕ:     {result.ImprovementPercent:F1}% по дням");
        }
        else
        {
            PrintBoxLine(w, $"  ВОЛНА:         {result.OriginalWaveDays} дн. -> {result.OptimizedWaveDays} дн.");
        }
        PrintBoxLine(w, $"  БУФЕР:         {result.BufferCapacity} палет (макс)");

        // Время переходов между палетами (из исторических данных БД)
        if (result.PickerTransitionSec > 0 || result.ForkliftTransitionSec > 0)
        {
            PrintBoxLine(w, $"  ПЕРЕХОДЫ:      пикер {result.PickerTransitionSec:F1}с ({result.PickerTransitionCount} набл.)");
            PrintBoxLine(w, $"                 форклифт {result.ForkliftTransitionSec:F1}с ({result.ForkliftTransitionCount} набл.)");
        }
        PrintBoxSep(w);

        // Таблица по дням — палеты и буфер
        if (result.DayBreakdowns.Any())
        {
            PrintBoxLine(w, "  День       ФактП ОптП  +/-  Буф Работн  Факт    Опт");
            foreach (var db in result.DayBreakdowns)
            {
                var origP = db.OriginalReplGroups + db.OriginalDistGroups;
                var optP = db.OptimizedReplGroups + db.OptimizedDistGroups;
                var delta = db.AdditionalPallets;
                var deltaStr = delta >= 0 ? $"+{delta}" : $"{delta}";
                var workers = $"{db.ForkliftWorkers}+{db.PickerWorkers}";
                var factStr = FormatDurationShort(db.ActualActiveDuration);
                var optStr2 = FormatDurationShort(db.OptimizedMakespan);
                PrintBoxLine(w, $"  {db.Date:dd.MM} {origP,5} {optP,4} {deltaStr,4} ->{db.BufferLevelEnd,2} {workers,6}  {factStr,-7} {optStr2,-7}");
            }
            var totalOrigP = result.DayBreakdowns.Sum(d => d.OriginalReplGroups + d.OriginalDistGroups);
            var totalOptP = result.DayBreakdowns.Sum(d => d.OptimizedReplGroups + d.OptimizedDistGroups);
            PrintBoxLine(w, $"  Итого {totalOrigP,5} {totalOptP,4}");
            PrintBoxSep(w);
        }

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
        sb.AppendLine($"  Факт (работа):         {FormatDuration(result.ActualActiveDuration)} (сумма ежедневного активного)");
        sb.AppendLine($"  Оптимизированное:      {FormatDuration(result.OptimizedDuration)} (сумма ежедневного makespan)");
        sb.AppendLine($"  Метод:                 Кросс-дневной пул + буфер");
        sb.AppendLine($"  Буфер:                 {result.BufferCapacity} палет (макс)");
        sb.AppendLine($"  Волна:                 {result.OriginalWaveDays} дн. факт -> {result.OptimizedWaveDays} дн. опт ({(result.DaysSaved > 0 ? $"-{result.DaysSaved}" : "0")} дн.)");
        sb.AppendLine($"  Улучшение:             {result.ImprovementPercent:F1}% по дням");
        sb.AppendLine($"  Палеты (repl/dist):    {result.TotalReplGroups}/{result.TotalDistGroups}");
        if (result.PickerTransitionSec > 0 || result.ForkliftTransitionSec > 0)
        {
            sb.AppendLine($"  Переход (пикер):       {result.PickerTransitionSec:F1}с медиана ({result.PickerTransitionCount} наблюдений из БД)");
            sb.AppendLine($"  Переход (форклифт):    {result.ForkliftTransitionSec:F1}с медиана ({result.ForkliftTransitionCount} наблюдений из БД)");
        }
        sb.AppendLine();

        // Разбивка по дням — палеты + буфер + время
        if (result.DayBreakdowns.Any())
        {
            sb.AppendLine("--- РАЗБИВКА ПО ДНЯМ ---");
            sb.AppendLine($"  {"Дата",-12} {"Ф+П",5} {"ФактП",6} {"ОптП",5} {"+/-",5} {"Буф",4} {"Факт(работа)",14} {"Оптимизация",14} {"Разница",8}");
            sb.AppendLine($"  {new string('-', 85)}");

            foreach (var db in result.DayBreakdowns)
            {
                var workers = $"{db.ForkliftWorkers}+{db.PickerWorkers}";
                var origP = db.OriginalReplGroups + db.OriginalDistGroups;
                var optP = db.OptimizedReplGroups + db.OptimizedDistGroups;
                var delta = db.AdditionalPallets;
                var deltaStr = delta >= 0 ? $"+{delta}" : $"{delta}";
                var sign = db.ImprovementPercent > 0 ? "-" : "+";
                sb.AppendLine($"  {db.Date:dd.MM.yyyy}   {workers,5} {origP,6} {optP,5} {deltaStr,5} ->{db.BufferLevelEnd,2} {FormatDurationShort(db.ActualActiveDuration),14} {FormatDurationShort(db.OptimizedMakespan),14} {sign}{Math.Abs(db.ImprovementPercent):F1}%");
            }

            var totalOrigP = result.DayBreakdowns.Sum(d => d.OriginalReplGroups + d.OriginalDistGroups);
            var totalOptP = result.DayBreakdowns.Sum(d => d.OptimizedReplGroups + d.OptimizedDistGroups);
            var totalActual = TimeSpan.FromSeconds(result.DayBreakdowns.Sum(d => d.ActualActiveDuration.TotalSeconds));
            var totalOpt = TimeSpan.FromSeconds(result.DayBreakdowns.Sum(d => d.OptimizedMakespan.TotalSeconds));
            sb.AppendLine($"  {new string('-', 85)}");
            sb.AppendLine($"  {"ИТОГО",-12} {"",5} {totalOrigP,6} {totalOptP,5} {"",5} {"",4} {FormatDurationShort(totalActual),14} {FormatDurationShort(totalOpt),14} {result.ImprovementPercent:F1}%");
            sb.AppendLine();
        }

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

        // === ФАКТИЧЕСКИЙ ЛОГ (по времени) ===
        sb.AppendLine("--- ФАКТИЧЕСКИЙ ЛОГ ПАЛЕТ (по времени) ---");
        sb.AppendLine($"  {"#",-4} {"Тип",-5} {"Откуда",-16} {"Куда",-16} {"Работник",-18} {"Начало",-17} {"Конец",-17} {"Факт",6} {"Д",2} {"Вес",7} {"Связь (кто и сколько)"}");
        sb.AppendLine($"  {new string('-', 155)}");

        int num = 1;
        foreach (var td in result.TaskDetails)
        {
            var typeShort = td.TaskType == "Replenishment" ? "Repl" : "Dist";
            var workerStr = FormatWorkerShort(td.WorkerCode, td.WorkerName);
            var fromBin = td.FromBin.Length > 14 ? td.FromBin[..14] : td.FromBin;
            var toBin = td.ToBin.Length > 14 ? td.ToBin[..14] : td.ToBin;
            var startStr = td.StartedAt.HasValue ? td.StartedAt.Value.ToString("dd.MM HH:mm:ss") : "n/a";
            var endStr = td.CompletedAt.HasValue ? td.CompletedAt.Value.ToString("dd.MM HH:mm:ss") : "n/a";
            var durStr = td.ActualDurationSec.HasValue ? FormatSecShort(td.ActualDurationSec.Value) : "n/a";

            // Связанная задача
            var linkedStr = "";
            if (td.LinkedWorkerCode != null)
            {
                var linkedType = td.TaskType == "Replenishment" ? "dist" : "repl";
                var linkedWorker = FormatWorkerShort(td.LinkedWorkerCode, td.LinkedWorkerName ?? "");
                var linkedDur = td.LinkedActualDurationSec.HasValue
                    ? FormatSecShort(td.LinkedActualDurationSec.Value) : "?";
                linkedStr = $"{linkedType}: {linkedWorker} {linkedDur}";
            }

            sb.AppendLine($"  {num,-4} {typeShort,-5} {fromBin,-16} {toBin,-16} {workerStr,-18} {startStr,-17} {endStr,-17} {durStr,6} {td.ActionCount,2} {td.TotalWeightKg,7:F1} {linkedStr}");
            num++;
        }
        sb.AppendLine();

        // === ОПТИМИЗИРОВАННЫЙ ПЛАН ===
        sb.AppendLine("--- ОПТИМИЗИРОВАННЫЙ ПЛАН ПАЛЕТ ---");
        sb.AppendLine($"  {"#",-4} {"Тип",-5} {"Откуда",-16} {"Куда",-16} {"Факт работник",-18} {"→Опт работник",-18} {"Факт",6} {"→Опт",6} {"Связь: факт→опт"}");
        sb.AppendLine($"  {new string('-', 140)}");

        num = 1;
        foreach (var td in result.TaskDetails)
        {
            var typeShort = td.TaskType == "Replenishment" ? "Repl" : "Dist";
            var fromBin = td.FromBin.Length > 14 ? td.FromBin[..14] : td.FromBin;
            var toBin = td.ToBin.Length > 14 ? td.ToBin[..14] : td.ToBin;
            var workerFact = FormatWorkerShort(td.WorkerCode, td.WorkerName);
            var workerOpt = FormatWorkerShort(td.OptimizedWorkerCode ?? td.WorkerCode, "");
            var durFact = td.ActualDurationSec.HasValue ? FormatSecShort(td.ActualDurationSec.Value) : "n/a";
            var durOpt = FormatSecShort(td.OptimizedDurationSec);

            // Связанная задача: факт→опт
            var linkedStr = "";
            if (td.LinkedWorkerCode != null)
            {
                var linkedType = td.TaskType == "Replenishment" ? "dist" : "repl";
                var lFactW = FormatWorkerShort(td.LinkedWorkerCode, "");
                var lOptW = FormatWorkerShort(td.LinkedOptWorkerCode ?? td.LinkedWorkerCode, "");
                var lFactD = td.LinkedActualDurationSec.HasValue
                    ? FormatSecShort(td.LinkedActualDurationSec.Value) : "?";
                var lOptD = td.LinkedOptDurationSec.HasValue
                    ? FormatSecShort(td.LinkedOptDurationSec.Value) : "?";
                linkedStr = $"{linkedType}: {lFactW} {lFactD}→{lOptW} {lOptD}";
            }

            sb.AppendLine($"  {num,-4} {typeShort,-5} {fromBin,-16} {toBin,-16} {workerFact,-18} {workerOpt,-18} {durFact,6} {durOpt,6} {linkedStr}");
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

    private static string FormatWorkerShort(string code, string name)
    {
        if (string.IsNullOrEmpty(code)) return "—";
        // Берём последние 3 цифры кода + фамилия
        var shortCode = code.Length > 3 ? code[^3..] : code;
        if (!string.IsNullOrEmpty(name))
        {
            var surname = name.Split(' ')[0];
            if (surname.Length > 10) surname = surname[..10];
            return $"{shortCode} {surname}";
        }
        return shortCode;
    }

    private static string FormatSecShort(double sec)
    {
        if (sec >= 3600)
            return $"{sec / 3600:F1}ч";
        if (sec >= 60)
            return $"{sec / 60:F0}м{(int)(sec % 60):D2}с";
        return $"{sec:F0}с";
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
