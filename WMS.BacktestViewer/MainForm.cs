using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.XtraGantt;
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
        private DataGridView _gridDecisions;
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
                new TreeListColumn { FieldName = "Name", Caption = "Работник / Задача", VisibleIndex = 0, Width = 220 },
                new TreeListColumn { FieldName = "TaskType", Caption = "Тип", VisibleIndex = 1, Width = 40 },
                new TreeListColumn { FieldName = "Weight", Caption = "кг", VisibleIndex = 2, Width = 45 },
                new TreeListColumn { FieldName = "DurationStr", Caption = "Время", VisibleIndex = 3, Width = 50 },
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

            // Tooltip с деталями при наведении
            gantt.ToolTipController = new DevExpress.Utils.ToolTipController();
            gantt.NodeCellStyle += (sender, e) =>
            {
                if (e.Node != null)
                {
                    var tooltip = e.Node.GetValue("Tooltip")?.ToString();
                    if (!string.IsNullOrEmpty(tooltip))
                        e.Node.SetValue("_tt", tooltip);
                }
            };

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

            var workerGroups = events
                .GroupBy(e => e.WorkerCode)
                .OrderBy(g => g.First().WorkerRole)
                .ThenBy(g => g.Key)
                .ToList();

            int nextId = 1;
            foreach (var wg in workerGroups)
            {
                var first = wg.First();
                var workerId = nextId++;
                var workerTasks = wg.OrderBy(e => e.StartTime).ToList();

                var workerStart = workerTasks.Min(e => e.StartTime);
                var workerEnd = workerTasks.Max(e => e.EndTime);
                var totalDur = workerTasks.Sum(e => e.DurationSec);
                var totalWeightKg = workerTasks.Sum(e => e.WeightKg) / 1000m;
                var role = first.WorkerRole ?? "?";
                var roleShort = role == "Forklift" ? "FK" : role == "Picker" ? "PK" : "??";

                dt.Rows.Add(
                    workerId,
                    DBNull.Value,
                    $"{roleShort} {first.WorkerName ?? first.WorkerCode}",
                    roleShort,
                    $"{totalWeightKg:F0}",
                    FormatDuration(totalDur),
                    $"{workerTasks.Count} задач",
                    workerStart,
                    workerEnd,
                    100.0,
                    $"{role}: {first.WorkerCode} ({first.WorkerName})\n" +
                    $"Задач: {workerTasks.Count}, Вес: {totalWeightKg:F1} кг\n" +
                    $"Время: {FormatDuration(totalDur)}\n" +
                    $"{workerStart:dd.MM HH:mm} — {workerEnd:dd.MM HH:mm}"
                );

                foreach (var task in workerTasks)
                {
                    var taskId = nextId++;
                    var weightKg = task.WeightKg / 1000m;
                    var typeChar = (task.TaskType ?? "").StartsWith("Repl") ? "R" : "D";
                    var fromZ = string.IsNullOrEmpty(task.FromZone) || task.FromZone == "?" ? "" : task.FromZone;
                    var toZ = string.IsNullOrEmpty(task.ToZone) || task.ToZone == "?" ? "" : task.ToZone;
                    var route = (!string.IsNullOrEmpty(fromZ) && !string.IsNullOrEmpty(toZ))
                        ? $"{fromZ}→{toZ}" : "";

                    // Короткое имя для дерева
                    var shortName = task.ProductName ?? task.ProductCode ?? "";
                    if (shortName.Length > 25) shortName = shortName.Substring(0, 22) + "...";

                    // Короткий текст на баре Ганта
                    var barText = $"{weightKg:F1}кг";
                    if (!string.IsNullOrEmpty(route)) barText = $"{route} {barText}";

                    // Полный tooltip
                    var tooltip = $"Тип: {task.TaskType}\n" +
                        $"Товар: {task.ProductName} ({task.ProductCode})\n" +
                        $"Вес: {weightKg:F1} кг, Кол-во: {task.Qty}\n" +
                        $"Ячейки: {task.FromBin} → {task.ToBin}\n" +
                        $"Время: {task.DurationSec:F0}с ({task.StartTime:HH:mm:ss} — {task.EndTime:HH:mm:ss})";
                    if (task.BufferLevel > 0)
                        tooltip += $"\nБуфер: {task.BufferLevel}";
                    if (task.TransitionSec > 0)
                        tooltip += $"\nПереход: {task.TransitionSec:F0}с";

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
                        tooltip
                    );
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

        private DataGridView CreateDecisionsGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                Font = new Font("Consolas", 8),
                RowTemplate = { Height = 20 },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(255, 250, 240)
                }
            };
            grid.Columns.AddRange(new DataGridViewColumn[]
            {
                new DataGridViewTextBoxColumn { Name = "Seq", HeaderText = "#", FillWeight = 25 },
                new DataGridViewTextBoxColumn { Name = "Day", HeaderText = "День", FillWeight = 40 },
                new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Решение", FillWeight = 60 },
                new DataGridViewTextBoxColumn { Name = "Worker", HeaderText = "Работник", FillWeight = 55 },
                new DataGridViewTextBoxColumn { Name = "Priority", HeaderText = "Приор.", FillWeight = 35 },
                new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "Сек", FillWeight = 30 },
                new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "кг", FillWeight = 35 },
                new DataGridViewTextBoxColumn { Name = "Buffer", HeaderText = "Буфер", FillWeight = 45 },
                new DataGridViewTextBoxColumn { Name = "Constraint", HeaderText = "Огранич.", FillWeight = 55 },
                new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "Причина", FillWeight = 160 },
            });
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

                // Лог решений
                FillDecisionsGrid(_gridDecisions, _decisions);

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

        private void FillDecisionsGrid(DataGridView grid, List<DecisionInfo> decisions)
        {
            grid.Rows.Clear();
            foreach (var d in decisions)
            {
                grid.Rows.Add(
                    d.DecisionSeq,
                    d.DayDate.ToString("dd.MM"),
                    d.DecisionType,
                    d.ChosenWorkerCode ?? "-",
                    d.ChosenTaskPriority > 0 ? d.ChosenTaskPriority.ToString("F0") : "-",
                    d.ChosenTaskDurationSec > 0 ? d.ChosenTaskDurationSec.ToString("F0") : "-",
                    d.ChosenTaskWeightKg > 0 ? (d.ChosenTaskWeightKg / 1000m).ToString("F1") : "-",
                    $"{d.BufferLevelBefore}→{d.BufferLevelAfter}/{d.BufferCapacity}",
                    d.ActiveConstraint ?? "none",
                    d.ReasonText
                );
            }

            foreach (DataGridViewRow row in grid.Rows)
            {
                var type = row.Cells["Type"].Value?.ToString() ?? "";
                if (type.StartsWith("skip"))
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 220, 220);
            }
        }
    }
}
