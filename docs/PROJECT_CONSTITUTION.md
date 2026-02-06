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

### Формула улучшения
```
ActualActive = сумма per-day merged intervals
OptimizedDuration = сумма per-day makespans
ImprovementPercent = (ActualActive - OptimizedDuration) / ActualActive × 100%
```

## 3. Константы

| Константа | Значение | Описание |
|-----------|----------|----------|
| PickerTransitionTimeSec | 60 | Пауза между палетами у пикера |
| DefaultRouteDurationSec | 120 | Fallback если нет статистики (используется редко) |
| Bin code формат | `01A-01-02-03` | Зона = символы после "01" (например "A") |

## 4. Ограничения и правила

- НЕ удалять исторические данные из БД (NO TRUNCATE/DELETE)
- Бэктест = READ-ONLY, не модифицирует данные
- CLI: основной workflow через интерактивное меню (без --flags)
- Один BSL файл для 1С: `docs/1C_HTTPService_Complete.bsl`
- Подробный отчёт → файл (reports/), краткий → консоль
