-- The four downstream systems the saga talks to, simulated in one PostgreSQL database so tests can read the truth
-- after every run. Each system honours an idempotency key the way a real payment or dispatch API does, and each
-- undo writes a tombstone when it arrives before the effect (ADR 0002).

CREATE TABLE sim.platform_orders (
    idempotency_key text        PRIMARY KEY,
    order_id        text        NOT NULL,
    store_id        text        NOT NULL,
    status          text        NOT NULL CHECK (status IN ('accepted', 'cancelled')),
    -- true when the row was created by the undo (the effect had not happened yet): a late forward call must not apply.
    tombstone       boolean     NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX platform_orders_order_id ON sim.platform_orders (order_id);

CREATE TABLE sim.kitchen_tickets (
    idempotency_key text        PRIMARY KEY,
    order_id        text        NOT NULL,
    store_id        text        NOT NULL,
    ticket_no       bigint      GENERATED ALWAYS AS IDENTITY,
    status          text        NOT NULL CHECK (status IN ('confirmed', 'cancelled')),
    tombstone       boolean     NOT NULL DEFAULT false,
    lines           integer     NOT NULL DEFAULT 0 CHECK (lines >= 0),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX kitchen_tickets_order_id ON sim.kitchen_tickets (order_id);

CREATE TABLE sim.payments (
    idempotency_key text        PRIMARY KEY,
    order_id        text        NOT NULL,
    amount_kurus    bigint      NOT NULL CHECK (amount_kurus >= 0),
    -- captured -> refunded by an undo; voided = tombstone written by an undo that found no capture.
    status          text        NOT NULL CHECK (status IN ('captured', 'refunded', 'voided')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX payments_order_id ON sim.payments (order_id);

-- Money movements, append-only. The consistency check sums this table, not the payments status, so a bug that flips a
-- status without moving money (or the reverse) is caught.
CREATE TABLE sim.payment_ledger (
    entry_id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    idempotency_key text        NOT NULL,
    order_id        text        NOT NULL,
    kind            text        NOT NULL CHECK (kind IN ('charge', 'refund')),
    amount_kurus    bigint      NOT NULL CHECK (amount_kurus > 0),
    recorded_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX payment_ledger_order_id ON sim.payment_ledger (order_id);

CREATE TABLE sim.courier_dispatches (
    idempotency_key text        PRIMARY KEY,
    order_id        text        NOT NULL,
    status          text        NOT NULL CHECK (status IN ('assigned', 'pending', 'cancelled')),
    courier_id      text,
    tombstone       boolean     NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX courier_dispatches_order_id ON sim.courier_dispatches (order_id);

-- Injected faults. Consumed atomically (row lock), so concurrent attempts on different workers see each fault once.
CREATE TABLE sim.fault_rules (
    rule_id   bigint  GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id  text    NOT NULL,
    step      text    NOT NULL CHECK (step IN ('platform', 'kitchen', 'payment', 'courier')),
    phase     text    NOT NULL CHECK (phase IN ('forward', 'compensate')),
    kind      text    NOT NULL CHECK (kind IN ('reject', 'transient', 'lost-response', 'slow', 'pending')),
    remaining integer NOT NULL CHECK (remaining >= 0),
    delay_ms  integer NOT NULL DEFAULT 0 CHECK (delay_ms BETWEEN 0 AND 60000)
);
CREATE INDEX fault_rules_lookup ON sim.fault_rules (order_id, step, phase) WHERE remaining > 0;

-- The ledger is evidence: the application may add entries, never change or remove them.
REVOKE UPDATE, DELETE ON sim.payment_ledger FROM saga_app;
