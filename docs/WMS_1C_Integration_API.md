# Спецификация API интеграции с WMS 1С

## Обзор

Данный документ описывает HTTP API, который должна предоставить WMS система на платформе 1С для интеграции с системой управления буфером (Buffer Management System).

### Архитектура интеграции

```
┌─────────────────────┐         HTTP/JSON          ┌─────────────────────┐
│                     │ ◄─────────────────────────► │                     │
│  Buffer Management  │         REST API           │     WMS 1С          │
│       System        │                            │                     │
│                     │ ◄────────────────────────── │                     │
└─────────────────────┘      Webhooks/Events       └─────────────────────┘
```

### Базовые требования

- **Протокол**: HTTP/HTTPS
- **Формат данных**: JSON (UTF-8)
- **Аутентификация**: API Key в заголовке `X-API-Key`
- **Базовый URL**: `http://<wms-server>/wms/hs/buffer-api/v1`

---

## Аутентификация

Все запросы должны содержать заголовок:

```http
X-API-Key: <api_key>
```

Рекомендуется также поддержка Basic Auth для совместимости:

```http
Authorization: Basic <base64(login:password)>
```

---

## Структуры данных

### Справочники (Enums)

#### ПриоритетЗаказа (OrderPriority)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Low` | Низкий приоритет |
| 1 | `Normal` | Обычный приоритет |
| 2 | `High` | Высокий приоритет |
| 3 | `Urgent` | Срочный |

#### СтатусВолны (WaveStatus)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Pending` | Ожидает запуска |
| 1 | `InProgress` | Выполняется |
| 2 | `Completed` | Завершена |
| 3 | `Failed` | Ошибка |

#### СтатусПалеты (PalletStatus)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Available` | Доступна |
| 1 | `Reserved` | Зарезервирована |
| 2 | `InTransit` | В пути |
| 3 | `InBuffer` | В буфере |
| 4 | `Consumed` | Израсходована |

#### СтатусСборщика (PickerStatus)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Idle` | Ожидает |
| 1 | `Active` | Работает |
| 2 | `OnBreak` | На перерыве |
| 3 | `Offline` | Не в сети |

#### СтатусКарщика (ForkliftStatus)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Idle` | Свободен |
| 1 | `EnRoute` | В пути |
| 2 | `Loading` | Загрузка |
| 3 | `Unloading` | Разгрузка |
| 4 | `Maintenance` | Техобслуживание |

#### СтатусЗадания (DeliveryTaskStatus)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Pending` | Ожидает |
| 1 | `Assigned` | Назначено |
| 2 | `InProgress` | Выполняется |
| 3 | `Completed` | Завершено |
| 4 | `Failed` | Ошибка |
| 5 | `Cancelled` | Отменено |

#### ПриоритетЗадания (TaskPriority)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Low` | Низкий |
| 1 | `Normal` | Обычный |
| 2 | `High` | Высокий |
| 3 | `Critical` | Критический |

#### КатегорияВеса (WeightCategory)

| Код | Значение | Описание |
|-----|----------|----------|
| 0 | `Light` | Лёгкий (< 5 кг) |
| 1 | `Medium` | Средний (5-20 кг) |
| 2 | `Heavy` | Тяжёлый (> 20 кг) |

---

### Структуры объектов

#### Позиция (Position)

