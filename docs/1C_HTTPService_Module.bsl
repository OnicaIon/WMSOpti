// ============================================================================
// HTTP Service Module: BufferAPI
// 1C:Enterprise 8.3 (English dialect)
//
// Root URL: /wms/hs/buffer-api/v1
// ============================================================================

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

Function FormatDateISO8601(Date)
    If Not ValueIsFilled(Date) Then
        Return Null;
    EndIf;
    Return Format(Date, "DF=yyyy-MM-ddTHH:mm:ssZ");
EndFunction

Function ParseDateISO8601(DateString)
    If Not ValueIsFilled(DateString) Then
        Return Undefined;
    EndIf;
    Try
        Return XMLValue(Type("Date"), DateString);
    Except
        Return Undefined;
    EndTry;
EndFunction

Function StructureToJSON(Structure)
    JSONWriter = New JSONWriter;
    JSONWriter.SetString();
    WriteJSON(JSONWriter, Structure);
    Return JSONWriter.Close();
EndFunction

Function JSONToStructure(JSONString)
    JSONReader = New JSONReader;
    JSONReader.SetString(JSONString);
    Return ReadJSON(JSONReader);
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

Function CreateErrorResponse(StatusCode, ErrorCode, Message, Details = Undefined)
    ErrorStructure = New Structure;
    ErrorStructure.Insert("code", ErrorCode);
    ErrorStructure.Insert("message", Message);
    If Details <> Undefined Then
        ErrorStructure.Insert("details", Details);
    EndIf;

    Result = New Structure;
    Result.Insert("error", ErrorStructure);

    Return CreateResponse(StatusCode, Result);
EndFunction

Function CheckAuthentication(Request)
    // Check X-API-Key header
    APIKey = Request.Headers.Get("X-API-Key");
    If ValueIsFilled(APIKey) Then
        // Validate API key against settings
        ValidKey = Constants.BufferAPIKey.Get();
        If APIKey = ValidKey Then
            Return True;
        EndIf;
    EndIf;

    // Check Basic Auth
    AuthHeader = Request.Headers.Get("Authorization");
    If ValueIsFilled(AuthHeader) And StrStartsWith(AuthHeader, "Basic ") Then
        Credentials = Base64Value(Mid(AuthHeader, 7));
        // Validate credentials
        // ... implementation depends on your auth system
        Return True;
    EndIf;

    Return True; // For development, allow all requests
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

Function GetTaskStatusCode(Status)
    If Status = Enums.TaskStatuses.New Then
        Return 0;
    ElsIf Status = Enums.TaskStatuses.Assigned Then
        Return 1;
    ElsIf Status = Enums.TaskStatuses.InProgress Then
        Return 2;
    ElsIf Status = Enums.TaskStatuses.Completed Then
        Return 3;
    ElsIf Status = Enums.TaskStatuses.Error Then
        Return 4;
    ElsIf Status = Enums.TaskStatuses.Cancelled Then
        Return 5;
    Else
        Return 0;
    EndIf;
EndFunction

Function GetPickerStatusCode(Status)
    If Status = Enums.PickerStatuses.Idle Then
        Return 0;
    ElsIf Status = Enums.PickerStatuses.Working Then
        Return 1;
    ElsIf Status = Enums.PickerStatuses.Break Then
        Return 2;
    ElsIf Status = Enums.PickerStatuses.Offline Then
        Return 3;
    Else
        Return 0;
    EndIf;
EndFunction

Function GetForkliftStatusCode(Status)
    If Status = Enums.ForkliftStatuses.Idle Then
        Return 0;
    ElsIf Status = Enums.ForkliftStatuses.EnRoute Then
        Return 1;
    ElsIf Status = Enums.ForkliftStatuses.Loading Then
        Return 2;
    ElsIf Status = Enums.ForkliftStatuses.Unloading Then
        Return 3;
    ElsIf Status = Enums.ForkliftStatuses.Maintenance Then
        Return 4;
    Else
        Return 0;
    EndIf;
EndFunction

Function GetPalletStatusCode(Status)
    If Status = Enums.PalletStatuses.InStorage Then
        Return 0;
    ElsIf Status = Enums.PalletStatuses.Reserved Then
        Return 1;
    ElsIf Status = Enums.PalletStatuses.InTransit Then
        Return 2;
    ElsIf Status = Enums.PalletStatuses.InBuffer Then
        Return 3;
    ElsIf Status = Enums.PalletStatuses.Consumed Then
        Return 4;
    Else
        Return 0;
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
// ENDPOINT: GET /tasks
// Returns executed task actions from document "Task action"
// Based on actual 1C WMS structure
// ============================================================================

