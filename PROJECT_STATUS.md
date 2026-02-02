# WMS Buffer Management - Статус проекта

**Последнее обновление:** 2026-02-02

---

## 1. ВЫПОЛНЕНО

### 1.1 Архитектура системы

```
WMS.BufferManagement/
├── Domain/                          # Доменные сущности
│   ├── Entities/                    # Pallet, Picker, Forklift, BufferZone, Task
│   ├── Events/                      # События системы
│   └── Interfaces/                  # Контракты
│
├── Layers/
│   ├── Realtime/                    # Реактивный слой (100-500ms)
│   │   ├── BufferControl/           # HysteresisController - управление буфером
│   │   ├── Dispatcher/              # ForkliftDispatcher - распределение задач
│   │   └── StateMachine/            # BufferStateMachine - FSM состояний
│   │
│   ├── Tactical/                    # Тактический слой (секунды)
│   │   ├── PalletAssignmentOptimizer.cs  # Заглушка под OR-Tools
│   │   └── WaveManager.cs           # Управление волнами
│   │
│   └── Historical/                  # Исторический слой (offline)
│       ├── DataCollection/          # MetricsStore - сбор метрик
│       ├── Persistence/             # TimescaleDB репозиторий
│       │   ├── TimescaleDbRepository.cs  # 1200+ строк, все CRUD операции
│       │   └── Models/              # TaskRecord, WorkerRecord, etc.
│       └── Prediction/              # ML модели
│           ├── MlTrainer.cs         # Обучение моделей
│           └── PickerSpeedPredictor.cs
│
├── Infrastructure/
│   ├── WmsIntegration/              # Интеграция с WMS 1C
│   │   ├── Wms1CClient.cs           # HTTP клиент к 1C API
│   │   ├── WmsDataSyncService.cs    # Фоновая синхронизация
│   │   └── RealTimeDataProvider.cs  # Провайдер данных реального времени
│   ├── EventBus/                    # InMemoryEventBus
│   └── Configuration/               # Классы конфигурации
│
├── Services/
│   ├── BufferManagementService.cs   # Главный цикл управления буфером
│   └── AggregationService.cs        # Агрегация метрик
│
├── Tools/                           # CLI инструменты
│   ├── SyncCommand.cs               # --sync-* команды
│   └── TrainModelsCommand.cs        # --train-ml команда
│
└── Simulation/                      # Симулятор для тестирования
```

### 1.2 База данных TimescaleDB

**Схема таблиц:**

