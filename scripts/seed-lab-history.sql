-- ============================================================================
-- SCRUM-25 · Historical lab results — performance seed dataset
-- ============================================================================
-- Creates 1000 finished-product batches, each with a chemical panel, a sensory
-- evaluation and (for ~85%) a determination, spread evenly over all six product
-- lines (FM, FLM, SY, SK, DY, CD) and about 120 days from 2026-06-01.
--
--   ~60% Pass · ~25% Fail (one or two coded reasons) · ~15% Pending
--
-- Shapes follow SCRUM-20: liquid lines (FM, FLM) carry lactometer, temperature,
-- CLR, SNF and TS; fermented lines (SY, SK, DY, CD) carry fat and pH only, and
-- grade texture. Failed-on-fat panels carry an out-of-spec flag with the limit
-- in force, like a real panel would (SCRUM-22).
--
-- Used by:
--   * the JMeter run (perf/quality-lab.jmx, SCRUM-43) — the "read: lab history*"
--     samplers query this data on staging;
--   * the query-plan review in docs/lab-history.md.
--
-- Identifiable and re-runnable: every row uses dispatch number 'SEED-HIST-nnnn'
-- and batch code 'SEED-<line>-nnnn'. Running the script again deletes the
-- previous seed first (cascades to panels, sensory, determinations, reasons).
-- Real batches are never touched.
--
-- Run (MySQL 8.0.21+, after migrations have been applied):
--   mysql -h <host> -u <user> -p quality_lab < scripts/seed-lab-history.sql
--
-- Remove the seed without re-creating it:
--   DELETE FROM batch_work_items WHERE DispatchNumber LIKE 'SEED-HIST-%';
-- ============================================================================

SET SESSION cte_max_recursion_depth = 2000;

START TRANSACTION;

DELETE FROM batch_work_items WHERE DispatchNumber LIKE 'SEED-HIST-%';

DROP TEMPORARY TABLE IF EXISTS seed_hist;

CREATE TEMPORARY TABLE seed_hist AS
WITH RECURSIVE n (i) AS (
    SELECT 0
    UNION ALL
    SELECT i + 1 FROM n WHERE i < 999
),
base AS (
    SELECT
        i,
        ELT(i % 6 + 1, 'FM', 'FLM', 'SY', 'SK', 'DY', 'CD')                       AS line,
        (i % 6) < 2                                                              AS liquid,
        TIMESTAMPADD(MINUTE, i * 174, TIMESTAMP('2026-06-01 06:00:00'))           AS tested_at,
        (i * 37 + 11) % 100                                                      AS roll,
        (i * 7) % 5 + 1                                                          AS r1,
        ((i * 7) % 5 + 1) % 5 + 1                                                AS r2
    FROM n
)
SELECT
    i,
    line,
    liquid,
    tested_at,
    UUID()                                                                       AS batch_id,
    UUID()                                                                       AS panel_id,
    UUID()                                                                       AS sensory_id,
    UUID()                                                                       AS det_id,
    CONCAT('SEED-', line, '-', LPAD(i, 4, '0'))                                  AS batch_code,
    CONCAT('SEED-HIST-', LPAD(i, 4, '0'))                                        AS dispatch,
    CASE WHEN roll < 60 THEN 'Pass' WHEN roll < 85 THEN 'Fail' ELSE NULL END    AS result,
    CASE WHEN roll >= 60 AND roll < 85 THEN
        IF(liquid,
           ELT(r1, 'LOW_FAT', 'LOW_SNF', 'HIGH_PH', 'SENSORY_TASTE', 'SENSORY_COLOUR'),
           ELT(r1, 'LOW_FAT', 'LOW_PH', 'SENSORY_SMELL', 'SENSORY_COLOUR', 'SENSORY_TEXTURE'))
    END                                                                          AS reason1,
    CASE WHEN roll >= 60 AND roll < 85 AND i % 3 = 0 THEN
        IF(liquid,
           ELT(r2, 'LOW_FAT', 'LOW_SNF', 'HIGH_PH', 'SENSORY_TASTE', 'SENSORY_COLOUR'),
           ELT(r2, 'LOW_FAT', 'LOW_PH', 'SENSORY_SMELL', 'SENSORY_COLOUR', 'SENSORY_TEXTURE'))
    END                                                                          AS reason2,
    -- Seeded minimum-fat limits (QualityLabDbContext.HasData)
    CASE line WHEN 'FM' THEN 3.50 WHEN 'DY' THEN 2.00 ELSE 3.00 END              AS min_fat
FROM base;

-- Batches ---------------------------------------------------------------------
INSERT INTO batch_work_items
    (Id, BatchCode, DispatchNumber, ProductLine, StoringTankCode, CompletionTimeUtc, Status, CreatedAtUtc, UpdatedAtUtc)
SELECT
    batch_id, batch_code, dispatch, line, 'ST-SEED',
    TIMESTAMPADD(HOUR, -1, tested_at),
    CASE result WHEN 'Pass' THEN 'Cleared' WHEN 'Fail' THEN 'Failed' ELSE 'PanelRecorded' END,
    TIMESTAMPADD(HOUR, -1, tested_at),
    tested_at
