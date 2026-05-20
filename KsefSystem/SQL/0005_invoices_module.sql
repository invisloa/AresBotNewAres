-- 0005_invoices_module.sql

-- 1. Create Invoices Table with STRICT Constraints
CREATE TABLE invoices (
    id SERIAL PRIMARY KEY,
    tenant_id INT NOT NULL,
    invoice_number VARCHAR(50) NOT NULL,
    vendor_id VARCHAR(50) NOT NULL,
    net_total DECIMAL(18, 2) NOT NULL,
    vat_total DECIMAL(18, 2) NOT NULL,
    gross_total DECIMAL(18, 2) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

    -- THE HARD CONSTRAINT (Symmetry Check)
    CONSTRAINT chk_invoices_math CHECK (gross_total = net_total + vat_total)
);

-- 2. Create Invoice Lines Table (Optional but good practice for completeness)
CREATE TABLE invoice_lines (
    id SERIAL PRIMARY KEY,
    invoice_id INT NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
    description VARCHAR(255) NOT NULL,
    net_amount DECIMAL(18, 2) NOT NULL,
    vat_amount DECIMAL(18, 2) NOT NULL,
    gross_amount DECIMAL(18, 2) NOT NULL,

    -- THE HARD CONSTRAINT for Lines
    CONSTRAINT chk_lines_math CHECK (gross_amount = net_amount + vat_amount)
);

-- 3. Create KSeF Quarantine Table (Staging Area)
CREATE TABLE ksef_import_quarantine (
    id SERIAL PRIMARY KEY,
    tenant_id INT NOT NULL,
    ksef_reference_number VARCHAR(100) NOT NULL UNIQUE,
    raw_xml TEXT NOT NULL, -- Storing the full original XML
    validation_errors JSONB, -- Flexible storage for validation errors
    status VARCHAR(20) NOT NULL DEFAULT 'NEW', -- NEW, QUARANTINED, PROCESSED, REJECTED
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Index for faster lookup of new items
CREATE INDEX idx_ksef_quarantine_status ON ksef_import_quarantine(status);
