-- Connection probe only. No character, account, or device data is exposed.
begin;
create or replace function public.altmate_healthcheck()
returns boolean
language sql stable security invoker
set search_path = ''
as $$
  select exists(select 1 from pg_catalog.pg_namespace where nspname = 'public');
$$;
revoke all on function public.altmate_healthcheck() from public;
grant usage on schema public to anon;
grant execute on function public.altmate_healthcheck() to anon, authenticated;
notify pgrst, 'reload schema';
commit;
