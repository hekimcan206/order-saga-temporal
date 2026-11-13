-- v2 of the saga tells the customer when an order was unwound (ADR 0004). One notice per order: the key makes a
-- retried activity, or a replayed workflow, send it once.
CREATE TABLE sim.customer_notifications (
    idempotency_key text        PRIMARY KEY,
    order_id        text        NOT NULL,
    kind            text        NOT NULL CHECK (kind IN ('order-cancelled', 'order-cancelled-refund-issued')),
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX customer_notifications_order_id ON sim.customer_notifications (order_id);
