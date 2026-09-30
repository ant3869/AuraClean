# Gauntlet Loop — Round 2 (2026-09-29, lead: Hermes)

Goal (one sentence): grind AuraClean's remaining known defects and add one shippable
feature, with every piece judged against a frozen, inspectable bar.

Skill: gauntlet-loop, run mode. Builders never grade themselves; critics get the
artifact only (no builder reasoning); blind judgment, forced pass/fail, single biggest
gap on fail. Brake: 1 builder round + max 1 refiner round per piece — whatever still
fails gets reverted/parked, not looped forever. No agent runs `dotnet build`
(parallel builds conflict); the lead runs the single build+test gate at the end.

## Frozen bars (bar-selection rule 4 — same reference every round)

- E bar: FRONTEND_AUDIT_REPORT.md HIGH "50+ empty catch{}" → zero silent catches in
  Services/ + Helpers/. Objective: build 0 warnings / 0 errors, tests green.
- F bar: DESIGN_CRITIQUE_v2.md item 4 (Glass naming dishonest) → zero `AuraGlassCard`
  refs in owned files, consistent new names, both themes intact.
- G bar: DESIGN_CRITIQUE_v2.md item 3 (max 3–5 items/section) + task.md § Navigation
  (4–6 top-level destinations) → no section over 5 items, nav behavior identical.
- H bar: FRONTEND_AUDIT_REPORT.md HIGH (cross-thread collections) → no collection
  mutated off the UI thread; CSV export opens in Excel, one row per record.

## Pieces (disjoint file ownership — no two builders share a file)

| Piece | Builder owns | Bar |
|---|---|---|
| E silent catches | Services/*.cs, Helpers/*.cs | 0 silent catches |
| F Glass rename | Resources/*.xaml, Views/*.xaml EXCEPT MainWindow + CleanupHistoryView | 0 AuraGlassCard refs |
| G nav split | Views/MainWindow.xaml, ViewModels/MainViewModel.cs | ≤5 items/section |
| H thread-safety + CSV export | ViewModels/*.cs EXCEPT MainViewModel.cs, Views/CleanupHistoryView.xaml, CleanupHistoryService.cs (additive only), FeatureBatchDTests.cs (append only) | no off-thread mutation; CSV correct |

## Status

- Wave 1 builders: dispatched
- Wave 1 critics: E PASS, F FAIL (6 dangling AuraGlassCard in CleanupHistoryView), G PASS, H PASS/PASS
- Refiner round: lead fixed F gap (CleanupHistoryView → AuraStatCard/AuraCard/AuraDataCard, 0 AuraGlass refs) + fixed 2 pre-existing brace errors the E builder introduced in ThreatScannerService (unclosed lambda) + 1 scope error in UninstallerService (dir→basePath)
- Lead gate (build + 333 tests): PASS — 0 warnings, 0 errors, 340/340 green (7 new CSV asserts)
- Smoothing pass: skipped, no conflicts (disjoint ownership held; only intentional cross-file fix was MainWindow.xaml.cs nav mapping by builder G)
- Commit: pending