```sql
-- Основная таблица задач (hypertable)
CREATE TABLE tasks (
    id UUID PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    pallet_id TEXT,
    product_type TEXT,
    weight_kg DECIMAL(10,3),
    weight_category TEXT,
    qty DECIMAL(10,3),
    worker_id TEXT,                -- ID работника (пикер/карщик)
    worker_name TEXT,
    worker_role TEXT,              -- 'Picker' или 'Forklift'
    template_code TEXT,
    template_name TEXT,
    task_basis_number TEXT,        -- Номер задания (группировка строк)
    from_zone TEXT,
    from_slot TEXT,                -- Код ячейки источника (01I-07-052-1)
    to_zone TEXT,
    to_slot TEXT,                  -- Код ячейки назначения
    distance_meters DECIMAL(10,2),
    status TEXT,
    duration_sec DECIMAL(10,2),
    failure_reason TEXT
);

-- Статистика работников
CREATE TABLE workers (
    id TEXT PRIMARY KEY,
    name TEXT,
    role TEXT,                     -- 'Picker' или 'Forklift'
    avg_speed DECIMAL(10,4),       -- Средняя скорость
    tasks_completed INTEGER,
    total_duration_sec DECIMAL(12,2),
    last_active_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ
);

-- Статистика маршрутов (для карщиков)
CREATE TABLE route_statistics (
    id SERIAL PRIMARY KEY,
    from_zone TEXT NOT NULL,
    to_zone TEXT NOT NULL,
    avg_duration_sec DECIMAL(10,2),
    median_duration_sec DECIMAL(10,2),
    p25_duration_sec DECIMAL(10,2),
    p75_duration_sec DECIMAL(10,2),
    min_duration_sec DECIMAL(10,2),
    max_duration_sec DECIMAL(10,2),
    sample_count INTEGER,
    updated_at TIMESTAMPTZ
);

-- Зоны склада
CREATE TABLE zones (
    code TEXT PRIMARY KEY,
    name TEXT,
    zone_type TEXT,               -- 'Storage', 'Packing', 'Buffer'
    is_buffer BOOLEAN DEFAULT FALSE,
    synced_at TIMESTAMPTZ
);

-- Ячейки склада
CREATE TABLE cells (
    code TEXT PRIMARY KEY,        -- '01I-07-052-1'
    zone_code TEXT,               -- 'I'
    name TEXT,
    row_num TEXT,
    position TEXT,
    level TEXT,
    cell_type TEXT,
    is_buffer BOOLEAN DEFAULT FALSE,
    synced_at TIMESTAMPTZ
);

-- Продукты
CREATE TABLE products (
    code TEXT PRIMARY KEY,
    sku TEXT,
    name TEXT,
    weight_kg DECIMAL(10,3),
    volume_m3 DECIMAL(10,6),
    weight_category TEXT,
    category_code TEXT,
    category_name TEXT,
    synced_at TIMESTAMPTZ
);
```

**Текущие данные:**
- `tasks`: **1,597,152** записей
- `workers`: 68 работников (пикеры + карщики)
- `cells`: 64 буферных ячейки (зона "I")
- `route_statistics`: 8 уникальных маршрутов

### 1.3 Синхронизация с WMS 1C

**Реализованные эндпоинты:**

| Эндпоинт | Метод | Описание |
|----------|-------|----------|
| `/tasks?after={id}&limit=N` | GET | Инкрементальная загрузка задач |
| `/workers` | GET | Список работников |
| `/zones` | GET | Справочник зон |
| `/cells?zone={code}` | GET | Ячейки по зоне |
| `/products?after={id}` | GET | Справочник продуктов |
| `/health` | GET | Проверка доступности |

**CLI команды синхронизации:**

```bash
# Синхронизация задач (инкрементально)
dotnet run -- --sync-tasks

# Полная перезагрузка задач
dotnet run -- --truncate-tasks --sync-tasks

# Синхронизация справочников
dotnet run -- --sync-zones --sync-cells --sync-products

# Всё вместе
dotnet run -- --sync-all
```

### 1.4 ML модели (ML.NET FastTree)

**Модель 1: Picker Duration** (`picker_model_2026-02-02.zip`)

```
Задача: Предсказать время выполнения задания пикером

Входные features:
- worker_id (категориальный) → one-hot encoding
- lines_count (float)        → количество строк в задании
- total_qty (float)          → общее количество товаров
- hour_of_day (float)        → час дня (0-23)
- day_of_week (float)        → день недели (0-6)

Target: total_duration_sec (float) → суммарное время всех строк задания

Группировка: по task_basis_number (номер задания)

Результаты на тестовых данных (10,498 образцов):
┌─────────────────────────────┬────────────┬─────────────┐
│ Метод                       │ MAE (сек)  │ Улучшение   │
├─────────────────────────────┼────────────┼─────────────┤
│ Глобальное среднее          │     312.22 │ baseline    │
│ Среднее по работнику        │     268.05 │      14.2%  │
│ ML FastTree                 │     187.05 │      40.1%  │
└─────────────────────────────┴────────────┴─────────────┘
```

**Модель 2: Forklift Duration** (`forklift_model_2026-02-02.zip`)

