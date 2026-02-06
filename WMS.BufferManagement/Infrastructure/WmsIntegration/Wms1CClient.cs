using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WMS.BufferManagement.Infrastructure.WmsIntegration;

/// <summary>
/// Настройки подключения к WMS 1C
/// </summary>
public class Wms1CSettings
{
    public string BaseUrl { get; set; } = "http://localhost:8080/wms/hs/buffer-api/v1";
    public string ApiKey { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int DefaultLimit { get; set; } = 1000;
}

/// <summary>
/// Ответ API с пагинацией
/// </summary>
public class PagedResponse<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = new();

    [JsonPropertyName("lastId")]
    public string? LastId { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

/// <summary>
/// Запись задания из WMS (документ "Task action" / "Действие задания")
/// Соответствует структуре документа в 1С WMS AURORA PROD
/// </summary>
public class WmsTaskRecord
{
    /// <summary>Номер документа</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>UUID действия (ActionId)</summary>
    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    /// <summary>UUID плана действия (PlanActionId)</summary>
    [JsonPropertyName("planActionId")]
    public string? PlanActionId { get; set; }

    /// <summary>Дата создания документа</summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>Время начала выполнения (StartedAt)</summary>
    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    /// <summary>Время завершения (CompletedAt)</summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>Длительность выполнения в секундах</summary>
    [JsonPropertyName("durationSec")]
    public double? DurationSec { get; set; }

    /// <summary>Номер основания задания (Task basis)</summary>
    [JsonPropertyName("taskBasisNumber")]
    public string? TaskBasisNumber { get; set; }

    /// <summary>Код шаблона действия (Template.Code): 029=Forklift, 031=Picker</summary>
    [JsonPropertyName("templateCode")]
    public string? TemplateCode { get; set; }

    /// <summary>Название шаблона действия (Template)</summary>
    [JsonPropertyName("templateName")]
    public string? TemplateName { get; set; }

    /// <summary>Определяет роль работника по коду шаблона</summary>
    public string WorkerRole => TemplateCode switch
    {
        "029" => "Forklift",  // Distribution replenish
        "031" => "Picker",    // Put product into the bin
        _ => "Unknown"
    };

    /// <summary>Тип действия (Action type): PUT_INTO, TAKE_FROM и т.д.</summary>
    [JsonPropertyName("actionType")]
    public string? ActionType { get; set; }

    /// <summary>Ячейка хранения - источник (Storage bin)</summary>
    [JsonPropertyName("storageBinCode")]
    public string? StorageBinCode { get; set; }

    /// <summary>Палета хранения - источник (Storage pallet)</summary>
    [JsonPropertyName("storagePalletCode")]
    public string? StoragePalletCode { get; set; }

    /// <summary>SKU товара (Storage product)</summary>
    [JsonPropertyName("productSku")]
    public string? ProductSku { get; set; }

    /// <summary>Наименование товара (Storage product)</summary>
    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    /// <summary>Код товара (из rtProducts)</summary>
    [JsonPropertyName("productCode")]
    public string? ProductCode { get; set; }

    /// <summary>Вес единицы товара (кг)</summary>
    [JsonPropertyName("productWeight")]
    public double ProductWeight { get; set; }

    /// <summary>Количество (Qty)</summary>
    [JsonPropertyName("qty")]
    public double Qty { get; set; }

    /// <summary>Ячейка размещения - назначение (Allocation bin)</summary>
    [JsonPropertyName("allocationBinCode")]
    public string? AllocationBinCode { get; set; }

    /// <summary>Палета размещения - назначение (Allocation pallet)</summary>
    [JsonPropertyName("allocationPalletCode")]
    public string? AllocationPalletCode { get; set; }

    /// <summary>Код исполнителя (Assignee)</summary>
    [JsonPropertyName("assigneeCode")]
    public string? AssigneeCode { get; set; }

    /// <summary>Имя исполнителя (Assignee)</summary>
    [JsonPropertyName("assigneeName")]
    public string? AssigneeName { get; set; }

    /// <summary>Статус: 0=New, 2=InProgress, 3=Completed</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }
}

/// <summary>
/// Сборщик из WMS
/// </summary>
public class WmsPickerRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("zone")]
    public string? Zone { get; set; }

