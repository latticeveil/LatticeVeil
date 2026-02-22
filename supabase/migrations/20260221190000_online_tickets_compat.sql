-- Online ticket table (compat + safe)
create extension if not exists "pgcrypto";

create table if not exists public.online_tickets (
  id uuid primary key default gen_random_uuid()
);

alter table public.online_tickets
  add column if not exists user_id uuid,
  add column if not exists target text,
  add column if not exists build_hash text,
  add column if not exists build_sha256 text,
  add column if not exists expires_at timestamptz,
  add column if not exists created_at timestamptz not null default now();

create index if not exists idx_online_tickets_user_target on public.online_tickets (user_id, target);
create index if not exists idx_online_tickets_expires_at on public.online_tickets (expires_at);
