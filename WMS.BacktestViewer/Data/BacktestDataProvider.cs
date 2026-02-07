using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using WMS.BacktestViewer.Models;

namespace WMS.BacktestViewer.Data
{
    /// <summary>
    /// Провайдер данных бэктеста из TimescaleDB.
    /// Читает таблицы backtest_* для отображения в Ганте и UI.
    /// </summary>
    public class BacktestDataProvider
    {
        private readonly string _connectionString;

        public BacktestDataProvider(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Получить список всех бэктестов
        /// </summary>
        public async Task<List<BacktestRunInfo>> GetBacktestRunsAsync()
        {
            var result = new List<BacktestRunInfo>();
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                using (var cmd = new NpgsqlCommand(@"
                    SELECT id, wave_number, wave_date, wave_status, analyzed_at,
                           total_repl_groups, total_dist_groups, unique_workers,
                           actual_active_sec, optimized_duration_sec, improvement_pct,
                           original_wave_days, optimized_wave_days, days_saved
                    FROM backtest_runs
                    ORDER BY analyzed_at DESC
                    LIMIT 50", conn))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        result.Add(new BacktestRunInfo
                        {
                            Id = reader.GetGuid(0),
                            WaveNumber = reader.GetString(1),
                            WaveDate = reader.GetDateTime(2),
                            WaveStatus = reader.IsDBNull(3) ? null : reader.GetString(3),
                            AnalyzedAt = reader.GetDateTime(4),
                            TotalReplGroups = reader.GetInt32(5),
                            TotalDistGroups = reader.GetInt32(6),
                            UniqueWorkers = reader.GetInt32(7),
                            ActualActiveSec = reader.IsDBNull(8) ? 0 : (double)reader.GetDecimal(8),
                            OptimizedDurationSec = reader.IsDBNull(9) ? 0 : (double)reader.GetDecimal(9),
                            ImprovementPct = reader.IsDBNull(10) ? 0 : (double)reader.GetDecimal(10),
                            OriginalWaveDays = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                            OptimizedWaveDays = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                            DaysSaved = reader.IsDBNull(13) ? 0 : reader.GetInt32(13)
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Получить события для Ганта по run_id.
        /// timelineType: "fact", "optimized" или null (оба).
        /// </summary>
        public async Task<List<GanttTask>> GetScheduleEventsAsync(Guid runId, string timelineType = null)
        {
            var result = new List<GanttTask>();
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                var sql = @"SELECT timeline_type, worker_code, worker_name, worker_role,
                                task_ref, task_type, day_date,
                                start_time, end_time, duration_sec,
                                from_bin, to_bin, from_zone, to_zone,
                                product_code, product_name, weight_kg, qty,
                                sequence_number, buffer_level, duration_source, transition_sec
                            FROM backtest_schedule_events
                            WHERE run_id = @rid";

                if (timelineType != null)
                    sql += " AND timeline_type = @tt";
                sql += " ORDER BY timeline_type, worker_code, start_time";

                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("rid", runId);
                    if (timelineType != null)
                        cmd.Parameters.AddWithValue("tt", timelineType);

                    long id = 1;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new GanttTask
                            {
                                Id = id++,
                                TimelineType = reader.GetString(0),
                                WorkerCode = reader.GetString(1),
                                WorkerName = reader.IsDBNull(2) ? null : reader.GetString(2),
                                WorkerRole = reader.IsDBNull(3) ? null : reader.GetString(3),
                                TaskRef = reader.IsDBNull(4) ? null : reader.GetString(4),
                                TaskType = reader.IsDBNull(5) ? null : reader.GetString(5),
                                DayDate = reader.GetDateTime(6),
                                StartTime = reader.IsDBNull(7) ? DateTime.MinValue : reader.GetDateTime(7),
                                EndTime = reader.IsDBNull(8) ? DateTime.MinValue : reader.GetDateTime(8),
                                DurationSec = reader.IsDBNull(9) ? 0 : (double)reader.GetDecimal(9),
                                FromBin = reader.IsDBNull(10) ? null : reader.GetString(10),
                                ToBin = reader.IsDBNull(11) ? null : reader.GetString(11),
                                FromZone = reader.IsDBNull(12) ? null : reader.GetString(12),
                                ToZone = reader.IsDBNull(13) ? null : reader.GetString(13),
                                ProductCode = reader.IsDBNull(14) ? null : reader.GetString(14),
                                ProductName = reader.IsDBNull(15) ? null : reader.GetString(15),
                                WeightKg = reader.IsDBNull(16) ? 0 : reader.GetDecimal(16),
                                Qty = reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                                SequenceNumber = reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                                BufferLevel = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                                DurationSource = reader.IsDBNull(20) ? null : reader.GetString(20),
                                TransitionSec = reader.IsDBNull(21) ? 0 : (double)reader.GetDecimal(21)
                            });
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Получить лог решений оптимизатора
        /// </summary>
        public async Task<List<DecisionInfo>> GetDecisionLogAsync(Guid runId)
        {
            var result = new List<DecisionInfo>();
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                using (var cmd = new NpgsqlCommand(@"
                    SELECT decision_seq, day_date, decision_type,
                           task_ref, task_type, chosen_worker_code,
                           chosen_worker_remaining_sec, chosen_task_priority,
                           chosen_task_duration_sec, chosen_task_weight_kg,
                           buffer_level_before, buffer_level_after, buffer_capacity,
                           alt_workers_json::text, alt_tasks_json::text,
                           active_constraint, reason_text
                    FROM backtest_decision_log
                    WHERE run_id = @rid
                    ORDER BY decision_seq", conn))
                {
                    cmd.Parameters.AddWithValue("rid", runId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            result.Add(new DecisionInfo
                            {
                                DecisionSeq = reader.GetInt32(0),
                                DayDate = reader.GetDateTime(1),
                                DecisionType = reader.GetString(2),
                                TaskRef = reader.IsDBNull(3) ? null : reader.GetString(3),
                                TaskType = reader.IsDBNull(4) ? null : reader.GetString(4),
                                ChosenWorkerCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                                ChosenWorkerRemainingSec = reader.IsDBNull(6) ? 0 : (double)reader.GetDecimal(6),
                                ChosenTaskPriority = reader.IsDBNull(7) ? 0 : (double)reader.GetDecimal(7),
                                ChosenTaskDurationSec = reader.IsDBNull(8) ? 0 : (double)reader.GetDecimal(8),
                                ChosenTaskWeightKg = reader.IsDBNull(9) ? 0 : reader.GetDecimal(9),
                                BufferLevelBefore = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                                BufferLevelAfter = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                                BufferCapacity = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                                AltWorkersJson = reader.IsDBNull(13) ? null : reader.GetString(13),
                                AltTasksJson = reader.IsDBNull(14) ? null : reader.GetString(14),
                                ActiveConstraint = reader.IsDBNull(15) ? null : reader.GetString(15),
                                ReasonText = reader.IsDBNull(16) ? null : reader.GetString(16)
                            });
                        }
                    }
                }
            }
            return result;
        }
    }
}
