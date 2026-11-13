# SmartX: Real Time Processing and Device Management (PoE Part 3)

Student name:Kwanda Zulu
Student number:ST10439005 
SmartX is a simulated IoT mesh for South African hydroponic farms, utility trackers and smart grid sites. It has:

- an **ASP.NET Core Minimal API** backend on .NET 10;
- a **Blazor WebAssembly** dashboard;
- **SignalR**, which pushes live updates from the API to the dashboard.

**Part 1** built the ingestion gateway and the Sensor Pulse Grid. **Part 2** turns it into a real time processing engine:

- **Two lane ingestion queue.** A `Queue<T>` handles normal traffic, and a `PriorityQueue` lets critical alerts skip ahead of it.
- **Device lookups.** A `Dictionary` keyed by MAC address finds any device in O(1).
- **Timelines.** A `SortedDictionary` stores readings in time order for the graphs.
- **Alert de duplication.** `HashSet`s keep alerts and disconnected nodes unique.
- **Undo.** A `Stack<T>` keeps the history of manual override commands.
- **Predictive action engine.** It learns what operators search for and do after an alert, then suggests the next step.

---

## Contents

1. [What is new in the final PoE](#1-what-is-new-in-the-final-poe)
2. [Where each rubric requirement lives](#2-where-each-rubric-requirement-lives)
3. [Install on Windows](#3-install-on-windows)
4. [Restore, build, test and run](#4-restore-build-test-and-run)
5. [What you should see](#5-what-you-should-see)
6. [The Part 3 data structures, explained](#6-the-part-3-data-structures-explained)
7. [How to test Part 3](#7-how-to-test-part-3)
8. [How to test Parts 1 and 2](#8-how-to-test-parts-1-and-2)
9. [Run with Docker](#9-run-with-docker)
10. [Continuous integration](#10-continuous-integration)
11. [API reference](#11-api-reference)
12. [Part 2 design rationale](#12-part-2-design-rationale)
13. [Troubleshooting](#13-troubleshooting)
14. [References](#14-references)
---

## 1. What is new in Part 2

**Network Topology & Mesh Routing** (`/mesh`):

- **Live mesh map.** The 34 ESP32 devices, three gateways and the central server are drawn as a graph with 111 links. Links in the minimum spanning tree are drawn solid blue; the Dijkstra shortest path tree can be overlaid dashed orange. Down nodes show a red ✕, isolated nodes turn orange, and critical nodes (articulation points) get a dashed ring.
- **Route tracking by MAC.** Type a MAC address (or click a node) to see every hop to the server, the energy per hop, the cheapest individual path for comparison, and what the route becomes if the next hop fails.
- **Self healing.** Fail a gateway or a device and Prim's algorithm rebuilds the tree over the nodes that are still up. Devices that stop reporting (the Part 2 disconnect `HashSet`) are routed around automatically. Every heal is logged with the number of nodes re routed, the change in tree cost and the recompute time.
- **Subnet explorer.** BFS or DFS from a gateway lists the devices connected to it, the ones that are down, the ones that are alive but cut off, and where each affected device should hop instead.
- **Prim vs Kruskal vs Dijkstra.** Both MST algorithms run on every change and must agree on the total cost. A benchmark runs the same code on random graphs of up to 20 000 nodes.
- **Sensor history.** Each device's readings are kept in an **augmented AVL tree**. "Moisture for Node A between 14:00 and 16:00" is a range query that visits O(log n + k) nodes, and the count, minimum, maximum and mean of the range come from subtree aggregates in O(log n), without iterating a list. 24 hours of one minute history is seeded at start up.

Plus: a GitHub Actions workflow that restores, builds and tests on every push, 20 new unit tests, a PowerShell walkthrough script and new REST requests.

## 2. Where each rubric requirement lives

| Requirement | Where |
|---|---|
| Visualise the active mesh | `src/SmartX.Client/Components/MeshMap.razor`, `Pages/Mesh.razor` |
| Track routing paths of nodes by MAC | `MeshService.Route` → `GET /api/mesh/route/{mac}`; route panel on `/mesh` |
| Well organised historical log of a selected sensor | `TelemetryHistoryService` → `GET /api/devices/{mac}/history`; `Components/SensorHistoryLog.razor` |
| All three menu pillars active | `Layout/MainLayout.razor`, `Pages/Home.razor`, `Endpoints/SystemEndpoints.cs` |
| BST / AVL / red black tree with range queries | `src/SmartX.Core/Collections/AvlTree.cs` (augmented AVL) |
| Graph of ESP32 nodes and wireless edges | `src/SmartX.Core/Mesh/MeshGraph.cs`, `RadioEnergyModel.cs`, `src/SmartX.Api/Mesh/MeshLayout.cs` |
| BFS / DFS: devices on a gateway, which are down, where to hop | `MeshAlgorithms.BreadthFirst` / `DepthFirst`, `MeshService.Traverse` |
| Prim or Kruskal MST for power efficient routing | `MeshAlgorithms.Prim` (indexed min heap) and `MeshAlgorithms.Kruskal` (binary heap + union find) |
| Heaps | `Collections/IndexedMinHeap.cs`, `Collections/BinaryMinHeap.cs` |
| Tests | `tests/SmartX.Core.Tests/AvlTreeTests.cs`, `HeapAndUnionFindTests.cs`, `MeshAlgorithmTests.cs` |
| README, Docker, CI | this file, `docker-compose.yml`, `.github/workflows/ci.yml` |
| Feedback from Parts 1 and 2 | `docs/FEEDBACK_UPDATES.md` |


## 3. Install on Windows (step by step)

You need: the **.NET 10 SDK**, **Git**, an editor, and optionally **Docker Desktop**.

### 3.1 Install the tools

Open **PowerShell** (Start → type `PowerShell`) and run:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
```

Close PowerShell, open a new window, and check:

```powershell
dotnet --list-sdks     # must show a line starting with 10.0.
git --version
```

If `dotnet` is "not recognized", restart the PC.

### 3.2 Pick an editor

| Editor | Install | Notes |
|---|---|---|
| **Visual Studio 2026 Community** | `winget install Microsoft.VisualStudio.Community` | During setup tick **ASP.NET and web development**. Visual Studio 2022 cannot open .NET 10 projects. |
| **JetBrains Rider** | free for students | Opens `SmartX.slnx` directly. |
| **VS Code** | `winget install Microsoft.VisualStudioCode` | Add the **C# Dev Kit** and **REST Client** extensions. |

### 3.3 Optional: Docker Desktop

```powershell
winget install Docker.DockerDesktop
```

Docker Desktop needs WSL2 and will offer to enable it. Restart when asked.

### 3.4 Allow the PowerShell test scripts

The `scripts\*.ps1` helpers are unsigned. Allow local scripts for your user once:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

### 3.5 Get the code

Unzip or clone to a short local path that is **not** inside OneDrive, for example `C:\dev\SmartX`:

```powershell
cd C:\dev
git clone https://github.com/TreehouseLefan/PROG7312.git
cd SmartX
```

## 4. Build, test and run

From `C:\dev\SmartX`:

```powershell
dotnet restore SmartX.slnx
dotnet build SmartX.slnx
dotnet test SmartX.slnx
```

- **Build** should end with `Build succeeded` and 0 errors. Warnings are fine.
- **Test** should end with `Failed: 0`. There are tests for every Part 1 and Part 2 structure.

Run the system in **two** PowerShell windows, both opened in `C:\dev\SmartX`:

```powershell
# Window 1: the API (http://localhost:5100)
dotnet run --project src\SmartX.Api
```

```powershell
# Window 2: the dashboard (http://localhost:5200)
dotnet run --project src\SmartX.Client
```

Open **http://localhost:5200**. Stop either window with `Ctrl+C`. If Windows Firewall asks, click **Allow** for private networks.

**Visual Studio instead:**

1. Open `SmartX.slnx`.
2. Right click the solution → **Configure Startup Projects** → **Multiple startup projects**.
3. Set `SmartX.Api` and `SmartX.Client` to **Start**.
4. Press **F5**.

## 5. What you should see

**API window** (within a few seconds):

```
info: SmartX.Seeder[0]
      Seeded 34 devices and ~10,000 historical readings across 4 Sub-Zones.
info: SmartX.EngineSeeder[0]
      Seeded the Action Engine with ~135 historical operator events over 7 days.
info: SmartX.HistorySeeder[0]
      History seeder: 48,960 readings inserted into per device AVL trees.
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5100
 
 ```

About once a minute you will also see lines like `Auto event: spike queued for …`. That is the simulator creating events on purpose.

**Dashboard (http://localhost:5200):**

- **Home:** all three pillars are live. *Network Topology & Mesh Routing* now shows "Final PoE · live".
- **Menu:** **Telemetry**, **Commands** and **Mesh** groups. The Mesh link shows a red badge with the number of nodes that are down or isolated.
- **Mesh Routing** (`/mesh`):
  - **Stat strip:** 38 nodes · 111 links, all up, MST energy of roughly 4 900 µJ, and 2 critical nodes (the server and `GW-B`, which is Facility B's only gateway).
  - **Map:** Facility A (Stellenbosch) on the left with two gateways, Facility B (Sandton) on the right with one. Hover any node for its MAC, status and route cost.
  - **Trace a route:** click a sensor, or type `24:0A:C4:0A:00:00` (A 01 Soil Moisture) and press **Trace**. The route lights up yellow, the "if next hop fails" route appears dashed pink, and the panel lists each hop with its distance and energy.
  - **Self healing:** **Fail GW A1**. Within a second the map redraws, every Facility A sensor now reaches the server through `GW-A2`, and the log shows how many nodes were re routed and how the tree cost changed. **Fail GW B** isolates all of Facility B (orange), because it is an articulation point. **Reset all** restores everything.
  - **Subnet explorer:** pick `GW-A1`, **BFS**, **Run**. The table lists every device reached, its hop count and its parent. With `GW-A1` failed, run BFS from `GW-A2` and the "Where to hop now" list explains each re route.
  - **Prim vs Kruskal vs Dijkstra:** both MSTs report the same total; the Dijkstra tree costs more in total but gives some nodes fewer hops. Run the 20 000 node benchmark to see the algorithms at scale.
  - **Sensor history:** pick a sensor, choose **Custom**, set a date and **14:00 → 16:00** inside the "Stored" span shown at the top right (the seeder covers the 24 hours before start up, and live readings are added from then on), then **Run range query**. You get about 120 readings (one a minute), their min / mean / max, a bucketed chart, a paged table, and the AVL statistics. For example, a tree of about 2 000 readings has a height of around 11 against a guaranteed maximum of 15, and the query visits a few dozen nodes where a list scan would compare all 2 000.

State is kept across navigation: go to the Pulse Grid and back, and the selected node, route, traversal and history page are still there.

## 6. How to test the queues, dictionaries, sets, stack and engine

Each test has a **dashboard**, a **PowerShell** and a **`.http`** route. The `requests/smartx.http` file works in Visual Studio, Rider, or VS Code with REST Client.

### 6.1 Simulate a high priority alert (Queue&lt;T&gt; vs PriorityQueue)

| Route | Do this | Expect |
|---|---|---|
| Dashboard | Command Stream → **Simulate critical moisture** (or **Simulate power spike**) | A message says the reading went to the **Priority lane**, a red toast appears, a highlighted `PRIORITY` row appears in the stream, and the alert shows in *Active alerts*. |
| PowerShell | `.\scripts\simulate-critical-alert.ps1` (or `-Kind power`) | Prints `Lane: Priority` with the reason, then the active alerts table. |
| `.http` | "Simulate a HIGH-PRIORITY alert" request | `202 Accepted` with `"lane": "Priority"`. |
| Raw HTTP | `Invoke-RestMethod -Method Post http://localhost:5100/api/telemetry -ContentType application/json -Body '{"deviceMac":"<B sensor MAC>","payloadType":"float","value":12.5}'` | Same as above. Any reading below 20 % on a soil sensor, or at or above 4,000 W on a meter, takes the priority lane. |

### 6.2 Historical telemetry in an augmented AVL tree

**Why a balanced tree.** Readings arrive in timestamp order. Inserting sorted keys into a plain binary search tree builds a linked list of height n, so a range query degrades to O(n). An AVL tree rebalances with rotations after each insert or delete, so its height stays below 1.44·log₂(n + 2) [16], [7]. With the 25 000 reading cap per device that is at most 20 levels, against 25 000 for an unbalanced tree.

**Keys.** The key is `TimelineKey(UtcTicks, Sequence)`, so two readings with the same timestamp are both kept.

**Augmentation.** Every node also stores the size, sum, minimum and maximum of its subtree, updated during rotations in O(1):

```csharp
// AvlTree.cs
private static void Update(Node node)
{
    node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));
    node.Size   = 1 + SizeOf(node.Left) + SizeOf(node.Right);
    node.Sum    = node.Measure + (node.Left?.Sum ?? 0) + (node.Right?.Sum ?? 0);
    node.Min    = Math.Min(node.Measure, Math.Min(node.Left?.Min ?? double.PositiveInfinity, node.Right?.Min ?? double.PositiveInfinity));
    node.Max    = Math.Max(node.Measure, Math.Max(node.Left?.Max ?? double.NegativeInfinity, node.Right?.Max ?? double.NegativeInfinity));
}
```
This gives three queries that a `List<T>` cannot answer without scanning:

| Query | Method | Cost |
|---|---|---|
| All readings between 14:00 and 16:00 | `Range(from, to)`: skips every subtree that lies wholly outside the window | O(log n + k) |
| Count, min, max and mean of that window | `Aggregate(from, to)`: combines whole-subtree aggregates along two root to leaf paths | O(log n) |
| Page 7 of that window, newest first | `Page(from, to, skip, take, descending)`: rank arithmetic on subtree sizes jumps straight to the first row | O(log n + take) |

```csharp
// TelemetryHistoryService.Query: one page, the summary and 48 chart buckets, without a scan
var page    = tree.Page(fromKey, toKey, page * pageSize, pageSize, descending, out int total, out int pageVisited);
var summary = tree.Aggregate(fromKey, toKey, out int summaryVisited);   // count / min / max / mean in O(log n)
```

Each device keeps at most 25 000 readings; the oldest is evicted with `TryRemoveMin` in O(log n). The page shows the tree height, its theoretical bound, the rotations so far and how many nodes each query visited compared with a linear scan, so the efficiency claim can be checked live.

**Why AVL and not red black.** Both guarantee O(log n). AVL trees are more strictly balanced (about 1.44 log n against 2 log n for red-black [7]), so lookups are shallower, at the cost of more rotations on insert. This workload is read heavy (each reading is written once and queried many times), so the shallower tree is the better fit. .NET's `SortedDictionary` is a red black tree, but it exposes neither subtree aggregates nor rank, so it cannot answer the aggregate or paging queries in O(log n).

### 6.3 Dictionaries: O(1) lookups and sorted timelines

`MeshAlgorithms.BreadthFirst` uses a `Queue<int>` and visits nodes in rings of hop distance, so the depth it records is the **fewest hops** from the start. `DepthFirst` uses an explicit `Stack<int>` (no recursion, so a long chain cannot overflow the call stack) and follows one branch to the end before backtracking. Both take a predicate `isUp`, so down nodes are never entered:

```csharp
// MeshService.Traverse: start at a gateway, stay inside its facility, skip anything that is down
bool IsTraversable(int x) => c.Up[x] && (fromServer || g.Vertices[x].Kind != MeshNodeKind.Server);
TraversalResult result = depthFirst
    ? MeshAlgorithms.DepthFirst(g, start, IsTraversable)
    : MeshAlgorithms.BreadthFirst(g, start, IsTraversable);
```

Any in scope node that is up but not visited is **cut off** from that gateway. For every node whose usual next hop (taken from the all up baseline tree) is now down, the explorer reports its new next hop from the healed tree, or says it is isolated. Both traversals run in O(V + E) [7].

### 6.4 Critical nodes (articulation points)

A node is critical if removing it disconnects the mesh. These are the **articulation points**, found in one DFS using discovery times and low link values (Hopcroft and Tarjan [17]). In Smart X they are the server and `GW-B`. That tells an engineer exactly where a second gateway would add redundancy.

### 6.5 Prim's algorithm with an indexed min-heap

Prim grows one tree from the server, always adding the cheapest link from the tree to a new node [18]. `IndexedMinHeap` stores, for each vertex, its position in the heap array, so **decrease key** is O(log n) and the heap never holds stale duplicates:

```csharp
// MeshAlgorithms.Prim (inner loop)
int u = heap.ExtractMin(out double weight);
inTree[u] = true;
foreach (int e in graph.IncidentEdges(u))
{
    int v = graph.Edges[e].Other(u);
    if (inTree[v] || !isUp(v) || graph.Edges[e].Cost >= best[v]) continue;
    best[v] = graph.Edges[e].Cost;
    parent[v] = u;                     // v's next hop towards the server
    heap.InsertOrDecrease(v, best[v]);
}
```

Running time O(E log V). The `parent` array **is** the routing table: each node forwards to its parent, so a route is `PathToRoot(parent, server, node)`.

### 6.6 Kruskal's algorithm with a binary heap and union-find

Kruskal sorts all links cheapest first and keeps each one that joins two different components [19]. Smart-X builds a `BinaryMinHeap` over the live links in O(E) with Floyd's bottom up heapify, pops them in order, and uses a `DisjointSet` to reject links that would close a cycle. The set uses union by rank with path halving (a form of path compression), so a sequence of operations costs almost O(1) each, amortised: the bound grows only with the inverse Ackermann function [20], [7]. Running time O(E log E).

Kruskal produces a **forest**, so it also counts how many separate pieces the live mesh has. Smart-X compares its cost on the server's component with Prim's on every change. They must match, because every minimum spanning tree of a graph has the same total weight [7]; the individual edges may differ only where two links have equal cost.

### 6.7 MST vs shortest paths: an honest trade-off

The brief asks for the most power efficient routing. The MST minimises the **total** energy of the routing backbone (the cost of one broadcast round to every node), and, as a known property, it also minimises the **most expensive single link** on the path between any two nodes [7]. It does **not** minimise each node's own path. Dijkstra's algorithm [21] does that, so Smart X runs it too and shows both:

| | MST (Prim) | Shortest path tree (Dijkstra) |
|---|---|---|
| Total energy of all links in use | lowest possible | higher |
| Worst single link | lowest possible | higher |
| One node's own path to the server | can be longer (more hops) | cheapest possible |
| Every device sends one raw packet to the server (no aggregation) | more energy in total | less energy in total |

The last row matters. In the design model of the default layout the MST backbone costs about 4 870 µJ against 6 170 µJ for the shortest-path tree, but if every device forwards its own unaggregated packet all the way to the server, the per-packet path energies add up to about 16 000 µJ along the MST against 11 900 µJ along the shortest paths, and the mean route is 4.15 hops against 2.91. The MST is therefore the right backbone when packets are **aggregated** at each hop (one combined packet per link, as in LEACH-style clustering [15]) or **broadcast** from the server, while raw per-reading telemetry is cheaper on shortest paths. Smart-X shows both trees so an engineer can choose per traffic type.

Sub-Zone C shows this most clearly: trace one of its devices, such as `C-10 Pump B Relay`, and compare the MST row with the Dijkstra row. The MST route can run several more hops through `GW-A1` than the node's cheapest path through `GW-A2`. The route panel shows both, so the trade-off is visible rather than hidden.

### 6.8 Self-healing

`MeshService` checks liveness every second: a node is down when an operator failed it on the Mesh page **or** when the Part 2 `AlertCenter` has it in its disconnected-node `HashSet`. When the set of down nodes changes, Prim, Kruskal, Dijkstra and the articulation-point search all rerun over the live nodes only, which takes well under a millisecond for 38 nodes. Any node whose parent changed has been re-routed; any live node with no parent is isolated. A failed device is also silenced in the simulator, so Parts 1 and 2 see it go offline too. The new snapshot is pushed to every dashboard over SignalR only when its version changes.

### 6.9 Complexity summary

| Operation | Structure | Time |
|---|---|---|
| Insert / delete a reading | AVL tree | O(log n) |
| Readings in a time window | AVL range query | O(log n + k) |
| Count / min / max / mean of a window | augmented AVL | O(log n) |
| Page *p* of a window | AVL rank seek | O(log n + page size) |
| Who is reachable from a gateway | BFS / DFS | O(V + E) |
| Critical nodes | DFS low link | O(V + E) |
| Minimum energy routing tree | Prim + indexed heap | O(E log V) |
| Cross check MST | Kruskal + heap + union find | O(E log E) |
| Cheapest individual paths | Dijkstra + indexed heap | O(E log V) |

## 7. How to test Part 3

**In the dashboard:** follow the tour in [section 5](#5-what-you-should-see).

**PowerShell walkthrough** (API running):

```powershell
.\scripts\test-mesh-routing.ps1
```

It traces `24:0A:C4:0A:00:00`, fails `gw-a1`, prints the healed route, runs BFS from `gw-a2` with hop advice, restores the gateway, and queries the sensor's history for 14:00–16:00 yesterday with the AVL statistics.

**REST requests:** open `requests/smartx.http` in Visual Studio or VS Code (REST Client) and run the requests under "Part 3".

**Unit tests:**

```powershell
dotnet test SmartX.slnx --filter "FullyQualifiedName~AvlTree|FullyQualifiedName~MeshAlgorithm|FullyQualifiedName~HeapAndUnionFind"
```

They check, among other things, that 10 000 sorted inserts keep the AVL height within its bound, that `Aggregate` matches a linear scan on random data, that Prim and Kruskal both find weight 37 on the textbook graph of Cormen et al. [7], that Dijkstra matches hand computed distances, and that a down node is routed around.

## 8. How to test Parts 1 and 2

Each test has a **dashboard**, a **PowerShell** and a **`.http`** route. The `requests/smartx.http` file works in Visual Studio, Rider, or VS Code with REST Client.

### 8.1 Simulate a high-priority alert (Queue&lt;T&gt; vs PriorityQueue)

| Route | Do this | Expect |
|---|---|---|
| Dashboard | Command Stream → **Simulate critical moisture** (or **Simulate power spike**) | A message says the reading went to the **Priority lane**, a red toast appears, a highlighted `PRIORITY` row appears in the stream, and the alert shows in *Active alerts*. |
| PowerShell | `.\scripts\simulate-critical-alert.ps1` (or `-Kind power`) | Prints `Lane: Priority` with the reason, then the active alerts table. |
| `.http` | "Simulate a HIGH-PRIORITY alert" request | `202 Accepted` with `"lane": "Priority"`. |
| Raw HTTP | `Invoke-RestMethod -Method Post http://localhost:5100/api/telemetry -ContentType application/json -Body '{"deviceMac":"<B sensor MAC>","payloadType":"float","value":12.5}'` | Same as above. Any reading below 20 % on a soil sensor, or at or above 4,000 W on a meter, takes the priority lane. |

### 8.2 Prove the bypass under load

| Route | Do this | Expect |
|---|---|---|
| Dashboard | **Burst test** with 5,000 (or 20,000) | "✓ Priority bypass proven: … The critical packet was processed #1 …" with its wait compared to the standard packets. |
| PowerShell | `.\scripts\prove-priority-bypass.ps1 -Count 20000` | `Critical packet processed at position: #1` in green. |
| Manual | **Pause dispatcher**, watch the standard lane fill, click **Simulate critical moisture**, then **Resume** | The critical packet is the first row processed after resuming. |

Burst packets are marked synthetic. They travel through the real queues and are timed, but they are discarded instead of stored, so they do not distort the live data.

### 8.3 Dictionaries: O(1) lookups and sorted timelines

| Route | Do this | Expect |
|---|---|---|
| Dashboard | Type a MAC in any format (`240ac40b0001`, `24-0A-…`) in **Find devices** | One result, with the strategy line reading *Exact MAC: Dictionary&lt;MacKey, SensorDevice&gt; O(1) lookup* and a time in microseconds. |
| Dashboard | Search `pump a`, filter **Category = Actuator**, **State = Offline**, or **Active alerts only** | Results come from the word index plus HashSet intersection. The strategy line says so. |
| Dashboard | Select a device and switch the chart window between 5m, 15m, 1h and 4h | Graph from the device's `SortedDictionary`, with stored vs returned points and µs timing. |
| Dashboard | Diagnostics → **Run lookup benchmark** | Dictionary vs list-scan times, with `MacKey` collisions = 0 vs hundreds for a naive hash. |
| PowerShell | `.\scripts\test-dictionary-lookups.ps1` | All four checks printed. |

### 8.4 Sets: duplicate disconnect alerts are suppressed

1. Pulse Grid → **Simulate outage** on Sub Zone B, or Command Stream → select a device → *Simulate disconnect* on its full profile page.
2. Watch **Active alerts**:
   - each device appears once in *Disconnected nodes* (`HashSet<MacKey>`);
   - one *Disconnected* alert per device;
   - the **duplicates suppressed** counter climbs every second, because the Pulse engine re reports offline devices every tick and the set absorbs each repeat.
3. After 30 seconds the devices report again. They leave the set and the alerts are cleared.

### 8.5 Stack: undo a manual override

| Route | Do this | Expect |
|---|---|---|
| Dashboard | **Close all valves** (SubZone C), then **↶ Undo** | The bulk command appears on top of the undo stack. Undo pops it and re opens all four valves, then moves it to the redo stack. Each command goes *Pending* → *Confirmed* when the valves report back. |
| PowerShell | `.\scripts\test-undo.ps1` | Valve states before, after closing, and after undo. |

### 8.6 Predictive Action Engine

1. Command Stream → **Dry out Sub Zone B**. Within about 30 seconds:
   - a forecast insight ("projected to fall below 20% in ~N min") appears first;
   - then **critical moisture** alerts on the B sensors, which take the priority lane.
2. **Suggested Actions** then shows:
   - **Turn on C-09 Pump A Relay**, with its confidence (typically 60–90 %; it dips while several sensors alert at once and recovers as you act) and a reason such as "When soil moisture fell below the critical level in Sub Zone B, operators turned on C-09 Pump A Relay within 10 min in 8x% of past cases …";
   - **Search "pump a"**: the behaviour pattern from the brief.
3. Click **Do it**. The command is issued and confirmed, Pump A's meter jumps to about 1.1 kW, and the B moisture graph starts rising. The *hit rate* and acceptance statistics update.
4. Click **Show what the engine has learned** to see the rule table (support, confidence, lift).
5. Teach it something new: in **Find devices**, search a term and press **Enter**, then open a device. Repeat that a few times after the same alert. New rules appear, and the sequence model starts predicting your next step.


## 9. Run with Docker

Start Docker Desktop, wait for "Engine running", then from `C:\dev\SmartX`:

```powershell
docker compose up --build
```

- **Dashboard:** http://localhost:8080
- **API:** http://localhost:5100 (try http://localhost:5100/api/mesh)
- **Stop:** `Ctrl+C`, then `docker compose down`.

Don't run Docker and the two `dotnet run` windows at the same time; they both need port 5100.

## 10. Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request to `main`:

1. installs the .NET SDK named in `global.json`;
2. `dotnet restore`, `dotnet build -c Release` and `dotnet test`, uploading the test results;
3. builds both Docker images with `docker compose build`.

Open the **Actions** tab on GitHub to see each run. A green tick on a commit means it compiled and every test passed on a clean machine, which is independent evidence that the submitted code builds [22].

## 11. API reference

### Part 3 endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/mesh` | Whole mesh: nodes (status, critical, MST parent, hops, cost), links (in MST, in shortest-path tree, live), stats, self-healing log |
| GET | `/api/mesh/route/{node}` | Route for a node id **or MAC**: MST hops, Dijkstra path, next hop, route if the next hop fails, explanation |
| GET | `/api/mesh/traverse?from=gw-a1&mode=bfs\|dfs&subZone=sz-c` | BFS / DFS from a node: visit order, depth, parent, down and cut-off nodes, hop advice |
| POST | `/api/mesh/nodes/{node}/fail` | Fail a gateway or device (the server cannot fail); routes self-heal |
| POST | `/api/mesh/nodes/{node}/restore` | Restore it |
| POST | `/api/mesh/reset` | Restore every failed node |
| GET | `/api/mesh/benchmark?nodes=5000` | BFS, Prim, Kruskal and Dijkstra on a random geometric graph |
| GET | `/api/devices/{mac}/history?from=&to=&page=0&pageSize=25&order=desc&buckets=48` | AVL range query: one page, summary, chart buckets and tree statistics. Times are ISO 8601, e.g. `2026-11-11T14:00:00+02:00`; the default is the last two hours |

SignalR method `mesh` pushes a new snapshot whenever the routes change.

### Part 2 endpoints

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/telemetry` | Device reading. Returns `202` with `lane` (`Standard`/`Priority`), `sequence` and queue depths. Returns `503` when the FIFO lane is full. |
| GET | `/api/stream/queue` | Lane depths, waits (avg / p95), throughput |
| POST | `/api/stream/queue/pause`, `/resume` | Demo switch for the dispatcher |
| POST | `/api/stream/burst?count=5000` | Priority-bypass proof |
| GET | `/api/stream/recent?max=50` | Last processed packets |
| GET | `/api/stream/devices?query=&category=&state=&alertsOnly=&record=` | Search and filter. `record=true` records the search for the engine. |
| POST | `/api/stream/devices/{mac}/inspect` | Records that an operator opened a device |
| GET | `/api/devices/{mac}/timeline?minutes=15&maxPoints=240` | Timeline from the SortedDictionary |
| GET | `/api/history?minutes=&kind=` | Global event history (sorted) |
| GET / POST | `/api/commands/history`, `/api/commands` | Command history; issue a command |
| POST | `/api/commands/undo`, `/api/commands/redo` | Stack-based undo / redo |
| GET | `/api/commands/shadows` | Device shadows (polled by devices) |
| GET | `/api/alerts` | Active alerts, disconnected set, counters |
| POST | `/api/alerts/simulate?kind=moisture\|power` | Critical reading through the normal gateway |
| GET | `/api/engine`, `/api/engine/rules` | Suggestions, insights, statistics; learned rules |
| POST | `/api/engine/suggestions/{id}/accept`, `/dismiss` | Feedback on suggestions |
| GET | `/api/diagnostics/lookup?devices=&lookups=` | Dictionary vs list benchmark |
| POST | `/api/simulator/dryout/{sz-a\|sz-b}` | Fast dry-out scenario |


The full OpenAPI document is at http://localhost:5100/openapi/v1.json.

## 12. Part 2 design rationale

**Two lanes, one consumer.**
- A FIFO `Queue<T>` preserves arrival order for ordinary telemetry.
- A `PriorityQueue<TElement,TPriority>` is an array backed quaternary min heap with O(log n) enqueue and dequeue [3][7]. It lets a critical reading overtake thousands of queued ones.
- Microsoft's documentation states that `PriorityQueue` does not guarantee FIFO order for equal priorities [3]. Smart-X therefore uses a `(severity, sequence)` priority with a custom comparer, so equal severity alerts stay in arrival order. A unit test checks this.
- A single dispatcher keeps per device processing ordered and lock free on the hot path.
- The FIFO lane is bounded and answers HTTP 503 when full (back pressure). The priority lane is never shed.

**Dictionaries.**
- `Dictionary<TKey,TValue>` is a hash table with close to O(1) retrieval [4].
- Its performance depends on how well keys spread across buckets [7]. Smart-X stores MAC addresses as their 48 bit value and mixes the bits before hashing. The benchmark shows that a naive 32 bit truncation collides for devices from vendors whose prefixes share a low byte, while `MacKey` does not.
- Timelines use `SortedDictionary<TKey,TValue>`, a binary search tree with O(log n) insertion and retrieval. It is faster than `SortedList` for unsorted inserts [5], and inserts are unsorted here because priority packets are processed ahead of older standard ones.

**Sets.**
- `HashSet<T>` holds no duplicates, and `Add` returns false for an existing element [6]. This turns the level triggered "device is still offline" signal into a single edge triggered alert.
- This matters for operators: high false alarm and duplicate alarm rates desensitise people who monitor alarms [10].

**Stack.**
- Undo and redo follow the Command pattern, keeping executed commands on a last-in-first-out history [9].
- Each record stores the before and after state of every device it touched, so even bulk commands revert exactly.

**Predictive Action Engine.**
- **Situation → action rules.** Situation → action pairs are mined with the support, confidence and lift measures of association rule mining [1], over a 10 minute window. A per occurrence `HashSet` stops one occurrence being counted twice.
- **Action sequences.** A first order Markov model of consecutive operator actions predicts the next step, an approach shown to work for predicting user access sequences [2].
- **Recency weighting.** Counts decay with a 7 day half life, so the engine adapts when habits change.
- **Ranking.** Suggestions are scored and ranked with a bounded min heap (top k in O(n log k)).
- **Filtering.** Suggestions are dropped if already satisfied (for example, the pump is already on) or dismissed, and each one shows its evidence.
- **Forecasting.** Ordinary least squares [11] over the last five minutes projects when each soil and power sensor will cross its critical threshold, so problems are flagged before any alert fires.
- **Disconnect detection.** This is inherited from Part 1's accrual detector [12].

**State-preserving navigation.** Blazor components are discarded when you navigate away. Smart-X therefore keeps module state in a registered in memory state container service, which Microsoft documents as the way for components to share state [13]. In WebAssembly that service lives as long as the app is loaded. A single SignalR connection is also kept open for the life of the app [14]. A browser refresh still clears memory, so after a reload the dashboard rebuilds its state from the API's REST snapshots.

## 13. Troubleshooting

| Symptom | Fix |
|---|---|
| `NETSDK1045` / "does not support targeting .NET 10.0" | Install the .NET 10 SDK (`dotnet --list-sdks`). |
| Dashboard shows "API could not be reached" | Start `SmartX.Api` first and check http://localhost:5100/api/health. |
| Live badge stays grey | Check that the API is running, then press Retry. CORS origins are listed in `src/SmartX.Api/appsettings.json`. |
| Mesh page says "Loading the mesh…" | The API is an older build: stop it, run `dotnet build`, start it again. |
| History query returns 0 readings | The window is outside the stored span. Use a preset (1 h, 2 h…) or a custom window inside the "Stored" dates. |
| A node stays down after **Restore** | It comes back once it reports again (a few seconds). **Reset all** restores every failed node. |
| `running scripts is disabled on this system` | Run `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`. |
| Port 5100 in use | Run `netstat -ano \| findstr :5100`, then `taskkill /PID <pid> /F`. |
| `dotnet build` fails with "file is locked" | Stop the running API/client (or the test runner) first; Windows locks DLLs that are in use. |
| Blank page | Press `Ctrl+F5`, or open F12 → Console and read the error. |

## 14. References

[1] R. Agrawal, T. Imieliński and A. Swami, "Mining association rules between sets of items in large databases," in *Proc. ACM SIGMOD Int. Conf. Management of Data*, Washington, DC, USA, 1993, pp. 207–216. Available: https://www.cs.cmu.edu/~natassa/courses/15-721/papers/p207-agrawal.pdf [Accessed 14 October 2026].

[2] M. Deshpande and G. Karypis, "Selective Markov models for predicting Web page accesses," *ACM Transactions on Internet Technology*, vol. 4, no. 2, pp. 163–184, 2004. Available: https://archive.siam.org/meetings/sdm01/pdf/sdm01_04.pdf [Accessed 14 October 2026].

[3] Microsoft, "PriorityQueue<TElement,TPriority> Class," *Microsoft Learn*, n.d. Available: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2 [Accessed 14 October 2026].

[4] Microsoft, "Dictionary<TKey,TValue> Class," *Microsoft Learn*, n.d. Available: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2 [Accessed 14 October 2026].

[5] Microsoft, "SortedDictionary<TKey,TValue> Class," *Microsoft Learn*, n.d. Available: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.sorteddictionary-2 [Accessed 14 October 2026].

[6] Microsoft, "HashSet<T> Class," *Microsoft Learn*, n.d. Available: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.hashset-1 [Accessed 14 October 2026].

[7] T. H. Cormen, C. E. Leiserson, R. L. Rivest and C. Stein, *Introduction to Algorithms*, 4th ed. Cambridge, MA, USA: MIT Press, 2022.

[8] Amazon Web Services, "AWS IoT Device Shadow service," *AWS IoT Core Developer Guide*, n.d. Available: https://docs.aws.amazon.com/iot/latest/developerguide/iot-device-shadows.html [Accessed 14 October 2026].

[9] E. Gamma, R. Helm, R. Johnson and J. Vlissides, *Design Patterns: Elements of Reusable Object-Oriented Software*. Reading, MA, USA: Addison-Wesley, 1994.

[10] M. Cvach, "Monitor alarm fatigue: An integrative review," *Biomedical Instrumentation & Technology*, vol. 46, no. 4, pp. 268–277, 2012. Available:https://array.aami.org/doi/10.2345/0899-8205-46.4.268 [Accessed 14 October 2026].

[11] National Institute of Standards and Technology, "Linear least squares regression," in *NIST/SEMATECH e-Handbook of Statistical Methods*, sec. 4.1.4.1, n.d. Available: https://www.itl.nist.gov/div898/handbook/pmd/section1/pmd141.htm [Accessed 14 October 2026].

[12] N. Hayashibara, X. Défago, R. Yared and T. Katayama, "The φ accrual failure detector," in *Proc. 23rd IEEE Int. Symp. Reliable Distributed Systems (SRDS 2004)*, Florianópolis, Brazil, 2004, pp. 66–78. Available: https://ieeexplore.ieee.org/document/1353004 [Accessed 14 October 2026].

[13] Microsoft, "ASP.NET Core Blazor state management," *Microsoft Learn*, n.d. Available: https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management [Accessed 14 October 2026].

[14] Microsoft, "Overview of ASP.NET Core SignalR," *Microsoft Learn*, n.d. Available: https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction [Accessed 14 October 2026].

[15] W. R. Heinzelman, A. Chandrakasan and H. Balakrishnan, "Energy-efficient communication protocol for wireless microsensor networks," in *Proc. 33rd Annu. Hawaii Int. Conf. System Sciences (HICSS-33)*, Maui, HI, USA, 2000. Available: https://ieeexplore.ieee.org/document/926982 [Accessed 12 November 2026].

[16] G. M. Adelson-Velsky and E. M. Landis, "An algorithm for the organization of information," *Doklady Akademii Nauk SSSR*, vol. 146, no. 2, pp. 263–266, 1962 (English translation in *Soviet Mathematics Doklady*, vol. 3, pp. 1259–1263, 1962). Available: https://www.mathnet.ru/eng/dan26964 [Accessed 12 November 2026].

[17] J. Hopcroft and R. Tarjan, "Algorithm 447: Efficient algorithms for graph manipulation," *Communications of the ACM*, vol. 16, no. 6, pp. 372–378, 1973. Available: https://dl.acm.org/doi/10.1145/362248.362272 [Accessed 12 November 2026].

[18] R. C. Prim, "Shortest connection networks and some generalizations," *Bell System Technical Journal*, vol. 36, no. 6, pp. 1389–1401, 1957. Available: https://ieeexplore.ieee.org/document/6773228  
[Accessed 12 November 2026].

[19] J. B. Kruskal, "On the shortest spanning subtree of a graph and the traveling salesman problem," *Proceedings of the American Mathematical Society*, vol. 7, no. 1, pp. 48–50, 1956. Available: https://pubs.ams.org/PROC/1956-007-01/S0002-9939-1956-0078686-7 [Accessed 12 November 2026].

[20] R. E. Tarjan, "Efficiency of a good but not linear set union algorithm," *Journal of the ACM*, vol. 22, no. 2, pp. 215–225, 1975. Available: https://www.semanticscholar.org/paper/Efficiency-of-a-Good-But-Not-Linear-Set-Union-Tarjan/449230e4ebcf6ba071636e07cdca76ef85213a43 [Accessed 12 November 2026].

[21] E. W. Dijkstra, "A note on two problems in connexion with graphs," *Numerische Mathematik*, vol. 1, pp. 269–271, 1959. Available: https://link.springer.com/article/10.1007/BF01386390 [Accessed 12 November 2026].

[22] GitHub, "Building and testing .NET," *GitHub Docs*, n.d. Available: https://docs.github.com/en/actions/tutorials/build-and-test-code/net [Accessed 12 November 2026].

[23] I. F. Akyildiz, X. Wang and W. Wang, "Wireless mesh networks: a survey," *Computer Networks*, vol. 47, no. 4, pp. 445–487, 2005. Available: https://www.sciencedirect.com/science/article/abs/pii/S1389128604003457?via%3Dihub [Accessed 12 November 2026].

