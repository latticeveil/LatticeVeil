-- Clean up old tables and set up new hash verification system
-- Run this in Supabase SQL Editor

-- Drop old tables
DROP TABLE IF EXISTS public.game_builds CASCADE;
DROP TABLE IF EXISTS public.dev_testers CASCADE;

-- Create game_hashes table (if not exists)
CREATE TABLE IF NOT EXISTS public.game_hashes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  target text NOT NULL CHECK (target IN ('dev', 'release')),
  hash text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (target, is_active)
);

-- Create online_tickets table
CREATE TABLE IF NOT EXISTS public.online_tickets (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL,
  target text NOT NULL,
  build_hash text NOT NULL,
  expires_at timestamptz NOT NULL,
  created_at timestamptz DEFAULT now()
);

-- Disable RLS entirely - we handle security in the application code
ALTER TABLE public.online_tickets DISABLE ROW LEVEL SECURITY;

-- Function to ensure only one active hash per target
CREATE OR REPLACE FUNCTION public.ensure_single_active_hash()
RETURNS TRIGGER AS $$
BEGIN
  -- Deactivate all other hashes for this target when activating one
  IF NEW.is_active = true THEN
    UPDATE public.game_hashes 
    SET is_active = false 
    WHERE target = NEW.target AND id != NEW.id;
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger to enforce single active hash
DROP TRIGGER IF EXISTS single_active_hash_trigger ON public.game_hashes;
CREATE TRIGGER single_active_hash_trigger
  BEFORE INSERT OR UPDATE ON public.game_hashes
  FOR EACH ROW EXECUTE FUNCTION public.ensure_single_active_hash();

-- RLS
ALTER TABLE public.game_hashes ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.online_tickets ENABLE ROW LEVEL SECURITY;

-- Drop existing policies
DROP POLICY IF EXISTS "game_hashes_read_anon" ON public.game_hashes;
DROP POLICY IF EXISTS "game_hashes_read_auth" ON public.game_hashes;
DROP POLICY IF EXISTS "online_tickets_insert_own" ON public.online_tickets;
DROP POLICY IF EXISTS "online_tickets_select_own" ON public.online_tickets;

-- Create policies for game_hashes (allow reads)
CREATE POLICY "game_hashes_read_anon"
ON public.game_hashes
FOR SELECT
TO anon
USING (is_active = true);

CREATE POLICY "game_hashes_read_auth"
ON public.game_hashes
FOR SELECT
TO authenticated
USING (is_active = true);
