using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.XtraGantt;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Columns;
using DevExpress.XtraGrid.Views.Grid;
using DevExpress.XtraTreeList;
using DevExpress.XtraTreeList.Columns;
using WMS.BacktestViewer.Data;
using WMS.BacktestViewer.Models;

namespace WMS.BacktestViewer
{
    public partial class MainForm : Form
    {
        private const string ConnectionString =
            "Host=localhost;Port=5433;Database=wms_history;Username=wms;Password=wms_password";

        private readonly BacktestDataProvider _dataProvider;
        private List<BacktestRunInfo> _runs;
        private List<GanttTask> _allEvents;
        private List<DecisionInfo> _decisions;

        // UI controls
        private ComboBox _cmbWaves;
        private Button _btnLoad;
        private TabControl _tabGantt;
        private SplitContainer _splitMain;
        private GridControl _gridDecisions;
        private Label _lblStatus;
        private Label _lblSummary;

        // DevExpress Gantt
        private GanttControl _ganttFact;
        private GanttControl _ganttOptimized;

        public MainForm()
        {
            _dataProvider = new BacktestDataProvider(ConnectionString);
            InitializeComponent();
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = "WMS Backtest Viewer — Факт vs Оптимизация";
            this.Size = new Size(1600, 900);
            this.StartPosition = FormStartPosition.CenterScreen;

            // === Верхняя панель: выбор волны ===
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                Padding = new Padding(8)
            };

            var lblWave = new Label
            {
                Text = "Волна:",
                Location = new Point(10, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 10)
            };

            _cmbWaves = new ComboBox
            {
                Location = new Point(70, 12),
                Width = 550,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10)
            };

            _btnLoad = new Button
            {
                Text = "Загрузить",
                Location = new Point(635, 10),
                Width = 100,
                Height = 28,
                Font = new Font("Segoe UI", 9)
            };
            _btnLoad.Click += async (s, e) => await LoadBacktestDataAsync();

            _lblSummary = new Label
            {
                Location = new Point(750, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };

            topPanel.Controls.AddRange(new Control[] { lblWave, _cmbWaves, _btnLoad, _lblSummary });

            // === Статус бар ===
            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Text = "Готов",
                Padding = new Padding(5, 3, 0, 0),
                BackColor = Color.FromArgb(240, 240, 240),
                Font = new Font("Segoe UI", 8)
            };