```
Задача: Предсказать время маршрута карщика

Входные features:
- worker_id (категориальный)
- from_zone (категориальный)
- to_zone (категориальный)
- weight_kg (float)
- hour_of_day (float)
- day_of_week (float)

Target: duration_sec (float)

Результаты на тестовых данных (10,553 образцов):
┌─────────────────────────────┬────────────┬─────────────┐
│ Метод                       │ MAE (сек)  │ Улучшение   │
├─────────────────────────────┼────────────┼─────────────┤
│ Глобальное среднее          │      21.17 │ baseline    │
│ Среднее по маршруту         │      20.82 │       1.7%  │
│ ML FastTree                 │      20.26 │       4.3%  │
└─────────────────────────────┴────────────┴─────────────┘

Примечание: маршруты карщиков короткие (~23 сек) и стабильные,
поэтому ML даёт небольшое улучшение.
```

**Файлы моделей:**
```
data/models/
├── picker_model_2026-02-02.zip      # 2.1 MB
├── forklift_model_2026-02-02.zip    # 1.8 MB
└── metrics.json                      # Метрики обучения
```

### 1.5 CLI интерфейс

```bash
# Справка
dotnet run -- --help

# Синхронизация
dotnet run -- --sync-tasks          # Задачи (инкрементально)
dotnet run -- --sync-zones          # Зоны
dotnet run -- --sync-cells          # Ячейки
dotnet run -- --sync-products       # Продукты
dotnet run -- --sync-all            # Всё
dotnet run -- --truncate-tasks      # Очистить задачи перед sync

# Статистика
dotnet run -- --calc                # Всё пересчитать
dotnet run -- --calc-workers        # Только работники
dotnet run -- --calc-routes         # Только маршруты
dotnet run -- --calc-picker-product # Связь пикер-товар

# ML обучение
dotnet run -- --train-ml            # Обучить модели

# Полный сервис
dotnet run                          # Запуск BufferManagementService
```

### 1.6 Гистерезис-контроллер буфера

```csharp
// HysteresisController.cs
Параметры:
- LowThreshold = 30%      // Ниже → состояние Low
- HighThreshold = 70%     // Выше → состояние Overflow
- CriticalThreshold = 15% // Ниже → состояние Critical
- DeadBand = 5%           // Зона нечувствительности

Состояния:
- Normal (30-70%)    → поддерживать текущий темп
- Low (15-30%)       → активировать доп. карщика
- Critical (<15%)    → все карщики, макс. приоритет
- Overflow (>70%)    → снизить интенсивность
```

### 1.7 Презентация

Файл: `docs/WMS_Buffer_Optimization.pptx` (14 слайдов)

Содержание:
1. Титульный
2. Текущая ситуация (схема потока)
3. Почему это важно (потери)
4. 3 уровня оптимизации
5. ML-модели для прогнозирования
6. **Умное назначение задач пикерам** (кто лучше справится)
7. **Аналитика по товарам** (сложность, типичные количества)
8. OR-Tools оптимизация (карщики + пикеры)
9. Цикл оптимизации
10. **Пример: карщики** (балансировка нагрузки)
11. **Пример: пикеры** (назначение по эффективности)
12. Ожидаемые результаты
13. План внедрения
14. Резюме

---

## 2. В ПРОЦЕССЕ

| Задача | Статус | Примечание |
|--------|--------|------------|
| Синхронизация данных | ✅ Завершено | 1.6M записей загружено |
| ML обучение | ✅ Завершено | Picker MAE=187s, Forklift MAE=20s |

---

## 3. ОСТАЛОСЬ СДЕЛАТЬ

### 3.1 Умное назначение задач пикерам

**Задача:** Назначать задачи на сборку тем пикерам, которые выполнят их быстрее

```
Критерии выбора пикера для задачи:

1. Эффективность пикера для данного товара
   - ML модель учитывает связку (пикер, товар) → скорость
   - Из истории: кто лучше справляется с этим типом товара

2. Текущая занятость пикера
   - Свободен сейчас → приоритет
   - Скоро освободится (< 2 мин) → можно назначить
   - Занят надолго → пропустить

3. Расположение пикера
   - Ближе к нужной ячейке буфера → меньше время подхода

4. Балансировка нагрузки
   - Равномерное распределение задач в волне
   - Не перегружать одного пикера
```

