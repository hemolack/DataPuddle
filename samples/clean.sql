-- Typed, trimmed copy of the feed. ${CustomerId} is filled in from the pipeline parameters.
CREATE TABLE stage.invoice_typed AS
SELECT
    CAST(InvoiceId AS INTEGER)      AS InvoiceId,
    ${CustomerId}                     AS CustomerId,
    CAST(InvoiceDate AS DATE)       AS InvoiceDate,
    CAST(Amount AS DECIMAL(12,2))   AS Amount,
    upper(trim(Status))             AS Status
FROM stage.invoice_raw;

-- If an invoice id appears more than once, keep the last row in the file.
CREATE TABLE stage.invoice_clean AS
SELECT * FROM stage.invoice_typed
QUALIFY row_number() OVER (PARTITION BY InvoiceId ORDER BY InvoiceDate DESC) = 1;

DROP TABLE stage.invoice_typed;
