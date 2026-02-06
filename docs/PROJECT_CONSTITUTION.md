# WMS Buffer Management — Конституция проекта

Дизайн-документ с архитектурными решениями и правилами.
Пополняется по мере развития проекта.

## 1. Архитектура волн дистрибьюции

### Структура задач
- Оба типа задач (replenishment и distribution) = документы `rtWMSProductSelection`
- Связь с волной: `sgExtendedDocumentsStatuses.Operation = Task.Operation`
- **Replenishment** (форклифт): `Task.PrevTask = UNDEFINED`
- **Distribution** (пикер): `Task.PrevTask ≠ UNDEFINED` (PrevTask → связанный replenishment)

### Статусы волны
```
NotStarted → ReplInProgress → WaitDist → DistInProgress → Completed
```

### Важно для 1С
- `UNDEFINED ≠ NULL ≠ EmptyRef` — для проверки незаполненных ссылок всегда `= UNDEFINED`
- Template codes: 029 = Forklift (replenishment), 031 = Picker (distribution)

## 2. Бэктестирование

### Принцип: оптимизация по дням
Волна может длиться несколько дней. Оптимизация выполняется **отдельно для каждого календарного дня**:
- Задачи не переносятся между днями
- Работники ограничены фактическим временем на ЭТОЙ волне в ЭТОТ день
- Работник мог параллельно работать на других волнах → его capacity = его факт на этой волне

### Двухстадийный scheduling
```
Стадия 1: Replenishment → форклифты (LPT greedy + capacity)
  - Сортировка задач по длительности ↓ (Longest Processing Time first)
  - Назначение форклифту с наибольшим оставшимся capacity
  - Фиксация finish time каждого replenishment

Стадия 2: Distribution → пикеры (EFF + precedence + пауза)
  - Distribution доступна только ПОСЛЕ завершения связанного replenishment
  - Назначение пикеру с наименьшим текущим finish time (Earliest Finish First)
  - 1 минута паузы между палетами (переход, сканирование)
```

### Единица оптимизации
Задача (task group / rtWMSProductSelection), НЕ отдельное действие.
Задача целиком назначается одному работнику.

### Активное время (interval merging)
Фактическое время = merge всех [startedAt, completedAt] интервалов.
Автоматически исключает: ночное время, перерывы, простои.

### Источники оценки времени (приоритет)
1. `actual` — фактическая длительность из 1С (DurationSec, или CompletedAt - StartedAt)
2. `picker_product` — средняя длительность пикер+товар из БД
3. `route_stats` — средняя длительность маршрута (зона→зона) из БД (минимум 3 поездки)
4. `default` — среднее по волне (waveMeanDurationSec)

### Масштабирование длительностей task groups
Действия в 1С записываются с перекрывающимися timestamps (параллельные задачи).
`ComputeGroupSpanSec` даёт wall-clock span одной группы, но несколько групп одного
работника могут работать одновременно → сумма spans >> реальное время.

**Решение:** пропорциональное масштабирование:
```
rawTotal = сумма ComputeGroupSpanSec по группам работника (одного типа)
mergedInterval = ComputeWorkerDayCapacitySec (merged intervals работника)
scaleFactor = mergedInterval / rawTotal
scaledDuration[group] = rawSpan[group] × scaleFactor
→ сумма scaled по работнику = mergedInterval ✓
```

Масштабирование раздельное для repl и dist (forklift capacity → repl groups, picker capacity → dist groups).

### Формула улучшения
```
ActualActive = сумма per-day merged intervals
OptimizedDuration = сумма per-day makespans
ImprovementPercent = (ActualActive - OptimizedDuration) / ActualActive × 100%
```

## 3. Константы

| Константа | Значение | Описание |
|-----------|----------|----------|
| PickerTransitionTimeSec | 0 (было 60) | Пауза между палетами у пикера (временно выключена) |
| DefaultRouteDurationSec | 120 | Fallback если нет статистики (используется редко) |
| Bin code формат | `01A-01-02-03` | Зона = символы после "01" (например "A") |

## 4. Ограничения и правила

- НЕ удалять исторические данные из БД (NO TRUNCATE/DELETE)
- Бэктест = READ-ONLY, не модифицирует данные
- CLI: основной workflow через интерактивное меню (без --flags)
- Один BSL файл для 1С: `docs/1C_HTTPService_Complete.bsl`
- Подробный отчёт → файл (reports/), краткий → консоль

## 5. Кросс-дневная оптимизация

### Принцип: палеты из пула, работники из расписания
- ВСЕ палеты волны = общий пул кандидатов с самого начала
- Нет понятия "будущих дней" — есть пул палет и расписание работников
- Работники ограничены сменами (из фактических данных: кто работал, сколько)
- Буфер ограничен ёмкостью (BufferConfig.Capacity, по умолчанию 50)
- Каждый день заполняется из пула до исчерпания capacity работников

### Приоритет палет
```
score = weightKg * 1000 - durationSec * 10 - zoneDistance
```
1. Вес ↓ (тяжёлые первыми — основа палеты, стабильность)
2. Длительность ↑ (быстрые — не тормозить старт)
3. Расстояние ↑ (ближние первыми, дальние потом)

### Буфер (producer-consumer)
```
Форклифты (producer) → [БУФЕР ≤ capacity] → Пикеры (consumer)
```
- Repl завершён → палета в буфер (+1)
- Dist завершён → палета из буфера (-1)
- Чередование назначений (interleaved): 1 repl, 1 dist, повторить
- Буфер переносится между днями

### Связка repl→dist
- Distribution начинается только ПОСЛЕ завершения своего replenishment
- PrevTaskRef связывает dist → repl
- Repl и dist могут быть в разных днях

### Ключевые метрики
- OriginalWaveDays vs OptimizedWaveDays → DaysSaved
- Per-day: OriginalPallets vs OptimizedPallets, BufferLevel
- Формула времени: без изменений (ActualActive vs OptimizedDuration)
