-- Online enforcement schema for Veilnet
-- Run this in Supabase SQL Editor (manual apply), or via migrations later.

create extension if not exists pgcrypto;

-- 1) Official build hashes by target (use existing game_hashes table)
-- Ensure game_hashes table exists with correct structure
create table if not exists public.game_hashes (
  id uuid primary key default gen_random_uuid(),
  hash text not null,
  target text not null check (target in ('release', 'dev')),
  is_active boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique(target, is_active)
);

-- 2) Per-user online tickets
create table if not exists public.online_tickets (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  device_id text null,
  build_hash text not null,
  channel text not null,
  expires_at timestamptz not null,
  created_at timestamptz not null default now()
);

-- 3) Optional dev testers allowlist
create table if not exists public.dev_testers (
  user_id uuid primary key references auth.users(id) on delete cascade,
  added_at timestamptz not null default now()
);

-- RLS
alter table public.game_hashes enable row level security;
alter table public.online_tickets enable row level security;
alter table public.dev_testers enable row level security;

-- Clean policy names so reruns are safe
-- game_hashes read-only to clients

drop policy if exists "game_hashes_read_anon" on public.game_hashes;
drop policy if exists "game_hashes_read_auth" on public.game_hashes;

create policy "game_hashes_read_anon"
on public.game_hashes
for select
to anon
using (is_active = true);

create policy "game_hashes_read_auth"
on public.game_hashes
for select
to authenticated
using (is_active = true);

-- online_tickets owner-only insert/select + expired self-delete

drop policy if exists "online_tickets_insert_own" on public.online_tickets;
drop policy if exists "online_tickets_select_own" on public.online_tickets;
drop policy if exists "online_tickets_delete_expired_own" on public.online_tickets;

create policy "online_tickets_insert_own"
on public.online_tickets
for insert
to authenticated
with check (auth.uid() = user_id);

create policy "online_tickets_select_own"
on public.online_tickets
for select
to authenticated
using (auth.uid() = user_id);

create policy "online_tickets_delete_expired_own"
on public.online_tickets
for delete
to authenticated
using (auth.uid() = user_id and expires_at < now());

-- dev_testers intentionally locked down (no anon/auth policies)