    [JsonPropertyName("currentOrderId")]
    public string? CurrentOrderId { get; set; }

    [JsonPropertyName("shiftStart")]
    public DateTime? ShiftStart { get; set; }

    [JsonPropertyName("shiftEnd")]
    public DateTime? ShiftEnd { get; set; }

    [JsonPropertyName("palletsToday")]
    public int PalletsToday { get; set; }

    [JsonPropertyName("itemsToday")]
    public int ItemsToday { get; set; }
}

/// <summary>
/// Событие активности сборщика
/// </summary>
public class WmsPickerActivityRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("pickerId")]
    public string PickerId { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("palletId")]
    public string? PalletId { get; set; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    [JsonPropertyName("itemsPicked")]
    public int? ItemsPicked { get; set; }

    [JsonPropertyName("durationSec")]
    public double? DurationSec { get; set; }
}

/// <summary>
/// Карщик из WMS
/// </summary>
public class WmsForkliftRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("operatorId")]
    public string? OperatorId { get; set; }

    [JsonPropertyName("operatorName")]
    public string? OperatorName { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("currentTaskId")]
    public string? CurrentTaskId { get; set; }

    [JsonPropertyName("positionX")]
    public double? PositionX { get; set; }

    [JsonPropertyName("positionY")]
    public double? PositionY { get; set; }

    [JsonPropertyName("lastUpdateAt")]
    public DateTime? LastUpdateAt { get; set; }
}

/// <summary>
/// Снимок буфера из WMS
/// </summary>
public class WmsBufferSnapshotRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("currentCount")]
    public int CurrentCount { get; set; }

    [JsonPropertyName("activeForklifts")]
    public int ActiveForklifts { get; set; }

    [JsonPropertyName("activePickers")]
    public int ActivePickers { get; set; }
}

/// <summary>
/// Текущее состояние буфера
/// </summary>
public class WmsBufferState
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("currentCount")]
    public int CurrentCount { get; set; }

    [JsonPropertyName("pallets")]
    public List<WmsBufferPallet> Pallets { get; set; } = new();
}

public class WmsBufferPallet
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("productSku")]
    public string? ProductSku { get; set; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("weightKg")]
    public double WeightKg { get; set; }

    [JsonPropertyName("arrivedAt")]
    public DateTime ArrivedAt { get; set; }
}

/// <summary>
/// Интерфейс клиента WMS 1C
/// </summary>
public interface IWms1CClient
{
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
    Task<PagedResponse<WmsTaskRecord>> GetTasksAsync(string? afterId = null, int? limit = null, CancellationToken ct = default);
    Task<PagedResponse<WmsPickerRecord>> GetPickersAsync(string? afterId = null, CancellationToken ct = default);
    Task<PagedResponse<WmsPickerActivityRecord>> GetPickerActivityAsync(string? afterId = null, DateTime? fromTime = null, CancellationToken ct = default);
    Task<PagedResponse<WmsForkliftRecord>> GetForkliftsAsync(CancellationToken ct = default);
    Task<WmsBufferState?> GetBufferStateAsync(CancellationToken ct = default);
    Task<PagedResponse<WmsBufferSnapshotRecord>> GetBufferSnapshotsAsync(string? afterId = null, DateTime? fromTime = null, CancellationToken ct = default);
    Task<string> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default);
    Task UpdateTaskStatusAsync(string taskId, int status, string? failureReason = null, CancellationToken ct = default);

    // Справочники
    Task<PagedResponse<WmsWorkerRecord>> GetWorkersAsync(string? afterId = null, string? group = null, CancellationToken ct = default);
    Task<PagedResponse<WmsCellRecord>> GetCellsAsync(string? afterId = null, string? zoneCode = null, int? limit = null, CancellationToken ct = default);
    Task<PagedResponse<WmsZoneRecord>> GetZonesAsync(CancellationToken ct = default);
    Task<WmsStatistics?> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get products catalog (Nomenclature) with weight data for Heavy-on-Bottom rule
    /// </summary>
    Task<PagedResponse<WmsProductRecord>> GetProductsAsync(
        string? afterId = null,
        string? categoryCode = null,
        DateTime? modifiedAfter = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>
    /// Получить задания волны дистрибьюции по номеру волны
    /// GET /wave-tasks?wave={waveNumber}
    /// </summary>
    Task<Services.Backtesting.WaveTasksResponse?> GetWaveTasksAsync(
        string waveNumber,
        CancellationToken ct = default);
}

