# Export correctness remediation plan

Status: Implementation complete, locally validated, and reviewed by Opus 5.5 with no unresolved findings. Planning and implementation reviews each reached consensus after two rounds on 2026-10-09.

Baseline: `d911d9c` on `chore/review-code`, matching the locally available `origin/main`. The working tree was clean before this document was added. There are no repository-specific `AGENTS.md` files or existing test projects in this checkout.

## Objective and scope

Correct the six defects identified in the repository review while retaining the existing CLI options, unqualified SQLite destination names, wildcard syntax, type conversions, and batching behavior. Deliver behavioral regression tests at the SQL Server, SQLite, and CLI boundaries.

This plan was created under planning-only authorization. The user authorized its implementation and an Opus 5.5 review on 2026-10-09, then authorized committing and pushing directly to `main`. Opening a PR or deploying remains outside the requested scope.

The previous review reproduced all six defects with synthetic data in an isolated SQL Server LocalDB instance and SQLite files. `dotnet build .\siphon.slnx --nologo` passed with zero warnings/errors. The temporary SQL database and instance were removed. Those reproductions establish the baseline defects; they do not validate the proposed fixes.

The baseline restore resolved `System.CommandLine` 2.0.12, `Microsoft.Data.Sqlite` 10.0.12, and `Microsoft.Data.SqlClient` 7.1.1. Application references currently float with `Version="*"`; retain their existing declarations in this scoped change, record versions used for red/green validation, and recheck API assumptions if a restore resolves different versions. Package pinning is a separate reproducibility decision.

| ID | Defect and active code | Reproduced outcome |
| --- | --- | --- |
| F1 | `SqlServerReader.GetTableSchemaAsync` discards the schema in `DestinationTable`; `SqliteWriter.CreateTableAsync` drops that table on every import. | Selecting `dbo.Customer` and `sales.Customer` reports two tables processed but retains only the second table's row. |
| F2 | `Program.RunAsync` initializes the writer even when wildcard expansion selects nothing. | An unmatched wildcard with `--overwrite` deletes the existing database, creates an empty replacement, and exits 0. |
| F3 | The per-table `InvalidOperationException` handler continues to the unconditional successful summary. | An explicitly requested missing table logs an error but exits 0. |
| F4 | Schema discovery includes hidden temporal columns while `ReadTableDataAsync` uses `SELECT *`; the writer reads by ordinal. | A temporal table with hidden period columns fails with an index-out-of-range error. |
| F5 | SQL Server and SQLite identifiers are interpolated between brackets without escaping. | A column named `Label]` aborts export with a SQLite token error. |
| F6 | `Distinct()` compares unresolved selection strings. | `-t Customer -t "dbo.Cust*"` exports `dbo.Customer` twice and doubles the summary counts. |

## Agreed behavior to implement

### 1. Preflight the complete selection before touching the output

Perform connection validation, table resolution, deduplication, schema loading, destination table/column collision checks, and reserved destination table-name checks before constructing/initializing `SqliteWriter`. A preflight failure must return exit code 1 without creating a missing output file, changing an existing file, or creating its parent directory.

- Preserve the exact-name grammar: a bare name means `dbo.<name>`; an exact qualified name is split once at the first dot, as today. Do not introduce a bracketed-name parser or additional CLI syntax in this work.
- Continue matching wildcard patterns against the displayed full `schema.table` name using the existing matcher. Preserve the existing warning when one pattern has no matches but another selection yields valid tables.
- If the final selection is empty, report `No tables matched the requested selection.` and fail before writer initialization, with or without `--overwrite`.
- A missing exact table fails the entire preflight, including a mixed valid/missing selection. Preserve the existing message `Table '<schema>.<table>' does not exist or has no columns.` This intentionally replaces the current misleading partial-success behavior.
- Load every selected table's column mapping before output initialization. Schema-query failures are fatal preflight errors.
- Preflight is validation of observed metadata, not a schema lock or a promise of a consistent source snapshot. A later source/schema change must fail honestly; do not add transaction-wide source locking.

### 2. Resolve and deduplicate actual SQL Server objects

