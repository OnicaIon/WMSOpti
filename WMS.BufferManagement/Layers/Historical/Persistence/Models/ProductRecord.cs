namespace WMS.BufferManagement.Layers.Historical.Persistence.Models;

/// <summary>
/// Product record for database storage
/// </summary>
public class ProductRecord
{
    public string Code { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ExternalCode { get; set; }
    public string? VendorCode { get; set; }
    public string? Barcode { get; set; }
    public decimal WeightKg { get; set; }
    public decimal VolumeM3 { get; set; }
    public int WeightCategory { get; set; }
    public string? CategoryCode { get; set; }
    public string? CategoryName { get; set; }
    public int MaxQtyPerPallet { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}