```json
{
  "x": 10.5,
  "y": 20.3,
  "z": 0.0
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `x` | `number` | Да | Координата X (метры) |
| `y` | `number` | Да | Координата Y (метры) |
| `z` | `number` | Нет | Координата Z (уровень/этаж), по умолчанию 0 |

#### Заказ (Order)

```json
{
  "id": "ORD-2024-001234",
  "customerId": "CUST-001",
  "createdAt": "2024-01-15T10:30:00Z",
  "dueTime": "2024-01-15T14:00:00Z",
  "priority": 1,
  "lines": [
    {
      "productId": "SKU-001",
      "productName": "Товар 1",
      "quantity": 10,
      "preferredPalletId": "PAL-001"
    }
  ]
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `id` | `string` | Да | Уникальный ID заказа (Документ.ЗаказКлиента.Номер) |
| `customerId` | `string` | Да | ID клиента (Справочник.Контрагенты.Код) |
| `createdAt` | `datetime` | Да | Дата создания (ISO 8601) |
| `dueTime` | `datetime` | Нет | Срок выполнения |
| `priority` | `int` | Да | Приоритет (0-3) |
| `lines` | `array` | Да | Строки заказа |

#### Строка заказа (OrderLine)

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `productId` | `string` | Да | Артикул товара (Справочник.Номенклатура.Артикул) |
| `productName` | `string` | Да | Наименование товара |
| `quantity` | `int` | Да | Количество |
| `preferredPalletId` | `string` | Нет | Предпочтительная палета |

#### Волна (Wave)

```json
{
  "id": "WAVE-2024-0001",
  "createdAt": "2024-01-15T10:00:00Z",
  "startedAt": null,
  "completedAt": null,
  "status": 0,
  "orderIds": ["ORD-001", "ORD-002", "ORD-003"],
  "totalPallets": 15
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `id` | `string` | Да | ID волны |
| `createdAt` | `datetime` | Да | Дата создания |
| `startedAt` | `datetime` | Нет | Дата начала |
| `completedAt` | `datetime` | Нет | Дата завершения |
| `status` | `int` | Да | Статус волны (0-3) |
| `orderIds` | `array[string]` | Да | Список ID заказов в волне |
| `totalPallets` | `int` | Да | Общее количество палет |

#### Палета (PalletInfo)

```json
{
  "id": "PAL-2024-000001",
  "productId": "SKU-001",
  "productName": "Товар 1",
  "quantity": 100,
  "weightKg": 250.5,
  "weightCategory": 2,
  "currentZone": "STORAGE-A",
  "currentSlot": "A-01-02-03",
  "position": {
    "x": 10.5,
    "y": 20.3,
    "z": 2.0
  },
  "lastMovedAt": "2024-01-15T08:00:00Z",
  "status": 0
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `id` | `string` | Да | ID палеты (штрихкод) |
| `productId` | `string` | Да | Артикул товара |
| `productName` | `string` | Да | Наименование товара |
| `quantity` | `int` | Да | Количество единиц на палете |
| `weightKg` | `number` | Да | Общий вес (кг) |
| `weightCategory` | `int` | Да | Категория веса (0-2) |
| `currentZone` | `string` | Да | Текущая зона (Справочник.СкладскиеЗоны.Код) |
| `currentSlot` | `string` | Да | Текущая ячейка (Справочник.СкладскиеЯчейки.Код) |
| `position` | `Position` | Да | Физические координаты |
| `lastMovedAt` | `datetime` | Да | Время последнего перемещения |
| `status` | `int` | Да | Статус палеты (0-4) |

#### Сборщик (PickerInfo)

```json
{
  "id": "EMP-001",
  "name": "Иванов И.И.",
  "zone": "PICKING-A",
  "status": 1,
  "shiftStart": "2024-01-15T08:00:00Z",
  "shiftEnd": "2024-01-15T20:00:00Z"
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `id` | `string` | Да | Табельный номер |
| `name` | `string` | Да | ФИО |
| `zone` | `string` | Да | Рабочая зона |
| `status` | `int` | Да | Статус (0-3) |
| `shiftStart` | `datetime` | Да | Начало смены |
| `shiftEnd` | `datetime` | Нет | Конец смены |

#### Статистика сборщика (PickerStats)

```json
{
  "pickerId": "EMP-001",
  "palletsProcessed": 25,
  "itemsPicked": 450,
  "averageSpeed": 120.5,
  "efficiency": 95.2,
  "totalActiveTime": "PT6H30M",
  "totalIdleTime": "PT30M"
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `pickerId` | `string` | Да | Табельный номер |
| `palletsProcessed` | `int` | Да | Обработано палет |
| `itemsPicked` | `int` | Да | Собрано единиц товара |
| `averageSpeed` | `number` | Да | Средняя скорость (единиц/час) |
| `efficiency` | `number` | Да | Эффективность (% от нормы) |
| `totalActiveTime` | `duration` | Да | Время работы (ISO 8601 duration) |
| `totalIdleTime` | `duration` | Да | Время простоя |

#### Карщик (ForkliftInfo)

```json
{
  "id": "FORK-001",
  "operatorName": "Петров П.П.",
  "status": 0,
  "currentPosition": {
    "x": 15.0,
    "y": 30.0,
    "z": 0.0
  },
  "currentTaskId": null,
  "distanceFromBuffer": 45.5
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `id` | `string` | Да | ID погрузчика |
| `operatorName` | `string` | Да | ФИО оператора |
| `status` | `int` | Да | Статус (0-4) |
| `currentPosition` | `Position` | Нет | Текущая позиция |
| `currentTaskId` | `string` | Нет | ID текущего задания |
| `distanceFromBuffer` | `number` | Да | Расстояние до буфера (метры) |

#### Задание на перемещение (DeliveryTask)

**Запрос на создание:**
```json
{
  "palletId": "PAL-001",
  "sourceZone": "STORAGE-A",
  "sourceSlot": "A-01-02-03",
  "targetZone": "BUFFER",
  "targetSlot": "BUF-01",
  "priority": 2,
  "assignedForkliftId": "FORK-001"
}
```

**Информация о задании:**
```json
{
  "id": "TASK-2024-000001",
  "palletId": "PAL-001",
  "forkliftId": "FORK-001",
  "status": 2,
  "createdAt": "2024-01-15T10:30:00Z",
  "startedAt": "2024-01-15T10:31:00Z",
  "completedAt": null,
  "currentPosition": {
    "x": 20.0,
    "y": 25.0,
    "z": 0.0
  }
}
```

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `id` | `string` | Да (ответ) | ID задания |
| `palletId` | `string` | Да | ID палеты |
| `sourceZone` | `string` | Да | Зона отправления |
| `sourceSlot` | `string` | Да | Ячейка отправления |
| `targetZone` | `string` | Да | Зона назначения |
| `targetSlot` | `string` | Да | Ячейка назначения |
| `priority` | `int` | Да | Приоритет (0-3) |
| `assignedForkliftId` | `string` | Нет | Назначенный карщик |
| `forkliftId` | `string` | Нет | Фактический исполнитель |
| `status` | `int` | Да (ответ) | Статус (0-5) |
| `createdAt` | `datetime` | Да (ответ) | Время создания |
| `startedAt` | `datetime` | Нет | Время начала |
| `completedAt` | `datetime` | Нет | Время завершения |
| `currentPosition` | `Position` | Нет | Текущая позиция карщика |

---

## API Endpoints

### Проверка доступности

#### `GET /health`

Проверка работоспособности API.

**Ответ (200 OK):**
```json
{
  "status": "ok",
  "timestamp": "2024-01-15T10:30:00Z",
  "version": "1.0.0"
}
```

---

### Заказы

#### `GET /orders`

Получить список активных заказов.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Нет | Заказы созданные после этого времени |
| `limit` | `int` | Нет | Максимальное количество (по умолчанию 100) |

**Пример запроса:**
```http
GET /wms/hs/buffer-api/v1/orders?fromTime=2024-01-15T00:00:00Z&limit=50
X-API-Key: your-api-key
```

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "id": "ORD-001",
      "customerId": "CUST-001",
      "createdAt": "2024-01-15T10:30:00Z",
      "dueTime": "2024-01-15T14:00:00Z",
      "priority": 1,
      "lines": [...]
    }
  ],
  "total": 1
}
```

#### `GET /orders/{orderId}`

Получить детали заказа.

**Ответ (200 OK):**
```json
{
  "order": {
    "id": "ORD-001",
    ...
  },
  "requiredPallets": [
    {
      "productId": "SKU-001",
      "quantity": 10,
      "weight": 2,
      "availablePalletIds": ["PAL-001", "PAL-002"]
    }
  ],
  "estimates": {
    "estimatedPickingTime": "PT30M",
    "estimatedDeliveryTime": "PT5M",
    "estimatedCompletionTime": "2024-01-15T11:05:00Z"
  }
}
```

---

### Волны

#### `GET /waves/next`

Получить следующую волну для обработки.

**Ответ (200 OK):**
```json
{
  "id": "WAVE-001",
  "createdAt": "2024-01-15T10:00:00Z",
  "status": 0,
  "orderIds": ["ORD-001", "ORD-002"],
  "totalPallets": 10
}
```

**Ответ (204 No Content):** Нет волн для обработки.

#### `POST /waves/{waveId}/start`

Подтвердить запуск волны.

**Тело запроса:**
```json
{
  "startedAt": "2024-01-15T10:30:00Z"
}
```

**Ответ (200 OK):**
```json
{
  "success": true
}
```

#### `POST /waves/{waveId}/complete`

Завершить волну.

**Тело запроса:**
```json
{
  "success": true,
  "completedOrders": 5,
  "failedOrders": 0,
  "actualDuration": "PT45M",
  "failureReason": null
}
```

**Ответ (200 OK):**
```json
{
  "success": true
}
```

---

### Палеты

#### `GET /pallets`

Получить список палет в зоне хранения.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `productType` | `string` | Нет | Фильтр по артикулу товара |
| `zone` | `string` | Нет | Фильтр по зоне |
| `status` | `int` | Нет | Фильтр по статусу |

**Пример запроса:**
```http
GET /wms/hs/buffer-api/v1/pallets?zone=STORAGE-A&status=0
```

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "id": "PAL-001",
      "productId": "SKU-001",
      "productName": "Товар 1",
      "quantity": 100,
      "weightKg": 250.5,
      "weightCategory": 2,
      "currentZone": "STORAGE-A",
      "currentSlot": "A-01-02-03",
      "position": {"x": 10.5, "y": 20.3, "z": 2.0},
      "lastMovedAt": "2024-01-15T08:00:00Z",
      "status": 0
    }
  ],
  "total": 1
}
```

#### `GET /pallets/{palletId}`

Получить информацию о конкретной палете.

**Ответ (200 OK):** Объект PalletInfo

**Ответ (404 Not Found):** Палета не найдена

#### `POST /pallets/{palletId}/reserve`

Зарезервировать палету для доставки.

**Тело запроса:**
```json
{
  "forkliftId": "FORK-001",
  "timeoutMinutes": 10
}
```

**Ответ (200 OK):**
```json
{
  "success": true,
  "reservationId": "RES-001",
  "expiresAt": "2024-01-15T10:40:00Z",
  "failureReason": null
}
```

**Ответ (409 Conflict):**
```json
{
  "success": false,
  "reservationId": null,
  "expiresAt": null,
  "failureReason": "Палета уже зарезервирована"
}
```

#### `DELETE /pallets/{palletId}/reserve`

Освободить резервацию палеты.

**Ответ (200 OK):**
```json
{
  "success": true
}
```

#### `POST /pallets/{palletId}/delivery`

Подтвердить доставку палеты в буфер.

**Тело запроса:**
```json
{
  "bufferSlotId": "BUF-01",
  "deliveryTime": "2024-01-15T10:35:00Z"
}
```

**Ответ (200 OK):**
```json
{
  "success": true
}
```

#### `POST /pallets/{palletId}/consume`

Подтвердить потребление палеты сборщиком.

**Тело запроса:**
```json
{
  "pickerId": "EMP-001",
  "quantityTaken": 50,
  "quantityRemaining": 50,
  "pickingDuration": "PT5M",
  "destinationCell": "CELL-A-01"
}
```

**Ответ (200 OK):**
```json
{
  "success": true
}
```

---

### Персонал

#### `GET /pickers`

Получить список активных сборщиков.

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "id": "EMP-001",
      "name": "Иванов И.И.",
      "zone": "PICKING-A",
      "status": 1,
      "shiftStart": "2024-01-15T08:00:00Z",
      "shiftEnd": "2024-01-15T20:00:00Z"
    }
  ],
  "total": 20
}
```