FROM seed_hist;

-- Chemical panels ---------------------------------------------------------------
INSERT INTO chemical_panels
    (Id, BatchWorkItemId, BatchCode, DispatchNumber, ProductLine,
     FatPercent, LactometerReading, TemperatureCelsius, Ph, CorrectedClr, Snf, Ts,
     Version, HasOutOfSpecFlags, OutOfSpecFlagsJson, TestedBy, TestedAtUtc, CreatedAtUtc, IsLocked)
SELECT
    panel_id, batch_id, batch_code, dispatch, line,
    fat,
    IF(liquid, 29.00, NULL),
    IF(liquid, 27.00, NULL),
    IF(liquid, 6.70, 4.40),
    IF(liquid, 29.60, NULL),
    IF(liquid, 8.55, NULL),
    IF(liquid, ROUND(8.55 + fat, 2), NULL),
    1,
    low_fat,
    IF(low_fat,
       JSON_ARRAY(JSON_OBJECT('Parameter', 'FatPercent', 'ActualValue', fat, 'Limit', 'Min', 'LimitValue', min_fat)),
       NULL),
    'seed-tech',
    tested_at,
    tested_at,
    result IS NOT NULL
FROM (
    SELECT s.*,
           (reason1 = 'LOW_FAT' OR reason2 = 'LOW_FAT')                          AS low_fat,
           IF(reason1 = 'LOW_FAT' OR reason2 = 'LOW_FAT', ROUND(min_fat - 0.40, 2), 3.80) AS fat
    FROM seed_hist s
) p;

-- Sensory evaluations -------------------------------------------------------------
INSERT INTO sensory_evaluations
    (Id, BatchWorkItemId, Taste, TasteNote, Smell, SmellNote, Colour, ColourNote,
     Appearance, AppearanceNote, Texture, TextureNote, EvaluatedBy, EvaluatedAtUtc, CreatedAtUtc, UpdatedAtUtc, IsLocked)
SELECT
    sensory_id, batch_id,
    IF('SENSORY_TASTE'   IN (reason1, reason2), 'Unacceptable', 'Acceptable'),
    IF('SENSORY_TASTE'   IN (reason1, reason2), 'Off-flavour (seed)', NULL),
    IF('SENSORY_SMELL'   IN (reason1, reason2), 'Unacceptable', 'Acceptable'),
    IF('SENSORY_SMELL'   IN (reason1, reason2), 'Sour note (seed)', NULL),
    IF('SENSORY_COLOUR'  IN (reason1, reason2), 'Unacceptable', 'Acceptable'),
    IF('SENSORY_COLOUR'  IN (reason1, reason2), 'Off shade (seed)', NULL),
    'Acceptable', NULL,
    IF(liquid, NULL, IF('SENSORY_TEXTURE' IN (reason1, reason2), 'Unacceptable', 'Acceptable')),
    IF(NOT liquid AND 'SENSORY_TEXTURE' IN (reason1, reason2), 'Not set (seed)', NULL),
    'seed-tech',
    tested_at, tested_at, tested_at,
    result IS NOT NULL
FROM seed_hist;

-- Determinations ------------------------------------------------------------------
INSERT INTO determinations
    (Id, BatchWorkItemId, BatchCode, Result, ReasonCodesJson, OverrideReason, Notes,
     SupersededDeterminationId, SupersededById, DeterminedBy, DeterminedAtUtc, CreatedAtUtc)
SELECT
    det_id, batch_id, batch_code, result,
    CASE
        WHEN reason1 IS NULL THEN NULL
        WHEN reason2 IS NULL THEN JSON_ARRAY(reason1)
        ELSE JSON_ARRAY(reason1, reason2)
    END,
    NULL, 'SCRUM-25 seed', NULL, NULL,
    'seed-analyst',
    TIMESTAMPADD(MINUTE, 30, tested_at),
    TIMESTAMPADD(MINUTE, 30, tested_at)
FROM seed_hist
WHERE result IS NOT NULL;

-- Indexed reason codes (one row per code) -----------------------------------------
INSERT INTO determination_reason_codes (Id, DeterminationId, Code)
SELECT UUID(), det_id, reason1 FROM seed_hist WHERE reason1 IS NOT NULL
UNION ALL
SELECT UUID(), det_id, reason2 FROM seed_hist WHERE reason2 IS NOT NULL;

DROP TEMPORARY TABLE seed_hist;

COMMIT;

-- Quick check ---------------------------------------------------------------------
SELECT p.ProductLine,
       COUNT(*)                                             AS panels,
       SUM(d.Result = 'Pass')                               AS passed,
       SUM(d.Result = 'Fail')                               AS failed,
       SUM(d.Id IS NULL)                                    AS pending
FROM chemical_panels p
LEFT JOIN determinations d ON d.BatchWorkItemId = p.BatchWorkItemId AND d.SupersededById IS NULL
WHERE p.DispatchNumber LIKE 'SEED-HIST-%'
GROUP BY p.ProductLine
ORDER BY p.ProductLine;
