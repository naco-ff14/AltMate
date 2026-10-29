-- Apply after 202610280001. No real credentials belong in migrations.
begin;
create schema if not exists extensions;
create extension if not exists pgcrypto with schema extensions;
-- pgcrypto may already be in public from the first migration. Resolve explicitly below.
create schema if not exists altmate_private;
revoke create on schema public from public, anon, authenticated;
revoke all on schema altmate_private from public, anon, authenticated;

create table altmate_private.sync_pairings (
  code_hash text primary key,
  user_id uuid not null references auth.users(id) on delete cascade,
  name text not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default (now() + interval '10 minutes'),
  consumed_at timestamptz
);
create index on altmate_private.sync_pairings(user_id, created_at);
create table altmate_private.sync_keys (
  user_id uuid primary key references auth.users(id) on delete cascade,
  hmac_key text not null
);
alter table altmate_private.sync_pairings enable row level security;
alter table altmate_private.sync_keys enable row level security;
alter table public.devices add column last_upload_at timestamptz;
alter table public.sync_events add column payload_hash text;

-- Stable crypto wrappers support either Supabase's extensions schema or the original public installation.
create function altmate_private.random_hex() returns text language plpgsql
set search_path = pg_catalog, extensions, public as $$
begin return encode(gen_random_bytes(32), 'hex'); end $$;
create function altmate_private.hash(value text) returns text language plpgsql
set search_path = pg_catalog, extensions, public as $$
begin return encode(digest(convert_to(value, 'UTF8'), 'sha256'), 'hex'); end $$;
revoke all on function altmate_private.random_hex(), altmate_private.hash(text) from public;

