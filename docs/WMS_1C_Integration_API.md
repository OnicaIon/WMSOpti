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