#### `GET /pickers/{pickerId}/stats`

Получить статистику сборщика за период.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |

**Ответ (200 OK):** Объект PickerStats

#### `GET /forklifts`

Получить список карщиков.

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "id": "FORK-001",
      "operatorName": "Петров П.П.",
      "status": 0,
      "currentPosition": {"x": 15.0, "y": 30.0, "z": 0.0},
      "currentTaskId": null,
      "distanceFromBuffer": 45.5
    }
  ],
  "total": 3
}
```

#### `PUT /forklifts/{forkliftId}/status`

Обновить статус карщика.

**Тело запроса:**
```json
{
  "status": 1,
  "position": {
    "x": 20.0,
    "y": 25.0,
    "z": 0.0
  }
}
```

**Ответ (200 OK):**
```json
{
  "success": true
}
```

---

### Задания

#### `GET /tasks`

Получить список активных заданий.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `status` | `int` | Нет | Фильтр по статусу |
| `forkliftId` | `string` | Нет | Фильтр по карщику |

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "id": "TASK-001",
      "palletId": "PAL-001",
      "forkliftId": "FORK-001",
      "status": 2,
      "createdAt": "2024-01-15T10:30:00Z",
      "startedAt": "2024-01-15T10:31:00Z",
      "completedAt": null,
      "currentPosition": {"x": 20.0, "y": 25.0, "z": 0.0}
    }
  ],
  "total": 5
}
```

