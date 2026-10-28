# Architecture Decision Log

## 2026-09-13 17:45:01 PDT - Read grades through a dedicated endpoint

Decision:
Read `currentGrades.json` through `CurrentGradesProvider` and expose only the dashboard fields from `/api/current-grades`. Keep this data separate from `/api/state` and the MikroTik service.

Reasons:
- The grades file is an independent local data source with a different update lifecycle.
- A separate endpoint lets a grades-file error remain visible without breaking access-control state refreshes.
- Returning only the requested fields avoids exposing teacher contact data to the browser unnecessarily.

Telemetry:
None. This decision is based on the requested JSON contract and the existing application boundaries.

## 2026-09-13 17:45:01 PDT - Treat grade percentages as fractions

Decision:
Validate `averagePercentage` in the inclusive range 0 through 1, display it as a percentage, and highlight only non-null values below 0.85.

Reasons:
- The provided values use fractional representation, for example 0.842 for 84.2 percent.
- Null means that a class has no grade and must not be presented as a failing grade.

Telemetry:
Sample input contains five numeric values between 0.833 and 1.0 and two null values.

## 2026-09-13 20:46:08 PDT - Apply grade limits only to future access changes

Decision:
Evaluate the grade policy when building UI state and when handling a new access request or manual extension. Do not shorten, end, or otherwise modify an existing session when grades change. Do not add grade-policy work to the background sweep.

Reasons:
- An already granted session should remain predictable for the child and parent.
- Applying the rule only at an explicit request boundary avoids a new race between the background sweep and manual session changes.
- If an existing session exceeds the new 300-minute limit, reject an extension before contacting MikroTik and leave the session unchanged.

Telemetry:
The focused service regression test confirmed that an existing 600-minute session remains unchanged and no MikroTik request is made when a restricted extension is rejected.

## 2026-09-13 20:46:08 PDT - Keep grade policy stateless in version one

Decision:
Use `currentGrades.json` as the source of truth and do not create a grade-policy SQLite table. Support one policy-enabled user, use a shared typed file reader for display and policy consumers, and use a 14-day TTL for a normal report. A stale report with a low grade remains restricted; a stale normal report blocks only new requests.

Reasons:
- The current Canvas report is delivered approximately weekly and copied into the file manually.
- A 14-day TTL tolerates one missed weekly update without trusting a normal report forever.
- No persisted policy transitions or policy emails are required for the initial behavior.
- A single root `userName` in the source file can unambiguously represent only one policy-enabled user.

Telemetry:
The evaluator test matrix covers normal, restricted, exact-threshold, stale-normal, stale-restricted, invalid timestamp metadata, and local-time conversion cases. The full suite passed 28 tests after implementation.

## 2026-09-13 20:46:08 PDT - Separate effective limit from hard cap

Decision:
For normal grades, use the configured day limit as the effective limit and `day limit + graceMinutes` as the hard cap. For restricted grades, use 300 minutes as both the effective limit and hard cap. Do not expose a separate grade-policy grace flag.

Reasons:
- The request UI and requested window are bounded by the effective limit.
- Grace remains compatible with existing normal behavior but cannot extend the restricted daily cap beyond five hours.
- Removing the flag avoids a configuration option with little visible effect and keeps the restricted rule exact.

Telemetry:
Unit tests verify a normal 600-minute limit with a 615-minute hard cap and a restricted 300-minute limit with a 300-minute hard cap.

## 2026-09-13 20:46:08 PDT - Refine the grades and state API boundary

Decision:
Keep the raw grades display on `/api/current-grades`, but allow `/api/state` to expose only the derived grade-policy status, effective limit, hard cap, source time, and low-grade identifiers needed by the access UI. Both consumers use `CurrentGradesFileReader`; neither consumes the other's HTTP contract.

Reasons:
- The earlier decision to keep grades entirely separate from `/api/state` was too broad once grades became an explicit input to access decisions.
- Returning the derived policy state lets the UI explain why a new request is restricted or unavailable without exposing teacher contact data.
- A shared typed reader prevents the dashboard and policy code from parsing the same file differently.

Telemetry:
The `/api/current-grades` smoke request returned HTTP 200 with the expected `userName`, `asOf`, and seven dashboard rows. Inline UI JavaScript syntax validation passed.

## 2026-09-13 21:12:55 PDT - Render the grades table as cards on narrow screens