Introduce a small typed source identity containing database-local `object_id`, canonical schema name, and raw table name. Obtain discovery identities from `sys.tables` joined to `sys.schemas`; resolve exact selections with parameterized schema/table predicates against that same catalog. Let SQL Server apply its identifier comparison rules rather than approximating its collation in .NET.

- Deduplicate by `object_id` across every selection path, including runs with no wildcard. Preserve first-selection order and discovery order within each wildcard expansion.
- Resolve a bare `Customer`, `dbo.Customer`, and an overlapping wildcard to the same object once. On a case-insensitive database, case variants resolve once. On a case-sensitive database, different objects must remain different even when their names differ only by case.
- Carry schema and table names as separate fields from discovery through reading; never parse a generated display name to recover the identity. This also keeps wildcard-discovered names containing dots intact.
- This tool selects base tables. An exact view is not a valid table selection and must fail preflight rather than producing the current empty, apparently successful export.
- Retain `TableMapping.SourceTable` as a display value. Add the raw source table name/object identity needed by the reader, or carry the identity alongside the mapping; the implementation must not derive executable identifiers from `SourceTable`.

### 3. Reject destination collisions while retaining existing names

Keep `DestinationTable = <raw source table name>`. Reject a run containing two distinct source objects whose destination names collide under SQLite identifier comparison before initializing the writer. Do not automatically rename tables or merge their contents.

- Apply SQLite's ASCII case-insensitive identifier comparison. Do not use unrestricted .NET `OrdinalIgnoreCase`, which could reject distinct non-ASCII identifiers that SQLite allows. Protect this comparison with execution tests against SQLite, including ASCII case variants and a non-ASCII case pair.
- A duplicate reference to one source object is deduplicated, not treated as a collision.
- A collision error must name the destination, list its conflicting canonical source names in deterministic order, and advise exporting them to separate output files. Exit 1 and preserve the output.
- After loading every mapping, use the same SQLite identifier comparer to reject colliding destination column names within each table. A legal case-sensitive source such as `dbo.T(Id int, id int)` cannot become a SQLite table. Name the canonical source table and conflicting source columns in mapping ordinal order; exit 1 before writer initialization. Do not rename or drop either column.
- Reject destination table names beginning with the ASCII case-insensitive prefix `sqlite_`, which SQLite reserves for internal objects. Identify the source and reserved destination in the error and exit 1 before touching the output. This is a known name-compatibility guard, not a promise to preflight every possible SQLite storage/runtime failure.
- Preserve the current behavior of replacing selected destination tables in a valid existing database when `--overwrite` is absent. Detecting historical provenance of tables already in an output file is outside this fix; there is no provenance metadata in the current format.

### 4. Read precisely the mapped columns

Replace the active `SELECT *` data path with an explicit projection built from the same ordered `TableMapping.Columns` consumed by the writer. Pass the validated mapping/identity to `ReadTableDataAsync` and update its only application caller.

- Preserve `ORDINAL_POSITION` ordering explicitly in the mapping rather than depending on dictionary enumeration as the order contract. A dictionary may still be used to mark primary-key columns.
- Include hidden temporal period columns in the export. Explicit SQL Server projection makes them readable; keep their existing datetime conversion and SQLite storage types.
- Preserve `CommandBehavior.SequentialAccess`, streaming, cancellation support already present in the reader, and parameterized values in the writer.
- A temporal test must compare ordinary and period-column values with the source, including a fixture with period columns between visible columns. Do not merely assert that extraction no longer throws.

### 5. Quote identifiers for each SQL dialect

Add small shared quoting helpers, or equivalent centralized private helpers, with separate SQL Server and SQLite behavior.

- SQL Server: wrap each raw schema/table/column identifier in `[` and `]`, doubling every embedded `]`. Quote the schema and table separately.
- SQLite: use double-quoted identifiers and double every embedded double quote. Do not attempt to escape SQLite bracket syntax with SQL Server rules.
- Use the helpers for the reader's explicit projection and qualified source name, and every writer `DROP`, `CREATE`, `INSERT`, column definition, and primary-key column reference.
- Continue binding metadata predicates and inserted values as parameters. Quoting identifiers does not replace value parameters.
- Cover table, schema, and column names containing `]`; SQLite identifiers containing double quotes; spaces and reserved words; and a primary-key column with punctuation. Verify exact names and round-tripped rows through executed queries.