create function public.create_sync_pairing(p_name text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare owner uuid := auth.uid(); code text;
begin
  if owner is null then raise exception 'authentication_required' using errcode = '28000'; end if;
  if p_name is null or length(btrim(p_name)) not between 1 and 80 or p_name ~ '[[:cntrl:]]' then
    raise exception 'invalid_device_name' using errcode = '22023';
  end if;
  perform pg_advisory_xact_lock(hashtextextended(owner::text, 0));
  if (select count(*) from altmate_private.sync_pairings where user_id = owner and created_at > now() - interval '10 minutes') >= 3 then
    raise exception 'pairing_rate_limited' using errcode = 'P0001';
  end if;
  if (select count(*) from public.devices where user_id = owner and revoked_at is null) >= 10 then
    raise exception 'device_limit_reached' using errcode = 'P0001';
  end if;
  code := altmate_private.random_hex();
  insert into altmate_private.sync_pairings(code_hash, user_id, name)
    values (altmate_private.hash(code), owner, btrim(p_name));
  return jsonb_build_object('code', code, 'expires_at', now() + interval '10 minutes');
end $$;

create function public.revoke_sync_device(p_device_id uuid) returns void
language plpgsql security definer set search_path = '' as $$
begin
  if auth.uid() is null then raise exception 'authentication_required' using errcode = '28000'; end if;
  update public.devices set revoked_at = coalesce(revoked_at, now())
    where id = p_device_id and user_id = auth.uid();
end $$;

-- Only the server may call these functions. Ownership is derived from the hashed code/token.
create function public.claim_sync_pairing(p_code text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare pairing altmate_private.sync_pairings; secret text; device uuid; token text;
begin
  if p_code is null or p_code !~ '^[a-f0-9]{64}$' then
    raise exception 'invalid_pairing' using errcode = '28000';
  end if;
  select * into pairing from altmate_private.sync_pairings
    where code_hash = altmate_private.hash(p_code) and consumed_at is null and expires_at > now() for update;
  if not found then raise exception 'invalid_pairing' using errcode = '28000'; end if;
  perform pg_advisory_xact_lock(hashtextextended(pairing.user_id::text, 0));
  if (select count(*) from public.devices where user_id = pairing.user_id and revoked_at is null) >= 10 then
    raise exception 'device_limit_reached' using errcode = 'P0001';
  end if;
  insert into altmate_private.sync_keys(user_id, hmac_key) values (pairing.user_id, altmate_private.random_hex())
    on conflict (user_id) do nothing;
  select hmac_key into secret from altmate_private.sync_keys where user_id = pairing.user_id;
  token := altmate_private.random_hex();
  insert into public.devices(user_id, name, token_hash, last_seen_at)
    values (pairing.user_id, pairing.name, altmate_private.hash(token), now()) returning id into device;
  update altmate_private.sync_pairings set consumed_at = now() where code_hash = pairing.code_hash;
  return jsonb_build_object('DeviceId', device, 'Token', token, 'HmacKey', secret);
end $$;

create function public.ingest_sync(p_token text, p_envelope jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare device public.devices; existing public.sync_events; event uuid; fingerprint text;
  change jsonb; stamp timestamptz; applied integer := 0; affected integer;
begin
  if p_token is null or p_token !~ '^[a-f0-9]{64}$' then
    raise exception 'invalid_device' using errcode = '28000';
  end if;
  select * into device from public.devices where token_hash = altmate_private.hash(p_token) and revoked_at is null for update;
  if not found then raise exception 'invalid_device' using errcode = '28000'; end if;
  if jsonb_typeof(p_envelope) is distinct from 'object' or octet_length(p_envelope::text) > 1048576 or
    p_envelope->>'SchemaVersion' is distinct from '1' or
    p_envelope->>'DeviceId' is distinct from device.id::text or
    jsonb_typeof(p_envelope->'Changes') is distinct from 'array' or
    (p_envelope->>'Revision') is null or (p_envelope->>'Revision') !~ '^[0-9]{1,16}$' then
    raise exception 'invalid_envelope' using errcode = '22023';
  end if;
  if jsonb_array_length(p_envelope->'Changes') not between 1 and 1000 then
    raise exception 'invalid_envelope' using errcode = '22023';
  end if;
  event := (p_envelope->>'EventId')::uuid;
  if event is null then raise exception 'invalid_envelope' using errcode = '22023'; end if;
  fingerprint := altmate_private.hash(p_envelope::text);
  select * into existing from public.sync_events where event_id = event;
  if found then
    if existing.user_id <> device.user_id or existing.device_id <> device.id or existing.payload_hash is distinct from fingerprint then
      raise exception 'event_conflict' using errcode = '22023';
    end if;
    return jsonb_build_object('duplicate', true, 'applied', 0);
  end if;
  if device.last_upload_at is not null and device.last_upload_at > now() - interval '5 minutes' then
    raise exception 'sync_rate_limited' using errcode = 'P0001';
  end if;
  -- Reserve the event before changing entities. A concurrent UUID collision rolls back everything.
  insert into public.sync_events(event_id, user_id, device_id, revision, payload_hash)
    values (event, device.user_id, device.id, (p_envelope->>'Revision')::bigint, fingerprint);
  for change in select value from jsonb_array_elements(p_envelope->'Changes') loop
    if change->>'EntityType' is null or change->>'EntityType' not in
      ('character_gil','custom_delivery','housing_demolition','housing_lottery','free_company','free_company_gil','free_company_credits','submarine') or
      change->>'EntityKey' is null or change->>'EntityKey' !~ '^[a-f0-9]{64}$' or
      jsonb_typeof(change->'Data') is distinct from 'object' then
      raise exception 'invalid_change' using errcode = '22023';
    end if;
    stamp := (change->>'ObservedAt')::timestamptz;
    if stamp is null or stamp < '2000-01-01'::timestamptz or stamp > now() + interval '5 minutes' then
      raise exception 'invalid_observation' using errcode = '22023';
    end if;
    insert into public.entities(user_id, entity_type, entity_key, payload, observed_at)
      values (device.user_id, change->>'EntityType', change->>'EntityKey', change->'Data', stamp)
      on conflict (user_id, entity_type, entity_key) do update
      set payload = excluded.payload, observed_at = excluded.observed_at, updated_at = now()
      where excluded.observed_at > public.entities.observed_at;
    get diagnostics affected = row_count;
    if affected > 0 then
      applied := applied + 1;
      insert into public.entity_history(user_id, entity_type, entity_key, payload, observed_at)
        values (device.user_id, change->>'EntityType', change->>'EntityKey', change->'Data', stamp);
    end if;
  end loop;
  update public.devices set last_upload_at = now(), last_seen_at = now() where id = device.id;
  return jsonb_build_object('duplicate', false, 'applied', applied);
end $$;

create function public.prune_sync_history() returns void
language sql security definer set search_path = '' as $$
  delete from public.entity_history where observed_at < now() - interval '90 days';
  delete from public.sync_events where received_at < now() - interval '90 days';
  delete from altmate_private.sync_pairings where expires_at < now() - interval '1 day';
$$;

revoke all on function public.create_sync_pairing(text), public.revoke_sync_device(uuid),
  public.claim_sync_pairing(text), public.ingest_sync(text,jsonb), public.prune_sync_history()
  from public, anon, authenticated;
grant execute on function public.create_sync_pairing(text), public.revoke_sync_device(uuid) to authenticated;
grant execute on function public.claim_sync_pairing(text), public.ingest_sync(text,jsonb), public.prune_sync_history() to service_role;

-- Explicit read privileges: RLS does not itself grant SELECT (fixed on the first live setup).
grant usage on schema public to authenticated;
revoke all on public.devices, public.entities, public.entity_history, public.sync_events from anon, authenticated;
grant select(id, user_id, name, last_seen_at, revoked_at, created_at) on public.devices to authenticated;
grant select on public.entities, public.entity_history to authenticated;
-- A standard Supabase service_role has default privileges; make backend requirements explicit.
grant all on public.devices, public.entities, public.entity_history, public.sync_events to service_role;
grant usage, select on sequence public.entity_history_id_seq to service_role;
commit;
