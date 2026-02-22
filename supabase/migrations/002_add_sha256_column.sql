-- Add sha256 column to game_hashes table for compatibility
ALTER TABLE public.game_hashes ADD COLUMN IF NOT EXISTS sha256 text;

-- Update existing rows to copy hash to sha256 if sha256 is null
UPDATE public.game_hashes SET sha256 = hash WHERE sha256 IS NULL AND hash IS NOT NULL;