#### `POST /tasks`

Создать задание на перемещение.

**Тело запроса:**
```json
{
  "palletId": "PAL-001",
  "sourceZone": "STORAGE-A",
  "sourceSlot": "A-01-02-03",
  "targetZone": "BUFFER",
  "targetSlot": "BUF-01",
  "priority": 2,
  "assignedForkliftId": "FORK-001"
}
```

**Ответ (201 Created):**
```json
{
  "taskId": "TASK-001"
}
```

#### `PUT /tasks/{taskId}/status`

Обновить статус задания.

**Тело запроса:**
```json
{
  "status": 3,
  "reason": null
}
```

**Ответ (200 OK):**
```json
{
  "success": true
}
```

---

## События (Webhooks)

Buffer Management System может подписаться на события от WMS. Для этого необходимо настроить webhook URL в WMS.

### Настройка webhook

WMS должна отправлять POST запросы на настроенный URL при возникновении событий.

**Заголовки:**
```http
Content-Type: application/json
X-Event-Type: <event_type>
X-Event-Id: <unique_event_id>
X-Timestamp: <iso8601_timestamp>
```

### Типы событий

#### `order.created` - Создан новый заказ

```json
{
  "eventType": "order.created",
  "timestamp": "2024-01-15T10:30:00Z",
  "data": {
    "order": {
      "id": "ORD-001",
      "customerId": "CUST-001",
      "createdAt": "2024-01-15T10:30:00Z",
      "priority": 2,
      "lines": [...]
    }
  }
}
```

#### `pallet.moved` - Палета перемещена

```json
{
  "eventType": "pallet.moved",
  "timestamp": "2024-01-15T10:35:00Z",
  "data": {
    "palletId": "PAL-001",
    "fromZone": "STORAGE-A",
    "toZone": "BUFFER"
  }
}
```

#### `picker.status_changed` - Изменился статус сборщика

```json
{
  "eventType": "picker.status_changed",
  "timestamp": "2024-01-15T10:40:00Z",
  "data": {
    "pickerId": "EMP-001",
    "oldStatus": 0,
    "newStatus": 1
  }
}
```

#### `task.completed` - Задание завершено

```json
{
  "eventType": "task.completed",
  "timestamp": "2024-01-15T10:45:00Z",
  "data": {
    "taskId": "TASK-001",
    "success": true,
    "failureReason": null
  }
}
```

#### `buffer.level_changed` - Изменился уровень буфера

```json
{
  "eventType": "buffer.level_changed",
  "timestamp": "2024-01-15T10:50:00Z",
  "data": {
    "oldLevel": 0.5,
    "newLevel": 0.6,
    "reason": "Доставлена палета PAL-001"
  }
}
```

---

## Исторические данные (для ML и аналитики)

Эти endpoints предоставляют исторические данные для обучения ML моделей и аналитики. Данные экспортируются в систему Buffer Management для хранения в TimescaleDB.

### Структуры исторических данных

#### История заданий (TaskHistory)

