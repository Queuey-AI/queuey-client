-- The webhook the dashboard made, kept in a migration.
create trigger "orders-hook"
  after insert or update on "public"."orders"
  for each row
  execute function "supabase_functions"."http_request"(
    'https://abcdefghijklmnop.supabase.co/functions/v1/orders-hook',
    'POST',
    '{"Content-Type":"application/json","x-webhook-secret":"fixture-secret-in-a-migration-never-shown"}',
    '{}',
    '5000'
  );