Function GetTasks(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    // Parameters
    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    If Limit > 10000 Then
        Limit = 10000;
    EndIf;
    ModifiedAfter = ParseDateISO8601(GetQueryParameter(Request, "modifiedAfter"));

    // Query - based on actual WMS "Task action" document structure
    // Fields from screenshot: Number, created on, StartedAt, CompletedAt, ActionId,
    // Task basis, Template, PlanActionId, Action type, Storage bin, Storage pallet,
    // Storage product, Qty, Allocation bin, Allocation pallet, Assignee
    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Tasks.Number AS id,
    |   Tasks.Date AS createdAt,
    |   Tasks.StartedAt AS startedAt,
    |   Tasks.CompletedAt AS completedAt,
    |   Tasks.ActionId AS actionId,
    |   Tasks.TaskBasis AS taskBasis,
    |   Tasks.TaskBasis.Number AS taskBasisNumber,
    |   Tasks.Template AS template,
    |   Tasks.Template.Description AS templateName,
    |   Tasks.PlanActionId AS planActionId,
    |   Tasks.ActionType AS actionType,
    |   Tasks.StorageBin AS storageBin,
    |   Tasks.StorageBin.Code AS storageBinCode,
    |   Tasks.StoragePallet AS storagePallet,
    |   Tasks.StoragePallet.Code AS storagePalletCode,
    |   Tasks.StorageProductClassifier AS storageProductClassifier,
    |   Tasks.StorageProduct AS storageProduct,
    |   Tasks.StorageProduct.Description AS storageProductName,
    |   Tasks.StorageProduct.SKU AS storageProductSku,
    |   Tasks.Qty AS qty,
    |   Tasks.AllocationBin AS allocationBin,
    |   Tasks.AllocationBin.Code AS allocationBinCode,
    |   Tasks.AllocationPallet AS allocationPallet,
    |   Tasks.AllocationPallet.Code AS allocationPalletCode,
    |   Tasks.Assignee AS assignee,
    |   Tasks.Assignee.Description AS assigneeName,
    |   Tasks.Assignee.Code AS assigneeCode
    |FROM
    |   Document.TaskAction AS Tasks
    |WHERE
    |   Tasks.Number > &AfterID
    |   AND (&ModifiedAfter = DATETIME(1, 1, 1) OR Tasks.Date >= &ModifiedAfter)
    |ORDER BY
    |   Tasks.Number";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1); // +1 to check hasMore
    Query.SetParameter("ModifiedAfter", ?(ModifiedAfter = Undefined, Date(1, 1, 1), ModifiedAfter));

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        TaskItem = New Structure;

        // Basic identifiers
        TaskItem.Insert("id", Selection.id);
        TaskItem.Insert("actionId", Selection.actionId);
        TaskItem.Insert("planActionId", Selection.planActionId);

        // Timestamps
        TaskItem.Insert("createdAt", FormatDateISO8601(Selection.createdAt));
        TaskItem.Insert("startedAt", FormatDateISO8601(Selection.startedAt));
        TaskItem.Insert("completedAt", FormatDateISO8601(Selection.completedAt));

        // Calculate duration in seconds
        If ValueIsFilled(Selection.startedAt) And ValueIsFilled(Selection.completedAt) Then
            DurationSec = Selection.completedAt - Selection.startedAt;
            TaskItem.Insert("durationSec", DurationSec);
        Else
            TaskItem.Insert("durationSec", 0);
        EndIf;

        // Task basis (parent document)
        TaskItem.Insert("taskBasisNumber", Selection.taskBasisNumber);

        // Template and action type
        TaskItem.Insert("templateName", Selection.templateName);
        TaskItem.Insert("actionType", String(Selection.actionType));

        // Storage (source) location
        TaskItem.Insert("storageBinCode", Selection.storageBinCode);
        TaskItem.Insert("storagePalletCode", Selection.storagePalletCode);

        // Product info
        TaskItem.Insert("productSku", Selection.storageProductSku);
        TaskItem.Insert("productName", Selection.storageProductName);
        TaskItem.Insert("qty", Selection.qty);

        // Allocation (destination) location
        TaskItem.Insert("allocationBinCode", Selection.allocationBinCode);
        TaskItem.Insert("allocationPalletCode", Selection.allocationPalletCode);

        // Assignee (worker who executed the task)
        TaskItem.Insert("assigneeCode", Selection.assigneeCode);
        TaskItem.Insert("assigneeName", Selection.assigneeName);

        // Determine status based on completion
        If ValueIsFilled(Selection.completedAt) Then
            TaskItem.Insert("status", 3); // Completed
        ElsIf ValueIsFilled(Selection.startedAt) Then
            TaskItem.Insert("status", 2); // InProgress
        Else
            TaskItem.Insert("status", 0); // New
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

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: POST /tasks
// ============================================================================

Function CreateTask(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    Try
        RequestBody = JSONToStructure(Request.GetBodyAsString());
    Except
        Return CreateErrorResponse(400, "INVALID_JSON", "Invalid JSON in request body");
    EndTry;

    // Validate required fields
    If Not RequestBody.Property("palletId") Then
        Return CreateErrorResponse(400, "MISSING_FIELD", "palletId is required");
    EndIf;

    // Find pallet
    PalletRef = Catalogs.Pallets.FindByCode(RequestBody.palletId);
    If PalletRef.IsEmpty() Then
        Return CreateErrorResponse(404, "PALLET_NOT_FOUND", "Pallet not found: " + RequestBody.palletId);
    EndIf;

    // Check if pallet is available
    If PalletRef.Status <> Enums.PalletStatuses.InStorage Then
        Return CreateErrorResponse(409, "PALLET_NOT_AVAILABLE",
            "Pallet is not available for delivery",
            New Structure("palletId,currentStatus", RequestBody.palletId, String(PalletRef.Status)));
    EndIf;

    // Find zones and cells
    SourceZone = Catalogs.WarehouseZones.FindByCode(RequestBody.fromZone);
    SourceCell = Catalogs.WarehouseCells.FindByCode(RequestBody.fromSlot);
    TargetZone = Catalogs.WarehouseZones.FindByCode(RequestBody.toZone);
    TargetCell = Catalogs.WarehouseCells.FindByCode(RequestBody.toSlot);

    // Find forklift if specified
    ForkliftRef = Undefined;
    If RequestBody.Property("forkliftId") And ValueIsFilled(RequestBody.forkliftId) Then
        ForkliftRef = Catalogs.Forklifts.FindByCode(RequestBody.forkliftId);
    EndIf;

    // Create task document
    BeginTransaction();
    Try
        NewTask = Documents.DeliveryTask.CreateDocument();
        NewTask.Date = CurrentDate();
        NewTask.Pallet = PalletRef;
        NewTask.SourceZone = SourceZone;
        NewTask.SourceCell = SourceCell;
        NewTask.TargetZone = TargetZone;
        NewTask.TargetCell = TargetCell;
        NewTask.Status = Enums.TaskStatuses.New;
        NewTask.CreationDate = CurrentUniversalDate();

        If RequestBody.Property("priority") Then
            NewTask.Priority = RequestBody.priority;
        Else
            NewTask.Priority = 1; // Normal
        EndIf;

        If ForkliftRef <> Undefined And Not ForkliftRef.IsEmpty() Then
            NewTask.Forklift = ForkliftRef;
            NewTask.Status = Enums.TaskStatuses.Assigned;
        EndIf;

        // Calculate distance if coordinates are available
        If ValueIsFilled(SourceCell) And ValueIsFilled(TargetCell) Then
            DX = TargetCell.CoordinateX - SourceCell.CoordinateX;
            DY = TargetCell.CoordinateY - SourceCell.CoordinateY;
            NewTask.Distance = Sqrt(DX * DX + DY * DY);
        EndIf;

        NewTask.Write();

        // Update pallet status
        PalletObject = PalletRef.GetObject();
        PalletObject.Status = Enums.PalletStatuses.Reserved;
        PalletObject.Write();

        CommitTransaction();

        Result = New Structure;
        Result.Insert("taskId", NewTask.Code);
        Result.Insert("success", True);

        Return CreateResponse(201, Result);

    Except
        RollbackTransaction();
        Return CreateErrorResponse(500, "CREATE_FAILED", ErrorDescription());
    EndTry;
EndFunction

// ============================================================================
// ENDPOINT: PUT /tasks/{taskId}/status
// ============================================================================

Function UpdateTaskStatus(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    // Get taskId from URL
    TaskID = Request.URLParameters.Get("taskId");
    If Not ValueIsFilled(TaskID) Then
        Return CreateErrorResponse(400, "MISSING_TASK_ID", "Task ID is required");
    EndIf;

    Try
        RequestBody = JSONToStructure(Request.GetBodyAsString());
    Except
        Return CreateErrorResponse(400, "INVALID_JSON", "Invalid JSON in request body");
    EndTry;

    If Not RequestBody.Property("status") Then
        Return CreateErrorResponse(400, "MISSING_FIELD", "status is required");
    EndIf;

    // Find task
    Query = New Query;
    Query.Text = "SELECT Task.Ref FROM Document.DeliveryTask AS Task WHERE Task.Code = &Code";
    Query.SetParameter("Code", TaskID);
    Selection = Query.Execute().Select();

    If Not Selection.Next() Then
        Return CreateErrorResponse(404, "TASK_NOT_FOUND", "Task not found: " + TaskID);
    EndIf;

    TaskRef = Selection.Ref;

    BeginTransaction();
    Try
        TaskObject = TaskRef.GetObject();

        // Map status code to enum
        StatusCode = RequestBody.status;
        If StatusCode = 0 Then
            TaskObject.Status = Enums.TaskStatuses.New;
        ElsIf StatusCode = 1 Then
            TaskObject.Status = Enums.TaskStatuses.Assigned;
        ElsIf StatusCode = 2 Then
            TaskObject.Status = Enums.TaskStatuses.InProgress;
            If Not ValueIsFilled(TaskObject.StartDate) Then
                TaskObject.StartDate = CurrentUniversalDate();
            EndIf;
        ElsIf StatusCode = 3 Then
            TaskObject.Status = Enums.TaskStatuses.Completed;
            TaskObject.CompletionDate = CurrentUniversalDate();
            // Update pallet status
            If ValueIsFilled(TaskObject.Pallet) Then
                PalletObject = TaskObject.Pallet.GetObject();
                PalletObject.Status = Enums.PalletStatuses.InBuffer;
                PalletObject.CurrentZone = TaskObject.TargetZone;
                PalletObject.CurrentCell = TaskObject.TargetCell;
                PalletObject.Write();
            EndIf;
        ElsIf StatusCode = 4 Then
            TaskObject.Status = Enums.TaskStatuses.Error;
            TaskObject.CompletionDate = CurrentUniversalDate();
            If RequestBody.Property("failureReason") Then
                TaskObject.FailureReason = RequestBody.failureReason;
            EndIf;
            // Release pallet reservation
            If ValueIsFilled(TaskObject.Pallet) Then
                PalletObject = TaskObject.Pallet.GetObject();
                PalletObject.Status = Enums.PalletStatuses.InStorage;
                PalletObject.Write();
            EndIf;
        ElsIf StatusCode = 5 Then
            TaskObject.Status = Enums.TaskStatuses.Cancelled;
            // Release pallet reservation
            If ValueIsFilled(TaskObject.Pallet) Then
                PalletObject = TaskObject.Pallet.GetObject();
                PalletObject.Status = Enums.PalletStatuses.InStorage;
                PalletObject.Write();
            EndIf;
        EndIf;

        TaskObject.ModificationDate = CurrentUniversalDate();
        TaskObject.Write();

        CommitTransaction();

        Result = New Structure;
        Result.Insert("success", True);

        Return CreateResponse(200, Result);

    Except
        RollbackTransaction();
        Return CreateErrorResponse(500, "UPDATE_FAILED", ErrorDescription());
    EndTry;
EndFunction

// ============================================================================
// ENDPOINT: GET /pickers
// ============================================================================

Function GetPickers(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Pickers.Code AS id,
    |   Pickers.Description AS name,
    |   Pickers.Status AS status,
    |   Pickers.WorkZone.Code AS zone,
    |   Pickers.CurrentOrder.Number AS currentOrderId,
    |   Pickers.ShiftStart AS shiftStart,
    |   Pickers.ShiftEnd AS shiftEnd,
    |   Pickers.PalletsToday AS palletsToday,
    |   Pickers.ItemsToday AS itemsToday
    |FROM
    |   Catalog.Pickers AS Pickers
    |WHERE
    |   Pickers.Code > &AfterID
    |   AND NOT Pickers.DeletionMark
    |ORDER BY
    |   Pickers.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("name", Selection.name);
        Item.Insert("status", GetPickerStatusCode(Selection.status));
        Item.Insert("zone", Selection.zone);
        Item.Insert("currentOrderId", Selection.currentOrderId);
        Item.Insert("shiftStart", FormatDateISO8601(Selection.shiftStart));
        Item.Insert("shiftEnd", FormatDateISO8601(Selection.shiftEnd));
        Item.Insert("palletsToday", Selection.palletsToday);
        Item.Insert("itemsToday", Selection.itemsToday);

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /picker-activity
// ============================================================================

Function GetPickerActivity(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    FromTime = ParseDateISO8601(GetQueryParameter(Request, "fromTime"));

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Activity.Code AS id,
    |   Activity.EventTime AS timestamp,
    |   Activity.Picker.Code AS pickerId,
    |   Activity.EventType AS eventType,
    |   Activity.Pallet.Code AS palletId,
    |   Activity.Order.Number AS orderId,
    |   Activity.ItemsPicked AS itemsPicked,
    |   Activity.DurationSeconds AS durationSec
    |FROM
    |   InformationRegister.PickerActivity AS Activity
    |WHERE
    |   Activity.Code > &AfterID
    |   AND (&FromTime = DATETIME(1, 1, 1) OR Activity.EventTime >= &FromTime)
    |ORDER BY
    |   Activity.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
    Query.SetParameter("FromTime", ?(FromTime = Undefined, Date(1, 1, 1), FromTime));

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("timestamp", FormatDateISO8601(Selection.timestamp));
        Item.Insert("pickerId", Selection.pickerId);
        Item.Insert("eventType", MapEventType(Selection.eventType));
        Item.Insert("palletId", Selection.palletId);
        Item.Insert("orderId", Selection.orderId);
        Item.Insert("itemsPicked", Selection.itemsPicked);
        Item.Insert("durationSec", Selection.durationSec);

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

Function MapEventType(EventType)
    If EventType = Enums.PickerEventTypes.ShiftStart Then
        Return "shift_start";
    ElsIf EventType = Enums.PickerEventTypes.ShiftEnd Then
        Return "shift_end";
    ElsIf EventType = Enums.PickerEventTypes.BreakStart Then
        Return "break_start";
    ElsIf EventType = Enums.PickerEventTypes.BreakEnd Then
        Return "break_end";
    ElsIf EventType = Enums.PickerEventTypes.PalletStart Then
        Return "pallet_start";
    ElsIf EventType = Enums.PickerEventTypes.PalletEnd Then
        Return "pallet_end";
    ElsIf EventType = Enums.PickerEventTypes.OrderStart Then
        Return "order_start";
    ElsIf EventType = Enums.PickerEventTypes.OrderEnd Then
        Return "order_end";
    Else
        Return "unknown";
    EndIf;
EndFunction

// ============================================================================
// ENDPOINT: GET /forklifts
// ============================================================================

Function GetForklifts(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT
    |   Forklifts.Code AS id,
    |   Forklifts.Operator.Code AS operatorId,
    |   Forklifts.Operator.Description AS operatorName,
    |   Forklifts.Status AS status,
    |   Forklifts.CurrentTask.Code AS currentTaskId,
    |   Forklifts.PositionX AS positionX,
    |   Forklifts.PositionY AS positionY,
    |   Forklifts.LastUpdateTime AS lastUpdateAt
    |FROM
    |   Catalog.Forklifts AS Forklifts
    |WHERE
    |   NOT Forklifts.DeletionMark
    |ORDER BY
    |   Forklifts.Code";

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";

    While Selection.Next() Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("operatorId", Selection.operatorId);
        Item.Insert("operatorName", Selection.operatorName);
        Item.Insert("status", GetForkliftStatusCode(Selection.status));
        Item.Insert("currentTaskId", Selection.currentTaskId);
        Item.Insert("positionX", Selection.positionX);
        Item.Insert("positionY", Selection.positionY);
        Item.Insert("lastUpdateAt", FormatDateISO8601(Selection.lastUpdateAt));

        ItemsArray.Add(Item);
        LastID = Selection.id;
    EndDo;

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", False);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /pallets
// ============================================================================

Function GetPallets(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    Zone = GetQueryParameter(Request, "zone");
    Status = GetNumericParameter(Request, "status", -1);

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Pallets.Code AS id,
    |   Pallets.Product.SKU AS productSku,
    |   Pallets.Product.Description AS productName,
    |   Pallets.Quantity AS quantity,
    |   Pallets.GrossWeight AS weightKg,
    |   Pallets.CurrentZone.Code AS zone,
    |   Pallets.CurrentCell.Code AS slot,
    |   Pallets.CurrentCell.CoordinateX AS positionX,
    |   Pallets.CurrentCell.CoordinateY AS positionY,
    |   Pallets.CurrentCell.Level AS positionZ,
    |   Pallets.Status AS status,
    |   Pallets.CreationDate AS createdAt,
    |   Pallets.LastMovedDate AS lastMovedAt
    |FROM
    |   Catalog.Pallets AS Pallets
    |WHERE
    |   Pallets.Code > &AfterID
    |   AND NOT Pallets.DeletionMark
    |   AND (&Zone = """" OR Pallets.CurrentZone.Code = &Zone)
    |   AND (&Status = -1 OR &StatusFilter)
    |ORDER BY
    |   Pallets.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
    Query.SetParameter("Zone", ?(Zone = Undefined, "", Zone));
    Query.SetParameter("Status", Status);

    // Status filter logic would need to be expanded based on your enum

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("productSku", Selection.productSku);
        Item.Insert("productName", Selection.productName);
        Item.Insert("quantity", Selection.quantity);
        Item.Insert("weightKg", Selection.weightKg);
        Item.Insert("zone", Selection.zone);
        Item.Insert("slot", Selection.slot);
        Item.Insert("positionX", Selection.positionX);
        Item.Insert("positionY", Selection.positionY);
        Item.Insert("positionZ", Selection.positionZ);
        Item.Insert("status", GetPalletStatusCode(Selection.status));
        Item.Insert("createdAt", FormatDateISO8601(Selection.createdAt));
        Item.Insert("lastMovedAt", FormatDateISO8601(Selection.lastMovedAt));

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: POST /pallets/{palletId}/reserve
// ============================================================================

Function ReservePallet(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    PalletID = Request.URLParameters.Get("palletId");
    If Not ValueIsFilled(PalletID) Then
        Return CreateErrorResponse(400, "MISSING_PALLET_ID", "Pallet ID is required");
    EndIf;

    Try
        RequestBody = JSONToStructure(Request.GetBodyAsString());
    Except
        Return CreateErrorResponse(400, "INVALID_JSON", "Invalid JSON in request body");
    EndTry;

    // Find pallet
    PalletRef = Catalogs.Pallets.FindByCode(PalletID);
    If PalletRef.IsEmpty() Then
        Return CreateErrorResponse(404, "PALLET_NOT_FOUND", "Pallet not found: " + PalletID);
    EndIf;

    // Check if already reserved
    If PalletRef.Status = Enums.PalletStatuses.Reserved Then
        Return CreateErrorResponse(409, "PALLET_RESERVED",
            "Pallet is already reserved",
            New Structure("palletId,reservedBy", PalletID, PalletRef.ReservedBy));
    EndIf;

    If PalletRef.Status <> Enums.PalletStatuses.InStorage Then
        Return CreateErrorResponse(409, "PALLET_NOT_AVAILABLE",
            "Pallet is not available for reservation",
            New Structure("palletId,currentStatus", PalletID, String(PalletRef.Status)));
    EndIf;

    TimeoutMinutes = 10;
    If RequestBody.Property("timeoutMinutes") Then
        TimeoutMinutes = RequestBody.timeoutMinutes;
    EndIf;

    BeginTransaction();
    Try
        PalletObject = PalletRef.GetObject();
        PalletObject.Status = Enums.PalletStatuses.Reserved;
        PalletObject.ReservedBy = RequestBody.forkliftId;
        PalletObject.ReservationExpires = CurrentUniversalDate() + TimeoutMinutes * 60;
        PalletObject.Write();

        CommitTransaction();

        ReservationID = "RES-" + Format(CurrentUniversalDate(), "DF=yyyyMMddHHmmss");

        Result = New Structure;
        Result.Insert("success", True);
        Result.Insert("reservationId", ReservationID);
        Result.Insert("expiresAt", FormatDateISO8601(PalletObject.ReservationExpires));

        Return CreateResponse(200, Result);

    Except
        RollbackTransaction();
        Return CreateErrorResponse(500, "RESERVE_FAILED", ErrorDescription());
    EndTry;
EndFunction

// ============================================================================
// ENDPOINT: DELETE /pallets/{palletId}/reserve
// ============================================================================

Function ReleasePalletReservation(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    PalletID = Request.URLParameters.Get("palletId");
    If Not ValueIsFilled(PalletID) Then
        Return CreateErrorResponse(400, "MISSING_PALLET_ID", "Pallet ID is required");
    EndIf;

    PalletRef = Catalogs.Pallets.FindByCode(PalletID);
    If PalletRef.IsEmpty() Then
        Return CreateErrorResponse(404, "PALLET_NOT_FOUND", "Pallet not found: " + PalletID);
    EndIf;

    BeginTransaction();
    Try
        PalletObject = PalletRef.GetObject();
        If PalletObject.Status = Enums.PalletStatuses.Reserved Then
            PalletObject.Status = Enums.PalletStatuses.InStorage;
            PalletObject.ReservedBy = "";
            PalletObject.ReservationExpires = Date(1, 1, 1);
            PalletObject.Write();
        EndIf;

        CommitTransaction();

        Result = New Structure;
        Result.Insert("success", True);

        Return CreateResponse(200, Result);

    Except
        RollbackTransaction();
        Return CreateErrorResponse(500, "RELEASE_FAILED", ErrorDescription());
    EndTry;
EndFunction

// ============================================================================
// ENDPOINT: GET /buffer
// ============================================================================

Function GetBuffer(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    // Get buffer zone configuration
    BufferZone = Catalogs.WarehouseZones.FindByCode("BUFFER");
    Capacity = 50; // Default
    If Not BufferZone.IsEmpty() Then
        Capacity = BufferZone.Capacity;
    EndIf;

    // Count pallets in buffer
    Query = New Query;
    Query.Text =
    "SELECT
    |   Pallets.Code AS id,
    |   Pallets.CurrentCell.Code AS slot,
    |   Pallets.Product.SKU AS productSku,
    |   Pallets.Product.Description AS productName,
    |   Pallets.Quantity AS quantity,
    |   Pallets.GrossWeight AS weightKg,
    |   Pallets.LastMovedDate AS arrivedAt
    |FROM
    |   Catalog.Pallets AS Pallets
    |WHERE
    |   Pallets.Status = VALUE(Enum.PalletStatuses.InBuffer)
    |   AND NOT Pallets.DeletionMark
    |ORDER BY
    |   Pallets.LastMovedDate DESC";

    Selection = Query.Execute().Select();

    PalletsArray = New Array;
    While Selection.Next() Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("slot", Selection.slot);
        Item.Insert("productSku", Selection.productSku);
        Item.Insert("productName", Selection.productName);
        Item.Insert("quantity", Selection.quantity);
        Item.Insert("weightKg", Selection.weightKg);
        Item.Insert("arrivedAt", FormatDateISO8601(Selection.arrivedAt));

        PalletsArray.Add(Item);
    EndDo;

    Result = New Structure;
    Result.Insert("timestamp", FormatDateISO8601(CurrentUniversalDate()));
    Result.Insert("capacity", Capacity);
    Result.Insert("currentCount", PalletsArray.Count());
    Result.Insert("pallets", PalletsArray);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /buffer-snapshots
// ============================================================================

Function GetBufferSnapshots(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    FromTime = ParseDateISO8601(GetQueryParameter(Request, "fromTime"));

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Snapshots.Code AS id,
    |   Snapshots.SnapshotTime AS timestamp,
    |   Snapshots.Capacity AS capacity,
    |   Snapshots.CurrentCount AS currentCount,
    |   Snapshots.ActiveForklifts AS activeForklifts,
    |   Snapshots.ActivePickers AS activePickers
    |FROM
    |   InformationRegister.BufferSnapshots AS Snapshots
    |WHERE
    |   Snapshots.Code > &AfterID
    |   AND (&FromTime = DATETIME(1, 1, 1) OR Snapshots.SnapshotTime >= &FromTime)
    |ORDER BY
    |   Snapshots.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
    Query.SetParameter("FromTime", ?(FromTime = Undefined, Date(1, 1, 1), FromTime));

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("timestamp", FormatDateISO8601(Selection.timestamp));
        Item.Insert("capacity", Selection.capacity);
        Item.Insert("currentCount", Selection.currentCount);
        Item.Insert("activeForklifts", Selection.activeForklifts);
        Item.Insert("activePickers", Selection.activePickers);

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /orders
// ============================================================================

Function GetOrders(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    StatusFilter = GetQueryParameter(Request, "status", "active");

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Orders.Number AS id,
    |   Orders.Date AS createdAt,
    |   Orders.DueDate AS dueTime,
    |   Orders.Priority AS priority,
    |   Orders.Status AS status,
    |   Orders.Customer.Code AS customerId,
    |   Orders.Customer.Description AS customerName,
    |   Orders.TotalQuantity AS totalItems,
    |   Orders.TotalWeight AS totalWeight
    |FROM
    |   Document.CustomerOrder AS Orders
    |WHERE
    |   Orders.Number > &AfterID
    |   AND NOT Orders.DeletionMark
    |   AND (&StatusFilter = ""all""
    |       OR (&StatusFilter = ""active"" AND Orders.Status IN (VALUE(Enum.OrderStatuses.New), VALUE(Enum.OrderStatuses.InProgress)))
    |       OR (&StatusFilter = ""completed"" AND Orders.Status = VALUE(Enum.OrderStatuses.Completed)))
    |ORDER BY
    |   Orders.Number";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
    Query.SetParameter("StatusFilter", StatusFilter);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("createdAt", FormatDateISO8601(Selection.createdAt));
        Item.Insert("dueTime", FormatDateISO8601(Selection.dueTime));
        Item.Insert("priority", Selection.priority);
        Item.Insert("status", String(Selection.status));
        Item.Insert("customerId", Selection.customerId);
        Item.Insert("customerName", Selection.customerName);
        Item.Insert("totalItems", Selection.totalItems);
        Item.Insert("totalWeight", Selection.totalWeight);

        // Get order lines
        LinesQuery = New Query;
        LinesQuery.Text =
        "SELECT
        |   Lines.Product.SKU AS productSku,
        |   Lines.Product.Description AS productName,
        |   Lines.Quantity AS quantity,
        |   Lines.Weight AS weightKg
        |FROM
        |   Document.CustomerOrder.Lines AS Lines
        |WHERE
        |   Lines.Ref.Number = &OrderNumber";
        LinesQuery.SetParameter("OrderNumber", Selection.id);

        LinesSelection = LinesQuery.Execute().Select();
        LinesArray = New Array;
        While LinesSelection.Next() Do
            LineItem = New Structure;
            LineItem.Insert("productSku", LinesSelection.productSku);
            LineItem.Insert("productName", LinesSelection.productName);
            LineItem.Insert("quantity", LinesSelection.quantity);
            LineItem.Insert("weightKg", LinesSelection.weightKg);
            LinesArray.Add(LineItem);
        EndDo;

        Item.Insert("lines", LinesArray);

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /cells
// Returns warehouse cells (Ячейки)
// ============================================================================

Function GetCells(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    ZoneCode = GetQueryParameter(Request, "zone");

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Cells.Code AS code,
    |   Cells.Barcode AS barcode,
    |   Cells.Zone AS zone,
    |   Cells.Zone.Code AS zoneCode,
    |   Cells.Zone.Description AS zoneName,
    |   Cells.Zone.ZoneType AS zoneType,
    |   Cells.Aisle AS aisle,
    |   Cells.Rack AS rack,
    |   Cells.Shelf AS shelf,
    |   Cells.Position AS position,
    |   Cells.Type AS cellType,
    |   Cells.IndexNumber AS indexNumber,
    |   Cells.Disabled AS disabled
    |FROM
    |   Catalog.Cells AS Cells
    |WHERE
    |   Cells.Code > &AfterID
    |   AND NOT Cells.DeletionMark
    |   AND (&ZoneCode = """" OR Cells.Zone.Code = &ZoneCode)
    |ORDER BY
    |   Cells.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
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
        Item.Insert("zoneType", String(Selection.zoneType));
        Item.Insert("aisle", Selection.aisle);
        Item.Insert("rack", Selection.rack);
        Item.Insert("shelf", Selection.shelf);
        Item.Insert("position", Selection.position);
        Item.Insert("cellType", String(Selection.cellType));
        Item.Insert("indexNumber", Selection.indexNumber);
        Item.Insert("disabled", Selection.disabled);

        ItemsArray.Add(Item);
        LastID = Selection.code;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /zones
// Returns warehouse zones (Зоны)
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
    |   Zones.Location AS location,
    |   Zones.Location.Description AS locationName,
    |   Zones.ZoneType AS zoneType,
    |   Zones.IndexNumber AS indexNumber,
    |   Zones.ExternalCode AS externalCode
    |FROM
    |   Catalog.Zones AS Zones
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
        Item.Insert("locationName", Selection.locationName);
        Item.Insert("zoneType", String(Selection.zoneType));
        Item.Insert("indexNumber", Selection.indexNumber);
        Item.Insert("externalCode", Selection.externalCode);

        ItemsArray.Add(Item);
    EndDo;

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", "");
    Result.Insert("hasMore", False);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /workers
// Returns mobile terminal users (workers/assignees)
// ============================================================================

Function GetWorkers(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    GroupFilter = GetQueryParameter(Request, "group");

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Workers.Code AS code,
    |   Workers.Description AS name,
    |   Workers.Barcode AS barcode,
    |   Workers.Cell AS cell,
    |   Workers.Cell.Code AS cellCode,
    |   Workers.Group AS workerGroup,
    |   Workers.Group.Description AS groupName
    |FROM
    |   Catalog.MobileTerminalUsers AS Workers
    |WHERE
    |   Workers.Code > &AfterID
    |   AND NOT Workers.DeletionMark
    |   AND (&GroupFilter = """" OR Workers.Group.Description = &GroupFilter)
    |ORDER BY
    |   Workers.Code";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
    Query.SetParameter("GroupFilter", ?(GroupFilter = Undefined, "", GroupFilter));

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
        Item.Insert("groupName", Selection.groupName);

        ItemsArray.Add(Item);
        LastID = Selection.code;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /operations
// Returns pending operations from Distribution operator workplace
// ============================================================================

Function GetOperations(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    AfterID = GetQueryParameter(Request, "after", "");
    Limit = GetNumericParameter(Request, "limit", 1000);
    StatusFilter = GetQueryParameter(Request, "status", "to_execute");

    Query = New Query;
    Query.Text =
    "SELECT TOP &Limit
    |   Ops.Ref AS ref,
    |   Ops.Number AS id,
    |   Ops.Date AS createdAt,
    |   Ops.Cell AS cell,
    |   Ops.Cell.Code AS cellCode,
    |   Ops.Container AS container,
    |   Ops.Container.Code AS containerCode,
    |   Ops.Operation AS operation,
    |   Ops.Product AS product,
    |   Ops.Product.Description AS productName,
    |   Ops.Product.SKU AS productSku,
    |   Ops.Quantity AS quantity,
    |   Ops.Status AS status,
    |   Ops.StatusTime AS statusTime
    |FROM
    |   Document.DistributionOperation AS Ops
    |WHERE
    |   Ops.Number > &AfterID
    |   AND NOT Ops.DeletionMark
    |   AND (&StatusFilter = ""all""
    |       OR (&StatusFilter = ""to_execute"" AND Ops.Status = VALUE(Enum.OperationStatuses.ToExecute))
    |       OR (&StatusFilter = ""executed"" AND Ops.Status = VALUE(Enum.OperationStatuses.Executed)))
    |ORDER BY
    |   Ops.Number";

    Query.SetParameter("AfterID", AfterID);
    Query.SetParameter("Limit", Limit + 1);
    Query.SetParameter("StatusFilter", StatusFilter);

    Selection = Query.Execute().Select();

    ItemsArray = New Array;
    LastID = "";
    Counter = 0;

    While Selection.Next() And Counter < Limit Do
        Item = New Structure;
        Item.Insert("id", Selection.id);
        Item.Insert("createdAt", FormatDateISO8601(Selection.createdAt));
        Item.Insert("cellCode", Selection.cellCode);
        Item.Insert("containerCode", Selection.containerCode);
        Item.Insert("operation", Selection.operation);
        Item.Insert("productSku", Selection.productSku);
        Item.Insert("productName", Selection.productName);
        Item.Insert("quantity", Selection.quantity);
        Item.Insert("status", String(Selection.status));
        Item.Insert("statusTime", FormatDateISO8601(Selection.statusTime));

        ItemsArray.Add(Item);
        LastID = Selection.id;
        Counter = Counter + 1;
    EndDo;

    HasMore = Selection.Next();

    Result = New Structure;
    Result.Insert("items", ItemsArray);
    Result.Insert("lastId", LastID);
    Result.Insert("hasMore", HasMore);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// ENDPOINT: GET /statistics
// Returns distribution statistics
// ============================================================================

Function GetStatistics(Request)
    If Not CheckAuthentication(Request) Then
        Return CreateErrorResponse(401, "UNAUTHORIZED", "Invalid API key");
    EndIf;

    Query = New Query;
    Query.Text =
    "SELECT
    |   Tasks.Status AS status,
    |   COUNT(*) AS count
    |FROM
    |   Document.TaskAction AS Tasks
    |WHERE
    |   Tasks.Date >= &TodayStart
    |   AND NOT Tasks.DeletionMark
    |GROUP BY
    |   Tasks.Status";

    Query.SetParameter("TodayStart", BegOfDay(CurrentDate()));

    Selection = Query.Execute().Select();

    StatusCounts = New Structure;
    TotalCount = 0;

    While Selection.Next() Do
        StatusName = String(Selection.status);
        StatusCounts.Insert(StatusName, Selection.count);
        TotalCount = TotalCount + Selection.count;
    EndDo;

    Result = New Structure;
    Result.Insert("date", FormatDateISO8601(CurrentDate()));
    Result.Insert("total", TotalCount);
    Result.Insert("byStatus", StatusCounts);

    Return CreateResponse(200, Result);
EndFunction

// ============================================================================
// HTTP SERVICE ROUTER
// Main entry point - routes requests to appropriate handlers
// ============================================================================

// This function should be set as the handler for all URL templates
Function ProcessRequest(Request)

    Method = Request.HTTPMethod;
    URLTemplate = Request.URLTemplateString;

    // Route to appropriate handler
    If URLTemplate = "/health" And Method = "GET" Then
        Return GetHealth(Request);

    ElsIf URLTemplate = "/tasks" And Method = "GET" Then
        Return GetTasks(Request);

    ElsIf URLTemplate = "/tasks" And Method = "POST" Then
        Return CreateTask(Request);

    ElsIf URLTemplate = "/tasks/{taskId}/status" And Method = "PUT" Then
        Return UpdateTaskStatus(Request);

    ElsIf URLTemplate = "/pickers" And Method = "GET" Then
        Return GetPickers(Request);

    ElsIf URLTemplate = "/picker-activity" And Method = "GET" Then
        Return GetPickerActivity(Request);

    ElsIf URLTemplate = "/forklifts" And Method = "GET" Then
        Return GetForklifts(Request);

    ElsIf URLTemplate = "/pallets" And Method = "GET" Then
        Return GetPallets(Request);

    ElsIf URLTemplate = "/pallets/{palletId}/reserve" And Method = "POST" Then
        Return ReservePallet(Request);

    ElsIf URLTemplate = "/pallets/{palletId}/reserve" And Method = "DELETE" Then
        Return ReleasePalletReservation(Request);

    ElsIf URLTemplate = "/buffer" And Method = "GET" Then
        Return GetBuffer(Request);

    ElsIf URLTemplate = "/buffer-snapshots" And Method = "GET" Then
        Return GetBufferSnapshots(Request);

    ElsIf URLTemplate = "/orders" And Method = "GET" Then
        Return GetOrders(Request);

    ElsIf URLTemplate = "/cells" And Method = "GET" Then
        Return GetCells(Request);

    ElsIf URLTemplate = "/zones" And Method = "GET" Then
        Return GetZones(Request);

    ElsIf URLTemplate = "/workers" And Method = "GET" Then
        Return GetWorkers(Request);

    ElsIf URLTemplate = "/operations" And Method = "GET" Then
        Return GetOperations(Request);

    ElsIf URLTemplate = "/statistics" And Method = "GET" Then
        Return GetStatistics(Request);

    Else
        Return CreateErrorResponse(404, "NOT_FOUND", "Endpoint not found: " + Method + " " + URLTemplate);

    EndIf;

EndFunction

// ============================================================================
// SCHEDULED JOB: Create Buffer Snapshots
// Run this every 30-60 seconds to record buffer state history
// ============================================================================

Procedure CreateBufferSnapshot() Export

    // Count active forklifts
    ForkliftQuery = New Query;
    ForkliftQuery.Text =
    "SELECT COUNT(*) AS Count
    |FROM Catalog.Forklifts AS F
    |WHERE F.Status IN (VALUE(Enum.ForkliftStatuses.Idle),
    |                   VALUE(Enum.ForkliftStatuses.EnRoute),
    |                   VALUE(Enum.ForkliftStatuses.Loading),
    |                   VALUE(Enum.ForkliftStatuses.Unloading))
    |   AND NOT F.DeletionMark";
    ActiveForklifts = ForkliftQuery.Execute().Unload()[0].Count;

    // Count active pickers
    PickerQuery = New Query;
    PickerQuery.Text =
    "SELECT COUNT(*) AS Count
    |FROM Catalog.Pickers AS P
    |WHERE P.Status IN (VALUE(Enum.PickerStatuses.Idle),
    |                   VALUE(Enum.PickerStatuses.Working))
    |   AND NOT P.DeletionMark";
    ActivePickers = PickerQuery.Execute().Unload()[0].Count;

    // Count pallets in buffer
    BufferQuery = New Query;
    BufferQuery.Text =
    "SELECT COUNT(*) AS Count
    |FROM Catalog.Pallets AS P
    |WHERE P.Status = VALUE(Enum.PalletStatuses.InBuffer)
    |   AND NOT P.DeletionMark";
    CurrentCount = BufferQuery.Execute().Unload()[0].Count;

    // Get capacity
    BufferZone = Catalogs.WarehouseZones.FindByCode("BUFFER");
    Capacity = 50;
    If Not BufferZone.IsEmpty() Then
        Capacity = BufferZone.Capacity;
    EndIf;

    // Create snapshot record
    RecordManager = InformationRegisters.BufferSnapshots.CreateRecordManager();
    RecordManager.Code = Format(CurrentUniversalDate(), "DF=yyyyMMddHHmmssffff");
    RecordManager.SnapshotTime = CurrentUniversalDate();
    RecordManager.Capacity = Capacity;
    RecordManager.CurrentCount = CurrentCount;
    RecordManager.ActiveForklifts = ActiveForklifts;
    RecordManager.ActivePickers = ActivePickers;
    RecordManager.Write();

EndProcedure

// ============================================================================
// SCHEDULED JOB: Record Picker Activity
// Call this from picker terminals when events occur
// ============================================================================

Procedure RecordPickerActivity(PickerCode, EventType, PalletCode = "", OrderNumber = "", ItemsPicked = 0, DurationSeconds = 0) Export

    PickerRef = Catalogs.Pickers.FindByCode(PickerCode);
    If PickerRef.IsEmpty() Then
        Return;
    EndIf;

    PalletRef = Undefined;
    If ValueIsFilled(PalletCode) Then
        PalletRef = Catalogs.Pallets.FindByCode(PalletCode);
    EndIf;

    OrderRef = Undefined;
    If ValueIsFilled(OrderNumber) Then
        OrderRef = Documents.CustomerOrder.FindByNumber(OrderNumber);
    EndIf;

    RecordManager = InformationRegisters.PickerActivity.CreateRecordManager();
    RecordManager.Code = Format(CurrentUniversalDate(), "DF=yyyyMMddHHmmssffff") + PickerCode;
    RecordManager.EventTime = CurrentUniversalDate();
    RecordManager.Picker = PickerRef;
    RecordManager.EventType = EventType;
    RecordManager.Pallet = PalletRef;
    RecordManager.Order = OrderRef;
    RecordManager.ItemsPicked = ItemsPicked;
    RecordManager.DurationSeconds = DurationSeconds;
    RecordManager.Write();

EndProcedure