### 6. Propagate truthful CLI results

Make `RunAsync` return `Task<int>` and propagate its result through the supported `System.CommandLine` asynchronous action/invocation APIs to the top-level process return value. The baseline package supports `SetAction(Func<ParseResult, CancellationToken, Task<int>>)` and `return await rootCommand.Parse(args).InvokeAsync();`; the one-argument `Task<int>` action overload also exists. Remove the `Environment.Exit` calls from this execution path so owned connections and transactions are disposed normally. Preserve existing reader cancellation support; a new end-to-end Ctrl+C policy is outside these six fixes.

- Return 0 only when the nonempty, validated selection is exported completely. Keep `Extraction completed successfully.` for that result.
- Connection, preflight, SQL Server, SQLite, and unexpected fatal failures return 1 with the existing relevant error diagnostics.
- Retain the current continue-on-`InvalidOperationException` behavior for failures during an individual table transfer, but count those tables as failed, omit the successful completion message, emit `Extraction completed with errors.`, and return 1. Other fatal exception classes still abort the run.
- Report processed tables and total rows using successfully completed, deduplicated table exports. If applicable, also report the number of failed tables. Distinguish partial output from a successful complete extraction.
- Preserve batch commits: a transfer failure can leave already committed batches in a partial output file. Whole-export atomic replacement and rollback of all prior tables/batches are outside this plan. Document this limit rather than presenting preflight as full failure atomicity.

## Implementation sequence and files

### Step 1: Establish behavioral regression tests

Add `tests/Siphon.Tests/Siphon.Tests.csproj` with xUnit and a compatible test SDK/runner at explicit stable versions, plus its entry in `siphon.slnx`. Choose package versions during implementation and verify restore compatibility with .NET 10; do not upgrade unrelated application dependencies.

The root SDK project currently recursively includes files. Append `;tests/**` to `DefaultItemExcludes` in `siphon.csproj`, preserving `$(DefaultItemExcludes)`, so the test project's sources, resources, and generated `bin/obj` contents stay outside the executable project. Keep test fixtures synthetic and SQLite files in unique temporary directories outside the repository.

- Keep pure selection/collision and SQLite writer tests independent of SQL Server. Tag SQL Server tests, including process-level export tests that require SQL Server, with `Category=SqlServerIntegration`; keep `--help` outside that category.
- Launch `dotnet <built-output>/siphon.dll` directly for process-level CLI tests with `ProcessStartInfo.ArgumentList`, asynchronous draining of stdout/stderr, and a bounded timeout that kills the test-owned process tree on expiry. Redirect stdin and close it immediately, and supply all export arguments (`-s -d -t -o -w`) so tests cannot enter interactive prompts. Assert exit code and destination database state.
- For SQL integration tests on Windows, provision a uniquely named, test-owned LocalDB instance/database and seed only synthetic tables. Fixture cleanup must target only its own database/instance, including on failed tests. Do not reuse AMS or any existing user database. Missing LocalDB is an explicit prerequisite failure for this suite, not a silently passing test.
- Capture meaningful red results for the six defects before changing application behavior. Some new helper tests cannot compile before helper introduction; these supplement rather than replace baseline behavior reproductions.

### Step 2: Add selection preflight and object identity

Owned files: `Program.cs`, `Services/SqlServerReader.cs`, `Models/TableMapping.cs`, and small new source-identity/selection-planning files under `Models/` or `Services/` as needed.

Extract only enough selection/preflight logic to test ordered deduplication and collision rejection directly. Keep new helper types internal and grant `InternalsVisibleTo("Siphon.Tests")`; do not make implementation details public merely for tests or try to test top-level local functions directly. Avoid a generic service layer or a DI container. Keep output initialization after a completed, nonempty preflight plan, including per-table destination-column checks and the reserved table-name guard. Resolve F1, F2, and F6 together, and the missing-selection portion of F3.

