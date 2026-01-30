# WMS Buffer Management System - План проекта

## Описание задачи

Система управления потоком товара на складе с трёхуровневой архитектурой:
- **Зона буфера**: принимает моно-палеты из зоны хранения
- **3 карщика**: доставляют палеты в буфер (разное расстояние/время доставки)
- **20 сборщиков**: забирают палеты и раскидывают товары по ячейкам (заказам)

**Цель**: обеспечить постоянную подпитку буфера с динамической реакцией на скорость сборщиков и прогнозированием потребностей.

### Дополнительные требования:

1. **Weight Priority (Heavy-on-Bottom Rule)**
   - Каждая палета содержит товар с определённым весом
   - Тяжёлые товары должны подаваться ПЕРВЫМИ в заказ
   - Это предотвращает раздавливание лёгких товаров тяжёлыми

2. **Task Streams (Потоки заданий)**
   - Задания на перемещение группируются в потоки (streams)
   - **Потоки выполняются ПОСЛЕДОВАТЕЛЬНО** (один поток за другим)
   - **Задания внутри потока сортируются по весу** (тяжёлые первыми)

---

## Теоретическая база (Bartholdi & Hackman)

Архитектура основана на концепциях из [Warehouse & Distribution Science](https://www.warehouse-science.com/book/editions/wh-sci-0.98.1.pdf):

### Ключевые принципы:

1. **Wave Management** - управление волнами заказов
   - Группировка заказов во временные окна (pick waves)
   - Синхронизация подачи палет с волнами сборки

2. **Workload Balancing** - балансировка нагрузки
   - Равномерное распределение работы между карщиками
   - Учёт variance скорости сборщиков для сглаживания пиков

3. **Travel Time Minimization** - минимизация времени перемещения
   - "Travel time constitutes more than half of order picking time"
   - Оптимизация назначений палет с учётом расстояний

4. **Throughput vs. Response Time Trade-off**
   - Баланс между пропускной способностью и временем отклика
   - Гистерезис для предотвращения осцилляций

### Применение в архитектуре:

| Концепция B&H | Наш компонент | Реализация |
|---------------|---------------|------------|
| Wave Planning | Tactical Optimizer | Группировка палет по волнам |
| Workload Balance | Dispatcher | M/M/1 очередь с балансировкой |
| Travel Minimization | OR-Tools | Минимизация суммарного пути |
| Demand Forecasting | ML.NET Predictor | Прогноз скорости потребления |

---

## Архитектура решения

```
┌─────────────────────────────────────────────────────────────────┐
│                    WMS.BufferManagement                         │
├─────────────────────────────────────────────────────────────────┤
│  Layers/                                                        │
│  ├── Realtime/        (100-500ms cycle)                        │
│  │   ├── Dispatcher         - распределение задач карщикам     │
│  │   ├── BufferController   - PID/гистерезис управление        │
│  │   ├── StateMachine       - Stateless FSM                    │
│  │   └── Rules/             - NRules правила                   │
│  │                                                              │
│  ├── Tactical/        (каждые секунды)                         │
│  │   └── Optimizer          - Google OR-Tools оптимизация      │
│  │                                                              │
│  └── Historical/      (offline анализ)                         │
│      ├── DataCollector      - сбор метрик                      │
│      └── Predictor          - ML.NET модели                    │
│                                                                 │
│  Domain/                                                        │
│  ├── Entities/        - Pallet, Picker, Forklift, Buffer, etc. │
│  ├── Events/          - системные события                      │
│  └── Interfaces/      - контракты для внешних систем           │
│                                                                 │
│  Infrastructure/                                                │
│  ├── Messaging/       - внутренняя шина событий                │
│  └── Persistence/     - хранение истории (SQLite/in-memory)    │
│                                                                 │
│  Simulation/          - симулятор для тестирования             │
│  └── Console UI       - визуализация состояния                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Технологический стек

| Компонент | Технология | Версия |
|-----------|------------|--------|
| Runtime | .NET 8 | LTS |
| Rules Engine | NRules | 1.0.0 |
| State Machine | Stateless | 5.x |
| Optimization | Google.OrTools | 9.x |
| ML | ML.NET | 3.x |
| DI | Microsoft.Extensions.DependencyInjection | 8.x |
| Config | Microsoft.Extensions.Configuration | 8.x |
| Logging | Microsoft.Extensions.Logging (Console) | 8.x |

---

## Структура файлов

```
WMS.BufferManagement/
├── WMS.BufferManagement.csproj
├── appsettings.json
├── Program.cs
│
├── Domain/
│   ├── Entities/
│   │   ├── Product.cs             # Товар с весом и приоритетом
│   │   ├── Pallet.cs              # Моно-палета (содержит Product)
│   │   ├── Picker.cs              # Сборщик
│   │   ├── Forklift.cs            # Карщик
│   │   ├── BufferZone.cs          # Зона буфера
│   │   ├── StorageZone.cs         # Зона хранения
│   │   ├── DeliveryTask.cs        # Задание на перемещение
│   │   ├── TaskStream.cs          # Поток заданий (последовательное выполнение)
│   │   └── Order.cs               # Заказ (для сборщика)
│   │
│   ├── Events/
│   │   ├── PalletRequestedEvent.cs
│   │   ├── PalletDeliveredEvent.cs
│   │   ├── PalletConsumedEvent.cs
│   │   └── BufferLevelChangedEvent.cs
│   │
│   └── Interfaces/
│       ├── IWarehouseDataSource.cs    # Внешний источник данных
│       ├── ITaskExecutor.cs           # Исполнитель задач
│       └── IMetricsCollector.cs       # Сбор метрик
│
├── Layers/
│   ├── Realtime/
│   │   ├── Dispatcher/
│   │   │   ├── IDispatcher.cs
│   │   │   ├── ForkliftDispatcher.cs  # M/M/1 или Round-Robin
│   │   │   └── TaskQueue.cs
│   │   │
│   │   ├── BufferControl/
│   │   │   ├── IBufferController.cs
│   │   │   ├── HysteresisController.cs
│   │   │   └── PidController.cs       # Опционально
│   │   │
│   │   ├── StateMachine/
│   │   │   ├── BufferState.cs         # enum: Normal, Low, Critical, Overflow
│   │   │   ├── BufferStateMachine.cs  # Stateless FSM
│   │   │   └── Triggers.cs
│   │   │
│   │   └── Rules/
│   │       ├── BufferRules.cs         # NRules правила
│   │       └── RuleEngine.cs          # Обёртка над NRules
│   │
│   ├── Tactical/
│   │   ├── IOptimizer.cs
│   │   ├── PalletAssignmentOptimizer.cs  # OR-Tools CP-SAT
│   │   ├── WaveManager.cs                # Wave planning (B&H Ch.9)
│   │   ├── WorkloadBalancer.cs           # Балансировка нагрузки
│   │   └── Models/
│   │       ├── OptimizationResult.cs
│   │       ├── Wave.cs                   # Волна заказов
│   │       └── Assignment.cs             # Назначение палета→карщик
│   │
│   └── Historical/
│       ├── DataCollection/
│       │   ├── MetricsStore.cs        # In-memory хранилище
│       │   └── HistoricalData.cs
│       │
│       └── Prediction/
│           ├── IPredictor.cs
│           ├── PickerSpeedPredictor.cs    # ML.NET регрессия
│           └── DemandPredictor.cs         # Прогноз потребности
│
├── Infrastructure/
│   ├── EventBus/
│   │   ├── IEventBus.cs
│   │   └── InMemoryEventBus.cs
│   │
│   └── Configuration/
│       └── SystemConfig.cs
│
├── Simulation/
│   ├── WarehouseSimulator.cs      # Симулятор склада
│   ├── SimulatedPicker.cs         # Симуляция сборщика
│   ├── SimulatedForklift.cs       # Симуляция карщика
│   └── ScenarioRunner.cs          # Запуск сценариев
│
└── Console/
    ├── ConsoleRenderer.cs         # ASCII визуализация
    └── CommandHandler.cs          # Интерактивные команды
```

---

## Ключевые алгоритмы

### 1. Realtime: Hysteresis Buffer Controller
```
Параметры:
- LowThreshold = 30%
- HighThreshold = 70%
- CriticalThreshold = 15%
- DeadBand = 5% (зона нечувствительности для предотвращения осцилляций)

Логика:
- Уровень < Critical → СРОЧНО: все карщики на подачу, приоритет = MAX
- Уровень < Low → активировать дополнительного карщика
- Уровень в [Low+DeadBand, High-DeadBand] → поддерживать текущий темп
- Уровень > High → снизить интенсивность, карщик может взять удалённую палету
```

### 1.1. Dispatcher: Queueing Theory (M/M/c)
```
Модель: M/M/c очередь (c = количество карщиков = 3)

Параметры:
- λ (arrival rate) = скорость потребления буфера сборщиками (палет/час)
- μ (service rate) = средняя скорость одного карщика (палет/час)
- ρ = λ / (c × μ) - утилизация системы

Метрики для мониторинга:
- E[W] = среднее время ожидания в очереди
- E[L] = средняя длина очереди
- P(queue > N) = вероятность превышения очереди

Правило: если ρ > 0.85 → предупреждение о перегрузке
         если ρ > 0.95 → критическое состояние, нужны доп. ресурсы
```

### 2. Tactical: OR-Tools Assignment (Bartholdi-style)
```
Целевая функция (минимизация):
  Σ (travel_time[i,j] × assignment[i,j]) + λ × workload_variance

Где:
  - travel_time[i,j] = время карщика i до палеты j + время до буфера
  - workload_variance = дисперсия нагрузки между карщиками
  - λ = коэффициент балансировки (trade-off throughput vs fairness)

Ограничения:
  - Каждая палета назначена ровно одному карщику
  - Карщик не превышает capacity (обычно 1 палета)
  - Приоритетные палеты (critical buffer) назначаются первыми
  - Wave constraints: палеты одной волны завершаются до deadline
  - **Weight Priority**: палеты с тяжёлыми товарами → раньше лёгких
  - **Stream Constraints**: задания в потоке выполняются последовательно

Подход: CP-SAT Solver с warm-start от предыдущего решения
```

### 2.1. Weight Priority Algorithm (Heavy-on-Bottom)
```
Для каждого заказа Order:
  1. Получить список требуемых палет
  2. Отсортировать по weight DESC (тяжёлые первыми)
  3. Назначить sequence_number каждой палете в порядке сортировки
  4. При оптимизации: completion_time[heavy] < completion_time[light]

Constraint в OR-Tools:
  ∀ (pallet_i, pallet_j) ∈ same_order:
    if weight[i] > weight[j]:
      end_time[i] ≤ start_time[j]
```

### 2.2. Task Stream Sequencing
```
TaskStream = группа DeliveryTask, отсортированная по весу товара

Логика:
  1. Потоки выполняются ПОСЛЕДОВАТЕЛЬНО (Stream[n+1] после Stream[n])
  2. Задания ВНУТРИ потока сортируются по весу (тяжёлые → лёгкие)
  3. Это гарантирует heavy-on-bottom для каждого потока

Алгоритм:
  streams = GetAllStreams()
  for stream in streams:  // последовательно
      tasks = stream.Tasks.OrderByDescending(t => t.Pallet.Product.Weight)
      for task in tasks:  // в порядке веса
          Execute(task)
      WaitForStreamCompletion()

Constraint в OR-Tools:
  // Потоки последовательно
  ∀ i ∈ [0, len(streams)-1]:
    max(end_time[streams[i].Tasks]) ≤ min(start_time[streams[i+1].Tasks])

  // Внутри потока - по весу
  ∀ stream ∈ streams:
    ∀ (task_i, task_j) ∈ stream where weight[i] > weight[j]:
      end_time[task_i] ≤ start_time[task_j]
```

### 3. Wave Management (из B&H Chapter 9)
```
Параметры волны:
- WaveDuration = 15 минут (настраиваемо)
- WaveCapacity = buffer_capacity × expected_consumption_rate × WaveDuration

Логика:
1. Сбор заказов в batch до заполнения волны
2. Расчёт требуемых палет для волны
3. Прогнозирование времени доставки каждой палеты
4. Запуск подачи с упреждением (lead_time = max(travel_time) + safety_margin)
5. Мониторинг выполнения волны, коррекция при отклонениях
```

### 3. Historical: ML.NET Prediction
```
Features:
- picker_id, product_type, quantity, time_of_day, day_of_week

Target:
- consumption_speed (палет/час)

Модель: FastTree Regression
```

---

## Метрики и KPI

### Realtime (отображаются в консоли)
- `buffer_level` - текущий уровень буфера (%)
- `buffer_state` - состояние FSM (Normal/Low/Critical/Overflow)
- `forklift_utilization` - загрузка каждого карщика (%)
- `queue_length` - длина очереди задач
- `picker_consumption_rate` - текущая скорость потребления (pal/h)

### Tactical (для оптимизации)
- `average_delivery_time` - среднее время доставки палеты
- `workload_variance` - дисперсия нагрузки между карщиками
- `wave_completion_rate` - процент завершённых волн в срок
- `travel_distance_total` - суммарный пробег карщиков

### Historical (для ML)
- `picker_speed_by_product` - скорость сборщика по типу товара
- `time_of_day_patterns` - паттерны по времени суток
- `forecast_accuracy` - точность прогноза (MAPE)

---

## Конфигурация (appsettings.json)

```json
{
  "Buffer": {
    "Capacity": 50,
    "LowThreshold": 0.3,
    "HighThreshold": 0.7,
    "CriticalThreshold": 0.15,
    "DeadBand": 0.05
  },
  "Timing": {
    "RealtimeCycleMs": 200,
    "TacticalCycleMs": 2000,
    "HistoricalCycleMs": 60000
  },
  "Wave": {
    "DurationMinutes": 15,
    "SafetyMarginSeconds": 60,
    "MaxPalletsPerWave": 30
  },
  "Workers": {
    "ForkliftsCount": 3,
    "PickersCount": 20
  },
  "Optimization": {
    "WorkloadBalanceLambda": 0.3,
    "MaxSolverTimeMs": 500,
    "WarmStartEnabled": true
  },
  "Queueing": {
    "OverloadThreshold": 0.85,
    "CriticalThreshold": 0.95
  },
  "Simulation": {
    "Enabled": true,
    "SpeedMultiplier": 1.0,
    "RandomSeed": 42
  }
}
```

---

## Консольный интерфейс

```
╔══════════════════════════════════════════════════════════════╗
║  WMS Buffer Management System v1.0                           ║
╠══════════════════════════════════════════════════════════════╣
║  BUFFER: [████████████░░░░░░░░] 60% (30/50)  State: NORMAL   ║
╠══════════════════════════════════════════════════════════════╣
║  FORKLIFTS:                                                  ║
║    [1] BUSY   → Pallet #142  ETA: 45s   Distance: 120m       ║
║    [2] IDLE   ←              Queue: 2                        ║
║    [3] BUSY   → Pallet #143  ETA: 30s   Distance: 80m        ║
╠══════════════════════════════════════════════════════════════╣
║  PICKERS (top 5 active):                                     ║
║    #03: 12.5 pal/h  #07: 11.2 pal/h  #12: 10.8 pal/h        ║
║  Total consumption: 180 pal/h   Predicted: 185 pal/h         ║
╠══════════════════════════════════════════════════════════════╣
║  Commands: [s]tatus [p]ause [r]esume [+]speed [-]speed [q]uit║
╚══════════════════════════════════════════════════════════════╝
```

---

## Этапы реализации

### Этап 1: Базовая структура
1. Создать проект .NET 8 и подключить NuGet пакеты
2. Реализовать доменные сущности (Pallet, Picker, Forklift, BufferZone)
3. Создать интерфейсы для внешних систем
4. Настроить DI и конфигурацию

### Этап 2: Realtime Control Layer
5. Реализовать Stateless State Machine для буфера
6. Реализовать HysteresisController
7. Создать ForkliftDispatcher с очередью задач
8. Настроить NRules с базовыми правилами

### Этап 3: Tactical Planning Layer
9. Интегрировать Google OR-Tools
10. Реализовать PalletAssignmentOptimizer с CP-SAT
11. Добавить WaveManager для управления волнами
12. Реализовать WorkloadBalancer

### Этап 4: Historical Intelligence Layer
13. Создать MetricsStore для сбора данных
14. Реализовать ML.NET предиктор скорости сборщиков
15. Добавить DemandPredictor для прогноза потребности

### Этап 5: Симуляция и консоль
16. Создать симулятор склада с настраиваемыми сценариями
17. Реализовать консольный рендерер с ASCII-визуализацией
18. Добавить интерактивные команды и метрики

### Этап 6: Интеграция и тестирование
19. Связать все слои через EventBus
20. Запуск основного цикла с таймерами для каждого слоя
21. Тестирование под WSL
22. Прогон всех сценариев (Steady State, Surge, Failure, Variable)

---

## Верификация

### Как проверить работоспособность:

1. **Запуск приложения:**
   ```bash
   cd WMS.BufferManagement
   dotnet run
   ```

2. **Тестовые сценарии:**

   **Scenario 1: Steady State**
   - Буфер держится в пределах 30-70%
   - ρ (утилизация) < 0.85
   - Волны завершаются в срок

   **Scenario 2: Surge (пик нагрузки)**
   - Увеличить скорость сборщиков на 50%
   - Система должна активировать всех карщиков
   - Переход в состояние Low → Critical → восстановление

   **Scenario 3: Forklift Failure**
   - Один карщик выходит из строя
   - Система перераспределяет нагрузку
   - Проверка workload balancing

   **Scenario 4: Variable Picker Speeds**
   - Сборщики работают с разной скоростью (variance)
   - ML.NET корректирует прогнозы
   - Проверка адаптации системы

3. **Ожидаемое поведение:**
   - State Machine переключает состояния при изменении уровня буфера
   - Dispatcher равномерно распределяет задачи между карщиками
   - OR-Tools оптимизирует назначения каждые 2 секунды
   - ML.NET корректирует прогнозы на основе накопленных данных

---

## Зависимости (NuGet)

```xml
<PackageReference Include="NRules" Version="1.0.0" />
<PackageReference Include="Stateless" Version="5.16.0" />
<PackageReference Include="Google.OrTools" Version="9.11.4210" />
<PackageReference Include="Microsoft.ML" Version="3.0.1" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.1" />
```
