-- Store gate ticket JWTs so other functions (e.g., eos-secret) can validate them.
-- Safe to run multiple times.

alter table public.online_tickets
  add column if not exists ticket_jwt text;

create unique index if not exists uq_online_tickets_ticket_jwt
  on public.online_tickets (ticket_jwt)
  where ticket_jwt is not null;
