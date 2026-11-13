# ADR 0001: Transactional outbox and at-least-once messaging

- Status: Accepted
- Date: 2026-11-13
- Context: ClaimFlowAPI intake and adjudication microservices

## Context

Intake must persist a validated claim, its service lines, status history, and the event that will
drive adjudication. Publishing to RabbitMQ inside the HTTP request would be a dual write: the
database commit and the broker publish can succeed or fail independently. A crash after commit but
before publish loses adjudication work; a crash after publish but before commit sends an event for
a claim that does not exist.

The system also needs to survive broker restarts, a temporarily unavailable adjudication consumer,
and duplicate delivery. Claim status updates must be idempotent because consumers are
at-least-once.

## Decision

Use a **transactional outbox**:

1. In the same EF Core `SaveChangesAsync` transaction, Intake inserts the claim, lines, patient,
   status history, and an `OutboxMessages` row containing the serialized `claim.submitted` payload.
2. A hosted `OutboxPublisher` polls unprocessed rows, publishes to the durable RabbitMQ topic
   exchange with publisher confirms, and only then marks `ProcessedAt`.
3. Delivery is at-least-once: a crash between publish and mark will republish on the next drain.
4. Adjudication records every consumed `eventId` in `ProcessedEvents` and checks it before doing
   work. The Intake `PUT /status` endpoint additionally treats the same event id as idempotent.
5. RabbitMQ consumer failures retry with exponential backoff, then publish to
   `claimflow.dead-letter`; malformed messages go straight to the DLQ.
6. Correlation IDs travel on the event envelope, RabbitMQ headers, and the status callback.

## Consequences

Positive:

- Claim persistence and event creation are atomic.
- Broker outages do not lose accepted claims; the outbox drains when the broker returns.
- Duplicate deliveries are harmless at the adjudication and status layers.
- Poison messages are isolated and inspectable.

Negative / trade-offs:

- Adjudication is eventually consistent with intake; the benchmark reports queue-inclusive latency.
- Duplicate events can be delivered; deduplication state (`ProcessedEvents`) grows and needs a
  retention policy.
- Outbox rows need pruning/archival in a long-running deployment.
- Ordering is not guaranteed across claims; each claim's transitions are still serialized by the
  status endpoint's idempotent update.
- The polling publisher and inbox are designed for a single active consumer/publisher process pair;
  horizontal scale-out needs a distributed claim on the outbox or a dedicated relay.

## Alternatives considered

- **Publish directly from the request**: simplest, but the dual write loses or invents work.
- **Change Data Capture / Debezium**: strong option for a larger platform, but adds operational
  infrastructure and is heavier than this project needs.
- **Broker transactions spanning DB and RabbitMQ**: RabbitMQ transactions do not include SQL
  Server, so they do not solve the atomicity problem.
- **Synchronous HTTP adjudication**: simpler failure semantics but couples availability and removes
  the queue-based load leveling this project is demonstrating.

## Operational notes

- `IX_Outbox_Pending` indexes pending rows; monitor `ProcessedAt IS NULL` age and the
  `claimflow.dead-letter` queue depth.
- The publisher uses publisher confirms and will retry the same outbox row; consumers must remain
  idempotent.
- The outbox payload is JSON with a correlation id and is consumed by `AdjudicationConsumer`.
- Reporting is deliberately decoupled: it reads committed claims/status history, not the queue.
