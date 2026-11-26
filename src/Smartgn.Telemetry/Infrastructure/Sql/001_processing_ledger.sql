CREATE TABLE IF NOT EXISTS telemetry_processing (
    device_id text NOT NULL,
    event_id text NOT NULL,
    measured_ticks bigint NOT NULL,
    point_slot integer NOT NULL,
    fingerprint bytea NOT NULL,
    payload jsonb,
    stage text NOT NULL DEFAULT 'influx' CHECK (stage IN ('influx', 'alerting', 'done')),
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    quarantined_at timestamptz,
    last_error text,
    PRIMARY KEY (device_id, event_id),
    UNIQUE (device_id, measured_ticks, point_slot)
);
CREATE INDEX IF NOT EXISTS telemetry_processing_due
    ON telemetry_processing (stage, next_attempt_at)
    WHERE stage <> 'done' AND quarantined_at IS NULL;

CREATE TABLE IF NOT EXISTS telemetry_capacity (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    unfinished integer NOT NULL DEFAULT 0 CHECK (unfinished >= 0),
    quarantined integer NOT NULL DEFAULT 0 CHECK (quarantined BETWEEN 0 AND unfinished)
);
INSERT INTO telemetry_capacity DEFAULT VALUES ON CONFLICT DO NOTHING;

CREATE OR REPLACE FUNCTION telemetry_requeue(target_device text, target_event text)
RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
    UPDATE telemetry_processing
    SET quarantined_at = NULL, attempts = 0, next_attempt_at = now()
    WHERE device_id = target_device AND event_id = target_event AND quarantined_at IS NOT NULL;
    IF NOT FOUND THEN
        RETURN false;
    END IF;
    UPDATE telemetry_capacity SET quarantined = quarantined - 1;
    RETURN true;
END;
$$;