### Step 3: Fix projection ordering and identifier quoting

Owned files: `Services/SqlServerReader.cs`, `Services/SqliteWriter.cs`, `Program.cs`, mapping/quoting helpers, and regression tests.

Implement the explicit mapped projection and dialect-specific quoting together, then demonstrate temporal and punctuation round trips. Ensure ordinary tables, nullable values, single/composite primary keys, and multi-batch transfers still behave correctly. Do not change `TypeMapper` semantics.

### Step 4: Complete exit-code and summary handling

Owned files: `Program.cs`, a narrow extraction-result type/helper only if needed for testing, and CLI tests.

Propagate return codes, retain the defined runtime exception policy, and test truthful complete/partial/fatal results. Ensure cleanup occurs by normal disposal when failures return to the command action.

### Step 5: Document and review the final change

Owned files: `README.md` and tests/docs directly supporting this plan.

Document collision rejection, mixed unmatched-pattern warnings, failure exit codes, preflight output protection, inclusion of hidden temporal columns, and the partial-output limitation after transfer starts. State explicitly that, after successful preflight, `--overwrite` deletes the previous database during writer initialization; a later transfer failure does not restore it. Keep existing options and examples valid. Add test commands and prerequisites. Review the implementation with Opus 5.5 and reconcile concrete objections before considering it ready.

## Required behavioral coverage

| Case | Expected fixed behavior |
| --- | --- |
| Two same-named tables in different schemas, selected exactly or through `*.Customer` | Exit 1 before output creation/modification; error identifies both sources. Test existing output with `--overwrite` and without it. |
| Distinct objects named `Customer` and `customer` in a case-sensitive source database | They remain distinct identities; their SQLite destination collision is rejected. |
| One case-sensitive source table containing `Id` and `id` | Exit 1 before output modification; diagnostic names the table and both columns in ordinal order. Existing sentinel rows/file bytes survive with and without `--overwrite`, including a mixed selection containing a valid table first. |
| Source table with a destination name beginning `sqlite_`, including ASCII case variants | Exit 1 before writer initialization; source/destination are identified and existing output survives with and without `--overwrite`. |
| Distinct SQLite-compatible non-ASCII destination names | Collision comparison agrees with actual SQLite identifier behavior; no overbroad Unicode case folding. |
| Every wildcard is unmatched | Exit 1; existing sentinel rows/file bytes survive `--overwrite`; missing output and parent directory remain absent. |
| One unmatched pattern plus a valid selection | Warning remains; selected table exports successfully, exit 0. |
| Missing exact table, alone and mixed with a valid table | Exit 1 before output modification; preserve the existing complete missing-table diagnostic. |
| Bare, qualified, repeated exact, and overlapping wildcard references to one object | One transfer; unique processed count and total row count equal actual exported rows. Include a no-wildcard duplicate case and case variants on a case-insensitive source. |
| Normal table and temporal table with hidden period columns interleaved with visible columns | Exit 0; all mapped columns and source values appear in their corresponding SQLite columns. |
| Table/schema/column names with `]`, double quotes, spaces, or reserved words | Exit 0; exact names and values round-trip. Include punctuated PK columns and recreation of the same table to exercise `DROP`. |
| Empty table, nullable values, composite PK, and more than 1,000 rows | Existing mapping/constraint behavior preserved; complete row transfer and success exit. |
| Runtime `InvalidOperationException` after a valid preflight | Failed-table count, exit 1, and errors completion diagnostic; no complete-success diagnostic. A deterministic test stream/progress seam may be used without adding production failure switches. |
| Fatal SQL/SQLite failure after transfer begins | Exit 1, normal disposal, no success diagnostic; document and inspect any committed partial output. |
| Successful single-table CLI invocation and `--help` | Exit 0; existing arguments and prompt behavior remain usable. |

Output-protection tests must close/checkpoint seed connections before hashing files and disable pooling or clear test-owned pools before file cleanup. Inspect SQLite table contents as well as file bytes. Avoid source-text, SQL-string-fragment, or markup tests as substitutes for these behaviors.

## Validation commands and acceptance

Run each new regression in red-green order, then the narrow suite and full relevant suites:

