# Solver Worker Protocol

`SolverWorker` accepts exactly one newline-delimited JSON request on standard input,
writes exactly one JSON response to standard output, then exits. Standard output is
reserved for the response; diagnostics belong on standard error.

This process-per-request boundary isolates the desktop host from native crashes and
lets the host kill a worker that ignores cancellation. Protocol version `2` is not
backward compatible by default: any schema change requires a new version and C#
contract tests. Protocol version `2` adds explicit exactly-one candidate groups for
compact personal-timetable blocking clauses.

## Host cancellation

Cancellation is deliberately outside this one-request protocol. The WPF bridge owns a
cancellation token per command; when the UI cancels a solve, `NativeSolverClient` stops
waiting and kills the isolated worker process tree. A terminated worker has no response
and is never treated as a SAT result. This provides a bounded cancellation path even if
the native solver is unable to cooperate during a solve call.

## Request

```json
{
  "protocol_version": 2,
  "request_id": "a-correlation-id",
  "variable_count": 4,
  "clauses": [[1, 2], [-1, -2], [-1, -3]],
  "exactly_one_groups": [[1, 2], [3, 4]],
  "max_solutions": 5,
  "timeout_milliseconds": 30000
}
```

- `request_id` is a non-empty string up to 128 characters and is echoed unchanged.
- `variable_count` is `0..2,000,000`.
- Every literal is a non-zero signed integer in `[-variable_count, variable_count]`.
- Empty clauses are valid and mean the CNF is immediately infeasible.
- `exactly_one_groups` is required. It is either empty for a generic CNF, or every
  positive variable appears exactly once across non-empty groups. Personal timetable
  personal-selection requests use one group per requested teaching team.
- `max_solutions` is `1..100`.
- `timeout_milliseconds` is `1..300,000`.
- The request body is limited to 64 MiB, two million clauses, and ten million
  literals. Unknown JSON fields are ignored so telemetry fields can be added without
changing version 2 semantics.

The worker declares all caller variables before adding clauses and sets CaDiCaL
`factor=0`; both rules preserve deterministic incremental blocking behavior.

## Response

```json
{
  "protocol_version": 2,
  "request_id": "a-correlation-id",
  "status": "feasible",
  "solutions": [[1, -2, 3, -4]],
  "metrics": { "elapsed_milliseconds": 11, "solve_calls": 2 },
  "message": ""
}
```

Each solution is a complete signed assignment, ordered by variable ID. When
`exactly_one_groups` is supplied, the worker adds one negated selected candidate from
each group as its incremental blocking clause. This blocks the returned personal
  timetable with a clause proportional to requested teaching-team requests, not candidate count. For a
generic request with no groups, it falls back to negating every model literal. Both
forms guarantee distinct returned solutions.

- `feasible`: at least one model was found; enumeration stopped after all models or
  `max_solutions`.
- `infeasible`: no model exists.
- `timed_out`: the CaDiCaL terminator reached the supplied deadline. It can include
  models found before the timeout.
- `invalid_request` and `internal_error` are worker failures. The C# client treats
  either as a protocol exception, never as a scheduling result.

The C# client verifies the echoed request ID, status invariants, complete assignment
shape, uniqueness, and satisfaction of every CNF clause before it exposes a model to
application code. Application orchestration must additionally materialize choices and
run PT-1 through PT-7 validation.