```json
{
  "id": "TASK-2024-000001",
  "createdAt": "2024-01-15T10:30:00Z",
  "startedAt": "2024-01-15T10:31:00Z",
  "completedAt": "2024-01-15T10:35:00Z",
  "palletId": "PAL-001",
  "productType": "SKU-001",
  "productName": "Товар 1",
  "weightKg": 250.5,
  "weightCategory": 2,
  "forkliftId": "FORK-001",
  "fromZone": "STORAGE-A",
  "fromSlot": "A-01-02-03",
  "toZone": "BUFFER",
  "toSlot": "BUF-01",
  "distanceMeters": 45.5,
  "status": 3,
  "durationSec": 240.0,
  "failureReason": null
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `id` | `string` | ID задания |
| `createdAt` | `datetime` | Время создания |
| `startedAt` | `datetime` | Время начала выполнения |
| `completedAt` | `datetime` | Время завершения |
| `palletId` | `string` | ID палеты |
| `productType` | `string` | Артикул товара |
| `productName` | `string` | Наименование товара |
| `weightKg` | `number` | Вес палеты (кг) |
| `weightCategory` | `int` | Категория веса (0-2) |
| `forkliftId` | `string` | ID карщика |
| `fromZone` | `string` | Зона отправления |
| `fromSlot` | `string` | Ячейка отправления |
| `toZone` | `string` | Зона назначения |
| `toSlot` | `string` | Ячейка назначения |
| `distanceMeters` | `number` | Пройденное расстояние (м) |
| `status` | `int` | Финальный статус |
| `durationSec` | `number` | Длительность выполнения (сек) |
| `failureReason` | `string` | Причина ошибки (если есть) |

#### Метрики сборщика (PickerMetricHistory)

```json
{
  "time": "2024-01-15T10:30:00Z",
  "pickerId": "EMP-001",
  "consumptionRate": 5.5,
  "itemsPicked": 45,
  "efficiency": 95.2,
  "active": true
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `time` | `datetime` | Временная метка |
| `pickerId` | `string` | Табельный номер сборщика |
| `consumptionRate` | `number` | Скорость потребления палет (палет/час) |
| `itemsPicked` | `int` | Собрано единиц товара за интервал |
| `efficiency` | `number` | Эффективность (% от нормы) |
| `active` | `bool` | Активен ли сборщик |

#### Снимок буфера (BufferSnapshotHistory)

```json
{
  "time": "2024-01-15T10:30:00Z",
  "bufferLevel": 0.65,
  "bufferState": "Normal",
  "palletsCount": 32,
  "activeForklifts": 3,
  "activePickers": 18,
  "consumptionRate": 180.5,
  "deliveryRate": 175.0,
  "queueLength": 5,
  "pendingTasks": 8
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `time` | `datetime` | Временная метка |
| `bufferLevel` | `number` | Уровень заполнения (0.0-1.0) |
| `bufferState` | `string` | Состояние: Normal, Low, Critical, Overflow |
| `palletsCount` | `int` | Количество палет в буфере |
| `activeForklifts` | `int` | Активных карщиков |
| `activePickers` | `int` | Активных сборщиков |
| `consumptionRate` | `number` | Скорость потребления (палет/час) |
| `deliveryRate` | `number` | Скорость доставки (палет/час) |
| `queueLength` | `int` | Длина очереди заданий |
| `pendingTasks` | `int` | Ожидающих заданий |

---

### API Endpoints для исторических данных

#### `GET /history/tasks`

Получить историю выполненных заданий.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |
| `forkliftId` | `string` | Нет | Фильтр по карщику |
| `status` | `int` | Нет | Фильтр по статусу (3=Completed, 4=Failed) |
| `limit` | `int` | Нет | Максимум записей (по умолчанию 1000) |
| `offset` | `int` | Нет | Смещение для пагинации |

**Пример запроса:**
```http
GET /wms/hs/buffer-api/v1/history/tasks?fromTime=2024-01-01T00:00:00Z&toTime=2024-01-15T23:59:59Z&limit=500
X-API-Key: your-api-key
```

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "id": "TASK-001",
      "createdAt": "2024-01-15T10:30:00Z",
      "startedAt": "2024-01-15T10:31:00Z",
      "completedAt": "2024-01-15T10:35:00Z",
      "palletId": "PAL-001",
      "productType": "SKU-001",
      "productName": "Товар 1",
      "weightKg": 250.5,
      "weightCategory": 2,
      "forkliftId": "FORK-001",
      "fromZone": "STORAGE-A",
      "fromSlot": "A-01-02-03",
      "toZone": "BUFFER",
      "toSlot": "BUF-01",
      "distanceMeters": 45.5,
      "status": 3,
      "durationSec": 240.0,
      "failureReason": null
    }
  ],
  "total": 15420,
  "hasMore": true
}
```

#### `GET /history/tasks/stats`

Получить агрегированную статистику заданий по карщикам.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "forkliftId": "FORK-001",
      "totalTasks": 150,
      "completedTasks": 145,
      "failedTasks": 5,
      "avgDurationSec": 185.5,
      "totalDistanceMeters": 12500.0
    },
    {
      "forkliftId": "FORK-002",
      "totalTasks": 142,
      "completedTasks": 140,
      "failedTasks": 2,
      "avgDurationSec": 192.3,
      "totalDistanceMeters": 11800.0
    }
  ]
}
```

#### `GET /history/tasks/routes`

Получить топ медленных маршрутов для оптимизации.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `lastDays` | `int` | Нет | Количество дней (по умолчанию 7) |
| `limit` | `int` | Нет | Количество маршрутов (по умолчанию 20) |

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "fromZone": "STORAGE-C",
      "toZone": "BUFFER",
      "avgDurationSec": 320.5,
      "taskCount": 85
    },
    {
      "fromZone": "STORAGE-B",
      "toZone": "BUFFER",
      "avgDurationSec": 280.2,
      "taskCount": 120
    }
  ]
}
```