**Данные для решения:**
- Таблица `picker_product_stats` — статистика пикер↔товар
- ML модель picker_model — прогноз времени по (пикер, товар, объём)
- Реальное время: текущая задача пикера, прогноз окончания

**Файлы для реализации:**
- `Layers/Tactical/PickerAssignmentOptimizer.cs` — новый класс
- `Layers/Realtime/PickerStatusTracker.cs` — отслеживание занятости

### 3.2 Статистика по товарам (Product Analytics)

**Задача:** Понять "сложность" каждого товара для планирования

```
Метрики по товару (независимо от пикера):

1. Среднее время распределения
   - avg_distribution_time_sec — сколько в среднем занимает распределение
   - Позволяет оценить сложность товара

2. Стандартные количества
   - typical_qty_per_task — типичное количество в задании
   - min_qty, max_qty, median_qty — диапазон
   - Для планирования объёма работы

3. Вариативность времени
   - std_dev_time — насколько стабильно время
   - Высокая вариативность = сложный товар

4. Частота появления
   - tasks_per_day — сколько раз в день встречается
   - Приоритет для оптимизации частых товаров
```

**SQL для расчёта:**
```sql
CREATE TABLE product_statistics (
    product_code TEXT PRIMARY KEY,
    product_name TEXT,
    avg_distribution_time_sec DECIMAL(10,2),
    median_distribution_time_sec DECIMAL(10,2),
    std_dev_time_sec DECIMAL(10,2),
    typical_qty DECIMAL(10,2),
    min_qty DECIMAL(10,2),
    max_qty DECIMAL(10,2),
    tasks_count INTEGER,
    tasks_per_day DECIMAL(10,2),
    avg_weight_kg DECIMAL(10,3),
    updated_at TIMESTAMPTZ
);
```

**Использование:**
- Прогноз времени волны: сумма (кол-во товара × время на единицу)
- Выявление "проблемных" товаров с высокой вариативностью
- Планирование буфера: частые товары ближе к выходу

**Файлы для реализации:**
- `Layers/Historical/Persistence/ProductStatisticsRepository.cs`
- CLI: `--calc-product-stats`

### 3.3 OR-Tools оптимизация (Tactical Layer)

**Задача:** Минимизация общего времени волны (makespan)

```
Оптимизируем ДВА назначения одновременно:

1. Карщик → Палета → Ячейка буфера
   - Какой карщик везёт какую палету
   - В какую ячейку буфера
   - В каком порядке

2. Пикер → Задание на сборку
   - Какой пикер берёт какое задание
   - С учётом его эффективности для данного товара
   - С учётом занятости и времени освобождения

Входные данные:
- N заданий на сборку с прогнозом времени для КАЖДОГО пикера
- M палет для доставки с прогнозом времени для КАЖДОГО карщика
- Текущее состояние: кто чем занят, когда освободится

Ограничения:
- Тяжёлые палеты → нижние ячейки
- Приоритетные заказы → первыми
- Балансировка нагрузки между работниками
- Пикер не может взять задачу пока занят
```

**Файлы для реализации:**
- `Layers/Tactical/PalletAssignmentOptimizer.cs` — сейчас заглушка
- `Layers/Tactical/PickerAssignmentOptimizer.cs` — новый
- Нужно: интеграция Google.OrTools CP-SAT solver

### 3.4 Интеграция ML моделей в реальном времени

**Задача:** Использовать обученные модели для прогнозирования

