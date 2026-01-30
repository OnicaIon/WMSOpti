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
/// Запись задания из WMS
/// </summary>
public class WmsTaskRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("modifiedAt")]
    public DateTime? ModifiedAt { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("palletId")]
    public string PalletId { get; set; } = string.Empty;

    [JsonPropertyName("productSku")]
    public string? ProductSku { get; set; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("weightKg")]
    public double WeightKg { get; set; }

    [JsonPropertyName("forkliftId")]
    public string? ForkliftId { get; set; }

    [JsonPropertyName("forkliftOperator")]
    public string? ForkliftOperator { get; set; }

    [JsonPropertyName("fromZone")]
    public string? FromZone { get; set; }

    [JsonPropertyName("fromSlot")]
    public string? FromSlot { get; set; }

    [JsonPropertyName("fromX")]
    public double? FromX { get; set; }

    [JsonPropertyName("fromY")]
    public double? FromY { get; set; }

    [JsonPropertyName("toZone")]
    public string? ToZone { get; set; }

    [JsonPropertyName("toSlot")]
    public string? ToSlot { get; set; }

    [JsonPropertyName("toX")]
    public double? ToX { get; set; }

    [JsonPropertyName("toY")]
    public double? ToY { get; set; }

    [JsonPropertyName("distanceMeters")]
    public double? DistanceMeters { get; set; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }
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
