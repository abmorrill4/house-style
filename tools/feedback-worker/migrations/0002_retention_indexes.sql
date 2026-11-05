-- Indexes for the retention pruning in the weekly cron.
CREATE INDEX reports_created ON reports (created_at);
CREATE INDEX false_positives_created ON false_positives (created_at);
CREATE INDEX error_signatures_last_seen ON error_signatures (last_seen);