Decision:
Keep one semantic grades table and change only its CSS presentation at viewport widths up to 640 pixels. Each table row becomes a card with the class on the left, the mark and percentage on the right, and missing assignments plus the last update below it. Preserve the existing low-grade highlight and table presentation on wider screens.

Reasons:
- Reusing the same table rows avoids duplicate DOM, rendering logic, and accessibility content for desktop and mobile views.
- A 640-pixel breakpoint targets phone-sized screens without changing the existing tablet and desktop table behavior.
- The API contract does not expose teacher data, so the cards use exactly the fields already shown in the table.

Telemetry:
Chromium mobile emulation at 390 pixels reported a 390-pixel document width with no horizontal overflow; a grade card occupied 348 pixels and its right-side mark remained inside the viewport. At 1280 pixels, Chromium reported the grades elements as `table`, `table-row`, and `table-header-group`.

## 2026-09-13 21:23:15 PDT - Isolate responsive rules by table

Decision:
Scope the existing 760-pixel mobile rules to the access-control table and wrapper. Keep the grades-specific card layout limited to 640 pixels, and preserve its visually hidden table header instead of removing it from the accessibility tree.

Reasons:
- The earlier grades-card change did not account for the later, unscoped 760-pixel media rules, so those rules still changed the grades table between 641 and 760 pixels.
- The unscoped `thead { display: none; }` also overrode the accessible hiding technique used for the grades header at phone widths.
- Explicit table classes prevent future responsive changes for one table from leaking into the other.

Telemetry:
Chromium reported the grades row as `grid` at 390 pixels and `table-row` at both 700 and 1280 pixels. The grades header was not `display: none` at any tested width, while the access-table header remained hidden at 390 and 700 pixels. Document width matched viewport width at all three sizes.

## 2026-09-13 21:30:00 PDT - Prevent grade policy from increasing access and expose UI inputs

Decision:
Clamp the restricted effective limit to `min(base day limit, restrictedLimitMinutes)`. Return both the configured threshold and normal-decision TTL in `/api/state`, and use those values for grade highlighting and stale-report text in the UI.

Reasons:
- A restriction must never grant more time than the ordinary daily limit.
- The threshold and TTL are configuration values and the UI must describe the same policy that the server enforces.
- Runtime clamping preserves valid configurations when weekday limits vary, instead of rejecting a policy that is restrictive on some days but above the base limit on others.
- The earlier fixed 0.85 UI-highlighting decision was incomplete because it did not account for a configurable threshold; this decision supersedes that display rule.

Telemetry:
The full .NET test suite passed 30 tests, including regressions for a 300-minute restricted setting with a 120-minute base limit and for non-default threshold/TTL values propagated into user state. Both projects passed `dotnet format --verify-no-changes`; inline UI JavaScript syntax, JSON parsing, and `git diff --check` also passed.

## 2026-10-27 21:58:17 PDT - Make SQLite authoritative for all managed router schedules

Decision:
For users listed in the access configuration, reconcile the entire weekly schedule against the current authorized SQLite session at startup and on every sweep. Clear all ordinary and unlimited-rate (`tur-*`) weekday windows except the authorized interval; without a valid active session, clear all windows and pause the profile. Do not modify profiles outside the configuration. Do not re-evaluate grades or shorten existing sessions during reconciliation.

Reasons:
- The owner confirmed that time management is exclusively through the application; manual windows on managed profiles must be overwritten.
- The previous expired-session-only sweep could not discover orphan windows after DB loss or a failed manual disable. This replaces that incomplete cleanup strategy.
- MikroTik documents that `tur-*` windows independently allow unlimited access, so clearing only `mon..sun` is insufficient. Source: https://help.mikrotik.com/docs/spaces/ROS/pages/129531911/Kid%2BControl
- Pause before modifying the schedule, read back the result before resume, and verify the final state. Equivalent RouterOS clock formats must not cause repeated pause/resume cycles.

Telemetry:
Local HTTP/SQLite regression tests cover empty/recreated and older DB state, orphan ordinary and unlimited-rate windows, intact sessions, equivalent time formats, ignored PATCH operations, and unrelated profiles. No live router was contacted.

## 2026-10-27 21:58:17 PDT - Persist authorization and pending synchronization atomically

Decision:
Save session start, extension, shortening or end together with a `pending_router_sync` row in one SQLite transaction, before router mutations. The session itself stores the desired authorized interval; the pending table identifies unfinished synchronization. Clear the marker only after router verification. Serialize request, disable and reconciliation per managed user within the singleton service.