/// <summary>
/// Работник WMS (Mobile terminal user)
/// </summary>
public class WmsWorkerRecord
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("cellCode")]
    public string? CellCode { get; set; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; set; }
}

/// <summary>
/// Ячейка склада
/// </summary>
public class WmsCellRecord
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("zoneCode")]
    public string? ZoneCode { get; set; }

    [JsonPropertyName("zoneName")]
    public string? ZoneName { get; set; }

    [JsonPropertyName("cellType")]
    public string? CellType { get; set; }

    [JsonPropertyName("indexNumber")]
    public int IndexNumber { get; set; }

    [JsonPropertyName("inactive")]
    public bool Inactive { get; set; }

    [JsonPropertyName("aisle")]
    public string? Aisle { get; set; }

    [JsonPropertyName("rack")]
    public string? Rack { get; set; }

    [JsonPropertyName("shelf")]
    public string? Shelf { get; set; }

    [JsonPropertyName("position")]
    public string? Position { get; set; }

    [JsonPropertyName("pickingRoute")]
    public string? PickingRoute { get; set; }

    [JsonPropertyName("maxWeightKg")]
    public decimal MaxWeightKg { get; set; }

    [JsonPropertyName("volumeM3")]
    public decimal VolumeM3 { get; set; }
}

/// <summary>
/// Зона склада
/// </summary>
public class WmsZoneRecord
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("warehouseName")]
    public string? WarehouseName { get; set; }

    [JsonPropertyName("zoneType")]
    public string? ZoneType { get; set; }

    [JsonPropertyName("defaultCellCode")]
    public string? DefaultCellCode { get; set; }

    [JsonPropertyName("cellCodeTemplate")]
    public string? CellCodeTemplate { get; set; }

    [JsonPropertyName("cellBarcodeTemplate")]
    public string? CellBarcodeTemplate { get; set; }

    [JsonPropertyName("pickingRoute")]
    public string? PickingRoute { get; set; }

    [JsonPropertyName("extCode")]
    public string? ExtCode { get; set; }

    [JsonPropertyName("indexNumber")]
    public int IndexNumber { get; set; }
}

/// <summary>
/// Product from WMS (Nomenclature catalog)
/// Used for Heavy-on-Bottom rule and weight-based optimization
/// </summary>
public class WmsProductRecord
{
    /// <summary>Internal product code</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Product SKU</summary>
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    /// <summary>External code (from ERP)</summary>
    [JsonPropertyName("externalCode")]
    public string? ExternalCode { get; set; }

    /// <summary>Vendor code</summary>
    [JsonPropertyName("vendorCode")]
    public string? VendorCode { get; set; }

    /// <summary>Product barcode</summary>
    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    /// <summary>Product name</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Weight in kg (critical for Heavy-on-Bottom rule)</summary>
    [JsonPropertyName("weightKg")]
    public double WeightKg { get; set; }

    /// <summary>Volume in m³</summary>
    [JsonPropertyName("volumeM3")]
    public double VolumeM3 { get; set; }