---

#### `GET /history/pickers`

Получить историю метрик сборщиков.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |
| `pickerId` | `string` | Нет | Фильтр по сборщику |
| `interval` | `string` | Нет | Интервал агрегации: `raw`, `1m`, `5m`, `1h` (по умолчанию `5m`) |
| `limit` | `int` | Нет | Максимум записей |

**Пример запроса:**
```http
GET /wms/hs/buffer-api/v1/history/pickers?fromTime=2024-01-15T08:00:00Z&toTime=2024-01-15T20:00:00Z&interval=5m
```

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "time": "2024-01-15T10:30:00Z",
      "pickerId": "EMP-001",
      "consumptionRate": 5.5,
      "itemsPicked": 45,
      "efficiency": 95.2,
      "active": true
    }
  ],
  "total": 2880,
  "hasMore": false
}
```

#### `GET /history/pickers/{pickerId}/hourly`

Получить почасовую статистику конкретного сборщика.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |

**Ответ (200 OK):**
```json
{
  "pickerId": "EMP-001",
  "items": [
    {
      "hour": "2024-01-15T08:00:00Z",
      "avgRate": 5.2,
      "maxRate": 6.8,
      "avgEfficiency": 92.5,
      "samples": 12
    },
    {
      "hour": "2024-01-15T09:00:00Z",
      "avgRate": 5.8,
      "maxRate": 7.2,
      "avgEfficiency": 98.3,
      "samples": 12
    }
  ]
}
```

#### `GET /history/pickers/patterns`

Получить паттерны скорости сборщиков по часам дня (для ML).

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `lastDays` | `int` | Нет | Количество дней для анализа (по умолчанию 30) |

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "pickerId": "EMP-001",
      "hourOfDay": 8,
      "avgConsumptionRate": 4.5,
      "avgEfficiency": 85.0,
      "sampleCount": 22
    },
    {
      "pickerId": "EMP-001",
      "hourOfDay": 9,
      "avgConsumptionRate": 5.8,
      "avgEfficiency": 98.5,
      "sampleCount": 25
    }
  ]
}
```

---

#### `GET /history/buffer`

Получить историю состояния буфера.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |
| `interval` | `string` | Нет | Интервал: `raw`, `1m`, `5m`, `15m`, `1h` (по умолчанию `5m`) |

