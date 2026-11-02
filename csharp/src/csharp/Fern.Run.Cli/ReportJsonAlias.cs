// `ReportJson` moved to Fern.Scheduler on 2026-08-28 so the report owners that
// live there render through the same function this CLI does, and the f64
// spelling rule exists once. Every verb in this project already writes the bare
// name; this alias keeps that true without importing the whole Fern.Scheduler
// namespace into files that have their own PlanId, StorageCheck or PlanTypes.
global using ReportJson = Fern.Scheduler.ReportJson;
