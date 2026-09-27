# Extended tool notes

Detail that does not belong in `tools/list` descriptions. One section per topic.

## Advertised schemas and the contract

`tools/list` advertises an **abridged copy** of each input schema; the contract
compiled into the server (and into the add-in, which shares its hash) is the
full one. Nothing is deferred: every tool, argument, `kind` and `operation` is in
`tools/list`, with its enum, its `required` set and its `type`. What the copy
folds:

- **Union field schemas repeated inside branches.** Where a node has both
  `properties` and a `oneOf`/`anyOf`/`allOf`, a branch that repeats a union field
  keeps only what it adds or tightens. Both apply to the same instance, so
  `union AND branch'` accepts exactly what `union AND branch` accepts; `type` is
  always kept and a fully repeated field becomes `{"type": ...}`, never `{}` or a
  boolean. MEASURED 2026-09-26: `horizun_create_elements` 81,409 -> 30,707 bytes,
  `horizun_document_session` 17,551 -> 9,034.
- **The `idempotency_key` text** (305 characters, 65 places) is shown in a short
  form; the contract keeps every word.
- **Descriptions over 250 characters** are capped, as before.

The exact schema is one read away:

| Resource | Serves |
|---|---|
| `horizun://contract/tools` | every contract row |
| `horizun://contract/tools/{tool}` | one tool's row, full `input_schema` |
| `horizun://contract/tools/{tool}/{variant}` | one discriminated branch verbatim, with the fields it requires |