**Пример запроса:**
```http
GET /wms/hs/buffer-api/v1/history/buffer?fromTime=2024-01-15T00:00:00Z&toTime=2024-01-15T23:59:59Z&interval=15m
```

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "time": "2024-01-15T10:30:00Z",
      "bufferLevel": 0.65,
      "bufferState": "Normal",
      "palletsCount": 32,
      "activeForklifts": 3,
      "activePickers": 18,
      "consumptionRate": 180.5,
      "deliveryRate": 175.0,
      "queueLength": 5,
      "pendingTasks": 8
    }
  ],
  "total": 96,
  "hasMore": false
}
```

#### `GET /history/buffer/stats`

Получить агрегированную статистику буфера за период.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `fromTime` | `datetime` | Да | Начало периода |
| `toTime` | `datetime` | Да | Конец периода |

**Ответ (200 OK):**
```json
{
  "periodStart": "2024-01-15T00:00:00Z",
  "periodEnd": "2024-01-15T23:59:59Z",
  "avgLevel": 0.58,
  "minLevel": 0.15,
  "maxLevel": 0.85,
  "avgConsumption": 175.5,
  "avgDelivery": 178.2,
  "criticalCount": 3
}
```

---

### Экспорт данных для ML обучения

#### `GET /history/export/picker-speed`

Экспорт данных для обучения модели прогноза скорости сборщиков.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `lastDays` | `int` | Нет | Количество дней (по умолчанию 30) |
| `format` | `string` | Нет | Формат: `json` (по умолчанию), `csv` |

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "pickerId": "EMP-001",
      "hourOfDay": 10,
      "dayOfWeek": 1,
      "avgSpeedLast7Days": 5.2,
      "avgSpeedSameHour": 5.5,
      "speed": 5.8
    }
  ],
  "rowCount": 15000,
  "exportedAt": "2024-01-15T12:00:00Z"
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `pickerId` | `string` | ID сборщика |
| `hourOfDay` | `int` | Час дня (0-23) |
| `dayOfWeek` | `int` | День недели (0=Пн, 6=Вс) |
| `avgSpeedLast7Days` | `float` | Средняя скорость за последние 7 дней |
| `avgSpeedSameHour` | `float` | Средняя скорость в тот же час |
| `speed` | `float` | **Target**: фактическая скорость (палет/час) |

#### `GET /history/export/demand`

Экспорт данных для обучения модели прогноза спроса буфера.

**Параметры запроса:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|--------------|----------|
| `lastDays` | `int` | Нет | Количество дней (по умолчанию 30) |
| `format` | `string` | Нет | Формат: `json` (по умолчанию), `csv` |

**Ответ (200 OK):**
```json
{
  "items": [
    {
      "hourOfDay": 10,
      "dayOfWeek": 1,
      "activePickers": 18,
      "avgPickerSpeed": 5.5,
      "bufferLevel": 0.65,
      "demandNext15Min": 12.5
    }
  ],
  "rowCount": 8500,
  "exportedAt": "2024-01-15T12:00:00Z"
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `hourOfDay` | `int` | Час дня (0-23) |
| `dayOfWeek` | `int` | День недели (0=Пн, 6=Вс) |
| `activePickers` | `int` | Количество активных сборщиков |
| `avgPickerSpeed` | `float` | Средняя скорость сборщиков |
| `bufferLevel` | `float` | Уровень буфера (0-1) |
| `demandNext15Min` | `float` | **Target**: спрос за следующие 15 мин (палет) |

---

### Частота синхронизации исторических данных

| Тип данных | Рекомендуемая частота | Описание |
|------------|----------------------|----------|
| История заданий | Каждые 5 минут | Новые завершённые задания |
| Метрики сборщиков | Каждые 1 минуту | Текущая скорость и эффективность |
| Снимки буфера | Каждые 30 секунд | Состояние буфера |
| ML экспорт | Раз в сутки (ночью) | Полный экспорт для переобучения |

### Реализация в 1С — дополнительные шаблоны URL

```
└── Шаблоны URL (дополнение):
    ├── /history/tasks (GET)
    ├── /history/tasks/stats (GET)
    ├── /history/tasks/routes (GET)
    ├── /history/pickers (GET)
    ├── /history/pickers/{pickerId}/hourly (GET)
    ├── /history/pickers/patterns (GET)
    ├── /history/buffer (GET)
    ├── /history/buffer/stats (GET)
    ├── /history/export/picker-speed (GET)
    └── /history/export/demand (GET)
```

### Пример запроса истории в 1С

```bsl
Функция ПолучитьИсториюЗаданий(Запрос)

    Ответ = Новый HTTPСервисОтвет(200);
    Ответ.Заголовки.Вставить("Content-Type", "application/json; charset=utf-8");

    НачалоПериода = Дата(Запрос.ПараметрыЗапроса.Получить("fromTime"));
    КонецПериода = Дата(Запрос.ПараметрыЗапроса.Получить("toTime"));
    Лимит = Число(Запрос.ПараметрыЗапроса.Получить("limit"));
    Если Лимит = 0 Тогда Лимит = 1000; КонецЕсли;

    Запрос = Новый Запрос;
    Запрос.Текст =
    "ВЫБРАТЬ ПЕРВЫЕ &Лимит
    |   Задания.Ссылка.УникальныйИдентификатор КАК id,
    |   Задания.ДатаСоздания КАК createdAt,
    |   Задания.ДатаНачала КАК startedAt,
    |   Задания.ДатаЗавершения КАК completedAt,
    |   Задания.Палета.Код КАК palletId,
    |   Задания.Палета.Номенклатура.Артикул КАК productType,
    |   Задания.Палета.Номенклатура.Наименование КАК productName,
    |   Задания.Палета.ВесБрутто КАК weightKg,
    |   ВЫБОР
    |       КОГДА Задания.Палета.ВесБрутто >= 20 ТОГДА 2
    |       КОГДА Задания.Палета.ВесБрутто >= 5 ТОГДА 1
    |       ИНАЧЕ 0
    |   КОНЕЦ КАК weightCategory,
    |   Задания.Карщик.Код КАК forkliftId,
    |   Задания.ЗонаОтправления.Код КАК fromZone,
    |   Задания.ЯчейкаОтправления.Код КАК fromSlot,
    |   Задания.ЗонаНазначения.Код КАК toZone,
    |   Задания.ЯчейкаНазначения.Код КАК toSlot,
    |   Задания.Расстояние КАК distanceMeters,
    |   Задания.Статус КАК status,
    |   РАЗНОСТЬДАТ(Задания.ДатаНачала, Задания.ДатаЗавершения, СЕКУНДА) КАК durationSec,
    |   Задания.ПричинаОшибки КАК failureReason
    |ИЗ
    |   Документ.ЗаданиеНаПеремещение КАК Задания
    |ГДЕ
    |   Задания.ДатаЗавершения >= &НачалоПериода
    |   И Задания.ДатаЗавершения <= &КонецПериода
    |   И Задания.Статус В (
    |       ЗНАЧЕНИЕ(Перечисление.СтатусыЗаданий.Завершено),
    |       ЗНАЧЕНИЕ(Перечисление.СтатусыЗаданий.Ошибка))
    |УПОРЯДОЧИТЬ ПО
    |   Задания.ДатаЗавершения";

    Запрос.УстановитьПараметр("НачалоПериода", НачалоПериода);
    Запрос.УстановитьПараметр("КонецПериода", КонецПериода);
    Запрос.УстановитьПараметр("Лимит", Лимит);

    Выборка = Запрос.Выполнить().Выбрать();

    // Формируем JSON массив
    МассивЗаданий = Новый Массив;
    Пока Выборка.Следующий() Цикл
        Задание = Новый Структура;
        Задание.Вставить("id", Строка(Выборка.id));
        Задание.Вставить("createdAt", ФорматДатыISO8601(Выборка.createdAt));
        Задание.Вставить("startedAt", ФорматДатыISO8601(Выборка.startedAt));
        Задание.Вставить("completedAt", ФорматДатыISO8601(Выборка.completedAt));
        Задание.Вставить("palletId", Выборка.palletId);
        Задание.Вставить("productType", Выборка.productType);
        Задание.Вставить("productName", Выборка.productName);
        Задание.Вставить("weightKg", Выборка.weightKg);
        Задание.Вставить("weightCategory", Выборка.weightCategory);
        Задание.Вставить("forkliftId", Выборка.forkliftId);
        Задание.Вставить("fromZone", Выборка.fromZone);
        Задание.Вставить("fromSlot", Выборка.fromSlot);
        Задание.Вставить("toZone", Выборка.toZone);
        Задание.Вставить("toSlot", Выборка.toSlot);
        Задание.Вставить("distanceMeters", Выборка.distanceMeters);
        Задание.Вставить("status", СтатусВЧисло(Выборка.status));
        Задание.Вставить("durationSec", Выборка.durationSec);
        Задание.Вставить("failureReason", Выборка.failureReason);
        МассивЗаданий.Добавить(Задание);
    КонецЦикла;

    Результат = Новый Структура;
    Результат.Вставить("items", МассивЗаданий);
    Результат.Вставить("total", МассивЗаданий.Количество());
    Результат.Вставить("hasMore", МассивЗаданий.Количество() = Лимит);

    Ответ.УстановитьТелоИзСтроки(СтруктуруВJSON(Результат));
    Возврат Ответ;

КонецФункции
```

---

## Коды ошибок

| HTTP Код | Описание |
|----------|----------|
| 200 | Успешно |
| 201 | Создано |
| 204 | Нет содержимого |
| 400 | Неверный запрос |
| 401 | Не авторизован |
| 403 | Доступ запрещён |
| 404 | Не найдено |
| 409 | Конфликт (например, палета уже зарезервирована) |
| 500 | Внутренняя ошибка сервера |

**Формат ошибки:**
```json
{
  "error": {
    "code": "PALLET_ALREADY_RESERVED",
    "message": "Палета PAL-001 уже зарезервирована карщиком FORK-002",
    "details": {
      "palletId": "PAL-001",
      "reservedBy": "FORK-002",
      "reservedUntil": "2024-01-15T10:50:00Z"
    }
  }
}
```

---

## Реализация в 1С

### Структура HTTP-сервиса

```
Конфигурация
└── HTTP-сервисы
    └── BufferAPI
        ├── Корневой URL: /wms/hs/buffer-api/v1
        └── Шаблоны URL:
            ├── /health (GET)
            ├── /orders (GET)
            ├── /orders/{orderId} (GET)
            ├── /waves/next (GET)
            ├── /waves/{waveId}/start (POST)
            ├── /waves/{waveId}/complete (POST)
            ├── /pallets (GET)
            ├── /pallets/{palletId} (GET)
            ├── /pallets/{palletId}/reserve (POST, DELETE)
            ├── /pallets/{palletId}/delivery (POST)
            ├── /pallets/{palletId}/consume (POST)
            ├── /pickers (GET)
            ├── /pickers/{pickerId}/stats (GET)
            ├── /forklifts (GET)
            ├── /forklifts/{forkliftId}/status (PUT)
            ├── /tasks (GET, POST)
            └── /tasks/{taskId}/status (PUT)
```

### Пример модуля HTTP-сервиса (1С)

```bsl
// Модуль HTTP-сервиса BufferAPI

Функция ПолучитьПалеты(Запрос)

    Ответ = Новый HTTPСервисОтвет(200);
    Ответ.Заголовки.Вставить("Content-Type", "application/json; charset=utf-8");

    // Получаем параметры
    ФильтрЗона = Запрос.ПараметрыЗапроса.Получить("zone");
    ФильтрСтатус = Запрос.ПараметрыЗапроса.Получить("status");

    // Формируем запрос к БД
    Запрос = Новый Запрос;
    Запрос.Текст =
    "ВЫБРАТЬ
    |   Палеты.Код КАК id,
    |   Палеты.Номенклатура.Артикул КАК productId,
    |   Палеты.Номенклатура.Наименование КАК productName,
    |   Палеты.Количество КАК quantity,
    |   Палеты.ВесБрутто КАК weightKg,
    |   Палеты.Зона.Код КАК currentZone,
    |   Палеты.Ячейка.Код КАК currentSlot,
    |   Палеты.КоординатаX КАК x,
    |   Палеты.КоординатаY КАК y,
    |   Палеты.КоординатаZ КАК z,
    |   Палеты.ДатаПеремещения КАК lastMovedAt,
    |   Палеты.Статус КАК status
    |ИЗ
    |   РегистрСведений.РазмещениеПалет КАК Палеты
    |ГДЕ
    |   &УсловиеЗона
    |   И &УсловиеСтатус";

    // ... формирование результата ...

    РезультатJSON = СформироватьJSONПалеты(Выборка);
    Ответ.УстановитьТелоИзСтроки(РезультатJSON);

    Возврат Ответ;

КонецФункции
```

---

## Требования к производительности

| Метрика | Требование |
|---------|------------|
| Время ответа GET запросов | < 200 мс |
| Время ответа POST/PUT запросов | < 500 мс |
| Доступность API | 99.5% |
| Частота polling (если без webhooks) | 1 раз в секунду |
| Максимальный размер ответа | 1 МБ |

---

## Контакты

При возникновении вопросов по интеграции обращайтесь к команде разработки Buffer Management System.
