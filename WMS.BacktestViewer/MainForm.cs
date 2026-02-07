using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WMS.BacktestViewer.Data;
using WMS.BacktestViewer.Models;

// DevExpress — раскомментировать после добавления ссылок:
// using DevExpress.XtraGantt;
// using DevExpress.XtraTreeList;
// using DevExpress.XtraTreeList.Columns;
// using DevExpress.XtraEditors;

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

        // Гант-панели (будут заменены на DevExpress GanttControl)
        private DataGridView _gridFact;
        private DataGridView _gridOptimized;

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
                SplitterDistance = 500
            };

            // === Верхняя часть: два Ганта рядом ===
            _splitGantt = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 750
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
            _gridFact = CreateGanttGrid();
            factPanel.Controls.Add(_gridFact);
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
            _gridOptimized = CreateGanttGrid();
            optPanel.Controls.Add(_gridOptimized);
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
        /// Создать DataGridView для визуализации расписания (замена GanttControl)
        /// Когда DevExpress подключён — заменить на GanttControl
        /// </summary>
        private DataGridView CreateGanttGrid()
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
                    BackColor = Color.FromArgb(245, 245, 245)
                }
            };
            grid.Columns.AddRange(new DataGridViewColumn[]
            {
                new DataGridViewTextBoxColumn { Name = "Worker", HeaderText = "Работник", Width = 80 },
                new DataGridViewTextBoxColumn { Name = "Role", HeaderText = "Роль", Width = 60 },
                new DataGridViewTextBoxColumn { Name = "TaskType", HeaderText = "Тип", Width = 50 },
                new DataGridViewTextBoxColumn { Name = "Start", HeaderText = "Начало", Width = 80 },
                new DataGridViewTextBoxColumn { Name = "End", HeaderText = "Конец", Width = 80 },
                new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "Сек", Width = 40 },
                new DataGridViewTextBoxColumn { Name = "Route", HeaderText = "Маршрут", Width = 60 },
                new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "Вес,кг", Width = 50 },
                new DataGridViewTextBoxColumn { Name = "Buffer", HeaderText = "Буфер", Width = 45 },
                new DataGridViewTextBoxColumn { Name = "Transition", HeaderText = "Переход,с", Width = 55 },
                new DataGridViewTextBoxColumn { Name = "Seq", HeaderText = "#", Width = 30 },
                new DataGridViewTextBoxColumn { Name = "Product", HeaderText = "Товар", Width = 100 },
            });
            return grid;
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
                _cmbWaves.DisplayMember = null; // использует ToString()
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
            if (_cmbWaves.SelectedItem is not BacktestRunInfo run)
                return;

            try
            {
                _lblStatus.Text = $"Загрузка данных волны {run.WaveNumber}...";
                _btnLoad.Enabled = false;

                // Загружаем параллельно events и decisions
                var eventsTask = _dataProvider.GetScheduleEventsAsync(run.Id);
                var decisionsTask = _dataProvider.GetDecisionLogAsync(run.Id);

                await Task.WhenAll(eventsTask, decisionsTask);

                _allEvents = eventsTask.Result;
                _decisions = decisionsTask.Result;

                // Разделяем events по типу
                var factEvents = _allEvents.Where(e => e.TimelineType == "fact")
                    .OrderBy(e => e.WorkerCode).ThenBy(e => e.StartTime).ToList();
                var optEvents = _allEvents.Where(e => e.TimelineType == "optimized")
                    .OrderBy(e => e.WorkerCode).ThenBy(e => e.StartTime).ToList();

                // Заполняем гриды
                FillGanttGrid(_gridFact, factEvents);
                FillGanttGrid(_gridOptimized, optEvents);
                FillDecisionsGrid(_gridDecisions, _decisions);

                // Цвета по типу задачи
                ColorizeGrid(_gridFact, factEvents);
                ColorizeGrid(_gridOptimized, optEvents);

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

        private void FillGanttGrid(DataGridView grid, List<GanttTask> events)
        {
            grid.Rows.Clear();
            foreach (var e in events)
            {
                grid.Rows.Add(
                    e.WorkerCode,
                    e.WorkerRole?.Substring(0, Math.Min(3, e.WorkerRole?.Length ?? 0)),
                    e.TaskType?.Substring(0, Math.Min(4, e.TaskType?.Length ?? 0)),
                    e.StartTime.ToString("HH:mm:ss"),
                    e.EndTime.ToString("HH:mm:ss"),
                    e.DurationSec.ToString("F0"),
                    $"{e.FromZone}→{e.ToZone}",
                    e.WeightKg.ToString("F0"),
                    e.BufferLevel.ToString(),
                    e.TransitionSec.ToString("F0"),
                    e.SequenceNumber > 0 ? e.SequenceNumber.ToString() : "",
                    e.ProductName ?? e.ProductCode
                );
            }
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

            // Подсветить skip-решения
            foreach (DataGridViewRow row in grid.Rows)
            {
                var type = row.Cells["Type"].Value?.ToString() ?? "";
                if (type.StartsWith("skip"))
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 220, 220);
                }
            }
        }

        private void ColorizeGrid(DataGridView grid, List<GanttTask> events)
        {
            for (int i = 0; i < Math.Min(grid.Rows.Count, events.Count); i++)
            {
                var e = events[i];
                var row = grid.Rows[i];
                if (e.TaskType == "Replenishment")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(220, 235, 255); // голубой
                else if (e.TaskType == "Distribution")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(220, 255, 230); // зелёный
            }
        }
    }
}

