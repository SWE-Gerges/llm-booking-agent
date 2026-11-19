# ADR 0003: The model never owns money or identity

## Context

A language model can be steered by anything that reaches its context: user text, supplier-controlled strings in tool
output, or earlier turns. If a model-supplied argument could set a price, a passenger or a payment detail, one
successful prompt injection would be a financial or privacy incident. Personal and payment data typed into a chat
would also end up in prompts, logs and provider retention.

## Decision

- Submit tools (`submit_flight_booking`, `submit_hotel_booking`) take no arguments; their schema is an empty object
  with `additionalProperties: false`. They read the selected item, the confirmed price and the traveller details from
  the server-side `BookingContext` only.
- Prices enter the context only from the supplier: `recheck_flight_price` and `prebook_room` store the quote the
  backend returned. Follow-up tools resolve offers from the stored search snapshot by id, never from model-supplied
  data. A new search clears the selection and the confirmed price.
- Traveller and guest details are collected by `collect_*_details` tools that return a form schema to the API
  consumer and a short acknowledgement to the model. The form is submitted to non-LLM endpoints, validated, stored raw
  in the booking context and as a PII-masked copy for anything that may be logged or shown to the model.
- A policy refuses submit tools unless the price was confirmed in this session and is still fresh.
- Every submit mints an idempotency key that is persisted before the backend call, so replays return the same
  booking.
- A guard replaces card, IBAN and account numbers typed into chat with a redacted placeholder before anything is
  stored, and the model is never called for that message.

## Consequences

- A successful injection can at worst make the assistant say something wrong or call a permitted tool; it cannot
  change what is booked or for whom. The test suite includes an injection corpus and a model that tries to pass price
  arguments to submit tools.
- The user fills a form instead of chatting their details; the UI needs a form renderer. The API returns the schema
  with every field typed and flags conditional requirements (`requiredBy`).
- Raw personal data lives in the booking-context row. Encryption at rest is not implemented and is listed in the
  roadmap.