Variants are found in the contract by rule (a `oneOf`/`anyOf` whose branches are
each selected by one `const` of the same field, listed in that field's enum):
today `horizun_create_elements` `kind` (32) and `horizun_document_session`
`operation` (5). `resources/templates/list` returns both templates and
`completion/complete` fills `{tool}` and `{variant}`.

**`schema_help`.** A call that failed (`isError`, or a rehearsal with
`invalid > 0`) and whose arguments violate the full contract gets advice. A
reply that already has `structuredContent` (a rehearsal, or an error with a
fallback block or trace) carries `structuredContent.schema_help`: `contract_uri`,
up to 20 `pointer: message` violations and, for a discriminated tool, one entry
per kind/operation in the arguments with its `uri` and (for up to 3 branches,
12 KB) the verbatim branch; an unknown value gets `valid_values`. It is bounded
at 16 KB. A failing `oneOf` is explained by the branch the row names by its
`const` (a wall row is told what a wall needs), and by the closest branch only
when it names none. An error with no `structuredContent` gets none created: it
gains one text line with the first violation and the URI of the schema that holds
it, because readers that take `structuredContent` in place of the text (the
procedure judge; clients that forward it to the model) would otherwise see the
advice and lose the error. In every case this is advice attached after the
verdict: a success keeps its text exactly the payload, an error gains one line,
and `isError`, `invalid`, `errors`, `fallback` and `capability_gaps` are
unchanged.

**Names inside a branch.** Every argument name is kept at every instance
location. Inside one branch, a field whose whole nested object schema equals the
union field's (for example `source_reference` in 27 `create_elements` kinds) is
shown as `{"type":"object"}`, and its nested names are read in the union field one
level up; `AdvertisedSchemaEquivalenceTests` rehydrates every such fold. Keeping
stub copies of those names in each branch would cost about 8 KB (estimated from
the field's 9 keys), the margin this work exists to create.

**What a call is validated against.** There is no generic JSON-Schema check
before dispatch, and this change does not add one. Validation reads the FULL
contract where it always did: `ToolInputRules` in the add-in, each command's
parser, the host tools' unknown-argument refusals, and the procedure/workflow
checks of top-level `required`/`additionalProperties`. The advertised copy is
read only by `tools/list` and tests.

**Client evidence** (from source, not a live run): codex-rs
`sanitize_json_schema` keeps `oneOf`/`anyOf`/`allOf`/`enum`/`items`/`required`/
`additionalProperties`, turns `const` into `enum`, drops `maxItems`/`if`/`then`/
`default`, and coerces boolean schemas to `{type: string}` (hence no boolean
subschemas). Claude Code flattens a top-level `oneOf` into an "Input constraint"
line and passes a nested `oneOf` through. The copy adds no keyword kinds.

**The ledger.** `tests/Horizun.Server.Tests/tools-list-ledger.json` records, per
tool, the SHA-256 of the advertised description and schema and the entry's bytes,
plus the totals of every permission profile and tool pack (MEASURED 2026-09-26:
524,199 -> 460,166 bytes for all 122 tools; ceiling 524,288). A change that moves
any of it fails naming the tool and the signed delta. To accept it deliberately,
set `HORIZUN_UPDATE_TOOLS_LEDGER=1`, run `dotnet test tests/Horizun.Server.Tests
-c Release --filter "FullyQualifiedName~ToolsListLedger"`, then review and commit the diff
with the change that caused it (the rewrite fails once, and refuses under `CI`).

**Reserve levers** (measured on the prototype, not implemented): defer the
discriminated branches behind the variant template plus a host-resident describe
tool (about -14.8 KB; a new contract row moves `Contract.Hash`); cap descriptions
at 230 instead of 250 (-5.2 KB, lossy); omit spec-default annotations (-7.5 KB,
client-display risk).

## Wire parse errors and cancellation retries

### Parse errors (-32700)

A line on stdin that is not valid JSON is answered with JSON-RPC error -32700,
`id: null`, and an `error.data` object:

| field | meaning |
|---|---|
| `hint` | `unescaped_backslash` (a Windows path such as `C:\x` not written as `C:\\x`), `concatenated_messages` (two messages on one line), `truncated_message` (the line ended inside a string or object, usually a raw newline inside a string value), `byte_order_mark`, `bare_word` (an unquoted word where JSON needs a quoted string, `:` or `,`), `not_json`, `invalid_json` |
| `line`, `column`, `offset`, `length` | where the parser stopped; `offset` is the zero-based character index in the received line |
| `shape`, `shape_caret` | a window of up to 20 characters either side of `offset`, with every letter shown as `a`, every digit as `0` and every non-ASCII character as `?`; quotes, backslashes and punctuation are kept |
| `content_echoed` | always `false`. The words, numbers, paths and tokens you sent are never repeated |

The server log line carries the same fields and the client name that
`initialize` declared (`clientInfo.name`, reduced to 40 safe characters).

Build every message with a JSON serializer (`ConvertTo-Json`, `json.dumps`) and end each one with a newline.
`scripts/hz-call.ps1` checks its arguments before it starts a server. It takes
`-ArgumentsObject @{ path = 'C:\folder\a.rvt' }` so a caller never has to escape
JSON by hand.

### Cancellation and timeout: what a retry does

When a tool call is cancelled by the client or times out, the error detail
(`revit_transport_failed`) includes a `retry` object, and the message ends with a
matching `RETRY:` sentence:

| `retry.verdict` | when | what to do |
|---|---|---|
| `same_key_runs_fresh` | the request was removed from Revit's queue before it started, or was never sent | send the identical call again. If it had an `idempotency_key` and `confirmation_token`, reuse them: neither was consumed |
| `same_key_replays_recorded_answer` | the request may have started, and it carried an `idempotency_key` | send the identical call with the **same** key. Revit runs one command at a time, so the retry waits behind the original and then replays the recorded answer from the durable ledger without writing again. A new key would write a second time |
| `inspect_model_first` | the request may have started, and it carried no key | nothing can prove what happened, so inspect the model before sending anything |

If the call waited 60 s or more, the sentence also names `horizun_submit_job`
and `horizun_job_status`, and `retry.prefer` is set to `horizun_submit_job`.
The size of the batch (`retry.batch_items`) is reported but does not trigger
this advice: in 1,447 logged `horizun_create_elements` calls the slowest took
3.4 s. Progress: any `tools/call` that sends `_meta.progressToken` already gets
`notifications/progress` every 5 s, with the bridge's queue or run state when
the bridge can see it.

---

**Resumen (español).** Un -32700 ahora dice dónde falló (línea, columna,
posición), qué tipo de fallo parece (`hint`: barra invertida sin escapar, dos
mensajes en una línea, mensaje cortado, BOM, palabra sin comillas) y una ventana
de texto donde cada letra se muestra como `a` y cada dígito como `0`. Nunca se
repite el contenido enviado. Una llamada cancelada o que agotó el tiempo indica
qué pasa si se reintenta. Si el trabajo no empezó, la misma llamada corre de
nuevo. Si pudo empezar y lleva `idempotency_key`, la misma clave devuelve la
respuesta registrada sin escribir otra vez; una clave nueva duplicaría la
escritura. Si pudo empezar y no lleva clave, hay que revisar el modelo antes de
reenviar nada. Si la llamada esperó 60 s o más, se recomienda
`horizun_submit_job`.



## Phases, design options, parts and assemblies

Two tools, each with an `operation` switch. Reads need no token. Every write takes
`target_document`, runs as a dry run by default and returns a single-use
`confirmation_token`; the apply spends it.

**How a write is verified.** The write runs inside a `TransactionGroup`. The dry
run applies it (every inner transaction commits), reads a `PostconditionCheck`
back from the model and rolls the whole group back. The apply does the same
write, checks it while the group can still be undone (any unverified property
rolls everything back), assimilates, then reads the checklist again from the
committed model. That second reading is what the reply publishes in
`postconditions`.

### `horizun_manage_phases`

| operation | writes | what it does |
|---|---|---|
| `list` | no | Phases in Revit's order; each phase filter with `presentation` for `new`, `existing`, `demolished`, `temporary` (`by_category`, `overridden`, `hidden`); design options with option set, `is_primary`, member count and the first 100 member ids; the active option. `design_options_writable` is always `false`. |
| `element_status` | no | For `element_ids`: created and demolished phase, `ElementOnPhaseStatus` in `phase_id` (default: last phase), and the design option (`main_model` when the element is in none). |
| `set_element_phases` | yes | Sets `created_phase_id` and/or `demolished_phase_id` (`-1` clears the demolition). The final pair is checked in order: demolition in the same phase is allowed (Temporary), before the creation is refused. Only elements where `HasPhases` and `ArePhasesModifiable` are true. |
| `create_phase_filter` | yes | `PhaseFilter.Create` with a unique `name`, then the presentations you name. |
| `edit_phase_filter` | yes | `filter_id` plus a new `name` and/or `presentation`. |
| `rename_phase` | yes | `phase_id` plus a unique `name`. |
| `create_phase` | refused | `no_phase_creation_api`. |
| `assign_design_option` | refused | `no_design_option_assignment_api`. |

**API limits (checked in RevitAPI.xml for 2023–2027, the same in every year).** No
call creates, inserts, deletes or reorders a phase. `Element.DesignOption` is
read-only, and nothing adds elements to an option set, moves them between options
or sets the active option. Python cannot do these things either, so the refusals
offer no fallback. Moving an element into a secondary option hides it from every
view that shows the primary option or the main model. That is why the refusal
tells you to review views before you do it by hand.

### `horizun_manage_assemblies_parts`

| operation | writes | what it does |
|---|---|---|
| `list` | no | Every part (id, excluded, source ids, original category), every assembly (name, naming category, members). With `element_ids`: whether each one is valid for parts, its part ids and its assembly. |
| `create_parts` | yes | `PartUtils.CreateParts` on `element_ids`. It refuses elements that already have parts or that fail `AreElementsValidForCreateParts`. Verified: each source has associated parts. |
| `divide_parts` | yes | `PartUtils.DivideParts` of part `element_ids` by `reference_ids`, which must be levels, grids or reference planes. The API requires a sketch plane even without curves, so a horizontal one is created. Verified: each divided part has at least two derived parts. The cut geometry itself is not checked. |
| `exclude_parts` / `restore_parts` | yes | Sets `Part.Excluded`, re-read per part. |
| `dissolve_parts` | yes | Deletes the `PartMaker` of each original in `element_ids`. This is the only way the API removes parts, and it loses every division, exclusion and part parameter. Verified: the originals still exist and have no parts. |
| `create_assembly` | yes | `AssemblyInstance.Create` with `naming_category_id` (default: the first member's category, checked with `IsValidNamingCategory`) and an optional unique `name`. The name is set in a second transaction because Revit only allows it after the creating transaction commits. Verified: members, naming category, name. |
| `assembly_views` | yes | `views` from `3d`, `plan`, `section_a`, `section_b`, `elevation_front`, `part_list`. Verified: each view's `AssociatedAssemblyInstanceId`. |
| `disassemble` | yes | `AssemblyInstance.Disassemble`. Verified: the instance is gone and every former member exists outside any assembly. |

The tool is marked destructive because `dissolve_parts` and `disassemble` remove elements.

### Live probes

`scripts/live-probes/phases-options-parts.probes.ps1` runs in the write tier of
`verify-live.ps1`. It creates three walls of its own at x ≥ 720 m and runs these
cases:

1. `list` reads the phases and the phase filters.
2. `create_phase` refuses with the API reason.
3. `set_element_phases` sets the first/last phase on one wall, reads the wall back as demolished, then restores it.
4. Design options `list`. It reports `not_covered` when the fixture has no options.
5. `create_parts` then `dissolve_parts` on one wall.
6. `create_assembly` of the other two walls, then `disassemble`.

The probe deletes the three walls afterwards. `phases-options-parts.tests.ps1` runs
the module offline against a fake `Call` and `Apply`. `divide_parts`,
`exclude_parts`, `assembly_views`, the phase-filter writes and `rename_phase` have
no live probe yet.

### Resumen (español)

- `horizun_manage_phases` lee:
  - las fases en orden;
  - los filtros de fase, con su presentación por estado nuevo, existente, demolido y temporal;
  - las opciones de diseño y el estado de fase de cada elemento.
- `horizun_manage_phases` escribe fases de elementos, crea y edita filtros de fase y renombra fases.
- La API de Revit 2023–2027 no permite dos cosas, y la herramienta las rechaza con el motivo y sin fallback a Python:
  - crear o reordenar fases;
  - mover elementos entre opciones de diseño.
- `horizun_manage_assemblies_parts` lista, crea, divide, excluye, restaura y disuelve partes. También crea ensamblajes, genera sus vistas y los desensambla.
- Cada escritura:
  1. se ensaya dentro de un TransactionGroup que se revierte;
  2. se aplica con el token;
  3. se relee del modelo confirmado.

  Si algo no cuadra, se revierte entera.



## Views, schedules and DWG layers

Extends three existing tools instead of adding new ones. Every write keeps the
existing contract: dry run by default, a confirmation token bound to what the ids
resolve to, one transaction, and a re-read after the commit.

### `horizun_manage_views` — filters, V/G and templates

| Operation | What it does | What is re-read |
|---|---|---|
| `edit_filter` | Replaces the rules of an existing filter (`filter_id`, `filter_name` or `filter_key`), optionally its `categories`; `rules` + `match` as in `create_filter`. Revit's own `ElementFilterIsAcceptableForParameterFilterElement` is asked before any transaction. | The filter's rules as Revit hands them back, as a text signature (`AND(`/`OR(`, rule class, parameter id, evaluator, value), compared with the signature of the filter that was written; the categories. The reply carries `rules_reread`. |
| `order_filters` | Puts `filter_ids` (ALL the filters of the view, top first) in that order. Revit has no `SetFilterOrder` in 2023–2027, so every filter is removed and added back, restoring its overrides, visibility and enabled flag. | `GetOrderedFilters()` and every filter's restored state. |
| `apply_filter` + `enabled` | The filter's *Enable Filter* flag (`SetIsFilterEnabled`). | `GetIsFilterEnabled`. |
| `explain_graphics` | READ. For 1–20 `element_ids` in a view: every layer that can decide how the element looks — element override, each filter in order (enabled, matches, visible, overrides), the category row — and the winner per property plus the visibility verdict. When the view's template governs filters or V/G, those rows come from the template and say `from_template`. The dry run already carries the report. | Recomputed after the commit on the apply path. |
| `set_category_visibility` | Now takes `subcategory` (by name), `overrides` (colours, weight, `line_pattern` by name or `Solid`, transparency, halftone) and/or `hidden`. A view whose template governs the category's V/G row is refused naming the template and the alternative: the same action with `view_id=<template>`, or `set_template_controls controlled=false`. | Hidden flag and each override field that was set. |
| `create_template` | `View.CreateViewTemplate` from `view_id`, named `name`. | `IsTemplate` and the name. |
| `set_template_controls` | Template `view_id`, `parameters` (BuiltInParameter names such as `VIS_GRAPHICS_MODEL`, or labels), `controlled` true/false. A parameter the template cannot govern is refused listing the ones it can. | `GetNonControlledTemplateParameterIds`. |
| `apply_template` with `template_view_id: -1` | Removes the template. | `ViewTemplateId` is invalid. |

Precedence used by `explain_graphics` (Revit's, not ours): element override, then
filters top-down (a higher filter wins property by property; disabled or
non-matching filters contribute nothing), then the category row, then object
styles. Visibility is not a precedence: any layer that hides wins.

### `horizun_create_schedule` — multi-category and key schedules

- `category: "OST_MultiCategory"` (or `multi_category`) creates a multi-category
  schedule (`ViewSchedule.CreateSchedule` with `InvalidElementId`, the same on
  2023–2027; `BuiltInCategory` has no `OST_MultiCategory`). `Category` is accepted
  as a field alias. The re-read checks the committed category id is invalid.
- `key_schedule: true` + `key_rows` (0–500) creates a key schedule over one
  category and inserts that many key rows. The re-read checks `IsKeySchedule` and
  counts the key elements owned by the schedule. `include_links` and `itemized` do
  not apply to key schedules and are refused when sent.

### `horizun_export` — the DWG layer table

- `format: "dwg_layers"`, `output_path` ending in `.json`, `dwg_setup.name`:
  reads the named DWG export setup's layer table and writes it to the file. If the
  setup does not exist it is created (from `dwg_setup.source`, or with Revit's
  defaults). `dwg_setup.layers` rows (`category` as a BuiltInCategory token or the
  display name, optional `subcategory`, `layer`, `color`, `cut_layer`, `cut_color`
  with AutoCAD index colours 1–255) edit existing rows; a row the table does not
  have is refused, never added.
- **Persistence is measured, not assumed.** After `SetExportLayerTable` +
  `SetDWGExportOptions`, the table is re-read from a fresh `FindByName` before the
  commit (a mismatch rolls back and names the rows) and again after it; each edit
  reports `persisted`. Save/close/reopen is not part of the measurement.
- `format: "dwg"` with `dwg_setup: { "name": ... }` exports using that setup
  (`DWGExportOptions.GetPredefinedOptions`); `acad_version` still overrides the
  file version. The layer mapping is not proved from the DWG binary.

API availability, checked in each `RevitAPI.xml` 2023–2027: `View.GetOrderedFilters`,
`SetIsFilterEnabled`, `ParameterFilterElement.SetElementFilter`,
`ViewSchedule.CreateKeySchedule`, `BaseExportOptions.Get/SetExportLayerTable`,
`ExportDWGSettings.Create/FindByName/SetDWGExportOptions` exist in all five years;
`SetFilterOrder` exists in none. Whether each year keeps the layer-table write is
recorded per year by the live probe `scripts/live-probes/views-schedules-dwg.probes.ps1`.

**Resumen (español).** `horizun_manage_views` suma editar reglas de filtros,
reordenar/habilitar filtros, un informe de precedencia (elemento > filtros en
orden > categoría > estilos de objeto; si la plantilla gobierna V/G, esas filas son
de la plantilla), V/G por categoría y subcategoría con patrón de línea, y
plantillas (crear, qué parámetros gobiernan, aplicar/quitar con `-1`). Una vista
cuya plantilla gobierna V/G se rechaza indicando editar la plantilla.
`horizun_create_schedule` crea tablas multicategoría (`OST_MultiCategory`) y de
claves (`key_schedule`, `key_rows`). `horizun_export` lee y escribe la tabla de
capas de una configuración DWG con nombre (`format: dwg_layers`) y exporta con
ella; la persistencia se mide releyendo antes y después del commit, por año.



## Groups and worksets

### `horizun_manage_groups`

| operation | needs | what is re-read after the commit |
|---|---|---|
| `list` | - (optional `type_id`, `max_rows`) | read only: every group type (kind `model` / `detail` / `attached_detail`), its instances, member ids (500 per instance), nested groups, parent group, attached detail group types |
| `create` | `element_ids`, `name` | the group exists, its members equal `element_ids`, the type carries `name` |
| `add_members` / `remove_members` | one `group_ids` entry (the reference), `element_ids`, `scope` when the type has other instances | the reference's members, the type name, whether the old type was deleted or kept, the new type's instance count, and every other instance |
| `rename_type` | `type_id`, `name` | the name and an unchanged instance count |
| `duplicate_type` | `type_id`, `name` | the new type's name, zero instances on it, the source's count unchanged |
| `swap_type` | `group_ids`, target `type_id` | each instance's type and the target's instance count |
| `ungroup` | `group_ids` | each group is gone and each former member exists outside any group |
| `convert_to_link` | - | refused: `code: api_absent` |

**Redefining a group.** Revit has no edit-group API. The reference instance is
ungrouped, the member set changed, and a new group type is made from it. The
reference gets a NEW group id and its instance parameters are not carried. When
the type has other instances the call must say `scope`:

- `all_instances`: every other instance is swapped onto the new type, the old type
  is deleted and the new one takes its name. Their member ids change, and members
  removed from them are deleted, not left loose. The new type's origin is Revit's,
  so each swapped instance is moved back by a displacement MEASURED from a member
  whose (category, type) is unique before and after; then every member it held
  before is checked by category, type and bounding box (0.001 ft). No unique
  member, no measurement: the whole change rolls back.
- `this_instance`: only the reference moves to a new type named `name`; the other
  instances stay on the old type, untouched.

A type with attached detail group types is refused: the regroup would orphan them.

**Convert to link.** `GroupType` offers `LoadFrom` and nothing that saves a group as
a model, in every RevitAPI 2023–2027. Do it in Revit (select the group, Link).

### `horizun_manage_worksets`

Every operation, `list` included, needs a workshared model; otherwise the reply is
`code: not_workshared` and nothing is read or written.

| operation | needs | re-read |
|---|---|---|
| `list` | - | read only: user worksets with open, editable, owner, visible-by-default, element count, and the active workset |
| `create` | `name` | the workset exists with that name |
| `rename` | `workset_id`, `name` | the name (a workset owned by another user is refused) |
| `move_elements` | destination `workset_id`, `element_ids` OR `category` (BuiltInCategory, host only, ≤ 10,000) | each moved element's `WorksetId`, and that no skipped element moved |
| `set_default` | `workset_id` (open) | the active workset id |
| `visibility` | `workset_id`, `view_ids`, `visibility` = `visible` / `hidden` / `use_global` | each view's workset visibility |

Elements owned by another user are reported with the owner (`borrowed_by_other`)
and never forced; elements whose workset parameter is read-only (group members,
hosted sub-elements) are reported `workset_not_editable`. Both make the outcome
`partial`, not `verified_applied`. `set_default` changes a session setting, so its
dry run is a measured preview, not a provisional change.

### Resumen en español

`horizun_manage_groups` lista, crea, redefine (añadir/quitar miembros), renombra,
duplica, cambia de tipo y desagrupa grupos. Revit no tiene API de "editar grupo":
la instancia de referencia se desagrupa y se reagrupa (obtiene un id nuevo). Si el
tipo tiene otras instancias, `scope` es obligatorio: `all_instances` cambia todas
(se recolocan por un desplazamiento medido y se comprueba cada miembro) y
`this_instance` solo la de referencia, con un tipo nuevo. `convert_to_link` se
rechaza tipado (`api_absent`): no existe en la API 2023–2027.
`horizun_manage_worksets` solo opera sobre modelos workshared (si no,
`not_workshared`): lista, crea, renombra, mueve elementos (los prestados por otro
usuario se reportan, nunca se fuerzan), fija el workset activo y la visibilidad
por vista. Todo con ensayo por defecto, token de un solo uso y relectura tras el
commit.



## MEP routing and sizing

`horizun_mep_routing` is one multi-operation tool over the routing preferences and
size catalogs of pipes, ducts, conduits and cable trays. It is separate from
`horizun_manage_system_types` on purpose: that tool duplicates types and writes their
parameters; a routing rule is not a parameter, a size catalog is not an element type,
and a resize is an instance write whose side effects land on other elements.

| operation | writes | what it does | what is re-read after commit |
|---|---|---|---|
| `read` | no | `type_id`: the type's rules per `RoutingPreferenceRuleGroupType` (part, description, size ranges), preferred junction and the fittings a rule can name. Conduit/cable-tray types carry no routing preferences; their elbow/tee/cross/transition/union are listed instead. `segment_id`: material, schedule, roughness, every nominal/inner/outer size. `element_ids`: kind, size, catalog and whether the size is in it. Nothing: every MEP type, every pipe segment, and the duct (round/rectangular/oval), conduit (per standard) and cable-tray catalogs. | - |
| `set_rules` | yes | Ordered `add` / `remove` / `move` edits per group on a pipe or duct type, plus optional `junction` (Tee/Tap). An add without `min_size`/`max_size` covers all sizes. Indexes refer to the list as the previous edit left it. | Every touched group equals the list computed from the list read before plus the edits; every untouched group is unchanged; the junction. |
| `add_sizes` / `remove_sizes` | yes | `catalog` = `segment` (with `segment_id`), `conduit` (with `conduit_standard`), `duct_round`, `duct_rectangular`, `duct_oval` or `cable_tray`. Segment and conduit sizes need `inner` and `outer`; conduit sizes also `bend_radius`. A size already present (add) or absent (remove) is refused; a size still used by an element is refused before any write, naming the elements. | Each size present with its inner/outer/bend, or absent; every other size unchanged. |
| `resize` | yes | `element_ids` or `system_id` (piping or duct network), to `diameter` or `width`+`height`. Each value must be a size of that element's own catalog (the pipe's segment, the duct shape's list, the conduit type's standard, the cable-tray list). Runs already at that size are skipped and listed. | Each run's size parameters equal the request, and every connector connected before is still connected. The fittings Revit removed/replaced, retyped or inserted (transitions) are reported in `result`. |
| `size_by_flow` | no | Per pipe or round/rectangular duct: the smallest catalog size (`used_in_sizing`) whose free area carries the flow at or below `max_velocity` (m/s). Flow is the element's calculated flow, or `flow` (L/s) for all. Pipes use the segment's inner diameter; rectangular ducts hold the current `height` (or the one given) and pick the width. `resize_calls` groups the proposals into ready `resize` requests. | - |
| `route` | yes | `kind` (pipe/duct/conduit/cable_tray), `type_id`, `system_type_id` (pipe/duct), `level_id`, size (`diameter` for pipes, conduits and round ducts; `width`+`height` for rectangular/oval ducts and cable trays - a mismatch is refused before any write), `start`/`end` ([x,y,z]; points and sizes in `units`, default mm), `clearance_mm` (default 50), `grid_mm` (default 100, minimum 10), `max_nodes` (default 20000, maximum 200000), optional `preferred_elevation` ({min_mm, max_mm}; a missing side is unbounded). A pure 3-D orthogonal A* (`RouteSearch`, Revit-free, in `Core/RouteSearch.cs`) finds a polyline around every physical host element and every loaded-link element near the search box (a rotated link's boxes from all eight corners), inflated by `clearance_mm` plus half the run's OUTSIDE size (a pipe's outer diameter from its type's segment; `size_basis` says when the nominal was used). The end is snapped toward the start and its off-grid remainder absorbed into the last leg along that axis - no stub legs, never doubling back. Every leg keeps a minimum length (first/last: one outside size; between two bends: two) so each elbow has room; a path that cannot is `no_route` naming the short leg in mm. The search box (6 grid steps around start/end) grows to x3 and x9 before refusing, and the refusal says when the box, not `max_nodes`, was binding. The dry run declares the N segments and N-1 elbows it will create, and the token binds the rounded polyline: a route that changed by apply time is refused as stale. | Free ends (the route's own start/end) on their points (1 mm); at each bend, where the elbow trims both runs back, the elbow's nominal junction (intersection of its connector axes) on the planned vertex (1 mm), the physical end on the planned leg's axis pulled back toward the other end, and connected to that elbow; each segment's size re-read from the same parameters `resize` uses (1 mm; a size Revit snapped to its catalog fails, named), every elbow's connector count, and a `SpatialCoherence` check against every created element - any error against a physical host or link, or a check left partial (budget or cap hit, links not examined), throws and rolls back the whole route, named. |

| `hangers` | yes | `element_ids` of straight pipes, ducts, cable trays or conduits; `hanger_type_id` (a level-based generic model, pipe/duct accessory or specialty equipment type you supply; work-plane- and face-based families are refused before anything is placed); `spacing_mm` (maximum gap) and `end_offset_mm` (clearance from each run end and each tap fitting), both required; `max_rod_mm` (default 3000); `rod_length_parameter` (optional instance length parameter); `attach` = `structure_above`. Stations: each tap owns a clearance zone of `end_offset_mm` either side; the span between the end clearances is cut at those zones and every free segment gets a station at each edge plus an even split, so no gap inside it exceeds `spacing_mm`. End stations obey the tap clearance too (moved inward, `end_stations_moved_mm`). A gap that cannot be closed - across a zone wider than `spacing_mm`, or where a station has nothing above - is listed per run in `gaps_above_spacing` with its cause. Stations are counted before any ray: more than 1000 refuses. Each station casts a ray up from the run's top (centreline + half its outside height) against floors, structural framing and roofs of the host and of loaded links, in a temporary 3D view that is rolled back (no template, filters or section box; structure, links and open worksets visible; the run's phase with demolished hidden); closed worksets are listed, not searched. The nearest hit within `max_rod_mm` is the support (`source` host/linked, element id, link instance id) and its distance the rod length; `rod_length_parameter` must be an instance Length parameter. Stations with nothing above are listed as `no_support_above` and not placed; a ray that cannot be cast refuses the call; runs shorter than 2 x `end_offset_mm` get none. The instance goes on the run's centreline, turned to its horizontal direction. Risers are skipped and reported; only flex runs are refused with the Python fallback grant (wires and other curves are plain errors). | Each hanger's type, position (1 mm; Z from its level plus the offset that governs it), rotation (0.5 degree) and rod parameter (1 mm), and `count`: placed equals planned. |

Writes follow the bridge's two-step flow: the dry run (default) applies the change in
a transaction, verifies it, rolls it back and returns a `confirmation_token`; the apply
spends the token, re-verifies inside a TransactionGroup and rolls everything back on
any mismatch. Units are `mm` (default), `in` or `feet`; catalog sizes match at
1e-5 ft (about 0.003 mm).

**Limits.** `size_by_flow` claims velocity only: no friction, pressure drop or fluid
properties, because Revit's duct/pipe sizing dialog has no public API and a
friction-based size would be a guess. Oval ducts, conduits and cable trays are not
sized by flow. Flex pipes and flex ducts are outside `resize` and carry the Python
fallback grant. Moving a rule whose criterion is not a size range is refused rather
than dropping the criterion. The API members used (`RoutingPreferenceManager`,
`RoutingPreferenceRule`, `PrimarySizeCriterion`, `Segment`/`PipeSegment` sizes,
`DuctSizeSettings`, `ConduitSizeSettings`, `CableTraySizes`) read identically in the
2023-2027 API documentation, so there is no per-year branch.

**`route` example** (mm, apply after a `dry_run` returns a `confirmation_token`):

```json
{
  "operation": "route", "target_document": "...", "kind": "pipe",
  "type_id": 123456, "system_type_id": 234567, "level_id": 345678,
  "diameter": 100, "start": [0, 0, 3000], "end": [8000, 0, 3000],
  "clearance_mm": 75, "grid_mm": 150,
  "dry_run": false, "confirmation_token": "..."
}
```

The reply's `result` carries `length_mm`, `bends`, `polyline_mm`, `segment_ids` and
`elbow_ids`; a failed search returns `no_route: <reason> (blocking region: <name>)`
instead of a token, naming the obstacle that closed every path.

**`slope` (gravity pipe runs).** `element_ids` names the run's pipes (the fittings
joining them come along), or one seed pipe with `walk: true` follows the connected
network through pipe fittings (by category, OST_PipeFitting), up to 200 pipes plus
fittings. Accessories, fixtures, equipment and unnamed pipes are boundaries, never
walked through; their connections are listed as `external_connections` and must
still be connected afterwards, and a plan that would MOVE an end connected to one is
refused by name (hold that end, name the element, or disconnect it). A pipe with a
tap (a connection along its length) is refused. `slope_percent` is the grade (2.0 = 2%).
`fixed_end` holds one open end at its current elevation:

- `upstream` / `downstream` - only for a run with exactly two open ends, and only
  when a connector reports flow direction (a calculated system); otherwise refused.
  Readings that contradict each other (both ends In, or both Out) are refused.
- `<element_id>` - the pipe (or fitting) owning one open end. Direction comes from
  flow; when flow does not read (or contradicts the other end) it is refused - high
  and low give opposite slopes, so the bridge does not guess.
- `<element_id>:high` / `<element_id>:low` - the caller states whether the held end
  is the upstream end or the outlet. A branched run held at a high end needs flow
  to name the one outlet; otherwise hold the outlet with `:low`.

Every point sits slope x its horizontal path length to the outlet above the outlet,
so a tee's branch drains toward the main whichever end is held. Fittings move as
rigid bodies (their legs keep their rise), so pipes carry the whole drop at exactly
the target slope. Pipes steeper than 45 degrees are risers and keep their rise
(`risers_kept`). `min_clearance` (in `units`) refuses the run, naming the point, if
any point would land below the highest level at or below the held end plus that
clearance; without it, a point the re-grade would drag from above that level to
below it is refused. The rehearsal shows the held end, the outlet, target
elevations of every pipe end and fitting centre. Apply moves each fitting
vertically by its target minus its centre re-read at that moment, sets each pipe's
LocationCurve, regenerates, and reconnects a connector pair that separated but still
coincides (listed in `reconnected`); then it re-reads, in `units`, the held end,
every pipe end against its planned elevation, every pipe's signed slope (outlet
side lower, within 0.05 percentage points), every fitting centre, `min_clearance`,
every connector pair recorded before and every fitting's connected count, and rolls
back naming the element otherwise. The confirmation token binds every pipe's and
fitting's geometry and connections: a change between dry run and apply is
`stale_plan`. Revit has no pipe slope API (no `SlopeType` for pipes;
`PipeSettings.GetPipeSlopes` only lists preset slopes). AutoRouteFailures has an
`AttemptToConnectNonSlopingElementToSlopedPipeWarning` and an `...Error`; the
Warning does not fail a commit, so a separated fitting is caught only by the
post-commit connector re-read, and how elbows follow moved ends is measured by the
`pipe-slope` live probe (connector origins).

**Resumen (español).** `slope` da pendiente a una red de tubería por gravedad desde
un extremo fijo (`fixed_end`), con los accesorios siguiendo a los tubos; cada punto
queda a pendiente x distancia horizontal hasta la salida, un ramal de tee drena hacia
la principal, y la aplicación relee pendientes y conexiones o revierte nombrando el
elemento.

**Resumen (español).** `horizun_mep_routing` lee y edita las preferencias de
enrutamiento (reglas por grupo con rangos de tamaño, unión preferida), los segmentos
de tubería y los catálogos de tamaños de ducto, conduit y bandeja; cambia el tamaño de
tubos, ductos, conduits y bandejas solo a tamaños de su propio catálogo, verifica
releyendo tras el commit y reporta los accesorios que Revit reemplazó o insertó; y
propone tamaños por caudal con un límite de velocidad (solo velocidad, sin fricción:
el dimensionamiento de Revit no tiene API pública) sin escribir nada. `route` traza
una ruta ortogonal en 3D que evita obstáculos (elementos físicos y de vínculos
cargados, con el margen de `clearance_mm`) entre dos puntos, crea los tramos y codos,
y solo confirma si la relectura de extremos/conexiones y el chequeo espacial salen
limpios; si no hay camino dentro de `max_nodes` se niega nombrando la zona que bloquea.

el dimensionamiento de Revit no tiene API pública) sin escribir nada. `hangers` coloca un tipo de soporte que da quien llama (modelo genérico,
accesorio o equipo especial, no alojado) en estaciones a `end_offset_mm` de cada extremo y
cada derivación y a no más de `spacing_mm` entre sí (repone estaciones en el borde de la
zona de cada derivación; el hueco que no se puede cerrar lo nombra en `gaps_above_spacing`);
mide con un rayo hacia arriba, en una vista 3D temporal que se revierte, la losa, viga o
cubierta más cercana (del modelo o de un vínculo cargado) dentro de `max_rod_mm`, escribe
la longitud de varilla si se nombra un parámetro de longitud, y no coloca las estaciones sin
nada encima: las reporta como `no_support_above`. Los montantes verticales se omiten y se reportan.



## Styles, units and electrical

Three multi-operation tools share one write ritual (`Commands/VerifiedModelEdit.cs`):
`dry_run` (the default) applies the edit inside a transaction, regenerates, re-reads
every requested property through a `PostconditionCheck` and rolls back, reporting
Revit's rollback status and a single-use `confirmation_token` bound to the measured
"before" state. The apply spends the token, runs inside a `TransactionGroup`,
re-reads before and after the inner commit and only assimilates a fully verified
checklist; anything else rolls the group back. The reply publishes the checklist
re-read from the committed model (`postconditions`) and an `application` block.
Read operations need no token and accept an optional `target_document` guard.

### `horizun_manage_styles`

| operation | what it does |
|---|---|
| `list_object_styles` | Without `category`: top-level model and annotation categories. With `category` (OST_ name, id or name): that category and its subcategories. Rows: projection/cut weight, colour, projection line pattern, material, `graphics_style_id`. |
| `set_object_style` | `category` (+ optional `subcategory`) and any of `projection_weight`, `cut_weight` (cuttable categories only), `color` (#RRGGBB), `line_pattern` (name or `Solid`), `material` (exact name). |
| `create_subcategory` | `category` (parent) + `name`, with the same optional style fields. Refused when the parent does not allow subcategories or the name exists. |
| `list_line_styles` / `create_line_style` | The same, fixed to the Lines category (a line style IS a Lines subcategory). |
| `list_line_patterns` / `create_line_pattern` | `name` + `segments` `[{type: dash|space|dot, length}]` in `units` (mm default); re-read segment by segment. |
| `list_fill_patterns` / `create_fill_pattern` | `name`, `target` drafting/model, `fill` solid/hatch/crosshatch, `angle` (degrees), `spacing`, `spacing2`; re-read solid flag, grid count, first grid angle and spacing. |

Nothing here deletes a style. Subcategories, line patterns and fill patterns are
removed with `horizun_delete_verified` on the published ids; built-in categories
cannot be deleted.

### `horizun_manage_units`

| operation | what it does |
|---|---|
| `read` | FormatOptions per spec: unit, accuracy, symbol, zero/space suppression, digit grouping; plus the document's decimal and grouping symbols. Default: length, area, volume, angle, slope, air flow, pipe flow, current, potential, power. `specs=[...]` or `all=true` for more. Specs are resolved against the running Revit by short name (`length`), unversioned id or full ForgeTypeId, because spec versions differ between years. |
| `set` | `spec` with any of `unit`, `accuracy`, `symbol` (`none` clears it), `suppress_*`, `use_digit_grouping`; and/or `decimal_symbol`, `digit_grouping_symbol`. Invalid accuracy/unit/symbol is refused naming the valid ones. |
| `project_information` | Without `values`: the named fields (name, number, client, address, status, issue_date, author, building_name, organization_name, organization_description) and every Project Information parameter. With `values`: text and integer parameters written by field or parameter name, each re-read. Other storage types go through `horizun_write_params_verified`. |
| `base_points` | Without `project_position`: Project Base Point and Survey Point (internal and shared positions, pinned) and the project position at the base point (E/W, N/S, elevation, angle to true north). With `project_position` (`east_west`, `north_south`, `elevation` in `units`, `angle_to_true_north` in degrees) **and** `confirm_shared_coordinates=true`: `ProjectLocation.SetProjectPosition` at the project base point. |

**Risk of `base_points` writes.** Re-specifying shared coordinates moves the model
against everything positioned by them: linked models placed by shared coordinates,
coordinate-based IFC/DWG/NWC exports and survey ties. In a workshared model it
needs ownership of the shared coordinates. Only the project coordinator should
decide it; the extra `confirm_shared_coordinates` flag exists so a token alone
cannot do it.

### `horizun_electrical`

| operation | what it does |
|---|---|
| `list_panels` | Electrical equipment: panel name, family/type, distribution system and its voltage, circuits fed, summed apparent load (VA), Revit's total connected load and demand current strings. |
| `list_circuits` | Every `ElectricalSystem` (optionally `panel_id`): type, panel, number, load name, member ids, apparent (VA) and true (W) load, voltage, poles, length (m), voltage drop (V up to 2025; Revit's own parameter string on 2026–2027, where the API property was deprecated and then removed). |
| `create_circuit` | `element_ids` (MEP family instances), `system_type` (default PowerCircuit), optional `panel_id`. Re-reads members, type and panel. |
| `assign_panel` | `circuit_id` + `panel_id` via `SelectPanel`; re-reads the base equipment. |
| `add_to_circuit` / `remove_from_circuit` | `circuit_id` + `element_ids`; re-reads the member set. Emptying a circuit is refused (delete it instead). |
| `panel_schedule` | `panel_id` (+ optional `template_id`): `PanelScheduleView.CreateInstanceView`; re-reads the view's panel. Refused when the panel already has one. |

Voltage, poles and distribution-system compatibility are Revit's decisions: the
rehearsal performs the real call, so a refusal returns Revit's message with nothing
committed.

### Resumen (español)

Tres herramientas multi-operación con el mismo rito de escritura: ensayo en una
transacción que se revierte (dry_run por defecto), token de un solo uso, aplicación
dentro de un TransactionGroup y relectura de cada valor pedido tras el commit; si
algo no se verifica, el grupo se revierte. `horizun_manage_styles` lista y edita
estilos de objeto, crea subcategorías, estilos de línea, patrones de línea y de
relleno (no borra: eso es `horizun_delete_verified`). `horizun_manage_units` lee y
cambia el formato de unidades por spec (ForgeTypeId, 2023–2027), lee/escribe
Información de proyecto y lee los puntos base; escribir coordenadas compartidas
exige además `confirm_shared_coordinates=true`, porque mueve el modelo respecto de
vínculos, exportaciones y topografía. `horizun_electrical` lista tableros y
circuitos, crea circuitos, asigna tablero, agrega/quita elementos y crea la tabla
de tablero.



## Curtain grids, railings, slab shape and arrays

All four follow the same discipline: dry run by default, a single-use
`confirmation_token` for the apply, the committed model re-read through a
`PostconditionCheck`, and the whole edit rolled back when it disagrees. Units are
`mm` unless `units` says otherwise.

### `horizun_manage_curtain`

`element_id` is a curtain wall or a curtain system (`grid_index` picks the system's grid).

| operation | arguments | re-read after commit |
|---|---|---|
| `read` | – | u/v lines (ends, `offset` on walls, segments, mullions on each, lock/pin), mullions (type, the grid line they sit on by geometry), panels (type, category, `is_door`, centre), `base_z` |
| `add_grid_line` | `direction` u\|v, `offset` (walls: v = along the location line from its start, u = above the base) **or** `point` | exactly one new line of that direction, at the offset within 0.5 mm (or through the point) |
| `remove_grid_line` | `grid_line_id` | the line is gone and the count fell by one |
| `set_mullions` | `grid_line_id`, `mode` add\|remove, `mullion_type_id` (add), `segment_index` (else every segment) | each targeted segment carries a mullion of that type / none |
| `set_panel_type` | `panel_ids` **or** `point`, `type_id` (panel, curtain-wall door/window, or wall type) | the panel's type, including when Revit replaces the element (new id reported) |

Revit keeps no link between a mullion and its grid line: membership is geometric
(within 1 mm). A segment that already carries a mullion is refused for `add`
(Revit would silently leave it); change its type with `horizun_transform_elements`
`change_type`. Grid lines and panels governed by the wall type's layout may be
refused by Revit; the refusal and the rollback are reported.

### `horizun_slab_shape`

`element_id` is a floor or a roof. `read` lists vertices `[x, y, z, type]`, creases
and the slab top. `add_point` takes `points: [[x, y, offset]]`; `modify_subelement`
takes `points` for existing vertices, or `start`/`end` (`[x, y]`) plus `offset` for
one crease; `add_split_line` takes `start`/`end` and adds a missing interior end
point first; `reset_shape` erases the shape points.

The API changed between years, measured from each year's `RevitAPI.xml`:
2023 has only the `SlabShapeEditor` property and `DrawPoint`/`DrawSplitLine`;
2024 adds `GetSlabShapeEditor()`; 2025 adds `AddPoint`/`AddSplitLine`; 2026–2027
drop the property and the `Draw*` calls. The command compiles the right call per year.
`SlabShapeVertex.Position.Z` is not documented as absolute or relative to the slab
top, so both readings are measured and the one that held is returned in
`evidence.z_convention`.

### `horizun_create_railing`

Either `host_id` (a stair or ramp; `placement` treads\|stringer; Revit creates one
railing per side it decides, all ids are returned and each host is re-read) or `path`
(an open polyline of XYZ points on `level_id`, checked with
`Railing.IsValidPathForRailing`; `GetPath()` is compared segment by segment in plan).
`base_offset` sets `STAIRS_RAILING_HEIGHT_OFFSET`. The height is the type's and is
reported as `type_height`.

### Arrays in `horizun_transform_elements`

`array_linear` (`vector`) and `array_radial` (`axis_start`, `axis_end`,
`angle_degrees`) with `count` (members including the original: linear 2–200,
radial 3–200), `anchor` second\|last and `group` (default false: copies are free
elements; true keeps Revit's associated array, whose id is returned). The array is
created in the active view. Member k of each source is expected at k × step:
anchor=second uses the vector/angle as the step, anchor=last splits it over
count − 1 (a full 360° turn over count). Every copy is matched against that formula
within 1e-5 ft; grouped members are judged by the elements inside their groups.
A radial copy's position is checked, not its axes.

### Resumen (español)

`horizun_manage_curtain` lee y edita rejillas de muro cortina (líneas U/V con
offset medido sobre el muro, montantes por segmento, tipo de panel incluidas
puertas de muro cortina). `horizun_slab_shape` edita la forma de losas y cubiertas
con guardas por año de la API (2023 propiedad + `DrawPoint`; 2025+ `AddPoint`;
2026+ solo `GetSlabShapeEditor`) y relee la elevación de cada vértice, informando
qué lectura de `Position.Z` se cumplió. `horizun_create_railing` crea barandas sobre
escalera/rampa o por boceto y relee tipo, anfitrión y trayecto.
`horizun_transform_elements` suma `array_linear` y `array_radial`, con o sin
asociación, y verifica cada copia contra la fórmula. Todo en ensayo por defecto,
con token y rollback si la relectura no coincide.



## Clash resolution and batch undo

### horizun_resolve_clash

Detection is common; resolving a clash **with a verification** is the point of this tool.
It works on findings of the ledger that `horizun_clash record_findings=true` maintains
(see `horizun_coordination list`).

**propose** (read-only, default). For each `finding_ids` entry:

1. Roles. Pipes, ducts, conduits, cable trays and flex runs are *movable*. Structure
   (structural framing/columns/foundations, structural walls and floors) and
   architecture are never moved: the row is `report_only` with
   `report_only_no_movable_side`. When both sides are runs, the smaller cross section
   moves; equal sections are `ambiguous_both_movable` (a design decision).
2. Safety and scope. A run in a linked model (`linked_element_not_writable`) or a
   pinned run (`pinned`) is reported, not moved. A run with any connected connector
   is no longer refused outright: its whole connected network (pipes/ducts/conduits/
   cable trays and their fittings/accessories, walked through live connectors, capped
   at 60 elements) is collected and re-checked fresh every time - propose AND apply -
   never trusted from an earlier call. It moves as one rigid body (`mode: "run_shift"`)
   only when EVERY member is a host element, unpinned and outside any group, AND every
   connector at the network's boundary is open (not connected to equipment, fixtures or
   terminals outside the set); otherwise it stays report-only, naming what blocks it:
   `network_exceeds_cap`, `member_in_group`, `boundary_connector_blocked` (with the
   blocking element), or `linked_element_not_writable`/`pinned` for a bad member.
3. Geometry (`Core/ClashResolveRules.cs`). The mover's centreline and exact section are
   compared with the fixed element's bounding box projected on each escape direction:
   `shift` perpendicular to the run in plan, and `elevation` (vertical) for horizontal
   runs only. The run's own axis is never an escape. Distances are whole millimetres,
   rounded away from the clash, and include `clearance_mm` (default 50). Candidates over
   `max_move_mm` (default 600) are rejected.
4. Third elements, HOST AND LINKS. A candidate whose moved box would reach any other
   host model element is rejected (`would_touch_other_elements`, `contacts`); the same
   box, carried into the coordinates of every LOADED Revit link (the link instance's
   total transform, inverted), is checked against that link's elements too
   (`link_contacts`). For a run_shift EVERY member's box is checked, not just the
   mover's - a fitting far from the clash that would land on something is caught too.
   An unloaded link is named in `links_skipped` - it is never silently counted as
   clear. If no candidate survives, the finding is report-only.

Each proposal carries `kind`, `mode` (`single` or `run_shift`), `distance_mm`,
`affected_elements` (every member for a run_shift), a `prediction`, and the ready
`next_arguments` for apply.

**apply**. `dry_run` defaults to true and issues a token bound to the proposals; the
`element_id`/`vector_mm` of each proposal is re-resolved and re-validated fresh against
the LIVE model (pinned/connected/network eligibility all re-derived, never trusted from
propose - a network that changed since propose is refused, not moved on stale
information). Inside one TransactionGroup: the neighbourhood (every host model element
whose box meets the swept region of every MEMBER, grown by the clearance) is measured on
solids BEFORE the move; every member of every move is moved together in one
`ElementTransformUtils.MoveElements` call (a run_shift moves rigidly, never one segment at
a time); positions are re-read (1 mm tolerance); the same neighbourhood is measured AFTER -
AND, in both passes, every member's solid is carried into every loaded link's coordinates
and intersected there too (`ElementIntersectsSolidFilter` + `BooleanOperationsUtils`,
exactly like `SpatialCoherence.AgainstLinks`; the pair key is
`link:<name>:<hostId>~<linkElementId>`, distinct from a host-host pair). For a run_shift,
every internal connector pair recorded at propose time is also re-read
(`connections:<idA>-<idB>`) - a rigid translation should never drop one, and the contract
re-reads rather than assumes. The group is kept only when every position, every internal
connection, the targeted pair, and "no new clash" (host OR link) all measure clean.
Anything else - including a boolean that failed, so the result is unmeasured - rolls the
whole group back and returns `new_clashes` (naming the link when that is what grew back)
and the `postconditions` checklist. Two proposals whose networks share a member are
refused as a batch, before anything moves (moving one would silently disturb the other).
An unloaded link is listed in `links_skipped` on every reply (propose and apply) and never
treated as measured-clear, but it does not by itself block an apply whose host-side pairs
are otherwise clean - the same convention `SpatialCoherence.AgainstLinks` already uses. A
kept apply records an undo batch covering EVERY member (not just the primary mover, so
`horizun_undo` moves the whole network back together) and marks each finding
`resolved_by_model` with the measurement written into its history.

**propose_opening / apply_opening** (sleeves and structural openings). For a clash that
moving cannot fix - an MEP run (pipe, duct, conduit, cable tray, flex run) through a
wall, floor, roof, ceiling, structural framing or column - the run keeps its line and
the HOST gets an opening, or a sleeve. Pure geometry lives in `Core/SleeveRules.cs`.

- **propose_opening** (read-only). Both sides must be host elements (a linked side is
  `linked_element_not_writable`), exactly one an MEP run. The crossing is where the run's
  centreline passes through the host's SOLID (`Solid.IntersectWithCurve`, segments
  inside; entry, exit, `crossing_point_mm` = their midpoint); the host's bounding box is
  only a prefilter, so a rotated wall or a sloped roof gets the real crossing. A run that
  no longer enters the solid (it already passes through an opening) is
  `run_does_not_cross_host`. The size uses the run's OUTER section (`outer_section`): the
  outside diameter (`RBS_PIPE_OUTER_DIAMETER` / conduit outer diameter) when larger than
  the connector's nominal size, plus twice the thickest insulation; a rectangular/oval
  section is squared to its larger side on the connector's axes (unreadable axes: sized
  for every rotation). That section is projected through the host's MEASURED thickness
  along the run, so a skewed crossing is sized `section/cos + thickness*tan`
  (`SleeveRules.FootprintHalfExtent`), then `clearance_mm` (default 50, half per side) is
  added. Routes by host kind: wall -> `wall_opening` (`NewOpening(wall, pt1, pt2)`, always
  rectangular, straight walls only - a curved wall is `curved_wall_not_supported`, a run
  steeper than |z| 0.85 is `run_too_steep_for_wall_opening`); floor/roof/ceiling ->
  `floor_opening` (`NewOpening(host, CurveArray, false)`: a VERTICAL cut, circular for a
  vertical round run; a run flatter than |z| 0.5 is `run_too_flat_for_floor_opening`);
  framing/column -> `sleeve_only` with `cut_refused.code = member_cut_not_offered`. The API
  CAN cut a beam, brace or column (`NewOpening(member, profile, eRefFace)`); this operation
  deliberately does not, because a structural member's penetration is an engineer's sized
  decision - only a sleeve family is placed. A STRUCTURAL wall/floor
  (`WALL_STRUCTURAL_SIGNIFICANT` / `FLOOR_PARAM_IS_STRUCTURAL`) is refused as
  `structural_host_requires_opt_in` unless `allow_structural=true` records that a person
  approved the cut; the same flag is required to place a sleeve on any structural host,
  since a sleeve's void may cut it. Any other host category is `host_kind_not_supported`.
- **apply_opening**. `dry_run` (default) -> `confirmation_token` -> one TransactionGroup.
  Each proposal carries its `crossing_point_mm`, `opening_width_mm`,
  `opening_height_mm` and `shape`, so the token binds the geometry the person saw; apply
  re-derives everything from the live model and refuses a drift beyond 1 mm as
  `geometry_changed_since_propose`. Before writing, the run must still meet the host on
  solids (`nothing_to_cut` otherwise - a second pass over a finding that stays open never
  stacks a second opening). Without `sleeve_type_id` a wall/floor/roof/ceiling is cut; a
  framing/column proposal is refused by name. With `sleeve_type_id` (a family type the
  caller loaded - nothing is compiled in) the family is placed by its own placement type:
  hosted on the host at the crossing; face-based on the host face the run enters (an
  instance host such as a steel beam is walked through `GetSymbolGeometry()` with its
  transform, because instance-geometry references cannot host new elements); level-based
  (`OneLevelBased`, e.g. a generic model) with `NewFamilyInstance(XYZ, symbol, Level, ...)`
  on the run's reference level; line-based along entry->exit; anything else at the
  crossing point. Point placements are rotated about Z to a horizontal run.
  `approval_parameter` + `approval_value` write a text mark (e.g. `pending structural
  approval`) on the created element; a missing, read-only or non-text parameter rolls
  everything back.
- **Postconditions**, re-read inside the group before it is kept: `created:<finding>`;
  `clearance:<finding>` - a clearance envelope (the run's outer section grown by
  `clearance_mm/2`, less 0.5 mm tolerance, from before the entry to past the exit) is
  intersected with the cut host's solid and the sleeve's own solid and must meet no
  material; nothing measurable (an uncut, void-only sleeve) is unreadable and rolls back;
  `host_cleared:<finding>` (openings) - a solid re-detection must find the run no longer
  meeting the host; `host_cut:<finding>` (sleeves) - MEASURED the same way, never assumed,
  so a hosted sleeve whose void cuts on hosting reports `host_cut: true`;
  `level:<finding>` and `at_crossing:<finding>` for a level-based or point-placed sleeve;
  `approval:<finding>`; and `no_new_clash` around the crossing (the sleeve inside its own
  host is the design, not a new clash; the sleeve against the run, a third element or a
  loaded link is; an unreadable sleeve geometry makes detection incomplete). Any failure
  rolls the whole group back and reports the rollback status read from Revit. A kept
  apply records an undo batch (`created`) for `horizun_undo`, and the finding gets an
  `opening_requested` history entry (with the measured host_cut) but STAYS OPEN: only a
  later `horizun_clash record_findings=true` measurement can resolve it.
- To measure live: the orientation of a point-placed sleeve depends on how its family
  was modelled (caught by the clearance envelope, not predicted); whether a rectangular
  duct connector's `CoordinateSystem` BasisX/BasisY are its section axes (the opening is
  squared to the larger side either way); face-based placement on a sloped or curved host
  face is not built (it fails with the reason).
- Live probe: `scripts/live-probes/sleeves-openings.probes.ps1` (offline test
  `sleeves-openings.tests.ps1`) stages an own level with a pipe through a wall, a vertical
  pipe through a floor and a vertical pipe through a beam; it applies the wall and floor
  cuts (with `allow_structural=true` as the fixture's recorded approval), reads the finding
  back as still `open` with its `opening_requested` entry, checks the beam refusal by name
  in propose and in the apply rehearsal, deletes everything and closes the probe's own
  findings (`closed_by_decision`). The sleeve family path is not staged (no sleeve family
  ships with the fixtures).

### horizun_undo

Revit exposes no Undo through its API. After a verified commit,
`horizun_transform_elements` (move, copy, rotate, mirror, pin/unpin, change_type,
set_curve on a line, move_tag_head), `horizun_write_params_verified`,
`horizun_create_elements` and `horizun_resolve_clash` record an inverse in
`<data root>/undo/<document hash>.json` (last 20 batches) and report it as an `undo`
block. Operations without an inverse (set_tag_leader, wall_join, a non-line set_curve,
a parameter whose previous value was unreadable) record the batch as **not undoable** by
name, so `undo_last` never skips past it to an older batch.

- `list` - the document's batches, newest first, and whether `undo_last` is available.
- `undo_last` - dry run first. Refused when the document was saved or synchronized since
  the batch (its VersionGUID/NumberOfSaves stamp moved), or when any element the batch
  touched no longer carries the state the batch left (location, type, pin, orientation,
  tag head, or the parameter value). Otherwise every inverse runs in one transaction in
  reverse order (created -> deleted, parameters -> previous values, moves -> the opposite
  vector, rotations -> the opposite angle, mirrors -> the same plane); a delete that would
  take elements the batch did not create rolls back; every element is re-read against
  the state the batch found, in a `PostconditionCheck`.

### Resumen (español)

`horizun_resolve_clash propose` convierte hallazgos abiertos del ledger en correcciones
candidatas conservadoras: solo mueve tramos MEP del modelo anfitrión, sin conexiones ni
pin, el mínimo más la holgura (desplazamiento perpendicular o cambio de elevación);
estructura, arquitectura, vínculos, tramos conectados o movimientos que tocarían a un
tercero quedan como "solo informar". `apply` mueve dentro de un TransactionGroup y
vuelve a detectar sobre sólidos: el par debe desaparecer sin clashes nuevos o se revierte
todo; solo entonces el hallazgo pasa a `resolved_by_model`. `horizun_undo` deshace el
último lote Horizun registrado, y se niega si el documento se guardó o sincronizó desde
entonces o si esos elementos cambiaron.

### Handoff from naviscoord-mcp (Navisworks coordination)

naviscoord-mcp's `navis_handoff` writes `coordination_handoff.json` (schema
`naviscoord.coordination/1`) and `revit_worklist.json` (schema
`naviscoord.coordination.worklist/1`) after a Navisworks clash analysis. The Revit
side of that handoff is two `horizun_coordination` operations plus the existing
`horizun_resolve_clash`/`horizun_verify_changes` - no new tool, because the ledger
they read and write is the same one `horizun_clash record_findings=true` already
maintains:

```
navis_handoff (Navisworks)
   -> coordination_handoff.json
   -> horizun_coordination operation=import_navisworks   (RE-DETECTS, then records)
   -> horizun_resolve_clash propose / apply               (moves the responsible side)
      -or- horizun_coordination operation=show             (paints the pair in a persistent view)
   -> horizun_verify_changes                               (picture + spatial check of the result)
   -> re-run the clash test in Navisworks
```

**`operation=import_navisworks`** never takes Navisworks' word for a clash. Every
issue's `targets` (each an `{side, revit_element_id, source_file, discipline, ...}`)
are matched against the ACTIVE document and every LOADED rvt link by `source_file`,
normalized (extension and NWC/export/copy decoration stripped, case-insensitive -
`Core/NavisworksHandoff.cs`), then the pair is **RE-DETECTED here**: a solid
intersection (volume reported), or a measured bounding-box gap when it does not
reproduce. Only a REPRODUCED pair becomes or refreshes a ledger finding, tagged
`external_source=navisworks` with the issue's `priority`, `responsible`,
`immovable_side` (resolved to the ledger's A/B letter from the issue's own
`side_a_discipline`/`side_b_discipline`) and `suggested_action` carried along - and
`runComplete` is always false, so an import never marks anything `resolved_by_model`
by itself; that stays a measured, complete `horizun_clash` run's job, exactly as
`CoordinationRules.Merge` already enforces for every other source. `revit_worklist.json`
keeps only the responsible side per issue (naviscoord's own filter), so it carries no
fixed-side element to pair against; imported anyway, every one of its issues reports
`not_traceable` naming that reason rather than inventing a one-sided finding. Reports
`issues_total`/`traceable`/`matched`/`reproduced`/`not_reproduced`/`not_traceable`
(the last covering both "no Element Id" and "Element Id resolves in no loaded
document"), dry run by default.

**`horizun_resolve_clash`** needs no change to accept these findings: `propose` reads
the imported `immovable_side` off the finding and enforces it ON TOP of its own
natural choice (`Core/ClashResolveRules.EnforceImmovableSide`) - unchanged when the
natural mover already respects it, inverted to the other side when that side is
itself a movable host run, and refused (`immovable_side_blocks_resolution`) when the
marked-immovable side is the only one that could move. `apply` re-detects on solids
exactly as it does for a `horizun_clash`-detected finding.

**`operation=show`** creates a PERSISTENT 3D view (section box around the selected
findings; the side that must move painted red, the immovable side orange, an unknown
preference blue) so a coordinator can see what a proposal is about to move without
leaving Revit. This is the one `horizun_coordination` operation that writes the
model - `dry_run` -> token -> apply, verified by re-reading the view, its section box
and a bounded sample of the overrides - the ledger file itself stays untouched by it.
A finding side that lives in a link is painted at the LINK INSTANCE level (Revit has
no per-element override across a link boundary) and `link_level_overrides` says so.

**Measured end to end on 2026-09-26** (Navisworks Manage 2026 + Revit 2026, one pipe
Ø150 through one 457x475 column): Navisworks found the clash (112 mm penetration),
`navis_handoff` named Revit ids 1352627/1352136, `import_navisworks` reproduced it
here (8.66 L shared), `show` painted it, `resolve_clash` moved the pipe 363 mm with the
column kept immovable, re-detection on solids cleared the pair with no new clash, and
the SAME Navisworks test re-run against the saved `.rvt` (through the `.nwf` that keeps
the sets and tests) reported the result Resolved - 0 active issues. Two traps it cost:

- **Detail level decides what Navisworks sees.** Navisworks reads a `.rvt` through its
  3D view; at Coarse a pipe is a single LINE (1 primitive) and a Hard test between
  solids reports **zero** clashes against it. The same pipe at Fine was 1587 triangles
  and clashed. Before a model goes to Navisworks, its 3D view (`{3D}`, or the one named
  `Navisworks`) must be Fine - otherwise "0 clashes" means "MEP was never tested".
- **A view camera is not an obstacle.** Revit files every 3D view's camera under the
  MODEL category `OST_Cameras`, with a bounding box the size of the view. The resolver
  counted three of them as elements a move "would touch" and refused every lateral
  shift; cameras, viewers, section boxes and MEP system elements are now outside what
  both the spatial check and the resolver treat as physical.
- **Navisworks reads a view named with "Navisworks" before `{3D}`.** With `{3D}` at
  Fine and a 3D view called "HZ Navisworks b3b1d996" at Medium, both pipes arrived as
  lines again and the Hard test found nothing. Renaming only that view brought them
  back as solids (1587 and 1787 triangles). `show` named its own view
  "Horizun - Navisworks <date>" by default, so the product planted the view that
  spoiled the next export. Now:
  - `show` defaults to "Horizun - Coordination <date>" and refuses any `view_name`
    containing "Navisworks";
  - `navisworks_readiness` judges every 3D view whose name contains "Navisworks"
    (listed in `candidate_views`);
  - `prepare_navisworks` sets every one of those views to Fine.

**The loop, closed without re-running Navisworks (measured the same day).** Second
round on the same model:
1. `navisworks_readiness` caught the Medium view (`not_ready`, naming it).
2. `prepare_navisworks` set it to Fine and re-read it.
3. A new pipe through the column was detected in Navisworks (76 mm), handed off,
   imported (reproduced), shown in "Horizun - Coordination <date>", and moved 338 mm
   by `resolve_clash`.
4. `navisworks_status` suggested `resolved` for it.
5. `navis_set_status` applied that suggestion in Navisworks, verified by re-reading
   the document. The analysis then held zero active issues.

The status travels from Revit's measured verdict back to the coordination model;
nobody types it.

#### Resumen (español)

`navis_handoff` de naviscoord-mcp escribe `coordination_handoff.json`;
`horizun_coordination operation=import_navisworks` empareja cada elemento por
`source_file` normalizado contra el documento activo y los vínculos cargados, y
**vuelve a medir el choque aquí mismo** (intersección de sólidos, o la distancia
medida si no se reproduce) antes de tocar el ledger - solo un par REPRODUCIDO entra
o se refresca como hallazgo, con origen `navisworks` y `runComplete=false` siempre,
así que nunca resuelve un hallazgo por sí solo. `horizun_resolve_clash` no cambió:
ya respeta el `immovable_side` importado (invierte el lado que mueve o se niega si
el lado inmóvil es el único que podría moverse) sobre su propia elección natural.
`horizun_coordination operation=show` crea una vista 3D persistente con el lado que
debe moverse en rojo y el inmóvil en naranja, para mirar antes de aplicar; es la
única operación de `horizun_coordination` que escribe el modelo, con el mismo
dry_run -> token -> apply -> verificación releída del resto del producto.

Probado de punta a punta el 2026-09-26 (Navisworks 2026 + Revit 2026): choque
detectado en Navisworks, reproducido en Revit, tubería movida 363 mm con la columna
inmóvil, y el mismo test de Navisworks lo marcó Resuelto al re-correrlo. Ojo: la vista
3D que Navisworks lee debe estar en detalle **Fino**; en Grueso la tubería llega como
una línea y el test Hard reporta cero choques contra ella.

### BCF import from any tool, and Navisworks readiness

`horizun_coordination operation=import` used to stop at "unmatched" for any topic
that was not this ledger's own export (matched by the guid `BcfTopicGuid` mints for
each finding). A `.bcfzip` a coordinator actually gets back - from Navisworks, ACC,
Solibri, BIMcollab - names its clash by the topic's viewpoint **Components**
(`IfcGuid` and/or `AuthoringToolId`), never by that internal guid. Those topics are
now resolved and re-detected exactly like `operation=import_navisworks` does beside
it, instead of merely being reported:

```
somebody's coordination tool
   -> a .bcfzip (BCF 2.1 or 3.0 - both viewpoint shapes are read, Core/BcfMarkupReader.cs)
   -> horizun_coordination operation=import
        - topic matches THIS ledger's own guid  -> status/comments only (unchanged)
        - topic from ANY OTHER tool             -> resolve Components, RE-DETECT, fold in
   -> only a REPRODUCED pair becomes a finding (origin `bcf`, runComplete=false always)
```

**Resolving a Component**, in `Core/CoordinationImportBcfExternal.cs`, two
independent paths tried in this order:

- **`AuthoringToolId`** - tried as a Revit Element Id (an integer), then as a Revit
  UniqueId (`Document.GetElement(string)`), against the active document and every
  LOADED link.
- **`IfcGuid`** - the compressed 22-character IFC GlobalId. First the `IFC_GUID`
  parameter (a native `ElementParameterFilter`, so this never walks the model to find
  it); when nothing was ever stored there, Revit's own **computed** default -
  `ExportUtils.GetExportId(doc, id)` - is what a fresh export actually writes, so the
  incoming compressed guid is decoded back to that same .NET `Guid` (`Core/IfcGuidCodec.cs`,
  a Revit-free port of the buildingSMART IFC2x3 compression algorithm) and matched
  against an index built once per document per import, capped defensively - a model
  too large to scan in one call falls back to the parameter path alone rather than
  hanging.

A topic resolving fewer than two DISTINCT elements is reported `external_not_traceable`
with the reason, never invented; one that resolves two but does not reproduce lands in
`external_not_reproduced`. A reproduced pair's row in `external_reproduced` carries the
topic's `topic_guid`/`title`/`priority`/`assigned_to`/`status`, and its status/AssignedTo/
comments are folded onto the (possibly brand-new) finding through the SAME
conflict-aware logic a matched topic already uses.

**`operation=navisworks_readiness`** (read-only) finds the 3D view Navisworks' own
Revit exporter reads - one named exactly `Navisworks`, else the default `{3D}` - and
reports its detail level, section box, visual phase, and every MODEL category **with
elements in the model** that is hidden in it. Verdict `not_ready` when detail level is
not Fine (see the measured Coarse-vs-Fine trap two sections up - it is the same one)
or when an MEP category with elements is hidden (it will never reach Navisworks at
all, regardless of detail level). **`operation=prepare_navisworks`** is the fix -
`dry_run` -> token -> apply - setting that view's detail level to Fine and, only with
`unhide=true`, unhiding the named categories; `View.CanCategoryBeHidden` is checked
BEFORE ever opening a transaction (a view template governing category visibility, or
a dependent view, refuses there), and `DetailLevel`/`GetCategoryHidden` are re-read
after commit to decide success - never the calls not throwing.

**`operation=navisworks_status`** (read-only) reports, for every ledger finding with
`external_source=navisworks`, its own `revit_status` and `resolved_by_model_at` plus
a `navis_set_status` suggestion list (`resolved` only when a complete detection run
measured the pair gone, `active` otherwise - including a human `closed_by_decision`
or `accepted_risk`, which is a decision, not a measurement) ready to feed to
naviscoord-mcp's `navis_set_status`; `path`+`overwrite` optionally write it to a JSON
file, re-read and row-count verified. It writes nothing to Navisworks itself.

#### Resumen (español)

`operation=import` ya no se detiene en "unmatched" para un topic que no es de este
ledger: cada Component de su viewpoint (`IfcGuid`/`AuthoringToolId`) se resuelve
contra el documento activo y los vínculos cargados - por Element Id o UniqueId, o por
el parámetro `IFC_GUID` y, si no está guardado, por el id de exportación calculado de
Revit decodificado del mismo guid comprimido (`Core/IfcGuidCodec.cs`) - y el par se
**vuelve a medir** exactamente como `import_navisworks`. Solo un par reproducido entra
como hallazgo (origen `bcf`, `runComplete=false` siempre); uno que resuelve menos de
dos elementos se reporta `external_not_traceable` con la razón, nunca se inventa.

`navisworks_readiness` (solo lectura) busca la vista 3D `Navisworks` o `{3D}` y
reporta su nivel de detalle, caja de sección, fase visual y qué categorías MEP con
elementos están ocultas - `not_ready` si el detalle no es Fino o si hay una categoría
MEP oculta. `prepare_navisworks` (escritura, dry_run -> token -> apply) pone el
detalle en Fino y, solo con `unhide=true`, desoculta las categorías nombradas,
verificado por relectura tras el commit. `navisworks_status` (solo lectura) reporta
el estado propio de cada hallazgo de origen `navisworks` y sugiere qué mandarle a
naviscoord-mcp - nunca escribe en Navisworks.

## Parameters and classification

### `horizun_manage_parameters`

| operation | writes | what it does |
|---|---|---|
| `list_bindings` | no | Every entry of the BindingMap: name, parameter element id, data type (SpecTypeId), Instance/Type, categories, group, shared + GUID, VariesAcrossGroups. |
| `create_shared` | model + SPF | Reuses or creates the definition `name` in group `spf_group` of `spf_path` (file created if missing), then binds it to `categories` as `binding_kind` in `group` (default `PG_DATA`). Instance bindings get `SetAllowVaryBetweenGroups(true)` unless `allow_vary_between_groups=false`. |
| `create_project` | no | Refused by name: RevitAPI 2023-2027 has no call that creates a non-shared project parameter (`SharedParameterElement.Create` is the only factory; measured by reflection over all five years). No Python fallback is offered, because a script meets the same absent API. |
| `rebind` | model | Changes the category set (`categories` is the FINAL set), Instance/Type and/or group of an existing binding, found by `guid` or a unique `name`. |
| `remove_binding` | model | Removes the binding; the parameter element stays in the document. |
| `global_list` | no | Every Global Parameter: value (internal, display, formatted), formula, reporting, affected elements. |
| `global_create` / `global_set` / `global_delete` | model | `value` in the project's display units for measurable specs; `formula`; `associate: [{element_id, parameter}]`. `value` and `formula` are exclusive; a value on a formula-driven global is refused. |

**Data types.** 2023+ has no `ParameterType`. `data_type` accepts a SpecTypeId id
(`autodesk.spec.aec:length-2.0.0`, version optional), a SpecTypeId path (`String.Text`,
`Boolean.YesNo`, `Length`) or a legacy ParameterType name (`Text`, `YesNo`, `Integer`,
`Number`, `Length`, `Area`, `Volume`, `Angle`, `URL`, `Material`...), mapped in
`Core/ParameterClassificationRules.cs`.

**Rehearsal and verification.** `dry_run` defaults to true: the write runs in a
transaction that is rolled back and the reply carries the postcondition checklist and a
single-use `confirmation_token`. For `create_shared` the rehearsal runs against a
temporary COPY of the SPF, so the caller's file is never touched by a dry run. Apply
re-runs the same plan, checks it before the commit and re-reads it after:
binding kind, categories, group, data type, VariesAcrossGroups, and for `create_shared`
the SPF FILE parsed from disk (GUID, name, group). `rebind` / `remove_binding` count the
elements (instances or types) that hold a value in the categories being dropped; that
count is part of the plan the token binds (`ExpectedCascadeCount`), so a model that
gained values since the dry run is refused as a stale plan.

**Residual gaps.** The SPF definition is written before the transaction; a binding that
then fails leaves the definition in the file (`spf_definition_created` says so).
`global_set` checks each association, not that the element parameter already shows the
global's value.

### `horizun_query_classification` (read only)

| operation | returns |
|---|---|
| `keynote_table` / `assembly_code` | `source` (element id, path, server id, version status, file exists, link status), every entry (code, parent, text, level) with `types` and `instances` carrying that code, `codes_in_use`. |
| `unused_codes` | Codes of `table` that no type carries. |
| `missing_codes` | Placed types (one or more instances) without a code, and types (or materials, for keynotes) whose code is not in the table. |
| `family_lookup_tables` | Per family (all, or `family_id`): size table names, columns (name, spec) and row count. |

Codes are read from `KEYNOTE_PARAM` (types and materials) and from the assembly code
parameter of types (`UNIFORMAT_CODE` up to 2025, `ASSEMBLY_CODE` from 2026 - renamed in
the API, guarded per year). An empty or unloaded table is reported with a `warning`,
because it makes every code read as absent. Lookup tables are read with
`FamilySizeTableManager.GetFamilySizeTableManager(projectDocument, familyId)`: the
family is not opened, edited or reloaded.

### Live probes

`scripts/live-probes/parameters.probes.ps1` (offline test: `parameters.tests.ps1`):
list_bindings; create_project refused; create_shared of `HZ_PROBE_<run>` (Text, Walls,
Instance) into a temporary SPF under ScratchRoot, verified in the model and in the file,
then removed with remove_binding; a Length global created at 1500, read back, set to
2500 and deleted; keynote_table and family_lookup_tables read.

---

**Resumen en español.** `horizun_manage_parameters` lista el BindingMap completo, crea
parámetros compartidos en un SPF (el ensayo usa una copia temporal del SPF) y los
bindea, cambia o retira bindings contando los valores que se perderían, y gestiona
Global Parameters (valor en unidades de visualización, fórmula, asociación a parámetros
de elementos, borrado) releyendo el valor calculado. `create_project` se rechaza con
nombre: ninguna API de Revit 2023-2027 crea parámetros de proyecto no compartidos.
`horizun_query_classification` es solo lectura: tablas de keynote y Assembly Code (ruta,
estado, entradas, padre, uso por código), códigos sin uso, tipos sin código o con
códigos que no existen en la tabla, y tablas de búsqueda de familias leídas desde el
proyecto sin abrir la familia.



## Model diff, explanation and quality history

`horizun_model_diff` answers the question a contractor asks of every new
delivery - *what changed?* - without a coordination model or a linked copy.

### Operations

| operation | reads | writes |
|---|---|---|
| `snapshot` | active document, or `file_path` + `expected_version` opened in the background (detached when workshared) and closed without saving | `%USERPROFILE%\.horizun\snapshots\<id>.json.gz` + `<id>.meta.json` |
| `list` | the meta files | nothing |
| `compare` | two sides: a snapshot id or `active` | `snapshots\exports\<comparison>.csv` and `.json` |
| `colorize` | `before` snapshot vs the active model | a NEW view duplicated from `view_id` with overrides (the only model write) |
| `explain` | the active model, the project's quality history, `project_context_path` | nothing |
| `record_quality` | runs `horizun_model_scan` (summary) or `horizun_audit_model` in process | one line appended to `quality-history\<project>.jsonl` |
| `quality_trend` | the JSONL | `quality-history\<project>.trend.csv` |

### What a snapshot holds

Model elements only (model categories, not types, not view-specific): UniqueId,
element id, BuiltInCategory and category name, family and type (and the type's
UniqueId), level, workset, created/demolished phase, bounding box and location
(point or curve end points) in internal feet, every instance parameter and -
once per type - every type parameter, normalised (`d:` doubles rounded to 1e-9
in internal units, `i:` integers, `s:` strings trimmed to 256 characters, `e:`
element ids, `n:` no value), and a hash of volume, area and bounding-box extent.
`EDITED_BY` is skipped: who borrowed an element last is not a model change.
`max_elements` (default 50,000, cap 500,000) and `categories` bound it; a
truncated snapshot says so and so does every comparison using it. The document
facts carry the title, path, Revit version, saved format, VersionGUID and number
of saves when Revit exposes them.

### How a comparison decides

- **Identity is the UniqueId.** An element in both sides is compared; otherwise
  it is added or deleted.
- **Modified** means any of: type (family/type/type UniqueId), level, workset,
  phases, a move above `move_tolerance_mm` (default 1 mm; location first, else
  bounding-box centre, reported as `moved_mm`), geometry hash, or an instance
  parameter whose value differs (numbers within 1e-6 internal units are equal).
  Each change carries `before` and `after`.
- **Type parameters** are reported once per type in `type_changes`, not on each
  instance.
- **Re-created models.** When the smaller side shares under 50 % of its
  UniqueIds (and holds at least 10 elements), `summary.identity_warning` says so.
  `heuristic_match=true` then pairs deleted and added elements with the same
  category, family and type whose anchor points lie within 50 mm, nearest first,
  each used once; every such row has `inferred=true` and `before_unique_id`.
- **Discipline** is inferred from the BuiltInCategory (structure, mechanical,
  plumbing, electrical, site, architecture) - Revit declares none per element.
- `detail` is paged (`offset`, `limit` up to 1,000); the exports hold every row.
  CSV cells that start with `= + - @` (and are not numbers) are prefixed with `'`.

### colorize

`dry_run` (default) resolves the comparison and issues a token bound to the
document, `before`, `view_id` and the exact set of element UniqueIds to colour.
The apply duplicates the view (no detailing), removes its template, names it
`Horizun diff <snapshot>`, overrides added elements green (0,170,0) and modified
orange (255,140,0) - projection line colour plus a solid surface pattern when the
model has one - re-reads every override before committing (rolls back on any
mismatch) and again after. Elements not visible in the view are counted in
`not_in_view`; deleted elements cannot be coloured and are counted too.

### Quality history

`record_quality` stores what the scan or audit itself reported: for
`model_scan`, every bucket `total` of every section with `status: ok` as
`section.bucket`, plus `document.*_count` and `file_size_mb`; a failed section
contributes nothing and is listed in `failed`. For `audit_model`, each finding's
`count` and `is_issue`, and `health.score` when a health profile produced one.
`complete=false` marks a run that did not see the whole model. Malformed lines in
the JSONL are counted in `malformed_lines`, never skipped silently. `quality_trend`
returns one wide row per run (filter with `metrics`, `*` suffix allowed), ready
to pass as `rows` to `horizun_power_bi_push`.

### Limits

- A background open refuses when the file's version differs from the host
  (no upgrade), and cloud models are not opened by `snapshot`.
- The geometry hash is cheap by design: a reshaped element with the same volume,
  area and extent is not detected as a geometry change.
- `colorize` re-reads the projection line colour of each override; the surface
  pattern is set but not compared.

### Resumen (español)

`horizun_model_diff` responde "¿qué cambió entre estas dos entregas?". `snapshot`
guarda una instantánea del modelo activo o de un `.rvt` abierto en segundo plano
(desvinculado si es colaborativo) y cerrado sin guardar; `compare` reporta
añadidos, borrados y modificados (parámetros antes/después, movimientos sobre la
tolerancia, cambios de tipo) por categoría, disciplina inferida y nivel, con
paginación y exportación CSV/JSON. La identidad es el UniqueId; si el modelo fue
re-creado se avisa y `heuristic_match` empareja por categoría/tipo/ubicación,
marcando cada par como `inferred`. `colorize` es la única escritura: duplica una
vista y colorea con dry_run, token y relectura. `explain` resume solo hechos
medidos y, con `project_context_path`, lo que falta para ISO 19650 según el mismo
validador de `horizun_project_context`. `record_quality` y `quality_trend` llevan
el historial de calidad en JSONL y lo entregan listo para Power BI.



## Impact preview (MCP Apps)

**What it is.** An MCP App served at `ui://horizun/impact-preview`
(`text/html;profile=mcp-app`, MCP Apps extension, spec 2026-01-26). A host that
supports MCP Apps shows it beside the reply of a bulk write's **rehearsal**
(`dry_run=true`, the default) so a person can see what the write will touch before
approving it, and take some elements out.

**Which tools declare it.** Only the five whose rehearsal payload it knows how to read,
through `_meta.ui.resourceUri` in `tools/list`:

| Tool | Rows shown | What "exclude" removes from the request |
|---|---|---|
| `horizun_write_params_verified` | one per write (`rows[].index`), before -> requested | that entry of `writes[]` |
| `horizun_set_keynote` | one per resolved target (type or instance), current -> new keynote | every id in `element_ids` that resolved to that target |
| `horizun_delete_verified` | one per requested id and per cascade (`change_preview`) | `mode=ids`: the id from `ids`; `mode=purge_unused`: the id is added to `protect_ids`. A cascade row cannot be excluded alone: exclude the id that causes it |
| `horizun_transform_elements` | one per element (`change_preview`), operation / type / pin before -> after | the id from each operation's `element_ids`; an operation left empty is dropped |
| `horizun_create_elements` | one per planned element (`create:<index>`), kind, type, level | that entry of `elements[]` |

A rehearsal expanded from a CSV (`tabular_source`) is shown but cannot be narrowed row by
row: the rows are generated from the file on every call.

**What it shows.** How many elements the resolved plan holds (`plan_resolved.elements`),
counts by category and by level, a table of the rows with before -> after where the reply
has both, and warnings: elements you did not name that change too (shared type,
cascade), a blast radius that is a lower bound, unresolved entries, a withheld token,
and "only N of M rows are shown - the rest WILL be applied" when `change_preview` was
truncated (it carries at most 50 rows; exclusion is only possible for shown rows).

**The contract it keeps.** A `confirmation_token` approves ONE request: all five commands
fold the field the app narrows into their plan hash (`ImpactPreviewAppTests` asserts it
against the command sources). So the app never spends an old token on fewer elements:

1. With rows excluded, **Rehearse without N excluded** calls the same tool again with
   `dry_run=true`, the reduced request, and no token or idempotency key. The new
   rehearsal replaces the old one on screen, with a check that the excluded rows left
   the plan and that it did not grow.
2. With nothing excluded, **Apply** calls the tool with the arguments of the rehearsal on
   screen, `dry_run=false`, that rehearsal's own `confirmation_token` and a fresh
   `idempotency_key` (kept for a retry of the same token, so a retry replays instead of
   writing twice). The reply's `application.state` is shown; the button is then spent.

The app only calls tools when the host grants it (`hostCapabilities.serverTools`) and it
knows the original arguments (`ui/notifications/tool-input`); otherwise it is a read-only
view and the apply goes through the chat as always. After a re-rehearsal or an apply it
tells the model what happened through `ui/update-model-context`. The command's own gate
still decides: a stale plan, an expired token or a different document is refused by the
bridge exactly as for any other client.

**What it does not do.** It fetches nothing and loads nothing: no URL, no CDN, empty CSP
(`connectDomains`, `resourceDomains`, `frameDomains`, `baseUriDomains`). It renders only
what the rehearsal said. No view thumbnail: `horizun_capture_view` holds Revit's UI thread
and would be a tool call the user did not ask for. A host without MCP Apps sees exactly
the reply it saw before.

**Hosts.** The MCP Apps project lists ChatGPT, Claude, VS Code, Goose, Postman, MCPJam,
mcp-use and Alpic Playground as supporting hosts
([ext-apps README](https://github.com/modelcontextprotocol/ext-apps), read 2026-09-24).
Not measured here in any of them; the flow was exercised against a scripted host that
speaks the 2026-01-26 messages.

**Tests.** `tests/Horizun.Server.Tests/ImpactPreviewAppTests.cs` (resource, mime type,
CSP, declaring tools, tools/list cost, no network, plan-hash coverage, field names tied to
the emitting source) and `ImpactPreview/adapter.test.js`, the pure adapter under node
against five synthetic fixtures (no live capture of these rehearsals existed when it was
written). `scripts/live-probes/impact-preview.probes.ps1` measures the real replies in
Revit, including that a full-plan token is refused for a narrowed request.

### Resumen (español)

`ui://horizun/impact-preview` es una MCP App que muestra el **ensayo** (dry_run) de las
cinco escrituras masivas - `write_params_verified`, `set_keynote`, `delete_verified`,
`transform_elements`, `create_elements`: total por categoría y nivel, antes -> después por
fila, advertencias (colaterales, cascada, vista truncada, token retenido) y una casilla
por fila para **excluirla**. El token ata la petición completa (el campo que se reduce
entra en el hash del plan de cada comando), así que excluir filas pide un **nuevo
ensayo** de la petición reducida, y **Aplicar** solo gasta el token del ensayo que está en
pantalla, con una idempotency_key nueva. HTML autocontenido sin URLs ni CSP abierta, modo
claro/oscuro, tabla accesible. Sin herramientas nuevas: tools/list crece 305 bytes.
Hosts según el proyecto MCP Apps: ChatGPT, Claude, VS Code, Goose, Postman, MCPJam,
mcp-use y Alpic Playground (no medido aquí en ninguno).



## Code checks, 4D and federation

### `horizun_code_check` — a requirement set with geometry

Runs the grammar of [requirement-set.md](requirement-set.md) over the active
model. That grammar had a loader (`Core/RequirementSet.cs`) and no tool; this is
the tool, and it runs `docs/requirement-sets/*.json` unchanged. It is separate from
`horizun_audit_model` (whose `requirement_set` gates the audit's aggregate counts)
and from `horizun_audit_access` (a flat threshold map without per-rule selectors,
citations or `not_decidable`).

Additions to the grammar, all optional:

| Key | Where | Meaning |
|---|---|---|
| `measure` | assertion | A geometric quantity the bridge computes, instead of `parameter` (exactly one of the two). |
| `between` | operator | `value: [min, max]`, inclusive. |
| `unit` | assertion | `mm`, `m`, `m2`, `lx`: a numeric parameter is converted to it first; a parameter whose spec does not accept the unit reads as blank. |
| `missing_is` | assertion | `fails` (default) or `not_decidable` for an absent or blank value. |
| `name_matches`, `parameter_equals`, `measure_range` | selector | Element name regex; `{parameter, value}`; `{measure, min?, max?}` selects elements whose measure lies in `(min, max]`. |
| `source` | rule | Norm and numeral, echoed on every rule row. |
| `unverified_value` | rule | The threshold was not verified against the norm's text: the value may be omitted and every element is `not_decidable`. |
| `config` | rule | Configuration of `exit_count_minus_required`. |

Unknown keys in a rule, selector or assertion are refused, like unknown top-level keys.

Measures (`Core/CodeCheckRules.cs`):

| Measure | Source | Quality |
|---|---|---|
| `door_clear_width_mm` | door Width (instance, then type) | UPPER bound: below a minimum it fails for certain, above it is `not_decidable`. |
| `ramp_slope_percent`, `ramp_run_length_mm`, `ramp_width_mm`, `ramp_landing_length_mm` | the ramp's own planar top faces | exact geometry; a ramp with no flat top face has no landing measure. |
| `stair_riser_mm`, `stair_tread_mm`, `stair_2r_plus_t_mm`, `stair_run_width_mm` | `Stairs.ActualRiserHeight/ActualTreadDepth`, narrowest `StairsRun.ActualRunWidth` | exact; handrails not deducted. |
| `space_illuminance_lx` | Space `Average Estimated Illumination` | 0 or empty is `not_decidable` (Revit computed nothing). |
| `exit_count_minus_required` | per Level: placed rooms, doors | needs `config`: an occupant load (`occupant_load_parameter`, or `occupancy_parameter` + `area_per_person_by_group: {group: m2}` so each room takes the factor of the group it declares, or one `area_per_person_m2`), `exit_door` (`{parameter, value}` or `{mark_prefix}`) and `required_exits: [{max_load, exits}]`; any missing piece - including a room with no group or a group the table does not list - is `not_decidable`. |
| `travel_distance_m` | per room, routed with Revit's path of travel | only when the rule's OWN `config` names `route_view_id(s)` (a floor plan, crop off) and `exits`; a rule without one stays `not_decidable` and never borrows another rule's routes. The value is a LOWER bound: above the limit it `fails`; it `passes` only under a proven ceiling (see below); otherwise `not_decidable`. Multi-level egress is `not_decidable` with a reason prefixed `not_assessable:`. |

Outcomes are `passes`, `fails`, `not_decidable`, `unreadable`. A rule's verdict is
`fails` if any element fails, `not_decidable` if any element is undecided or the
rule examined nothing, `passes` otherwise. Parameters may be named by display name
or by `BuiltInParameter` token (`ALL_MODEL_MARK`), which does not change with the
language of Revit. Categories accept `OST_*` tokens or display names.

Example sets in `standards/` (data, not compiled in; review before use):

- `co-ntc6047-accesibilidad.json` — NTC 6047:2013: door clear width 800 mm (16.1.2),
  ramp width 1 200 mm (8.2.3), slope by run length (8.2.2, Tabla 2), landing 1 500 mm
  (8.2.4), stairs riser ≤ 180, tread ≥ 260, 2C+H 600–660, flight width 1 200 (11.1–11.2).
  The 1:14 row of Tabla 2 is ambiguous in the PDF (3 640 or 3 920 mm) and that band
  is marked `unverified_value`.
- `co-nsr10-titulo-k-evacuacion.json` — NSR-10 Título K, K.3, read on 2026-09-24 from
  the text of the Comisión Asesora Permanente
  ([idrd.gov.co copy](https://idrd.gov.co/sites/default/files/documentos/Construcciones/11titulo-k-nsr-100.pdf),
  pages K-15 to K-23) and checked against MinVivienda's
  [anexo técnico de modificaciones](https://minvivienda.gov.co/sites/default/files/consultasp/Anexo%20t%C3%A9cnico_4.pdf),
  whose only change to Título K is the K.3.2.5 cross-reference. Values below; "set"
  marks the ones the set applies. Exit doors, 1 200 mm stairs and room occupancy groups
  are selected through placeholder parameters (`Exit Door`, `NSR10 Stair Class`,
  `Occupancy Group`) that a project renames.

  | Requirement | Value | Numeral | In the set |
  |---|---|---|---|
  | Riser (contrahuella) | 100 to 180 mm | K.3.8.3.4 (b) | set |
  | Tread (huella), straight flight | ≥ 280 mm | K.3.8.3.4 (a) | set |
  | 2 risers + 1 tread | 620 to 640 mm | K.3.8.3.4 (c) | set |
  | Curved flights / circular / spiral tread | ≥ 240 mm at 1/3 (≤ 420 outer) / ≥ 250 mm / ≥ 190 mm at 300 mm | K.3.8.3.4 (d), K.3.8.3.9, K.3.8.3.10 | no (not separated by the selector) |
  | Stair width, load < 50 | ≥ 900 mm | K.3.8.3.3 | set (floor for every evacuation stair) |
  | Stair width, load > 50 or public use | ≥ 1 200 mm | K.3.8.3.3 | set, on stairs marked by a placeholder |
  | Stair width inside dwellings / single-family | 900 / 750 mm | K.3.8.3.3 | no (K.3.8.3 excludes stairs inside dwellings) |
  | Landing depth; rise between landings | = stair width, ≤ 1.20 m needed; < 2.40 m (assembly, institutional), < 3.50 m others | K.3.8.3.5 | no measure |
  | Stair headroom | ≥ 2.0 m | K.3.8.3.7 | no measure |
  | Exit door clear width | ≥ 800 mm (bedrooms 700; each leaf of a split door ≥ 700) | K.3.8.2.1 | set (upper bound: nominal leaf) |
  | Exit door height | ≥ 2.0 m | K.3.8.2.1 | set (`DOOR_HEIGHT`) |
  | Doors in series; opening force | ≥ 2.10 m apart; < 250 N | K.3.8.2.3, K.3.8.2.6 | no measure |
  | Exit access (corridor) width | ≥ 900 mm and ≥ capacity by Tabla K.3.3-2 | K.3.3.4 | no measure |
  | Width per person: corridors, doors, passages / stairs (mm) | A 5/8, C 5/10, F 6/10, I-1 6/10, I-2…I-5 13/15, L 5/10, P 10/18, R 5/10; −50 % with a complete extinguishing system | Tabla K.3.3-2, K.3.3.3.1 | no (per-exit load not in the model) |
  | Occupant load factor (m² net per occupant) | A 28; C-1 10; C-2 3 (street level and below) / 6 (other floors); F 9; I-1 11; I-2 7; I-3 2; I-4 2.8; I-5 0.3; L-1 0.7; L-2 1.3; L-3 0.7; L-4 0.7; L-5 0.3; P 9; R 18; E, T by occupancy; M the largest of its parts | Tabla K.3.3-1 | set (`area_per_person_by_group`) |
  | Minimum exits by occupant load | 0–100: 1; 101–500: 2; 501–1 000: 3; > 1 000: 4 | K.3.4.2, Tabla K.3.4-1 | set (per level) |
  | Travel distance to an exit, without / with sprinklers (m) | A-1 60/75; A-2 90/120; C-1 60/90; C-2 60/75; F-1 60/75; F-2 90/120; I 45/60; L 60/75; P not allowed/22; R 60/75; +30 % if straight, no intermediate stairs, to exterior at grade | K.3.6.5, Tabla K.3.6-1 | no value (group-dependent and never computed) |
  | Travel inside a room of ≤ 6 people; dead-end corridors | ≤ 15 m; ≤ 6 m | K.3.6.3, K.3.5.1.3 | no measure |
  | Evacuation ramp: slope; width; landings; headroom | 1:12 (printed "8 %"); ≥ 1.10 m (exceptions 0.90–2.4 m); 1.8–3.6 m; ≥ 2.0 m | K.3.8.6.2, .4, .7, .5 | no (the 1:12 vs 8 % reading is ambiguous; not transcribed) |
- `co-retilap-iluminancia.json` — RETILAP as modified by Resolución 40150 de 2024,
  Libro 3, Tabla 3.2.2.6 a (maintained illuminance Ēm per space type), matched on
  Space names; plus a Room template with a declared illuminance parameter.


#### Egress travel distance — `operation: "travel_distance"`

Routes egress per room with Revit's own `PathOfTravel` service (same members in 2023–2027).

```json
{ "operation": "travel_distance",
  "travel": { "view_ids": [123], "exits": { "parameter": "Comments", "value": "EXIT" },
              "room_ids": [456], "max_m": 45, "create_paths": false } }
```

- `exits` is the caller's declaration — `{parameter, value?}`, `{mark_prefix}` or `{element_ids}`. Nothing in a
  model reliably marks an exit, so it is never guessed.
- Per room: the longest of the routed sample points (boundary corners pulled 300 mm toward the room point and kept
  only if Revit's own room lookup still puts them in that room, the room point, and any whole-plan start from
  `FindStartsOfLongestPathsFromRooms` that falls in the room) to the nearest declared exit (`FindShortestPaths`; the
  exit reached comes from `FindEndsOfShortestPaths`). Each row gives `distance_m`, `bound: "lower"`,
  `upper_bound_m`, `candidates`, `routed`, `dropped_outside_room`, `exit_door_id`, `start_m`, `polyline_m` and
  `outcome` (`passes|fails|measured|not_decidable|not_assessable`).
- **`distance_m` is a lower bound.** The true farthest point is at least that far. Over `max_m` is a certain
  `fails`. A `passes` needs `upper_bound_m <= max_m`, and a ceiling is proven only for a convex room of straight
  walls, no island, and every sample routed: `min over routed samples c of travel(c) + max over vertices |v - c|`,
  assuming a straight walk inside the room to `c` is clear (`ceiling_assumes`). Otherwise `not_decidable`, with the
  reason: not convex, curved wall, island, or `k of N sample points found no route` (a start inside furniture).
- Measuring opens **no transaction**: the Find* calls are computations. `create_paths: true` is the only write:
  dry run → `confirmation_token` → apply creates one `PathOfTravel` per measured room in its plan, re-read after
  the commit (owner view + length within max(50 mm, 1 %) of the measured route).
- Honest limits: one level per plan view. A room with no declared exit matched on its level (egress through a
  stair) is `not_assessable` — exits on another level are never flattened into the route. A requested room on a
  level no given plan shows, in another phase than the plan's, or in a secondary design option is `not_decidable`
  with `out of scope: ...`; with `room_ids` omitted such rooms are only counted (`coverage.travel_distance.
  rooms_not_asked_out_of_scope`). Obstacles are what the plan shows, in its phase. A plan whose crop box is active is
  refused (Revit ignores what lies outside the crop and `Create` throws there).
- `create_paths` is refused under `permission_profile=read_only`; the check and the measurement stay available there
  and under `force_read_only_on_workshared`. One room whose `PathOfTravel.Create` throws is that room's unverified row.
- In a requirement set, a rule `{ "measure": "travel_distance_m", "operator": "lte", "value": 45 }` with its own
  `config: { route_view_id | route_view_ids, exits }` is measured under its own key; two rules with different
  exits are two measurements. `coverage.travel_distance` is keyed by rule id.
### `horizun_link_schedule` — 4D

`operation`: `import` (file only), `match` (read), `write` and `status_view`
(MutatingUnlessDryRun: dry run by default, `confirmation_token`, `idempotency_key`).

- Formats by extension: `.xml` MS Project MSPDI (summary tasks and UID 0 skipped;
  `Id` is the UID), `.csv`/`.txt` with header `id,name,start,finish[,wbs,percent_complete,actual_start,actual_finish]`
  (Spanish aliases accepted; `,` `;` or TAB), `.xer` Primavera P6 (TASK rows, WBS
  path from PROJWBS without the project node, WBS summary rows skipped). Dates must
  be ISO (`yyyy-MM-dd`…): `05/02/2026` is rejected by line, never guessed. A DTD in
  the XML is refused.
- `match`: `{parameter, key: id|wbs}` — candidates are the model elements that carry
  the parameter; or `{rules:[{activity, category, level?, parameter?, value?}]}`.
  An element whose key names two activities, or that two rules assign differently,
  is `ambiguous` and linked to neither. Reply lists links, elements without activity
  and activities without elements.
- `write`: `{activity_parameter, start_parameter?, finish_parameter?}` — TEXT INSTANCE
  parameters only; a missing, read-only, non-text parameter or a group member is an
  unresolved row. A refused `Set` rolls the batch back; every row is re-read.
- `status_view`: `view_id`, `as_of`. Duplicates the view (the source is untouched),
  detaches the copy from its template and colours each linked element: done green,
  in_progress amber, future grey, late red. `late` needs percent complete or actual
  dates; without them the status is the planned one. Each override is re-read.
  The token binds the schedule file's SHA-256: an edited file is a stale plan.

### `horizun_federation_check` — federation QA (read-only)

`rules`: `models:[{match, discipline?, allowed_categories?, forbidden_categories?}]`
(`match` is a regex on the model title, `$host` for the host; a model no rule claims
is `unclassified`, two rules make it `ambiguous`), `expected_links:[{name_matches,
count?=1, workset_matches?}]` (missing / fewer / duplicated / wrong workset, and
links no entry expected), `same_site` (default true). Same site is measured: three
points of each link are taken to shared coordinates through the link's own
project location and through the instance transform plus the host's; the largest
disagreement is compared with `tolerance_mm` (default 10). An unloaded link is
`not_decidable`. Link workset and phase are reported.

### Resumen (español)

- `horizun_code_check`: evalúa requirement-sets declarativos (parámetros y medidas
  geométricas). Las normas son datos en `standards/`; cada regla cita norma y
  numeral; lo no calculable o no verificado es `not_decidable`, nunca `passes`.
- `horizun_link_schedule`: 4D con MS Project XML, CSV o XER; empareja por parámetro
  o reglas sin elegir nunca entre dos actividades; escribe actividad/fechas y
  colorea una vista duplicada por estado, con ensayo, token y relectura.
- `horizun_federation_check`: categorías fuera de lugar por modelo, vínculos
  esperados/faltantes/duplicados, workset y fase, y coordenadas compartidas
  coherentes medidas en tres puntos.

## Spatial coherence after every write (`spatial_check`, `horizun_verify_changes`)

A typed write re-reads its postconditions: that proves the request was carried out,
not that the result makes sense. Measured in field use: a modelling session left a
column and a door in the same place with every postcondition true.

**Automatic.** Every call that leaves the model changed carries `spatial_check`, and
`attention` as the FIRST key of the reply when it found something. The dispatcher
watches `DocumentChanged` for the duration of the call (`Core/ChangeWatch.cs`), keeps
what the call added or modified after its own rollbacks, and checks those model
elements (`Core/SpatialCoherence.cs`): every element whose solid intersects theirs
(`ElementIntersectsElementFilter`), the shared volume (`BooleanOperationsUtils`), and
how the two relate. The rules (`Core/SpatialCoherenceRules.cs`, unit-tested) judge:

| Situation | Verdict |
|---|---|
| door or window sharing **3 L or more** of solid with a column, beam, another wall, MEP, furniture, stair | error — blocked opening |
| a door's frame touching its floor or the wall beside it (**under 3 L**; measured 0.1–1.1 L on a real model) | expected, not a finding |
| a wall, column or stair filling **10 L or more** of the 0.6 m clear zone in front of or behind a walk-through door (**1.80 m or taller**) | error — passage blocked |
| an intersection Revit could not measure | at most a warning, never silently clean |
| two elements of the same type occupying ≥95 % of the same volume | error — duplicate |
| duct/pipe/tray/conduit through a structural column, beam or foundation | error — clash |
| MEP against MEP without a connector between them | warning |
| same category overlapping without a join (walls, floors, columns) | warning |
| furniture, fixtures, equipment against structure or each other | warning |
| host and hosted, joined elements, MEP connected, curtain members, the structural frame (beam–column–slab), walls on slabs, MEP through walls/floors | expected, not a finding |
| shared volume below ~28 cm³ (touching faces) | nothing |

It never rolls anything back — the write already committed and verified what was
asked. It is bounded (800 elements, 8 s) and says `partial` when a bound stopped it.
Loaded Revit links are examined too: each changed solid is carried into the link's
coordinates and judged by category (host/join/connector relations do not cross
files); an unloaded link is listed in `links_skipped`, never counted as clear.
`HORIZUN_SPATIAL_CHECK=off` disables it for a process, for a bulk import that checks
once at the end.

**Data-only tools now get a bounded look too.** Parameters, keynotes, worksets,
materials, schedules, views… used to be skipped outright: `DocumentChanged` cannot
tell a moved element from a renamed one, and checking every parameter write in full
would pay a solid-intersection pass for a rename. But a parameter write CAN move
geometry — an offset, a base height, a type swap — so `Core/DataOnlyGeometryRules.cs`
(backed by a bounded `Core/BBoxCache.cs`) decides per element instead of skipping the
tool: a modified element whose bounding box differs from the one recorded the last
time ANY Horizun call saw it (moved beyond ~3 mm) is always checked; an element never
seen before this Revit session is checked only when there are at most 200 such
elements across the call, whole, never partially. Above that cap the check is
skipped for that write and `spatial_check.scope` (or `.scope_note`) says exactly how
many elements and why — a documented limit, not a silent gap. `horizun_verify_changes`
remains the way to look at those elements explicitly afterwards.

**On demand.** `horizun_verify_changes` checks the elements a Horizun write in the
active document changed, or the `element_ids` given, and returns the findings plus an
IMAGE: a temporary isometric 3D view with a section box around them — blue changed,
red in an error, orange in a warning, annotations hidden — created in a transaction
group that is always rolled back (`image.temporary_view_rollback = RolledBack`). Call
it after a modelling batch and look at the image: the check sees solids, not intent (a
wrong level or room, or a missing element, needs the picture).

- `scope` (default `last_write`): the previous behaviour, unchanged — only the most
  recent Horizun write in this document (kept in memory since Revit started).
  `scope=session` instead unions every write's added/modified ids since Revit started
  (or `since_utc`, an ISO-8601 UTC instant), so several writes in a row get checked
  together; `ChangeLedger` now keeps a bounded (500 writes) history per document
  alongside the "last write" entry it always kept. The union is capped at 2000 distinct
  ids — `scope.truncated` and `scope.truncated_why` say so when it was cut. Passing
  `element_ids` overrides `scope` entirely, as before.
- `include_annotation=true` additionally runs the TAG/TEXT-NOTE OVERLAP check
  (`Core/TagOverlapRules.cs` for the pure 2D rectangle geometry, `Core/TagOverlapCheck.cs`
  for the Revit-side gathering): every `IndependentTag` and `TextNote` in the relevant
  view(s), compared pairwise by their VIEW-COORDINATE bounding box
  (`Element.get_BoundingBox(view)` — not model space; annotation is drawn flat on the
  sheet or view). Two tags/text notes stacked on top of each other are reported; two
  tags of the SAME host element that overlap are called out by name ("two tags of the
  same element overlap"). A LABEL-ONLY tag whose extent Revit does not expose through
  that call is `unmeasured`, never folded into "clear" — the check did not look at it.
  `view_ids` names the view(s) explicitly (each must resolve to a view in this
  document, or the call is refused); omitted, it defaults to the owning view of any
  tag/text note already in scope, else the active view of THIS document. Findings
  land in `annotation_check` and fold into the same `attention` headline as
  `spatial_check`. `horizun_annotate` also gets this automatically and for free right
  after it creates a tag or text note — capped to at most 3 owning views so the
  automatic pass stays cheap; a batch that annotated more views calls
  `horizun_verify_changes(include_annotation=true, view_ids=[...])` explicitly.

### Visual diff before/after (`operation=snapshot` / `operation=compare_to`)

`operation` defaults to `check` (everything above). Two more operations turn the same
capture path into a before/after comparison; both need `target_document` (the mutation
gate, because the temporary view is created and then rolled back like the check's own
picture). As in the check, the rollback is not a footnote: when it does not confirm, or
the temporary view still resolves in the document afterwards, the call fails with
`temporary_view_not_rolled_back` and `write_started: true` (delete the view with
`horizun_delete_verified`). A capture that failed after a confirmed rollback answers
`capture_failed` with `write_started: false`.

- **`snapshot`** saves a named baseline: `snapshot_name` (sanitised to letters, digits,
  `-`, `_`, `.`; kept per document) plus EITHER `element_ids` — the camera is framed
  around them exactly like the check's image (padded section box, `orientation`, crop
  fitted to the box) — OR an orthographic 3D view (`view_id`, else the active 3D view)
  whose own orientation and active crop/section box are reused. Refused seeds: one with
  neither box active (Revit would refit to the model's extents, so an element added far
  away would move the whole frame), a perspective camera view
  (`perspective_seed_not_supported`: the capture view is orthographic and a perspective
  crop box means something else there) and a sketched crop region
  (`non_rectangular_crop_not_supported`). The camera stored is the one Revit APPLIED to
  the temporary view (read back after the commit), not the one requested, in
  `%USERPROFILE%\.horizun\verify\baselines\<doc-key>\<name>.png` + `.json`. `<doc-key>`
  is the document title plus a hash of its path, so two models with the same file name
  never share baselines; the json also records `document_path` (compare_to refuses
  another document with `baseline_other_document`), the camera (eye, up, forward,
  section and crop boxes with their transforms), `pixel_size`, `captured_at_utc` /
  `captured_at_ticks` and the PNG's `png_sha256`. Both files are written under temporary
  names, re-read (the PNG must decode, the hash must match), and only then moved over
  the previous pair; `replaced_existing` (with `replaced_captured_at_utc`) says when a
  same-named baseline was overwritten.
- **`compare_to`** (`snapshot_name` of an existing baseline) first refuses a PNG that no
  longer matches its camera's hash (`baseline_unpaired`: an interrupted snapshot or a
  replaced file), then rebuilds a temporary view from the STORED camera — not from
  whatever the live view looks like now — and checks that Revit applied it: view and up
  directions, the crop rectangle within half a pixel, the section box within
  max(half a pixel, 1e-3 ft). Otherwise, or when the image size differs, it refuses with
  `frame_not_reproduced` and both cameras, instead of diffing misaligned pixels
  (`camera_reproduced` reports the tolerance and the deviation measured). It exports at
  the same `pixel_size` and diffs with `Core/ImageDiff`: per-pixel max(|ΔR|,|ΔG|,|ΔB|)
  above 24 counts as changed, the mask is dilated by 2 px, and a region is kept only with
  at least 8 RAW changed pixels (so an isolated antialiasing speck is dropped whatever
  the dilation). Returns `before_path`, `after_path`, `diff_path` (the after image with
  the dilated mask painted red), all three re-read before `artifacts_verified: true`;
  `changed_pixel_ratio` (measured on the RAW mask); `regions` (up to 50, largest first)
  each with `pixel_bbox`, `pixel_count` (raw), `model_bbox_approx` (the pixel rectangle
  swept through the section box's depth — the crop's near/far range without one — and
  clipped to the section box, feet: a bound on where the change is, not an element box)
  and `element_ids`: the elements changed since the baseline whose bounding box,
  projected into the image through the same crop map (`Core/CropPixelMap`), touches the
  region. `elements_changed_since_baseline` lists the ids `ChangeLedger` recorded for
  Horizun writes after the capture (compared by ticks) in this Revit session — manual
  edits are not in the ledger, although they do show in the pixels. Deleted elements are
  only counted (`elements_deleted_since_baseline_count`): a region they caused has no
  `element_ids`. The ledger keeps the last 500 writes per document; when older writes
  since the baseline were evicted, `change_history_evicted` and
  `elements_changed_since_baseline_truncated` are true.

Typical use: `snapshot` on the elements about to be touched, model, then `compare_to`
and look at `diff_path`. PNG decoding/encoding runs in the add-in through WPF imaging
(PresentationCore, referenced on net48, net8 and net10); `Core/ImageDiff` itself only
sees `int[]` ARGB arrays, so its threshold, dilation and labelling are unit-tested on
plain net8. To measure live (probe `visual-diff`): whether Revit's export adds a margin
around the crop (the attribution tolerates 6 px), whether a read-back camera is
re-applied within the tolerances above, and how far a recapture of an unchanged scene
is from exactly 0.

### Equipment clearance zones (`clearance_rules`)

The door clear zone generalised to any equipment a project declares: a panelboard's
front working space, an air handler's service side, a valve's overhead access. No
distance or catalogue is compiled in - a rule is caller data, so the check stays
organisation-neutral:

```json
"clearance_rules": [
  { "category": "OST_ElectricalEquipment", "family_contains": "panel", "face": "front",
    "depth_mm": 900, "width_extra_mm": 150, "height_mm": 2000 }
]
```

- `category` (required): a `BuiltInCategory` token (`OST_*`). `family_contains` /
  `type_contains` (optional): case-insensitive substrings of the instance's family /
  type name. The FIRST rule an instance satisfies, in declaration order, applies.
- `face`: `front` (default) - a box off the face the instance looks out of; `all` -
  front, back, left and right; `top` - a box above the equipment. Front/back/left/right
  are read in the instance's OWN frame (from its own solids), so a rotated instance
  keeps its front. A face-based / work-plane-based instance (a panel hosted on a wall
  face) looks out along its transform's Z, not its `FacingOrientation`, which lies in
  the host face; one with no horizontal front is reported, never skipped.
- `depth_mm` (required, > 0): how far the zone extends off the face (for `top`, how
  high). `width_extra_mm` (default 0): added on EACH side of the equipment's own
  width. `height_mm` (default 2000): the working-space height, measured up from the
  instance's LEVEL (its own, its schedule level, or its host's), not its underside -
  a panel mounted 1.2 m up still sees a low cabinet in front of it - and never lower
  than the equipment's own top.
- Obstacles are physical elements of the document AND of every loaded link (the zone
  is carried into the link's coordinates, the same path the pair check uses for
  links), intersected on solids with the door clear zone's own code path. The
  equipment's own host (a panel's wall), its nested components, floors, ceilings,
  roofs, the structural frame, railings, doors and windows are never obstacles; an
  MEP run connected to the equipment by a connector is expected, not an invasion.
- Findings read `clearance zone of <equipment> is invaded by <element>`: an ERROR for
  a wall, column, stair, curtain panel/mullion or other equipment (electrical,
  mechanical, specialty, or any category a rule names); a WARNING for anything else
  (furniture, casework, MEP runs). The door calibration applies: an intrusion under
  10 L is ignored, and a volume Revit could not measure stays a finding.
- Scope, like the door clear zone: ruled equipment in the checked elements, AND ruled
  equipment the call did not touch that lies within the largest rule's reach of a
  checked element - so placing a column in front of an existing panel is caught by the
  column's own write. Link obstacles are looked for only around equipment in scope.
- A malformed rule is refused by index (`clearance_rules[1].category must be ...`)
  before anything is read: a value of the wrong JSON type (`"900mm"`, `null` for
  `depth_mm`) and a category this Revit does not know (`OST_ElectricalEquipments`,
  wrong case) included.
- `spatial_check.equipment_clearance` reports `zoned` (ruled instances whose zones were
  built and searched) and `not_measured` (id + reason, e.g. no horizontal front). Any
  not-measured instance or unreadable file rule makes the answer `partial`, never
  `clean`.

**Where rules come from.** `horizun_verify_changes(clearance_rules=[...])` always
wins (`spatial_check.clearance_rules_source = "argument"`). Without the argument - and
in the AUTOMATIC `spatial_check` after every write - the rules are read, in this
order: (1) the project's own `clearance-rules.json`, found walking up at most four
folders from the active document's file; the walk stops at the folder holding
`project-context.json` (the project root), whose closed schema has no place for rules;
(2) `%USERPROFILE%\.horizun\clearance-rules.json`; (3) neither: no equipment clearance
check - the door clear zone still runs. Both files are a plain array of rules (or an
object with a `clearance_rules` array). Only a LITERAL `[]` in the project file is an
opt-out that also silences step 2; a file whose entries are all malformed is not.
`spatial_check.clearance_rules_source` names the source without an absolute path;
file errors come back in `spatial_check.clearance_rules_errors` and make the answer
`partial`, never a failure of the write it follows. Live probe:
`scripts/live-probes/clearance-zones.probes.ps1`.

## Field notes: naming side effects and a read-only add-in's own writes

Three usage notes from a 2026-09-25 field session, kept here rather than invented
into a new tool, per the standing rule that this bridge is organisation-neutral
and states observed behaviour rather than a company's policy:

- **Renaming a workset takes the whole workset into your ownership.**
  `horizun_manage_worksets` `rename` calls Revit's `WorksetTable.RenameWorkset`,
  and on a central/local model that checks the workset out to the caller like any
  other edit to it - every element already owned by someone else on that workset is
  unaffected, but the workset itself (and anything not yet owned by anyone) is now
  borrowed by whoever renamed it, exactly as if they had edited an element on it.
  This is a Revit workset-ownership behaviour, not something `rename` adds on top;
  the reply's `owner`/`borrowed_by_other` fields on other operations describe the
  same mechanism from the element side.
- **Renaming a level renames its homonymous views.** A newly created level in
  Revit creates matching plan views named after it (Level 1 -> "Level 1"); Revit's
  own UI renames a level's floor plan/ceiling plan views along with it when the
  level is renamed through the normal path. A script or add-in that changes a
  `Level` element's name through a path that does not trigger that side effect can
  leave the level and its views out of sync in a way that reads as "the level
  renamed but the plan didn't". Worth checking after any level rename, typed or
  via `horizun_execute_python`.
- **A "read-only" mode is not automatically read-only for an external add-in.** A
  third-party add-in's own "read-only" or "audit" mode can still open a
  Transaction and commit it - for example creating a temporary 3D or schedule view
  to drive its report, then deleting that view when it finishes. From outside, this
  looks exactly like the kind of side effect `horizun_execute_python`'s own
  `read_only=true` is built to prevent for OUR scripts (see the top-level tool
  description): a transient view created and removed within one call. It is not
  necessarily a defect in that add-in, but a caller auditing "did anything change"
  around a third-party tool must not assume a name like "read-only" describes what
  the Revit API actually recorded - re-read the model, the way `read_only_check`
  does here, rather than trusting the label.

**Calibration on a real model (2026-09-26).** A read-only pass over a detached copy of a
real architecture model (Revit 2025, 234 doors) first reported 157 errors: door frames
embedded 0.1–1.1 L in floors and adjacent walls, a clear zone as deep as the door is wide
(a 2.6 m balcony door "saw" its parapet), and a gas-meter niche door "blocked" by its own
back wall. With the 3 L opening threshold, a fixed 0.6 m zone, walk-through doors only
(≥ 1.80 m) and a bounding-box prefilter, the same pass finished in 24 s with one finding -
a chute hatch against its hopper whose volume Revit could not measure, now a warning.
A column in a doorway (51 L) and one 0.7 m in front of a door still come back as errors.

## horizun_fix_planimetry: non-rectangular (polygon) crops

`set_crop` accepts `crop.loop` - a closed polygon of at least 3 `[x, y]`
view-plane points - as an alternative to `crop.min`/`crop.max`. It is refused BY
NAME (not as a capability gap) on a view whose
`ViewCropRegionShapeManager.CanHaveShape` is false; a rectangle is unaffected by
this and still goes through `View.CropBox`.

- **The polygon is written through `SetCropShape(CurveLoop)`, then cleaned up in
  the same transaction.** `SetCropShape` was MEASURED (Revit 2026, 2026-08-25, on
  a rectangular loop, the live gate) to install a crop-region sketch and create
  two non-view-specific `Dimension` elements as a side effect - elements that were
  still there after the shape was removed again. A command whose contract is that
  it writes only what it names cannot leave those behind, so `Apply` diffs the
  document's `Dimension` elements immediately before and after `SetCropShape`,
  and deletes whatever appeared, before the transaction that holds them ever
  commits. The assumption behind this - that those dimensions are UI witnesses of
  the sketch's constraints and not the shape's geometry, so removing them leaves
  the polygon intact - is UNVERIFIED beyond the rectangular-loop measurement above;
  it has NOT been measured live for an actual polygon loop. If it is wrong, the
  rehearsal's and the apply's own re-read of the shape (vertex by vertex, see
  below) simply fails the postcondition and the whole batch rolls back - it can
  never silently report success over a broken shape or a model that kept the
  extra elements. `scripts/live-probes/fix-planimetry.probes.ps1` needs a case
  that actually sets a polygon crop and inspects the model's `Dimension` count
  before/after to close this gap.
- **Verification compares vertices, not a bounding box.** `crop_shape`'s postcondition
  re-reads `ViewCropRegionShapeManager.GetCropShape()`'s loop and matches every
  requested vertex to a distinct read vertex within the batch's tolerance
  (default 0.1 mm, well inside the 1 mm this feature targets) - unordered, since
  Revit is free to start or wind the loop however it likes. A rectangle's crop
  still compares as a bounding box, unchanged.

## horizun_fix_planimetry: `set_view_display` (detail level and discipline)

`set_view_display` sets `View.DetailLevel` and/or `View.Discipline` of ONE view,
citing a finding about that view - in practice a requirement-set rule of
`horizun_audit_planimetry` with `entity: "view"` and `field: "detail_level"` or
`"discipline"` (the same property names `horizun_model_scan`'s `view_profile`
judges as `expected_detail_level` / `expected_discipline`). No universal check
maps to it, so a universal finding cannot cite it.

```json
{ "operation": "set_view_display", "finding": { "...": "copied verbatim" },
  "view_id": 12345, "detail_level": "Fine", "discipline": "Architectural" }
```

- **Values are the audit's own spelling, exactly.** `detail_level` is `Coarse`,
  `Medium` or `Fine` (`Undefined` is a reading, never a target); `discipline` is
  `Architectural`, `Structural`, `Mechanical`, `Electrical`, `Plumbing` or
  `Coordination`. Case is not forgiven, because the re-read compares strings.
- **At least one of the two**, otherwise the action names nothing and is refused.
- **Refused BY NAME before any transaction** when the view's template controls
  the parameter (`GetTemplateParameterIds` minus
  `GetNonControlledTemplateParameterIds` contains `VIEW_DETAIL_LEVEL` /
  `VIEW_DISCIPLINE`): the assignment would be overwritten by the template. The
  remedies are `set_view_template` or an edit of the template itself. Also refused
  on a template, a sheet or a schedule, when `HasDetailLevel` /
  `HasViewDiscipline` is false, and when `CanModifyDetailLevel` /
  `CanModifyViewDiscipline` is false.
- **Re-read after the commit:** the requested value(s) must read back, and the
  property NOT requested must read back exactly as it was before
  (`detail_level_unchanged` / `discipline_unchanged`). The audit then re-runs and
  reports the cited finding as resolved or persistent.

## horizun_deliver_ifc: telling an empty parameter apart from a dropped mapping

`model_comparison` (inside the `pset_mapping` gate's evidence) merges a
BEFORE-export read of the model with the AFTER-export coverage check, per
declared property: `exported`, `empty_in_model`, `not_applied`,
`parameter_missing`. The model-side read resolves each mapping row's IFC
classes to a Revit category through a built-in table
(`DeliverIfcCommand.IfcClassCategories`) covering common architecture,
structure and MEP classes (walls, slabs, columns, beams, doors, windows,
stairs, railings, spaces, ducts, pipes, cable trays, flow terminals,
electrical appliances...). A class outside that table makes the ROW
`category_unmapped` - the model is not read for it, and only the file's own
coverage (unaffected) is known.

`exported` and `not_applied` are **aggregate** counts, not element-matched: no
IFC GlobalId correlates a specific model element to its file entity without
recomputing the exporter's own GUID algorithm (this bridge does not carry
that algorithm). The arithmetic (`not_applied = max(0, has_value - carrying)`)
holds as long as the model census and the file's coverage check are counting
the SAME population; a `population_mismatch_note` on the row says so whenever
the heuristic category resolution and the file's own IFC-class count disagree,
rather than trusting the aggregate silently. `PsetMapping.CombineWithModel`
(Revit-free) does the merge; `DeliverIfcCommand.ComputeModelCensus` (Revit-
side) builds the per-row model counts.

## horizun_health: the verification catalog and modal-dialog detail

`include_verification_catalog=true` adds a `verification_catalog` block: for
every writing tool, its `mechanism` (the enum name from `WriteVerificationCatalog`
- `PostconditionChecklist`, `PerRowReread`, `CountReconciliation`,
`FileArtifactReread`, `DelegatedChildDeclaration`, `RemoteAcknowledgement`,
`RemoteReread`, `QueuedNotExecuted` or `SelfReported`) and `residual_gap_count` (how many known,
unfixed gaps the catalog still names for it - 0 means none are DECLARED, not that
the tool is flawless). `full_text_source` points at the source file
(`src/Horizun.Revit/Core/WriteVerificationCatalog.cs`), where every gap's own
sentence, evidence fields and source files actually live; the summary is kept
out of the default reply (`include_verification_catalog` defaults to `false`)
so an ordinary health call stays small.

**A blocked UI thread is diagnosed from a thread that is not blocked.** When
Revit's UI thread is stuck on a modal dialog, no typed command - `horizun_health`
included - can run there to answer: `ICommand.Execute` is never called, because
the ExternalEvent the bridge raises is only serviced when Revit is idle. The
"Revit has a MODAL DIALOG open" and timeout failures a queued request gets
instead come from `Dispatcher.cs`/`ModalProbe.cs`, which read the dialog's own
Win32 window from a background thread (never the UI thread) and never click or
post to it. Both failures now carry a structured `modal_dialog` block in
`structuredContent` beside the prose message:

| field | meaning |
|---|---|
| `dialog_window_found` | false means only "the main window is disabled" is known - the dialog itself could not be enumerated (it can live on another thread of the same process) |
| `title`, `class_name` | `GetWindowText`/`GetClassName` of the dialog window itself (`#32770` is the generic Windows dialog class) |
| `main_text` | best-effort: the LONGEST text found on a direct `Static`/`SysLink` child - a heuristic, because a dialog can carry several short labels besides its real message |
| `all_text` | every `Static`/`SysLink` child's text this probe could read, in enumeration order, so a caller can judge `main_text`'s guess for themselves |
| `buttons` | every `Button` child's caption, in enumeration order |
| `owning_module` | `GetWindowModuleFileName` for the dialog - which module's window class this is (Revit's own core, or a third-party add-in's dialog on the same UI thread); null when Win32 could not resolve it |

A null field means Win32 could not read it, never that the dialog has no such
thing. `modal_dialog` is absent (not merely null) when the probe itself is
unavailable or the main window is enabled.

## horizun_cde_cloud: ACC Issues (`issues_list`, `issue_create`, `issue_update`)

`provider=acc` only. The full flow (key marker, 3-legged `data:write`, read-back) is in
[INFORMATION-MANAGEMENT.md, "ACC Issues from coordination findings"](INFORMATION-MANAGEMENT.md#acc-issues-from-coordination-findings).
The arguments the schema keeps terse:

| argument | meaning |
|---|---|
| `issue` | the issue fields, every value a string: `title`, `description`, `issue_type_id` (the ACC **subtype** id - `issues_list` returns `issue_types` with their subtypes), `status` (`draft`, `open`, `pending`, `in_progress`, `completed`, `in_review`, `not_approved`, `in_dispute`, `closed`), `assigned_to`, `assigned_to_type` (`user` default, `company`, `role`), `due_date` and `start_date` (`YYYY-MM-DD`), `location_id`, `root_cause_id`. In `issues_list` only `status`, `issue_type_id` and `assigned_to` are accepted, as filters. |
| `finding` | one coordination-ledger row: a row of this bridge's own ledger (the `horizun_coordination` CSV columns or JSON keys) as it is, or another tool's row. Column aliases (case-insensitive, spaces read as `_`, first present wins): title ← `title`, `name`, `summary`, `clash_name`, `check`, else `Clash: <category_a> vs <category_b>` (the ledger has no title column); description ← `description`, `detail`, `details`, `comment`, `message`, `reason`, `note`, plus a `column: value` line for each context column (`category_a`, `category_b`, `side_a`, `side_b`, `point_mm`, `priority`, `responsible`, `immovable_discipline`, `suggested_action`, `scope`, `severity`, `discipline`, `category`, `test`, `level`, `grid`, `location`, `zone`, `element_a`, `element_b`, `element_ids`, `elements`, `distance`, `point`, `x`, `y`, `z`, `source`, `model`); key ← `external_key`, `finding_id`, `clash_id`, `issue_key`, `guid`, `id`. `issue` overrides the finding. |
| `external_key` | 1-100 characters of `A-Z a-z 0-9 . _ : -`, stored as `[horizun-key:<key>]` on the last line of the description. Required on create (or a finding id). An `issue_update` naming a key the issue does not carry yet WRITES the marker (its text kept) - it is part of the plan and of `changes`. In `issues_list` it selects the issues carrying it. |
| `issue_id` | the ACC issue id (UUID). `issue_update` also finds the issue by its key when `issue_id` is absent. |
| `dry_run`, `confirmation_token` | the writes rehearse by default; the apply sends the same arguments with `dry_run=false` and the rehearsal's token. |

Reply of a write: `state` (`rehearsed`, `applied`, `applied_unverified`,
`already_exists`, `no_change`), `plan`, `changes` (update), `issue_id`, `display_id`,
`web_url`, `verification` and `host_verified`, `mapped_from_finding`, `external_key`,
`attachments` (always "none" in this pass), `auth` and `http`. A 2-legged-only
configuration is refused before any request, with the steps to obtain a 3-legged token.

What the verdict rests on:

- **The read-back.** Every field sent is compared with a GET after the write; the
  description is compared WHOLE (line endings and outer whitespace normalised), not by
  containment, so a PATCH that never landed does not pass because the old text still
  contains the new one, and an emptied description is compared too. Nothing compared
  is never `host_verified`.
- **A lost answer.** A POST is not retried. A 5xx, a transport loss, or a 2xx whose body is
  empty or not a JSON object is reconciled by scanning for the key again (`reconciled`);
  a 2xx PATCH without a usable body goes straight to the read-back.
- **The idempotency scan** reads every issue page sorted by `displayId` (fixed at creation),
  so an issue edited during the scan cannot move to a page already read. A scan that could
  not finish withholds the token.
- **The call budget.** The dry run withholds its token when `max_calls` leaves no room for
  the write and its read-back; the apply checks the same before the POST/PATCH, so a spent
  budget is "no request was made", never a possibly-landed write.
- **The token file.** A refresh asks no scope, so APS keeps the scopes of the original
  sign-in: a read no longer leaves a `data:read`-only token behind. A stored token that is
  still valid but lacks `data:write` is refreshed for a write instead of being reused.
  (That APS v2 accepts a refresh without `scope`, and that the Issues list accepts
  `sortBy=displayId`, are to be measured live.)

Live probe: `scripts/live-probes/cde-cloud-issues.probes.ps1` (read-only cases; the apply
is `not_covered` and is run by hand, with the user's approval, against a test project).

## horizun_framing: light-gauge / drywall framing from a detail

A consultancy often models, from wall-type and ceiling DETAILS (received as images or
2-D DWG details), the framing the finished walls and ceilings hide. `horizun_framing`
builds exactly that framing from a typed **spec**. It never reads an image: the MCP
client (a vision-capable model, guided by the prompt `framing-from-detail`) reads the
detail into the spec, the person confirms it, and the tool builds and re-reads it.
Families, sizes, spacings and rules are the caller's data; nothing organisation-specific
is compiled in.

| operation | writes | what it does |
|---|---|---|
| `wall` | yes | studs, tracks, kings, jacks, headers, sills, cripples and blocking inside one layer of each straight Basic wall in `element_ids` (or visible in `view_id`, where curtain, stacked and curved walls are listed in `plan.skipped` with their reason instead of refusing the call; named in `element_ids` they refuse) |
| `ceiling` | yes | mains, cross (furring) channels, perimeter track and hangers for each Ceiling in `element_ids` (or visible in `view_id`, where sloped, multi-region and sketchless ceilings are listed in `plan.skipped` with their reason; named in `element_ids` they refuse), the hangers ray-cast to the structure above |
| `read` | no | what a previous apply produced, per source, found by the marker on each member (`member_count` counts members; `work_plane_count` counts work planes, which this build no longer creates) |
| `remove` | yes | deletes the members (and any work planes an earlier build created) a previous apply produced for `element_ids`, verified |

Every write rehearses first (`dry_run` defaults to true) and returns a
`confirmation_token` that binds the operation, the sources, the spec and the RESOLVED
PLAN: every member's role, type and both endpoints. A wall that moved, gained a door or
changed type between the rehearsal and the apply yields another plan, the token no longer
matches, and nothing is written. The apply sends the token and an `idempotency_key`.

### Example: a 92 mm (3-5/8") stud partition at 406 mm (16") on centre

```json
{
  "operation": "wall", "element_ids": [412345], "dry_run": true,
  "spec": { "wall": {
    "layer": "core",
    "stud":  { "type_id": 900101, "spacing_mm": 406.4, "start": "wall_start", "double_at_ends": false, "width_mm": 41.3 },
    "track": { "bottom_type_id": 900102, "top_same_as_bottom": true, "thickness_mm": 0.9 },
    "openings": { "king_studs": 1, "jack_studs": true, "header_type_id": 900102, "sill_type_id": 900102, "cripple_spacing_mm": 406.4,
                  "header_depth_mm": 92.1, "sill_depth_mm": 92.1 },
    "blocking": [ { "height_mm": 1200, "type_id": 900102 } ]
  } }
}
```

### Example: a suspended drywall ceiling (mains at 1200, furring at 400, hangers at 1200)

```json
{
  "operation": "ceiling", "element_ids": [523456], "dry_run": true,
  "spec": { "ceiling": {
    "main":      { "type_id": 900201, "spacing_mm": 1200, "direction": "short", "depth_mm": 38 },
    "cross":     { "type_id": 900202, "spacing_mm": 400, "depth_mm": 22 },
    "perimeter": { "type_id": 900203, "depth_mm": 22 },
    "hanger":    { "type_id": 900204, "spacing_mm": 1200, "max_length_mm": 3000, "attach": "structure_above" },
    "drop_mm": 22
  } }
}
```

### Spec fields and defaults

- **wall.layer**: `"core"` (default) puts the studs on the core's structural layer, else
  the thickest core layer, else the thickest layer (reported as `choice`); an integer is a
  compound layer index, exterior first. A membrane (zero thickness) is refused.
- **wall.stud**: `type_id` and `spacing_mm` required; `start` `wall_start` (default) |
  `wall_end` | `centred`; `max_first_bay_mm`; `double_at_ends` (default false); `width_mm`
  (the flange along the wall; default the type's published section width, refused when
  neither exists).
- **wall.track**: `bottom_type_id` required; `top_type_id` or `top_same_as_bottom`
  (default true when no top type is named); `thickness_mm` (default 0 with a warning:
  studs then run from base to top of wall).
- **wall.openings**: `king_studs` 1 (default) or 2, `jack_studs` (default true),
  `header_type_id`, `sill_type_id`, `cripple_spacing_mm`, `header_depth_mm`, `sill_depth_mm`
  (the header's / sill's section depth in z: the header's axis sits at head + depth/2 on
  the jacks and the cripples above start at head + depth; the sill's axis at sill - depth/2
  and the cripples below end at sill - depth; a blocking row inside that framed depth is
  skipped. Without a depth the axis sits ON the head / sill line, half the member in the
  void, and the plan warns `no_header_depth` / `no_sill_depth`. A header that would rise
  into the top track, or a sill that would sink into the bottom track, is not placed and
  is named in `warnings`). Hosted doors, windows and
  rectangular wall openings are read from the wall (rough size when the family publishes
  it, else nominal, else the bounding box; each opening says which in `read_from`). A stud
  station inside an opening is removed; no stud ever crosses one. A cripple that meets
  ANOTHER opening's framed void (a vent stacked over a door) is cut around it
  (`cripple_cut_by_opening:<own>:<other>`), and one that would overlap a member already
  placed (a `cripple_spacing_mm` barely above the stud width) is dropped
  (`cripple_overlaps_member_dropped:<opening>`). The post-commit `no_stud_through_opening`
  re-read allows the same 1 mm as `member_endpoints`, so a jack flush with the jamb read
  back a hair inside it is round-off, not a crossing.
- **wall.blocking**: up to 20 rows `{height_mm, type_id}`, split at the studs.
- **ceiling.main**: `type_id`, `spacing_mm` required; `direction` `short` | `long`
  (default) | an angle in degrees. **ceiling.cross** `{type_id, spacing_mm}`,
  **ceiling.perimeter** `{type_id}` optional. **ceiling.hanger**: `type_id`, `spacing_mm`
  (along each main) required; `max_length_mm` default 3000; `end_offset_mm` default half
  the spacing; `attach` `structure_above`. **ceiling.drop_mm**: from the ceiling's top face
  up to the mains' underside, default 0 (in the example the furring, 22 mm deep, sits on
  the board and the mains bear on the furring). `depth_mm` on main, cross and perimeter is
  optional: each axis sits half its depth above the face it bears on; without it the axis
  sits ON that face and the plan says so in `warnings`.

### Ceiling geometry

- **Boundary**: the ceiling's own sketch, every loop chained end to end (arcs tessellated),
  the largest loop first; holes are honoured. A sketch with a second region outside the
  largest one is refused (split it into one ceiling per region), and so is a sloped
  ceiling (its box taller than the type's compound width + 1 mm). Under a `view_id` scope
  either one is listed in `plan.skipped` instead, and the call refuses only when every
  ceiling the view shows was skipped.
- **Heights** (model z, mm): cross and perimeter axes at top face + depth/2; mains at top
  face + `drop_mm` + depth/2; each hanger from the mains' top face up to its support.
- **Hangers**: one ray straight up per station, from the mains' top face, against floors,
  structural framing and roofs of the host and of loaded links, in a temporary 3-D view
  that is always rolled back (no template, filters or section box; the ceiling's phase).
  The nearest hit within `max_length_mm` is the support and its distance the rod; a
  horizun_framing member is never a support. A station with nothing above is listed in
  `no_support_above` (main index and point) and NOT placed; the counts, the signature
  and the confirmation token are those of the hangers that will exist.
- **Verification** adds `inside_boundary`: every member end within 1 mm of the sketch
  boundary (holes count), and reports per ceiling `hanger_supports` (support -> hangers).

Spec errors come back together, before anything is read from the model, as
`{path, code, detail}` with code `missing`, `not_object`, `not_integer`, `not_number`,
`not_boolean`, `below_minimum`, `above_maximum`, `bad_value`, `conflict`, `not_array` or
`unknown_field` (the reply's `code` is `invalid_spec`, `write_started` false). Spacings are
bounded to 10..20000 mm, so a spacing typed in inches or metres refuses arithmetically
instead of placing millions of members; a call plans at most 5000 members per source and
20000 in total.

### Category and placement rules

The member's family decides how it is placed, and a family that cannot take the member's
orientation is refused by name before anything is written:

- **Structural Framing** places as a beam (`StructuralType.Beam`) on a HORIZONTAL axis only:
  tracks, headers, sills, blocking, mains, cross, perimeter. Its z-justification is set to
  centre so the axis is the member's centreline, and its automatic joins are switched off
  at both ends: a track or a blocking piece ends where the plan says, against a stud's
  face, instead of being cut back or extended by Revit to meet a column or another beam.
- **Structural Columns** place as a column (`StructuralType.Column`) on a VERTICAL axis only:
  studs, kings, jacks, cripples, hangers.
- **Line-based Generic Model** places HORIZONTAL members only, on the source's level
  (`NewFamilyInstance(Curve, FamilySymbol, Level, StructuralType)`), with the height put back
  from the axis. A vertical member of this kind is refused by name: standing a line on a
  created reference plane or sketch plane was refused by Revit for every member ("does not
  coincide with the input face", at 0.000 mm off the plane; MEASURED 2026-09-26 in Revit
  2026) because that overload hosts on a FACE of an element. Studs, kings, jacks, cripples
  and hangers therefore need a Structural Columns type.

The Revit API documents no orientation rule for `NewFamilyInstance(Curve, FamilySymbol,
Level, StructuralType)`, so the tool relies on none: which placements Revit commits and
whether the committed axis keeps the planned ends is what `framing.probes.ps1` measures.
MEASURED 2026-09-26 in Revit 2026 (10/10): a line-based member's location curve stays on
its level and its height is the `Offset from Host` parameter, so the re-read adds it
(`endpoint_read: location_curve_plus_level_offset`); a column placed on a vertical line
reports a location curve (its real axis), not a point with base and top constraints.

### Verification, markers and idempotence

After the commit the tool re-reads, by marker, every planned member: its type, both
endpoints within 1 mm of the plan (a vertical column's ends are read from its base and
top constraints), `|y|` inside the carrying layer, no vertical member inside an opening's
void, counts per role equal to the plan, every hosted insert of the wall with the same
type and location as before, and no member geometry-joined with its source wall
(`source_unjoined`). Revit joins some column families with the wall they stand in and cuts
the wall by them; a stud never cuts the partition it frames, so such a join is undone inside
the write and counted in `evidence.source_joins_undone`. Any disagreement rolls the whole
edit back.

Each member (and work plane) carries an extensible-storage marker naming its source
element (id and UniqueId), role, plan index, spec hash and plan signature. A second apply
of the same spec on the same wall is `already_applied`: nothing is created and the
existing members are re-read against the plan. Framing from another spec on the same
source is refused until `operation=remove` takes it away. Hand-modelled framing carries no
marker and is never read, claimed or removed.

The marker also records the member's OWN UniqueId. Extensible storage travels with a copy,
so a wall copied or arrayed together with its framing brings members whose marker still
names the original wall; their UniqueId no longer matches, so they are **foreign copies**:
`remove` keeps them (`plan.foreign_copies_kept`, `evidence.foreign_copies_kept`), the
idempotence and the re-read never count them, the spatial check never excuses them, and
`read` lists them apart (`foreign_copies`, `foreign_copy_count`).

`remove` measures, in a rolled-back transaction, what Revit deletes WITH the members (tags,
dimensions, anything hosted on the tool's work planes): the rehearsal shows it in
`plan.cascade` (count, by category, ids), the token binds it, and after the commit
`cascade_absent` and `cascade_as_measured` re-read it (`evidence.cascaded_ids`); a cascade
the token did not bind rolls the whole remove back.

A ceiling's hangers are re-cast after the commit: one ray up from just under each rod's top
must meet a floor, framing or roof within 1 mm of that top (`hanger_reaches_support`,
`evidence.hanger_recheck`).

**The automatic spatial check** (`spatial_check` after every write) treats a framing
member and its own source wall or ceiling as an EXPECTED intersection (reason "framing
inside its own source"), read from the marker: studs inside the wall they frame are what was
asked for. Two members of one source are judged by the normal rules: studs meeting tracks
share no volume (no finding), but mains running through cross members (`drop_mm` under the
cross depth) or any real overlap between members is reported. A member touching anything
else is still reported.

**Live probe** (`scripts/live-probes/framing.probes.ps1`, offline twin
`framing.tests.ps1`). On its own level it authors a line-based Generic Model member,
frames an own compound wall with a door and a window (rehearsal, verified apply, second
apply `already_applied`, read, remove), then an own ceiling 600 mm under an own floor
(rehearsal and verified apply: `inside_boundary` matched and every hanger's support is
that floor, by id) and a second ceiling with nothing above (rehearsal: every station
`no_support_above`, no hanger planned). The ceiling framing is removed with
`operation=remove` before the staging is deleted, because its members are not hosted by
the ceiling. A last case frames a wall at 45 degrees with the document's own Structural
Columns type as studs and Structural Framing type as tracks (the Column and Beam placements;
columns re-read from their constraints, beams from their curves) and is `not_covered`, named,
when the document carries neither category.

## horizun_manage_views: `renumber_sheets` (a register-wide map)

`renumber_sheets` renumbers many sheets at once from a map `old number -> new
number`, as ONE action inside the batch's single transaction:

```json
{ "operation": "renumber_sheets",
  "renumber": { "A101": "A102", "A102": "A101", "A103": "A110" } }
```

- **Collisions are refused before anything is written, all at once**: an old
  number no sheet holds, an old number named twice, two sheets sent to the same
  number, a new number held by a sheet the map does NOT move (checked against
  EVERY sheet, placeholders included), and a number another action of the same
  batch creates. Numbers compare case-insensitively, like the create operations.
- **Swaps and cycles are allowed.** Revit refuses a number another sheet still
  holds at the moment of assignment, so the steps are ordered: a sheet moves
  straight to its target as soon as it is free, and each closed cycle parks ONE
  sheet on a temporary `HZTMP-n` number that no sheet or target holds. A swap
  costs one extra step; a shifted series (`A101->A102->A103->A104`) costs none.
- **The rehearsal shows the plan**: `plan[i].renumber` lists `final` (sheet id,
  from, to), the ordered `steps` with `temporary` flags, `temporary_steps` and
  `unchanged` (entries whose new number equals the old one exactly).
- **The token binds the whole register** (every sheet's UniqueId and number):
  a sheet renumbered by anyone between rehearsal and apply refuses as stale.
- **After the commit every sheet is re-read**: `rows[i].renumber.renumbered[]`
  carries `reread` and `verified` per sheet; a single mismatch fails the action
  and the batch rolls back before commit.
- One `renumber_sheets` per batch (merge the maps). Its targets are reserved for
  the rest of the batch; the numbers it frees are NOT offered to a later
  `create_sheet` in the same batch, which checks the document as it was.
- Why not `horizun_fix_planimetry set_sheet_number`: that one corrects a cited
  finding and refuses a number another sheet holds, which a swap needs.