// =============================================================================
// ЗАМЕЧАНИЕ: Ниже пример интеграции с DevExpress GanttControl 21.2
// После раскомментирования ссылок в .csproj, замените DataGridView на GanttControl:
//
// private GanttControl CreateDevExpressGantt(string title)
// {
//     var gantt = new GanttControl();
//     gantt.Dock = DockStyle.Fill;
//
//     // Колонки TreeList (левая панель)
//     gantt.Columns.Add(new TreeListColumn { FieldName = "WorkerCode", Caption = "Работник", Width = 100 });
//     gantt.Columns.Add(new TreeListColumn { FieldName = "TaskType", Caption = "Тип", Width = 60 });
//     gantt.Columns.Add(new TreeListColumn { FieldName = "Route", Caption = "Маршрут", Width = 60 });
//     gantt.Columns.Add(new TreeListColumn { FieldName = "WeightKg", Caption = "Вес", Width = 50 });
//
//     // Маппинг полей
//     gantt.TreeListMappings.KeyFieldName = "Id";
//     gantt.TreeListMappings.ParentFieldName = "ParentId";
//     gantt.ChartMappings.TextFieldName = "DisplayName";
//     gantt.ChartMappings.StartDateFieldName = "StartTime";
//     gantt.ChartMappings.FinishDateFieldName = "EndTime";
//     gantt.ChartMappings.ProgressFieldName = "Progress";
//
//     // Настройки вида
//     gantt.OptionsView.ShowBaseline = false;
//     gantt.OptionsView.ShowResources = true;
//
//     return gantt;
// }
//
// Биндинг данных:
//   var tasks = events.Select((e, i) => new {
//       Id = i + 1,
//       ParentId = (int?)null, // плоская структура, или группировка по WorkerCode
//       e.WorkerCode,
//       e.TaskType,
//       Route = $"{e.FromZone}→{e.ToZone}",
//       e.WeightKg,
//       e.StartTime,
//       e.EndTime,
//       e.DisplayName,
//       Progress = 100
//   }).ToList();
//   gantt.DataSource = tasks;
//
// Для иерархии по работникам:
//   ParentId = null для "ресурса" (работника)
//   ParentId = id работника для задачи
// =============================================================================
