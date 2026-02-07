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
        private SplitContainer _splitMain;
        private SplitContainer _splitGantt;
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
                Height = 60,
                Padding = new Padding(10)
            };

            var lblWave = new Label
            {
                Text = "Волна:",
                Location = new Point(10, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 10)
            };

            _cmbWaves = new ComboBox
            {
                Location = new Point(70, 15),
                Width = 600,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10)
            };

            _btnLoad = new Button
            {
                Text = "Загрузить",
                Location = new Point(690, 13),
                Width = 120,
                Height = 30,
                Font = new Font("Segoe UI", 10)
            };
            _btnLoad.Click += async (s, e) => await LoadBacktestDataAsync();

            _lblSummary = new Label
            {
                Location = new Point(830, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 9)
            };

            topPanel.Controls.AddRange(new Control[] { lblWave, _cmbWaves, _btnLoad, _lblSummary });

            // === Статус бар ===
            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                Text = "Готов",
                Padding = new Padding(5),
                BackColor = Color.FromArgb(240, 240, 240),
                Font = new Font("Segoe UI", 9)
            };

            // === Главный сплиттер: Ганты (сверху) | Решения (снизу) ===
            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 550
            };

            // === Верхняя часть: два Ганта рядом ===
            _splitGantt = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 780
            };

            // Панель ФАКТ
            var factPanel = new Panel { Dock = DockStyle.Fill };
            var lblFact = new Label
            {
                Text = "ФАКТ (как было)",
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(200, 220, 255),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _ganttFact = CreateGanttControl();
            factPanel.Controls.Add(_ganttFact);
            factPanel.Controls.Add(lblFact);

            // Панель ОПТИМИЗАЦИЯ
            var optPanel = new Panel { Dock = DockStyle.Fill };
            var lblOpt = new Label
            {
                Text = "ОПТИМИЗАЦИЯ (как могло быть)",
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(200, 255, 220),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _ganttOptimized = CreateGanttControl();
            optPanel.Controls.Add(_ganttOptimized);
            optPanel.Controls.Add(lblOpt);

            _splitGantt.Panel1.Controls.Add(factPanel);
            _splitGantt.Panel2.Controls.Add(optPanel);

            // === Нижняя часть: лог решений ===
            var decisionsPanel = new Panel { Dock = DockStyle.Fill };
            var lblDecisions = new Label
            {
                Text = "ЛОГ РЕШЕНИЙ ОПТИМИЗАТОРА (ПОЧЕМУ)",
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(255, 240, 200),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _gridDecisions = CreateDecisionsGrid();
            decisionsPanel.Controls.Add(_gridDecisions);
            decisionsPanel.Controls.Add(lblDecisions);

            _splitMain.Panel1.Controls.Add(_splitGantt);
            _splitMain.Panel2.Controls.Add(decisionsPanel);

            this.Controls.Add(_splitMain);
            this.Controls.Add(topPanel);
            this.Controls.Add(_lblStatus);

            // Загрузить список волн при открытии
            this.Load += async (s, e) => await LoadWaveListAsync();
        }

        /// <summary>
        /// Создать DevExpress GanttControl с иерархией по работникам
        /// </summary>
        private GanttControl CreateGanttControl()
        {
            var gantt = new GanttControl
            {
                Dock = DockStyle.Fill
            };

            // Колонки TreeList (левая панель — дерево)
            gantt.Columns.AddRange(new TreeListColumn[]
            {
                new TreeListColumn { FieldName = "Name", Caption = "Работник / Задача", VisibleIndex = 0, Width = 180 },
                new TreeListColumn { FieldName = "TaskType", Caption = "Тип", VisibleIndex = 1, Width = 50 },
                new TreeListColumn { FieldName = "Route", Caption = "Маршрут", VisibleIndex = 2, Width = 60 },
                new TreeListColumn { FieldName = "Weight", Caption = "Вес,кг", VisibleIndex = 3, Width = 50 },
                new TreeListColumn { FieldName = "DurationStr", Caption = "Длит.", VisibleIndex = 4, Width = 50 },
                new TreeListColumn { FieldName = "BufferStr", Caption = "Буфер", VisibleIndex = 5, Width = 45 },
            });

            // Маппинг полей для Ганта
            gantt.TreeListMappings.KeyFieldName = "Id";
            gantt.TreeListMappings.ParentFieldName = "ParentId";

            gantt.ChartMappings.TextFieldName = "DisplayText";
            gantt.ChartMappings.StartDateFieldName = "StartDate";
            gantt.ChartMappings.FinishDateFieldName = "FinishDate";
            gantt.ChartMappings.ProgressFieldName = "Progress";

            // Настройки вида
            gantt.OptionsView.ShowBaseline = false;

            // Раскрывать все узлы
            gantt.OptionsBehavior.Editable = false;

            return gantt;
        }

        /// <summary>
        /// Подготовить данные для GanttControl — иерархия: Работник → Задачи
        /// </summary>
        private DataTable BuildGanttDataTable(List<GanttTask> events)
        {
            var dt = new DataTable();
            dt.Columns.Add("Id", typeof(int));
            dt.Columns.Add("ParentId", typeof(int));
            dt.Columns.Add("Name", typeof(string));
            dt.Columns.Add("TaskType", typeof(string));
            dt.Columns.Add("Route", typeof(string));
            dt.Columns.Add("Weight", typeof(string));
            dt.Columns.Add("DurationStr", typeof(string));
            dt.Columns.Add("BufferStr", typeof(string));
            dt.Columns.Add("DisplayText", typeof(string));
            dt.Columns.Add("StartDate", typeof(DateTime));
            dt.Columns.Add("FinishDate", typeof(DateTime));
            dt.Columns.Add("Progress", typeof(double));

            // Группировка по работникам
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

                // Работник = summary row (parent)
                var workerStart = workerTasks.Min(e => e.StartTime);
                var workerEnd = workerTasks.Max(e => e.EndTime);
                var totalDur = workerTasks.Sum(e => e.DurationSec);
                var role = first.WorkerRole ?? "?";
                var roleLetter = role.Length >= 3 ? role.Substring(0, 3) : role;

                dt.Rows.Add(
                    workerId,
                    DBNull.Value,  // нет родителя
                    $"[{roleLetter}] {first.WorkerCode} ({first.WorkerName})",
                    roleLetter,
                    "",
                    $"{workerTasks.Sum(e => e.WeightKg):F0}",
                    FormatDuration(totalDur),
                    "",
                    $"{first.WorkerCode}: {workerTasks.Count} задач, {FormatDuration(totalDur)}",
                    workerStart,
                    workerEnd,
                    100.0
                );

                // Задачи работника (children)
                foreach (var task in workerTasks)
                {
                    var taskId = nextId++;
                    var route = $"{task.FromZone}→{task.ToZone}";
                    var typeShort = task.TaskType?.Length >= 4
                        ? task.TaskType.Substring(0, 4) : (task.TaskType ?? "?");

                    dt.Rows.Add(
                        taskId,
                        workerId,  // родитель = работник
                        task.ProductName ?? task.ProductCode ?? task.TaskRef?.Substring(0, 8),
                        typeShort,
                        route,
                        $"{task.WeightKg:F0}",
                        $"{task.DurationSec:F0}с",
                        task.BufferLevel > 0 ? task.BufferLevel.ToString() : "",
                        $"{typeShort} {route} {task.WeightKg:F0}кг {task.DurationSec:F0}с",
                        task.StartTime,
                        task.EndTime > task.StartTime ? task.EndTime : task.StartTime.AddSeconds(Math.Max(task.DurationSec, 1)),
                        100.0
                    );
                }
            }

            return dt;
        }

        private static string FormatDuration(double totalSec)
        {
            if (totalSec < 60) return $"{totalSec:F0}с";
            if (totalSec < 3600) return $"{totalSec / 60:F1}м";
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
                Font = new Font("Consolas", 9),
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(255, 250, 240)
                }
            };
            grid.Columns.AddRange(new DataGridViewColumn[]
            {
                new DataGridViewTextBoxColumn { Name = "Seq", HeaderText = "#", Width = 30 },
                new DataGridViewTextBoxColumn { Name = "Day", HeaderText = "День", Width = 70 },
                new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Тип решения", Width = 80 },
                new DataGridViewTextBoxColumn { Name = "Worker", HeaderText = "Работник", Width = 70 },
                new DataGridViewTextBoxColumn { Name = "Priority", HeaderText = "Приоритет", Width = 60 },
                new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "Длит,с", Width = 50 },
                new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "Вес,кг", Width = 50 },
                new DataGridViewTextBoxColumn { Name = "Buffer", HeaderText = "Буфер", Width = 60 },
                new DataGridViewTextBoxColumn { Name = "Constraint", HeaderText = "Ограничение", Width = 80 },
                new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "Причина", Width = 200 },
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

                _lblSummary.Text = $"Факт: {factEvents.Count} палет | " +
                    $"Опт: {optEvents.Count} палет | " +
                    $"Решений: {_decisions.Count} | " +
                    $"Улучшение: {run.ImprovementPct:F1}% | " +
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
            // Подписка на отрисовку баров — цвет зависит от типа задачи
            gantt.CustomDrawTask += (sender, e) =>
            {
                if (e.Node == null || e.Node.ParentNode == null) return; // skip parent rows

                var taskType = e.Node.GetValue("TaskType")?.ToString() ?? "";
                if (taskType.StartsWith("Repl"))
                {
                    e.Appearance.BackColor = Color.FromArgb(100, 150, 255); // синий — replenishment
                }
                else if (taskType.StartsWith("Dist"))
                {
                    e.Appearance.BackColor = Color.FromArgb(80, 200, 120); // зелёный — distribution
                }
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
                    d.ChosenTaskWeightKg > 0 ? d.ChosenTaskWeightKg.ToString("F0") : "-",
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