```powershell
dotnet restore .\siphon.slnx
dotnet build .\siphon.slnx --no-restore --nologo
dotnet test .\tests\Siphon.Tests\Siphon.Tests.csproj --no-build --filter 'Category!=SqlServerIntegration'
# Windows host with SqlLocalDB available; creates only test-owned fixtures:
dotnet test .\tests\Siphon.Tests\Siphon.Tests.csproj --no-build --filter 'Category=SqlServerIntegration'
dotnet run --project .\siphon.csproj --no-build -- --help
git diff --check
```

Tests that launch the executable must use the built project output, not install/publish artifacts. Use bounded fixtures and test timeouts; include fixture setup and cleanup failures in test results. A passing pure/SQLite suite does not establish SQL Server or CLI fixes if the integration suite could not run.

Done means all six findings have behavioral protection; output preservation is proven for preflight rejection; temporal and quoted-identifier data is compared with the source; success/failure exit codes are verified through real child processes; the relevant build/tests pass; only intended files change; and the final Opus 5.5 review has no unresolved correctness objections. Any unrun checks or environment restrictions must be stated explicitly.

## Planning review record

### Round 1: Opus 5.5 (`claudeAgent` / `claude-opus-5-5`)

Review task/client request: `siphon-remediation-plan-opus55-review-r1-20261009`. The reviewer inspected the plan, application callers/models, SQLite writer, SQL Server reader, project/solution files, README, and resolved package APIs. Its verdict was that all six original defects are covered, with one material objection before implementation readiness.

- **M1 accepted:** Extend the destination-collision preflight to columns as well as tables. Added rejection of SQLite-equivalent column names and case-sensitive source tests that preserve output with/without `--overwrite`, including a valid table selected before the invalid one. The primary agent confirmed SQLite rejects `Id`/`id` columns and permits a non-ASCII `É`/`é` pair through executed in-memory Python SQLite statements; the implementation suite must verify the project's bundled SQLite too.
- **Accepted bounded refinements:** Guard SQLite-reserved `sqlite_` table names during preflight; exclude the whole nested test tree from root default items; specify internal test access and direct, noninteractive process launch; record baseline dependency versions; clarify that overwrite removes the old file before data transfer. These close known validation/test gaps without changing destination names or introducing new CLI options.
- **Deferred broader suggestions:** Do not introduce a new end-to-end cancellation policy, remove the approximate-row-count gate without a separately established requirement, or rewrite every metadata query to use object IDs. The six findings can be corrected with canonical names, existing reader cancellation support, and the stated error policy. No source locking or whole-export rollback is implied.
- **Primary-agent assessment after round 1:** The revised design addresses M1 and retains the agreed scope. Requested a fresh Opus 5.5 review to confirm readiness and check the refinements.

### Round 2: Opus 5.5 (`claudeAgent` / `claude-opus-5-5`)

Review task/client request: `siphon-remediation-plan-opus55-review-r2-20261009`. The reviewer rechecked the revised plan against the baseline code, explicitly confirmed M1 was resolved, and agreed the plan is ready to implement with no remaining blocking objections.

- Confirmed the shared ASCII-only SQLite comparer covers both table and column collisions, checks precede writer construction/initialization, and preservation tests include both overwrite modes and a valid table selected first.
- Confirmed the reserved-name guard, complete test-tree exclusion, bounded noninteractive CLI tests, integer exit-code propagation, normal resource disposal, and overwrite/partial-output documentation.
- Noted that column diagnostics must follow the same ordinal mapping order and fresh synthetic fixtures should supply stable row counts. Both fit the existing requirements; no further design change was requested.
- **Consensus:** The primary agent agrees with the final reviewer verdict. All six original findings and the bounded preflight refinements have defined behavior, implementation ownership, and behavioral acceptance criteria. No unresolved planning objections remain. This consensus approves the plan's technical readiness, not implementation, a commit, or publication.

## Implementation record

