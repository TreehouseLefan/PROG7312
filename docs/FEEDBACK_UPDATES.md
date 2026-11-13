# Updates made from lecturer feedback (Parts 1 and 2)


## Part 1: Sensor Data Ingestion & Telemetry

 # | Feedback received 
|---|---|---|---|
| 1 | lecturers feedback :nothing as I got 100% for the project 

## Part 2: Real-Time Command Stream & History

| # | Feedback received 
|---|---|---|---|
| 1 | lecturers feedback :nothing as I got 100% for the project 


## Other improvements to Parts 1 and 2 made during Part 3

These changes were made while building the final PoE. They are listed so the marker can see what changed in earlier work.

| Area | Change | Where |
|---|---|---|
| Navigation | The Mesh menu item is no longer a locked placeholder: it opens the live Mesh Routing page and shows a badge with the number of nodes that are down or isolated. | `src/SmartX.Client/Layout/MainLayout.razor` |
| Home page / API | The third pillar is now active ("Final PoE · live") on both the dashboard and `GET /api/pillars`. | `Pages/Home.razor`, `Endpoints/SystemEndpoints.cs` |
| Ingestion (Part 1) | Every validated reading, live or uploaded in a batch, is now also stored in the per-device AVL history, so long-range queries no longer depend on the in-memory timeline window. | `Services/TelemetryIngestionService.cs`, `Services/TelemetryHistoryService.cs` |
| Disconnect tracking (Part 2) | The `HashSet` of disconnected nodes now drives mesh re-routing, so a device that stops reporting is routed around automatically. | `Mesh/MeshService.cs` |
| Simulator (Part 2) | Added `Reconnect`, so a device failed from the Mesh page can be brought back immediately instead of waiting for its disconnect timer. | `Services/SimulatorControl.cs` |
| History log (Part 2) | New `Mesh` event kind: failures, restores and their re-routing results appear in the global history and can be filtered. | `Contracts/StreamContracts.cs`, `Mesh/MeshService.cs` |
| Testing | 20 new unit tests and a GitHub Actions workflow that builds and tests every push. | `tests/SmartX.Core.Tests/`, `.github/workflows/ci.yml` |
| Documentation | README rewritten for the final PoE: setup, all three pillars, data-structure explanations with code, API reference and troubleshooting. | `README.md` |
