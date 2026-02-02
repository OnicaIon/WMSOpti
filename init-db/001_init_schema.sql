-- ============================================================================
-- WMS Buffer Management - Database Schema
-- TimescaleDB (PostgreSQL 16)
-- ============================================================================

-- Enable TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ============================================================================
-- TASK RECORDS (time-series)
-- ============================================================================

CREATE TABLE IF NOT EXISTS task_records (
    id UUID PRIMARY KEY,
    task_number VARCHAR(20),
    action_id VARCHAR(50),
    created_at TIMESTAMPTZ NOT NULL,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    duration_sec INTEGER,

    -- Location
    from_slot VARCHAR(30),
    to_slot VARCHAR(30),
    from_zone VARCHAR(10),
    to_zone VARCHAR(10),

    -- Product
    product_code VARCHAR(50),
    product_name VARCHAR(200),
    qty INTEGER,
    weight_kg DECIMAL(12,3),

    -- Pallet
    pallet_code VARCHAR(50),

    -- Worker
    assignee_code VARCHAR(50),
    assignee_name VARCHAR(100),

    -- Action
    action_type VARCHAR(30),
    template_name VARCHAR(100),

    -- Metadata
    synced_at TIMESTAMPTZ DEFAULT NOW()
);

-- Convert to hypertable for time-series optimization
SELECT create_hypertable('task_records', 'created_at',
    chunk_time_interval => INTERVAL '7 days',
    if_not_exists => TRUE);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_task_records_completed ON task_records(completed_at DESC);
CREATE INDEX IF NOT EXISTS idx_task_records_assignee ON task_records(assignee_code, completed_at DESC);
CREATE INDEX IF NOT EXISTS idx_task_records_product ON task_records(product_code, completed_at DESC);
CREATE INDEX IF NOT EXISTS idx_task_records_from_zone ON task_records(from_zone);
CREATE INDEX IF NOT EXISTS idx_task_records_to_zone ON task_records(to_zone);
CREATE INDEX IF NOT EXISTS idx_task_records_action_type ON task_records(action_type);

-- ============================================================================
-- PICKER-PRODUCT STATISTICS (aggregated)
-- ============================================================================

CREATE TABLE IF NOT EXISTS picker_product_stats (
    worker_code VARCHAR(50) NOT NULL,
    product_code VARCHAR(50) NOT NULL,

    -- Statistics
    task_count INTEGER DEFAULT 0,
    total_qty INTEGER DEFAULT 0,
    total_weight_kg DECIMAL(12,3) DEFAULT 0,
    total_duration_sec INTEGER DEFAULT 0,

    -- Calculated rates (IQR normalized)
    lines_per_min DECIMAL(10,4),
    qty_per_min DECIMAL(10,4),
    kg_per_min DECIMAL(10,4),

    -- Metadata
    first_task_at TIMESTAMPTZ,
    last_task_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ DEFAULT NOW(),

    PRIMARY KEY (worker_code, product_code)
);

CREATE INDEX IF NOT EXISTS idx_picker_product_stats_worker ON picker_product_stats(worker_code);
CREATE INDEX IF NOT EXISTS idx_picker_product_stats_product ON picker_product_stats(product_code);

-- ============================================================================
-- ROUTE STATISTICS (aggregated)
-- ============================================================================

CREATE TABLE IF NOT EXISTS route_stats (
    from_zone VARCHAR(10) NOT NULL,
    to_zone VARCHAR(10) NOT NULL,

    -- Statistics
    task_count INTEGER DEFAULT 0,
    avg_duration_sec DECIMAL(10,2),
    min_duration_sec INTEGER,
    max_duration_sec INTEGER,
    p50_duration_sec DECIMAL(10,2),
    p90_duration_sec DECIMAL(10,2),

    -- Metadata
    first_task_at TIMESTAMPTZ,
    last_task_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ DEFAULT NOW(),

    PRIMARY KEY (from_zone, to_zone)
);

-- ============================================================================
-- DEMAND PATTERNS (aggregated)
-- ============================================================================

CREATE TABLE IF NOT EXISTS demand_patterns (
    hour_of_day INTEGER NOT NULL,  -- 0-23
    day_of_week INTEGER NOT NULL,  -- 0-6 (Sunday=0)

    -- Statistics
    avg_tasks_per_hour DECIMAL(10,2),
    avg_pallets_per_hour DECIMAL(10,2),
    avg_active_workers DECIMAL(10,2),
    sample_count INTEGER DEFAULT 0,

    -- Metadata
    updated_at TIMESTAMPTZ DEFAULT NOW(),

    PRIMARY KEY (hour_of_day, day_of_week)
);

-- ============================================================================
-- ZONES (reference data)
-- ============================================================================

