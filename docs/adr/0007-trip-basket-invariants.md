# ADR 0007: Transfers and the trip basket keep their invariants on the server

## Context

A user often wants a flight, a hotel and an airport transfer together, with one form and one payment. A basket that
the model fills and prices itself would reopen the problem ADR 0003 closed: the model could add services it was never
offered, carry a stale price into checkout, or book the same person several times under different services.

## Decision

- Transfers are a third service behind `ITransferBackend` (in-memory fake). They follow the flight and hotel pattern:
  a search stores a snapshot with a handle, `select_transfer` re-quotes with the backend and records that quote as the
  confirmed price, and no tool takes a price from the model. Transfers are booked only through the trip.
- The basket is a booking context of its own (service `Trip`). Its `trip_services` list is written only by the
  basket class, within a server-side allow-list (`Trip:AllowedServices`) and size limit (`Trip:MaxServices`). The tool
  schemas restrict the service names, and the basket re-checks them, so the model cannot add a service the context does
  not allow. A service can be added only when its own context holds a confirmed, fresh price.
- No price is stored in the basket. Every view and every submission recomputes the lines from the confirmed selection
  of each service. A new search for a service in the basket, or a price older than the confirmation window, turns its
  line into "needs price check" and blocks the form and the submission until it is confirmed again.
- One combined traveller form: the number of travellers is the largest party size among the services, passport fields
  are required only when a confirmed service needs them, and the same person (normalised name and date of birth)
  cannot be entered twice. Every service books the same de-duplicated manifest. Adding or removing a service clears the
  stored form, so the form is shown again for the changed trip.
- `submit_trip_booking` takes no arguments. It reads only the booking contexts, mints one persisted idempotency key,
  sends one submission with one line per service and a total that must equal their sum, and consumes the selections
  afterwards. A replay returns the same booking; adding to a booked trip starts a fresh one.

## Consequences

- The model can steer which services are in the trip but never what they cost, who travels, or what is submitted.
- Basket correctness does not depend on the model remembering to re-check prices: stale lines are caught at checkout.
- Recomputing lines costs a few context reads per basket call; the contexts are small and keyed by session.
- The trip is capped at three services and one currency. More services or currencies would need an explicit design.
