-- Supabase SQL: online session advertise + discovery
-- Run in Supabase SQL Editor.

create extension if not exists pgcrypto;

create table if not exists public.online_sessions (
  id uuid primary key default gen_random_uuid(),
  host_user_id uuid not null references auth.users(id) on delete cascade,
  host_product_user_id text null,
  host_username text not null,
  world_name text not null,
  mode text null,
  cheats boolean not null default false,
  player_count integer not null default 1,
  max_players integer not null default 8,
  join_target text null,
  status text null,
  is_hosting boolean not null default true,
  is_in_world boolean not null default false,
  updated_at timestamptz not null default now(),
  expires_at timestamptz not null
);

alter table public.online_sessions
  add column if not exists host_product_user_id text,
  add column if not exists host_username text,
  add column if not exists world_name text,
  add column if not exists mode text,
  add column if not exists cheats boolean,
  add column if not exists player_count integer,
  add column if not exists max_players integer,
  add column if not exists join_target text,
  add column if not exists status text,
  add column if not exists is_hosting boolean,
  add column if not exists is_in_world boolean,
  add column if not exists updated_at timestamptz,
  add column if not exists expires_at timestamptz;

alter table public.online_sessions
  alter column host_username set default 'Player',
  alter column world_name set default 'WORLD',
  alter column cheats set default false,
  alter column player_count set default 1,
  alter column max_players set default 8,
  alter column is_hosting set default true,
  alter column is_in_world set default false,
  alter column updated_at set default now();

update public.online_sessions
set host_username = coalesce(nullif(trim(host_username), ''), 'Player'),
    world_name = coalesce(nullif(trim(world_name), ''), 'WORLD'),
    cheats = coalesce(cheats, false),
    player_count = coalesce(player_count, 1),
    max_players = coalesce(max_players, 8),
    is_hosting = coalesce(is_hosting, true),
    is_in_world = coalesce(is_in_world, false),
    updated_at = coalesce(updated_at, now()),
    expires_at = coalesce(expires_at, now())
where host_username is null
   or world_name is null
   or cheats is null
   or player_count is null
   or max_players is null
   or is_hosting is null
   or is_in_world is null
   or updated_at is null
   or expires_at is null;

alter table public.online_sessions
  alter column host_username set not null,
  alter column world_name set not null,
  alter column cheats set not null,
  alter column player_count set not null,
  alter column max_players set not null,
  alter column is_hosting set not null,
  alter column is_in_world set not null,
  alter column updated_at set not null,
  alter column expires_at set not null;

create unique index if not exists ux_online_sessions_host_user
  on public.online_sessions(host_user_id);

create index if not exists idx_online_sessions_updated_at
  on public.online_sessions(updated_at desc);

create index if not exists idx_online_sessions_expires_at
  on public.online_sessions(expires_at);

create index if not exists idx_online_sessions_host_product_user_id
  on public.online_sessions(host_product_user_id);

alter table public.online_sessions enable row level security;

drop policy if exists "online_sessions_read_active" on public.online_sessions;
create policy "online_sessions_read_active"
  on public.online_sessions
  for select
  to anon, authenticated
  using (
    expires_at > now()
    and is_hosting = true
    and is_in_world = true
  );

drop policy if exists "online_sessions_insert_own" on public.online_sessions;
create policy "online_sessions_insert_own"
  on public.online_sessions
  for insert
  to authenticated
  with check (auth.uid() = host_user_id);

drop policy if exists "online_sessions_update_own" on public.online_sessions;
create policy "online_sessions_update_own"
  on public.online_sessions
  for update
  to authenticated
  using (auth.uid() = host_user_id)
  with check (auth.uid() = host_user_id);

drop policy if exists "online_sessions_delete_own" on public.online_sessions;
create policy "online_sessions_delete_own"
  on public.online_sessions
  for delete
  to authenticated
  using (auth.uid() = host_user_id);
