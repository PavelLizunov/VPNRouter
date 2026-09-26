# Omarchy transport differential review

## Scope and evidence

Coordinator plus independent Gemini read-only review of Headless Protocol,
Program and RouterBackendHandler. Product snapshot `3ddce2f14cf5f767cb85d042728836e98fc46251`
was compared with `0b8fbacc0c2697de97b33627f18e037d28a8cc8b` for output deadline
changes. Later snapshot `51c36dd2c58f9a897318148ad1a31ebe69a37863` adds the real
subprocess stdout-stall regression and passed 33 Headless groups. No live VPN,
privileged component or production settings were exercised.

## Findings and coordinator disposition

### MEDIUM / Confirmed: async response waiters exceed queue bounds

`ProtocolDispatcher.DispatchAsync` releases its execution slot in finally before
`ProtocolServer` awaits response enqueue. With open non-reading stdout, paced
asynchronous requests can finish their handlers and accumulate pending response
waiters outside the eight-frame output queue. Prerequisite: a client controlling
the backend stdin/stdout pipes. Impact: memory growth and continuing mutations
while delivery is stalled. The three-second output deadline limits duration,
but is not a count bound.

Tracked as OMARCHY-RESPONSE-INFLIGHT in OPEN-DEFECTS. Coordinator added a separate
five-task transport population cap before dispatch (one ordinary plus four
urgent slots), with a bounded inline busy response when full. This does not
permit concurrent ordinary handler executions or move snapshot to an urgent
lane. The paced-async regression reproduced failure on red snapshot
`42542937b14f75ed26c49ab017cb0b0af3a96625`: 50 handler invocations exceeded the
14-frame/task aggregate bound. The same test passed on green snapshot
`188e011ea8779e5cab0a60fa428748f3459124c0`. The snapshots differ only in Server;
this is executed red/green evidence, unlike the reviewer's illustrative pseudocode.

### Confirmed earlier: permanent output stall lacked shutdown

Tracked as OMARCHY-OUTPUT-STALL. A deadline now covers each frame's complete
write/newline/flush sequence. Only one payload invocation is pending; token checks
prevent a late return from starting its next write after cancellation. Synchronous
blocking before WriteAsync returns is isolated from the timeout waiter. Handler
disposal begins independently of stdout drain and has an external deadline.

Real OS-pipe check on snapshot3ddce2f1: exit0 after 4.68s with stdin open,
stdout never read, no data files. The persisted regression passes in snapshot51c36dd2.
Uncooperative pending I/O may remain until process exit; its data/token lifetime
is retained rather than unsafely disposed. This is bounded abandonment, not proof
that the underlying OS call was interrupted.

### False positive: ordinary snapshot busy rejection

Snapshot is deliberately ordinary. Its inability to bypass an active operation
is the approved contract, not a defect.

### Accepted tradeoff: input reader pauses on full output queue

The reviewer labeled synchronous EnqueueResponseAsync backpressure a secondary
defect. Coordinator rejects that classification as stated: bounded read
backpressure is intentional, and an indefinitely stalled peer triggers shutdown.
Urgent controls already in unread input cannot bypass a blocked pipe consumer;
no promise of immediate control delivery through a dead transport is made.

## Review limitations

- This is scoped transport review, not whole-product security acceptance.
- A bounded handler disposal does not prove that every synchronous handler
  invocation or event producer is nonblocking; synchronous filesystem work
  remains a separate surface.
- Coordinator found OMARCHY-CONTROL-VALIDATION after the review: cancel ignored
  unknown fields and invalid target syntax; disconnect cancelled active work
  before backend parameter rejection. Validation now precedes effects. Snapshot
  `a1e31c1284ac928a55544e4d2b7f25259cde8705` passed 35 groups including the
  rejected-control/no-side-effects regression. The reviewer's original broad
  claim of complete per-method validation was not accepted.
- Full Core tests, live popup/input/scaling, routing dataplane and privileged
  readback remain unverified or outside present authority.
- Root-helper architecture awaits explicit owner approval. No approval is implied
  by automated goal continuation or this report.

## Follow-up: synchronous handlers and precancelled persistence

Coordinator reproduced OMARCHY-SYNC-HANDLER: a handler blocking before returning
its Task occupied the reader, preventing urgent cancel processing. Dispatcher
now reserves slots synchronously and schedules only admitted handler calls;
await/finally retains each slot and ordinary CTS until handler completion.
Red `4c47c39cef0939cd0ac22d4c68072a8e3092423a` failed only the new synchronous
handler regression (35 passed, 1 failed); green
`fb35e98ddbe987e9c03811e2ec55b3c2d835e098` passed all 36 groups (bash-36).
The regression separately exercises a blocking ordinary call and disconnect.

Coordinator also found OMARCHY-CANCELLED-MUTATION: an already-cancelled token
could reach settings persistence. A cancellation check now precedes the mutation
action. Red `9d4999cee8e543421ff462949a1011aac0b86281` failed the new storage
cancellation assertion. Candidate `2bc9072e28fd5373e65921d73257b5e4e6cbdead`
passed that regression but failed backpressure delivery (10 of 12 responses);
bash-37 exited 1 and was not accepted as green.

The backpressure test signalled EOF before pending async response enqueues had
settled. EOF intentionally cancels those enqueues. The test now retains its live
peer until all 12 unique response IDs arrive, then sends EOF in cleanup. The
separate paced-response test still checks bounded handler admission under a
stalled consumer. A write-block signal alone does not prove the queue is full.

Final executable snapshot `5edc14bfd5f550fcc61129122cb28c8d64e287ab`, bash-38:
Release build 31 warnings / 0 errors; two consecutive runs each passed 36 groups,
including 41 lifecycle checks, real subprocess SIGTERM/EOF/stdout stalls, and
precancelled settings preservation (config bytes and revision). Exit 0. A later
comment-only edit clarifies connected-apply compensation; no executable change.

Independent Gemini review initially returned an incomplete tool-call fragment,
which was not accepted. A second read-only review returned a scoped pass. The
coordinator accepts its slot/CTS lifetime analysis but rejects broader claims:
at most five handler calls is not a bound on all thread-pool tasks; Task.Run may
complete before dispatch returns; the test's gate does not itself prove input
admission paused; the cancellation check is not atomic with persistence. Later
cancellation and asynchronous mutations require their existing checks, not a
blanket claim that all mutation races are solved. Synchronous cancellation
callbacks and thread-pool starvation remain outside this regression's proof.
No live network or privileged behavior was exercised.

## Verdict

Changes required for overall acceptance. Scoped verified behaviors and remaining
issues are recorded in the phase outcome and OPEN-DEFECTS; implementation commits
and PR verification remain pending.
