-- AltMate Web Dashboard v1: private, read-only client access.
create extension if not exists pgcrypto;

create table public.devices (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  name text not null,
  token_hash text not null unique,
  revoked_at timestamptz,
  last_seen_at timestamptz,
  created_at timestamptz not null default now()
);

create table public.entities (
  user_id uuid not null references auth.users(id) on delete cascade,
  entity_type text not null,
  entity_key text not null,
  payload jsonb not null,
  observed_at timestamptz not null,
  updated_at timestamptz not null default now(),
  primary key (user_id, entity_type, entity_key)
);

create table public.sync_events (
  event_id uuid primary key,
  user_id uuid not null references auth.users(id) on delete cascade,
  device_id uuid not null references public.devices(id),
  revision bigint not null,
  received_at timestamptz not null default now()
);

create table public.entity_history (
  id bigint generated always as identity primary key,
  user_id uuid not null references auth.users(id) on delete cascade,
  entity_type text not null,
  entity_key text not null,
  payload jsonb not null,
  observed_at timestamptz not null
);

create index on public.entity_history (user_id, entity_type, entity_key, observed_at desc);
create index on public.entity_history (observed_at);
create index on public.sync_events (received_at);

alter table public.devices enable row level security;
alter table public.entities enable row level security;
alter table public.sync_events enable row level security;
alter table public.entity_history enable row level security;

create policy "read own devices" on public.devices
for select to authenticated using (user_id = (select auth.uid()));
create policy "read own entities" on public.entities
for select to authenticated using (user_id = (select auth.uid()));
create policy "read own history" on public.entity_history
for select to authenticated using (user_id = (select auth.uid()));

-- Writes must go through a separately authenticated server-side API.
-- Never expose a service_role key in the client or Windows agent.
