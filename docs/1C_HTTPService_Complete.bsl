// ============================================================================
// HTTP Service Module: BufferAPI (COMPLETE VERSION)
// 1C:Enterprise 8.3
// ============================================================================

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

Function FormatDateISO8601(Date)
    If Not ValueIsFilled(Date) Then
        Return "";
    EndIf;
    Return Format(Date, "DF=yyyy-MM-ddTHH:mm:ssZ");
EndFunction

Function StructureToJSON(Structure)
    JSONWriter = New JSONWriter;
    JSONWriter.SetString();
    WriteJSON(JSONWriter, Structure);
    Return JSONWriter.Close();
EndFunction

Function CreateResponse(StatusCode, Body = Undefined)
    Response = New HTTPServiceResponse(StatusCode);
    Response.Headers.Insert("Content-Type", "application/json; charset=utf-8");
    Response.Headers.Insert("Access-Control-Allow-Origin", "*");

    If Body <> Undefined Then
        If TypeOf(Body) = Type("String") Then
            Response.SetBodyFromString(Body);
        Else
            Response.SetBodyFromString(StructureToJSON(Body));
        EndIf;
    EndIf;

    Return Response;
EndFunction

Function CreateErrorResponse(StatusCode, ErrorCode, Message)
    ErrorStructure = New Structure;
    ErrorStructure.Insert("code", ErrorCode);
    ErrorStructure.Insert("message", Message);

    Result = New Structure;
    Result.Insert("error", ErrorStructure);

    Return CreateResponse(StatusCode, Result);
EndFunction

Function CheckAuthentication(Request)
    // Allow all requests for now
    Return True;
EndFunction

Function GetQueryParameter(Request, ParameterName, DefaultValue = Undefined)
    Value = Request.QueryOptions.Get(ParameterName);
    If Value = Undefined Then
        Return DefaultValue;
    EndIf;
    Return Value;
EndFunction

Function GetNumericParameter(Request, ParameterName, DefaultValue = 0)
    Value = GetQueryParameter(Request, ParameterName);
    If Value = Undefined Then
        Return DefaultValue;
    EndIf;
    Try
        Return Number(Value);
    Except
        Return DefaultValue;
    EndTry;
EndFunction

Function GetWeightCategory(WeightKg)
    If WeightKg >= 20 Then
        Return 2; // Heavy
    ElsIf WeightKg >= 5 Then
        Return 1; // Medium
    Else
        Return 0; // Light
    EndIf;
EndFunction

// ============================================================================
// ENDPOINT: GET /health
// ============================================================================

Function GetHealth(Request)
    Result = New Structure;
    Result.Insert("status", "ok");
    Result.Insert("timestamp", FormatDateISO8601(CurrentUniversalDate()));
    Result.Insert("version", "1.0");

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /products
// ============================================================================

