# Coordination dashboard (Power BI)

A Power BI project (`.pbip`) over the clash-coordination ledger that
`horizun_coordination` keeps per document: the pairs imported from a Navisworks
handoff, from a BCF file, or detected by `horizun_clash`, with their status.

**[Español](#español)**

## What it shows

- **Open Findings**: pairs with status `open` or `assigned`, still in the model.
- **Open Critical or High**: open pairs with priority `critical` or `high`.
- **Resolved by Model**: pairs a complete detection run measured as gone. Nobody
  sets this status by hand, which is why it counts as resolved.
- **Resolution Rate**: resolved by model over all findings.
- **Regressions**: pairs that came back after being resolved.
- Open against resolved per responsible discipline, open findings by priority,
  where the findings came from (`navisworks`, `bcf`, `horizun_clash`), and the
  list of findings.

Accepted risks and pairs closed by decision are counted apart
(`Closed by Decision`), because they are still in the model.

## Use it with your project

1. Export the ledger from Revit:

   ```json
   { "operation": "export", "format": "csv", "path": "C:/coordination/ledger.csv" }
   ```

   (`horizun_coordination`). Re-export after each coordination round.
2. Open `Horizun-Coordination/Horizun-Coordination.pbip` in Power BI Desktop.
3. **Transform data → Edit parameters → `LedgerCsvPath`**. Point it at the
   exported file, then refresh.

`data/coordination-ledger.csv` is **synthetic sample data**, 72 invented findings
with no project behind them. Copy it to the parameter's default path
(`C:\Horizun\coordination-ledger.csv`) to see the dashboard before you have a
ledger of your own.

The last six columns of the export are `scope`, `external_source`,
`external_issue_id`, `priority`, `responsible` and `immovable_discipline`. They
come after the original fourteen, in that order, so a reader of the older
fourteen-column file keeps working.

## How it was checked

The project was built and validated with the Power BI MCP:

- the TMDL was parsed by the official serializer and the PBIR checked against the
  official schema;
- Power BI Desktop opened it and refreshed it: 72 rows;
- a DAX query read the measures: 72 total, 34 open, 27 resolved (37.5%),
  11 open critical/high, 2 regressions;
- a capture of the rendered page confirmed each visual shows data.

## Español

Proyecto de Power BI sobre el registro de choques de `horizun_coordination`:
abiertos, críticos o altos abiertos, resueltos por el modelo (lo mide una detección
completa, no una persona), tasa de resolución, regresiones, y el reparto por
disciplina responsable, prioridad y origen (Navisworks, BCF o `horizun_clash`).

Para usarlo con tu proyecto:

1. Exporta el registro con `horizun_coordination` (`operation=export`,
   `format=csv`).
2. Abre el `.pbip` en Power BI Desktop.
3. Apunta el parámetro `LedgerCsvPath` al archivo exportado y actualiza.

Los datos de `data/` son sintéticos: 72 hallazgos inventados.