Reasons:
- SQLite and RouterOS cannot share an atomic transaction. Persisting desired state first makes crashes between pause, PATCH, resume and completion recoverable by the next sweep.
- A failed/cancelled router request retains the authorized interval and original absolute expiry; retry never grants a fresh duration. A persisted disable always wins over old router access, including after restart.
- Full reconciliation runs even without pending rows, because DB loss or manual router changes can remove or bypass those markers.
- The additional table is created automatically for existing DBs without rewriting session history. Coordination assumes one running application instance per router/state database; multiple replicas are outside the supported deployment.
- A persisted grant can become effective on a later successful retry even when its original HTTP request failed. Until then, usage follows the persisted session timestamps, without extending the expiry to compensate for downtime.
- Clock/NTP diagnostics are tracked separately in issue #2.

Telemetry:
Failure injection covers SQLite transaction rollback and router errors before/after commands, cancellation, expired pending grants, restart recovery, extension recovery and deterministic concurrent grant/sweep/disable ordering. Test time is provided through TimeProvider; production uses the system clock.

## 2026-10-27 22:00:58 PDT - Accept local issue 1 implementation after recovery verification

Decision:
Keep the implementation and additive SQLite migration described above. Local verification is complete; live router rollout and clock monitoring are separate work.

Reasons:
- The 39 additional regression cases cover router/DB divergence, partial command outcomes, transaction rollback, completion failure after resume, deterministic concurrency, cancellation, DB migration and preservation of existing sessions under low grades.
- The original 30 tests remain passing. Equivalent router windows produce no mutation on repeated sweeps.

Telemetry:
- `DOTNET_ROLL_FORWARD=Major dotnet test kidControlGantt.sln --no-restore`: 69 passed, 0 failed, 0 skipped; reported test duration 186 ms. Target remains net9.0; execution used the installed .NET 10.0.11 runtime because .NET 9 runtime is unavailable locally.
- `dotnet build kidControlGantt.sln --no-restore -c Release`: succeeded, 0 warnings, 0 errors.
- `dotnet format kidControlGantt.sln --no-restore --verify-no-changes` and `git diff --check`: passed.
- Router integration was tested with a stateful HTTP handler and real temporary SQLite files; no live router/configuration was contacted or modified.

## 2026-10-27 22:15:47 PDT - Show two clocks directly instead of adding a clock monitor

Decision:
Implement the owner's reduced issue #2 scope as a small clock panel and read-only `/api/clock` endpoint. Show the server time used for SQLite timestamps and the router time in the application's display zone. Fetch both samples every five seconds while the page is open, advance their displays using browser monotonic elapsed time, and highlight both values when the absolute difference exceeds five seconds. Do not add a background monitor, database tables, NTP configuration, clock adjustment or access-policy changes.

Reasons:
- The owner explicitly requested just the current database/server and router times with a highlight when they differ, replacing the earlier broader monitoring proposal.
- SQLite has no independent clock in this application: session timestamps come from the server's TimeProvider.
- Read `/rest/system/clock` on the same configured router, retaining any reverse-proxy URL prefix. Parse local router date/time with the supplied `gmt-offset`, which already includes DST; display both absolute instants in the same zone. Source: https://manual.mikrotik.com/docs/system-information-and-utilities/clock/
- Compare the router sample with the midpoint of the server request; a five-second display tolerance avoids warning on ordinary request latency and one-second clock precision. The tolerance is a UI constant, not a new access rule.
- Keep clock polling separate from `/api/state`, so clock permission/API errors cannot interrupt access control or hide the server clock. Show an unavailable router value explicitly and log the underlying failure.

Telemetry:
The 21 new C# cases cover absolute time conversion, legacy/ISO dates, DST offset handling, date boundaries, invalid responses, reverse-proxy URLs, midpoint comparison, router failures, timeout and caller cancellation. Local browser/API checks with a loopback-only fake router verified aligned clocks, a 90-second difference, unavailable router time and recovery. A JavaScript VM check verified both drift directions, the exact five-second boundary and ticking values. No real router was contacted.

Validation: all 90 tests passed; Release build completed with 0 warnings and 0 errors; format verification and git diff checks passed. Tests target net9.0 and ran on installed .NET 10.0.11 with DOTNET_ROLL_FORWARD=Major.
