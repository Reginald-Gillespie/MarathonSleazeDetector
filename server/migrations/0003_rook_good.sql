-- Nice to Rooks evidence per runner.
ALTER TABLE scores ADD COLUMN w_rook_good REAL NOT NULL DEFAULT 0;