    /// <summary>Weight category: 0=Light, 1=Medium, 2=Heavy</summary>
    [JsonPropertyName("weightCategory")]
    public int WeightCategory { get; set; }

    /// <summary>Category code</summary>
    [JsonPropertyName("categoryCode")]
    public string? CategoryCode { get; set; }

    /// <summary>Category name</summary>
    [JsonPropertyName("categoryName")]
    public string? CategoryName { get; set; }

    /// <summary>Maximum quantity per pallet</summary>
    [JsonPropertyName("maxQtyPerPallet")]
    public int MaxQtyPerPallet { get; set; }

    /// <summary>Picking type (pieces, boxes, pallets)</summary>
    [JsonPropertyName("pickingType")]
    public string? PickingType { get; set; }

    /// <summary>Unit of measure</summary>
    [JsonPropertyName("unitName")]
    public string? UnitName { get; set; }

    /// <summary>Last modification timestamp</summary>
    [JsonPropertyName("modifiedAt")]
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>
/// Статистика WMS
/// </summary>
public class WmsStatistics
{
    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("byStatus")]
    public Dictionary<string, int> ByStatus { get; set; } = new();
}

public class CreateTaskRequest
{
    [JsonPropertyName("palletId")]
    public string PalletId { get; set; } = string.Empty;

    [JsonPropertyName("fromZone")]
    public string FromZone { get; set; } = string.Empty;

    [JsonPropertyName("fromSlot")]
    public string FromSlot { get; set; } = string.Empty;

    [JsonPropertyName("toZone")]
    public string ToZone { get; set; } = string.Empty;

    [JsonPropertyName("toSlot")]
    public string ToSlot { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("forkliftId")]
    public string? ForkliftId { get; set; }
}

