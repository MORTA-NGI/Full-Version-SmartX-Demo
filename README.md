# Smart-X IoT Mesh Ecosystem

A .NET 10 gateway suite for a South African IoT venture that monitors hydroponic farms and smart-grid sub-stations.
Thousands of ESP32 microcontrollers publish multi-typed telemetry (`float` soil moisture, `int` wattage, `bool` valve
states). Smart-X ingests, validates, prioritises, stores and routes that telemetry, and gives operators a live
dashboard to act on it.

| Pillar | Module | Part |
|---|---|---|
| 📡 | **Sensor Data Ingestion & Telemetry** - registration, attachments, Anomaly Radar | Part 1 |
| ⚡ | **Real-Time Command Stream & History** - FIFO/priority queues, undo stack, predictive actions | Part 2 |
| 🕸️ | **Network Topology & Mesh Routing** - graph, BFS/DFS, Prim's MST, AVL history | PoE |

---

## Contents

1. [Architecture](#1-architecture)
2. [Prerequisites](#2-prerequisites)
3. [Restore, build and run (VS Code)](#3-restore-build-and-run-vs-code)
4. [Run with Docker Compose](#4-run-with-docker-compose)
5. [Using the application](#5-using-the-application)
6. [Testing the data structures (Part 2)](#6-testing-the-data-structures-part-2)
7. [Data structure explanations](#7-data-structure-explanations)
8. [API reference](#8-api-reference)
9. [Configuration](#9-configuration)
10. [Project structure](#10-project-structure)
11. [Troubleshooting](#11-troubleshooting)

---

## 1. Architecture

```mermaid
flowchart LR
    subgraph Devices["ESP32 fleet (SmartX.DeviceSimulator)"]
        D1[MOIST-B2<br/>float] & D2[METER-L2<br/>int] & D3[PUMP-A<br/>bool]
    end
    subgraph API["SmartX.Api - ASP.NET Core Minimal API (.NET 10)"]
        E[Edge validation<br/>Dictionary O(1) MAC lookup] --> L{Severity?}
        L -- critical --> PQ[PriorityQueue]
        L -- normal --> Q[Queue&lt;T&gt;]
        PQ & Q --> P[StreamProcessor<br/>BackgroundService]
        P --> S[TelemetryPacket&lt;T&gt;<br/>jagged rows → List&lt;T&gt;]
        P --> H[SortedDictionary timeline<br/>+ AVL archive]
        P --> A[AlertService<br/>HashSet de-dup]
        A --> I[InsightEngine<br/>predictive actions]
        G[TopologyService<br/>Graph · BFS · DFS · Prim's MST]
    end
    subgraph Web["SmartX.Web - Blazor (interactive server)"]
        U1[Ingestion & Anomaly Radar] & U2[Command Stream] & U3[Topology]
    end
    Devices -- "HTTP POST /api/telemetry/{float|int|bool}" --> E
    Web -- "async HttpClient (JSON)" --> API
```

* **SmartX.Core**: a class library shared by every project. It holds the generics, operator overloads, jagged-array buffer, custom ledger collection, AVL tree, min-heap, mesh graph algorithms, recursive validators and the API contracts.
* **SmartX.Api**: an ASP.NET Core Minimal API and the only receiver of telemetry. All state is in memory in thread-safe singletons, and it seeds about 92 000 historical readings on start-up.
* **SmartX.Web**: a Blazor Web App (interactive server render mode, all C#). It talks to the API only over HTTP.
* **SmartX.DeviceSimulator**: a console app that behaves like the ESP32 fleet. Every sensor POSTs its reading to the API once per second. It simulates brown-outs (a node goes silent, then backfills a jagged batch), load surges, and a "leaky" irrigation bay that reacts to pump commands.

---

## 2. Prerequisites

| Tool | Version | Notes |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | **10.0.100 or later** | `dotnet --version` must print 10.x |
| [VS Code](https://code.visualstudio.com/) | latest | install the recommended extensions when prompted: **C# Dev Kit**, C#, Docker |
| Git | any | for the GitHub repository |
| Docker Desktop | optional | only for section 4 |

An internet connection is needed the first time you build, because `dotnet restore` downloads the Blazor framework assets from NuGet.

---

## 3. Restore, build and run (VS Code)

Open the **`SmartX`** folder (the one containing `SmartX.sln`) in VS Code.

### 3.1 Restore and compile

```powershell
dotnet restore SmartX.sln
dotnet build SmartX.sln
```

You should see `Build succeeded. 0 Warning(s) 0 Error(s)`.

### 3.2 Boot the three processes

Open **three terminals** in VS Code (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>`</kbd>) and run one command in each, **in this order**:

```powershell
# Terminal 1 - backend API (http://localhost:5080)
dotnet run --project src/SmartX.Api

# Terminal 2 - Blazor dashboard (http://localhost:5090)
dotnet run --project src/SmartX.Web

# Terminal 3 - ESP32 fleet simulator (sends live telemetry to the API)
dotnet run --project src/SmartX.DeviceSimulator -- --api http://localhost:5080
```

Then open **http://localhost:5090** in your browser.

> **One-click alternatives:**
> * **Run and Debug** panel → pick **"Smart-X: API + Web + Simulator"** → ▶ (debugs all three at once).
> * Command palette → *Tasks: Run Task* → **run: everything**.

Simulator options: `--interval 500` (milliseconds between ticks) and `--no-dropouts` (disable random brown-outs).

### 3.3 Check it works

```powershell
curl http://localhost:5080/health
# {"status":"healthy","service":"Smart-X Gateway API","devices":32,...}
```

---

## 4. Run with Docker Compose

```powershell
docker compose up --build
```

| Container | URL |
|---|---|
| `smartx-web` | http://localhost:5090 |
| `smartx-api` | http://localhost:5080 |
| `smartx-simulator` | posts to `http://api:8080` on the compose network |

Encrypted uploads are kept in the `smartx-attachments` volume. Stop everything with `docker compose down` (add `-v` to wipe uploads).

---

## 5. Using the application

### Gateway home

The landing page shows the three architectural pillars plus live fleet KPIs. Disabled pillars are greyed out with a lock (see `ActivePillars` in [Configuration](#9-configuration)).

### 📡 Part 1: Sensor ingestion & telemetry (`/ingestion`)

| Tab | What to try |
|---|---|
| **Anomaly Radar** | The engagement strategy. Each row is a sensor and each column is a 10-second bucket. Colour = rolling z-score, so spikes glow red; a hatched grey gap means the node stopped reporting (dropout). The page also has a health score ring, a spike/dropout feed, heartbeat pulses, and an **issue-triage list** (report, acknowledge, resolve) with mean time to acknowledge, a triage streak and badges. |
| **Register sensor** | Enter a MAC (or click *Generate ESP32 MAC*), a name, a category → metric, and Facility → Zone → Sub-zone → Node. Choosing a node runs the **recursive path validator** live (e.g. `Node B-02 → Sub-Zone B → Zone 1 · Grow Hall → Facility A`). Client-side DataAnnotations validation runs first; server-side errors (duplicate MAC, full node, unsafe path) appear under the matching field. |
| **Device registry** | Search and filter all sensors. Click a row to open the **device profile**, where you can upload config files, photos and logs (multipart, streamed, AES-256-GCM encrypted). `.json` configs are validated recursively, and photos preview inline. |
| **Pipeline & OOP** | A live view of the jagged staging rows filling and flushing into `List<T>`, plus the **operator-overloading demo**: pick two meters and the API evaluates `meter1 + meter2`, `-`, `/` and `>`. |

Add a node to the deployment tree with a power demand larger than its parent's capacity (e.g. 500 W under *Sub-Zone A*) to see the recursive validator report a power overload.

### ⚡ Part 2: Real-time command stream (`/stream`)

* **KPI strip**: FIFO depth and latency, priority depth and latency, throughput, duplicates suppressed by the HashSets, and undo-stack depth.
* **Devices**: search (press Enter; searches are recorded for the prediction engine), category filter and alert-state chips (*Alerting, Critical, Warning, Disconnected, Healthy*). Click a card to see its `SortedDictionary` timeline and, for actuators, the **Switch ON / OFF / Toggle** override buttons.
* **Suggested actions**: predictions with a confidence score and an **Apply** button. *Learned patterns* shows what the engine has learned.
* **Command history**: the undo stack (top = next to be reverted), plus batch commands (*Shut all valves*, *Stop all pumps*, *All relays on*). A batch command is undone as one unit.
* **Processing order**: the last 30 packets with their lane (`FIFO` / `PRIORITY`) and latency.
* **Unique alert sets**: the live contents of both HashSets.

**Demo storyline (about 2 minutes with the simulator running):** `MOIST-B2` sits in a leaky bay and dries out. When it drops below 15 % it raises a **critical** reading, which goes through the priority lane. A suggestion card then appears: *"Switch ON PUMP-A, 78 %, in 7 of 9 past responses operators did this"*. Click **Apply**, and the simulator sees the pump running, so moisture climbs back up. Click **Undo last** to revert it.

### 🕸️ PoE: Network topology & mesh routing (`/topology`)

* Switch between **Facility A** (farm) and **Facility B** (sub-station).
* The SVG map shows all radio links, the **MST backbone** (bold) and a traced route (animated, flowing toward the gateway).
* Click a node, or type a name or MAC and press **Trace route**. The route panel compares the **MST path**, the **fewest-hop BFS path** and the **cheapest-energy Dijkstra path**.
* **Fail next hop** powers off the first relay on the route. The MST and BFS are recomputed, the route re-heals, and the *Self-healing* panel lists which neighbours depended on the failed node and where they hop now. Click the failed node's chip to restore it.
* **BFS from the gateway**: every reachable node with its hop distance. Offline nodes and cut-off islands (from the DFS components) are listed separately.
* **Historical telemetry**: pick a sensor and a window (quick buttons include *Today 14:00–16:00*) to run an AVL range query. It shows the number of results, **tree nodes visited vs. tree size**, tree height, rotations and the query time in µs.

---

## 6. Testing the data structures (Part 2)

Make sure the API is running (`dotnet run --project src/SmartX.Api`). The examples are in **PowerShell** (Windows) with the **curl** equivalent underneath.

### 6.1 Simulate a high-priority alert (PriorityQueue bypass)

**From the UI:** on `/stream`, click **Flood + critical**. This queues 3 000 normal packets in the FIFO lane, then sends one critical reading. In *Processing order* the `PRIORITY` row appears at the top while the FIFO backlog is still draining, and its latency is a fraction of the FIFO latency.

**From the command line:**

```powershell
# 1. Build a FIFO backlog
Invoke-RestMethod -Method Post "http://localhost:5080/api/stream/simulate/flood?count=5000"
# 2. Fire a critical reading for MOIST-B2 (soil moisture 9 % < 15 % critical limit)
Invoke-RestMethod -Method Post "http://localhost:5080/api/stream/simulate/critical?device=MOIST-B2"
#    → lane: PRIORITY
# 3. Compare the two lanes
Invoke-RestMethod "http://localhost:5080/api/stream/stats" | Select fifoDepth, priorityDepth, avgFifoLatencyMs, avgPriorityLatencyMs
```

```bash
curl -X POST "http://localhost:5080/api/stream/simulate/flood?count=5000"
curl -X POST "http://localhost:5080/api/stream/simulate/critical?device=MOIST-B2"
curl http://localhost:5080/api/stream/stats
```

You can also post a reading like a real device would. Any value outside the metric's critical band is routed to `PRIORITY`:

```powershell
$mac = (Invoke-RestMethod "http://localhost:5080/api/devices?query=METER-L2").devices[0].mac
Invoke-RestMethod -Method Post "http://localhost:5080/api/telemetry/int" -ContentType "application/json" `
  -Body (@{ mac = $mac; metric = "power_w"; value = 4200 } | ConvertTo-Json)
# → { "accepted": true, "lane": "PRIORITY", "severity": "Critical", ... }
```

### 6.2 Dictionary O(1) lookup and its validation

```powershell
# Unknown MAC → rejected by the O(1) Dictionary lookup
Invoke-RestMethod -Method Post "http://localhost:5080/api/telemetry/float" -ContentType "application/json" `
  -Body '{"mac":"24:6F:28:00:00:01","metric":"soil_moisture","value":40}'
# → 400 "Device 24:6F:28:00:00:01 is not registered with this gateway."

# Wrong type for the device → rejected (MOIST-B2 publishes float, not int)
$mac = (Invoke-RestMethod "http://localhost:5080/api/devices?query=MOIST-B2").devices[0].mac
Invoke-RestMethod -Method Post "http://localhost:5080/api/telemetry/int" -ContentType "application/json" -Body (@{ mac=$mac; value=5 } | ConvertTo-Json)
```

The `elapsedMs` field in `GET /api/devices` shows how long the lookup and filter took (typically under 0.1 ms for the whole fleet).

### 6.3 SortedDictionary timeline

```powershell
Invoke-RestMethod "http://localhost:5080/api/devices/$mac/timeline?minutes=5" | Select -Expand points | Select -First 5
```

The points come back already in timestamp order, even when a device backfills older readings after a brown-out.

### 6.4 Stack undo

```powershell
$pump = (Invoke-RestMethod "http://localhost:5080/api/devices?query=PUMP-A").devices[0].mac
Invoke-RestMethod -Method Post "http://localhost:5080/api/commands" -ContentType "application/json" -Body (@{ mac=$pump; action="On" } | ConvertTo-Json)
Invoke-RestMethod -Method Post "http://localhost:5080/api/commands" -ContentType "application/json" -Body '{"scope":"AllOfMetric","metric":"valve_state","action":"Off"}'
(Invoke-RestMethod "http://localhost:5080/api/commands").undoStack | Select description   # newest on top
Invoke-RestMethod -Method Post "http://localhost:5080/api/commands/undo"                    # reverts ALL valves at once
```

### 6.5 HashSet de-duplication

Stop the simulator (Ctrl+C in its terminal). After about 12 seconds every node goes offline, and the watchdog re-reports them every 2 seconds. Watch **Duplicates suppressed** climb on `/stream` while **Disconnected nodes** stays at one entry per node and the alert feed shows a single `DISCONNECTED` alert per node. Restart the simulator and the nodes are removed from the set, each with one `RECONNECTED` entry.

### 6.6 Predictive engine

If the simulator is running, the device's next normal reading clears an injected alert about a second later, just as real hardware would. Either stop the simulator first, or wait about 2.5 minutes for MOIST-B2 to dry out naturally (the simulator models it as a leaky bay).

```powershell
Invoke-RestMethod "http://localhost:5080/api/stream/simulate/critical?device=MOIST-B2" -Method Post
(Invoke-RestMethod "http://localhost:5080/api/insights").suggestions | Select title, confidence, reason
# → "Switch ON PUMP-A" 0.78  "MOIST-B2 is critical low. In 7 of 9 past responses ... operators did this."
```

Search for a device on `/stream` and then command another one. Repeat this a few times and the *Learned patterns* table shows the new sequence.

### 6.7 Jagged backfill batch

```powershell
Invoke-RestMethod -Method Post "http://localhost:5080/api/telemetry/batch" -ContentType "application/json" -Body (@{
  mac = $mac; metric = "soil_moisture"; startUtc = (Get-Date).ToUniversalTime().AddMinutes(-5).ToString("o"); intervalMs = 5000
  batches = @(@(40,41,42), @(43,44), @(45,46,47,48))
} | ConvertTo-Json -Depth 4)
# → "Flattened 3 jagged rows (3 + 2 + 4) into 9 readings."
```

---

## 7. Data structure explanations

### 7.1 Part 1: advanced OOP

#### Generics: `TelemetryPacket<T>` (no boxing)
`src/SmartX.Core/Telemetry/TelemetryPacket.cs`

```csharp
public readonly struct TelemetryPacket<T> : IEquatable<TelemetryPacket<T>>
    where T : struct, IEquatable<T>
{
    public T Value { get; }          // stored inline - never as object
    public bool Equals(TelemetryPacket<T> other) =>
        Value.Equals(other.Value)    // constrained call → no boxing
        && TimestampUtc == other.TimestampUtc && ...;
}
```

Because the wrapper is a **struct** and `T : struct`, the JIT compiles a separate specialised copy for `float`, `int` and `bool`. The API has **one typed endpoint per T** (`/api/telemetry/float|int|bool`), binds `TelemetryRequest<T>`, and stores each packet in a `TypedTelemetryStore<T>`. Packets of different types share a single `Queue<TelemetryEnvelope>` through a **tagged-union struct** (`TelemetryEnvelope`), which has one inline slot per primitive and a `ValueKind` discriminator, so nothing is ever cast to `object`. Converting to `double` for charts uses `Unsafe.As<T, float>`, which reinterprets the value in place rather than boxing it.

#### Operator overloading
* **C# 14 extension operators** on the generic packet. Arithmetic is only available when `T` is numeric:
  ```csharp
  extension<T>(TelemetryPacket<T>) where T : struct, IEquatable<T>, INumber<T>
  {
      public static TelemetryPacket<T> operator +(TelemetryPacket<T> l, TelemetryPacket<T> r) => ... l.Value + r.Value ...;
      public static bool operator >(TelemetryPacket<T> l, TelemetryPacket<T> r) => l.Value > r.Value;
  }
  extension(TelemetryPacket<bool>) { operator |  /* any valve open? */,  operator &  /* all pumps on? */ }
  ```
* **`SmartMeterReading`** (`+`, `-`, `/`, `*`, `>`, `<`, `>=`, `<=` plus a `reading > int` breaker-limit check):
  ```csharp
  SmartMeterReading meter3 = meter1 + meter2;   // aggregate load
  bool trip = meter3 > 3500;                    // over the breaker limit?
  ```
  This is used by `GET /api/telemetry/aggregate` and the *Pipeline & OOP* tab.
* **`EnvironmentalSample`**: `(s1 + s2 + s3) / 3` for averaging, `latest - baseline` for drift.

#### Jagged / multi-dimensional arrays → `List<T>`
* **`JaggedStagingBuffer<T>`** (`T[][]`): each row is a sequential burst. Rows are allocated once and **reused** after every flush (no garbage in steady state). When all rows are full, `FlushInto(List<T>)` block-copies each row with span-based `AddRange`. A device's offline backfill becomes **its own row of a different length**, which is the reason for using a jagged array.
* **`TelemetryLedger<T>`**: a **custom bounded collection** wrapping `List<T>`. It appends spans, trims the oldest 10 % in one `RemoveRange`, and returns `Tail(n)` as a zero-copy `ReadOnlySpan<T>`.
* **`POST /api/telemetry/batch`** accepts a `float[][]` of uneven bursts and flattens it in order into `List<float>`.
* **Multi-dimensional `double[rows, columns]`**: the Anomaly Radar heatmap grid (devices × time buckets) in `AnomalyDetector.BuildHeatmap`.

#### Recursion
* **`DeploymentValidator.Validate`** walks Facility → Zone → Sub-Zone → Node. It checks the tier order, unique ids, enabled branches and **power demand vs. capacity**, summing demand back up the call stack. Base cases: a leaf Node, a depth greater than 16, and an already-visited id (which also guards against cycles).
* **`ValidatePath`** finds the node recursively (DFS with backtracking on a stack) and validates every ancestor, e.g. *Node B-02 → Sub-Zone B → Zone 1 → Facility A*.
* **`ConfigProfileValidator`** recursively walks uploaded JSON config profiles (objects and arrays at any depth) and range-checks keys such as `sampleRateMs`, `txPowerDbm`, `wifiChannel` and `thresholds.min < max`.

#### Dynamic engagement: the Anomaly Radar
A device × time heatmap of rolling z-scores, built from a 60-sample ring buffer per device. Grey gaps make disconnects visible at a glance. It is combined with heartbeat pulses, a spike feed and an issue-triage list (`List<IssueReport>`) that has mean time to acknowledge, a triage streak and badges.

### 7.2 Part 2: real-time stream

| Requirement | Implementation | File |
|---|---|---|
| FIFO message queue | `Queue<TelemetryEnvelope>` | `StreamProcessor.cs` |
| Priority queue | `PriorityQueue<TelemetryEnvelope, (int NegSeverity, long Seq)>`, which is checked **before every dequeue** | `StreamProcessor.cs` |
| Undo history | `Stack<CommandRecord>`. Each record stores the *previous* state of every actuator it touched | `CommandService.cs` |
| Live device registry | `Dictionary<string, SensorDevice>` keyed by normalised MAC, giving an O(1) lookup per packet | `DeviceRegistry.cs` |
| Timeline logs | `SortedDictionary<DateTime, double>` per device (always time-ordered) | `HistoryService.cs` |
| Unique error / disconnect sets | `HashSet<string>` (disconnected nodes) and `HashSet<AlertKey>` (device + error code) | `AlertService.cs` |
| Predictive engine | nested `Dictionary` count models (condition → action, Markov sequence) and a `PriorityQueue` top-N | `InsightEngine.cs` |

```csharp
// StreamProcessor - the bypass: the critical lane is checked first on EVERY dequeue
if (_priority.TryDequeue(out env, out _)) { lane = "PRIORITY"; return true; }
if (_fifo.TryDequeue(out env))            { lane = "FIFO";     return true; }
```

```csharp
// AlertService - O(1) duplicate suppression
if (!_disconnected.Add(mac)) { _duplicatesSuppressed++; return false; }
```

**Predictive engine:** whenever an operator searches, inspects or commands a device, the engine records it against the alert conditions that were active at that moment. It keeps three models:

1. **Condition → action counts.** *"While MOIST-B2 was CRITICAL_LOW, operators switched PUMP-A ON 7 of 9 times."* These counts are also generalised to the metric level, so a new sensor benefits from the history of similar ones.
2. **A first-order Markov chain over actions.** *"After inspecting METER-L2, operators switch RELAY-BLK1 OFF."*
3. **A live fault spotlight**, ranked by severity and anomaly score, plus two domain rules (irrigation and load-shedding).

Candidates from all three are merged and ranked by score using a heap. The engine starts with a week of seeded operator history so it is useful on the first run.

### 7.3 PoE: trees, graphs and MST (Network Topology feature)

#### AVL tree: historical range queries
`src/SmartX.Core/Collections/AvlTree.cs`

Each sensor's archive is an `AvlTree<long ticks, double value>`. Inserts rebalance with at most two rotations (LL, LR, RR or RL cases), so the height stays at or below 1.44·log₂ n. A range query **prunes** every subtree that cannot overlap the window:

```csharp
while (current is not null)
{
    visited++;
    if (current.Key.CompareTo(from) < 0) { current = current.Right; continue; } // whole left side too old → skip
    stack.Push(current);
    current = current.Left;
}
var node = stack.Pop();
if (node.Key.CompareTo(to) > 0) break;     // everything after is too new → stop
```

**Why it matters:** with about 2 900 readings per sensor, a 2-hour window visits about **130 nodes instead of 2 900**. Cost is O(log n + k) instead of O(n). The UI shows *nodes visited / tree size*, tree height and rotations, so the saving is visible. Retention uses `RemoveMin()` (O(log n)) so the archive stays bounded.

#### Graph: the mesh
`src/SmartX.Core/Topology/MeshGraph.cs` (plus `TopologyService.cs`, which builds the graph)

Vertices are ESP32 nodes plus the gateway. Edges are radio links between nodes within range, weighted by **transmit energy (mJ)**, which grows with distance² like real RF path loss. RSSI is shown in dBm. The graph is stored as an **adjacency list** (`Dictionary<string, List<MeshEdge>>`), which is ideal for a sparse wireless mesh: O(1) vertex lookup and O(degree) neighbour iteration. If layout leaves an island, it is bridged so the mesh starts out connected.

#### BFS and DFS
* **`BreadthFirst(graph, gateway)`** traverses only through **online** nodes and returns hop counts (BFS guarantees the minimum), a parent map for rebuilding any route, the visit order, `UnreachableOnline` (nodes that are alive but cut off) and `Offline`. Cost is O(V + E).
* **`ConnectedComponents`** (iterative DFS with a `Stack<string>`) splits the live mesh into islands ("subnets").
* **Self-healing** compares each node's next hop on the *healthy* MST with the *live* MST after a failure, giving output like *"MOIST-B2 now hops via MOIST-B1 (8 hops, 51.4 mJ)"*.

#### Minimum spanning tree: Prim's algorithm with a binary min-heap
`MeshAlgorithms.PrimMst` + `src/SmartX.Core/Collections/MinHeap.cs`

```csharp
inTree.Add(root);
foreach (var e in graph.Neighbours(root)) heap.Push(e, e.Cost);
while (heap.TryPop(out var edge, out var cost))
{
    if (inTree.Contains(edge.A) && inTree.Contains(edge.B)) continue; // would form a cycle
    var v = inTree.Contains(edge.A) ? edge.B : edge.A;
    if (!graph.TryGetVertex(v, out var vx) || !vx.IsOnline) continue;  // can't relay through a dead node
    inTree.Add(v); mst.Add(edge); total += cost;
    foreach (var next in graph.Neighbours(v)) if (!inTree.Contains(next.Other(v))) heap.Push(next, next.Cost);
}
```

Prim's grows the tree outward from the gateway and always takes the cheapest edge that reaches a new online node. The result connects every reachable sensor using the **minimum total transmit energy**, which is how Zigbee, Thread and ESP-NOW-style meshes keep batteries alive. Cost is O(E log V). In the seeded farm, the MST uses about **57 % less energy than using every link**. A node's route to the server is its path along the MST, found by `RouteOverTree`. For comparison, the UI also shows the fewest-hop BFS route and a per-node **Dijkstra** cheapest path, which uses the same heap.

---

## 8. API reference

| Method | Route | Purpose |
|---|---|---|
| GET | `/health` | liveness |
| GET | `/api/gateway/status` | pillars + fleet KPIs |
| GET | `/api/metrics` | metric catalogue (type, unit, normal/critical bands) |
| GET | `/api/devices?query=&category=&state=&record=` | search/filter (`state` = alerting, critical, warning, disconnected, healthy) |
| GET | `/api/devices/{mac}` | one device |
| POST | `/api/devices` | register (server-side validation → 400 ValidationProblem) |
| GET | `/api/devices/{mac}/timeline?minutes=` | SortedDictionary timeline |
| POST | `/api/devices/{mac}/inspect` | records an "inspect" action for the predictive engine |
| GET/POST | `/api/devices/{mac}/attachments` | list / multipart upload (streamed, encrypted) |
| GET/DELETE | `/api/devices/{mac}/attachments/{id}` | download (decrypted) / delete |
| GET | `/api/deployment` | deployment tree + recursive validation |
| GET | `/api/deployment/validate/{nodeId}` | recursive path validation |
| POST | `/api/deployment/nodes` | add a child node (tree is re-validated) |
| POST | `/api/telemetry/float` · `/int` · `/bool` | typed ingestion → `TelemetryPacket<T>` |
| POST | `/api/telemetry/batch` | jagged `float[][]` backfill |
| GET | `/api/telemetry/pipeline` | jagged staging rows / ledger stats |
| GET | `/api/telemetry/aggregate?a=&b=` | operator-overloading demo (`Meter1 + Meter2`) |
| GET | `/api/engagement/radar` | Anomaly Radar heatmap, heartbeats, spikes, issues |
| POST | `/api/engagement/issues` · `/issues/{id}/ack` · `/issues/{id}/resolve` | issue triage |
| GET | `/api/stream/stats` | queue depths, latencies, lanes, HashSets, alert feed |
| POST | `/api/stream/simulate/flood?count=` · `/simulate/critical?device=` | load-test helpers |
| GET/POST | `/api/commands` | undo stack + log / issue a command |
| POST | `/api/commands/undo` | pop the stack |
| GET | `/api/commands/actuators` | desired actuator states (polled by devices) |
| GET | `/api/insights` | suggested actions + learned patterns |
| GET | `/api/topology?facility=` | graph, MST, BFS, DFS components, healing |
| GET | `/api/topology/route?query=` | MST / BFS / Dijkstra routes for one node |
| POST | `/api/topology/nodes/{mac}/toggle` | simulate a node failure / repair |
| GET | `/api/history/{mac}?from=&to=` | AVL range query (UTC ISO-8601) |

---

## 9. Configuration

`src/SmartX.Api/appsettings.json`

| Key | Default | Meaning |
|---|---|---|
| `Gateway:ActivePillars` | `3` | `1` = Part 1 (pillars 2 and 3 disabled), `2` = Parts 1–2, `3` = full PoE. Useful for demoing each submission stage. |
| `Gateway:MaxPacketsPerTick` / `TickMilliseconds` | `300` / `100` | Simulated gateway CPU budget (about 3 000 packets/s), so floods build a visible backlog |
| `Gateway:StaleAfterSeconds` / `OfflineAfterSeconds` | `5` / `12` | Heartbeat thresholds |
| `Gateway:SeedHistoryHours` / `SeedIntervalSeconds` | `48` / `60` | Mock history seeded into the AVL trees on start-up |
| `Attachments:MaxFileBytes` | 15 MB | Upload limit per file |
| `Attachments:EncryptionKeyBase64` | *(empty)* | 32-byte AES key. If empty, a key is generated in `App_Data/attachments/.attachment.key` (git-ignored) |

`src/SmartX.Web/appsettings.json`: `SmartXApi:BaseUrl` (default `http://localhost:5080`).

---

## 10. Project structure

```
SmartX/
├─ SmartX.sln
├─ docker-compose.yml · global.json · .vscode/ (launch, tasks, extensions)
└─ src/
   ├─ SmartX.Core/                     shared library (no external packages)
   │  ├─ Telemetry/    TelemetryPacket<T> + extension operators, SmartMeterReading,
   │  │                TelemetryEnvelope (tagged union), MetricCatalog, SignalModel
   │  ├─ Collections/  JaggedStagingBuffer<T>, TelemetryLedger<T>, AvlTree<K,V>, MinHeap<E,P>
   │  ├─ Deployment/   DeploymentNode, DeploymentValidator (recursive), ConfigProfileValidator (recursive)
   │  ├─ Topology/     MeshGraph, BFS, DFS components, Prim's MST, Dijkstra
   │  ├─ Devices/      MacAddress, enums
   │  └─ Contracts/    DTOs shared by API + Web + Simulator
   ├─ SmartX.Api/                      ASP.NET Core Minimal API
   │  ├─ Endpoints/    Ingestion (Part 1), Stream (Part 2), Topology (PoE)
   │  ├─ Services/     DeviceRegistry, StreamProcessor, AlertService, CommandService, InsightEngine,
   │  │                HistoryService, EngagementService, TopologyService, AttachmentStore, DataSeeder…
   │  └─ Security/     ChunkedAesGcm (streaming AES-256-GCM)
   ├─ SmartX.Web/                      Blazor Web App (interactive server)
   │  ├─ Components/Pages/   Home, Ingestion, DeviceProfile, CommandStream, Topology
   │  ├─ Components/Shared/  RadarHeatmap, MeshMap, LineChart, PipelineVisual, Toasts, StatusPill, PillarGuard
   │  └─ Services/           SmartXApiClient, UiState (per-user state across pages), LiveComponentBase
   └─ SmartX.DeviceSimulator/          ESP32 fleet simulator (console)
```

---

## 11. Troubleshooting

| Symptom | Fix |
|---|---|
| Dashboard says **"API unreachable"** | Start the API first (`dotnet run --project src/SmartX.Api`). The dashboard reconnects on its own. |
| All sensors show **Offline** | The simulator isn't running. Start it in a third terminal. |
| `NETSDK1045` / wrong SDK | Install the .NET 10 SDK and check `dotnet --version`. |
| Port 5080/5090 in use | Change `applicationUrl` in `src/*/Properties/launchSettings.json`, and update `SmartXApi:BaseUrl` and the simulator's `--api` argument to match. |
| Restore fails | The first build needs internet access to nuget.org (for the Blazor framework assets). |
| Want a clean slate | Stop the API. All state is in memory, so a restart re-seeds. Delete `src/SmartX.Api/App_Data` to remove uploads. |
