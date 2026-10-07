package local.rider.autosave;

import java.util.Objects;

/** Tests the real production gate. No Rider SDK, Kotlin runtime, or JUnit needed. */
public final class SaveGateStandaloneTest {
    private static int passed;
    private static int failed;

    public static void main(String[] args) {
        run("unchanged successful document is skipped", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(1));
            gate.beginPass();
            yes(gate.needsAnotherPass(1, 2));
            gate.beginPass();
            no(gate.needsAnotherPass(2, 2));
            equal(null, gate.finish(2, true));
            no(gate.begin(2));
            yes(gate.begin(3));
        });
        run("reentrant saves are coalesced and stable formatting converges", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(1));
            gate.beginPass();
            no(gate.begin(1));
            no(gate.begin(2));
            yes(gate.needsAnotherPass(1, 2));
            gate.beginPass();
            no(gate.needsAnotherPass(2, 2));
            equal(null, gate.finish(2, true));
            no(gate.begin(2));
        });
        run("failed formatting can be retried", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(1));
            gate.beginPass();
            equal(null, gate.finish(1, false));
            yes(gate.begin(1));
        });
        run("latest saved generation survives a failed older operation", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(10));
            gate.beginPass();
            no(gate.begin(11));
            no(gate.begin(12));
            Long retry = gate.finish(12, false);
            equal(12L, retry);
            yes(gate.begin(retry.longValue()));
            gate.beginPass();
            no(gate.needsAnotherPass(12, 12));
            equal(null, gate.finish(12, true));
            no(gate.begin(12));
        });
        run("late save cannot be marked completed by older success", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(20));
            gate.beginPass();
            no(gate.begin(21));
            yes(gate.needsAnotherPass(20, 21));
            // Even an incorrectly optimistic caller cannot swallow a pending save.
            equal(21L, gate.finish(21, true));
            yes(gate.begin(21));
        });
        run("a same-stamp save still records a pending event", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(30));
            gate.beginPass();
            no(gate.begin(30));
            yes(gate.needsAnotherPass(30, 30));
            equal(30L, gate.finish(30, false));
            yes(gate.begin(30));
        });
        run("starting a fresh pass consumes only earlier pending events", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(40));
            no(gate.begin(41));
            gate.beginPass();
            no(gate.needsAnotherPass(41, 41));
            no(gate.begin(42));
            yes(gate.needsAnotherPass(41, 41));
            equal(42L, gate.finish(42, false));
        });
        run("a changed stamp requires stabilization without another save event", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(50));
            gate.beginPass();
            yes(gate.needsAnotherPass(50, 51));
            equal(null, gate.finish(51, false));
            yes(gate.begin(51));
        });
        run("a later failure invalidates an earlier completed stamp", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(60));
            gate.beginPass();
            equal(null, gate.finish(60, true));
            yes(gate.begin(61));
            gate.beginPass();
            equal(null, gate.finish(61, false));
            // Stamps need not be assumed monotonic across restore/undo scenarios.
            yes(gate.begin(60));
        });
        run("pending uses event order rather than numerical maximum", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(Long.MAX_VALUE));
            gate.beginPass();
            no(gate.begin(5));
            no(gate.begin(-1));
            equal(-1L, gate.finish(-1, false));
        });
        run("finishing clears pending state for the next worker", () -> {
            SaveGate gate = new SaveGate();
            yes(gate.begin(70));
            no(gate.begin(71));
            equal(71L, gate.finish(71, false));
            yes(gate.begin(72));
            gate.beginPass();
            no(gate.needsAnotherPass(72, 72));
            equal(null, gate.finish(72, true));
            no(gate.begin(72));
        });
        run("separate documents have independent gates", () -> {
            SaveGate first = new SaveGate();
            SaveGate second = new SaveGate();
            yes(first.begin(80));
            yes(second.begin(80));
            first.beginPass();
            second.beginPass();
            no(first.begin(81));
            yes(first.needsAnotherPass(80, 80));
            no(second.needsAnotherPass(80, 80));
            equal(null, second.finish(80, true));
            no(second.begin(80));
            equal(81L, first.finish(81, false));
            yes(first.begin(81));
        });
        run("operations without a worker fail visibly", () -> {
            SaveGate gate = new SaveGate();
            illegalState(gate::beginPass);
            illegalState(() -> gate.needsAnotherPass(1, 1));
            illegalState(() -> gate.finish(1, true));
        });
        System.out.println("RESULT: " + passed + " passed, " + failed + " failed");
        System.out.println("Scope: production SaveGate state only; no Rider/backend integration executed.");
        if (failed != 0) throw new AssertionError(failed + " tests failed");
    }

    private static void run(String name, Runnable test) {
        try {
            test.run();
            passed++;
            System.out.println("PASS: " + name);
        } catch (Throwable failure) {
            failed++;
            System.out.println("FAIL: " + name + ": " + failure);
        }
    }

    private static void yes(boolean value) {
        if (!value) throw new AssertionError("Expected true");
    }

    private static void no(boolean value) {
        if (value) throw new AssertionError("Expected false");
    }

    private static void equal(Object expected, Object actual) {
        if (!Objects.equals(expected, actual)) {
            throw new AssertionError("Expected " + expected + ", got " + actual);
        }
    }

    private static void illegalState(Runnable operation) {
        try {
            operation.run();
        } catch (IllegalStateException expected) {
            return;
        }
        throw new AssertionError("Expected IllegalStateException");
    }
}