Implemented source-identity resolution and ordered deduplication, complete selection/schema preflight, table/column collision and reserved-name rejection, explicitly ordered projections including hidden temporal columns, dialect-specific identifier quoting, and integer CLI results with normal resource disposal. Existing destination names, type conversions, and batch behavior remain as specified. Public string-based reader entry points remain available and delegate to the corrected paths. The README documents exit codes, preflight preservation, partial output, and test prerequisites.

Added a .NET 10 xUnit test project with pinned test dependencies, scoped default-item exclusion in the root project, and direct CLI invocation from copied build output. SQL fixtures use their own unique LocalDB instance and synthetic databases; teardown drops those databases and removes that instance, including attempted cleanup after setup errors.

Validation performed on 2026-10-09:

- Baseline SQL Server/CLI regressions before application fixes: 23 failed and 4 passed, reproducing all six original defects and the additional preflight gaps.
- `dotnet build .\siphon.slnx --no-restore --nologo`: passed, zero warnings/errors.
- SQLite/unit suite (`Category!=SqlServerIntegration`): 10 passed, zero failed/skipped.
- SQL Server/CLI suite (`Category=SqlServerIntegration`): 31 passed, zero failed/skipped after the review correction. Includes CI/CS source catalogs, hidden temporal-value comparison, punctuation round trips/recreation, canonical identity and selection ordering, collision/empty/missing/reserved output protection, marked-base-table selection compatibility, connection failure, and fatal SQL-read partial output.
- Runtime failure tests execute the real SQLite writer: a recoverable stream failure retains committed batches, rolls back the incomplete batch, continues the next table, and returns an errors result; a fatal SQLite constraint failure aborts later tables.
- `dotnet run --project .\siphon.csproj --no-build -- --help`: passed, exit 0.
- `git diff --check`: passed. Application packages remain at the baseline resolved versions recorded above.

An initial overlapping test build encountered a Windows test-host DLL lock; after the host finished, the suites were built/run sequentially and passed. No live database, development secret, commit, or external publication was involved.

### Implementation review round 1: Opus 5.5

Review task/client request: `siphon-implementation-opus55-review-r1-20261009`. The reviewer inspected every tracked diff and untracked implementation/test file, independently ran the build and 10-test SQLite/unit suite, and found no material correctness objections. It confirmed all six findings, the preflight refinements, public entry points, resource lifetimes, and test isolation conform to the approved plan. The initial 29-test SQL suite result available to that review was the primary agent's validation, not a second SQL run by the reviewer.

- **P3 scope refinement resolved:** Removed the additional `is_ms_shipped = 0` predicate from exact resolution and discovery; the plan did not authorize narrowing the base-table selection set. Added two real CLI regression cases using a synthetic table marked with the SQL Server system-object procedure. Tests explicitly confirm that this table is still a base table in the original `INFORMATION_SCHEMA.TABLES` catalog. Both cases failed with the new predicate and passed after removal. This verifies catalog compatibility without assuming CDC/replication behavior.
- **Optional view-test refinement accepted:** The exact-view rejection test now also verifies the complete missing-table diagnostic, alongside exit code 1 and absence of an output file.
- **Optional build-layout refinement deferred:** The test copy target uses this repository's current `bin/<configuration>/net10.0` output. Framework/output-layout changes are outside this diff; no stale artifact was used in validation.

### Implementation review round 2: Opus 5.5

Review task/client request: `siphon-implementation-opus55-review-r2-20261009`. The reviewer confirmed the selection-scope correction is complete in both queries, the marked-base-table regression tests establish the original catalog's behavior and execute both CLI selection paths, and the strengthened exact-view test verifies the intended failure. It found no qualifying defects and explicitly stated that the final implementation is ready.

The second round was a static follow-up review, not another test run. The primary agent's final clean build, 31-test SQL Server/CLI suite, and the 10-test SQLite/unit suite establish local validation; the reviewer independently built and reran the 10-test suite in round 1. Fixture marking uses an undocumented system procedure only in the test-owned LocalDB instance; moving these fixtures to a shared/non-admin SQL Server would require a separate fixture design.

All implementation review findings are resolved. The user subsequently authorized committing and pushing the completed work directly to `main`. No production deployment, live-data proof, or PR was requested or performed. The documented preflight guarantees and partial-output limitations remain in effect.
