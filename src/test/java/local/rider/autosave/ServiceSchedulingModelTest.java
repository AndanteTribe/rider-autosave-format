package local.rider.autosave;

import java.util.ArrayDeque;

/**
 * MODEL ONLY: mirrors the listener/service scheduling decisions using the real
 * SaveGate. Does not instantiate or execute NativeFormattingService, Rider,
 * IntelliJ documents, coroutines, the backend, or a real filesystem.
 */
public final class ServiceSchedulingModelTest {
    private static int passed;

    public static void main(String[] args) {
        run("ordinary formatting needs a second unchanged pass", () -> {
            Model model = started(1);
            model.beginPass();
            model.cleanupSucceeded(2, false);
            yes(model.active);
            model.beginPass();
            model.cleanupSucceeded(2, false);
            no(model.active);
            equal(2, model.totalPasses);
            model.onSaved(2);
            no(model.active);
        });
        run("own resaves cannot bypass the stabilization bound", () -> {
            Model model = started(1);
            for (int stamp = 2; stamp <= 4; stamp++) {
                model.beginPass();
                model.cleanupSucceeded(stamp, false);
                equal(0, model.queue.size());
            }
            no(model.active);
            yes(model.boundReached);
            model.pump();
            equal(1, model.workers);
            equal(3, model.totalPasses);
        });
        run("new save during failed pass starts one replacement worker", () -> {
            Model model = started(1);
            model.beginPass();
            model.userSave(2);
            model.pump();
            equal(1, model.workers);
            model.abort();
            equal(1, model.queue.size());
            model.pump();
            equal(2, model.workers);
            model.beginPass();
            model.cleanupSucceeded(2, false);
            no(model.active);
            equal(0, model.queue.size());
        });
        run("new save during profile lookup is covered by the next pass", () -> {
            Model model = started(1);
            model.userSave(2);
            model.pump();
            model.beginPass();
            model.cleanupSucceeded(2, false);
            no(model.active);
            equal(1, model.totalPasses);
        });
        run("pending event survives failure before the first pass", () -> {
            Model model = started(1);
            model.userSave(2);
            model.pump();
            model.abort();
            equal(1, model.queue.size());
            model.pump();
            equal(2, model.workers);
            model.beginPass();
            model.cleanupSucceeded(2, false);
            no(model.active);
        });
        run("unsaved typing blocks queued cleanup and remains retryable", () -> {
            Model model = started(1);
            model.stamp = 2;
            model.unsaved = true;
            model.beginPass();
            no(model.active);
            equal(0, model.totalPasses);
            model.userSave(2);
            model.pump();
            model.beginPass();
            model.cleanupSucceeded(2, false);
            equal(1, model.totalPasses);
        });
        run("eligibility change before a pass prevents backend work", () -> {
            Model model = started(1);
            model.eligible = false;
            model.beginPass();
            no(model.active);
            equal(0, model.totalPasses);
        });
        run("vetoed internal save does not cache completion", () -> {
            Model model = started(1);
            model.beginPass();
            model.cleanupSucceeded(2, true);
            no(model.active);
            yes(model.unsaved);
            model.userSave(2);
            model.pump();
            yes(model.active);
            equal(2, model.workers);
        });
        run("bound exhaustion does not turn pending requests into another loop", () -> {
            Model model = started(1);
            model.beginPass();
            model.cleanupSucceeded(2, false);
            model.beginPass();
            model.cleanupSucceeded(3, false);
            model.beginPass();
            model.userSave(4);
            model.pump();
            model.cleanupSucceeded(5, false);
            no(model.active);
            yes(model.boundReached);
            equal(0, model.queue.size());
            // A subsequent independent user save may start a new bounded worker.
            model.userSave(6);
            model.pump();
            yes(model.active);
            equal(2, model.workers);
        });
        System.out.println("RESULT: " + passed + " scheduling-model tests passed");
        System.out.println("MODEL ONLY: real SaveGate plus simulated service/listener; no Rider integration executed.");
    }

    private static Model started(long stamp) {
        Model model = new Model();
        model.stamp = stamp;
        model.onSaved(stamp);
        yes(model.active);
        return model;
    }

    private static final class Model {
        final SaveGate gate = new SaveGate();
        final ArrayDeque<Runnable> queue = new ArrayDeque<>();
        long stamp;
        long startStamp;
        boolean unsaved;
        boolean eligible = true;
        boolean internalSave;
        boolean active;
        boolean boundReached;
        int passes;
        int totalPasses;
        int workers;

        void userSave(long savedStamp) {
            stamp = savedStamp;
            unsaved = false;
            beforeSaving();
        }

        void beforeSaving() {
            if (internalSave) return;
            long capturedStamp = stamp;
            queue.add(() -> onSaved(capturedStamp));
        }

        void onSaved(long capturedStamp) {
            if (!eligible || unsaved || !gate.begin(stamp)) return;
            active = true;
            passes = 0;
            workers++;
        }

        void beginPass() {
            yes(active);
            if (!eligible || unsaved) {
                abort();
                return;
            }
            gate.beginPass();
            startStamp = stamp;
            passes++;
            totalPasses++;
        }

        void cleanupSucceeded(long resultingStamp, boolean vetoSave) {
            yes(active);
            unsaved |= stamp != resultingStamp;
            stamp = resultingStamp;
            if (!eligible) {
                abort();
                return;
            }
            boolean previous = internalSave;
            internalSave = true;
            try {
                if (unsaved) beforeSaving();
                if (!vetoSave) unsaved = false;
            } finally {
                internalSave = previous;
            }
            if (unsaved) {
                abort();
            } else if (!gate.needsAnotherPass(startStamp, stamp)) {
                finish(true, true);
            } else if (passes == 3) {
                boundReached = true;
                finish(false, false);
            }
        }

        void abort() {
            finish(false, true);
        }

        void finish(boolean stable, boolean retryPending) {
            Long pending = gate.finish(stamp, stable);
            active = false;
            if (retryPending && pending != null) queue.add(() -> onSaved(pending));
        }

        void pump() {
            int remaining = 1000;
            while (!queue.isEmpty()) {
                if (--remaining == 0) throw new AssertionError("Event queue did not drain");
                queue.remove().run();
            }
        }
    }

    private static void run(String name, Runnable test) {
        test.run();
        passed++;
        System.out.println("PASS (model): " + name);
    }

    private static void yes(boolean value) {
        if (!value) throw new AssertionError("Expected true");
    }

    private static void no(boolean value) {
        if (value) throw new AssertionError("Expected false");
    }

    private static void equal(int expected, int actual) {
        if (expected != actual) throw new AssertionError("Expected " + expected + ", got " + actual);
    }
}
