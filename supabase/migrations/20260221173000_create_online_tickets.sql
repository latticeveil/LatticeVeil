-- Creates the ticket table used by online-ticket / online-ticket-validate
-- Safe to run multiple times.
create extension if not exists "pgcrypto";

create table if not exists public.online_tickets (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null,
  target text not null,
  build_hash text not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now()
);

create index if not exists idx_online_tickets_user_target on public.online_tickets (user_id, target);
create index if not exists idx_online_tickets_expires_at on public.online_tickets (expires_at);
