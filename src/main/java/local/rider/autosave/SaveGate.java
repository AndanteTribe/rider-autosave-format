package local.rider.autosave;

/**
 * EDT-confined state for one document's formatting worker.
 *
 * <p>A save received during a worker is remembered, rather than treated as an
 * already formatted revision. A successful backend response alone does not
 * establish stability: the caller must compare document stamps and drain
 * pending save requests before recording a completed stamp.
 */
public final class SaveGate {
    private boolean running;
    private Long completedStamp;
    private Long pendingStamp;

    /** Acquires a worker, or remembers the newest save for the existing worker. */
    public boolean begin(long savedStamp) {
        if (running) {
            pendingStamp = savedStamp;
            return false;
        }
        if (completedStamp != null && completedStamp.longValue() == savedStamp) {
            return false;
        }
        running = true;
        pendingStamp = null;
        return true;
    }

    /**
     * Starts a pass over the current document. Call only after eligibility and
     * saved-state checks, immediately before capturing its starting stamp.
     * Requests before this point are covered by the new pass.
     */
    public void beginPass() {
        requireRunning();
        pendingStamp = null;
    }

    /** True if this pass cannot prove that it covered the latest saved state. */
    public boolean needsAnotherPass(long startStamp, long endStamp) {
        requireRunning();
        return pendingStamp != null || startStamp != endStamp;
    }

    /**
     * Releases the worker and returns any unprocessed save request to its caller.
     * The caller must schedule that request after a failed/aborted operation if
     * the document remains eligible. Returning the request atomically avoids a
     * separate take-and-finish operation accidentally erasing it.
     *
     * <p>Only a proven stable, saved result with no pending request is cached.
     * Failure or an inconclusive result invalidates any earlier completed stamp.
     */
    public Long finish(long stamp, boolean stableAndSaved) {
        requireRunning();
        Long pending = pendingStamp;
        completedStamp = stableAndSaved && pending == null ? Long.valueOf(stamp) : null;
        pendingStamp = null;
        running = false;
        return pending;
    }

    private void requireRunning() {
        if (!running) {
            throw new IllegalStateException("No formatting worker owns this gate");
        }
    }
}
