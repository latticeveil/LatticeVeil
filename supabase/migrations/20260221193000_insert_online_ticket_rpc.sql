-- RPC function to insert online tickets (bypasses PostgREST cache issues)
create or replace function public.insert_online_ticket(
  p_user_id uuid,
  p_target text,
  p_build_hash text,
  p_expires_at timestamptz,
  p_ticket_jwt text
)
returns void as $$
begin
  insert into public.online_tickets (
    user_id,
    target,
    build_hash,
    expires_at,
    ticket_jwt
  ) values (
    p_user_id,
    p_target,
    p_build_hash,
    p_expires_at,
    p_ticket_jwt
  );
end;
$$ language plpgsql security definer;
