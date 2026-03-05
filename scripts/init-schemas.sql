-- Create bounded context schemas
CREATE SCHEMA IF NOT EXISTS billing;
CREATE SCHEMA IF NOT EXISTS payments;
CREATE SCHEMA IF NOT EXISTS ledger;
CREATE SCHEMA IF NOT EXISTS infra;  -- outbox, audit_log, processed_messages

-- Grant permissions
GRANT ALL ON SCHEMA billing TO billing_user;
GRANT ALL ON SCHEMA payments TO billing_user;
GRANT ALL ON SCHEMA ledger TO billing_user;
GRANT ALL ON SCHEMA infra TO billing_user;