CREATE TABLE IF NOT EXISTS zones (
    code VARCHAR(10) PRIMARY KEY,
    name VARCHAR(50),
    warehouse_code VARCHAR(20),
    warehouse_name VARCHAR(100),
    zone_type VARCHAR(20),  -- Resources, Receipt, Storage, Picking, Packing, Shipping
    default_cell_code VARCHAR(30),
    cell_code_template VARCHAR(100),
    picking_route VARCHAR(20),
    ext_code VARCHAR(40),
    index_number INTEGER,
    is_buffer BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_zones_is_buffer ON zones(is_buffer) WHERE is_buffer = TRUE;
CREATE INDEX IF NOT EXISTS idx_zones_zone_type ON zones(zone_type);

-- ============================================================================
-- CELLS (reference data)
-- ============================================================================

CREATE TABLE IF NOT EXISTS cells (
    code VARCHAR(30) PRIMARY KEY,
    barcode VARCHAR(30),
    zone_code VARCHAR(10) REFERENCES zones(code),
    cell_type VARCHAR(20),  -- Storage, Picking, Employee
    index_number INTEGER,
    is_active BOOLEAN DEFAULT TRUE,
    aisle VARCHAR(5),
    rack VARCHAR(5),
    shelf VARCHAR(5),
    position VARCHAR(5),
    picking_route VARCHAR(20),
    max_weight_kg DECIMAL(10,3),
    volume_m3 DECIMAL(10,3),
    is_buffer BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_cells_zone ON cells(zone_code);
CREATE INDEX IF NOT EXISTS idx_cells_is_buffer ON cells(is_buffer) WHERE is_buffer = TRUE;
CREATE INDEX IF NOT EXISTS idx_cells_aisle_rack ON cells(aisle, rack);
CREATE INDEX IF NOT EXISTS idx_cells_picking_route ON cells(picking_route);

-- ============================================================================
-- BUFFER SNAPSHOTS (time-series)
-- ============================================================================

CREATE TABLE IF NOT EXISTS buffer_snapshots (
    time TIMESTAMPTZ NOT NULL,
    buffer_level DECIMAL(5,4),      -- 0.0000-1.0000
    buffer_state VARCHAR(20),       -- Normal, Low, Critical, Overflow
    pallets_count INTEGER,
    capacity INTEGER,
    active_forklifts INTEGER,
    active_pickers INTEGER,
    consumption_rate DECIMAL(10,2), -- pallets/hour
    delivery_rate DECIMAL(10,2),    -- pallets/hour

    PRIMARY KEY (time)
);

SELECT create_hypertable('buffer_snapshots', 'time',
    chunk_time_interval => INTERVAL '1 day',
    if_not_exists => TRUE);

-- ============================================================================
-- SYNC STATE (tracking)
-- ============================================================================

CREATE TABLE IF NOT EXISTS sync_state (
    key VARCHAR(50) PRIMARY KEY,
    value TEXT,
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Initialize sync state
INSERT INTO sync_state (key, value) VALUES
    ('last_task_id', ''),
    ('last_sync_at', '')
ON CONFLICT (key) DO NOTHING;

-- ============================================================================
-- VIEWS
-- ============================================================================

-- Buffer cells view
CREATE OR REPLACE VIEW buffer_cells AS
SELECT c.*, z.zone_type, z.name as zone_name
FROM cells c
JOIN zones z ON c.zone_code = z.code
WHERE c.is_buffer = TRUE AND c.is_active = TRUE;

-- Buffer capacity function
CREATE OR REPLACE FUNCTION get_buffer_capacity()
RETURNS INTEGER AS $$
    SELECT COALESCE(COUNT(*)::INTEGER, 0) FROM buffer_cells;
$$ LANGUAGE SQL STABLE;

-- ============================================================================
-- COMPRESSION POLICY (for old data)
-- ============================================================================

-- Enable compression on task_records after 7 days
ALTER TABLE task_records SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'assignee_code',
    timescaledb.compress_orderby = 'created_at DESC'
);

SELECT add_compression_policy('task_records', INTERVAL '7 days', if_not_exists => TRUE);

-- Enable compression on buffer_snapshots after 7 days
ALTER TABLE buffer_snapshots SET (
    timescaledb.compress,
    timescaledb.compress_orderby = 'time DESC'
);

SELECT add_compression_policy('buffer_snapshots', INTERVAL '7 days', if_not_exists => TRUE);

-- ============================================================================
-- RETENTION POLICY (optional - 90 days)
-- ============================================================================

-- Uncomment to enable automatic data retention
-- SELECT add_retention_policy('task_records', INTERVAL '90 days', if_not_exists => TRUE);
-- SELECT add_retention_policy('buffer_snapshots', INTERVAL '90 days', if_not_exists => TRUE);