/// <summary>
/// HTTP клиент для WMS 1C API
/// </summary>
public class Wms1CClient : IWms1CClient
{
    private readonly HttpClient _http;
    private readonly Wms1CSettings _settings;
    private readonly ILogger<Wms1CClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public Wms1CClient(
        HttpClient http,
        IOptions<Wms1CSettings> settings,
        ILogger<Wms1CClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        _http.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);

        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            _http.DefaultRequestHeaders.Add("X-API-Key", _settings.ApiKey);
        }

        if (!string.IsNullOrEmpty(_settings.Username) && !string.IsNullOrEmpty(_settings.Password))
        {
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMS health check failed");
            return false;
        }
    }

    public async Task<PagedResponse<WmsTaskRecord>> GetTasksAsync(
        string? afterId = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("tasks", afterId, limit);
        return await GetAsync<PagedResponse<WmsTaskRecord>>(url, ct) ?? new PagedResponse<WmsTaskRecord>();
    }

    public async Task<PagedResponse<WmsPickerRecord>> GetPickersAsync(
        string? afterId = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("pickers", afterId);
        return await GetAsync<PagedResponse<WmsPickerRecord>>(url, ct) ?? new PagedResponse<WmsPickerRecord>();
    }

    public async Task<PagedResponse<WmsPickerActivityRecord>> GetPickerActivityAsync(
        string? afterId = null,
        DateTime? fromTime = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("picker-activity", afterId);
        if (fromTime.HasValue)
        {
            url += $"&fromTime={fromTime.Value:O}";
        }
        return await GetAsync<PagedResponse<WmsPickerActivityRecord>>(url, ct) ?? new PagedResponse<WmsPickerActivityRecord>();
    }

    public async Task<PagedResponse<WmsForkliftRecord>> GetForkliftsAsync(CancellationToken ct = default)
    {
        return await GetAsync<PagedResponse<WmsForkliftRecord>>("forklifts", ct) ?? new PagedResponse<WmsForkliftRecord>();
    }

    public async Task<WmsBufferState?> GetBufferStateAsync(CancellationToken ct = default)
    {
        return await GetAsync<WmsBufferState>("buffer", ct);
    }

    public async Task<PagedResponse<WmsBufferSnapshotRecord>> GetBufferSnapshotsAsync(
        string? afterId = null,
        DateTime? fromTime = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("buffer-snapshots", afterId);
        if (fromTime.HasValue)
        {
            url += $"&fromTime={fromTime.Value:O}";
        }
        return await GetAsync<PagedResponse<WmsBufferSnapshotRecord>>(url, ct) ?? new PagedResponse<WmsBufferSnapshotRecord>();
    }

    public async Task<string> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("tasks", request, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions, ct);
        return result.GetProperty("taskId").GetString() ?? throw new InvalidOperationException("No taskId in response");
    }

    public async Task UpdateTaskStatusAsync(
        string taskId,
        int status,
        string? failureReason = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            status,
            completedAt = status == 3 ? DateTime.UtcNow : (DateTime?)null,
            failureReason
        };

        var response = await _http.PutAsJsonAsync($"tasks/{taskId}/status", payload, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<PagedResponse<WmsWorkerRecord>> GetWorkersAsync(
        string? afterId = null,
        string? group = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("workers", afterId);
        if (!string.IsNullOrEmpty(group))
        {
            url += $"&group={Uri.EscapeDataString(group)}";
        }
        return await GetAsync<PagedResponse<WmsWorkerRecord>>(url, ct) ?? new PagedResponse<WmsWorkerRecord>();
    }

    public async Task<PagedResponse<WmsCellRecord>> GetCellsAsync(
        string? afterId = null,
        string? zoneCode = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("cells", afterId, limit ?? 10000);
        if (!string.IsNullOrEmpty(zoneCode))
        {
            url += $"&zone={Uri.EscapeDataString(zoneCode)}";
        }
        return await GetAsync<PagedResponse<WmsCellRecord>>(url, ct) ?? new PagedResponse<WmsCellRecord>();
    }

    public async Task<PagedResponse<WmsZoneRecord>> GetZonesAsync(CancellationToken ct = default)
    {
        return await GetAsync<PagedResponse<WmsZoneRecord>>("zones", ct) ?? new PagedResponse<WmsZoneRecord>();
    }

    public async Task<WmsStatistics?> GetStatisticsAsync(CancellationToken ct = default)
    {
        return await GetAsync<WmsStatistics>("statistics", ct);
    }

    public async Task<PagedResponse<WmsProductRecord>> GetProductsAsync(
        string? afterId = null,
        string? categoryCode = null,
        DateTime? modifiedAfter = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl("products", afterId, limit);

        if (!string.IsNullOrEmpty(categoryCode))
        {
            url += $"&category={Uri.EscapeDataString(categoryCode)}";
        }

        if (modifiedAfter.HasValue)
        {
            url += $"&modifiedAfter={modifiedAfter.Value:O}";
        }

        return await GetAsync<PagedResponse<WmsProductRecord>>(url, ct) ?? new PagedResponse<WmsProductRecord>();
    }

    public async Task<Services.Backtesting.WaveTasksResponse?> GetWaveTasksAsync(
        string waveNumber,
        CancellationToken ct = default)
    {
        var url = $"wave-tasks?wave={Uri.EscapeDataString(waveNumber.Trim())}";
        return await GetAsync<Services.Backtesting.WaveTasksResponse>(url, ct);
    }

    private string BuildUrl(string endpoint, string? afterId = null, int? limit = null)
    {
        var url = endpoint;
        var hasParams = false;

        if (!string.IsNullOrEmpty(afterId))
        {
            url += $"?after={Uri.EscapeDataString(afterId)}";
            hasParams = true;
        }

        if (limit.HasValue)
        {
            url += hasParams ? "&" : "?";
            url += $"limit={limit.Value}";
        }
        else
        {
            url += hasParams ? "&" : "?";
            url += $"limit={_settings.DefaultLimit}";
        }

        return url;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("WMS API error: {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling WMS API: {Url}", url);
            throw;
        }
    }
}
