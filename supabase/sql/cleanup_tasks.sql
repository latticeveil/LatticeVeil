-- SQL script to set up automatic cleanup of expired data using pg_cron.
-- This script should be run in the Supabase SQL Editor.

-- 1. Create a function to perform the cleanup
CREATE OR REPLACE FUNCTION public.cleanup_expired_data()
RETURNS void AS $$
BEGIN
  -- Delete expired launcher link codes
  DELETE FROM public.launcher_link_codes
  WHERE expires_at < now();

  -- Delete expired online tickets
  DELETE FROM public.online_tickets
  WHERE expires_at < now();

  -- Optional: Log the cleanup (requires a logs table)
  -- INSERT INTO public.cleanup_logs (task, deleted_at) VALUES ('expired_data', now());
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- 2. Schedule the cleanup task using pg_cron (runs every hour)
-- Note: 'cron' extension must be enabled in Supabase (Database -> Extensions)
SELECT cron.schedule(
  'cleanup-expired-data-job', -- name of the cron job
  '0 * * * *',                -- cron schedule (every hour at minute 0)
  'SELECT public.cleanup_expired_data()'
);

-- 3. (Optional) Manual first run
-- SELECT public.cleanup_expired_data();
