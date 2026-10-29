-- Apply after dashboard-v2. Existing registrations remain valid.
begin;
create table altmate_private.plugin_pairings (
  secret_hash text primary key,
  code text unique not null,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default now() + interval '10 minutes',
  user_id uuid references auth.users(id) on delete cascade,
  name text,
  device_id uuid references public.devices(id) on delete cascade
);
create table altmate_private.plugin_pairing_attempts (
  user_id uuid not null references auth.users(id) on delete cascade,
  attempted_at timestamptz not null default now()
);
create index on altmate_private.plugin_pairing_attempts(user_id, attempted_at);
alter table altmate_private.plugin_pairings enable row level security;
alter table altmate_private.plugin_pairing_attempts enable row level security;
revoke all on altmate_private.plugin_pairings, altmate_private.plugin_pairing_attempts from public, anon, authenticated;

-- The random 256-bit secret stays on the plugin. The 48-bit display code can only approve.
create function public.begin_plugin_pairing(p_secret text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare request altmate_private.plugin_pairings; display_code text;
begin
  if p_secret is null or p_secret !~ '^[a-f0-9]{64}$' then raise exception 'invalid_pairing' using errcode='28000'; end if;
  perform pg_advisory_xact_lock(hashtextextended('plugin_pairing_issue', 0));
  delete from altmate_private.plugin_pairings where expires_at < now() - interval '1 day';
  delete from altmate_private.plugin_pairing_attempts where attempted_at < now() - interval '1 day';
  select * into request from altmate_private.plugin_pairings where secret_hash=altmate_private.hash(p_secret);
  if found then
    if request.expires_at <= now() then raise exception 'invalid_pairing' using errcode='28000'; end if;
    return jsonb_build_object('Code', request.code, 'ExpiresAt', request.expires_at);
  end if;
  if (select count(*) from altmate_private.plugin_pairings where created_at > now()-interval '10 minutes') >= 500 then
    raise exception 'pairing_rate_limited' using errcode='P0001';
  end if;
  loop
    display_code := upper(substr(altmate_private.random_hex(),1,12));
    exit when not exists(select 1 from altmate_private.plugin_pairings p where p.code=display_code);
  end loop;
  insert into altmate_private.plugin_pairings(secret_hash,code) values(altmate_private.hash(p_secret),display_code) returning * into request;
  return jsonb_build_object('Code', request.code, 'ExpiresAt', request.expires_at);
end $$;

create function public.approve_plugin_pairing(p_code text, p_name text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare owner uuid := auth.uid(); request altmate_private.plugin_pairings;
begin
  if owner is null then raise exception 'authentication_required' using errcode='28000'; end if;
  select * into request from altmate_private.plugin_pairings where code=p_code for update;
  perform pg_advisory_xact_lock(hashtextextended(owner::text,0));
  if (select count(*) from altmate_private.plugin_pairing_attempts where user_id=owner and attempted_at > now()-interval '10 minutes') >= 20 then
    return jsonb_build_object('error','rate_limited');
  end if;
  insert into altmate_private.plugin_pairing_attempts(user_id) values(owner);
  if p_name is null or length(btrim(p_name)) not between 1 and 80 or p_name ~ '[[:cntrl:]]' then
    return jsonb_build_object('error','invalid_name');
  end if;
  if p_code is null or p_code !~ '^[A-F0-9]{12}$' then return jsonb_build_object('error','invalid_code'); end if;
  if request.secret_hash is null or request.expires_at <= now() or request.user_id is not null then
    return jsonb_build_object('error','invalid_code');
  end if;
  if (select count(*) from public.devices where user_id=owner and revoked_at is null) +
     (select count(*) from altmate_private.plugin_pairings where user_id=owner and device_id is null and expires_at>now()) >= 10 then
    return jsonb_build_object('error','device_limit');
  end if;
  update altmate_private.plugin_pairings set user_id=owner, name=btrim(p_name) where secret_hash=request.secret_hash;
  return jsonb_build_object('approved',true);
end $$;

create function public.poll_plugin_pairing(p_secret text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare request altmate_private.plugin_pairings; token text; secret text; device uuid;
begin
  if p_secret is null or p_secret !~ '^[a-f0-9]{64}$' then raise exception 'invalid_pairing' using errcode='28000'; end if;
  select * into request from altmate_private.plugin_pairings where secret_hash=altmate_private.hash(p_secret) for update;
  if not found or request.expires_at<=now() then raise exception 'invalid_pairing' using errcode='28000'; end if;
  if request.user_id is null then return jsonb_build_object('Pending',true); end if;
  perform pg_advisory_xact_lock(hashtextextended(request.user_id::text,0));
  token := altmate_private.hash(p_secret || ':plugin-device');
  if request.device_id is null then
    if (select count(*) from public.devices where user_id=request.user_id and revoked_at is null)>=10 then
      raise exception 'device_limit_reached' using errcode='P0001';
    end if;
    insert into altmate_private.sync_keys(user_id,hmac_key) values(request.user_id,altmate_private.random_hex()) on conflict(user_id) do nothing;
    insert into public.devices(user_id,name,token_hash,last_seen_at) values(request.user_id,request.name,altmate_private.hash(token),now()) returning id into device;
    update altmate_private.plugin_pairings set device_id=device where secret_hash=request.secret_hash;
  else
    device:=request.device_id;
    if not exists(select 1 from public.devices where id=device and revoked_at is null) then raise exception 'invalid_pairing' using errcode='28000'; end if;
  end if;
  select hmac_key into secret from altmate_private.sync_keys where user_id=request.user_id;
  return jsonb_build_object('DeviceId',device,'Token',token,'HmacKey',secret);
end $$;
revoke all on function public.begin_plugin_pairing(text), public.poll_plugin_pairing(text), public.approve_plugin_pairing(text,text) from public, anon, authenticated;
grant execute on function public.begin_plugin_pairing(text), public.poll_plugin_pairing(text) to service_role;
grant execute on function public.approve_plugin_pairing(text,text) to authenticated;
commit;