```csharp
// Нужно реализовать:
public class MlPredictor
{
    private PredictionEngine<PickerTaskData, PickerPrediction> _pickerEngine;
    private PredictionEngine<ForkliftTaskData, ForkliftPrediction> _forkliftEngine;

    public float PredictPickerDuration(string workerId, int linesCount, decimal totalQty);
    public float PredictForkliftDuration(string workerId, string fromZone, string toZone, decimal weightKg);
}
```

**Файлы:**
- `Layers/Historical/Prediction/MlPredictor.cs` — новый класс
- Интеграция в `BufferManagementService.cs`

### 3.5 Обратная связь WMS → Создание заданий

**Задача:** При Critical состоянии создавать задания в WMS

```csharp
// В BufferManagementService при Critical:
if (_controller.CurrentState == BufferState.Critical)
{
    var palletsToRequest = CalculatePalletsNeeded();
    foreach (var pallet in palletsToRequest)
    {
        await _wmsClient.CreateTaskAsync(new CreateTaskRequest
        {
            PalletId = pallet.Id,
            FromZone = "STORAGE",
            ToZone = "BUFFER",
            Priority = TaskPriority.Critical
        });
    }
}
```

**Нужно:**
- Реализовать `POST /tasks` в `Wms1CClient.cs`
- Добавить логику выбора палет для подачи

### 3.6 Мониторинг и алерты

**Задача:** Уведомления при критических ситуациях

- Telegram/Slack интеграция при Critical состоянии
- Dashboard с метриками (Grafana + TimescaleDB)
- Логирование в структурированном формате (Serilog)

### 3.7 Тестирование

- Unit тесты для ML предикторов
- Integration тесты для WMS синхронизации
- Load тесты для BufferManagementService

### 3.8 Документация API

- OpenAPI/Swagger для внутреннего API
- Документация интеграции с 1C

---

## 4. КОНФИГУРАЦИЯ

**appsettings.json:**

```json
{
  "Historical": {
    "ConnectionString": "Host=localhost;Port=5433;Database=wms_history;Username=wms;Password=wms_password"
  },
  "Wms1C": {
    "BaseUrl": "http://192.168.1.100:8080/wms/hs/buffer-api/v1",
    "TimeoutSeconds": 30
  },
  "Buffer": {
    "Capacity": 64,
    "LowThreshold": 0.3,
    "HighThreshold": 0.7,
    "CriticalThreshold": 0.15,
    "DeadBand": 0.05
  },
  "MlModels": {
    "Path": "data/models"
  }
}
```

---

## 5. ЗАВИСИМОСТИ (NuGet)

```xml
<PackageReference Include="Microsoft.ML" Version="3.0.1" />
<PackageReference Include="Microsoft.ML.FastTree" Version="3.0.1" />
<PackageReference Include="Google.OrTools" Version="9.11.4210" />
<PackageReference Include="Npgsql" Version="8.0.5" />
<PackageReference Include="Stateless" Version="5.16.0" />
<PackageReference Include="NRules" Version="1.0.1" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.2" />
```

---

## 6. ИСТОРИЯ ИЗМЕНЕНИЙ

| Дата | Изменение |
|------|-----------|
| 2026-02-02 | ML модели обучены (Picker MAE=187s, Forklift MAE=20s) |
| 2026-02-02 | Презентация создана (docs/WMS_Buffer_Optimization.pptx) |
| 2026-02-02 | CLI команды: --train-ml, --sync-*, --calc-* |
| 2026-02-02 | Синхронизация 1.6M задач завершена |
| 2026-02-01 | TimescaleDB схема, репозиторий |
| 2026-01-xx | Базовая архитектура, HysteresisController |

---

## 7. КОНТАКТЫ И РЕСУРСЫ

- **Репозиторий:** `/home/onica_on/WMS.BufferManagement/`
- **База данных:** TimescaleDB на localhost:5433
- **WMS API:** http://192.168.1.100:8080/wms/hs/buffer-api/v1
- **Модели ML:** `data/models/`
- **Презентация:** `docs/WMS_Buffer_Optimization.pptx`
