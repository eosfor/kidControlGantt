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
