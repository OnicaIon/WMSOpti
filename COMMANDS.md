# WMS Buffer Management - Команды запуска

## Синхронизация данных

```bash
# Синхронизация задач (инкрементально, добавляет новые)
dotnet run -- --sync-tasks

# Полная перезагрузка задач (очистка + загрузка всех)
dotnet run -- --truncate-tasks --sync-tasks

# Синхронизация зон и ячеек
dotnet run -- --sync-zones --sync-cells

# Синхронизация продуктов
dotnet run -- --sync-products

# Синхронизация всего
dotnet run -- --sync-all
```

## Расчёт статистики

```bash
# Пересчёт всей статистики
dotnet run -- --calc

# Только статистика работников
dotnet run -- --calc-workers

# Только статистика маршрутов (IQR нормализация)
dotnet run -- --calc-routes

# Только статистика пикер-товар
dotnet run -- --calc-picker-product
```

## ML обучение

```bash
# Обучение моделей (picker + forklift)
dotnet run -- --train-ml
```

## Полный сервис

```bash
# Запуск полного сервиса (sync + buffer management)
dotnet run
```

## Справка

```bash
dotnet run -- --help
```

---

## Важно

- `--truncate-tasks` — УДАЛЯЕТ ВСЕ ЗАДАЧИ перед синхронизацией
- `--sync-tasks` без truncate — добавляет только новые записи
- `TruncateBeforeSync` в appsettings.json должен быть `false` для инкрементальной загрузки
