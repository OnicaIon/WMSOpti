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

        // Общая информация
        PrintBoxLine(w, $"  Дата волны:    {result.WaveDate:dd.MM.yyyy HH:mm}");
        PrintBoxLine(w, $"  Статус:        {result.WaveStatus}");
        PrintBoxLine(w, $"  Действий:      repl {result.TotalReplenishmentTasks}, dist {result.TotalDistributionTasks}");
        PrintBoxLine(w, $"  Палет (групп): repl {result.TotalReplGroups}, dist {result.TotalDistGroups}");
        PrintBoxLine(w, $"  Работников:    {result.UniqueWorkers}");
        PrintBoxSep(w);

        // Главные метрики — дни и палеты
        var totalFactP = result.DayBreakdowns.Sum(d => d.OriginalReplGroups + d.OriginalDistGroups);
        var totalOptP = result.DayBreakdowns.Sum(d => d.OptimizedReplGroups + d.OptimizedDistGroups);
        var firstDay = result.DayBreakdowns.Where(d => d.OriginalReplGroups + d.OriginalDistGroups > 0).Min(d => d.Date);
        var lastDay = result.DayBreakdowns.Where(d => d.OriginalReplGroups + d.OriginalDistGroups > 0).Max(d => d.Date);

        PrintBoxLine(w, $"  ФАКТ:          {result.OriginalWaveDays} дн. ({firstDay:dd.MM} — {lastDay:dd.MM}), {totalFactP} палет");
        PrintBoxLine(w, $"  ОПТИМИЗАЦИЯ:   {result.OptimizedWaveDays} дн., {totalOptP} палет");

        if (result.DaysSaved > 0)
            PrintBoxLine(w, $"  УЛУЧШЕНИЕ:     {result.ImprovementPercent:F1}% (-{result.DaysSaved} дн.)");

        PrintBoxLine(w, $"  Метод:         Кросс-дневной пул + буфер");
        PrintBoxLine(w, $"  Буфер:         {result.BufferCapacity} палет (макс)");

        // Время переходов между палетами
        if (result.PickerTransitionSec > 0 || result.ForkliftTransitionSec > 0)
        {
            PrintBoxLine(w, $"  Переходы:      пикер {result.PickerTransitionSec:F0}с, форклифт {result.ForkliftTransitionSec:F0}с");
        }
        PrintBoxSep(w);

        // Таблица по дням — только палеты, работники, буфер (БЕЗ часов)
        if (result.DayBreakdowns.Any())
        {
            PrintBoxLine(w, "  День     Работн  ФактП  ОптП   +/-  Буфер");
            foreach (var db in result.DayBreakdowns)
            {
                var origP = db.OriginalReplGroups + db.OriginalDistGroups;
                var optP = db.OptimizedReplGroups + db.OptimizedDistGroups;
                var delta = db.AdditionalPallets;
                var deltaStr = delta >= 0 ? $"+{delta}" : $"{delta}";
                var workers = $"{db.ForkliftWorkers}+{db.PickerWorkers}";
                var dayMark = origP == 0 && optP > 0 ? "*" : " "; // * = виртуальный день
                PrintBoxLine(w, $" {dayMark}{db.Date:dd.MM}   {workers,6}  {origP,5}  {optP,4}  {deltaStr,4}  ->{db.BufferLevelEnd,2}");
            }
            PrintBoxLine(w, $"  Итого          {totalFactP,5}  {totalOptP,4}");
            PrintBoxSep(w);
        }

        // Таблица работников
        PrintBoxLine(w, "  Работник           Роль  Факт      Опт       %");
        foreach (var wb in result.WorkerBreakdowns.OrderByDescending(b => b.ActualDuration))
        {
            var name = $"{wb.WorkerCode} {wb.WorkerName}";
            if (name.Length > 20) name = name[..20];
            var roleShort = wb.Role switch
            {
                "Forklift" => "Ф",
                "Picker" => "П",
                _ => "?"
            };
            var factStr = $"{wb.ActualTasks}з/{FormatDurationShort(wb.ActualDuration)}";
            var optStr = $"{wb.OptimizedTasks}з/{FormatDurationShort(wb.OptimizedDuration)}";
            var sign = wb.ImprovementPercent > 0 ? "-" : "+";
            PrintBoxLine(w, $"  {name,-20} {roleShort}  {factStr,-11} {optStr,-11} {sign}{Math.Abs(wb.ImprovementPercent):F0}%");
        }

        PrintBoxSep(w);
        PrintBoxLine(w, $"  Источники оценки:");
        PrintBoxLine(w, $"    actual: {result.ActualDurationsUsed}  default: {result.DefaultEstimatesUsed}  route: {result.RouteStatsUsed}  picker: {result.PickerStatsUsed}");
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
        sb.AppendLine($"  Палет (групп):         repl {result.TotalReplGroups}, dist {result.TotalDistGroups}");
        sb.AppendLine($"  Уникальных работников: {result.UniqueWorkers}");
        sb.AppendLine();

        // Результаты сравнения
        var totalFactP = result.DayBreakdowns.Sum(d => d.OriginalReplGroups + d.OriginalDistGroups);
        var totalOptP = result.DayBreakdowns.Sum(d => d.OptimizedReplGroups + d.OptimizedDistGroups);

        sb.AppendLine("--- РЕЗУЛЬТАТЫ СРАВНЕНИЯ ---");
        sb.AppendLine($"  Факт:                  {result.OriginalWaveDays} дн., {totalFactP} палет");
        sb.AppendLine($"  Факт период:           {result.ActualStartTime:dd.MM.yyyy HH:mm} — {result.ActualEndTime:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"  Оптимизация:           {result.OptimizedWaveDays} дн., {totalOptP} палет");
        sb.AppendLine($"  Улучшение:             {result.ImprovementPercent:F1}% ({(result.DaysSaved > 0 ? $"-{result.DaysSaved}" : "0")} дн.)");
        sb.AppendLine($"  Метод:                 Кросс-дневной пул + буфер");
        sb.AppendLine($"  Буфер:                 {result.BufferCapacity} палет (макс)");
        if (result.PickerTransitionSec > 0 || result.ForkliftTransitionSec > 0)
        {
            sb.AppendLine($"  Переход (пикер):       {result.PickerTransitionSec:F1}с ({result.PickerTransitionCount} наблюдений)");
            sb.AppendLine($"  Переход (форклифт):    {result.ForkliftTransitionSec:F1}с ({result.ForkliftTransitionCount} наблюдений)");
        }
        sb.AppendLine();

        // Разбивка по дням — палеты и работники (БЕЗ часов)
        if (result.DayBreakdowns.Any())
        {
            sb.AppendLine("--- РАЗБИВКА ПО ДНЯМ ---");
            sb.AppendLine($"  {"Дата",-12} {"Работн",6} {"ФактП",6} {"ОптП",5} {"+/-",5} {"Буфер",6}");
            sb.AppendLine($"  {new string('-', 50)}");

            foreach (var db in result.DayBreakdowns)
            {
                var workers = $"{db.ForkliftWorkers}+{db.PickerWorkers}";
                var origP = db.OriginalReplGroups + db.OriginalDistGroups;
                var optP = db.OptimizedReplGroups + db.OptimizedDistGroups;
                var delta = db.AdditionalPallets;
                var deltaStr = delta >= 0 ? $"+{delta}" : $"{delta}";
                var dayMark = origP == 0 && optP > 0 ? "*" : " ";
                sb.AppendLine($" {dayMark}{db.Date:dd.MM.yyyy}   {workers,6} {origP,6} {optP,5} {deltaStr,5} ->{db.BufferLevelEnd,3}");
            }
            sb.AppendLine($"  {new string('-', 50)}");
            sb.AppendLine($"  {"ИТОГО",-12} {"",6} {totalFactP,6} {totalOptP,5}");
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
