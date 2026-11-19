# ADR 0009: Encryption at rest for stored booking details

## Context

Passenger and contact details submitted through the forms (names, dates of birth, passport data, e-mail, phone) are kept
in the booking context so that a submit tool can use them without the model ever seeing them. They are the most
sensitive data the service stores. A database dump, a backup or a read-only SQL account should not be enough to read
them.

## Decision

- The whole `booking_contexts.DataJson` column is stored encrypted. Encryption sits behind a port,
  `IFormDataProtector` (`Protect` / `Unprotect`), implemented with ASP.NET Core Data Protection (authenticated
  encryption, per-message randomisation). An EF Core value converter applies it, so no store or tool handles ciphertext
  and no caller can forget to encrypt.
- The implementation uses one versioned purpose string (`Agent.Persistence.BookingContext.v1`). A future format change
  gets a new purpose and a migration step instead of an in-place reinterpretation.
- **Key ring.** `DataProtection:KeyRingPath` is the folder of the Data Protection key ring. It is required in
  Production (startup fails without it) and optional in Development. Keys roll
  over automatically and old keys stay in the ring, so rows written under an earlier key stay readable.
- **Fail closed.** A value that was tampered with, written under a different key ring, or is not protected data raises
  `FormDataProtectionException`. Nothing is returned partially and nothing falls back to plaintext. The message names
  neither the cause nor the content. Through the API it surfaces as a generic `500` problem response.
- Rows written before encryption existed are refused, not read: this repository has no released data to migrate, and
  a silent plaintext fallback would hide exactly the failure the control exists to catch.

## Consequences

- Losing the key ring makes stored booking contexts unreadable. The ring belongs on a persistent, backed-up volume
  that is kept apart from the database backups.
- The key ring itself is written unencrypted by default (Data Protection logs a warning at startup saying so). In a real
  deployment protect it with a certificate or a cloud key service (`ProtectKeysWith*`) or keep it on a volume with its
  own access control. That is a deployment decision this repository only documents.
- Only the booking-context column is encrypted. Chat messages are stored as text, so anything a user types into the chat
  is stored as typed (the sensitive-data guard stops card and bank numbers, not names). Use storage-level encryption for
  the database as well; retention (`Retention:*`) bounds how long any of it is kept.
- Encrypted columns cannot be searched or indexed. Nothing queries inside the booking context, so this costs nothing
  today.
- Existing rows are not re-encrypted when keys roll over; they remain readable because the old key stays in the ring.
