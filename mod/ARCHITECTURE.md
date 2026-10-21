# Simulation and transport boundaries

The host orders commands and fixed 20/30/60 Hz steps. Every cluster remains mandatory;
camera LOD and optional work budgets cannot change shared simulation results. Native
cluster workers (up to eight, CPU dependent) finish at the game's normal main-thread
barrier before mutations, snapshots, checksums or publication. Worker support alone
does not establish determinism; see TESTING.md for measured agreement.

ENet has one dedicated native owner thread. It services acknowledgements during Unity
loading. Only immutable bytes and connection-generation keys cross its queues; game
callbacks execute on the main thread. Managed queues are capped at 4096 items / 64 MiB.
Outgoing reservations survive native enqueue until packet-free callbacks: 32 MiB per
peer, 64 MiB and 4096 packets globally. The game inbox is capped at 256 items / 16 MiB;
transport polling applies backpressure before filling it. A one-second real-time
heartbeat acknowledges completed steps. The host permits at most two simulation
seconds of unacknowledged progress, slowing shared simulation for a slow replica.

Periodic checks cover 256 spatial partitions in a fixed, validated cycle. Four checks
are scheduled per 1/12 simulated second and processed within a main-thread time budget.
Each check reads live production state at one completed, ordered step; hashes are never
assembled from data captured across different steps. Backlog bounds slow simulation
instead of dropping checks. Counts, configuration, signals, research and trains/cargo
remain covered. Cargo maps are sorted by coordinates; their full native state is kept.
Topology events invalidate cached sorted coordinates, current models are resolved on
read, and observer/count disagreements stop the session.

Admissions are serialized. The host pauses at a complete boundary, captures immutable
save blobs and a full world digest, then packages only those bytes on a
single background worker. The client loads on the main thread, verifies actual loaded
state against the frozen digest and acknowledges that exact step/revision. Only then
does simulation resume. Existing replicas prepare the same boundary in ordered input.
Disconnect/reconnect uses fresh identities; obsolete queued work cannot enter a new
session. A checksum mismatch stops the replica; it must reload from the host.

Capture, full join validation and native loading still have a substantial one-time
cost on huge worlds. Normal periodic checks avoid full-world traversal. Shared saves
remain host-owned. ZIP/source packaging allowlists exclude game assemblies and QA data.