Function GetProducts(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    If Limit > 10000 Then
        Limit = 10000;
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT TOP " + Format(Limit + 1, "NG=0") + "
    |   Products.Code AS code,
    |   Products.Description AS name,
    |   ISNULL(Products.SKU, """") AS sku,
    |   ISNULL(Products.ExternalCode, """") AS externalCode,
    |   ISNULL(Products.VendorCode, """") AS vendorCode,
    |   ISNULL(Products.Barcode, """") AS barcode,
    |   ISNULL(Products.Weight, 0) AS weightKg,
    |   ISNULL(Products.Volume, 0) AS volumeM3,
    |   ISNULL(Products.MaxQtyPerPallet, 0) AS maxQtyPerPallet,
    |   ISNULL(Products.Category.Code, """") AS categoryCode,
    |   ISNULL(Products.Category.Description, """") AS categoryName
    |FROM
    |   Catalog.rtProducts AS Products
    |WHERE
    |   Products.Code > &AfterID
    |   AND NOT Products.DeletionMark
    |ORDER BY
    |   Products.Code";

    Query.SetParameter("AfterID", AfterID);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("code", Selection.code);
        Item.Insert("name", Selection.name);
        Item.Insert("sku", Selection.sku);
        Item.Insert("externalCode", Selection.externalCode);
        Item.Insert("vendorCode", Selection.vendorCode);
        Item.Insert("barcode", Selection.barcode);
        Item.Insert("weightKg", Selection.weightKg);
        Item.Insert("volumeM3", Selection.volumeM3);
        Item.Insert("weightCategory", GetWeightCategory(Selection.weightKg));
        Item.Insert("categoryCode", Selection.categoryCode);
        Item.Insert("categoryName", Selection.categoryName);
        Item.Insert("maxQtyPerPallet", Selection.maxQtyPerPallet);

        ItemsArray.Add(Item);
        LastID = Selection.code;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /tasks
// ============================================================================

Function GetTasks(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    If Limit > 10000 Then
        Limit = 10000;
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT TOP " + String(Limit + 1) + "
    |   Tasks.Number AS id,
    |   Tasks.Date AS createdAt,
    |   Tasks.ActionId AS actionId,
    |   Tasks.StartedAt AS startedAt,
    |   Tasks.CompletedAt AS completedAt,
    |   ISNULL(Tasks.StorageBin.Code, """") AS storageBinCode,
    |   ISNULL(Tasks.StoragePallet.Code, """") AS storagePalletCode,
    |   ISNULL(Tasks.AllocationBin.Code, """") AS allocationBinCode,
    |   ISNULL(Tasks.AllocationPallet.Code, """") AS allocationPalletCode,
    |   CASE
    |       WHEN Tasks.StorageProductClassifier.Description <> """"
    |           THEN Tasks.StorageProductClassifier.Description
    |       WHEN Tasks.StorageProduct.Product.Description <> """"
    |           THEN Tasks.StorageProduct.Product.Description
    |       ELSE """"
    |   END AS productName,
    |   CASE
    |       WHEN Tasks.StorageProductClassifier.SKU <> """"
    |           THEN Tasks.StorageProductClassifier.SKU
    |       WHEN Tasks.StorageProduct.Product.SKU <> """"
    |           THEN Tasks.StorageProduct.Product.SKU
    |       ELSE """"
    |   END AS productSku,
    |   ISNULL(Tasks.StorageProduct.Product.Code, """") AS productCode,
    |   ISNULL(Tasks.StorageProduct.Product.Weight, 0) AS productWeight,
    |   Tasks.Qty AS qty,
    |   ISNULL(Tasks.Assignee.Code, """") AS assigneeCode,
    |   ISNULL(Tasks.Assignee.Description, """") AS assigneeName,
    |   ISNULL(Tasks.Template.Code, """") AS templateCode,
    |   ISNULL(Tasks.Template.Description, """") AS templateName,
    |   Tasks.TaskBasis AS taskBasis,
    |   ISNULL(Tasks.TaskBasis.Number, """") AS taskBasisNumber,
    |   ISNULL(Tasks.PlanActionId, """") AS planActionId
    |FROM
    |   Document.sgTaskAction AS Tasks
    |WHERE
    |   Tasks.Number > &AfterID
    |   AND NOT Tasks.DeletionMark
    |ORDER BY
    |   Tasks.Number";

    Query.SetParameter("AfterID", AfterID);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        TaskItem = New Structure;

        TaskItem.Insert("id", Selection.id);
        TaskItem.Insert("actionId", String(Selection.actionId));
        TaskItem.Insert("createdAt", FormatDateISO8601(Selection.createdAt));
        TaskItem.Insert("startedAt", FormatDateISO8601(Selection.startedAt));
        TaskItem.Insert("completedAt", FormatDateISO8601(Selection.completedAt));

        If ValueIsFilled(Selection.startedAt) And ValueIsFilled(Selection.completedAt) Then
            DurationSec = Selection.completedAt - Selection.startedAt;
            TaskItem.Insert("durationSec", DurationSec);
        Else
            TaskItem.Insert("durationSec", 0);
        EndIf;

        TaskItem.Insert("storageBinCode", Selection.storageBinCode);
        TaskItem.Insert("storagePalletCode", Selection.storagePalletCode);
        TaskItem.Insert("allocationBinCode", Selection.allocationBinCode);
        TaskItem.Insert("allocationPalletCode", Selection.allocationPalletCode);
        TaskItem.Insert("productSku", Selection.productSku);
        TaskItem.Insert("productName", Selection.productName);
        TaskItem.Insert("productCode", Selection.productCode);
        TaskItem.Insert("productWeight", Selection.productWeight);
        TaskItem.Insert("qty", Selection.qty);
        TaskItem.Insert("assigneeCode", Selection.assigneeCode);
        TaskItem.Insert("assigneeName", Selection.assigneeName);
        TaskItem.Insert("templateCode", Selection.templateCode);
        TaskItem.Insert("templateName", Selection.templateName);

        // Связь с родительским заданием (sgTask)
        TaskItem.Insert("taskBasisNumber", Selection.taskBasisNumber);
        TaskItem.Insert("planActionId", Selection.planActionId);

        If ValueIsFilled(Selection.completedAt) Then
            TaskItem.Insert("status", 3);
        ElsIf ValueIsFilled(Selection.startedAt) Then
            TaskItem.Insert("status", 2);
        Else
            TaskItem.Insert("status", 0);
        EndIf;

        ItemsArray.Add(TaskItem);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /workers
// ============================================================================

Function GetWorkers(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    If Limit > 10000 Then
        Limit = 10000;
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT TOP " + Format(Limit + 1, "NG=0") + "
    |   Workers.Code AS code,
    |   Workers.Description AS name,
    |   ISNULL(Workers.Barcode, """") AS barcode,
    |   ISNULL(Workers.Cell.Code, """") AS cellCode,
    |   ISNULL(Workers.Comment, """") AS comment
    |FROM
    |   Catalog.rtMSUsers AS Workers
    |WHERE
    |   Workers.Code > &AfterID
    |   AND NOT Workers.DeletionMark
    |ORDER BY
    |   Workers.Code";

    Query.SetParameter("AfterID", AfterID);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("code", Selection.code);
        Item.Insert("name", Selection.name);
        Item.Insert("barcode", Selection.barcode);
        Item.Insert("cellCode", Selection.cellCode);
        Item.Insert("comment", Selection.comment);

        ItemsArray.Add(Item);
        LastID = Selection.code;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /cells
// ============================================================================

Function GetCells(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 10000);
    If Limit > 50000 Then
        Limit = 50000;
    EndIf;
    ZoneCode = GetQueryParameter(Request, "zone");

    Query = New Query;
    Query.Text =
    "SELECT TOP " + Format(Limit + 1, "NG=0") + "
    |   Cells.Code AS code,
    |   ISNULL(Cells.Description, """") AS barcode,
    |   ISNULL(Cells.Zone.Code, """") AS zoneCode,
    |   ISNULL(Cells.Zone.Description, """") AS zoneName,
    |   CASE
    |       WHEN Cells.CellType = VALUE(Enum.rtCellTypes.Storage) THEN ""Storage""
    |       WHEN Cells.CellType = VALUE(Enum.rtCellTypes.Picking) THEN ""Picking""
    |       WHEN Cells.CellType = VALUE(Enum.rtCellTypes.Employee) THEN ""Employee""
    |       ELSE """"
    |   END AS cellType,
    |   ISNULL(Cells.IndexNumber, 0) AS indexNumber,
    |   ISNULL(Cells.Inactive, FALSE) AS inactive,
    |   ISNULL(Cells.Aisle, """") AS aisle,
    |   ISNULL(Cells.Rack, """") AS rack,
    |   ISNULL(Cells.Shelf, """") AS shelf,
    |   ISNULL(Cells.Position, """") AS position,
    |   ISNULL(Cells.PickingRoute, """") AS pickingRoute,
    |   ISNULL(Cells.Weight, 0) AS maxWeightKg,
    |   ISNULL(Cells.Volume, 0) AS volumeM3
    |FROM
    |   Catalog.rtCells AS Cells
    |WHERE
    |   Cells.Code > &AfterID
    |   AND NOT Cells.DeletionMark
    |   AND (&ZoneCode = """" OR Cells.Zone.Code = &ZoneCode)
    |ORDER BY
    |   Cells.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("ZoneCode", ?(ZoneCode = Undefined, "", ZoneCode));

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("code", Selection.code);
        Item.Insert("barcode", Selection.barcode);
        Item.Insert("zoneCode", Selection.zoneCode);
        Item.Insert("zoneName", Selection.zoneName);
        Item.Insert("cellType", Selection.cellType);
        Item.Insert("indexNumber", Selection.indexNumber);
        Item.Insert("inactive", Selection.inactive);
        Item.Insert("aisle", Selection.aisle);
        Item.Insert("rack", Selection.rack);
        Item.Insert("shelf", Selection.shelf);
        Item.Insert("position", Selection.position);
        Item.Insert("pickingRoute", Selection.pickingRoute);
        Item.Insert("maxWeightKg", Selection.maxWeightKg);
        Item.Insert("volumeM3", Selection.volumeM3);

        ItemsArray.Add(Item);
        LastID = Selection.code;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /zones
// ============================================================================

Function GetZones(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT
    |   Zones.Code AS code,
    |   Zones.Description AS name,
    |   ISNULL(Zones.Warehouse.Code, """") AS warehouseCode,
    |   ISNULL(Zones.Warehouse.Description, """") AS warehouseName,
    |   CASE
    |       WHEN Zones.ZoneType = VALUE(Enum.rtZoneTypes.Resources) THEN ""Resources""
    |       WHEN Zones.ZoneType = VALUE(Enum.rtZoneTypes.Receipt) THEN ""Receipt""
    |       WHEN Zones.ZoneType = VALUE(Enum.rtZoneTypes.Storage) THEN ""Storage""
    |       WHEN Zones.ZoneType = VALUE(Enum.rtZoneTypes.Picking) THEN ""Picking""
    |       WHEN Zones.ZoneType = VALUE(Enum.rtZoneTypes.Packing) THEN ""Packing""
    |       WHEN Zones.ZoneType = VALUE(Enum.rtZoneTypes.Shipping) THEN ""Shipping""
    |       ELSE """"
    |   END AS zoneType,
    |   ISNULL(Zones.CellByDefault.Code, """") AS defaultCellCode,
    |   ISNULL(Zones.CellCodeTemplate, """") AS cellCodeTemplate,
    |   ISNULL(Zones.CellBarcodeTemplate, """") AS cellBarcodeTemplate,
    |   ISNULL(Zones.PickingRoute, """") AS pickingRoute,
    |   ISNULL(Zones.ExtCode, """") AS extCode,
    |   ISNULL(Zones.IndexNumber, 0) AS indexNumber
    |FROM
    |   Catalog.rtZones AS Zones
    |WHERE
    |   NOT Zones.DeletionMark
    |ORDER BY
    |   Zones.Code";

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    While Selection.Next() Do
        Item = New Structure;
        Item.Insert("code", Selection.code);
        Item.Insert("name", Selection.name);
        Item.Insert("warehouseCode", Selection.warehouseCode);
        Item.Insert("warehouseName", Selection.warehouseName);
        Item.Insert("zoneType", Selection.zoneType);
        Item.Insert("defaultCellCode", Selection.defaultCellCode);
        Item.Insert("cellCodeTemplate", Selection.cellCodeTemplate);
        Item.Insert("cellBarcodeTemplate", Selection.cellBarcodeTemplate);
        Item.Insert("pickingRoute", Selection.pickingRoute);
        Item.Insert("extCode", Selection.extCode);
        Item.Insert("indexNumber", Selection.indexNumber);

        ItemsArray.Add(Item);
    EndDo;

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /buffer
// ============================================================================

Function GetBuffer(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    BufferZoneCode = "D";

    CellQuery = New Query;
    CellQuery.Text =
    "SELECT
    |   COUNT(*) AS totalCells
    |FROM
    |   Catalog.rtCells AS Cells
    |WHERE
    |   Cells.Zone.Code = &ZoneCode
    |   AND NOT Cells.DeletionMark
    |   AND NOT ISNULL(Cells.Inactive, FALSE)";

    CellQuery.SetParameter("ZoneCode", BufferZoneCode);
    CellResult = CellQuery.Execute().Select();

    Capacity = 50;
    If CellResult.Next() And CellResult.totalCells > 0 Then
        Capacity = CellResult.totalCells;
    EndIf;

    TaskQuery = New Query;
    TaskQuery.Text =
    "SELECT
    |   COUNT(*) AS currentCount
    |FROM
    |   Document.sgTaskAction AS Tasks
    |WHERE
    |   Tasks.AllocationBin.Zone.Code = &ZoneCode
    |   AND Tasks.CompletedAt >= &FromTime
    |   AND NOT Tasks.DeletionMark";

    TaskQuery.SetParameter("ZoneCode", BufferZoneCode);
    TaskQuery.SetParameter("FromTime", CurrentDate() - 3600);

    CurrentCount = 0;
    TaskResult = TaskQuery.Execute().Select();
    If TaskResult.Next() Then
        CurrentCount = TaskResult.currentCount;
    EndIf;

    Result = New Structure;
    Result.Insert("timestamp", FormatDateISO8601(CurrentUniversalDate()));
    Result.Insert("zoneCode", BufferZoneCode);
    Result.Insert("capacity", Capacity);
    Result.Insert("currentCount", CurrentCount);
    Result.Insert("fillLevel", ?(Capacity > 0, CurrentCount / Capacity, 0));

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /statistics
// ============================================================================

Function GetStatistics(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT
    |   ISNULL(SUM(CASE WHEN Tasks.CompletedAt IS NOT NULL THEN 1 ELSE 0 END), 0) AS completed,
    |   ISNULL(SUM(CASE WHEN Tasks.StartedAt IS NOT NULL AND Tasks.CompletedAt IS NULL THEN 1 ELSE 0 END), 0) AS inProgress,
    |   ISNULL(SUM(CASE WHEN Tasks.StartedAt IS NULL THEN 1 ELSE 0 END), 0) AS pending,
    |   COUNT(*) AS total
    |FROM
    |   Document.sgTaskAction AS Tasks
    |WHERE
    |   Tasks.Date >= &TodayStart
    |   AND NOT Tasks.DeletionMark";

    Query.SetParameter("TodayStart", BegOfDay(CurrentDate()));

    Selection = Query.Execute().Select();

    StatusCounts = New Structure;
    TotalCount = 0;

    If Selection.Next() Then
        StatusCounts.Insert("completed", ?(Selection.completed = Null, 0, Selection.completed));
        StatusCounts.Insert("inProgress", ?(Selection.inProgress = Null, 0, Selection.inProgress));
        StatusCounts.Insert("pending", ?(Selection.pending = Null, 0, Selection.pending));
        TotalCount = ?(Selection.total = Null, 0, Selection.total);
    Else
        StatusCounts.Insert("completed", 0);
        StatusCounts.Insert("inProgress", 0);
        StatusCounts.Insert("pending", 0);
    EndIf;

    WorkersQuery = New Query;
    WorkersQuery.Text =
    "SELECT COUNT(*) AS count
    |FROM Catalog.rtMSUsers AS W
    |WHERE NOT W.DeletionMark";

    WorkersResult = WorkersQuery.Execute().Select();
    WorkersCount = 0;
    If WorkersResult.Next() Then
        WorkersCount = WorkersResult.count;
    EndIf;

    Result = New Structure;
    Result.Insert("date", FormatDateISO8601(CurrentDate()));
    Result.Insert("total", TotalCount);
    Result.Insert("byStatus", StatusCounts);
    Result.Insert("workersCount", WorkersCount);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /forklifts
// Возвращает список карщиков (работники rtMSUsers)
// ============================================================================

Function GetForklifts(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT TOP 50
    |   Workers.Code AS id,
    |   Workers.Code AS operatorId,
    |   Workers.Description AS operatorName
    |FROM
    |   Catalog.rtMSUsers AS Workers
    |WHERE
    |   NOT Workers.DeletionMark
    |ORDER BY
    |   Workers.Code";

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    While Selection.Next() Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("operatorId", Selection.operatorId);
        Item.Insert("operatorName", Selection.operatorName);
        Item.Insert("status", 0);  // 0 = Idle
        Item.Insert("currentTaskId", "");
        Item.Insert("positionX", 0);
        Item.Insert("positionY", 0);
        Item.Insert("lastUpdateAt", FormatDateISO8601(CurrentUniversalDate()));

        ItemsArray.Add(Item);
    EndDo;

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", "");
    Result.Insert("hasMore", False);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /pickers
// Возвращает список сборщиков (работники rtMSUsers)
// ============================================================================

Function GetPickers(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    If Limit > 10000 Then
        Limit = 10000;
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT TOP " + Format(Limit + 1, "NG=0") + "
    |   Workers.Code AS id,
    |   Workers.Description AS name
    |FROM
    |   Catalog.rtMSUsers AS Workers
    |WHERE
    |   Workers.Code > &AfterID
    |   AND NOT Workers.DeletionMark
    |ORDER BY
    |   Workers.Code";

    Query.SetParameter("AfterID", AfterID);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("name", Selection.name);
        Item.Insert("status", 1);  // 1 = Active
        Item.Insert("zone", "");
        Item.Insert("palletsToday", 0);
        Item.Insert("itemsToday", 0);

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /picker-tasks
// Получение заданий сборщиков (sgTask) с табличной частью PlanActions
// ============================================================================

Function GetPickerTasks(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 100);
    If Limit > 1000 Then
        Limit = 1000;
    EndIf;

    // Получаем задания сборщиков
    Query = New Query;
    Query.Text =
    "SELECT TOP " + String(Limit + 1) + "
    |   Tasks.Number AS id,
    |   Tasks.Date AS createdAt,
    |   ISNULL(Tasks.Assignee.Code, """") AS assigneeCode,
    |   ISNULL(Tasks.Assignee.Description, """") AS assigneeName,
    |   ISNULL(Tasks.Template.Code, """") AS templateCode,
    |   ISNULL(Tasks.Template.Description, """") AS templateName,
    |   Tasks.ExecutionStatus AS executionStatus,
    |   Tasks.ExecutionDate AS executionDate,
    |   Tasks.Ref AS taskRef
    |FROM
    |   Document.sgTask AS Tasks
    |WHERE
    |   Tasks.Number > &AfterID
    |   AND NOT Tasks.DeletionMark
    |ORDER BY
    |   Tasks.Number";

    Query.SetParameter("AfterID", AfterID);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        TaskItem = New Structure;

        TaskItem.Insert("id", Selection.id);
        TaskItem.Insert("createdAt", FormatDateISO8601(Selection.createdAt));
        TaskItem.Insert("assigneeCode", Selection.assigneeCode);
        TaskItem.Insert("assigneeName", Selection.assigneeName);
        TaskItem.Insert("templateCode", Selection.templateCode);
        TaskItem.Insert("templateName", Selection.templateName);
        TaskItem.Insert("executionDate", FormatDateISO8601(Selection.executionDate));

        // Статус выполнения
        StatusCode = 0;
        If TypeOf(Selection.executionStatus) = Type("EnumRef.rtOperationExecutionStatuses") Then
            StatusStr = String(Selection.executionStatus);
            If StatusStr = "Завершено" Or StatusStr = "Completed" Then
                StatusCode = 3;
            ElsIf StatusStr = "В работе" Or StatusStr = "InProgress" Then
                StatusCode = 2;
            ElsIf StatusStr = "Назначено" Or StatusStr = "Assigned" Then
                StatusCode = 1;
            EndIf;
        EndIf;
        TaskItem.Insert("status", StatusCode);

        // Получаем табличную часть PlanActions
        PlanActionsArray = New Array;
        PlanQuery = New Query;
        PlanQuery.Text =
        "SELECT
        |   PA.LineNumber AS lineNumber,
        |   ISNULL(PA.ActionId, """") AS actionId,
        |   ISNULL(PA.ActionTemplate.Code, """") AS templateCode,
        |   ISNULL(PA.StorageProductClassifier.Code, """") AS productCode,
        |   ISNULL(PA.StorageProductClassifier.Description, """") AS productName,
        |   ISNULL(PA.StorageProductClassifier.SKU, """") AS productSku,
        |   ISNULL(PA.StorageBin.Code, """") AS storageBinCode,
        |   ISNULL(PA.AllocationBin.Code, """") AS allocationBinCode,
        |   PA.Qty AS qty
        |FROM
        |   Document.sgTask.PlanActions AS PA
        |WHERE
        |   PA.Ref = &TaskRef
        |ORDER BY
        |   PA.LineNumber";

        PlanQuery.SetParameter("TaskRef", Selection.taskRef);
        PlanSelection = PlanQuery.Execute().Select();

        While PlanSelection.Next() Do
            PlanLine = New Structure;
            PlanLine.Insert("lineNumber", PlanSelection.lineNumber);
            PlanLine.Insert("actionId", PlanSelection.actionId);
            PlanLine.Insert("templateCode", PlanSelection.templateCode);
            PlanLine.Insert("productCode", PlanSelection.productCode);
            PlanLine.Insert("productName", PlanSelection.productName);
            PlanLine.Insert("productSku", PlanSelection.productSku);
            PlanLine.Insert("storageBinCode", PlanSelection.storageBinCode);
            PlanLine.Insert("allocationBinCode", PlanSelection.allocationBinCode);
            PlanLine.Insert("qty", PlanSelection.qty);
            PlanActionsArray.Add(PlanLine);
        EndDo;

        TaskItem.Insert("planActions", PlanActionsArray);
        TaskItem.Insert("totalLines", PlanActionsArray.Count());

        ItemsArray.Add(TaskItem);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);
    Result.Insert("count", ItemsArray.Count());

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /wave-tasks?wave=<WaveNumber>
// Возвращает плановые и выполненные задания волны дистрибьюции
// ============================================================================

Function GetWaveTasks(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    WaveNumber = GetQueryParameter(Request, "wave");
    If WaveNumber = Undefined Or WaveNumber = "" Then
        Return CreateErrorResponse(400, "BAD_REQUEST", "Parameter 'wave' is required");
    EndIf;

    // Find wave document
    WaveQuery = New Query;
    WaveQuery.Text =
    "SELECT
    |   Wave.Ref AS Ref,
    |   Wave.Date AS Date,
    |   Wave.Number AS Number,
    |   Wave.ExecutionStatus AS ExecutionStatus
    |FROM
    |   Document.sgDistributionWave AS Wave
    |WHERE
    |   Wave.Number = &WaveNumber";
    WaveQuery.SetParameter("WaveNumber", WaveNumber);

    WaveResult = WaveQuery.Execute();
    If WaveResult.IsEmpty() Then
        Return CreateErrorResponse(404, "NOT_FOUND", "Wave not found: " + WaveNumber);
    EndIf;

    WaveSelection = WaveResult.Select();
    WaveSelection.Next();

    WaveRef = WaveSelection.Ref;
    WaveDate = WaveSelection.Date;
    WaveStatus = String(WaveSelection.ExecutionStatus);

    // Get tasks via sgExtendedDocumentsStatuses → rtWMSProductSelection
    TasksQuery = New Query;
    TasksQuery.Text =
    "SELECT
    |   Statuses.Document AS Wave,
    |   Statuses.Operation AS Operation,
    |   Task.Ref AS Task,
    |   Task.Number AS TaskNumber,
    |   Task.ExecutionStatus AS ExecutionStatus,
    |   Task.ExecutionDate AS ExecutionDate,
    |   Task.Assignee.Code AS AssigneeCode,
    |   Task.Assignee.Description AS AssigneeName,
    |   Task.ActionTemplate.Code AS TemplateCode,
    |   CASE
    |       WHEN Task.PrevTask = VALUE(Document.rtWMSProductSelection.EmptyRef)
    |           THEN ""Replenishment""
    |       ELSE ""Distribution""
    |   END AS TaskType
    |FROM
    |   InformationRegister.sgExtendedDocumentsStatuses AS Statuses
    |       INNER JOIN Document.rtWMSProductSelection AS Task
    |       ON Statuses.Operation = Task.Operation
    |WHERE
    |   Statuses.Document = &WaveRef
    |   AND Task.Posted
    |ORDER BY
    |   Task.Date";
    TasksQuery.SetParameter("WaveRef", WaveRef);

    TasksResult = TasksQuery.Execute();
    TasksSelection = TasksResult.Select();

    ReplenishmentArray = New Array;
    DistributionArray = New Array;

    While TasksSelection.Next() Do

        TaskRef = TasksSelection.Task;

        // Get plan actions and fact (sgTaskAction)
        ActionsQuery = New Query;
        ActionsQuery.Text =
        "SELECT
        |   PlanAction.StorageBin.Code AS StorageBin,
        |   PlanAction.AllocationBin.Code AS AllocationBin,
        |   PlanAction.StorageProduct.Code AS ProductCode,
        |   PlanAction.StorageProduct.Description AS ProductName,
        |   PlanAction.StorageProduct.Weight AS ProductWeight,
        |   PlanAction.Qty AS QtyPlan,
        |   PlanAction.SortOrder AS SortOrder,
        |   ISNULL(Fact.Qty, 0) AS QtyFact,
        |   Fact.CompletedAt AS FactCompletedAt,
        |   Fact.StartedAt AS FactStartedAt
        |FROM
        |   Document.rtWMSProductSelection.PlanActions AS PlanAction
        |       LEFT JOIN Document.sgTaskAction AS Fact
        |       ON Fact.TaskBasis = &TaskRef
        |           AND Fact.AllocationBin = PlanAction.AllocationBin
        |           AND Fact.StorageProduct = PlanAction.StorageProduct
        |           AND Fact.Posted
        |WHERE
        |   PlanAction.Ref = &TaskRef
        |ORDER BY
        |   PlanAction.SortOrder";
        ActionsQuery.SetParameter("TaskRef", TaskRef);

        ActionsResult = ActionsQuery.Execute();
        ActionsSelection = ActionsResult.Select();

        ActionsArray = New Array;

        While ActionsSelection.Next() Do
            Action = New Structure;
            Action.Insert("storageBin",   String(ActionsSelection.StorageBin));
            Action.Insert("allocationBin", String(ActionsSelection.AllocationBin));
            Action.Insert("productCode",   String(ActionsSelection.ProductCode));
            Action.Insert("productName",   String(ActionsSelection.ProductName));
            Action.Insert("weightKg",      ActionsSelection.ProductWeight);
            Action.Insert("qtyPlan",       ActionsSelection.QtyPlan);
            Action.Insert("qtyFact",       ActionsSelection.QtyFact);
            Action.Insert("sortOrder",     ActionsSelection.SortOrder);
            Action.Insert("completedAt",   FormatDateISO8601(ActionsSelection.FactCompletedAt));
            Action.Insert("startedAt",     FormatDateISO8601(ActionsSelection.FactStartedAt));

            If ValueIsFilled(ActionsSelection.FactCompletedAt) And ValueIsFilled(ActionsSelection.FactStartedAt) Then
                DurationSec = ActionsSelection.FactCompletedAt - ActionsSelection.FactStartedAt;
                Action.Insert("durationSec", DurationSec);
            Else
                Action.Insert("durationSec", 0);
            EndIf;

            ActionsArray.Add(Action);
        EndDo;

        // Build task group
        TaskGroup = New Structure;
        TaskGroup.Insert("taskRef",         String(TaskRef.UUID()));
        TaskGroup.Insert("taskNumber",       TrimAll(TasksSelection.TaskNumber));
        TaskGroup.Insert("assigneeCode",     String(TasksSelection.AssigneeCode));
        TaskGroup.Insert("assigneeName",     String(TasksSelection.AssigneeName));
        TaskGroup.Insert("templateCode",     String(TasksSelection.TemplateCode));
        TaskGroup.Insert("executionStatus",  StrReplace(String(TasksSelection.ExecutionStatus), " ", ""));
        TaskGroup.Insert("executionDate",    FormatDateISO8601(TasksSelection.ExecutionDate));
        TaskGroup.Insert("actions",          ActionsArray);

        // Distribute by type
        If TasksSelection.TaskType = "Replenishment" Then
            ReplenishmentArray.Add(TaskGroup);
        Else
            DistributionArray.Add(TaskGroup);
        EndIf;

    EndDo;

    // Build response
    Result = New Structure;
    Result.Insert("waveNumber",         TrimAll(WaveNumber));
    Result.Insert("waveDate",           FormatDateISO8601(WaveDate));
    Result.Insert("status",             WaveStatus);
    Result.Insert("replenishmentTasks", ReplenishmentArray);
    Result.Insert("distributionTasks",  DistributionArray);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// HTTP SERVICE ROUTER
// ============================================================================

Function ProcessRequest(Request)

    Method = Request.HTTPMethod;
    RelativeURL = Request.RelativeURL;

    QueryPos = StrFind(RelativeURL, "?");
    If QueryPos > 0 Then
        URLTemplate = Left(RelativeURL, QueryPos - 1);
    Else
        URLTemplate = RelativeURL;
    EndIf;

    If Left(URLTemplate, 1) <> "/" Then
        URLTemplate = "/" + URLTemplate;
    EndIf;

    If URLTemplate = "/health" And Method = "GET" Then
        Return GetHealth(Request);

    ElsIf URLTemplate = "/products" And Method = "GET" Then
        Return GetProducts(Request);

    ElsIf URLTemplate = "/tasks" And Method = "GET" Then
        Return GetTasks(Request);

    ElsIf URLTemplate = "/workers" And Method = "GET" Then
        Return GetWorkers(Request);

    ElsIf URLTemplate = "/cells" And Method = "GET" Then
        Return GetCells(Request);

    ElsIf URLTemplate = "/zones" And Method = "GET" Then
        Return GetZones(Request);

    ElsIf URLTemplate = "/buffer" And Method = "GET" Then
        Return GetBuffer(Request);

    ElsIf URLTemplate = "/statistics" And Method = "GET" Then
        Return GetStatistics(Request);

    ElsIf URLTemplate = "/forklifts" And Method = "GET" Then
        Return GetForklifts(Request);

    ElsIf URLTemplate = "/pickers" And Method = "GET" Then
        Return GetPickers(Request);

    ElsIf URLTemplate = "/picker-tasks" And Method = "GET" Then
        Return GetPickerTasks(Request);

    ElsIf URLTemplate = "/wave-tasks" And Method = "GET" Then
        Return GetWaveTasks(Request);

    Else
        Return CreateErrorResponse(404, "NOT_FOUND", "Endpoint not found: " + Method + " " + URLTemplate);

    EndIf;

EndFunction
