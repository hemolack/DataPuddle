# Deidentify plugin

SQL functions for the five de-identification actions used with the HIPAA Safe Harbor method
(45 CFR 164.514(b)(2)): **Remove, Generalize, Pseudonymize, Scan, Keep**.

The functions do not read extended properties or rewrite tables on their own. You write the
`SELECT` that calls them, and the checklist query below shows which action each column was tagged with.

## Install

1. Build: `dotnet build -c Release` in this folder.
2. Copy `bin/Release/net8.0/DataPuddle.Deidentify.dll` next to DataPuddle.
3. Configure in `appsettings.json`:

```json
"Plugins": {
  "Settings": {
    "Deidentify": {
      "MappingFile": "secure/pseudonyms.jsonl",
      "RestrictedZip3": "000 111 222",
      "ScanTermsFile": "secure/known-names.txt"
    }
  }
}
```

`.plugins` in the shell lists the functions once the plugin has loaded.

## Settings (`Plugins:Settings:Deidentify`)

| Setting | Purpose |
|---|---|
| `MappingFile` | Saves each identifier-to-surrogate pair so surrogates stay the same between runs. Without it the pairs live in memory and are lost when the session ends. **This file links surrogates to real identifiers, so it is PHI.** Keep it out of the de-identified data set and restrict access. |
| `RestrictedZip3` | Three-digit ZIP prefixes whose area has 20,000 people or fewer, separated by commas or spaces. |
| `RestrictedZip3File` | The same list in a text file (`#` starts a comment). |
| `ScanTermsFile` | One term per line (patient, provider and facility names), replaced by `[NAME]` in `deid_scan`. |

`deid_zip` is **not registered** until `RestrictedZip3` or `RestrictedZip3File` is set. The list has to be built
from current Census population data for three-digit ZIP areas. The plugin does not ship one, so it cannot hand
you a stale list. The values `000 111 222` above are placeholders, not real prefixes.

## Functions

| Action | Function | Result |
|---|---|---|
| Remove | `deid_remove(x)` | Always `NULL`. Leaving the column out of the output table is cleaner; use this when the column must exist. |
| Generalize | `deid_year(text)` | Year of a date, e.g. `'2024'`. |
| Generalize | `deid_birth_year(text)` | Year of birth, or `'90+'` if the person is 90 or older today. |
| Generalize | `deid_birth_year_as_of(text, asOf)` | Same, measured at the date in the second argument. |
| Generalize | `deid_age(n)` | Age, with 90 and over becoming `90` (meaning 90 or older). |
| Generalize | `deid_zip(text)` | First three digits, or `'000'` for a restricted area. |
| Pseudonymize | `deid_pseudonym(value, domain)` | Random surrogate. `domain` names the identifier type. |
| Scan | `deid_scan(text)` | Free text with SSNs, phones, emails, URLs, IPs, dates and known names replaced by tokens. |
| Scan | `deid_scan_terms(text, terms)` | Same, plus terms for that row separated by `\|`, e.g. the patient's own name. |
| Keep | `deid_keep(text)` | The text unchanged. For non-text columns just select the column. |

All Generalize functions return `NULL` for input they cannot read with certainty, such as a two-digit year
(`3/17/24`) or a ZIP with the wrong shape. Nothing unrecognized passes through.

## Example

```sql
CREATE TABLE deid.customer AS
SELECT
    deid_pseudonym(customer_id, 'customer_id') AS customer_id,   -- Pseudonymize
    deid_birth_year(CAST(birth_date AS VARCHAR)) AS birth_year,  -- Generalize
    deid_zip(zip) AS zip3,                                        -- Generalize
    deid_scan_terms(notes, first_name || '|' || last_name) AS notes, -- Scan
    diagnosis_code                                                -- Keep (reviewed)
FROM dbo.customer;                                                -- name, ssn, phone: Remove (left out)

CREATE TABLE deid.invoice AS
SELECT
    deid_pseudonym(customer_id, 'customer_id') AS customer_id,   -- same domain: joins to deid.customer still work
    deid_year(CAST(invoice_date AS VARCHAR)) AS invoice_year,
    amount
FROM dbo.invoice;
```

Points that matter when writing the query:

- **Cast dates to text.** DuckDB does not pass a `DATE` or `TIMESTAMP` column to a text function on its own, so
  write `CAST(birth_date AS VARCHAR)`. Cast a numeric ZIP and restore its leading zeros first:
  `deid_zip(lpad(CAST(zip AS VARCHAR), 5, '0'))`.
- **Use one domain per identifier type.** `deid_pseudonym(customer_id, 'customer_id')` gives the same surrogate in
  every table, which is what keeps joins working.
- **Date shifting does not meet Safe Harbor.** Dates other than the year must go.

## Checklist of tagged columns

If your tags are extended properties named `PHI_CATEGORY` and `DEID_ACTION`, this lists every column of a
schema with its tags. A row with empty tags is a column nobody has classified yet, and it should be resolved
before any output is released.

```sql
SELECT c.table_schema AS schema_name, c.table_name, c.column_name,
       max(p.property_value) FILTER (WHERE p.property_name = 'PHI_CATEGORY') AS phi_category,
       max(p.property_value) FILTER (WHERE p.property_name = 'DEID_ACTION') AS deid_action
FROM information_schema.columns c
LEFT JOIN puddle_meta.extended_properties p
  ON p.schema_name = c.table_schema AND p.table_name = c.table_name AND p.column_name = c.column_name
WHERE c.table_schema = 'dbo'
GROUP BY ALL
ORDER BY 1, 2, 3;
```

## Extended properties on the output

Direct copies (for example `diagnosis_code`) inherit their source column's extended properties. Everything
produced by a `deid_*` function is a computed column, so DataPuddle will not tag it and will print a note
listing it. Add rows to `puddle_meta.extended_properties` for those if you want the output to record what was done.

## Limits

- `deid_scan` is best effort. It misses misspelled names, nicknames and anything not in your terms list, and it
  does not remove a bare month ("in March") or other partial date wording. Remove or review free text that must be safe.
  If a scan runs out of time on pathological text, it returns `[REDACTED]` instead of the text.
- Safe Harbor also requires that you have no actual knowledge that what remains could identify someone. Rare
  diagnoses, extreme values and small populations are not caught by any column-level function.
- Plugin functions exist only for the session. They are not stored in `puddle.db`.
- DuckDB may evaluate a function call whose arguments are all constants once and reuse the result.
  `deid_pseudonym` is unaffected because identical input maps to identical output.
- A plugin is arbitrary code with the program's full permissions. Only load DLLs you built or trust.
