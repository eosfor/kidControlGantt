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