            // === Главный сплиттер: Ганты (сверху) | Решения (снизу) ===
            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 600
            };

            // === Верхняя часть: TabControl с двумя вкладками ===
            _tabGantt = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10)
            };

            // Вкладка ФАКТ
            var tabFact = new TabPage("ФАКТ (как было)")
            {
                BackColor = Color.White
            };
            _ganttFact = CreateGanttControl();
            tabFact.Controls.Add(_ganttFact);

            // Вкладка ОПТИМИЗАЦИЯ
            var tabOpt = new TabPage("ОПТИМИЗАЦИЯ (как могло быть)")
            {
                BackColor = Color.White
            };
            _ganttOptimized = CreateGanttControl();
            tabOpt.Controls.Add(_ganttOptimized);

            _tabGantt.TabPages.Add(tabFact);
            _tabGantt.TabPages.Add(tabOpt);

            // === Нижняя часть: лог решений ===
            var decisionsPanel = new Panel { Dock = DockStyle.Fill };
            var lblDecisions = new Label
            {
                Text = "ЛОГ РЕШЕНИЙ ОПТИМИЗАТОРА",
                Dock = DockStyle.Top,
                Height = 22,
                BackColor = Color.FromArgb(255, 240, 200),
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _gridDecisions = CreateDecisionsGrid();
            decisionsPanel.Controls.Add(_gridDecisions);
            decisionsPanel.Controls.Add(lblDecisions);

            _splitMain.Panel1.Controls.Add(_tabGantt);
            _splitMain.Panel2.Controls.Add(decisionsPanel);

            this.Controls.Add(_splitMain);
            this.Controls.Add(topPanel);
            this.Controls.Add(_lblStatus);

            // Загрузить список волн при открытии
            this.Load += async (s, e) => await LoadWaveListAsync();
        }

        private GanttControl CreateGanttControl()
        {
            var gantt = new GanttControl
            {
                Dock = DockStyle.Fill
            };

            // Колонки TreeList (левая панель — дерево)
            gantt.Columns.AddRange(new TreeListColumn[]
            {
                new TreeListColumn { FieldName = "Name", Caption = "Работник / Задача", VisibleIndex = 0, Width = 300 },
                new TreeListColumn { FieldName = "Weight", Caption = "кг", VisibleIndex = 1, Width = 45 },
                new TreeListColumn { FieldName = "DurationStr", Caption = "Время", VisibleIndex = 2, Width = 50 },
            });

            // Маппинг полей для Ганта
            gantt.TreeListMappings.KeyFieldName = "Id";
            gantt.TreeListMappings.ParentFieldName = "ParentId";

            gantt.ChartMappings.TextFieldName = "DisplayText";
            gantt.ChartMappings.StartDateFieldName = "StartDate";
            gantt.ChartMappings.FinishDateFieldName = "FinishDate";
            gantt.ChartMappings.ProgressFieldName = "Progress";

            // Настройки вида
            gantt.OptionsBehavior.Editable = false;

            return gantt;
        }

        private DataTable BuildGanttDataTable(List<GanttTask> events)
        {
            var dt = new DataTable();
            dt.Columns.Add("Id", typeof(int));
            dt.Columns.Add("ParentId", typeof(int));
            dt.Columns.Add("Name", typeof(string));
            dt.Columns.Add("TaskType", typeof(string));
            dt.Columns.Add("Weight", typeof(string));
            dt.Columns.Add("DurationStr", typeof(string));
            dt.Columns.Add("DisplayText", typeof(string));
            dt.Columns.Add("StartDate", typeof(DateTime));
            dt.Columns.Add("FinishDate", typeof(DateTime));
            dt.Columns.Add("Progress", typeof(double));
            dt.Columns.Add("Tooltip", typeof(string));

            // 3-уровневая иерархия: Роль → Работник → Задачи
            var roleGroups = events
                .GroupBy(e => e.WorkerRole ?? "Unknown")
                .OrderBy(g => g.Key)
                .ToList();

            int nextId = 1;
            foreach (var rg in roleGroups)
            {
                var roleId = nextId++;
                var roleName = rg.Key == "Forklift" ? "Форклифты" : rg.Key == "Picker" ? "Пикеры" : rg.Key;
                var roleEvents = rg.ToList();
                var roleStart = roleEvents.Min(e => e.StartTime);
                var roleEnd = roleEvents.Max(e => e.EndTime);
                var roleTotalDur = roleEvents.Sum(e => e.DurationSec);
                var roleWeightKg = roleEvents.Sum(e => e.WeightKg) / 1000m;
                var roleWorkerCount = roleEvents.Select(e => e.WorkerCode).Distinct().Count();

                // Уровень 1: Роль
                dt.Rows.Add(
                    roleId,
                    DBNull.Value,
                    $"{roleName} ({roleWorkerCount} чел.)",
                    "",
                    $"{roleWeightKg:F0}",
                    FormatDuration(roleTotalDur),
                    $"{roleName}: {roleEvents.Count} задач",
                    roleStart,
                    roleEnd,
                    100.0,
                    ""
                );

                var workerGroups = rg
                    .GroupBy(e => e.WorkerCode)
                    .OrderBy(g => g.Key)
                    .ToList();

                foreach (var wg in workerGroups)
                {
                    var first = wg.First();
                    var workerId = nextId++;
                    var workerTasks = wg.OrderBy(e => e.StartTime).ToList();
                    var workerStart = workerTasks.Min(e => e.StartTime);
                    var workerEnd = workerTasks.Max(e => e.EndTime);
                    var totalDur = workerTasks.Sum(e => e.DurationSec);
                    var totalWeightKg = workerTasks.Sum(e => e.WeightKg) / 1000m;

                    // Уровень 2: Работник
                    dt.Rows.Add(
                        workerId,
                        roleId,
                        !string.IsNullOrWhiteSpace(first.WorkerName) ? first.WorkerName : first.WorkerCode,
                        "",
                        $"{totalWeightKg:F0}",
                        FormatDuration(totalDur),
                        $"{workerTasks.Count} задач",
                        workerStart,
                        workerEnd,
                        100.0,
                        ""
                    );

                    // Уровень 3: Задачи
                    foreach (var task in workerTasks)
                    {
                        var taskId = nextId++;
                        var weightKg = task.WeightKg / 1000m;
                        var typeChar = (task.TaskType ?? "").StartsWith("Repl") ? "R" : "D";
                        var fromZ = string.IsNullOrEmpty(task.FromZone) || task.FromZone == "?" ? "" : task.FromZone;
                        var toZ = string.IsNullOrEmpty(task.ToZone) || task.ToZone == "?" ? "" : task.ToZone;
                        var route = (!string.IsNullOrEmpty(fromZ) && !string.IsNullOrEmpty(toZ))
                            ? $"{fromZ}→{toZ}" : "";

                        var shortName = task.ProductName ?? task.ProductCode ?? "";
                        if (shortName.Length > 25) shortName = shortName.Substring(0, 22) + "...";

                        var barText = $"{weightKg:F1}кг";
                        if (!string.IsNullOrEmpty(route)) barText = $"{route} {barText}";

                        dt.Rows.Add(
                            taskId,
                            workerId,
                            shortName,
                            typeChar,
                            $"{weightKg:F1}",
                            $"{task.DurationSec:F0}с",
                            barText,
                            task.StartTime,
                            task.EndTime > task.StartTime ? task.EndTime : task.StartTime.AddSeconds(Math.Max(task.DurationSec, 1)),
                            100.0,
                            ""
                        );
                    }
                }
            }

            return dt;
        }

        private static string FormatDuration(double totalSec)
        {
            if (totalSec < 60) return $"{totalSec:F0}с";
            if (totalSec < 3600) return $"{totalSec / 60:F0}м";
            return $"{totalSec / 3600:F1}ч";
        }

        private GridControl CreateDecisionsGrid()
        {
            var grid = new GridControl { Dock = DockStyle.Fill };
            var view = new GridView(grid);
            grid.MainView = view;

            view.Columns.AddRange(new GridColumn[]
            {
                new GridColumn { FieldName = "Seq", Caption = "#", Width = 35, VisibleIndex = 0 },
                new GridColumn { FieldName = "Day", Caption = "День", Width = 45, VisibleIndex = 1 },
                new GridColumn { FieldName = "Type", Caption = "Решение", Width = 70, VisibleIndex = 2 },
                new GridColumn { FieldName = "Worker", Caption = "Работник", Width = 120, VisibleIndex = 3 },
                new GridColumn { FieldName = "Priority", Caption = "Приор.", Width = 50, VisibleIndex = 4 },
                new GridColumn { FieldName = "Duration", Caption = "Сек", Width = 40, VisibleIndex = 5 },
                new GridColumn { FieldName = "Weight", Caption = "кг", Width = 40, VisibleIndex = 6 },
                new GridColumn { FieldName = "Buffer", Caption = "Буфер", Width = 55, VisibleIndex = 7 },
                new GridColumn { FieldName = "Constraint", Caption = "Огранич.", Width = 65, VisibleIndex = 8 },
                new GridColumn { FieldName = "Reason", Caption = "Причина", Width = 300, VisibleIndex = 9 },
            });

            // Автофильтр в заголовках колонок
            view.OptionsView.ShowAutoFilterRow = true;
            view.OptionsView.ColumnAutoWidth = true;
            view.OptionsBehavior.Editable = false;
            view.OptionsSelection.MultiSelect = false;

            // Чередование строк
            view.OptionsView.EnableAppearanceEvenRow = true;
            view.OptionsView.EnableAppearanceOddRow = true;
            view.Appearance.EvenRow.BackColor = Color.FromArgb(255, 250, 240);

            // Подсветка skip-решений
            view.RowStyle += (sender, e) =>
            {
                var v = sender as GridView;
                if (v == null) return;
                var type = v.GetRowCellValue(e.RowHandle, "Type")?.ToString() ?? "";
                if (type.StartsWith("skip"))
                    e.Appearance.BackColor = Color.FromArgb(255, 220, 220);
            };

            return grid;
        }

        private async Task LoadWaveListAsync()
        {
            try
            {
                _lblStatus.Text = "Загрузка списка волн...";
                _runs = await _dataProvider.GetBacktestRunsAsync();
                _cmbWaves.DataSource = _runs;
                _cmbWaves.DisplayMember = null;
                _lblStatus.Text = $"Загружено {_runs.Count} бэктестов";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Не удалось подключиться к БД:\n{ex.Message}",
                    "Ошибка подключения", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task LoadBacktestDataAsync()
        {
            if (!(_cmbWaves.SelectedItem is BacktestRunInfo run))
                return;

            try
            {
                _lblStatus.Text = $"Загрузка данных волны {run.WaveNumber}...";
                _btnLoad.Enabled = false;

                var eventsTask = _dataProvider.GetScheduleEventsAsync(run.Id);
                var decisionsTask = _dataProvider.GetDecisionLogAsync(run.Id);
                await Task.WhenAll(eventsTask, decisionsTask);

                _allEvents = eventsTask.Result;
                _decisions = decisionsTask.Result;

                var factEvents = _allEvents.Where(e => e.TimelineType == "fact")
                    .OrderBy(e => e.WorkerCode).ThenBy(e => e.StartTime).ToList();
                var optEvents = _allEvents.Where(e => e.TimelineType == "optimized")
                    .OrderBy(e => e.WorkerCode).ThenBy(e => e.StartTime).ToList();

                // Заполняем Ганты
                _ganttFact.DataSource = BuildGanttDataTable(factEvents);
                _ganttFact.ExpandAll();

                _ganttOptimized.DataSource = BuildGanttDataTable(optEvents);
                _ganttOptimized.ExpandAll();

                // Раскрасить бары по типу задачи
                SetGanttAppearance(_ganttFact);
                SetGanttAppearance(_ganttOptimized);

                // Имена работников для лога решений (из events)
                var workerNameLookup = _allEvents
                    .Where(e => !string.IsNullOrWhiteSpace(e.WorkerCode))
                    .GroupBy(e => e.WorkerCode)
                    .ToDictionary(g => g.Key, g =>
                    {
                        var n = g.Select(e => e.WorkerName).FirstOrDefault(n2 => !string.IsNullOrWhiteSpace(n2));
                        return n ?? g.Key;
                    });

                // Лог решений
                FillDecisionsGrid(_gridDecisions, _decisions, workerNameLookup);

                _lblSummary.Text = $"Факт: {factEvents.Count} | " +
                    $"Опт: {optEvents.Count} | " +
                    $"Решений: {_decisions.Count} | " +
                    $"{run.ImprovementPct:F1}% | " +
                    $"Дней: {run.OriginalWaveDays}→{run.OptimizedWaveDays}";

                _lblStatus.Text = $"Загружено: {_allEvents.Count} events, {_decisions.Count} решений";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Ошибка: {ex.Message}";
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnLoad.Enabled = true;
            }
        }

        private void SetGanttAppearance(GanttControl gantt)
        {
            gantt.CustomDrawTask += (sender, e) =>
            {
                if (e.Node == null || e.Node.ParentNode == null) return;

                var taskType = e.Node.GetValue("TaskType")?.ToString() ?? "";
                if (taskType == "R")
                    e.Appearance.BackColor = Color.FromArgb(100, 150, 255);  // синий — replenishment
                else if (taskType == "D")
                    e.Appearance.BackColor = Color.FromArgb(80, 200, 120);   // зелёный — distribution
            };
        }

        private void FillDecisionsGrid(GridControl grid, List<DecisionInfo> decisions,
            Dictionary<string, string> workerNameLookup)
        {
            var dt = new DataTable();
            dt.Columns.Add("Seq", typeof(int));
            dt.Columns.Add("Day", typeof(string));
            dt.Columns.Add("Type", typeof(string));
            dt.Columns.Add("Worker", typeof(string));
            dt.Columns.Add("Priority", typeof(string));
            dt.Columns.Add("Duration", typeof(string));
            dt.Columns.Add("Weight", typeof(string));
            dt.Columns.Add("Buffer", typeof(string));
            dt.Columns.Add("Constraint", typeof(string));
            dt.Columns.Add("Reason", typeof(string));

            foreach (var d in decisions)
            {
                var workerDisplay = d.ChosenWorkerCode ?? "-";
                if (!string.IsNullOrEmpty(d.ChosenWorkerCode)
                    && workerNameLookup.TryGetValue(d.ChosenWorkerCode, out var name)
                    && !string.IsNullOrWhiteSpace(name))
                {
                    workerDisplay = name;
                }

                dt.Rows.Add(
                    d.DecisionSeq,
                    d.DayDate.ToString("dd.MM"),
                    d.DecisionType,
                    workerDisplay,
                    d.ChosenTaskPriority > 0 ? d.ChosenTaskPriority.ToString("F0") : "-",
                    d.ChosenTaskDurationSec > 0 ? d.ChosenTaskDurationSec.ToString("F0") : "-",
                    d.ChosenTaskWeightKg > 0 ? (d.ChosenTaskWeightKg / 1000m).ToString("F1") : "-",
                    $"{d.BufferLevelBefore}→{d.BufferLevelAfter}/{d.BufferCapacity}",
                    d.ActiveConstraint ?? "none",
                    d.ReasonText ?? ""
                );
            }

            grid.DataSource = dt;
        }
    }
}
