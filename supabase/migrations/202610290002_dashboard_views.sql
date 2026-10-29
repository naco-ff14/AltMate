-- Dashboard v2 entity allowlist and additive metadata enrichment. Existing data/ownership are preserved.
begin;
create or replace function public.ingest_sync(p_token text, p_envelope jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare device public.devices; existing public.sync_events; event uuid; fingerprint text;
  change jsonb; stamp timestamptz; stored jsonb; applied integer := 0; affected integer;
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
      ('character_gil','custom_delivery','housing_demolition','housing_lottery','free_company','free_company_gil','free_company_credits','submarine','retainer_gil','character_profile','open_plot','submarine_fleet','sync_metadata') or
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
      set payload = case when excluded.observed_at = public.entities.observed_at then excluded.payload || jsonb_strip_nulls(public.entities.payload) else excluded.payload end, observed_at = excluded.observed_at, updated_at = now()
      where excluded.observed_at > public.entities.observed_at or (excluded.observed_at = public.entities.observed_at and (excluded.payload || jsonb_strip_nulls(public.entities.payload)) is distinct from public.entities.payload) returning public.entities.payload into stored;
    get diagnostics affected = row_count;
    if affected > 0 then
      applied := applied + 1;
      if change->>'EntityType' not in ('sync_metadata','character_profile','submarine_fleet') then
      insert into public.entity_history(user_id, entity_type, entity_key, payload, observed_at)
        values (device.user_id, change->>'EntityType', change->>'EntityKey', stored, stamp);
      end if;
    end if;
  end loop;
  update public.devices set last_upload_at = now(), last_seen_at = now() where id = device.id;
  return jsonb_build_object('duplicate', false, 'applied', applied);
end $$;

commit;
